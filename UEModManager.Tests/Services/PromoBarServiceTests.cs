using UEModManager.Services;
using UEModManager.Services.Promo;

namespace UEModManager.Tests.Services;

/// <summary>
/// 推广条的"要不要展示 + 记账 + 打开链接"，连着真实的 <see cref="UiPreferences"/> 一起跑。
///
/// <para>
/// 判定规则本身在 Core 里有单测（<c>PromoScheduleTests</c>）；这里钉的是接线：
/// 日期确实取自注入的时钟、展示记录确实落了盘并且重启后读得回来、
/// 记不上账时确实不展示、过期之后入口确实什么都不做。
/// </para>
///
/// <para>
/// <b>绝不触碰开发机真实的 <c>ui_config.json</c>，也绝不真的打开浏览器。</b>
/// 每条用例都把配置重定向到临时目录；打开链接的动作一律换成只记录不执行的假实现。
/// </para>
/// </summary>
[Collection(UiPreferencesStaticStateCollection.Name)]
public sealed class PromoBarServiceTests : IDisposable
{
    private const string OwnerLink = "https://gptpluscz.com/offer/ZpWxYTSoWT_ZMxGDFyLF99zRfeZXecxYa5gm7Zk4ajI";

    private static readonly DateOnly Nov25 = new(2026, 11, 25);
    private static readonly DateOnly Nov26 = new(2026, 11, 26);
    private static readonly DateOnly Dec31 = new(2026, 12, 31);
    private static readonly DateOnly Jan01 = new(2027, 1, 1);

    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "uemm_promo_" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>假浏览器收到的链接。任何一条用例都不该让真浏览器弹出来。</summary>
    private readonly List<string> _opened = new();

    public PromoBarServiceTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private string Config(string name)
    {
        var directory = Path.Combine(_root, name);
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "ui_config.json");
    }

    private PromoBarService Service(DateOnly today, PromoCampaign? campaign = null, Action<string>? open = null)
        => new(
            today: () => today,
            campaign: () => campaign ?? PromoCampaigns.GptTopUpCoupon,
            openInBrowser: open ?? _opened.Add);

    /// <summary>模拟"关掉软件再打开"：丢掉内存里的配置，下一次读取重新读盘。</summary>
    private static IDisposable Restart(string config) => UiPreferences.OverrideConfigPathForTests(config);

    // ─── 数据 ───

    [Fact]
    public void 链接是https_且与所有者给的一字不差()
    {
        var url = PromoCampaigns.Current!.Url;

        Assert.Equal(OwnerLink, url);
        Assert.StartsWith("https://", url, StringComparison.Ordinal);
        Assert.True(PromoLink.IsAllowed(url));
    }

    // ─── 展示入口 ───

    [Fact]
    public void 新手引导还没看过_入口不展示_也不记账()
    {
        var config = Config("onboarding");
        using var _ = UiPreferences.OverrideConfigPathForTests(config);

        Assert.Null(Service(Nov25).TryBeginDisplay());

        Assert.Equal(PromoDisplayRecord.Never, UiPreferences.LoadPromoDisplayRecord());
        Assert.False(File.Exists(config));
    }

    [Fact]
    public void 当天第一次启动_展示并记账()
    {
        var config = Config("first");
        using var _ = UiPreferences.OverrideConfigPathForTests(config);
        UiPreferences.SaveOnboardingGuideShown(true);

        var campaign = Service(Nov25).TryBeginDisplay();

        Assert.Same(PromoCampaigns.GptTopUpCoupon, campaign);
        Assert.Equal(new PromoDisplayRecord(Nov25, campaign!.Id), UiPreferences.LoadPromoDisplayRecord());
    }

    [Fact]
    public void 同一天重启_不再展示()
    {
        var config = Config("same_day");
        using (UiPreferences.OverrideConfigPathForTests(config))
        {
            UiPreferences.SaveOnboardingGuideShown(true);
            Assert.NotNull(Service(Nov25).TryBeginDisplay());

            // 同一次运行里再问一遍
            Assert.Null(Service(Nov25).TryBeginDisplay());
        }

        // 关掉软件再打开：记录在磁盘上，照样不展示
        using (Restart(config))
        {
            Assert.Null(Service(Nov25).TryBeginDisplay());
            Assert.Equal(PromoSkipReason.AlreadyShownToday, Service(Nov25).Evaluate().SkipReason);
        }
    }

    [Fact]
    public void 第二天重启_又展示一次()
    {
        var config = Config("next_day");
        using (UiPreferences.OverrideConfigPathForTests(config))
        {
            UiPreferences.SaveOnboardingGuideShown(true);
            Assert.NotNull(Service(Nov25).TryBeginDisplay());
        }

        using (Restart(config))
        {
            Assert.NotNull(Service(Nov26).TryBeginDisplay());
            Assert.Equal(Nov26, UiPreferences.LoadPromoDisplayRecord().LastShownDate);
            Assert.Null(Service(Nov26).TryBeginDisplay());
        }
    }

    [Fact]
    public void 最后展示日当天_照常展示()
    {
        using var _ = UiPreferences.OverrideConfigPathForTests(Config("last_day"));
        UiPreferences.SaveOnboardingGuideShown(true);

        Assert.NotNull(Service(Dec31).TryBeginDisplay());
    }

    [Theory]
    [InlineData(2027, 1, 1)]
    [InlineData(2027, 6, 1)]
    [InlineData(2031, 12, 31)]
    public void 过期后_入口不展示_也不改配置(int year, int month, int day)
    {
        var config = Config($"expired_{year}{month}{day}");
        using var _ = UiPreferences.OverrideConfigPathForTests(config);
        UiPreferences.SaveOnboardingGuideShown(true);
        var before = File.ReadAllText(config);

        var service = Service(new DateOnly(year, month, day));

        Assert.Null(service.TryBeginDisplay());
        Assert.Equal(PromoSkipReason.Expired, service.Evaluate().SkipReason);
        Assert.Equal(before, File.ReadAllText(config));
        Assert.Equal(PromoDisplayRecord.Never, UiPreferences.LoadPromoDisplayRecord());
    }

    [Fact]
    public void 十二月三十一日看过_元旦起再也不出现()
    {
        var config = Config("over_new_year");
        using (UiPreferences.OverrideConfigPathForTests(config))
        {
            UiPreferences.SaveOnboardingGuideShown(true);
            Assert.NotNull(Service(Dec31).TryBeginDisplay());
        }

        using (Restart(config))
        {
            for (var day = Jan01; day <= new DateOnly(2027, 1, 15); day = day.AddDays(1))
            {
                Assert.Null(Service(day).TryBeginDisplay());
            }

            // 过期后不再碰展示记录：最后一次停在 12-31
            Assert.Equal(Dec31, UiPreferences.LoadPromoDisplayRecord().LastShownDate);
        }
    }

    [Fact]
    public void 换了活动_当天可以再展示一次()
    {
        using var _ = UiPreferences.OverrideConfigPathForTests(Config("new_campaign"));
        UiPreferences.SaveOnboardingGuideShown(true);
        Assert.NotNull(Service(Nov25).TryBeginDisplay());

        var next = PromoCampaigns.GptTopUpCoupon with { Id = "another-campaign" };

        Assert.Same(next, Service(Nov25, next).TryBeginDisplay());
        Assert.Equal("another-campaign", UiPreferences.LoadPromoDisplayRecord().LastShownCampaignId);
        Assert.Null(Service(Nov25, next).TryBeginDisplay());
    }

    [Fact]
    public void 上次展示日期在未来_照常展示_并把记录写回今天()
    {
        using var _ = UiPreferences.OverrideConfigPathForTests(Config("clock_moved_back"));
        UiPreferences.SaveOnboardingGuideShown(true);

        // 玩家把系统时间调到过 12-20，看过一次，又调回来
        Assert.NotNull(Service(new DateOnly(2026, 12, 20)).TryBeginDisplay());

        Assert.NotNull(Service(Nov25).TryBeginDisplay());
        Assert.Equal(Nov25, UiPreferences.LoadPromoDisplayRecord().LastShownDate);
        Assert.Null(Service(Nov25).TryBeginDisplay());
    }

    [Fact]
    public void 没有活动时_入口不展示()
    {
        using var _ = UiPreferences.OverrideConfigPathForTests(Config("no_campaign"));
        UiPreferences.SaveOnboardingGuideShown(true);

        var service = new PromoBarService(today: () => Nov25, campaign: () => null, openInBrowser: _opened.Add);

        Assert.Null(service.TryBeginDisplay());
        Assert.Equal(PromoSkipReason.NoCampaign, service.Evaluate().SkipReason);
    }

    // ─── 记账 ───

    [Fact]
    public void 日期与活动标识在同一次写里落盘()
    {
        var config = Config("atomic");
        using var _ = UiPreferences.OverrideConfigPathForTests(config);
        UiPreferences.SaveOnboardingGuideShown(true);

        Assert.NotNull(Service(Nov25).TryBeginDisplay());

        var json = File.ReadAllText(config);
        Assert.Contains("\"PromoLastShownDate\": \"2026-11-25\"", json);
        Assert.Contains("\"PromoLastShownCampaignId\": \"gpt-topup-15off-2026\"", json);
    }

    [Fact]
    public void 记账不会动到别的设置()
    {
        var config = Config("keeps_others");
        using (UiPreferences.OverrideConfigPathForTests(config))
        {
            UiPreferences.SaveRepositoryRoot(@"D:\玩家的 MOD");
            UiPreferences.SaveEnglish(true);
            UiPreferences.SaveOnboardingGuideShown(true);

            Assert.NotNull(Service(Nov25).TryBeginDisplay());
        }

        using (Restart(config))
        {
            Assert.Equal(@"D:\玩家的 MOD", UiPreferences.LoadRepositoryRoot());
            Assert.True(UiPreferences.TryLoadEnglish(out var english) && english);
            Assert.True(UiPreferences.LoadOnboardingGuideShown());
        }
    }

    /// <summary>
    /// 对玩家说的是"每天最多一次"。配置写不进去（磁盘满、权限、被杀软锁住）时记不住今天展示过，
    /// 照样显示的话同一天每次启动都会再出现——所以记不上账就不展示。
    /// </summary>
    [Fact]
    public void 记不上账时_不展示()
    {
        var config = Config("unwritable");
        using var _ = UiPreferences.OverrideConfigPathForTests(config);
        UiPreferences.SaveOnboardingGuideShown(true);

        // 把配置文件的位置换成一个目录：之后任何写入都必然失败
        File.Delete(config);
        Directory.CreateDirectory(config);

        Assert.True(Service(Nov25).Evaluate().ShouldShow);
        Assert.Null(Service(Nov25).TryBeginDisplay());
        Assert.False(UiPreferences.TrySavePromoDisplayRecord(Nov25, "gpt-topup-15off-2026"));
    }

    /// <summary>
    /// 配置文件存在但这次没读出来（被别的程序独占）：内存里只有一份默认值。
    /// 这时候为了记一条广告展示把默认值写回去，会覆盖掉玩家的仓库位置——绝不能发生。
    /// </summary>
    [Fact]
    public void 配置读不出来时_不展示_也不覆盖原文件()
    {
        var config = Config("unreadable");
        using (UiPreferences.OverrideConfigPathForTests(config))
        {
            UiPreferences.SaveRepositoryRoot(@"D:\玩家的 MOD");
            UiPreferences.SaveOnboardingGuideShown(true);
        }

        var original = File.ReadAllBytes(config);

        using (Restart(config))
        {
            using (new FileStream(config, FileMode.Open, FileAccess.Read, FileShare.None))
            {
                Assert.False(UiPreferences.TrySavePromoDisplayRecord(Nov25, "gpt-topup-15off-2026"));
                Assert.Null(Service(Nov25).TryBeginDisplay());
            }

            Assert.Equal(original, File.ReadAllBytes(config));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(config)!, "*.bak"));

            // 锁放开之后一切照旧：设置还在，推广条也能正常展示
            Assert.Equal(@"D:\玩家的 MOD", UiPreferences.LoadRepositoryRoot());
            Assert.NotNull(Service(Nov25).TryBeginDisplay());
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void 没有活动标识的记录不落盘(string? campaignId)
    {
        var config = Config("blank_id");
        using var _ = UiPreferences.OverrideConfigPathForTests(config);

        Assert.False(UiPreferences.TrySavePromoDisplayRecord(Nov25, campaignId!));
        Assert.False(File.Exists(config));
    }

    [Fact]
    public void 手改坏的日期_当作从没展示过()
    {
        var config = Config("garbage_date");
        File.WriteAllText(config,
            "{ \"OnboardingGuideShown\": true, \"PromoLastShownDate\": \"昨天\", \"PromoLastShownCampaignId\": \"gpt-topup-15off-2026\" }");
        using var _ = UiPreferences.OverrideConfigPathForTests(config);

        Assert.Null(UiPreferences.LoadPromoDisplayRecord().LastShownDate);
        Assert.NotNull(Service(Nov25).TryBeginDisplay());
        Assert.Equal(Nov25, UiPreferences.LoadPromoDisplayRecord().LastShownDate);
    }

    // ─── 打开链接 ───

    [Fact]
    public void 领取_把所有者给的链接原样交给浏览器()
    {
        var result = Service(Nov25).Open(PromoCampaigns.GptTopUpCoupon);

        Assert.Equal(PromoOpenResult.Opened, result);
        Assert.Equal(new[] { OwnerLink }, _opened);
    }

    [Fact]
    public void 活动下线之后_不再打开链接()
    {
        // 软件从 12-31 一直开到元旦，推广条还留在界面上时被点了一下
        var result = Service(Jan01).Open(PromoCampaigns.GptTopUpCoupon);

        Assert.Equal(PromoOpenResult.Expired, result);
        Assert.Empty(_opened);
        Assert.True(Service(Jan01).IsExpired(PromoCampaigns.GptTopUpCoupon));
        Assert.False(Service(Dec31).IsExpired(PromoCampaigns.GptTopUpCoupon));
    }

    [Theory]
    [InlineData("http://gptpluscz.com/offer/x")]
    [InlineData("file:///C:/Windows/System32/calc.exe")]
    [InlineData("calc.exe")]
    [InlineData("ms-settings:privacy")]
    [InlineData("")]
    public void 不是https的链接_不交给系统(string url)
    {
        var tampered = PromoCampaigns.GptTopUpCoupon with { Url = url };

        var result = Service(Nov25, tampered).Open(tampered);

        Assert.Equal(PromoOpenResult.Blocked, result);
        Assert.Empty(_opened);
    }

    [Fact]
    public void 浏览器打不开时_报告失败而不是抛异常()
    {
        var service = Service(Nov25, open: _ => throw new System.ComponentModel.Win32Exception(1155));

        Assert.Equal(PromoOpenResult.Failed, service.Open(PromoCampaigns.GptTopUpCoupon));
    }

    [Fact]
    public void 空活动_直接报错而不是静默()
    {
        Assert.Throws<ArgumentNullException>(() => Service(Nov25).Open(null!));
        Assert.Throws<ArgumentNullException>(() => Service(Nov25).IsExpired(null!));
    }
}
