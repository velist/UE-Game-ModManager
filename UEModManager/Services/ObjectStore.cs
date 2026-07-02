using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Services.Security;

namespace UEModManager.Services
{
    /// <summary>
    /// 对象仓库。
    /// 基于内容哈希的文件存储，实现去重。
    /// 仓库结构：
    ///   {RepositoryRoot}/
    ///     {packageKey}/
    ///       manifest.json
    ///       files/
    ///         {fileName}
    ///       preview.*
    /// </summary>
    public class ObjectStore : IObjectStoreQuery
    {
        private readonly ILogger<ObjectStore> _logger;
        private string _repositoryRoot;

        public ObjectStore(ILogger<ObjectStore> logger)
        {
            _logger = logger;
            _repositoryRoot = UiPreferences.LoadRepositoryRoot()
                ?? Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "UEModManager", "Repository");
        }

        /// <summary>仓库根目录。</summary>
        public string RepositoryRoot => _repositoryRoot;

        /// <summary>
        /// 设置仓库根目录（用户可自定义）。
        /// </summary>
        public void SetRepositoryRoot(string path)
        {
            _repositoryRoot = path;
            UiPreferences.SaveRepositoryRoot(path);
            _logger.LogInformation("仓库路径设置为: {Path}", path);
        }

        /// <summary>
        /// 初始化仓库目录。
        /// </summary>
        public void EnsureInitialized()
        {
            if (!Directory.Exists(_repositoryRoot))
            {
                Directory.CreateDirectory(_repositoryRoot);
                _logger.LogInformation("创建仓库目录: {Path}", _repositoryRoot);
            }
        }

        /// <summary>
        /// 获取包在仓库中的目录路径。
        /// </summary>
        public string GetPackageDirectory(string packageKey)
            => Path.Combine(_repositoryRoot, packageKey);

        /// <summary>
        /// 获取包的文件存储目录。
        /// </summary>
        public string GetPackageFilesDirectory(string packageKey)
            => Path.Combine(_repositoryRoot, packageKey, "files");

        /// <summary>
        /// 获取包的 manifest 路径。
        /// </summary>
        public string GetManifestPath(string packageKey)
            => Path.Combine(_repositoryRoot, packageKey, "manifest.json");

        /// <summary>
        /// 存储文件到包仓库。
        /// 返回文件在仓库内的相对路径。
        /// </summary>
        public async Task<(string relativePath, string fileHash, long fileSize)> StoreFileAsync(
            string packageKey, string sourceFilePath, string? targetRelativeName = null)
        {
            EnsureInitialized();

            var fileName = PathSanitizer.SanitizeRelative(targetRelativeName ?? Path.GetFileName(sourceFilePath));
            var packageFilesDir = GetPackageFilesDirectory(packageKey);
            Directory.CreateDirectory(packageFilesDir);

            var targetPath = Path.Combine(packageFilesDir, fileName);
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            // 计算哈希
            var hash = await ComputeFileHashAsync(sourceFilePath);
            var fileSize = new FileInfo(sourceFilePath).Length;

            // 复制文件
            File.Copy(sourceFilePath, targetPath, true);

            var relativePath = Path.Combine(packageKey, "files", fileName);
            _logger.LogDebug("文件已存储: {Path} (hash={Hash})", relativePath, hash);

            return (relativePath, hash, fileSize);
        }

        /// <summary>
        /// 批量存储文件到包仓库。
        /// </summary>
        public async Task<List<(string relativePath, string fileHash, long fileSize, string fileName)>> StoreFilesAsync(
            string packageKey, IEnumerable<string> sourceFilePaths)
        {
            var results = new List<(string, string, long, string)>();
            foreach (var filePath in sourceFilePaths)
            {
                var (relPath, hash, size) = await StoreFileAsync(packageKey, filePath);
                results.Add((relPath, hash, size, Path.GetFileName(filePath)));
            }
            return results;
        }

        /// <summary>
        /// 存储预览图到包仓库。
        /// </summary>
        public string? StorePreviewImage(string packageKey, string sourceImagePath)
        {
            try
            {
                EnsureInitialized();
                var packageDir = GetPackageDirectory(packageKey);
                Directory.CreateDirectory(packageDir);

                var ext = Path.GetExtension(sourceImagePath);
                var previewPath = Path.Combine(packageDir, $"preview{ext}");

                // 删除旧预览图
                foreach (var old in Directory.GetFiles(packageDir, "preview*"))
                    try { File.Delete(old); } catch { }

                File.Copy(sourceImagePath, previewPath, true);
                return previewPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "存储预览图失败: {Package}", packageKey);
                return null;
            }
        }

        /// <summary>
        /// 获取包的预览图路径。
        /// </summary>
        public string? GetPreviewImagePath(string packageKey)
        {
            var packageDir = GetPackageDirectory(packageKey);
            if (!Directory.Exists(packageDir)) return null;

            return Directory.GetFiles(packageDir, "preview*")
                .FirstOrDefault();
        }

        /// <summary>
        /// 获取包内的所有文件路径。
        /// </summary>
        public List<string> GetPackageFiles(string packageKey)
        {
            var filesDir = GetPackageFilesDirectory(packageKey);
            if (!Directory.Exists(filesDir)) return new List<string>();
            return Directory.GetFiles(filesDir, "*.*", SearchOption.AllDirectories).ToList();
        }

        /// <summary>
        /// 删除包在仓库中的所有数据。
        /// </summary>
        public bool DeletePackage(string packageKey)
        {
            try
            {
                var packageDir = GetPackageDirectory(packageKey);
                if (Directory.Exists(packageDir))
                {
                    Directory.Delete(packageDir, true);
                    _logger.LogInformation("包仓库已删除: {Package}", packageKey);
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除包仓库失败: {Package}", packageKey);
                return false;
            }
        }

        /// <summary>
        /// 检查包是否存在于仓库中。
        /// </summary>
        public bool PackageExists(string packageKey)
            => Directory.Exists(GetPackageDirectory(packageKey))
               && File.Exists(GetManifestPath(packageKey));

        /// <summary>
        /// 计算文件的 SHA-256 哈希（前 16 字符）。
        /// </summary>
        public static async Task<string> ComputeFileHashAsync(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            var hashBytes = await SHA256.HashDataAsync(stream);
            return Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
        }

        /// <summary>
        /// 计算多个文件的组合内容哈希。
        /// 对所有文件哈希排序后再哈希，确保结果稳定。
        /// </summary>
        public static async Task<string> ComputeContentHashAsync(IEnumerable<string> filePaths)
        {
            var hashes = new List<string>();
            foreach (var path in filePaths.OrderBy(p => p))
            {
                hashes.Add(await ComputeFileHashAsync(path));
            }
            var combined = string.Join("|", hashes);
            var bytes = System.Text.Encoding.UTF8.GetBytes(combined);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }

        /// <summary>
        /// 获取仓库总占用大小。
        /// </summary>
        public long GetTotalSize()
        {
            if (!Directory.Exists(_repositoryRoot)) return 0;
            try
            {
                return Directory.GetFiles(_repositoryRoot, "*.*", SearchOption.AllDirectories)
                    .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
            }
            catch { return 0; }
        }

        /// <summary>
        /// 获取仓库中的所有包目录名。
        /// </summary>
        public List<string> GetAllPackageKeys()
        {
            if (!Directory.Exists(_repositoryRoot)) return new List<string>();
            return Directory.GetDirectories(_repositoryRoot)
                .Select(d => new DirectoryInfo(d).Name)
                .Where(name => File.Exists(Path.Combine(_repositoryRoot, name, "manifest.json")))
                .ToList();
        }
    }
}
