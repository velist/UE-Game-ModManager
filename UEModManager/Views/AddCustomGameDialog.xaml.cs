using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

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
    }
} 