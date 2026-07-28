using System;
using System.Globalization;

namespace UEModManager.Services.Paths;

/// <summary>空间预检的结论。</summary>
public enum DiskSpaceDecision
{
    /// <summary>够用，照常复制。</summary>
    Sufficient,

    /// <summary>
    /// 不够用。该项<b>不要开始复制</b>——不是为了省一次失败，而是为了不在目标盘上
    /// 留下半份数据：复制到一半撞满盘，目标里躺着一堆残留 + 一张进行中标记，
    /// 而磁盘已经满得连墓碑都写不下，用户还得自己去清。预检拦下来则一个字节都没写。
    /// </summary>
    Insufficient,

    /// <summary>
    /// 查不出目标卷的可用空间（UNC / 映射盘 / 卷未就绪 / 权限）。
    /// <b>按"够用"放行</b>，理由见 <see cref="DiskSpacePrecheck.Evaluate"/>。
    /// </summary>
    Unknown,
}

/// <summary>
/// 一次空间预检的结论与它的全部输入。
///
/// <para>
/// 把四个数字一起带出来，是为了让日志一行就能自证："需要 X（含余量 Y），可用 Z"。
/// 只回一个枚举的话，排障时永远要回头猜是余量算多了还是源体积估大了。
/// </para>
/// </summary>
/// <param name="Decision">结论。</param>
/// <param name="RequiredBytes">源数据的净体积。</param>
/// <param name="RequiredWithHeadroomBytes">含安全余量后真正要求的空闲字节数。</param>
/// <param name="AvailableBytes">目标卷可用字节数；查不到时为 <c>null</c>。</param>
public readonly record struct DiskSpaceCheck(
    DiskSpaceDecision Decision,
    long RequiredBytes,
    long RequiredWithHeadroomBytes,
    long? AvailableBytes)
{
    /// <summary>是否应当据此拒绝复制。只有 <see cref="DiskSpaceDecision.Insufficient"/> 拦人。</summary>
    public bool IsBlocking => Decision == DiskSpaceDecision.Insufficient;

    /// <summary>供日志与用户提示使用的一句话说明。</summary>
    public override string ToString()
        => Decision switch
        {
            DiskSpaceDecision.Unknown =>
                $"目标卷可用空间未知，按够用处理（需要 {DiskSpacePrecheck.Humanize(RequiredWithHeadroomBytes)}）",
            DiskSpaceDecision.Insufficient =>
                $"目标盘剩余空间不足：需要 {DiskSpacePrecheck.Humanize(RequiredWithHeadroomBytes)}" +
                $"（数据 {DiskSpacePrecheck.Humanize(RequiredBytes)} + 安全余量），" +
                $"实际可用 {DiskSpacePrecheck.Humanize(AvailableBytes ?? 0)}",
            _ =>
                $"空间充足：需要 {DiskSpacePrecheck.Humanize(RequiredWithHeadroomBytes)}，" +
                $"可用 {DiskSpacePrecheck.Humanize(AvailableBytes ?? 0)}",
        };
}

/// <summary>
/// 搬迁前的磁盘空间预检。纯函数：输入"要复制多少字节"与"目标卷还剩多少字节"，输出该不该开始复制。
///
/// <para>
/// <b>为什么要预检，而不是让复制自己撞墙。</b>撞墙的后果不是"这一项失败"这么干净：
/// 目标盘满时 <c>File.Copy</c> 抛在半路，目标位置留着一批已复制的文件和一张进行中标记，
/// 而墓碑写不下（磁盘已满）。下次启动会走 <see cref="RelocationAction.PurgeTargetThenCopy"/>
/// ——清空残留、再撞一次墙。数据一直在旧位置不会丢，但用户的目标盘被反复填满又清空，
/// 每次启动都白折腾几分钟，界面上只有一句"迁移未完成"。预检把这件事变成
/// "一个字节都没写 + 日志里写清差多少"。
/// </para>
///
/// <para>
/// <b>空间不足时的处置是"跳过该项"，不是方案 §三② 写的"降级为原地登记"。</b>
/// 原地登记的语义是"不搬，把当前位置写进配置让应用继续从原处读"，它成立需要两件事：
/// 有一个配置键能承载这项数据的位置，以及有读取方真的去读那个键。这对包仓库与生成物成立
/// （<c>RepositoryRoot</c> / <c>OverwritesRoot</c> 存在且 <c>ObjectStore</c> 读它们），
/// 所以那两项本来就是 <see cref="RelocationKind.RegisterInPlace"/>、根本不复制；
/// 但对真正要复制的四项（主配置 / 数据索引 / MOD 备份 / 部署事务备份）<b>都不成立</b>：
/// 路径归口之后它们的十几个读取方一律走写死的 <c>AppPaths</c> 新位置，没有任何配置键指向旧位置，
/// 登记了也没人读。要让"降级"真的生效，得先给这四项各造一个配置键再改一遍全部读取方——
/// 那是把整套路径体系重做成可配置化，远超一次空间预检该做的事。
/// 更要紧的是<b>语义倒退</b>：把安装目录登记成权威位置，正是本次迁移要根治的病根
/// （卸载/升级会清空安装目录）。为了"这次盘不够"把用户数据永久钉死在最危险的位置，
/// 代价远大于收益。方案写那一段时仓库还在搬移类里，几十 GB 确实需要一条降级出口；
/// 仓库改成原地登记之后，那条出口没有落点了。
/// </para>
///
/// <para>
/// 于是跳过项<b>计入失败</b>（而不是新增一种状态）：这恰好给出想要的三件事——
/// 不打版本标记，所以用户腾出空间后下次启动自动重试；
/// <see cref="DataMigrationStatus"/> 走失败类，所以提示对用户可见；
/// 而"具体差多少字节"由日志承担（<see cref="DiskSpaceCheck.ToString"/>）。
/// 与真正撞墙的失败的区别写在 <see cref="DiskSpaceDecision.Insufficient"/> 上：
/// 目标位置一个字节都没被写过。
/// </para>
/// </summary>
public static class DiskSpacePrecheck
{
    /// <summary>
    /// 按比例留的安全余量（源体积的 1/10，即方案 §三② 的 ×1.1）。
    /// 覆盖的是簇对齐（一个 4 KiB 簇装一个 100 字节的 JSON）、目录项、NTFS 元数据这些
    /// "复制过去会变大"的开销，源越大这部分绝对值越大，所以按比例而不是按固定值。
    /// </summary>
    public const int SafetyMarginDivisor = 10;

    /// <summary>
    /// 比例余量之外还要留的固定下限。
    ///
    /// <para>
    /// 光有 ×1.1 是不够的：主配置只有几 KB，1.1 倍多留 400 字节，
    /// 而盘上剩 500 KB 时一个 NTFS 簇、一条 MFT 记录、一次日志刷写就能让复制失败——
    /// "磁盘剩余空间正好等于所需字节数"这种判据本身就是错的。
    /// 取 64 MiB 是因为目标是 <c>%LOCALAPPDATA%</c>，也就是系统盘：
    /// 系统盘可用空间掉到百 MB 量级时，页面文件与系统日志本身就在挣扎，
    /// 此刻把最后一点空间用来搬 MOD 备份，换来的是整机不稳而不是"迁移成功"。
    /// 它只是下限，不负责充分性——充分性由上面那个比例余量负责。
    /// </para>
    /// </summary>
    public const long MinimumHeadroomBytes = 64L * 1024 * 1024;

    /// <summary>
    /// 复制 <paramref name="requiredBytes"/> 字节实际要求目标卷有多少空闲字节。
    /// 取"比例余量"与"固定下限"里更严的那个。
    /// </summary>
    public static long RequiredWithHeadroom(long requiredBytes)
    {
        if (requiredBytes < 0) throw new ArgumentOutOfRangeException(nameof(requiredBytes));
        if (requiredBytes == 0) return 0;

        // 源体积不可能接近 long.MaxValue，但这里溢出会翻成负数、把"不足"判成"充足"，
        // 是那种一旦发生就查不出来的方向，所以饱和处理。
        var proportional = AddSaturating(requiredBytes, requiredBytes / SafetyMarginDivisor);
        var floor = AddSaturating(requiredBytes, MinimumHeadroomBytes);
        return Math.Max(proportional, floor);
    }

    /// <summary>
    /// 判定。
    /// </summary>
    /// <param name="requiredBytes">源数据净体积。0 表示没东西要复制，一律放行。</param>
    /// <param name="availableBytes">
    /// 目标卷可用字节数。<c>null</c> = 查不出来，结论是
    /// <see cref="DiskSpaceDecision.Unknown"/> 并<b>放行</b>。
    ///
    /// <para>
    /// 放行是刻意的：预检的职责是"把注定失败的复制提前拦住"，不是"给搬迁加一道新的准入闸门"。
    /// 查不到就退回预检存在之前的行为——复制照跑，失败照失败，数据仍完整留在旧位置。
    /// 反过来"查不到就拒绝"会让所有把数据目录指到 UNC / 映射盘 / 未就绪卷上的用户永远搬不了，
    /// 而这些人的盘八成是够的，等于用一个查询失败换来一个永久性的功能缺失。
    /// </para>
    /// </param>
    public static DiskSpaceCheck Evaluate(long requiredBytes, long? availableBytes)
    {
        if (requiredBytes < 0) throw new ArgumentOutOfRangeException(nameof(requiredBytes));

        var withHeadroom = RequiredWithHeadroom(requiredBytes);

        if (requiredBytes == 0)
        {
            return new DiskSpaceCheck(
                DiskSpaceDecision.Sufficient, requiredBytes, withHeadroom, availableBytes);
        }

        if (availableBytes is null)
        {
            return new DiskSpaceCheck(
                DiskSpaceDecision.Unknown, requiredBytes, withHeadroom, null);
        }

        var decision = availableBytes.Value >= withHeadroom
            ? DiskSpaceDecision.Sufficient
            : DiskSpaceDecision.Insufficient;

        return new DiskSpaceCheck(decision, requiredBytes, withHeadroom, availableBytes);
    }

    /// <summary>
    /// 字节数转成人能读的量级。日志里写"需要 3221225472 字节"对排障几乎没有帮助，
    /// 而空间不足这条日志的唯一读者就是"要判断该腾多少地方"的人。
    /// </summary>
    public static string Humanize(long bytes)
    {
        if (bytes < 0) throw new ArgumentOutOfRangeException(nameof(bytes));
        if (bytes < 1024) return bytes + " B";

        string[] units = { "KiB", "MiB", "GiB", "TiB", "PiB" };
        double value = bytes;
        var unit = -1;
        do
        {
            value /= 1024;
            unit++;
        }
        while (value >= 1024 && unit < units.Length - 1);

        // 保留一位小数：整数会把 1.9 GiB 报成 1 GiB，差出快一倍。
        return value.ToString("0.#", CultureInfo.InvariantCulture) + " " + units[unit];
    }

    private static long AddSaturating(long a, long b)
        => long.MaxValue - a < b ? long.MaxValue : a + b;
}
