using System;
using System.IO;
using System.Net.Http;
using System.Net.Sockets;

namespace UEModManager.Diagnostics
{
    /// <summary>用户可以采取的一键补救动作；界面层据此决定是否多放一个按钮。</summary>
    public enum UserFacingErrorAction
    {
        None,
        /// <summary>以管理员身份重新启动本程序（权限不足时）</summary>
        RestartAsAdmin,
    }

    /// <summary>
    /// 翻译后的错误：Summary 说清「发生了什么」，Suggestion 说清「用户该怎么办」。
    /// 两者都是面向普通玩家的白话，不含异常类型名、HRESULT 或英文原文。
    /// </summary>
    public sealed record UserFacingError(string Summary, string Suggestion, UserFacingErrorAction Action);

    /// <summary>
    /// 把异常翻译成玩家看得懂的说明。原始异常只应进日志和"复制诊断信息"，不直接展示。
    /// </summary>
    public static class UserFacingErrors
    {
        // Win32 错误码在 IOException.HResult 里以 0x8007xxxx 形式出现
        private const int ErrorAccessDenied = unchecked((int)0x80070005);
        private const int ErrorSharingViolation = unchecked((int)0x80070020);
        private const int ErrorLockViolation = unchecked((int)0x80070021);
        private const int ErrorHandleDiskFull = unchecked((int)0x80070027);
        private const int ErrorDiskFull = unchecked((int)0x80070070);

        public static UserFacingError Describe(Exception ex)
        {
            ArgumentNullException.ThrowIfNull(ex);
            var known = DescribeKnown(Unwrap(ex));
            if (known != null) return known;

            // 业务代码里不少异常本身就写了给用户看的中文说明（如"不能删除最后一个方案"），
            // 这类消息比通用兜底更有用，原样展示；.NET 自身的异常消息是英文，不会命中这里
            var authored = FindAuthoredMessage(ex);
            if (authored != null)
                return new(authored, "", UserFacingErrorAction.None);

            return new("出现了意外错误。",
                "请重试一次；如果问题仍然存在，点「复制诊断信息」发给我们，方便定位问题。",
                UserFacingErrorAction.None);
        }

        private static UserFacingError? DescribeKnown(Exception ex)
        {
            switch (ex)
            {
                case UnauthorizedAccessException:
                    return AccessDenied();
                case PathTooLongException:
                    return new("文件路径太长，Windows 无法处理。",
                        "请把游戏或 MOD 放到层级更浅的文件夹（例如 D:\\Games）后重试。",
                        UserFacingErrorAction.None);
                case DirectoryNotFoundException:
                case FileNotFoundException:
                case DriveNotFoundException:
                    return new("找不到需要的文件或文件夹，可能已被移动、删除，或所在磁盘未连接。",
                        "请确认游戏目录和 MOD 文件仍在原处；如果改过游戏位置，请在设置里重新选择游戏目录。",
                        UserFacingErrorAction.None);
                case InvalidDataException:
                    return new("文件内容已损坏或格式不受支持。",
                        "请重新下载该 MOD 压缩包后再导入。",
                        UserFacingErrorAction.None);
                case IOException io when io.HResult == ErrorAccessDenied:
                    return AccessDenied();
                case IOException io when io.HResult is ErrorSharingViolation or ErrorLockViolation:
                    return new("文件正被其他程序占用。",
                        "请先关闭游戏（以及正在打开这些文件的压缩软件、编辑器），然后重试。",
                        UserFacingErrorAction.None);
                case IOException io when io.HResult is ErrorDiskFull or ErrorHandleDiskFull:
                    return new("磁盘空间不足。",
                        "请清理磁盘，或在「存储与恢复」里清理未使用的 MOD 文件后重试。",
                        UserFacingErrorAction.None);
                case HttpRequestException:
                case SocketException:
                    return Network();
                case TimeoutException:
                case OperationCanceledException when ex.InnerException is TimeoutException:
                    return new("网络请求超时。",
                        "请检查网络后重试；如果使用了代理或加速器，可以尝试切换线路。",
                        UserFacingErrorAction.None);
                case OperationCanceledException:
                    return new("操作已取消。", "", UserFacingErrorAction.None);
                default:
                    return null;
            }
        }

        private static UserFacingError AccessDenied() =>
            new("没有权限访问相关文件，游戏可能安装在受保护的目录（如 Program Files）里。",
                "以管理员身份重新启动本程序通常可以解决。",
                UserFacingErrorAction.RestartAsAdmin);

        private static UserFacingError Network() =>
            new("无法连接到服务器。",
                "请检查网络连接后重试；离线状态下本地 MOD 管理功能不受影响。",
                UserFacingErrorAction.None);

        private static string? FindAuthoredMessage(Exception ex)
        {
            for (var e = ex; e != null; e = e.InnerException)
            {
                if (ContainsCjk(e.Message)) return e.Message.Trim();
            }
            return null;
        }

        private static bool ContainsCjk(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var c in text)
                if (c >= '一' && c <= '鿿') return true;
            return false;
        }

        /// <summary>剥掉只起包装作用的外层异常，找到真正说明原因的那一层。</summary>
        private static Exception Unwrap(Exception ex)
        {
            var current = ex;
            for (var depth = 0; depth < 8; depth++)
            {
                if (current is AggregateException agg && agg.InnerExceptions.Count == 1)
                    current = agg.InnerExceptions[0];
                else if (current is System.Reflection.TargetInvocationException { InnerException: { } inner })
                    current = inner;
                else if (current.GetType() == typeof(Exception) || current is InvalidOperationException)
                {
                    // 业务代码常 throw new Exception("xxx", inner) 包一层；有更具体的内层就用内层
                    if (current.InnerException is { } wrapped) current = wrapped;
                    else break;
                }
                else break;
            }
            return current;
        }
    }
}
