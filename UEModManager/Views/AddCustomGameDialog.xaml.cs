using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using UEModManager.Models;
using UEModManager.Services;

namespace UEModManager.Views
{
    public partial class AddCustomGameDialog : Window, INotifyPropertyChanged
    {
        private string _gameName = "";
        private string _validationMessage = "";

        public string GameName
        {
            get => _gameName;
            set
            {
                if (_gameName != value)
                {
                    _gameName = value;
                    OnPropertyChanged(nameof(GameName));
                }
            }
        }

        public string ValidationMessage
        {
            get => _validationMessage;
            set
            {
                if (_validationMessage != value)
                {
                    _validationMessage = value;
                    OnPropertyChanged(nameof(ValidationMessage));
                }
            }
        }

        /// <summary>
        /// 用户选择的引擎类型。
        /// </summary>
        public EngineType SelectedEngineType
        {
            get
            {
                return EngineTypeComboBox.SelectedIndex switch
                {
                    1 => EngineType.UnrealEngine,
                    2 => EngineType.Unity,
                    3 => EngineType.REEngine,
                    4 => EngineType.Godot,
                    5 => EngineType.Decima,
                    _ => EngineType.Unknown // 自动检测时由调用方处理
                };
            }
        }

        /// <summary>
        /// 是否选择了"自动检测"。
        /// </summary>
        public bool IsAutoDetect => EngineTypeComboBox.SelectedIndex == 0;

        public AddCustomGameDialog()
        {
            InitializeComponent();
            DataContext = this;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        protected virtual void OnPropertyChanged(string propertyName)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        private void GameNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            GameName = GameNameTextBox.Text;
            if (string.IsNullOrWhiteSpace(GameName) || GameName.Length < 2)
            {
                ValidationMessage = "游戏名称不能为空，至少需要2个字符";
                this.ValidationMessageText.Visibility = Visibility.Visible;
            }
            else
            {
                this.ValidationMessageText.Visibility = Visibility.Collapsed;
            }
        }

        private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                DragMove();
            }
        }

        private void ConfirmButton_Click(object sender, RoutedEventArgs e)
        {
            if (!string.IsNullOrWhiteSpace(GameName) && GameName.Length >= 2)
            {
                DialogResult = true;
            }
            else
            {
                ValidationMessage = "游戏名称不能为空，至少需要2个字符";
                this.ValidationMessageText.Visibility = Visibility.Visible;
            }
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void BrowseGamePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = "选择游戏安装路径"
            };
            if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            {
                GamePathTextBox.Text = dialog.SelectedPath;

                // 自动检测引擎类型
                if (EngineTypeComboBox.SelectedIndex == 0) // "自动检测"
                {
                    var detected = GameConfigService.AutoDetectEngine(dialog.SelectedPath);
                    if (detected != EngineType.Unknown)
                    {
                        EngineTypeComboBox.SelectedIndex = detected switch
                        {
                            EngineType.UnrealEngine => 1,
                            EngineType.Unity => 2,
                            EngineType.REEngine => 3,
                            EngineType.Godot => 4,
                            _ => 0
                        };
                        EngineDetectionHint.Text = $"已自动识别为 {EngineProfile.Get(detected).DisplayName} 引擎";
                        EngineDetectionHint.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        EngineDetectionHint.Text = "未能自动识别引擎，请手动选择";
                        EngineDetectionHint.Visibility = Visibility.Visible;
                    }
                }
            }
        }
    }
} 