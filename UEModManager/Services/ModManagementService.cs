using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using SharpCompress.Common;
using SharpCompress.Readers;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.Detection;
using UEModManager.Services.Import;
using UEModManager.Services.Security;
using IOPath = System.IO.Path;

namespace UEModManager.Services
{
    /// <summary>
    /// 核心 MOD 管理服务。
    /// 从 MainWindow.xaml.cs 提取的所有 MOD 文件操作逻辑。
    /// </summary>
    public class ModManagementService
    {
        private readonly ILogger<ModManagementService> _logger;
        private readonly ModDataService _modDataService;

        /// <summary>
        /// 当前游戏类型（影响 CNS 匹配等逻辑）。
        /// </summary>
        public GameType CurrentGameType { get; set; } = GameType.Other;

        /// <summary>
        /// 当前引擎类型（影响 MOD 文件格式识别）。
        /// </summary>
        public EngineType CurrentEngineType { get; set; } = EngineType.UnrealEngine;

        /// <summary>
        /// 当前引擎配置档案的快捷访问。
        /// </summary>
        private EngineProfile EngineConfig => EngineProfile.Get(CurrentEngineType);

        public ModManagementService(ILogger<ModManagementService> logger, ModDataService modDataService)
        {
            _logger = logger;
            _modDataService = modDataService;
        }

        // ─── 扫描 ───

        /// <summary>
        /// 扫描指定路径下的所有 MOD，返回统一的 ModInfo 列表。
        /// 先扫 modPath（已启用），再扫 backupPath（已禁用），最后从 ModDataService 恢复元数据。
        /// </summary>
        public async Task<List<ModInfo>> ScanModsAsync(string modPath, string backupPath)
        {
            var mods = new List<ModInfo>();

            // 1. 扫描 modPath 中的已启用 MOD
            if (!string.IsNullOrEmpty(modPath) && Directory.Exists(modPath))
            {
                ScanEnabledMods(modPath, backupPath, mods);
            }

            // 2. 扫描 backupPath 中的已禁用 MOD
            if (!string.IsNullOrEmpty(backupPath) && Directory.Exists(backupPath))
            {
                ScanBackupMods(backupPath, modPath, mods);
            }

            // 3. 从 ModDataService 恢复自定义元数据（名称、描述、分类等）
            await RestoreMetadataAsync(mods);

            _logger.LogInformation("MOD 扫描完成: 共 {Total} 个, 已启用 {Enabled} 个",
                mods.Count, mods.Count(m => m.IsEnabled));

            return mods;
        }

        /// <summary>
        /// 剑星 CNS 模式的专用子目录名，在普通剑星模式下应排除。
        /// </summary>
        private static readonly string CNSSubDirectory = "CustomNanosuitSystem";

        /// <summary>
        /// 扫描 modPath 下已启用的 MOD。
        /// </summary>
        private void ScanEnabledMods(string modPath, string backupPath, List<ModInfo> mods)
        {
            // 1. 扫描子目录（新的组织结构：~mods/{ModName}/）
            foreach (var modDir in Directory.GetDirectories(modPath))
            {
                var modName = new DirectoryInfo(modDir).Name;

                // 剑星普通模式下跳过 CNS 专用目录，避免数据污染
                if (CurrentGameType == GameType.StellarBlade &&
                    modName.Equals(CNSSubDirectory, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug("剑星模式下跳过 CNS 目录: {Dir}", modName);
                    continue;
                }
                var modFiles = FindModFiles(modDir, SearchOption.AllDirectories);

                if (modFiles.Count > 0 && !mods.Any(m => m.RealName == modName))
                {
                    var mod = CreateModInfo(modName, modFiles, isEnabled: true);

                    // 查找预览图
                    FindPreviewImage(mod, backupPath);

                    // 检查备份状态
                    mod.BackupStatus = HasBackupFiles(mod.RealName, backupPath) ? "正常" : "备份失败";
                    if (mod.BackupStatus == "备份失败")
                    {
                        // 尝试创建备份
                        if (BackupModFiles(mod.RealName, modFiles, backupPath))
                            mod.BackupStatus = "正常";
                    }

                    mods.Add(mod);
                }
            }

            // 2. 扫描直接的 MOD 文件（兼容旧结构：~mods/*.pak）
            var directFiles = FindModFiles(modPath, SearchOption.TopDirectoryOnly);
            if (directFiles.Count > 0)
            {
                var groups = GroupModFilesByPrefix(directFiles);
                foreach (var group in groups)
                {
                    if (!mods.Any(m => m.RealName == group.Key))
                    {
                        var mod = CreateModInfo(group.Key, group.Value, isEnabled: true);
                        FindPreviewImage(mod, backupPath);
                        mod.BackupStatus = HasBackupFiles(mod.RealName, backupPath) ? "正常" : "备份失败";
                        if (mod.BackupStatus == "备份失败")
                        {
                            if (BackupModFiles(mod.RealName, group.Value, backupPath))
                                mod.BackupStatus = "正常";
                        }
                        mods.Add(mod);
                    }
                }
            }
        }

        /// <summary>
        /// 扫描备份目录中的已禁用 MOD。
        /// </summary>
        private void ScanBackupMods(string backupPath, string modPath, List<ModInfo> mods)
        {
            foreach (var dir in Directory.GetDirectories(backupPath))
            {
                var modName = new DirectoryInfo(dir).Name;

                // 查找是否已存在（可能是已启用的 MOD）
                var existing = mods.FirstOrDefault(m =>
                    m.Name == modName ||
                    m.RealName == modName ||
                    (CurrentGameType == GameType.StellarBladeCNS && IsCNSModMatch(m, modName)));

                if (existing == null)
                {
                    // 新的禁用 MOD
                    var files = Directory.GetFiles(dir);
                    var modFiles = files.Where(f => IsModFile(f)).ToList();
                    var allFiles = files.ToList();

                    var mod = new ModInfo
                    {
                        Name = modName,
                        RealName = modName,
                        IsEnabled = false,
                        Categories = new List<string> { DetermineModType(modName) },
                        FileSize = allFiles.Sum(f => new FileInfo(f).Length),
                        InstallDate = Directory.GetCreationTime(dir),
                        BackupStatus = "正常"
                    };

                    // 查找预览图
                    var preview = FindPreviewInDirectory(dir);
                    if (preview != null)
                        mod.PreviewImagePath = preview;

                    mods.Add(mod);
                }
                else
                {
                    // 更新已存在 MOD 的备份状态和预览图
                    var files = Directory.GetFiles(dir);
                    var modFiles = files.Where(f => IsModFile(f)).ToList();

                    existing.BackupStatus = modFiles.Count > 0 ? "正常" : "备份不完整";

                    if (string.IsNullOrEmpty(existing.PreviewImagePath))
                    {
                        var preview = FindPreviewInDirectory(dir);
                        if (preview != null)
                            existing.PreviewImagePath = preview;
                    }
                }
            }
        }

        // ─── 启用/禁用 ───

        /// <summary>
        /// 启用 MOD：从备份目录复制文件到 MOD 目录。
        /// 插件类型根据 PluginTargetPath 确定目标路径。
        /// </summary>
        public Task<bool> EnableModAsync(ModInfo mod, string modPath, string backupPath, string gamePath = "")
        {
            return Task.Run(() =>
            {
                try
                {
                    var modBackupDir = IOPath.Combine(backupPath, mod.RealName);
                    if (!Directory.Exists(modBackupDir))
                    {
                        _logger.LogWarning("备份目录不存在: {Path}", modBackupDir);
                        return false;
                    }

                    // 获取备份文件（排除预览图）
                    var backupFiles = Directory.GetFiles(modBackupDir, "*.*", SearchOption.AllDirectories)
                        .Where(f => !IOPath.GetFileName(f).StartsWith("preview", StringComparison.OrdinalIgnoreCase))
                        .ToList();

                    if (backupFiles.Count == 0)
                    {
                        _logger.LogWarning("备份目录中没有 MOD 文件: {Path}", modBackupDir);
                        return false;
                    }

                    var modTargetDir = GetModTargetDirectory(mod, modPath, gamePath);
                    var stagingDir = $"{modTargetDir}.staging";
                    if (Directory.Exists(stagingDir))
                        Directory.Delete(stagingDir, true);
                    Directory.CreateDirectory(stagingDir);

                    try
                    {
                        foreach (var backupFile in backupFiles)
                        {
                            var relativePath = IOPath.GetRelativePath(modBackupDir, backupFile);
                            var targetFile = IOPath.Combine(stagingDir, relativePath);
                            var targetFileDir = IOPath.GetDirectoryName(targetFile);
                            if (!string.IsNullOrEmpty(targetFileDir) && !Directory.Exists(targetFileDir))
                                Directory.CreateDirectory(targetFileDir);
                            File.Copy(backupFile, targetFile, true);
                        }

                        ReplaceDirectoryWithStaging(modTargetDir, stagingDir);
                    }
                    catch
                    {
                        if (Directory.Exists(stagingDir))
                            Directory.Delete(stagingDir, true);
                        throw;
                    }

                    mod.IsEnabled = true;
                    _logger.LogInformation("{Type} '{Name}' 已启用，复制了 {Count} 个文件",
                        mod.IsPlugin ? "插件" : "MOD", mod.Name, backupFiles.Count);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "启用 '{Name}' 失败", mod.Name);
                    return false;
                }
            });
        }

        /// <summary>
        /// 禁用 MOD：删除 MOD 目录中的文件（备份保留）。
        /// 插件类型根据 PluginTargetPath 确定目标路径。
        /// </summary>
        public Task<bool> DisableModAsync(ModInfo mod, string modPath, string backupPath, string gamePath = "")
        {
            return Task.Run(() =>
            {
                try
                {
                    var modTargetDir = GetModTargetDirectory(mod, modPath, gamePath);

                    if (Directory.Exists(modTargetDir))
                    {
                        var modBackupDir = IOPath.Combine(backupPath, mod.RealName);
                        if (!Directory.Exists(modBackupDir))
                        {
                            _logger.LogError("禁用 '{Name}' 被阻止：备份目录不存在 {Path}", mod.Name, modBackupDir);
                            return false;
                        }

                        var targetFileCount = CountFilesExcludingPreview(modTargetDir);
                        var backupFileCount = CountFilesExcludingPreview(modBackupDir);
                        if (backupFileCount < targetFileCount)
                        {
                            _logger.LogError(
                                "禁用 '{Name}' 被阻止：备份文件数不足 (backup={BackupCount}, target={TargetCount})",
                                mod.Name, backupFileCount, targetFileCount);
                            return false;
                        }

                        Directory.Delete(modTargetDir, true);
                        _logger.LogInformation("{Type} '{Name}' 已禁用",
                            mod.IsPlugin ? "插件" : "MOD", mod.Name);
                    }

                    mod.IsEnabled = false;
                    return true;
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogError(ex, "禁用 '{Name}' 失败：文件被占用", mod.Name);
                    return false;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "禁用 '{Name}' 失败", mod.Name);
                    return false;
                }
            });
        }

        /// <summary>
        /// 批量启用所有 MOD。
        /// </summary>
        public async Task<int> EnableAllAsync(IEnumerable<ModInfo> mods, string modPath, string backupPath, string gamePath = "")
        {
            int count = 0;
            foreach (var mod in mods.Where(m => !m.IsEnabled))
            {
                if (await EnableModAsync(mod, modPath, backupPath, gamePath))
                    count++;
            }
            return count;
        }

        /// <summary>
        /// 批量禁用所有 MOD。
        /// </summary>
        public async Task<int> DisableAllAsync(IEnumerable<ModInfo> mods, string modPath, string backupPath, string gamePath = "")
        {
            int count = 0;
            foreach (var mod in mods.Where(m => m.IsEnabled))
            {
                if (await DisableModAsync(mod, modPath, backupPath, gamePath))
                    count++;
            }
            return count;
        }

        // ─── 导入 ───

        /// <summary>
        /// 从文件路径导入 MOD。支持 .pak/.ucas/.utoc/.json/.zip/.rar/.7z。
        /// 返回导入成功的 MOD 数量。
        /// </summary>
        public async Task<int> ImportModsAsync(string[] filePaths, string modPath, string backupPath)
        {
            int imported = 0;
            foreach (var filePath in filePaths)
            {
                if (await ImportModFromFileAsync(filePath, modPath, backupPath))
                    imported++;
            }
            return imported;
        }

        /// <summary>
        /// 导入单个文件。
        /// </summary>
        public Task<bool> ImportModFromFileAsync(string filePath, string modPath, string backupPath)
        {
            return Task.Run(() =>
            {
                try
                {
                    if (!File.Exists(filePath))
                        return false;

                    var fileName = IOPath.GetFileNameWithoutExtension(filePath);
                    var ext = IOPath.GetExtension(filePath).ToLower();
                    var modBackupDir = IOPath.Combine(backupPath, fileName);

                    bool success;
                    if (ext is ".zip" or ".rar" or ".7z")
                        success = ImportCompressedMod(filePath, fileName, modPath, backupPath);
                    else if (EngineConfig.DirectImportExtensions.Contains(ext))
                        success = ImportDirectModFile(filePath, fileName, modBackupDir, modPath);
                    else
                        success = false;

                    if (success)
                        _logger.LogInformation("MOD '{Name}' 导入成功", fileName);

                    return success;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "导入 MOD 文件失败: {Path}", filePath);
                    return false;
                }
            });
        }

        private bool ImportDirectModFile(string filePath, string modName, string targetDir, string modPath)
        {
            try
            {
                if (!Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);

                var fileName = IOPath.GetFileName(filePath);
                File.Copy(filePath, IOPath.Combine(targetDir, fileName), true);

                // 同时复制到 MOD 目录（导入即启用）
                if (!string.IsNullOrEmpty(modPath) && Directory.Exists(modPath))
                {
                    var modSubDir = IOPath.Combine(modPath, modName);
                    if (!Directory.Exists(modSubDir))
                        Directory.CreateDirectory(modSubDir);
                    File.Copy(filePath, IOPath.Combine(modSubDir, fileName), true);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导入直接 MOD 文件失败");
                return false;
            }
        }

        private bool ImportCompressedMod(string filePath, string modName, string modPath, string backupPath)
        {
            var tempRoot = !string.IsNullOrWhiteSpace(backupPath)
                ? IOPath.Combine(backupPath, ".import-tmp")
                : IOPath.Combine(AppDomain.CurrentDomain.BaseDirectory, "Data", ".import-tmp");
            var tempDir = IOPath.Combine(tempRoot, $"uemod_temp_{Guid.NewGuid()}");

            try
            {
                Directory.CreateDirectory(tempDir);

                // 解压
                if (!ExtractCompressedFile(filePath, tempDir))
                    return false;

                // 处理嵌套压缩包
                ProcessNestedArchives(tempDir);

                // 清理残留压缩包
                foreach (var archive in Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => new[] { ".zip", ".rar", ".7z" }.Contains(IOPath.GetExtension(f).ToLower())))
                {
                    try { File.Delete(archive); } catch { }
                }

                var modFiles = Directory.GetFiles(tempDir, "*.*", SearchOption.AllDirectories)
                    .Where(f => IsModFile(f)).ToList();

                if (modFiles.Count == 0)
                    return false;

                var groups = ModFileGrouper.SplitByImportScope(modFiles, tempDir)
                    .SelectMany(scope => GroupModFilesByPrefix(scope)
                        .Where(g => g.Value.Count > 0)
                        .Select(g => new KeyValuePair<string, List<string>>(g.Key, g.Value)))
                    .ToList();

                int index = 1;
                var usedModNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var group in groups)
                {
                    var finalModName = DetermineGroupContainerBaseName(group.Value) ?? group.Key ?? $"{modName}_{index}";
                    finalModName = EnsureUniqueImportName(finalModName, backupPath, usedModNames);
                    var modBackupSubDir = IOPath.Combine(backupPath, finalModName);

                    if (!Directory.Exists(modBackupSubDir))
                        Directory.CreateDirectory(modBackupSubDir);

                    // 复制 MOD 文件到备份
                    foreach (var f in group.Value)
                        File.Copy(f, IOPath.Combine(modBackupSubDir, IOPath.GetFileName(f)), true);

                    // 查找预览图
                    CopyPreviewImageFromTemp(group.Value, tempDir, modBackupSubDir);

                    // 复制到 MOD 目录（导入即启用）
                    if (!string.IsNullOrEmpty(modPath) && Directory.Exists(modPath))
                    {
                        var modSubDir = IOPath.Combine(modPath, finalModName);
                        if (!Directory.Exists(modSubDir))
                            Directory.CreateDirectory(modSubDir);
                        foreach (var f in group.Value)
                            File.Copy(f, IOPath.Combine(modSubDir, IOPath.GetFileName(f)), true);
                    }

                    index++;
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "导入压缩 MOD 失败");
                return false;
            }
            finally
            {
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            }
        }

        // ─── 插件导入 ───

        /// <summary>
        /// 导入插件文件。支持任意格式，复制到备份目录和目标路径。
        /// </summary>
        /// <param name="filePaths">要导入的文件路径列表</param>
        /// <param name="pluginTargetPath">目标路径（相对于游戏根目录）</param>
        /// <param name="gamePath">游戏根目录</param>
        /// <param name="backupPath">备份目录</param>
        /// <returns>导入的插件 ModInfo 列表</returns>
        public Task<List<ModInfo>> ImportPluginsAsync(string[] filePaths, string pluginTargetPath, string gamePath, string backupPath)
        {
            return Task.Run(() =>
            {
                var imported = new List<ModInfo>();
                foreach (var filePath in filePaths)
                {
                    try
                    {
                        if (!File.Exists(filePath) && !Directory.Exists(filePath))
                        {
                            _logger.LogWarning("插件路径不存在: {Path}", filePath);
                            continue;
                        }

                        bool isDirectory = Directory.Exists(filePath) && !File.Exists(filePath);
                        var pluginName = IOPath.GetFileNameWithoutExtension(filePath);
                        if (isDirectory)
                            pluginName = new DirectoryInfo(filePath).Name;

                        // 备份目录
                        var pluginBackupDir = IOPath.Combine(backupPath, pluginName);
                        if (Directory.Exists(pluginBackupDir))
                        {
                            // 已存在同名插件，添加时间戳
                            pluginName = $"{pluginName}_{DateTime.Now:yyyyMMdd_HHmmss}";
                            pluginBackupDir = IOPath.Combine(backupPath, pluginName);
                        }
                        Directory.CreateDirectory(pluginBackupDir);

                        // 目标安装目录
                        var targetDir = IOPath.Combine(PathSanitizer.SafeCombine(gamePath, pluginTargetPath), pluginName);
                        Directory.CreateDirectory(targetDir);

                        long totalSize = 0;

                        if (isDirectory)
                        {
                            // 文件夹导入：保持目录结构
                            foreach (var file in Directory.GetFiles(filePath, "*.*", SearchOption.AllDirectories))
                            {
                                var relativePath = IOPath.GetRelativePath(filePath, file);
                                var backupDest = IOPath.Combine(pluginBackupDir, relativePath);
                                var targetDest = IOPath.Combine(targetDir, relativePath);

                                var backupDestDir = IOPath.GetDirectoryName(backupDest);
                                var targetDestDir = IOPath.GetDirectoryName(targetDest);
                                if (!string.IsNullOrEmpty(backupDestDir)) Directory.CreateDirectory(backupDestDir);
                                if (!string.IsNullOrEmpty(targetDestDir)) Directory.CreateDirectory(targetDestDir);

                                File.Copy(file, backupDest, true);
                                File.Copy(file, targetDest, true);
                                totalSize += new FileInfo(file).Length;
                            }
                        }
                        else
                        {
                            // 单文件导入
                            var fileName = IOPath.GetFileName(filePath);
                            File.Copy(filePath, IOPath.Combine(pluginBackupDir, fileName), true);
                            File.Copy(filePath, IOPath.Combine(targetDir, fileName), true);
                            totalSize = new FileInfo(filePath).Length;
                        }

                        var modInfo = new ModInfo
                        {
                            Name = pluginName,
                            RealName = pluginName,
                            IsEnabled = true,
                            IsPlugin = true,
                            PluginTargetPath = pluginTargetPath,
                            FileSize = totalSize,
                            InstallDate = DateTime.Now,
                            Categories = new List<string> { "插件" },
                            BackupStatus = "正常"
                        };

                        imported.Add(modInfo);
                        _logger.LogInformation("插件 '{Name}' 已导入到 {Path}", pluginName, pluginTargetPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "导入插件 '{Path}' 失败", filePath);
                    }
                }
                return imported;
            });
        }

        // ─── 删除 ───

        /// <summary>
        /// 完全删除 MOD（备份 + MOD 目录）。
        /// </summary>
        public Task<bool> DeleteModAsync(ModInfo mod, string modPath, string backupPath, string gamePath = "")
        {
            return Task.Run(() =>
            {
                try
                {
                    // 删除 MOD/插件目录
                    var targetDir = GetModTargetDirectory(mod, modPath, gamePath);

                    if (Directory.Exists(targetDir))
                        Directory.Delete(targetDir, true);

                    // 目标目录删除成功后再删除备份，避免目标被占用时丢失备份。
                    var backupDir = IOPath.Combine(backupPath, mod.RealName);
                    if (Directory.Exists(backupDir))
                        Directory.Delete(backupDir, true);

                    _logger.LogInformation("{Type} '{Name}' 已删除", mod.IsPlugin ? "插件" : "MOD", mod.Name);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "删除 '{Name}' 失败", mod.Name);
                    return false;
                }
            });
        }

        /// <summary>
        /// 批量删除 MOD。
        /// </summary>
        public async Task<int> DeleteModsAsync(IEnumerable<ModInfo> mods, string modPath, string backupPath, string gamePath = "")
        {
            int count = 0;
            foreach (var mod in mods.ToList())
            {
                if (await DeleteModAsync(mod, modPath, backupPath, gamePath))
                    count++;
            }
            return count;
        }

        private static string GetModTargetDirectory(ModInfo mod, string modPath, string gamePath)
        {
            if (mod.IsPlugin && !string.IsNullOrEmpty(mod.PluginTargetPath) && !string.IsNullOrEmpty(gamePath))
                return IOPath.Combine(PathSanitizer.SafeCombine(gamePath, mod.PluginTargetPath), mod.RealName);

            return IOPath.Combine(modPath, mod.RealName);
        }

        private static int CountFilesExcludingPreview(string directory)
        {
            return Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
                .Count(f => !IOPath.GetFileName(f).StartsWith("preview", StringComparison.OrdinalIgnoreCase));
        }

        private static void ReplaceDirectoryWithStaging(string targetDir, string stagingDir)
        {
            var oldDir = $"{targetDir}.old-{Guid.NewGuid():N}";
            var movedOld = false;

            try
            {
                if (Directory.Exists(targetDir))
                {
                    Directory.Move(targetDir, oldDir);
                    movedOld = true;
                }

                Directory.Move(stagingDir, targetDir);

                if (movedOld && Directory.Exists(oldDir))
                {
                    Directory.Delete(oldDir, true);
                    movedOld = false;
                }
            }
            catch
            {
                if (movedOld && Directory.Exists(oldDir) && !Directory.Exists(targetDir))
                {
                    try
                    {
                        Directory.Move(oldDir, targetDir);
                        movedOld = false;
                    }
                    catch
                    {
                        // 保留 oldDir，避免恢复失败时继续破坏原目标目录。
                    }
                }

                throw;
            }
        }

        // ─── 备份 ───

        /// <summary>
        /// 为 MOD 文件创建备份。
        /// </summary>
        public bool BackupModFiles(string modName, List<string> modFiles, string backupPath)
        {
            try
            {
                var modBackupDir = IOPath.Combine(backupPath, modName);
                if (!Directory.Exists(modBackupDir))
                    Directory.CreateDirectory(modBackupDir);

                foreach (var file in modFiles)
                {
                    var targetPath = IOPath.Combine(modBackupDir, IOPath.GetFileName(file));
                    File.Copy(file, targetPath, true);
                }

                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "备份 MOD '{Name}' 失败", modName);
                return false;
            }
        }

        // ─── 预览图 ───

        /// <summary>
        /// 更换 MOD 预览图。
        /// </summary>
        public string? ChangePreviewImage(ModInfo mod, string imagePath, string backupPath)
        {
            try
            {
                var modBackupDir = IOPath.Combine(backupPath, mod.RealName);
                if (!Directory.Exists(modBackupDir))
                    Directory.CreateDirectory(modBackupDir);

                var ext = IOPath.GetExtension(imagePath);
                var previewPath = IOPath.Combine(modBackupDir, $"preview{ext}");

                // 删除旧预览图
                foreach (var old in Directory.GetFiles(modBackupDir, "preview*"))
                    try { File.Delete(old); } catch { }

                File.Copy(imagePath, previewPath, true);
                mod.PreviewImagePath = previewPath;
                return previewPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "更换预览图失败");
                return null;
            }
        }

        // ─── 辅助方法 ───

        /// <summary>
        /// 查找目录中的 MOD 文件，根据当前引擎配置动态匹配扩展名。
        /// </summary>
        private List<string> FindModFiles(string directory, SearchOption option)
        {
            var extensions = EngineConfig.ModFileExtensions;
            return Directory.GetFiles(directory, "*.*", option)
                .Where(f => extensions.Contains(IOPath.GetExtension(f).ToLower()))
                .ToList();
        }

        private bool IsModFile(string filePath)
        {
            var ext = IOPath.GetExtension(filePath).ToLower();
            return EngineConfig.ModFileExtensions.Contains(ext);
        }

        private static bool IsImageFile(string filePath)
            => ArtifactTypeDetector.IsImageExtension(filePath);

        private ModInfo CreateModInfo(string modName, List<string> modFiles, bool isEnabled)
        {
            return new ModInfo
            {
                Name = modName,
                RealName = modName,
                IsEnabled = isEnabled,
                Categories = new List<string> { DetermineModType(modName) },
                FileSize = modFiles.Sum(f => new FileInfo(f).Length),
                InstallDate = modFiles.Count > 0 ? File.GetCreationTime(modFiles[0]) : DateTime.Now,
                BackupStatus = "未知"
            };
        }

        /// <summary>
        /// 根据 MOD 名称智能判断分类。委托 Core 的 ModCategoryClassifier。
        /// </summary>
        public static string DetermineModType(string modName)
            => ModCategoryClassifier.Classify(modName);

        private void FindPreviewImage(ModInfo mod, string backupPath)
        {
            if (string.IsNullOrEmpty(backupPath)) return;
            var backupDir = IOPath.Combine(backupPath, mod.RealName);
            if (!Directory.Exists(backupDir)) return;

            var preview = FindPreviewInDirectory(backupDir);
            if (preview != null)
                mod.PreviewImagePath = preview;
        }

        private static string? FindPreviewInDirectory(string directory)
        {
            var files = Directory.GetFiles(directory, "*.*", SearchOption.TopDirectoryOnly);
            return PreviewImageSelector.Select(files);
        }

        private bool HasBackupFiles(string modName, string backupPath)
        {
            if (string.IsNullOrEmpty(backupPath)) return false;
            var dir = IOPath.Combine(backupPath, modName);
            if (!Directory.Exists(dir)) return false;
            return Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
                .Any(f => !IOPath.GetFileName(f).StartsWith("preview", StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>
        /// 从恢复 ModDataService 恢复自定义元数据。
        /// </summary>
        private async Task RestoreMetadataAsync(List<ModInfo> mods)
        {
            var savedMods = await _modDataService.LoadModsAsync();
            foreach (var mod in mods)
            {
                var saved = savedMods.FirstOrDefault(s =>
                    s.Id == mod.RealName || s.RealName == mod.RealName || s.Name == mod.RealName);
                if (saved != null)
                {
                    if (!string.IsNullOrEmpty(saved.Name) && saved.Name != saved.RealName)
                        mod.Name = saved.Name;
                    if (!string.IsNullOrEmpty(saved.Description))
                        mod.Description = saved.Description;
                    if (saved.Categories.Count > 0 && saved.Categories[0] != "未分类")
                        mod.Categories = saved.Categories;
                    if (!string.IsNullOrEmpty(saved.PreviewImagePath))
                        mod.PreviewImagePath = saved.PreviewImagePath;
                }
            }
        }

        /// <summary>
        /// 按文件名前缀分组 MOD 文件。
        /// </summary>
        public Dictionary<string, List<string>> GroupModFilesByPrefix(List<string> modFiles)
        {
            var result = ModFileGrouper.GroupByBaseName(modFiles).ToDictionary(
                kv => kv.Key,
                kv => kv.Value,
                StringComparer.OrdinalIgnoreCase);

            if (CurrentGameType == GameType.StellarBladeCNS)
                result = MergeCNSRelatedGroups(result);

            return result;
        }

        private string? DetermineGroupContainerBaseName(List<string> groupFiles)
        {
            if (groupFiles == null || groupFiles.Count == 0) return null;

            string? pick(string ext) => groupFiles
                .Where(f => IOPath.GetExtension(f).Equals(ext, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(f => IOPath.GetFileNameWithoutExtension(f).Length)
                .Select(f => IOPath.GetFileNameWithoutExtension(f))
                .FirstOrDefault();

            // 按引擎配置的优先级扩展名依次查找
            foreach (var ext in EngineConfig.GroupPriorityExtensions)
            {
                var name = pick(ext);
                if (name != null) return name;
            }

            return IOPath.GetFileNameWithoutExtension(groupFiles[0]);
        }

        private static string EnsureUniqueImportName(string modName, string backupPath, HashSet<string> usedModNames)
        {
            var uniqueName = modName;
            var suffix = 1;
            while (usedModNames.Contains(uniqueName) || Directory.Exists(IOPath.Combine(backupPath, uniqueName)))
            {
                suffix++;
                uniqueName = $"{modName}_{suffix}";
            }

            usedModNames.Add(uniqueName);
            return uniqueName;
        }

        // ─── 解压缩 ───

        private bool ExtractCompressedFile(string filePath, string extractPath)
        {
            try
            {
                var ext = IOPath.GetExtension(filePath).ToLower();
                if (ext == ".zip")
                {
                    ExtractZipFile(filePath, extractPath);
                    return true;
                }

                using var stream = File.OpenRead(filePath);
                using var reader = ReaderFactory.OpenReader(stream, new ReaderOptions());
                while (reader.MoveToNextEntry())
                {
                    if (reader.Entry.IsDirectory) continue;
                    reader.WriteEntryToDirectory(extractPath, new ExtractionOptions
                    {
                        ExtractFullPath = true,
                        Overwrite = true
                    });
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "解压文件失败: {Path}", filePath);
                return false;
            }
        }

        private static void ExtractZipFile(string filePath, string extractPath)
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                ZipFile.ExtractToDirectory(filePath, extractPath, Encoding.GetEncoding(936), true);
            }
            catch (InvalidDataException)
            {
                ZipFile.ExtractToDirectory(filePath, extractPath, Encoding.UTF8, true);
            }
        }

        private void ProcessNestedArchives(string directory)
        {
            var processed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var pending = new Queue<string>(Directory.GetFiles(directory, "*.*", SearchOption.AllDirectories)
                .Where(CompressedArchive.IsCompressed));

            while (pending.Count > 0)
            {
                var archive = pending.Dequeue();
                if (!processed.Add(archive))
                    continue;

                var extractDir = archive + "_extracted";
                try
                {
                    Directory.CreateDirectory(extractDir);
                    if (!ExtractCompressedFile(archive, extractDir))
                        continue;

                    foreach (var nestedArchive in Directory.GetFiles(extractDir, "*.*", SearchOption.AllDirectories)
                        .Where(CompressedArchive.IsCompressed))
                    {
                        pending.Enqueue(nestedArchive);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "处理嵌套压缩包失败: {Path}", archive);
                }
            }
        }

        private void CopyPreviewImageFromTemp(List<string> groupFiles, string tempDir, string modBackupDir)
        {
            var modFileDir = IOPath.GetDirectoryName(groupFiles[0]) ?? tempDir;
            var images = Directory.GetFiles(modFileDir, "*.*", SearchOption.TopDirectoryOnly)
                .Where(f => IsImageFile(f)).ToList();

            if (images.Count > 0)
            {
                var preview = images.FirstOrDefault(f =>
                    IOPath.GetFileNameWithoutExtension(f).Contains("preview", StringComparison.OrdinalIgnoreCase))
                    ?? images[0];
                var ext = IOPath.GetExtension(preview);
                File.Copy(preview, IOPath.Combine(modBackupDir, $"preview{ext}"), true);
            }
        }

        // ─── CNS 相关 ───

        private bool IsCNSModMatch(ModInfo existing, string backupModName)
        {
            if (CurrentGameType != GameType.StellarBladeCNS)
                return false;
            if (existing.RealName == backupModName)
                return true;

            var existingKw = ExtractCNSKeywords(existing.RealName);
            var backupKw = ExtractCNSKeywords(backupModName);
            return existingKw.Intersect(backupKw).Any();
        }

        private static List<string> ExtractCNSKeywords(string name)
        {
            var lower = name.ToLower()
                .Replace("dekcns-", "").Replace(".dekcns", "")
                .Replace("_p", "").Replace("-p", "");
            return lower.Split(new[] { '-', '_', '.' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(p => p.Length >= 3).ToList();
        }

        private Dictionary<string, List<string>> MergeCNSRelatedGroups(Dictionary<string, List<string>> groups)
        {
            var result = new Dictionary<string, List<string>>(groups);
            var processed = new HashSet<string>();

            foreach (var group in groups)
            {
                if (processed.Contains(group.Key)) continue;
                var jsonFiles = group.Value.Where(f => IOPath.GetExtension(f).Equals(".json", StringComparison.OrdinalIgnoreCase)).ToList();
                if (jsonFiles.Count == 0) continue;

                var jsonName = IOPath.GetFileNameWithoutExtension(jsonFiles[0]);
                if (!jsonName.Contains("dekcns", StringComparison.OrdinalIgnoreCase) &&
                    !jsonName.Contains("cns", StringComparison.OrdinalIgnoreCase))
                    continue;

                var jsonKw = ExtractCNSKeywords(jsonName);
                string? bestMatch = null;
                int bestScore = 0;

                foreach (var other in groups)
                {
                    if (other.Key == group.Key || processed.Contains(other.Key)) continue;
                    if (!other.Value.Any(f => IOPath.GetExtension(f).Equals(".pak", StringComparison.OrdinalIgnoreCase)))
                        continue;

                    var pakName = IOPath.GetFileNameWithoutExtension(
                        other.Value.First(f => IOPath.GetExtension(f).Equals(".pak", StringComparison.OrdinalIgnoreCase)));
                    var pakKw = ExtractCNSKeywords(pakName);
                    int score = 0;
                    foreach (var j in jsonKw)
                    {
                        foreach (var p in pakKw)
                        {
                            if (j == p) score += 3;
                            else if (j.Contains(p) || p.Contains(j)) score += 2;
                        }
                    }

                    if (score > bestScore && score >= 2)
                    {
                        bestScore = score;
                        bestMatch = other.Key;
                    }
                }

                if (bestMatch != null)
                {
                    result[group.Key].AddRange(groups[bestMatch]);
                    result.Remove(bestMatch);
                    processed.Add(bestMatch);
                    processed.Add(group.Key);
                }
            }

            return result;
        }

        // ─── 工具 ───

        /// <summary>
        /// 格式化文件大小。
        /// </summary>
        public static string FormatFileSize(long bytes)
        {
            if (bytes <= 0) return "0 B";
            string[] units = { "B", "KB", "MB", "GB", "TB" };
            int idx = 0;
            double size = bytes;
            while (size >= 1024 && idx < units.Length - 1)
            {
                size /= 1024;
                idx++;
            }
            return $"{size:F1} {units[idx]}";
        }

        /// <summary>
        /// 获取目录总大小。
        /// </summary>
        public static long GetDirectorySize(string path)
        {
            if (!Directory.Exists(path)) return 0;
            return Directory.GetFiles(path, "*.*", SearchOption.AllDirectories)
                .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0L; } });
        }
    }
}
