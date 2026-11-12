using UEModManager.Localization;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;
using UEModManager.Infrastructure;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class ImportDialog : Window
    {
        /// <summary>用户选择的文件路径列表（对话框结果）。</summary>
        public List<string> SelectedFiles { get; } = [];

        /// <summary>
        /// 当前游戏的引擎文件过滤器（<see cref="Models.EngineProfile.FileDialogFilter"/>）。
        ///
        /// 不传时退回下面那份通用名单：过滤器按引擎分化是刻意的 ——
        /// Godot 的 .pck、暗黑4 的 .mpq、RE 引擎的 .tex/.mesh 都不在 UE 那份名单里，
        /// 写死 UE 名单会让非 UE 玩家在自己的 MOD 文件上只能切"所有文件"。
        /// </summary>
        public string? FileFilter { get; init; }

        /// <summary>引擎未知时的兜底过滤器：通用压缩包 + UE 三件套 + 常见插件/配置。</summary>
        private const string FallbackFileFilter =
            "支持的文件|*.zip;*.rar;*.7z;*.pak;*.utoc;*.ucas;*.dll;*.ini;*.json;*.cfg|所有文件|*.*";

        /// <summary>文件选择器实际使用的过滤器（已按语言翻译）。</summary>
        internal string ResolvedFileFilter => ResolveFileFilter(FileFilter);

        /// <summary>
        /// 过滤器解析：拿不到引擎名单时退回通用名单。抽成纯函数是为了能直接测 ——
        /// 构造一个真的窗口需要整套应用资源，测这一行不值得拉上那些。
        /// </summary>
        internal static string ResolveFileFilter(string? engineFilter)
            => UiText.Get(string.IsNullOrWhiteSpace(engineFilter) ? FallbackFileFilter : engineFilter);

        public ImportDialog()
        {
            InitializeComponent();
        }

        // ─── 窗口命令 ───

        private void OnCloseWindow(object sender, ExecutedRoutedEventArgs e) => Close();

        // ─── 拖拽 ───

        private void DropZone_DragOver(object sender, DragEventArgs e)
        {
            if (e.Data.GetDataPresent(DataFormats.FileDrop))
            {
                e.Effects = DragDropEffects.Copy;
                NormalDropState.Visibility = Visibility.Collapsed;
                DragOverState.Visibility = Visibility.Visible;
                DropZoneBorder.BorderBrush = (Brush)FindResource("PrimaryBrush");
            }
            else
            {
                e.Effects = DragDropEffects.None;
            }
            e.Handled = true;
        }

        protected override void OnDragLeave(DragEventArgs e)
        {
            base.OnDragLeave(e);
            NormalDropState.Visibility = Visibility.Visible;
            DragOverState.Visibility = Visibility.Collapsed;
            DropZoneBorder.BorderBrush = (Brush)FindResource("CyberBorderBrush");
        }

        private void DropZone_Drop(object sender, DragEventArgs e)
        {
            NormalDropState.Visibility = Visibility.Visible;
            DragOverState.Visibility = Visibility.Collapsed;
            DropZoneBorder.BorderBrush = (Brush)FindResource("CyberBorderBrush");

            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

            var files = (string[])e.Data.GetData(DataFormats.FileDrop)!;
            ProcessFiles(files);
        }

        // ─── 浏览按钮 ───

        private void BrowseButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                Title = UiText.Get("选择要导入的文件"),
                Filter = ResolvedFileFilter,
                Multiselect = true
            };

            if (dlg.ShowDialog() == true)
            {
                ProcessFiles(dlg.FileNames);
            }
        }

        // ─── 处理文件 ───

        private void ProcessFiles(string[] filePaths)
        {
            // 文件夹同样合法：解压后把文件夹拖进来是最自然的用法，
            // 早先这里只认 File.Exists，文件夹被静默丢掉 —— 对话框看起来毫无反应。
            var valid = filePaths.Where(f => File.Exists(f) || Directory.Exists(f)).ToList();
            if (valid.Count == 0)
            {
                CyberMessageBox.Show(this,
                    UiText.Get("拖入的内容既不是文件也不是文件夹，请重新选择。"),
                    UiText.Get("无法导入"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var unsupportedArchives = valid
                .Where(ImportWarningMessages.IsUnsupportedArchive)
                .ToList();
            if (unsupportedArchives.Count > 0)
            {
                CyberMessageBox.Show(this,
                    ImportWarningMessages.UnsupportedArchiveMessage,
                    ImportWarningMessages.UnsupportedArchiveTitle,
                    MessageBoxButton.OK,
                    MessageBoxImage.Warning);
                return;
            }

            SelectedFiles.Clear();
            SelectedFiles.AddRange(valid);
            DialogResult = true;
            Close();
        }
    }
}
