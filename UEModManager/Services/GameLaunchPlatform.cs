using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32;
using UEModManager.Services.Launch;
using UEModManager.Services.Steam;

namespace UEModManager.Services
{
    /// <summary>一个由管理器直接启动的进程。</summary>
    public interface IStartedProcess
    {
        int Id { get; }
        bool HasExited { get; }
    }

    /// <summary>
    /// 启动游戏要碰的全部系统接口：读 Steam 清单、打开链接、起进程、看进程。
    /// 抽成接口是为了让测试不会真的启动任何程序、也不用真的等几十秒。
    /// </summary>
    public interface IGameLaunchPlatform
    {
        /// <summary>本机能不能打开 <c>steam://</c> 链接。</summary>
        bool IsSteamClientAvailable();

        /// <summary>读出一个 <c>steamapps</c> 目录下的全部应用清单；读不到返回空。</summary>
        IReadOnlyList<SteamAppManifest> ReadAppManifests(string steamAppsDirectory);

        /// <summary>让系统打开一个链接（<c>steam://…</c>）。打不开时抛异常。</summary>
        void OpenUri(string uri);

        /// <summary>直接运行可执行文件；系统没有返回进程对象时为 null。</summary>
        IStartedProcess? StartExecutable(
            string path, string workingDirectory, string? arguments, IReadOnlyDictionary<string, string> environment);

        /// <summary>当前正在运行的进程。</summary>
        IReadOnlyList<RunningProcessInfo> SnapshotProcesses();

        Task DelayAsync(TimeSpan delay);
    }

    /// <summary><see cref="IGameLaunchPlatform"/> 的真实实现。</summary>
    public sealed class WindowsGameLaunchPlatform : IGameLaunchPlatform
    {
        /// <summary>清单文件的大小上限。真实清单只有几 KB，超出的不是清单，不读进内存。</summary>
        private const long MaxManifestBytes = 4 * 1024 * 1024;

        public bool IsSteamClientAvailable()
        {
            try
            {
                using var key = Registry.ClassesRoot.OpenSubKey(@"steam\shell\open\command");
                return !string.IsNullOrWhiteSpace(key?.GetValue(null) as string);
            }
            catch (Exception)
            {
                return false;
            }
        }

        public IReadOnlyList<SteamAppManifest> ReadAppManifests(string steamAppsDirectory)
        {
            var result = new List<SteamAppManifest>();
            try
            {
                if (!Directory.Exists(steamAppsDirectory)) return result;
                foreach (var file in Directory.EnumerateFiles(steamAppsDirectory, "appmanifest_*.acf"))
                {
                    try
                    {
                        if (new FileInfo(file).Length > MaxManifestBytes) continue;
                        var manifest = SteamAppManifest.Parse(File.ReadAllText(file), Path.GetFileName(file));
                        if (manifest != null) result.Add(manifest);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Steam 正在改写这一份：少一份清单而已，不影响别的
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
            return result;
        }

        public void OpenUri(string uri)
        {
            using var process = Process.Start(new ProcessStartInfo { FileName = uri, UseShellExecute = true });
        }

        public IStartedProcess? StartExecutable(
            string path, string workingDirectory, string? arguments, IReadOnlyDictionary<string, string> environment)
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                WorkingDirectory = workingDirectory,
                // 追加环境变量只能在非 ShellExecute 下生效；没有环境变量时保持 ShellExecute，
                // 这样需要管理员权限的游戏仍能弹出 UAC。
                UseShellExecute = environment.Count == 0
            };

            if (!string.IsNullOrEmpty(arguments))
                psi.Arguments = arguments;

            foreach (var (key, value) in environment)
                psi.Environment[key] = value;

            var process = Process.Start(psi);
            return process == null ? null : new StartedProcess(process);
        }

        public IReadOnlyList<RunningProcessInfo> SnapshotProcesses()
        {
            var result = new List<RunningProcessInfo>();
            foreach (var process in Process.GetProcesses())
            {
                using (process)
                {
                    try
                    {
                        result.Add(new RunningProcessInfo(process.Id, process.ProcessName, TryGetImagePath(process.Id)));
                    }
                    catch (Exception)
                    {
                        // 枚举途中进程退出
                    }
                }
            }
            return result;
        }

        public Task DelayAsync(TimeSpan delay) => Task.Delay(delay);

        // Process.MainModule 对提权/受保护的进程会抛拒绝访问，而且很慢；
        // QueryFullProcessImageName 只要"有限查询"权限，游戏和反作弊进程也读得到。
        private const uint ProcessQueryLimitedInformation = 0x1000;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inheritHandle, int processId);

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder name, ref uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private static string? TryGetImagePath(int processId)
        {
            var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (handle == IntPtr.Zero) return null;
            try
            {
                var buffer = new StringBuilder(1024);
                var size = (uint)buffer.Capacity;
                return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, (int)size) : null;
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        private sealed class StartedProcess : IStartedProcess
        {
            private readonly Process _process;

            public StartedProcess(Process process)
            {
                _process = process;
                Id = process.Id;
            }

            public int Id { get; }

            public bool HasExited
            {
                get
                {
                    try { return _process.HasExited; }
                    // 提权的游戏进程：管理器没权限查它的状态。按"还在运行"处理，免得把正常启动的游戏报成秒退
                    catch (Exception) { return false; }
                }
            }
        }
    }
}
