using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Services.Security;

namespace UEModManager.Services
{
    /// <summary>
    /// 对象仓库。
    /// 基于内容哈希的文件存储，实现去重。
    /// 仓库结构：
    ///   {RepositoryRoot}/
    ///     {packageKey}/
    ///       manifest.json
    ///       files/
    ///         {fileName}
    ///       preview.*
    /// </summary>
    public class ObjectStore : IObjectStoreQuery
    {
        private readonly ILogger<ObjectStore> _logger;
        private string _repositoryRoot;

        /// <summary>
        /// 仓库正在被整体搬到别的位置。非 0 即"上了闸"。
        ///
        /// <para>
        /// 用 <see cref="Interlocked"/> 的 0/1 而不是 <c>bool</c> 或 <c>lock</c>：
        /// 上闸这件事必须是"要么我拿到、要么我知道别人拿着"的原子操作
        /// ——两个搬移同时开始，两边各自往同一个目标复制，谁的墓碑先落地都是灾难。
        /// 而闸门的读取（<see cref="ThrowIfRelocating"/>）发生在每一次写入之前，
        /// 用不着比一次 volatile 读更重的东西。
        /// </para>
        /// </summary>
        private int _relocating;

        public ObjectStore(ILogger<ObjectStore> logger)
            : this(logger, Infrastructure.AppPaths.RepositoryRoot)
        {
        }

        /// <summary>
        /// 指定仓库根的构造函数（测试用）。DI 走上面的单参数构造函数——
        /// 容器无法解析 string，不会误选此重载。
        ///
        /// <para>
        /// 不能用 <see cref="SetRepositoryRoot"/> 代替：那个方法会把路径写进
        /// <see cref="UiPreferences"/>，跑一次测试就会改掉开发者真实的仓库位置偏好。
        /// </para>
        /// </summary>
        public ObjectStore(ILogger<ObjectStore> logger, string repositoryRoot)
        {
            _logger = logger;
            _repositoryRoot = repositoryRoot;
        }

        /// <summary>仓库根目录。</summary>
        public string RepositoryRoot => _repositoryRoot;

        /// <summary>仓库此刻是否正在被整体搬移。</summary>
        public bool IsRelocating => Volatile.Read(ref _relocating) != 0;

        /// <summary>
        /// 给仓库上闸，返回的对象 Dispose 时开闸。已经上着闸时抛
        /// <see cref="InvalidOperationException"/>。
        ///
        /// <para><b>闸门挡的是写入，不挡读取。</b>这不是偷懒，是搬移的落地顺序决定的：</para>
        /// <list type="bullet">
        /// <item><b>写入必须挡。</b>复制是对某一时刻的快照做的，快照之后新导入的包不会被复制过去，
        /// 而搬移最后一步会把旧位置清空——那个包就这么没了，而且用户刚刚才看到"导入成功"。</item>
        /// <item><b>读取不用挡。</b>旧位置的数据在墓碑落地<b>之前</b>一个字节都不会少，
        /// 而墓碑落地之后紧接着就把存放位置改成了新位置，此后新的读取自然走新位置。
        /// 也就是说任何一次读取要么读到完整的旧位置、要么读到完整的新位置。
        /// 挡读取反而有害：界面刷新、统计仓库大小、切游戏都是读，
        /// 一律抛异常会把一个正常的搬移变成满屏错误框。</item>
        /// </list>
        ///
        /// <para>
        /// 闸门是<b>第二道</b>防线。第一道是搬移界面本身是模态对话框——项目里所有会写仓库的
        /// 动作（导入、部署、删包、回收、整合包导入）都由主窗口上的用户操作发起，
        /// 一个 Owner 是主窗口的模态框把它们全挡住了，没有任何定时器或后台任务会写仓库。
        /// 留着闸门是因为"没有后台写入方"这件事是靠人读代码保证的，而这类保证在这个项目里
        /// 已经失效过三次；闸门一旦被撞上，用户看到的是一句明确的"正在搬移，请稍等"，
        /// 而不是一个包被静默删掉。
        /// </para>
        /// </summary>
        public IDisposable BeginRelocation()
        {
            if (Interlocked.CompareExchange(ref _relocating, 1, 0) != 0)
            {
                throw new InvalidOperationException("已经有一次 MOD 存放位置搬移在进行中");
            }

            _logger.LogInformation("仓库已上闸（搬移进行中），期间拒绝一切写入: {Root}", _repositoryRoot);
            return new RelocationGate(this);
        }

        /// <summary>
        /// 写入前的闸门检查。<b>公开</b>是刻意的：仓库根下面还有几个不经过本类写方法的写入方
        /// （<c>PackageRepository</c> 写 manifest、<c>PackageImportService</c> 的解压临时目录、
        /// <c>RepositoryReclaimService</c> 的残留回收、<c>ProfileLockService</c> 的整合包导入），
        /// 它们都必须能问到同一个闸门。把判据留在本类，是因为仓库根这个字段的主人就是本类。
        /// </summary>
        /// <param name="operation">给用户看的动作名，例如"导入 MOD"。</param>
        public void ThrowIfRelocating(string operation)
        {
            if (!IsRelocating) return;

            throw new InvalidOperationException(
                $"MOD 存放位置正在搬移，暂时不能{operation}。等搬完（界面上会提示）再试一次。");
        }

        private sealed class RelocationGate : IDisposable
        {
            private ObjectStore? _owner;

            internal RelocationGate(ObjectStore owner) => _owner = owner;

            public void Dispose()
            {
                var owner = Interlocked.Exchange(ref _owner, null);
                if (owner == null) return;

                Volatile.Write(ref owner._relocating, 0);
                owner._logger.LogInformation("仓库已开闸: {Root}", owner._repositoryRoot);
            }
        }

        /// <summary>
        /// 认下一个<b>已经落盘</b>的新仓库根：只改内存，不写配置。
        ///
        /// <para>
        /// <b>这里刻意没有"顺手把配置也写了"的版本。</b>本类此前有一个
        /// <c>SetRepositoryRoot(path)</c>，它写配置 + 改内存，一行就能换掉仓库位置——
        /// 而设置界面正是照着它写的：只改指针，不搬数据。用户改完位置，已导入的包实体
        /// 还躺在旧位置、新仓库是空的，界面上 MOD 全没了，他既不知道发生了什么、
        /// 也不会想到要自己去拷目录。把那个方法删掉是唯一能根治的做法：
        /// 只要它还在，早晚会有第二处调用它，而调用它的代价是用户以为自己的 MOD 丢了。
        /// </para>
        ///
        /// <para>
        /// 现在换位置只有一条路：<see cref="RepositoryRelocationService"/> 先把数据搬过去、
        /// 校验通过、写下墓碑，然后<b>在同一次原子写里</b>把存放位置与搬移日记一起落盘
        /// （<see cref="UiPreferences.CommitRepositoryRelocation"/>），最后调本方法把
        /// 内存里的值追上。"先落盘、再改内存"这条顺序仍然成立，只是落盘那一半由调用方完成
        /// ——它必须和日记写在一起，否则断电恢复读到的两个值会互相矛盾。
        /// </para>
        /// </summary>
        public void AdoptRelocatedRoot(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                throw new ArgumentException("仓库位置不能为空", nameof(path));

            _repositoryRoot = path;
            _logger.LogInformation("仓库路径已切换到: {Path}", path);
        }

        /// <summary>
        /// 初始化仓库目录。
        /// </summary>
        public void EnsureInitialized()
        {
            if (!Directory.Exists(_repositoryRoot))
            {
                Directory.CreateDirectory(_repositoryRoot);
                _logger.LogInformation("创建仓库目录: {Path}", _repositoryRoot);
            }
        }

        /// <summary>
        /// 获取包在仓库中的目录路径。
        /// packageKey 可能来自整合包内的清单（不可信），故一律按单段目录名校验，
        /// 防止 "..\\..\\Startup" 之类的键把后续的写入/递归删除带出仓库根。
        /// </summary>
        public string GetPackageDirectory(string packageKey)
            => Path.Combine(_repositoryRoot, PathSanitizer.SanitizeSegment(packageKey, nameof(packageKey)));

        /// <summary>
        /// 获取包的文件存储目录。
        /// </summary>
        public string GetPackageFilesDirectory(string packageKey)
            => Path.Combine(GetPackageDirectory(packageKey), "files");

        /// <summary>
        /// 获取包的 manifest 路径。
        /// </summary>
        public string GetManifestPath(string packageKey)
            => Path.Combine(GetPackageDirectory(packageKey), "manifest.json");

        /// <summary>
        /// 存储文件到包仓库。
        /// 返回文件在仓库内的相对路径。
        /// </summary>
        public async Task<(string relativePath, string fileHash, long fileSize)> StoreFileAsync(
            string packageKey, string sourceFilePath, string? targetRelativeName = null)
        {
            ThrowIfRelocating("导入 MOD");
            EnsureInitialized();

            var fileName = PathSanitizer.SanitizeRelative(targetRelativeName ?? Path.GetFileName(sourceFilePath));
            var packageFilesDir = GetPackageFilesDirectory(packageKey);
            Directory.CreateDirectory(packageFilesDir);

            var targetPath = Path.Combine(packageFilesDir, fileName);
            var targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            // 计算哈希
            var hash = await ComputeFileHashAsync(sourceFilePath);
            var fileSize = new FileInfo(sourceFilePath).Length;

            // 复制文件
            File.Copy(sourceFilePath, targetPath, true);

            var relativePath = Path.Combine(packageKey, "files", fileName);
            _logger.LogDebug("文件已存储: {Path} (hash={Hash})", relativePath, hash);

            return (relativePath, hash, fileSize);
        }

        /// <summary>
        /// 批量存储文件到包仓库。
        /// </summary>
        public async Task<List<(string relativePath, string fileHash, long fileSize, string fileName)>> StoreFilesAsync(
            string packageKey, IEnumerable<string> sourceFilePaths)
        {
            var results = new List<(string, string, long, string)>();
            foreach (var filePath in sourceFilePaths)
            {
                var (relPath, hash, size) = await StoreFileAsync(packageKey, filePath);
                results.Add((relPath, hash, size, Path.GetFileName(filePath)));
            }
            return results;
        }

        /// <summary>
        /// 存储预览图到包仓库，返回落盘后的完整路径。
        ///
        /// 写失败上抛而不再返回 null：调用方拿到 null 只知道"没成"，不知道是图片源文件
        /// 读不了、还是仓库目录不可写，于是 MainViewModel 只能给出一句猜测性的
        /// "请确认图片文件仍然存在且可读取"，把磁盘满/权限不足指到了错误的方向。
        /// 允许预览图失败但不允许整体失败的调用方（导入、数据迁移）自行 catch。
        /// </summary>
        public string StorePreviewImage(string packageKey, string sourceImagePath)
        {
            try
            {
                ThrowIfRelocating("更换预览图");
                EnsureInitialized();
                var packageDir = GetPackageDirectory(packageKey);
                Directory.CreateDirectory(packageDir);

                var ext = Path.GetExtension(sourceImagePath);
                var previewPath = Path.Combine(packageDir, $"preview{ext}");

                // 删除旧预览图。这里必须保持 GetFiles（先物化）：
                // 边枚举边删同一目录会让枚举器抛异常。
                foreach (var old in Directory.GetFiles(packageDir, "preview*"))
                {
                    try { File.Delete(old); }
                    catch (Exception ex)
                    {
                        // 删不掉旧图不阻断写新图：同扩展名会被下面的 Copy(overwrite) 直接覆盖，
                        // 不同扩展名则最多留下一张用不到的旧图，不值得让整个操作失败。
                        _logger.LogWarning(ex, "删除旧预览图失败: {Path}", old);
                    }
                }

                File.Copy(sourceImagePath, previewPath, true);
                return previewPath;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "存储预览图失败: {Package}", packageKey);
                throw;
            }
        }

        /// <summary>
        /// 获取包的预览图路径。
        /// </summary>
        public string? GetPreviewImagePath(string packageKey)
        {
            var packageDir = GetPackageDirectory(packageKey);
            if (!Directory.Exists(packageDir)) return null;

            return Directory.EnumerateFiles(packageDir, "preview*")
                .FirstOrDefault();
        }

        /// <summary>
        /// 获取包内的所有文件路径。
        /// </summary>
        public List<string> GetPackageFiles(string packageKey)
        {
            var filesDir = GetPackageFilesDirectory(packageKey);
            if (!Directory.Exists(filesDir)) return new List<string>();
            // EnumerateFiles：GetFiles 会先物化成 string[]，再 ToList 又复制一遍
            return Directory.EnumerateFiles(filesDir, "*.*", SearchOption.AllDirectories).ToList();
        }

        /// <summary>
        /// 删除包在仓库中的所有数据。
        /// </summary>
        public bool DeletePackage(string packageKey)
        {
            try
            {
                // 闸门检查刻意放在 try 里面：搬移期间删包会返回 false 并留一条错误日志，
                // 与"删不掉"走同一条路径。调用方（PackageRepository.DeletePackageAsync、
                // 导入失败的补偿清理）本来就按 false 处理，不会因此崩掉；
                // 而搬移是模态的，正常情况下压根走不到这里。
                ThrowIfRelocating("删除 MOD");

                var packageDir = GetPackageDirectory(packageKey);
                if (Directory.Exists(packageDir))
                {
                    Directory.Delete(packageDir, true);
                    _logger.LogInformation("包仓库已删除: {Package}", packageKey);
                }
                return true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "删除包仓库失败: {Package}", packageKey);
                return false;
            }
        }

        /// <summary>
        /// 检查包是否存在于仓库中。非法的 packageKey 视为不存在（谓词不抛异常）。
        /// </summary>
        public bool PackageExists(string packageKey)
        {
            try
            {
                return Directory.Exists(GetPackageDirectory(packageKey))
                       && File.Exists(GetManifestPath(packageKey));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// 包目录是否已存在于磁盘（不要求 manifest.json）。
        ///
        /// 用于识别"磁盘上有、索引里没有"的导入残留：导入是先逐个文件落盘、最后才注册，
        /// 中途失败会留下有 files/ 但无 manifest、无索引记录的孤儿目录。
        /// 这类键不能被下一次同名导入复用——StoreFileAsync 是 File.Copy(overwrite:true)，
        /// 残留文件不会被清掉，只会混进新包。
        /// 非法 packageKey 视为不存在（谓词不抛异常）。
        /// </summary>
        public bool PackageDirectoryExists(string packageKey)
        {
            try
            {
                return Directory.Exists(GetPackageDirectory(packageKey));
            }
            catch (ArgumentException)
            {
                return false;
            }
        }

        /// <summary>
        /// 枚举仓库根目录下的所有包目录名。
        /// 注意仓库根是**跨游戏共享**的，返回值包含其它游戏的包。
        /// </summary>
        public List<string> EnumeratePackageKeys()
        {
            if (!Directory.Exists(_repositoryRoot)) return new List<string>();

            try
            {
                return Directory.EnumerateDirectories(_repositoryRoot)
                    .Select(Path.GetFileName)
                    .Where(name => !string.IsNullOrEmpty(name))
                    .Select(name => name!)
                    .ToList();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "枚举仓库包目录失败: {Root}", _repositoryRoot);
                return new List<string>();
            }
        }

        /// <summary>
        /// 计算文件的 SHA-256 哈希（前 16 字符）。
        /// </summary>
        public static async Task<string> ComputeFileHashAsync(string filePath)
        {
            using var stream = File.OpenRead(filePath);
            var hashBytes = await SHA256.HashDataAsync(stream);
            return Convert.ToHexString(hashBytes)[..16].ToLowerInvariant();
        }

        /// <summary>
        /// 组合内容哈希的并发读取上限。
        ///
        /// SHA-256 本身在现代 CPU 上远快于磁盘，这里的瓶颈是读文件：
        /// NVMe 需要几路并发才能压满队列深度，而机械盘并发一高就变成来回寻道，反而更慢。
        /// 取一个对两种介质都不吃亏的保守值，并且不超过逻辑核数。
        /// </summary>
        private static readonly int HashConcurrency = Math.Min(4, Environment.ProcessorCount);

        /// <summary>
        /// 计算多个文件的组合内容哈希。
        /// 按路径排序后依次拼接各文件哈希，再整体哈希一次，保证结果与文件枚举顺序无关。
        /// </summary>
        /// <remarks>
        /// 文件哈希并发计算（上限 <see cref="HashConcurrency"/>），但结果**先并发算、后按排序位置回填**，
        /// 与串行版本逐字节一致——含多个 GB 级 .pak 的包，串行总耗时是所有文件读取时间之和。
        /// 排序沿用默认字符串比较器（非 Ordinal）：改比较器会改变拼接顺序，
        /// 进而改变已入库包的 ContentHash，使重复包检测失效。
        /// </remarks>
        public static async Task<string> ComputeContentHashAsync(IEnumerable<string> filePaths)
        {
            if (filePaths == null) throw new ArgumentNullException(nameof(filePaths));

            var ordered = filePaths.OrderBy(p => p).ToList();
            var hashes = new string[ordered.Count];

            var nextIndex = -1;
            var workers = Enumerable
                .Range(0, Math.Min(HashConcurrency, ordered.Count))
                .Select(_ => Task.Run(async () =>
                {
                    int index;
                    while ((index = Interlocked.Increment(ref nextIndex)) < ordered.Count)
                    {
                        hashes[index] = await ComputeFileHashAsync(ordered[index]);
                    }
                }));

            await Task.WhenAll(workers);

            var combined = string.Join("|", hashes);
            var bytes = System.Text.Encoding.UTF8.GetBytes(combined);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash)[..16].ToLowerInvariant();
        }

        /// <summary>
        /// 获取仓库总占用大小。纯统计用途，失败回落 0（只是界面上少一个数字），
        /// 但仍要留痕，否则"仓库大小永远显示 0"完全无从排查。
        /// </summary>
        public long GetTotalSize()
        {
            if (!Directory.Exists(_repositoryRoot)) return 0;
            try
            {
                // DirectoryInfo.EnumerateFiles：既不物化整棵树的路径字符串，
                // 枚举出的 FileInfo 也已带 Length，省掉逐文件再 stat 一次
                return new DirectoryInfo(_repositoryRoot)
                    .EnumerateFiles("*.*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "统计仓库大小失败: {Root}", _repositoryRoot);
                return 0;
            }
        }

        /// <summary>
        /// 获取仓库中的所有包目录名。
        /// </summary>
        public List<string> GetAllPackageKeys()
        {
            if (!Directory.Exists(_repositoryRoot)) return new List<string>();
            return Directory.EnumerateDirectories(_repositoryRoot)
                .Select(d => new DirectoryInfo(d).Name)
                .Where(name => File.Exists(Path.Combine(_repositoryRoot, name, "manifest.json")))
                .ToList();
        }
    }
}
