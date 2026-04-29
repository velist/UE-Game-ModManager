using UEModManager.Health;

namespace UEModManager.Core.Tests.Health;

public class HealthReportTests
{
    // ─── OverallStatus 聚合 ───

    [Fact]
    public void Overall_NoChecks_ReturnsOk()
    {
        var r = new HealthReport();
        Assert.Equal(HealthStatus.Ok, r.OverallStatus);
    }

    [Fact]
    public void Overall_AllOk_ReturnsOk()
    {
        var r = new HealthReport
        {
            Checks =
            [
                new("a", HealthStatus.Ok, "fine"),
                new("b", HealthStatus.Ok, "fine"),
            ]
        };
        Assert.Equal(HealthStatus.Ok, r.OverallStatus);
    }

    [Fact]
    public void Overall_OneWarningRest_Ok_ReturnsWarning()
    {
        var r = new HealthReport
        {
            Checks =
            [
                new("a", HealthStatus.Ok, "fine"),
                new("b", HealthStatus.Warning, "uhh"),
            ]
        };
        Assert.Equal(HealthStatus.Warning, r.OverallStatus);
    }

    [Fact]
    public void Overall_OneError_OverridesAllWarnings()
    {
        var r = new HealthReport
        {
            Checks =
            [
                new("a", HealthStatus.Warning, "uhh"),
                new("b", HealthStatus.Error, "broken"),
                new("c", HealthStatus.Warning, "still uhh"),
            ]
        };
        Assert.Equal(HealthStatus.Error, r.OverallStatus);
    }

    [Fact]
    public void Counts_AreAccurate()
    {
        var r = new HealthReport
        {
            Checks =
            [
                new("a", HealthStatus.Ok, ""),
                new("b", HealthStatus.Ok, ""),
                new("c", HealthStatus.Warning, ""),
                new("d", HealthStatus.Error, ""),
                new("e", HealthStatus.Error, ""),
            ]
        };

        Assert.Equal(2, r.OkCount);
        Assert.Equal(1, r.WarningCount);
        Assert.Equal(2, r.ErrorCount);
    }

    // ─── 文本渲染 ───

    [Fact]
    public void ToText_IncludesHeaderAndAllChecks()
    {
        var r = new HealthReport
        {
            CheckedAt = new DateTime(2026, 4, 28, 10, 0, 0, DateTimeKind.Utc),
            Checks =
            [
                new("CurrentGamePath", HealthStatus.Ok, "OK", "C:/Games/Demo"),
                new("ModPath", HealthStatus.Warning, "未配置 MOD 路径"),
                new("ProfileService", HealthStatus.Error, "无法加载", "FileNotFound"),
            ]
        };

        var text = r.ToText();

        Assert.Contains("Health Report", text);
        Assert.Contains("Overall: Error", text);
        Assert.Contains("Ok=1, Warn=1, Err=1", text);
        Assert.Contains("[OK] CurrentGamePath", text);
        Assert.Contains("[WARN] ModPath", text);
        Assert.Contains("[ERR] ProfileService", text);
        Assert.Contains("FileNotFound", text);
    }

    [Fact]
    public void ToText_NoDetail_OmitsDetailLine()
    {
        var r = new HealthReport
        {
            Checks = [new("X", HealthStatus.Ok, "fine")]
        };

        var text = r.ToText();
        Assert.DoesNotContain("└", text);
    }

    [Fact]
    public void ToText_WithDetail_RendersDetailLine()
    {
        var r = new HealthReport
        {
            Checks = [new("X", HealthStatus.Ok, "fine", "extra context")]
        };

        var text = r.ToText();
        Assert.Contains("└ extra context", text);
    }

    [Fact]
    public void Format_NullReport_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => HealthReportFormatter.Format(null!));
    }
}
