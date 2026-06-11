using System;
using System.Threading.Tasks;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.ViewModels
{
    /// <summary>
    /// MOD 详情面板 ViewModel。
    /// </summary>
    public partial class ModDetailViewModel : ObservableObject
    {
        private readonly ModManagementService _modService;
        private readonly GameConfigService _gameConfig;
        private readonly ModDataService _modData;
        private readonly ILogger _logger;
        private Func<ModInfo, bool, Task<bool>>? _toggleModAsync;
        private Func<ModInfo, string, Task<bool>>? _changePreviewAsync;
        private Func<ModInfo, Task<bool>>? _deleteModAsync;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ModName))]
        [NotifyPropertyChangedFor(nameof(FileName))]
        [NotifyPropertyChangedFor(nameof(Status))]
        [NotifyPropertyChangedFor(nameof(Category))]
        [NotifyPropertyChangedFor(nameof(FileSize))]
        [NotifyPropertyChangedFor(nameof(InstallDate))]
        [NotifyPropertyChangedFor(nameof(Description))]
        [NotifyPropertyChangedFor(nameof(IsEnabled))]
        [NotifyPropertyChangedFor(nameof(PreviewImage))]
        [NotifyPropertyChangedFor(nameof(HasMod))]
        private ModInfo? _currentMod;

        /// <summary>
        /// MOD 状态变更事件。
        /// </summary>
        public event Action? ModStateChanged;

        /// <summary>
        /// 关闭面板事件。
        /// </summary>
        public event Action? CloseRequested;

        public ModDetailViewModel(
            ModManagementService modService,
            GameConfigService gameConfig,
            ModDataService modData,
            ILogger logger)
        {
            _modService = modService;
            _gameConfig = gameConfig;
            _modData = modData;
            _logger = logger;
        }

        public void ConfigureActions(
            Func<ModInfo, bool, Task<bool>> toggleModAsync,
            Func<ModInfo, string, Task<bool>> changePreviewAsync,
            Func<ModInfo, Task<bool>> deleteModAsync)
        {
            _toggleModAsync = toggleModAsync;
            _changePreviewAsync = changePreviewAsync;
            _deleteModAsync = deleteModAsync;
        }

        // ─── 绑定属性 ───

        public bool HasMod => CurrentMod != null;
        public string ModName => CurrentMod?.Name ?? string.Empty;
        public string FileName => CurrentMod?.RealName ?? string.Empty;
        public string Status => CurrentMod?.IsEnabled == true ? "已启用" : "已禁用";
        public string Category => CurrentMod?.PrimaryCategory ?? "未分类";
        public string FileSize => CurrentMod?.FormattedSize ?? string.Empty;
        public string InstallDate => CurrentMod?.FormattedInstallDate ?? string.Empty;
        public string Description => CurrentMod?.Description ?? string.Empty;
        public bool IsEnabled => CurrentMod?.IsEnabled ?? false;

        public ImageSource? PreviewImage
        {
            get
            {
                if (CurrentMod == null) return null;

                // 优先使用已加载的图片
                if (CurrentMod.PreviewImage != null)
                    return CurrentMod.PreviewImage;

                // 尝试从路径加载
                if (!string.IsNullOrEmpty(CurrentMod.PreviewImagePath) &&
                    System.IO.File.Exists(CurrentMod.PreviewImagePath))
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.BeginInit();
                        bitmap.UriSource = new Uri(CurrentMod.PreviewImagePath, UriKind.Absolute);
                        bitmap.CacheOption = BitmapCacheOption.OnLoad;
                        bitmap.EndInit();
                        bitmap.Freeze();
                        CurrentMod.PreviewImage = bitmap;
                        return bitmap;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "加载预览图失败: {Path}", CurrentMod.PreviewImagePath);
                    }
                }

                return null;
            }
        }

        // ─── 命令 ───

        /// <summary>
        /// 切换启用/禁用。
        /// </summary>
        [RelayCommand]
        public async Task ToggleEnabledAsync()
        {
            if (CurrentMod == null) return;

            var success = _toggleModAsync != null
                ? await _toggleModAsync(CurrentMod, !CurrentMod.IsEnabled)
                : await ToggleLegacyAsync(CurrentMod);

            if (success)
            {
                OnPropertyChanged(nameof(Status));
                OnPropertyChanged(nameof(IsEnabled));
                ModStateChanged?.Invoke();
            }
        }

        /// <summary>
        /// 更换预览图。
        /// </summary>
        [RelayCommand]
        public async Task ChangePreviewAsync()
        {
            if (CurrentMod == null) return;

            var dialog = new Microsoft.Win32.OpenFileDialog
            {
                Title = "选择预览图",
                Filter = "图片文件|*.jpg;*.jpeg;*.png;*.bmp;*.gif;*.webp|所有文件|*.*"
            };

            if (dialog.ShowDialog() == true)
            {
                var changed = _changePreviewAsync != null
                    ? await _changePreviewAsync(CurrentMod, dialog.FileName)
                    : await ChangePreviewLegacyAsync(dialog.FileName);
                if (changed)
                {
                    CurrentMod.PreviewImage = null;
                    OnPropertyChanged(nameof(PreviewImage));
                }
            }
        }

        /// <summary>
        /// 删除当前 MOD。
        /// </summary>
        [RelayCommand]
        public async Task DeleteAsync()
        {
            if (CurrentMod == null) return;

            var success = _deleteModAsync != null
                ? await _deleteModAsync(CurrentMod)
                : await DeleteLegacyAsync(CurrentMod);

            if (success)
            {
                CurrentMod = null;
                ModStateChanged?.Invoke();
                CloseRequested?.Invoke();
            }
        }

        private async Task<bool> ToggleLegacyAsync(ModInfo mod)
        {
            if (mod.IsEnabled)
                return await _modService.DisableModAsync(mod, _gameConfig.CurrentModPath, _gameConfig.CurrentBackupPath);

            return await _modService.EnableModAsync(mod, _gameConfig.CurrentModPath, _gameConfig.CurrentBackupPath);
        }

        private async Task<bool> ChangePreviewLegacyAsync(string imagePath)
        {
            if (CurrentMod == null) return false;

            var newPath = _modService.ChangePreviewImage(CurrentMod, imagePath, _gameConfig.CurrentBackupPath);
            if (newPath == null) return false;

            await _modData.SaveModAsync(CurrentMod);
            return true;
        }

        private async Task<bool> DeleteLegacyAsync(ModInfo mod)
        {
            if (!await _modService.DeleteModAsync(mod, _gameConfig.CurrentModPath, _gameConfig.CurrentBackupPath))
                return false;

            await _modData.RemoveModAsync(mod.Id);
            return true;
        }

        /// <summary>
        /// 关闭详情面板。
        /// </summary>
        [RelayCommand]
        public void Close()
        {
            CloseRequested?.Invoke();
        }
    }
}
