using UEModManager.Services.Paths;

namespace UEModManager.Core.Tests.Services.Paths;

/// <summary>
/// 搬迁前磁盘空间预检的判定逻辑。
///
/// <para>
/// 这一层刻意只吃两个数字（要多少 / 有多少），把 <c>DriveInfo</c> 与目录遍历留在主项目：
/// D4（目标盘空间不足）真要靠真实的满盘来复现，得挂 VHD 或找小容量卷，
/// 跨机器不可复现、要额外权限、跑完还留残留。判定下沉成纯函数之后，
/// 余量取舍这类最容易改错的东西反而是全项目最好测的部分。
/// </para>
/// </summary>
public class DiskSpacePrecheckTests
{
    private const long Mib = 1024L * 1024;
    private const long Gib = 1024L * Mib;

    // ─── 余量：判据必须留余量，"正好等于"不算够 ───

    [Fact]
    public void ExactlyEqualToRequiredBytes_IsInsufficient()
    {
        // 这条是本类存在的理由。剩余空间正好等于源体积时复制仍会失败：
        // 簇对齐、目录项、NTFS 元数据都要地方，更别说系统同时还在写自己的日志。
        var check = DiskSpacePrecheck.Evaluate(requiredBytes: 4 * Gib, availableBytes: 4 * Gib);

        Assert.Equal(DiskSpaceDecision.Insufficient, check.Decision);
        Assert.True(check.IsBlocking);
    }

    [Fact]
    public void LargeSource_RequiresTenPercentHeadroom()
    {
        // 大数据走比例余量：10 GiB 要求 11 GiB
        Assert.Equal(11 * Gib, DiskSpacePrecheck.RequiredWithHeadroom(10 * Gib));
    }

    [Fact]
    public void SmallSource_RequiresFixedFloorInsteadOfTenPercent()
    {
        // 小数据走固定下限：主配置只有几 KB，1.1 倍多留几百字节毫无意义，
        // 盘上剩 500 KB 时一个簇 + 一条 MFT 记录就能让复制失败。
        const long tiny = 4 * 1024;

        Assert.Equal(tiny + DiskSpacePrecheck.MinimumHeadroomBytes,
            DiskSpacePrecheck.RequiredWithHeadroom(tiny));
    }

    [Fact]
    public void HeadroomAlwaysTakesTheStricterOfTheTwo()
    {
        // 比例余量恰好等于固定下限的量级附近，两条规则不许互相削弱
        const long crossover = 640 * Mib;   // 1/10 == 64 MiB == MinimumHeadroomBytes

        Assert.Equal(crossover + DiskSpacePrecheck.MinimumHeadroomBytes,
            DiskSpacePrecheck.RequiredWithHeadroom(crossover));
        Assert.Equal(2 * Gib + 2 * Gib / 10,
            DiskSpacePrecheck.RequiredWithHeadroom(2 * Gib));
    }

    [Fact]
    public void JustAboveHeadroom_IsSufficient()
    {
        var required = 100 * Mib;
        var check = DiskSpacePrecheck.Evaluate(
            required, DiskSpacePrecheck.RequiredWithHeadroom(required));

        Assert.Equal(DiskSpaceDecision.Sufficient, check.Decision);
        Assert.False(check.IsBlocking);
    }

    [Fact]
    public void JustBelowHeadroom_IsInsufficient()
    {
        var required = 100 * Mib;
        var check = DiskSpacePrecheck.Evaluate(
            required, DiskSpacePrecheck.RequiredWithHeadroom(required) - 1);

        Assert.Equal(DiskSpaceDecision.Insufficient, check.Decision);
    }

    [Fact]
    public void ZeroSource_NeedsNothing()
    {
        // 空目录/不存在的源：没东西要复制，盘上一个字节都没有也放行
        Assert.Equal(0, DiskSpacePrecheck.RequiredWithHeadroom(0));

        var check = DiskSpacePrecheck.Evaluate(requiredBytes: 0, availableBytes: 0);
        Assert.Equal(DiskSpaceDecision.Sufficient, check.Decision);
    }

    [Fact]
    public void HeadroomSaturatesInsteadOfOverflowing()
    {
        // 源体积不可能接近 long.MaxValue，但溢出会把"要求"翻成负数，于是
        // "可用 0 字节" 也 >= 要求，"不足"被判成"充足"——那是一旦发生就查不出来的方向。
        // 饱和到 MaxValue 只会让判定更严，绝不会更松。
        Assert.Equal(long.MaxValue, DiskSpacePrecheck.RequiredWithHeadroom(long.MaxValue));
        Assert.Equal(DiskSpaceDecision.Insufficient,
            DiskSpacePrecheck.Evaluate(long.MaxValue, availableBytes: 0).Decision);
        Assert.Equal(DiskSpaceDecision.Insufficient,
            DiskSpacePrecheck.Evaluate(long.MaxValue / 2, availableBytes: long.MaxValue / 2).Decision);
    }

    [Fact]
    public void NegativeInputs_Throw()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskSpacePrecheck.RequiredWithHeadroom(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskSpacePrecheck.Evaluate(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => DiskSpacePrecheck.Humanize(-1));
    }

    // ─── 查不到可用空间时放行 ───

    [Fact]
    public void UnknownAvailableSpace_DoesNotBlock()
    {
        // UNC / 映射盘 / 未就绪卷查不出可用空间。"查不到就拒绝"会让这些用户永远搬不了，
        // 而他们的盘八成是够的——等于用一次查询失败换来一个永久性的功能缺失。
        // 预检的职责是拦住注定失败的复制，不是给搬迁加一道新的准入闸门。
        var check = DiskSpacePrecheck.Evaluate(requiredBytes: 100 * Gib, availableBytes: null);

        Assert.Equal(DiskSpaceDecision.Unknown, check.Decision);
        Assert.False(check.IsBlocking);
    }

    [Fact]
    public void OnlyInsufficientBlocks()
    {
        // 新增结论时这条会先红：漏掉的分支会默认按"不拦"处理，
        // 那是安全的方向，但必须是有人明确决定过的。
        foreach (DiskSpaceDecision decision in Enum.GetValues<DiskSpaceDecision>())
        {
            var check = new DiskSpaceCheck(decision, 1, 2, 3);
            Assert.Equal(decision == DiskSpaceDecision.Insufficient, check.IsBlocking);
        }
    }

    // ─── 结论要能自证 ───

    [Fact]
    public void InsufficientDescription_CarriesBothNumbers()
    {
        // 空间不足这条日志的唯一读者是"要判断该腾多少地方"的人。
        // 只说"空间不足"等于让他去猜。
        var text = DiskSpacePrecheck.Evaluate(2 * Gib, 1 * Gib).ToString();

        Assert.Contains("2.2 GiB", text);   // 含余量后真正的要求
        Assert.Contains("2 GiB", text);     // 数据本身
        Assert.Contains("1 GiB", text);     // 实际可用
    }

    [Fact]
    public void EveryDecision_HasNonEmptyDescription()
    {
        foreach (DiskSpaceDecision decision in Enum.GetValues<DiskSpaceDecision>())
        {
            var text = new DiskSpaceCheck(decision, Gib, 2 * Gib, Gib).ToString();
            Assert.False(string.IsNullOrWhiteSpace(text));
        }
    }

    // ─── 量级 ───

    [Theory]
    [InlineData(0, "0 B")]
    [InlineData(512, "512 B")]
    [InlineData(1024, "1 KiB")]
    [InlineData(1536, "1.5 KiB")]
    [InlineData(1024L * 1024, "1 MiB")]
    [InlineData(1024L * 1024 * 1024, "1 GiB")]
    [InlineData(1024L * 1024 * 1024 * 1024, "1 TiB")]
    public void Humanize_UsesBinaryUnits(long bytes, string expected)
        => Assert.Equal(expected, DiskSpacePrecheck.Humanize(bytes));

    [Fact]
    public void Humanize_KeepsOneDecimalSoNearlyDoubleIsNotRoundedAway()
    {
        // 取整会把 1.9 GiB 报成 1 GiB，差出快一倍——用户照着这个数字腾空间会白忙一场
        Assert.Equal("1.9 GiB", DiskSpacePrecheck.Humanize((long)(1.9 * Gib)));
    }

    [Fact]
    public void Humanize_HandlesExtremes()
    {
        // 不许在最大值上抛，也不许滚出单位表
        Assert.Contains("PiB", DiskSpacePrecheck.Humanize(long.MaxValue));
    }
}
