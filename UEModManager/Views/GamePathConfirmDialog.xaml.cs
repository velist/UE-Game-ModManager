using System;
using System.ComponentModel;
using System.Windows;
using Microsoft.Win32;
using System.IO;
using System.Windows.Media;
using System.Windows.Controls;
using System.Windows.Input;
using System.Runtime.CompilerServices;

namespace UEModManager.Views
{
    public partial class GamePathConfirmDialog : Window, INotifyPropertyChanged
    {
        private string _gameName;
        private string _gamePath;
        private string _modPath;
        private string _backupPath;
        private bool _useDefaultBackupPath = true;
        private bool _autoScanOnGameSwitch = true;
        private bool _autoBackupMods = true;
        private bool _scanSubfolders = true;
        private bool _isPathsValid = false;

        // 路径状态反馈
        private string _gamePathStatus = "";
        private string _modPathStatus = "";
        private string _backupPathStatus = "";

        public string GameName
        {
            get => _gameName;
            set
            {
                _gameName = value;
                OnPropertyChanged(nameof(GameName));
            }
        }

        public string GamePath
        {
            get => _gamePath;
            set
            {
                _gamePath = value;
                OnPropertyChanged(nameof(GamePath));
                ValidatePaths();
                UpdateModPathFromGamePath();
            }
        }

        public string ModPath
        {
            get => _modPath;
            set
            {
                _modPath = value;
                OnPropertyChanged(nameof(ModPath));
                ValidatePaths();
            }
        }

        public string BackupPath
        {
            get => _backupPath;
            set
            {
                _backupPath = value;
                OnPropertyChanged(nameof(BackupPath));
                ValidatePaths();
            }
        }

        public bool UseDefaultBackupPath
        {
            get => _useDefaultBackupPath;
            set
            {
                _useDefaultBackupPath = value;
                OnPropertyChanged(nameof(UseDefaultBackupPath));
                UpdateDefaultBackupPath();
            }
        }

        public bool AutoScanOnGameSwitch
        {
            get => _autoScanOnGameSwitch;
            set
            {
                _autoScanOnGameSwitch = value;
                OnPropertyChanged(nameof(AutoScanOnGameSwitch));
            }
        }

        public bool AutoBackupMods
        {
            get => _autoBackupMods;
            set
            {
                _autoBackupMods = value;
                OnPropertyChanged(nameof(AutoBackupMods));
            }
        }

        public bool ScanSubfolders
        {
            get => _scanSubfolders;
            set
            {
                _scanSubfolders = value;
                OnPropertyChanged(nameof(ScanSubfolders));
            }
        }

        public bool IsPathsValid
        {
            get => _isPathsValid;
            set
            {
                _isPathsValid = value;
                OnPropertyChanged(nameof(IsPathsValid));
            }
        }

        public string GamePathStatus
        {
            get => _gamePathStatus;
            set
            {
                _gamePathStatus = value;
                OnPropertyChanged(nameof(GamePathStatus));
            }
        }

        public string ModPathStatus
        {
            get => _modPathStatus;
            set
            {
                _modPathStatus = value;
                OnPropertyChanged(nameof(ModPathStatus));
            }
        }

        public string BackupPathStatus
        {
            get => _backupPathStatus;
            set
            {
                _backupPathStatus = value;
                OnPropertyChanged(nameof(BackupPathStatus));
            }
        }

        // 命令绑定
        public RelayCommand BrowseGamePathCommand { get; }
        public RelayCommand BrowseModPathCommand { get; }
        public RelayCommand BrowseBackupPathCommand { get; }
        public RelayCommand ConfirmCommand { get; }
        public RelayCommand CancelCommand { get; }

        public GamePathConfirmDialog(string gameName, string gamePath, string modPath, string backupPath)
        {
            InitializeComponent();
            DataContext = this;
            
            GameName = gameName;
            GamePath = gamePath;
            ModPath = modPath;
            BackupPath = backupPath;
            
            // 初始化命令
            BrowseGamePathCommand = new RelayCommand(BrowseGamePath);
            BrowseModPathCommand = new RelayCommand(BrowseModPath);
            BrowseBackupPathCommand = new RelayCommand(BrowseBackupPath);
            ConfirmCommand = new RelayCommand(Confirm, () => IsPathsValid);
            CancelCommand = new RelayCommand(Cancel);
            
            // 验证路径
            ValidatePaths();
        }
        
        private void ValidatePaths()
        {
            bool isGamePathValid = !string.IsNullOrEmpty(GamePath) && Directory.Exists(GamePath);
            bool isModPathValid = !string.IsNullOrEmpty(ModPath);
            bool isBackupPathValid = !string.IsNullOrEmpty(BackupPath);
            
            GamePathStatus = isGamePathValid ? "✓ 路径有效" : "⚠ 游戏路径不存在";
            ModPathStatus = isModPathValid ? "✓ 路径有效" : "⚠ 请指定有效的MOD路径";
            BackupPathStatus = isBackupPathValid ? "✓ 路径有效" : "⚠ 请指定有效的备份路径";
            
            IsPathsValid = isGamePathValid && isModPathValid && isBackupPathValid;
        }
        
        private void UpdateModPathFromGamePath()
        {
            if (!string.IsNullOrEmpty(GamePath))
            {
                // 尝试推断MOD路径
                string potentialModPath = Path.Combine(GamePath, "Content", "Paks", "~mods");
                if (Directory.Exists(potentialModPath))
                {
                    ModPath = potentialModPath;
                }
                else
                {
                    // 尝试创建标准MOD路径
                    potentialModPath = Path.Combine(GamePath, "Content", "Paks", "~mods");
                    try
                    {
                        Directory.CreateDirectory(potentialModPath);
                        ModPath = potentialModPath;
                    }
                    catch
                    {
                        // 如果创建失败，使用游戏路径
                        ModPath = GamePath;
                    }
                }
            }
        }
        
        private void UpdateDefaultBackupPath()
        {
            if (UseDefaultBackupPath)
            {
                // 使用程序安装目录下的Backups子目录作为备份
                var defaultPath = Path.Combine(
                    AppDomain.CurrentDomain.BaseDirectory,
                    "Backups",
                    $"{GameName}_备份"
                );
                try
                {
                    Directory.CreateDirectory(defaultPath);
                    BackupPath = defaultPath;
                }
                catch
                {
                    // 如果创建失败，使用程序目录下的Backups文件夹
                    var fallbackPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
                    try
                    {
                        Directory.CreateDirectory(fallbackPath);
                        BackupPath = fallbackPath;
                    }
                    catch
                    {
                        // 最后的备选方案：使用程序目录
                        BackupPath = AppDomain.CurrentDomain.BaseDirectory;
                    }
                }
            }
        }
        
        private void BrowseGamePath()
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择游戏安装目录",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = GamePath // 修复：设置当前游戏路径作为初始路径
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                GamePath = dialog.SelectedPath;
            }
        }
        
        private void BrowseModPath()
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择MOD安装目录",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = ModPath // 修复：设置当前MOD路径作为初始路径
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                ModPath = dialog.SelectedPath;
            }
        }
        
        private void BrowseBackupPath()
        {
            // 确保有一个有效的初始路径
            string initialPath = BackupPath;
            if (string.IsNullOrEmpty(initialPath))
            {
                // 如果备份路径为空，使用程序安装目录下的Backups作为初始路径
                initialPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Backups");
            }
            
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择MOD备份目录",
                UseDescriptionForTitle = true,
                ShowNewFolderButton = true,
                SelectedPath = initialPath
            };

            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                BackupPath = dialog.SelectedPath;
                // 手动选择了备份路径，关闭默认选项
                UseDefaultBackupPath = false;
            }
        }
        
        private void Confirm()
        {
            if (IsPathsValid)
            {
                DialogResult = true;
                Close();
            }
        }
        
        private void Cancel()
        {
            DialogResult = false;
            Close();
        }
        
        public event PropertyChangedEventHandler PropertyChanged = delegate { };
        
        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged(this, new PropertyChangedEventArgs(propertyName));
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
    
    // 简单的命令实现
    public class RelayCommand : ICommand
    {
        private readonly Action _execute;
        private readonly Func<bool> _canExecute;
        
        public RelayCommand(Action execute, Func<bool> canExecute = null)
        {
            _execute = execute ?? throw new ArgumentNullException(nameof(execute));
            _canExecute = canExecute;
        }
        
        public bool CanExecute(object parameter)
        {
            return _canExecute == null || _canExecute();
        }
        
        public void Execute(object parameter)
        {
            _execute();
        }
        
        public event EventHandler CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }
}
