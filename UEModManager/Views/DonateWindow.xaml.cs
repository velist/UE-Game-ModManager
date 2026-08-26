using System.Windows;

namespace UEModManager.Views
{
    /// <summary>
    /// 捐赠二维码窗口。
    ///
    /// <para>
    /// 原先这里是侧边栏里的 <c>Popup</c>（<c>StaysOpen=False</c> + 悬停触发），有两个治不好的毛病：
    /// 一是侧边栏底部"捐赠 → 使用说明书 → 头像"是鼠标移向头像的必经路径，路过就弹；
    /// 二是 <c>StaysOpen=False</c> 的 Popup 会抓走 mouse capture，配上 <c>MouseLeftButtonDown</c>
    /// 触发就变成"按下才显示、松手即消失"，且它开着时下一次点击会被消耗在关闭它上面，
    /// 派发不到光标下的控件——再撞上 <c>ShowDialog()</c> 模态窗就是"账户设置窗口打不了字"。
    /// </para>
    ///
    /// <para>
    /// 改成独立窗口后，显示与关闭都只由用户显式操作决定，不参与鼠标捕获，上述问题从根上不存在。
    /// </para>
    /// </summary>
    public partial class DonateWindow : Window
    {
        public DonateWindow()
        {
            InitializeComponent();
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();
    }
}
