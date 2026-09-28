using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using UEModManager.Localization;

namespace UEModManager.Views
{
    /// <summary>
    /// 统一加载遮罩（转圈 + 提示文字）。
    ///
    /// 现场症状：点下"确认导入"之后界面完全没有任何反馈 —— 解压 600 MB 压缩包、
    /// 写 1189 个文件到仓库这一整段（实测约 15 秒）里窗口是冻住的，
    /// 用户无法分辨是在工作还是已经卡死。
    ///
    /// 之所以此前完全看不到遮罩：<c>MainWindow.xaml</c> 里早就有 <c>LoadingOverlay</c> 这段 XAML，
    /// 但全项目没有任何一处代码或绑定去切换它的 <c>Visibility</c>，
    /// <c>MainViewModel.IsLoading</c> / <c>LoadingMessage</c> 也没有任何 XAML 消费者
    /// （<c>BeginLoading</c> 一直在改这两个属性，只是没人听）。于是遮罩永远停在 Collapsed。
    ///
    /// 这个控件把两者的接线收到一处：谁想显示就把 <see cref="Visibility"/> 设成 Visible，
    /// 动画在可见时自动起转、隐藏时停掉（不可见还转着会白白占一个动画时钟）。
    /// </summary>
    public partial class LoadingOverlay : UserControl
    {
        /// <summary>提示文字。默认取"正在处理..."，由调用方按场景覆盖。</summary>
        public static readonly DependencyProperty MessageProperty =
            DependencyProperty.Register(
                nameof(Message), typeof(string), typeof(LoadingOverlay),
                new PropertyMetadata(UiText.Get("正在处理..."), OnMessageChanged));

        private bool _spinning;

        public LoadingOverlay()
        {
            InitializeComponent();
            IsVisibleChanged += OnIsVisibleChanged;
        }

        /// <summary>提示文字。</summary>
        public string Message
        {
            get => (string)GetValue(MessageProperty);
            set => SetValue(MessageProperty, value);
        }

        private static void OnMessageChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var overlay = (LoadingOverlay)d;
            var text = e.NewValue as string;
            if (overlay.MessageText != null)
                overlay.MessageText.Text = string.IsNullOrEmpty(text) ? UiText.Get("正在处理...") : text;
        }

        private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
        {
            if (IsVisible) StartSpinning();
            else StopSpinning();
        }

        /// <summary>开始转圈。重复调用无副作用。</summary>
        public void StartSpinning()
        {
            if (_spinning) return;
            _spinning = true;
            ((Storyboard)Resources["SpinStoryboard"]).Begin(this, isControllable: true);
            var pulse = (Storyboard)Resources["PulseStoryboard"];
            pulse.Begin(this, isControllable: true);
        }

        /// <summary>停止转圈，避免隐藏后动画时钟继续跑。</summary>
        public void StopSpinning()
        {
            if (!_spinning) return;
            _spinning = false;
            ((Storyboard)Resources["SpinStoryboard"]).Stop(this);
            ((Storyboard)Resources["PulseStoryboard"]).Stop(this);
        }
    }
}
