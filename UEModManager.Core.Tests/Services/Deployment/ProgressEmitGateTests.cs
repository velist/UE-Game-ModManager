using UEModManager.Services.Deployment;

namespace UEModManager.Core.Tests.Services.Deployment;

public class ProgressEmitGateTests
{
    private static readonly DateTime T0 = new(2026, 7, 27, 10, 0, 0);

    private static ProgressEmitGate Gate(int total, int intervalMs = 100)
        => new(total, T0, TimeSpan.FromMilliseconds(intervalMs));

    // ─── 最后一次必发 ───

    [Fact]
    public void ShouldEmit_FinalOperation_AlwaysEmits()
    {
        // 否则进度条永远停在 99%
        var gate = Gate(1000);

        // 时间与进度都远未达标
        Assert.True(gate.ShouldEmit(1000, T0));
    }

    [Fact]
    public void ShouldEmit_BeyondTotal_StillEmits()
        => Assert.True(Gate(10).ShouldEmit(11, T0));

    [Fact]
    public void ShouldEmit_ZeroTotal_EmitsImmediately()
        => Assert.True(Gate(0).ShouldEmit(0, T0));

    // ─── 时间闸门 ───

    [Fact]
    public void ShouldEmit_BeforeInterval_Suppresses()
    {
        var gate = Gate(10_000);

        Assert.False(gate.ShouldEmit(1, T0.AddMilliseconds(50)));
    }

    [Fact]
    public void ShouldEmit_AfterInterval_Emits()
    {
        var gate = Gate(10_000);

        Assert.True(gate.ShouldEmit(1, T0.AddMilliseconds(100)));
    }

    [Fact]
    public void ShouldEmit_IntervalMeasuredFromLastEmit_NotFromStart()
    {
        var gate = Gate(10_000);
        Assert.True(gate.ShouldEmit(1, T0.AddMilliseconds(100)));

        // 距上次发送只过了 50ms
        Assert.False(gate.ShouldEmit(2, T0.AddMilliseconds(150)));
        Assert.True(gate.ShouldEmit(3, T0.AddMilliseconds(200)));
    }

    // ─── 进度闸门 ───

    [Fact]
    public void ShouldEmit_OnePercentProgressed_EmitsEvenWithoutTime()
    {
        // 单个操作极快时，靠 1% 步长保证进度条平滑
        var gate = Gate(1000); // stride = 10

        Assert.False(gate.ShouldEmit(9, T0));
        Assert.True(gate.ShouldEmit(10, T0));
    }

    [Fact]
    public void ShouldEmit_ProgressMeasuredFromLastEmit()
    {
        var gate = Gate(1000); // stride = 10
        Assert.True(gate.ShouldEmit(10, T0));

        Assert.False(gate.ShouldEmit(19, T0));
        Assert.True(gate.ShouldEmit(20, T0));
    }

    [Theory]
    [InlineData(1000, 10)]
    [InlineData(100, 1)]
    [InlineData(50, 1)]   // 少于 100 个操作时退化为每个都发
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    public void Stride_IsOnePercentWithFloorOfOne(int total, int expectedStride)
        => Assert.Equal(expectedStride, Gate(total).Stride);

    // ─── 时间或进度：任一达标即发 ───

    [Fact]
    public void ShouldEmit_TimeAloneIsEnough()
    {
        // 拷贝一个 5GB 的 pak：进度只走了 1 个操作，但界面不能像卡死
        var gate = Gate(10_000); // stride = 100

        Assert.True(gate.ShouldEmit(1, T0.AddSeconds(3)));
    }

    [Fact]
    public void ShouldEmit_ProgressAloneIsEnough()
    {
        // 上万个小文件瞬间完成：时间没到但进度已经走了很多
        var gate = Gate(10_000); // stride = 100

        Assert.True(gate.ShouldEmit(100, T0));
    }

    // ─── 节流效果 ───

    [Fact]
    public void ShouldEmit_LargeDeployment_EmitsFarFewerThanOperations()
    {
        const int total = 10_000;
        var gate = Gate(total);
        var emitted = 0;

        // 模拟 2 秒内完成 10000 个操作
        for (var i = 1; i <= total; i++)
        {
            var now = T0.AddMilliseconds(2000.0 * i / total);
            if (gate.ShouldEmit(i, now)) emitted++;
        }

        // 时间闸门约 20 次 + 进度闸门约 100 次，远小于 10000
        Assert.InRange(emitted, 1, 250);
        // 且最后一次一定发了
        Assert.True(gate.ShouldEmit(total, T0.AddSeconds(2)));
    }

    [Fact]
    public void ShouldEmit_SmallDeployment_EmitsEveryOperation()
    {
        // 操作很少时不该节流掉任何一次——本来就没有性能问题，节流只会让进度显示变粗糙
        const int total = 20;
        var gate = Gate(total);
        var emitted = 0;

        for (var i = 1; i <= total; i++)
        {
            if (gate.ShouldEmit(i, T0)) emitted++;
        }

        Assert.Equal(total, emitted);
    }

    [Fact]
    public void ShouldEmit_SuppressedCallDoesNotAdvanceBaseline()
    {
        // 被抑制的调用不能记账，否则会把下一次的判断基准往前推，造成漏发
        var gate = Gate(1000); // stride = 10

        Assert.False(gate.ShouldEmit(5, T0));
        Assert.False(gate.ShouldEmit(9, T0));
        Assert.True(gate.ShouldEmit(10, T0)); // 仍以 0 为基准，累计到 10 才发
    }
}
