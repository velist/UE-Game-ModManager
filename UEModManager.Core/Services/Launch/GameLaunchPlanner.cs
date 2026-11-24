using System;
using System.Globalization;
using UEModManager.Models;

namespace UEModManager.Services.Launch
{
    /// <summary>决定启动方式要看的全部事实（由主程序探测后传入）。</summary>
    /// <param name="SteamAppId">游戏在 Steam 库里时，从 <c>appmanifest</c> 读出的 appid；不在库里或读不出为 0。</param>
    /// <param name="SteamClientAvailable">本机能不能打开 <c>steam://</c> 链接（装了 Steam 客户端）。</param>
    /// <param name="PreferDirect">玩家为这款游戏选了「直接运行游戏程序」。</param>
    /// <param name="HasEnvironmentVariables">这次启动要给游戏进程追加环境变量（经 Steam 启动传不过去）。</param>
    /// <param name="LaunchArguments">启动参数，可为空。</param>
    public sealed record GameLaunchFacts(
        uint SteamAppId,
        bool SteamClientAvailable,
        bool PreferDirect,
        bool HasEnvironmentVariables,
        string? LaunchArguments);

    /// <summary>这一次怎么启动游戏。</summary>
    /// <param name="Method">启动方式。</param>
    /// <param name="SteamAppId">Steam appid；游戏不在 Steam 库里为 0（与 <paramref name="Method"/> 无关）。</param>
    /// <param name="SteamUri">经 Steam 启动时要打开的链接；直接启动时为 null。</param>
    /// <param name="CanUseSteam">这款游戏能不能经 Steam 启动（界面据此决定是否给出切换入口）。</param>
    public sealed record GameLaunchPlan(GameLaunchMethod Method, uint SteamAppId, string? SteamUri, bool CanUseSteam);

    /// <summary>
    /// 启动方式决策（纯函数）。
    ///
    /// 装在 Steam 库里的游戏默认交给 Steam 启动：带 Steam 保护或需要 Steam 引导的游戏，
    /// 直接运行它的 exe 会被 Steam 客户端接管（弹回 Steam、甚至重启 Steam），游戏并不出现。
    /// 认不出 appid、没装 Steam 客户端、或玩家明确选了直接运行时，才直接运行 exe。
    /// </summary>
    public static class GameLaunchPlanner
    {
        public static GameLaunchPlan Decide(GameLaunchFacts facts)
        {
            ArgumentNullException.ThrowIfNull(facts);

            var canUseSteam = facts.SteamAppId != 0 && facts.SteamClientAvailable;
            if (!canUseSteam || facts.PreferDirect || facts.HasEnvironmentVariables)
                return new GameLaunchPlan(GameLaunchMethod.DirectExecutable, facts.SteamAppId, null, canUseSteam);

            return new GameLaunchPlan(
                GameLaunchMethod.Steam, facts.SteamAppId,
                BuildSteamUri(facts.SteamAppId, facts.LaunchArguments), canUseSteam);
        }

        /// <summary>
        /// 拼出让 Steam 启动某款游戏的链接。
        /// 没有参数用 <c>steam://rungameid/&lt;appid&gt;</c>；带参数用 <c>steam://run/&lt;appid&gt;//&lt;参数&gt;/</c>
        /// （参数按 URL 规则转义，Steam 会先向玩家确认一次）。
        /// </summary>
        public static string BuildSteamUri(uint appId, string? launchArguments = null)
        {
            if (appId == 0) throw new ArgumentOutOfRangeException(nameof(appId), "appid 不能为 0");

            var id = appId.ToString(CultureInfo.InvariantCulture);
            var arguments = launchArguments?.Trim();
            return string.IsNullOrEmpty(arguments)
                ? "steam://rungameid/" + id
                : "steam://run/" + id + "//" + Uri.EscapeDataString(arguments) + "/";
        }
    }
}
