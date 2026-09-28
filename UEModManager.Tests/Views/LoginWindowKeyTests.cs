using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Data;
using UEModManager.Services;
using UEModManager.Tests.Themes;
using UEModManager.Views;

namespace UEModManager.Tests.Views;

/// <summary>
/// 登录窗口的键盘出口：两个输入框里按回车要等价于点它下面那个按钮。
///
/// <para>
/// 这里钉的是<b>接线</b>，不是判断函数：只测"回车该不该发"的纯逻辑，在
/// <c>KeyDown</c> 忘了挂到 XAML 上时仍然会全绿。所以用真的
/// <see cref="LoginWindow"/> 派发真的 <c>KeyDownEvent</c>，断言落在最外面可见的事实上——
/// 验证码请求有没有真的从 <c>HttpClient</c> 出去。
/// </para>
///
/// <para>
/// <b>为什么不给 <c>CustomOtpService</c> 加接口来做假服务：</b>"回车有没有用"的最终判据是
/// 请求有没有发出去，而这一层（<c>WorkerEmailService</c> → <c>HttpClient</c>）比服务层更靠外，
/// 断言更有说服力，也顺带覆盖了"参数确实是用户打的那个邮箱"。假 <c>HttpMessageHandler</c>
/// 是仓库里已有的做法（见 <c>WorkerOtpProtocolTests</c>）。
/// </para>
///
/// <para>
/// <b>窗口不 <c>Show()</c>：</b>离屏窗口没有 <c>PresentationSource</c>，而 <c>KeyEventArgs</c>
/// 的构造函数要求一个真的输入源。这里借一个不可见的 <see cref="HwndSource"/> 把这件事补上，
/// 既拿得到输入源，又不会真的弹出窗口、抢焦点或需要消息循环。
/// </para>
///
/// <para>
/// 窗口构造时注入一个假的消息框：真的 <c>CyberMessageBox.Show</c> 内部是模态的
/// <c>ShowDialog</c>，成功和失败两个分支都会把测试挂死在消息泵里。
/// </para>
/// </summary>
[Collection(LanguageIntegrationCollection.Name)]
public sealed class LoginWindowKeyTests
{
    /// <summary>服务端认可的最小 challenge_id：64 位小写十六进制。</summary>
    private static readonly string Challenge = new('a', 64);

    private const string Email = "player@example.test";

    [Fact]
    public void 邮箱里回车会真的把验证码请求发出去一次()
        => OnWindow((window, worker, dialogs) =>
        {
            SetText(window, "EmailTextBox", Email);
            Assert.True(Button(window, "SendOtpButton").IsEnabled, "合法邮箱应当让发送按钮可用");

            Assert.True(Enter(window, "EmailTextBox").Handled, "EmailTextBox 没有处理回车");

            var request = Assert.Single(worker.Requests);
            Assert.EndsWith("/api/auth/otp/request", request.Path);
            Assert.Equal(Email, request.Fields["email"]);

            // 走完了成功分支：验证码区露出来、倒计时把按钮压灰。
            Assert.Equal(Visibility.Visible, Panel(window, "OtpInputPanel").Visibility);
            Assert.False(Button(window, "SendOtpButton").IsEnabled, "发送成功后应进入倒计时");
            Assert.Single(dialogs);
        });

    [Fact]
    public void 邮箱不合法时回车与灰按钮一样什么都不做()
        => OnWindow((window, worker, dialogs) =>
        {
            SetText(window, "EmailTextBox", "not-an-email");
            Assert.False(Button(window, "SendOtpButton").IsEnabled);

            Assert.True(Enter(window, "EmailTextBox").Handled);

            // 回车必须和点灰按钮完全一致：不发请求，也不弹提示。
            Assert.Empty(worker.Requests);
            Assert.Empty(dialogs);
        });

    [Fact]
    public void 验证码不足六位时回车不发起验证()
        => OnWindow((window, worker, dialogs) =>
        {
            SetText(window, "EmailTextBox", Email);
            SetText(window, "OtpTextBox", "123");
            Assert.False(Button(window, "VerifyLoginButton").IsEnabled);

            Assert.True(Enter(window, "OtpTextBox").Handled);

            Assert.Empty(worker.Requests);
            Assert.Empty(dialogs);
        });

    [Fact]
    public void 验证码框里回车会发起验证登录请求()
        => OnWindow((window, worker, dialogs) =>
        {
            // 先走一次发送，让服务端 challenge 落到本地，否则验证会被本地拦下。
            SetText(window, "EmailTextBox", Email);
            Enter(window, "EmailTextBox");
            Assert.Single(worker.Requests);

            SetText(window, "OtpTextBox", "123456");
            Assert.True(Button(window, "VerifyLoginButton").IsEnabled, "六位验证码应当让验证按钮可用");

            Assert.True(Enter(window, "OtpTextBox").Handled, "OtpTextBox 没有处理回车");

            Assert.Equal(2, worker.Requests.Count);
            Assert.EndsWith("/api/auth/otp/verify", worker.Requests[1].Path);
            Assert.Equal("123456", worker.Requests[1].Fields["code"]);
        });

    [Fact]
    public void 非回车按键不会被吞掉也不会发请求()
        => OnWindow((window, worker, dialogs) =>
        {
            SetText(window, "EmailTextBox", Email);

            var args = Press(window, "EmailTextBox", Key.A);

            Assert.False(args.Handled, "只有回车才该被本窗口消费掉");
            Assert.Empty(worker.Requests);
        });

    // ─── 夹具 ───

    private static TextBox Box(Window window, string name) => Assert.IsType<TextBox>(window.FindName(name));
    private static Button Button(Window window, string name) => Assert.IsType<Button>(window.FindName(name));
    private static StackPanel Panel(Window window, string name) => Assert.IsType<StackPanel>(window.FindName(name));
    private static void SetText(Window window, string name, string text) => Box(window, name).Text = text;

    private static KeyEventArgs Enter(Window window, string name) => Press(window, name, Key.Enter);

    /// <summary>向指定输入框派发一次真实的按键，并回传该事件供断言。</summary>
    private static KeyEventArgs Press(Window window, string name, Key key)
    {
        var args = new KeyEventArgs(Keyboard.PrimaryDevice, Source!, 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent
        };
        Box(window, name).RaiseEvent(args);
        return args;
    }

    /// <summary>当前用例的不可见输入源，<see cref="HwndSource"/> 创建的 HWND 不会显示。</summary>
    private static HwndSource? Source;

    private static void OnWindow(Action<LoginWindow, OtpWorkerHandler, List<string>> check)
        => ThemeResourceLoader.RunOnSta(() =>
        {
            using var db = new LocalDbContext(
                new DbContextOptionsBuilder<LocalDbContext>().UseSqlite("Data Source=:memory:").Options);
            using var handler = new OtpWorkerHandler();
            using var client = new HttpClient(handler) { BaseAddress = new Uri("https://auth.example.test") };
            using var worker = new WorkerEmailService(client, NullLogger<WorkerEmailService>.Instance);
            var auth = new LocalAuthService(db, NullLogger<LocalAuthService>.Instance);
            var otp = new CustomOtpService(NullLogger<CustomOtpService>.Instance, worker);

            var dialogs = new List<string>();
            LoginWindow.ShowMessage record = (_, message, _, _, _) =>
            {
                dialogs.Add(message);
                return MessageBoxResult.OK;
            };

            using var source = new HwndSource(new HwndSourceParameters("LoginWindowKeyTests")
            {
                Width = 1,
                Height = 1
            });
            Source = source;
            try
            {
                var window = new LoginWindow(auth, otp, NullLogger<LoginWindow>.Instance, ResourcesFor(), record);
                try { check(window, handler, dialogs); }
                finally { window.Close(); }
            }
            finally { Source = null; }

            return true;
        });

    /// <summary>窗口 XAML 里声明的资源（测试不创建全局 Application，就地挂一份）。</summary>
    private static ResourceDictionary ResourcesFor()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, "UEModManager.sln")))
            directory = directory.Parent;
        Assert.NotNull(directory);

        return ViewResourceLoader.Load(
            Path.Combine(directory!.FullName, "UEModManager"), "Views/LoginWindow.xaml").Resources;
    }

    /// <summary>
    /// 假的 Worker 端点：<c>request</c> 返回一个服务端认可的 challenge，<c>verify</c> 一律拒绝。
    ///
    /// <para>
    /// 验证一律失败是刻意的——成功会走到 <c>LocalAuthService</c> 与<b>真的关闭窗口</b>，
    /// 而这条用例只关心"请求有没有发出去"。失败分支同样能证明接线成立。
    /// </para>
    /// </summary>
    private sealed class OtpWorkerHandler : HttpMessageHandler
    {
        public List<(string Path, Dictionary<string, string> Fields)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            using var body = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellationToken));
            Requests.Add((request.RequestUri!.AbsolutePath, body.RootElement.EnumerateObject()
                .ToDictionary(field => field.Name, field => field.Value.GetString() ?? string.Empty)));

            return Reply(request.RequestUri!.AbsolutePath.EndsWith("request", StringComparison.Ordinal)
                ? $$"""{"success":true,"message":"验证码已发送","challenge_id":"{{Challenge}}","expires_in":600,"retry_after":60}"""
                : """{"success":false,"message":"invalid code"}""");
        }

        private static HttpResponseMessage Reply(string json) => new(System.Net.HttpStatusCode.OK)
        {
            Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json")
        };
    }
}
