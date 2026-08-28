using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using UEModManager.Data;
using UEModManager.Infrastructure;
using UEModManager.Services.Migration;

namespace UEModManager.Services
{
    /// <summary>头像迁移的执行结果，供日志与将来的 UI 提示取用。</summary>
    public sealed record AvatarMigrationOutcome(
        int Scanned,
        int Migrated,
        int Cleared,
        int Skipped,
        int Failed)
    {
        /// <summary>本次是否真的动了数据。用来判断"幂等空转"与"确实迁移了"。</summary>
        public bool ChangedAnything => Migrated > 0 || Cleared > 0;
    }

    /// <summary>
    /// 把 <c>Users.Avatar</c> 里指向旧头像目录（安装目录下）的记录搬到当前头像目录。
    ///
    /// <para>
    /// <b>为什么需要单独一个改写器：</b>头像路径是<b>绝对路径</b>存在 SQLite 的
    /// <c>Users.Avatar</c> 列里，不在 <c>config.json</c> 里，<c>AppConfigPathRewriter</c>
    /// 够不着；而 <see cref="DataLocationMigrator"/> 跑在 <c>EnsureDatabaseCreatedAsync</c>
    /// <b>之前</b>（"任何服务读写数据之前完成"是它的硬要求），那个时点根本读不到 <c>Users</c> 表。
    /// 所以只能另起一个跑在建库之后的改写器。
    /// </para>
    ///
    /// <para>
    /// <b>两条不可协商的顺序约束：</b>
    /// 一是<b>先复制成功、再回写列</b>——反过来就会造出"数据库指向一个已删除文件"，
    /// 把"数据在不理想的位置"变成"头像立刻不显示"，严格更糟。
    /// 二是<b>不删源文件</b>——删了就没有回退余地，而收益仅是几十 KB。
    /// </para>
    ///
    /// <para>
    /// 整体不抛异常：迁移失败时沿用旧位置继续，绝不阻断启动。语义与
    /// <see cref="DataLocationMigrator"/> 一致。
    /// </para>
    /// </summary>
    public sealed class AvatarLocationMigrator
    {
        private readonly ILogger<AvatarLocationMigrator> _logger;

        public AvatarLocationMigrator(ILogger<AvatarLocationMigrator> logger)
        {
            _logger = logger;
        }

        /// <summary>上次执行结果，供调用方与将来的 UI 提示取用。</summary>
        public AvatarMigrationOutcome? LastOutcome { get; private set; }

        /// <summary>
        /// 执行迁移，目录取自 <see cref="AppPaths"/>。
        /// </summary>
        /// <param name="db">
        /// 由调用方传入而非注入：<c>LocalDbContext</c> 是 <c>AddDbContext</c> 注册的
        /// <b>Scoped</b> 服务，把它注入单例改写器就是 captive dependency；
        /// 而启动流程本来就已经解析了一个实例，直接传进来最省事也最安全。
        /// </param>
        public Task<AvatarMigrationOutcome> RunAsync(LocalDbContext db, CancellationToken ct = default)
            => RunAsync(db, AppPaths.Legacy.AvatarsDirectory, AppPaths.AvatarsDirectory, ct);

        /// <summary>
        /// 执行迁移，显式指定新旧目录。
        ///
        /// <para>
        /// 生产代码走上面的无目录重载。这个重载存在是为了可测：<see cref="AppPaths"/> 是读真实
        /// 环境目录的静态属性，测试若走它就会往用户真实的 <c>%APPDATA%</c> 里写文件。
        /// </para>
        /// </summary>
        public async Task<AvatarMigrationOutcome> RunAsync(
            LocalDbContext db, string legacyRoot, string currentRoot, CancellationToken ct = default)
        {
            int scanned = 0, migrated = 0, cleared = 0, skipped = 0, failed = 0;

            try
            {
                // 遍历全部用户而不是只处理当前登录者：否则没登录过的账号永远迁不了。
                var users = await db.Users.ToListAsync(ct);
                scanned = users.Count;

                foreach (var user in users)
                {
                    ct.ThrowIfCancellationRequested();

                    switch (AvatarPathClassifier.Classify(user.Avatar, legacyRoot, currentRoot))
                    {
                        case AvatarLocation.Empty:
                        case AvatarLocation.AlreadyCurrent:
                            skipped++;
                            break;

                        case AvatarLocation.Foreign:
                            // 用户自选的位置、云端 URL、损坏字符串——一律不动。
                            // 尤其不能因为"文件当前不存在"就清空：U 盘/网络盘只是暂时不可访问。
                            _logger.LogDebug("[AvatarMigration] 跳过非托管路径: {Path}", user.Avatar);
                            skipped++;
                            break;

                        case AvatarLocation.Legacy:
                            var source = user.Avatar!;
                            if (!File.Exists(source))
                            {
                                // 确认属于我们管理的旧目录、且文件确实没了 → 头像已不存在，
                                // 置空让 UI 回退默认图标，不留悬空指针。
                                user.Avatar = null;
                                cleared++;
                                _logger.LogInformation("[AvatarMigration] 旧头像已丢失，置空: {Path}", source);
                                break;
                            }

                            var moved = TryRelocate(source, currentRoot);
                            if (moved is null)
                            {
                                // 复制失败 → 列保持原值不动，下次启动再试。绝不写成半吊子。
                                failed++;
                            }
                            else
                            {
                                user.Avatar = moved;
                                migrated++;
                                _logger.LogInformation("[AvatarMigration] 已迁移: {From} -> {To}", source, moved);
                            }
                            break;
                    }
                }

                if (migrated > 0 || cleared > 0)
                    await db.SaveChangesAsync(ct);
            }
            catch (Exception ex)
            {
                // 整体失败也不抛给启动流程。已经落盘的部分保持有效（复制在前、回写在后）。
                _logger.LogError(ex, "[AvatarMigration] 迁移过程异常终止");
            }

            var outcome = new AvatarMigrationOutcome(scanned, migrated, cleared, skipped, failed);
            LastOutcome = outcome;
            _logger.LogInformation(
                "[AvatarMigration] 扫描 {Scanned}，迁移 {Migrated}，置空 {Cleared}，跳过 {Skipped}，失败 {Failed}",
                scanned, migrated, cleared, skipped, failed);
            return outcome;
        }

        /// <summary>
        /// 把一个文件搬到目标目录，返回新的绝对路径；失败返回 null（调用方据此保留原值）。
        ///
        /// <para>
        /// 先写同目录下的临时文件、再 <see cref="File.Move(string,string)"/> 原子改名。
        /// 直接 <c>File.Copy</c> 到最终名的话，进程中断会留下半个文件，
        /// 而下次启动看到"目标已存在"就会误判为已完成。
        /// </para>
        ///
        /// <para>
        /// 目标同名时改用 <c>{原名}_{n}{扩展名}</c> 而不是覆盖：撞名需要同一用户在同一秒内
        /// 换过两次头像，概率极低；多出一个副本远比覆盖掉别人的文件安全。
        /// </para>
        /// </summary>
        private string? TryRelocate(string source, string targetDirectory)
        {
            string? temp = null;
            try
            {
                Directory.CreateDirectory(targetDirectory);

                var destination = ResolveFreeName(targetDirectory, Path.GetFileName(source));
                temp = destination + ".tmp";

                // 临时文件可能是上次中断留下的，覆盖它是安全的（它还没被任何人引用）
                File.Copy(source, temp, overwrite: true);
                File.Move(temp, destination);
                temp = null;

                return destination;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AvatarMigration] 搬移失败，保留原路径: {Source}", source);
                return null;
            }
            finally
            {
                if (temp is not null)
                {
                    try { File.Delete(temp); } catch { /* 清理失败无所谓，下次会覆盖 */ }
                }
            }
        }

        /// <summary>在目标目录里找一个还没被占用的文件名。</summary>
        private static string ResolveFreeName(string directory, string fileName)
        {
            var candidate = Path.Combine(directory, fileName);
            if (!File.Exists(candidate)) return candidate;

            var stem = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            for (var n = 2; n < int.MaxValue; n++)
            {
                candidate = Path.Combine(directory, $"{stem}_{n}{ext}");
                if (!File.Exists(candidate)) return candidate;
            }

            throw new IOException($"无法在 {directory} 下找到可用的文件名");
        }
    }
}
