using UEModManager.Localization;
using System;
using System.Threading.Tasks;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Logging;
using UEModManager.Models;

namespace UEModManager.ViewModels
{
    /// <summary>
    /// MOD 详情面板 ViewModel（主窗口右侧滑出面板）。
    ///
    /// 面板按钮全部走这里的命令，命令只把操作转交给 <see cref="ConfigureActions"/>
    /// 注入的回调；打开文件对话框、确认框、失败提示都留在 MainWindow code-behind，
    /// 与右键菜单共用同一批 FromUiAsync 帮助方法——同一个操作只有一份实现。
    ///
    /// 文本属性（<see cref="StatusText"/> / <see cref="ToggleText"/> / <see cref="Category"/>）
    /// 刻意返回**未翻译的源串**，翻译交给 XAML 的 <c>loc:Value</c>：那条路带语言订阅，
    /// 切换语言时会自己重取；在 VM 里翻好再绑，语言切了文案不会跟着变。
    /// </summary>
    public partial class ModDetailViewModel : ObservableObject
    {
        private readonly ILogger _logger;
        private Func<ModInfo, bool, Task<bool>>? _toggleModAsync;
        private Func<ModInfo, Task<bool>>? _changePreviewAsync;
        private Func<ModInfo, Task<bool>>? _deleteModAsync;
        private Func<ModInfo, Task<bool>>? _renameAsync;
        private Func<ModInfo, string, Task<bool>>? _moveToCategoryAsync;
        private Func<ModInfo, Task<bool>>? _editNoteAsync;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ModName))]
        [NotifyPropertyChangedFor(nameof(FileName))]
        [NotifyPropertyChangedFor(nameof(StatusText))]
        [NotifyPropertyChangedFor(nameof(ToggleText))]
        [NotifyPropertyChangedFor(nameof(Category))]
        [NotifyPropertyChangedFor(nameof(FileSize))]
        [NotifyPropertyChangedFor(nameof(InstallDate))]
        [NotifyPropertyChangedFor(nameof(Description))]
        [NotifyPropertyChangedFor(nameof(HasDescription))]
        [NotifyPropertyChangedFor(nameof(HasNoDescription))]
        [NotifyPropertyChangedFor(nameof(IsEnabled))]
        [NotifyPropertyChangedFor(nameof(PreviewImage))]
        [NotifyPropertyChangedFor(nameof(HasPreview))]
        [NotifyPropertyChangedFor(nameof(HasNoPreview))]
        [NotifyPropertyChangedFor(nameof(HasMod))]
        private ModInfo? _currentMod;

        /// <summary>
        /// MOD 状态变更事件（启用/禁用、删除等之后要重算计数）。
        /// </summary>
        public event Action? ModStateChanged;

        /// <summary>
        /// 关闭面板事件。
        /// </summary>
        public event Action? CloseRequested;

        public ModDetailViewModel(ILogger logger)
        {
            _logger = logger;
        }

        public void ConfigureActions(
            Func<ModInfo, bool, Task<bool>> toggleModAsync,
            Func<ModInfo, Task<bool>> changePreviewAsync,
            Func<ModInfo, Task<bool>> deleteModAsync,
            Func<ModInfo, Task<bool>> renameAsync,
            Func<ModInfo, string, Task<bool>> moveToCategoryAsync,
            Func<ModInfo, Task<bool>> editNoteAsync)
        {
            _toggleModAsync = toggleModAsync;
            _changePreviewAsync = changePreviewAsync;
            _deleteModAsync = deleteModAsync;
            _renameAsync = renameAsync;
            _moveToCategoryAsync = moveToCategoryAsync;
            _editNoteAsync = editNoteAsync;
        }

        // ─── 绑定属性 ───

        public bool HasMod => CurrentMod != null;
        public string ModName => CurrentMod?.Name ?? string.Empty;
        public string FileName => CurrentMod?.RealName ?? string.Empty;

        /// <summary>未翻译的源串，显示交给 XAML 的 loc:Value。</summary>
        public string StatusText => CurrentMod?.IsEnabled == true ? "已启用" : "已禁用";

        /// <summary>未翻译的源串（按钮文案：点下去会发生什么）。</summary>
        public string ToggleText => CurrentMod?.IsEnabled == true ? "禁用MOD" : "启用MOD";

        /// <summary>未翻译的分类名（内置分类由 loc:Value 的 CategoryName 翻译，玩家分类原样保留）。</summary>
        public string Category => CurrentMod?.PrimaryCategory ?? "未分类";

        public string FileSize => CurrentMod?.FormattedSize ?? string.Empty;
        public string InstallDate => CurrentMod?.FormattedInstallDate ?? string.Empty;
        public string Description => CurrentMod?.Description ?? string.Empty;
        public bool HasDescription => !string.IsNullOrWhiteSpace(CurrentMod?.Description);
        public bool HasNoDescription => !HasDescription;
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
                        var bitmap = UEModManager.Infrastructure.ImageLoader.LoadFrozen(CurrentMod.PreviewImagePath);
                        if (bitmap != null)
                        {
                            CurrentMod.PreviewImage = bitmap;
                            return bitmap;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "加载预览图失败: {Path}", CurrentMod.PreviewImagePath);
                    }
                }

                return null;
            }
        }

        public bool HasPreview => PreviewImage != null;
        public bool HasNoPreview => !HasPreview;

        // ─── 命令 ───

        /// <summary>
        /// 切换启用/禁用。
        /// </summary>
        [RelayCommand]
        public async Task ToggleEnabledAsync()
        {
            if (CurrentMod == null) return;
            if (_toggleModAsync == null)
                throw DeploymentServiceNotInitialized(UiText.Get("切换 MOD"));

            var success = await _toggleModAsync(CurrentMod, !CurrentMod.IsEnabled);

            if (success)
            {
                OnPropertyChanged(nameof(StatusText));
                OnPropertyChanged(nameof(ToggleText));
                OnPropertyChanged(nameof(IsEnabled));
                ModStateChanged?.Invoke();
            }
        }

        /// <summary>
        /// 更换预览图（文件对话框在 code-behind 的回调里）。
        /// </summary>
        [RelayCommand]
        public async Task ChangePreviewAsync()
        {
            if (CurrentMod == null) return;
            if (_changePreviewAsync == null)
                throw DeploymentServiceNotInitialized(UiText.Get("更换预览图"));

            if (await _changePreviewAsync(CurrentMod))
            {
                OnPropertyChanged(nameof(PreviewImage));
                OnPropertyChanged(nameof(HasPreview));
                OnPropertyChanged(nameof(HasNoPreview));
            }
        }

        /// <summary>
        /// 编辑显示名称（输入框在 code-behind 的回调里）。
        /// </summary>
        [RelayCommand]
        public async Task RenameAsync()
        {
            if (CurrentMod == null) return;
            if (_renameAsync == null)
                throw DeploymentServiceNotInitialized(UiText.Get("编辑名称"));

            if (await _renameAsync(CurrentMod))
                OnPropertyChanged(nameof(ModName));
        }

        /// <summary>
        /// 编辑描述/备注（多行输入框在 code-behind 的回调里）。
        /// </summary>
        [RelayCommand]
        public async Task EditNoteAsync()
        {
            if (CurrentMod == null) return;
            if (_editNoteAsync == null)
                throw DeploymentServiceNotInitialized(UiText.Get("编辑描述"));

            if (await _editNoteAsync(CurrentMod))
            {
                OnPropertyChanged(nameof(Description));
                OnPropertyChanged(nameof(HasDescription));
                OnPropertyChanged(nameof(HasNoDescription));
            }
        }

        /// <summary>
        /// 移动到指定分类。分类名单由 code-behind 用 AssignableCategories 现算，
        /// 这里只负责把选中的分类名转交给回调。
        /// </summary>
        [RelayCommand]
        public async Task MoveToCategoryAsync(string? categoryName)
        {
            if (CurrentMod == null || string.IsNullOrWhiteSpace(categoryName)) return;
            if (_moveToCategoryAsync == null)
                throw DeploymentServiceNotInitialized(UiText.Get("移动到分类"));

            if (await _moveToCategoryAsync(CurrentMod, categoryName))
                OnPropertyChanged(nameof(Category));
        }

        /// <summary>
        /// 删除当前 MOD（确认框与仓库删除都在 code-behind 的回调里）。
        /// </summary>
        [RelayCommand]
        public async Task DeleteAsync()
        {
            if (CurrentMod == null) return;
            if (_deleteModAsync == null)
                throw DeploymentServiceNotInitialized(UiText.Get("删除 MOD"));

            var success = await _deleteModAsync(CurrentMod);

            if (success)
            {
                CurrentMod = null;
                ModStateChanged?.Invoke();
                CloseRequested?.Invoke();
            }
        }

        /// <summary>
        /// 关闭详情面板。
        /// </summary>
        [RelayCommand]
        public void Close()
        {
            CloseRequested?.Invoke();
        }

        private InvalidOperationException DeploymentServiceNotInitialized(string operation)
        {
            _logger.LogError("详情面板操作未接线: {Operation}", operation);
            return new InvalidOperationException(UiText.Get("部署服务未初始化"));
        }
    }
}
