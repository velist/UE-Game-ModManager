using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Infrastructure;
using UEModManager.Services.Persistence;
using UEModManager.Services.Telemetry;

namespace UEModManager.Services
{
    /// <summary>
    /// 更新检查 + 匿名用量统计。一个请求同时办两件事：客户端问"有没有新版本"，
    /// 服务端顺手记下"这台设备今天还活着、跑的什么版本"。
    ///
    /// <para><b>本类的每一条规则都是"不能影响用户"的具体化</b></para>
    /// 统计是我们的需求，不是用户的需求。所以：
    /// <list type="bullet">
    /// <item>绝不出现在启动阻塞路径上（首次上报排在主窗口 <c>Loaded</c> 之后）；</item>
    /// <item>超时硬编码 5 秒，<b>绝不用 <see cref="HttpClient"/> 的默认 100 秒</b>——
    /// 审计 6.2 那个"主窗口出现前最长假死 100 秒"就是这么来的，而遥测域名一旦被墙，
    /// 失败模式恰好是 TCP 黑洞挂满超时；</item>
    /// <item>全部失败静默：不弹窗、不重试、不写 Error 级日志；</item>
    /// <item>连续失败 3 次就停掉本次会话的后续心跳，不在不可达的网络里每 15 分钟空转一次。</item>
    /// </list>
    ///
    /// <para><b>没告知过就绝不上报</b></para>
    /// 判定归口 <see cref="TelemetryConsent"/>，每次上报前都重新读一次偏好——用户在设置里
    /// 关掉开关之后，正在跑的心跳循环必须当场停，而不是等下次启动。
    ///
    /// <para><b>上报的完整字段清单</b></para>
    /// 随机设备 UUID、应用版本、Windows 版本号，以及登录后可选的邮箱哈希。就这四项。
    /// 不含 IP（服务端也不落库）、机器名、用户名、安装路径、游戏库、MOD 名称、明文邮箱。
    /// 清单的权威定义在 <see cref="TelemetryReport"/>，本类只负责把它发出去。
    /// </summary>
    public sealed class TelemetryService : IDisposable
    {
        /// <summary>
        /// 心跳间隔。与服务端 20 分钟的"在线"窗口配套（留 5 分钟余量给网络抖动与调度延迟）。
        ///
        /// <para>
        /// <b>不要为了让在线数更实时而缩短它。</b>"当前在线人数"是这套统计里唯一支撑不了
        /// 任何决策的指标——单机桌面工具没有容量要规划、没有并发要扩容。为一个虚荣指标
        /// 付性能和额度的钱是不划算的。
        /// </para>
        /// </summary>
        private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMinutes(15);

        /// <summary>本次会话连续失败多少次之后放弃。</summary>
        private const int MaxConsecutiveFailures = 3;

        private readonly ILogger<TelemetryService>? _logger;
        private readonly Func<string?>? _currentAccountEmail;
        private readonly HttpClient _http;
        private readonly CancellationTokenSource _cts = new();

        /// <summary>本次会话已经成功上报过的账号标识，用于避免每次心跳都重复发同一个哈希。</summary>
        private string? _reportedAccountHash;

        private int _consecutiveFailures;
        private bool _heartbeatStarted;
        private bool _disposed;

        /// <param name="currentAccountEmail">
        /// 取当前登录邮箱的回调。做成委托而不是直接注入 <see cref="LocalAuthService"/>：
        /// 本服务是单例而认证服务是 scoped，更重要的是——本服务<b>不该</b>知道邮箱是从哪来的，
        /// 它只需要一个字符串去算哈希。传 <c>null</c> 即视为始终未登录。
        /// </param>
        public TelemetryService(
            ILogger<TelemetryService>? logger,
            Func<string?>? currentAccountEmail = null,
            string apiBaseUrl = "https://api.modmanger.com")
        {
            _logger = logger;
            _currentAccountEmail = currentAccountEmail;

            var baseUrl = string.IsNullOrWhiteSpace(apiBaseUrl)
                ? "https://api.modmanger.com"
                : apiBaseUrl.TrimEnd('/');

            // 与 WorkerEmailService 同一套：本服务是 DI 单例，HttpClient 与进程同寿，
            // 默认连接池不会主动淘汰连接，DNS 结果被永久缓存 —— Cloudflare 侧 IP 变更后
            // 只能靠用户重启应用才能自愈。
            var handler = new SocketsHttpHandler
            {
                PooledConnectionLifetime = TimeSpan.FromMinutes(5)
            };
            _http = new HttpClient(handler)
            {
                BaseAddress = new Uri(baseUrl + "/"),
                Timeout = TimeSpan.FromSeconds(5)
            };
            _http.DefaultRequestHeaders.Add("User-Agent", "UEModManager");
            _http.DefaultRequestHeaders.Add("Accept", "application/json");
        }

        /// <summary>当前的告知/开关状态，供 UI 判断要不要弹告知框。</summary>
        public static TelemetryConsentDecision CurrentConsent()
            => TelemetryConsent.Decide(UiPreferences.LoadTelemetryConsent());

        /// <summary>
        /// 启动周期心跳。可重复调用，只有第一次生效。
        ///
        /// <para>
        /// 整个循环跑在线程池上、完全脱离 UI 线程，<b>不 await</b>——调用方（主窗口初始化）
        /// 必须一行都不为它等待。
        /// </para>
        /// </summary>
        public void StartHeartbeat()
        {
            if (_heartbeatStarted || _disposed) return;
            _heartbeatStarted = true;

            _ = Task.Run(() => HeartbeatLoopAsync(_cts.Token));
        }

        /// <summary>
        /// 上报一次账号（登录成功时调用）。同一个哈希在本次会话内只会真正发一次。
        ///
        /// <para>
        /// 失败不重试也不记账：下一次心跳会重新带上它（<see cref="_reportedAccountHash"/>
        /// 只在发送成功后才写），所以一次网络抖动最多延迟 15 分钟，不会永久漏计。
        /// </para>
        /// </summary>
        public async Task ReportSignInAsync(string? email)
        {
            try
            {
                var hash = TelemetryIdentity.HashEmail(email);
                if (hash == null || hash == _reportedAccountHash) return;

                await SendAsync(hash, _cts.Token);
            }
            catch
            {
                // 登录路径上绝不能因为统计失败而让用户看到任何东西。
                // SendAsync 内部已经记过日志，这里只是最后一道兜网。
            }
        }

        private async Task HeartbeatLoopAsync(CancellationToken token)
        {
            try
            {
                // 首次上报延后一点：主窗口刚 Loaded 时正在加载配置、扫仓库、跑健康检查，
                // 这一刻抢带宽和线程池只会让"打开软件很慢"更明显。统计晚 10 秒毫无损失。
                await Task.Delay(TimeSpan.FromSeconds(10), token);

                while (!token.IsCancellationRequested)
                {
                    // 每一轮都重新读一次偏好，而不是在循环外读一次就定终身。
                    //
                    // 两个方向都要能当场生效：用户在设置里关掉之后，正在跑的循环必须立刻停发
                    // （"我关了它还在发"是最伤信任的一种表现）；反过来在设置里打开之后，
                    // 也应该在下一轮就开始工作，而不是"要重启一次才算数"。
                    // 关着的时候这个循环只剩一个 Task.Delay，没有网络也没有磁盘，代价可忽略。
                    var decision = CurrentConsent();
                    if (decision.ShouldReport)
                    {
                        // 只有"连续失败够多次"才真正退出——网络不通时每 15 分钟空转一次没有意义。
                        if (!await SendAsync(PendingAccountHash(), token)) return;
                    }

                    await Task.Delay(HeartbeatInterval, token);
                }
            }
            catch (OperationCanceledException)
            {
                // 进程退出 / 主窗口关闭，正常路径。
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[Telemetry] 心跳循环退出");
            }
        }

        /// <summary>
        /// 本次心跳要不要顺带带上账号。
        ///
        /// <para>
        /// 这是<b>注册数的主要来源</b>，不是补丁。绝大多数老用户靠"记住我"自动登录，
        /// 根本不经过登录窗口——只在 <c>LoginWindow</c> 里上报的话，他们一个都不会被计入。
        /// 它同时兜住另一种情形：首次运行时登录窗口出现在主窗口之前，那次上报会因为
        /// "还没告知过用户"而被正确地丢弃，之后由这里补上。
        /// </para>
        /// </summary>
        private string? PendingAccountHash()
        {
            if (_reportedAccountHash != null || _currentAccountEmail == null) return null;

            try
            {
                return TelemetryIdentity.HashEmail(_currentAccountEmail());
            }
            catch
            {
                // 拿不到当前用户就当作未登录：少统计一个账号，比让心跳整个挂掉好。
                return null;
            }
        }

        /// <summary>
        /// 发一次。<b>返回 false 表示本次会话应当停止后续心跳。</b>
        /// </summary>
        private async Task<bool> SendAsync(string? accountHash, CancellationToken token)
        {
            var decision = CurrentConsent();
            if (!decision.ShouldReport)
            {
                _logger?.LogDebug("[Telemetry] {Decision}", decision.ToString());
                return false;
            }

            var deviceId = TryGetOrCreateDeviceId();
            if (deviceId == null) return false;   // 设备标识都存不下来，重试没有意义

            if (!TelemetryReport.TryCreate(deviceId, AppVersion(), OsVersion(), accountHash, out var report))
            {
                _logger?.LogDebug("[Telemetry] 上报体构造失败，设备标识不合法");
                return false;
            }

            try
            {
                using var content = new StringContent(report.ToJson(), Encoding.UTF8, "application/json");
                using var response = await _http.PostAsync("app/update", content, token);

                if (!response.IsSuccessStatusCode)
                {
                    return NoteFailure($"HTTP {(int)response.StatusCode}");
                }

                _consecutiveFailures = 0;
                if (report.AccountHash != null) _reportedAccountHash = report.AccountHash;
                _logger?.LogDebug("[Telemetry] 上报成功（{Bytes} 字节）", report.ByteCount);
                return true;
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                throw;                                   // 关窗/退出，交给循环的 catch
            }
            catch (TaskCanceledException)
            {
                return NoteFailure("超时");             // 5 秒到了。域名被墙时就是这一条
            }
            catch (HttpRequestException ex)
            {
                return NoteFailure(ex.Message);
            }
            catch (Exception ex)
            {
                return NoteFailure(ex.Message);
            }
        }

        /// <summary>
        /// 记一次失败。<b>Debug 级，不是 Error</b>：网络不通是完全正常的状态
        /// （断网、代理、被墙、防火墙），把它记成错误只会让真正的故障淹没在噪声里。
        /// </summary>
        private bool NoteFailure(string reason)
        {
            _consecutiveFailures++;
            _logger?.LogDebug("[Telemetry] 上报失败（第 {N} 次）：{Reason}", _consecutiveFailures, reason);
            return _consecutiveFailures < MaxConsecutiveFailures;
        }

        /// <summary>
        /// 读出设备标识；没有或已损坏就新建一个。
        ///
        /// <para>
        /// 落盘失败时返回 <c>null</c> 而不是"用一个内存里的临时 ID 先发着"：临时 ID 每次启动
        /// 都不一样，发出去只会让累计设备数虚高，还查不出原因。宁可这台机器不计数。
        /// </para>
        /// </summary>
        private string? TryGetOrCreateDeviceId()
        {
            string path;
            try
            {
                path = AppPaths.DeviceIdFile;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[Telemetry] 无法定位设备标识文件");
                return null;
            }

            try
            {
                if (File.Exists(path))
                {
                    var raw = File.ReadAllText(path);
                    if (TelemetryIdentity.TryNormalizeDeviceId(raw, out var existing)) return existing;
                    // 文件在但读不出合法值（被手改过、上次写到一半断电）：直接重建。
                    // 一个服务端注定要丢弃的值一路发出去，表现是"这台机器从来不计数"，更难查。
                    _logger?.LogDebug("[Telemetry] 设备标识文件内容不合法，重新生成");
                }
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[Telemetry] 读取设备标识失败，尝试重新生成");
            }

            try
            {
                var created = TelemetryIdentity.NewDeviceId();
                // 走原子写：写到一半断电会留下半截 UUID，下次启动读出来不合法又重建一个，
                // 于是每断一次电就多一台"新设备"。
                AtomicFileWriter.WriteAllText(path, created);
                return created;
            }
            catch (Exception ex)
            {
                _logger?.LogDebug(ex, "[Telemetry] 写入设备标识失败，本机不参与统计");
                return null;
            }
        }

        private static string AppVersion()
        {
            try
            {
                return typeof(TelemetryService).Assembly.GetName().Version?.ToString(3) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string OsVersion()
        {
            try
            {
                // 只取版本号（10.0.26200），不含 "Microsoft Windows NT" 这类前缀，
                // 也不含任何本机标识。
                return Environment.OSVersion.Version.ToString();
            }
            catch
            {
                return string.Empty;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            try { _cts.Cancel(); } catch { }
            _cts.Dispose();
            _http.Dispose();
        }
    }
}
