using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using UEModManager.Infrastructure;

namespace UEModManager.Security
{
    /// <summary>
    /// ?? Windows DPAPI ????????????????????????????
    /// - ????????? (CurrentUser)????????????
    /// - ?????%APPDATA%/UEModManager/config/{name}.enc
    /// - ????????????? AppData ????? {name}????? {name}.enc ??????????
    /// </summary>
    public static class SecretFileProtector
    {
        private static readonly byte[] s_entropy = Encoding.UTF8.GetBytes("UEMM::SecretEntropy::v1");

        /// <summary>
        /// 密钥文件目录。路径归口 <see cref="AppPaths.SecretsDirectory"/>，磁盘位置不变
        /// （仍是 <c>%APPDATA%\UEModManager\config</c>）——搬走会让老用户已加密的文件失联。
        /// </summary>
        public static string GetConfigDir()
        {
            var dir = AppPaths.SecretsDirectory;
            AppPaths.TryEnsureDirectory(dir);
            return dir;
        }

        public static string GetEncryptedPath(string plainName)
        {
            return Path.Combine(GetConfigDir(), plainName + ".enc");
        }

        public static string GetPlainPathInAppData(string plainName)
        {
            return Path.Combine(GetConfigDir(), plainName);
        }

        public static bool TryEnsureBundledSecret(string plainName, string bundledRelativePath)
        {
            try
            {
                var encryptedPath = GetEncryptedPath(plainName);
                if (File.Exists(encryptedPath))
                {
                    return true;
                }

                var targetPath = GetPlainPathInAppData(plainName);
                if (File.Exists(targetPath))
                {
                    return true;
                }

                var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;
                var bundledPath = Path.Combine(baseDir, bundledRelativePath);
                if (!File.Exists(bundledPath))
                {
                    return false;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                File.Copy(bundledPath, targetPath, true);
                TrySetHiddenSystem(targetPath);
                Console.WriteLine($"[Secret] 已从内置模板复制 {plainName} -> {targetPath}");
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Secret] TryEnsureBundledSecret 失败({plainName}): {ex.Message}");
                return false;
            }
        }

        public static string? FindPlaintextCandidate(string plainName)
        {
            // 1) AppData
            var appDataPlain = GetPlainPathInAppData(plainName);
            if (File.Exists(appDataPlain)) return appDataPlain;

            // 2) ????
            var baseDir = AppDomain.CurrentDomain.BaseDirectory ?? Environment.CurrentDirectory;
            var appPlain = Path.Combine(baseDir, plainName);
            if (File.Exists(appPlain)) return appPlain;

            // 3) ???? 4 ????/????
            var dir = baseDir;
            for (int i = 0; i < 4; i++)
            {
                dir = Path.GetFullPath(Path.Combine(dir, ".."));
                var candidate = Path.Combine(dir, plainName);
                if (File.Exists(candidate)) return candidate;
            }
            return null;
        }

        public static void EnsureEncryptedFromPlain(string plainName)
        {
            try
            {
                var encPath = GetEncryptedPath(plainName);
                if (File.Exists(encPath))
                {
                    // ??????????????
                    TrySetHiddenSystem(encPath);
                    return;
                }

                var plainPath = FindPlaintextCandidate(plainName);
                if (plainPath == null) return; // ???????

                var content = File.ReadAllBytes(plainPath);
                var cipher = ProtectedData.Protect(content, s_entropy, DataProtectionScope.CurrentUser);
                Directory.CreateDirectory(Path.GetDirectoryName(encPath)!);
                File.WriteAllBytes(encPath, cipher);
                TrySetHiddenSystem(encPath);

                // 🔒 保留明文文件作为备份，只加密不删除（v1.7.38修复）
                // TrySecureDelete(plainPath);
                Console.WriteLine($"[Secret] 已加密 {plainName} 到加密文件: {encPath}（保留明文备份）");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Secret] EnsureEncryptedFromPlain ??({plainName}): {ex}");
            }
        }

        public static bool TryLoadDecryptedText(string plainName, out string text)
        {
            try
            {
                var encPath = GetEncryptedPath(plainName);
                if (!File.Exists(encPath))
                {
                    text = string.Empty;
                    return false;
                }
                var cipher = File.ReadAllBytes(encPath);
                var plain = ProtectedData.Unprotect(cipher, s_entropy, DataProtectionScope.CurrentUser);
                text = Encoding.UTF8.GetString(plain);
                return true;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[Secret] TryLoadDecryptedText ??({plainName}): {ex.Message}");
                text = string.Empty;
                return false;
            }
        }

        private static void TrySetHiddenSystem(string path)
        {
            try
            {
                var attr = File.GetAttributes(path);
                attr |= FileAttributes.Hidden | FileAttributes.System;
                File.SetAttributes(path, attr);
            }
            catch { }
        }
    }
}
