using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services;
using UEModManager.Services.Config;

namespace UEModManager.ViewModels
{
    public partial class ConfigManagerViewModel : ObservableObject
    {
        private readonly ConfigMergeEngine _mergeEngine;
        private readonly PackageRepository _packageRepo;
        private readonly ObjectStore _objectStore;
        private readonly ProfileService _profileService;
        private readonly GameConfigService _gameConfig;
        private readonly ILogger _logger;

        public ObservableCollection<ConfigFileItem> ConfigFiles { get; } = new();
        public ObservableCollection<ConfigEntryRow> Entries { get; } = new();

        [ObservableProperty]
        private ConfigFileItem? _selectedFile;

        [ObservableProperty]
        private string _selectedFileName = "";

        [ObservableProperty]
        private string _selectedFilePath = "";

        [ObservableProperty]
        private int _totalKeys;

        [ObservableProperty]
        private int _conflictKeys;

        [ObservableProperty]
        private bool _isLoading;

        public ConfigManagerViewModel(
            ConfigMergeEngine mergeEngine,
            PackageRepository packageRepo,
            ObjectStore objectStore,
            ProfileService profileService,
            GameConfigService gameConfig,
            ILogger logger)
        {
            _mergeEngine = mergeEngine;
            _packageRepo = packageRepo;
            _objectStore = objectStore;
            _profileService = profileService;
            _gameConfig = gameConfig;
            _logger = logger;
        }

        public void Initialize()
        {
            ScanConfigFiles();
        }

        private void ScanConfigFiles()
        {
            ConfigFiles.Clear();

            var profile = _profileService.CurrentProfile;
            if (profile == null) return;

            // 收集所有已启用包中的配置类型 artifact
            var configPaths = new Dictionary<string, List<(string PackageKey, string DisplayName)>>();

            foreach (var entry in profile.Packages.Where(p => p.IsEnabled))
            {
                var pkg = _packageRepo.GetByKey(entry.PackageKey);
                if (pkg == null) continue;

                foreach (var artifact in pkg.Artifacts.Where(a =>
                    a.ArtifactType == ArtifactType.ConfigFile))
                {
                    var relPath = artifact.RelativeTargetPath;
                    if (!configPaths.ContainsKey(relPath))
                        configPaths[relPath] = new();
                    configPaths[relPath].Add((entry.PackageKey, pkg.DisplayName));
                }
            }

            foreach (var (path, sources) in configPaths.OrderBy(k => k.Key))
            {
                var fileName = Path.GetFileName(path);
                var format = ConfigMergeEngine.DetectFormat(path);
                ConfigFiles.Add(new ConfigFileItem
                {
                    RelativePath = path,
                    FileName = fileName,
                    Format = format,
                    SourceCount = sources.Count,
                    Sources = sources
                });
            }

            // 如果存在则自动选中第一个
            if (ConfigFiles.Count > 0)
                SelectFile(ConfigFiles[0]);
        }

        public async Task SelectFileAsync(ConfigFileItem file)
        {
            SelectFile(file);
            await LoadFileEntriesAsync(file);
        }

        private void SelectFile(ConfigFileItem file)
        {
            SelectedFile = file;
            SelectedFileName = file.FileName;
            SelectedFilePath = file.RelativePath;
        }

        public async Task LoadFileEntriesAsync(ConfigFileItem file)
        {
            IsLoading = true;
            Entries.Clear();

            try
            {
                var profile = _profileService.CurrentProfile;
                if (profile == null) return;

                // 构建合并计划
                var sources = new List<ConfigMergeSource>();
                foreach (var (pkgKey, displayName) in file.Sources)
                {
                    var pkg = _packageRepo.GetByKey(pkgKey);
                    if (pkg == null) continue;

                    var artifact = pkg.Artifacts.FirstOrDefault(a =>
                        a.RelativeTargetPath == file.RelativePath && a.ArtifactType == ArtifactType.ConfigFile);
                    if (artifact == null) continue;

                    // 读取文件内容
                    var fullPath = Path.Combine(
                        _objectStore.RepositoryRoot,
                        artifact.RelativeSourcePath);

                    if (File.Exists(fullPath))
                    {
                        var profileEntry = profile.Packages.FirstOrDefault(p => p.PackageKey == pkgKey);

                        sources.Add(new ConfigMergeSource
                        {
                            PackageKey = pkgKey,
                            DisplayName = displayName,
                            SourceFilePath = fullPath,
                            Priority = profileEntry?.Priority ?? 100
                        });
                    }
                }

                if (sources.Count == 0)
                {
                    TotalKeys = 0;
                    ConflictKeys = 0;
                    return;
                }

                var plan = new ConfigMergePlan
                {
                    TargetRelativePath = file.RelativePath,
                    Format = file.Format,
                    Strategy = ConfigMergeStrategy.MergeByKey,
                    Sources = sources
                };

                var result = await _mergeEngine.PreviewAsync(plan);

                // 填充条目
                foreach (var entry in result.EntrySourceMap)
                {
                    Entries.Add(new ConfigEntryRow
                    {
                        Section = entry.Section,
                        Key = entry.Key,
                        Value = entry.Value,
                        SourcePackageKey = entry.SourcePackageKey,
                        SourceDisplayName = entry.SourceDisplayName,
                        IsConflictResolution = entry.IsConflictResolution
                    });
                }

                TotalKeys = Entries.Count;
                ConflictKeys = result.Conflicts.Count;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "加载配置文件条目失败: {Path}", file.RelativePath);
            }
            finally
            {
                IsLoading = false;
            }
        }
    }

    public class ConfigFileItem
    {
        public string RelativePath { get; init; } = "";
        public string FileName { get; init; } = "";
        public ConfigFormat Format { get; init; }
        public int SourceCount { get; init; }
        public List<(string PackageKey, string DisplayName)> Sources { get; init; } = new();
    }

    public class ConfigEntryRow
    {
        public string Section { get; init; } = "";
        public string Key { get; init; } = "";
        public string Value { get; init; } = "";
        public string SourcePackageKey { get; init; } = "";
        public string SourceDisplayName { get; init; } = "";
        public bool IsConflictResolution { get; init; }
    }
}
