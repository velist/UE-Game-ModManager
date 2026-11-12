using UEModManager.Localization;
using System;
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

        /// <summary>
        /// 自动检测过、但没识别出引擎。此时不能再默默按 Unreal Engine 处理 ——
        /// 那样 Unity/Godot 游戏会全程按 pak 与 Content/Paks/~mods 走，玩家看到"添加成功"、
        /// 接着每一个 MOD 都装不上，而且没有任何地方提示过原因。
        /// </summary>
        private bool _autoDetectFailed;

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
        /// 下拉框里"引擎"项的顺序（索引 0 是"自动检测"）。
        ///
        /// 这里**只有一份**表：以前"索引 → 引擎"与"引擎 → 索引"各写一个 switch，
        /// 加一个引擎要改两处，漏一处就静默错位（选 Godot 得到 Decima 那种）。
        /// XAML 里的 <c>ComboBoxItem</c> 必须与本表同序，由
        /// <c>AddCustomGameDialogSourceGuardTests</c> 钉住项数。
        /// </summary>
        internal static readonly EngineType[] EngineChoices =
        [
            EngineType.UnrealEngine,
            EngineType.Unity,
            EngineType.REEngine,
            EngineType.Godot,
            EngineType.Decima,
            EngineType.Diablo4Engine,
        ];

        /// <summary>用户选择的引擎类型（"自动检测"时由调用方处理）。</summary>
        public EngineType SelectedEngineType
        {
            get
            {
                var index = EngineTypeComboBox.SelectedIndex - 1;
                return index >= 0 && index < EngineChoices.Length ? EngineChoices[index] : EngineType.Unknown;
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
                ValidationMessage = UiText.Get("游戏名称不能为空，至少需要2个字符");
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
            try
            {
                var safeName = GameConfigService.ValidateGameName(GameName);
                if (GameConfigService.IsBuiltInGameName(safeName))
                {
                    ShowValidation(UiText.Get("这个游戏已在内置列表中，无需重复添加"));
                    return;
                }

                // 提示过"请手动选择"就必须真的让玩家选：直接放行等于把这句话变成空话。
                if (IsAutoDetect && _autoDetectFailed)
                {
                    ShowValidation(UiText.Get("未能自动识别引擎，请手动选择"));
                    return;
                }

                GameName = safeName;
                DialogResult = true;
            }
            catch (ArgumentException ex)
            {
                ShowValidation(ex.Message);
            }
        }

        private void ShowValidation(string message)
        {
            ValidationMessage = message;
            ValidationMessageText.Visibility = Visibility.Visible;
        }

        private void CancelButton_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void BrowseGamePath_Click(object sender, RoutedEventArgs e)
        {
            var dialog = new System.Windows.Forms.FolderBrowserDialog
            {
                Description = UiText.Get("选择游戏安装路径")
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
                        var choice = Array.IndexOf(EngineChoices, detected);
                        EngineTypeComboBox.SelectedIndex = choice >= 0 ? choice + 1 : 0;
                        EngineDetectionHint.Text = UiText.Interpolate($"已自动识别为 {EngineProfile.Get(detected).DisplayName} 引擎");
                        EngineDetectionHint.Visibility = Visibility.Visible;
                    }
                    else
                    {
                        _autoDetectFailed = true;
                        EngineDetectionHint.Text = UiText.Get("未能自动识别引擎，请手动选择");
                        EngineDetectionHint.Visibility = Visibility.Visible;
                    }
                }
            }
        }
    }
}
