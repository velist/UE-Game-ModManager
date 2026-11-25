using System.Collections.Generic;
using UEModManager.Services.Reconciliation;

namespace UEModManager.ViewModels
{
    /// <summary>「刷新」过程中要玩家拿主意的几种情形。</summary>
    public enum DiskRefreshQuestionKind
    {
        /// <summary>仓库里登记的 MOD 一个都找不到：先当成位置问题。</summary>
        EverythingMissing,

        /// <summary>仓库里整包丢失，游戏目录里也没有它的文件：要不要从列表移除。</summary>
        RemoveLost,

        /// <summary>仓库里的文件丢了，游戏目录里还装着完整的一份：要不要找回。</summary>
        RecoverFromGame,

        /// <summary>仓库里整包丢失，游戏目录里还留着它的文件：只移除记录 / 连文件一起删 / 不处理。</summary>
        RemoveStillInstalled,

        /// <summary>仓库里缺了一部分文件：要不要移除（之后重新导入）。</summary>
        RemoveIncomplete,

        /// <summary>已启用的 MOD 装在游戏目录里的文件不见了：重新装回去 / 改为禁用 / 不处理。</summary>
        MissingDeployments,
    }

    /// <summary>玩家的回答。关掉对话框、按 Esc 一律是 <see cref="Dismiss"/>，即什么都不改。</summary>
    public enum DiskRefreshAnswer
    {
        /// <summary>什么都不改。</summary>
        Dismiss,
        Primary,
        Secondary,
    }

    /// <summary>一个要玩家回答的问题。文字已经按当前界面语言写好，界面只管显示。</summary>
    /// <param name="SecondaryText">第二个动作的按钮文字；只有两个按钮的问题为 null。</param>
    /// <param name="DismissText">"什么都不改"的按钮文字。</param>
    /// <param name="Danger">主按钮是不可撤销的动作：按钮标红，默认焦点落在 <paramref name="DismissText"/> 上。</param>
    public sealed record DiskRefreshQuestion(
        DiskRefreshQuestionKind Kind,
        string Title,
        string Message,
        string PrimaryText,
        string? SecondaryText,
        string DismissText,
        bool Danger = false);

    /// <summary>「刷新」过程中只需要告知、不需要回答的几种情形。</summary>
    public enum DiskRefreshNoticeKind
    {
        /// <summary>核对完，一切正常。</summary>
        NoProblems,

        /// <summary>仓库位置访问不到。</summary>
        RepositoryUnreachable,

        /// <summary>游戏目录访问不到。</summary>
        GameDirectoryUnreachable,

        /// <summary>还有缺文件的 MOD 启用着，装不了也撤不了任何东西。</summary>
        StillBlocked,

        /// <summary>处理完了，说一句做了什么。</summary>
        Done,

        /// <summary>有动作没做成。</summary>
        Failed,
    }

    /// <summary>一条告知。<paramref name="IsToast"/> 为 true 时用不打断操作的轻提示，否则弹窗。</summary>
    public sealed record DiskRefreshNotice(DiskRefreshNoticeKind Kind, string Title, string Message, bool IsToast);

    /// <summary>
    /// 「刷新」与玩家对话的出口。主窗口用对话框和轻提示实现它；
    /// 测试用它给出预先定好的回答，整条流程因此不依赖界面就能跑。
    /// </summary>
    public interface IDiskRefreshPrompts
    {
        DiskRefreshAnswer Ask(DiskRefreshQuestion question);

        void Notify(DiskRefreshNotice notice);
    }

    /// <summary>一次「刷新」做了什么。</summary>
    public sealed record DiskRefreshResult
    {
        /// <summary>没有执行：上一次刷新或别的操作还在进行。</summary>
        public bool Skipped { get; init; }

        /// <summary>核对的结论（处理之前的那一次）。没有执行或核对失败时为 null。</summary>
        public DiskReconcileReport? Report { get; init; }

        /// <summary>从列表里移除的 MOD（显示名）。</summary>
        public IReadOnlyList<string> Removed { get; init; } = [];

        /// <summary>从游戏目录找回的 MOD。</summary>
        public IReadOnlyList<string> Recovered { get; init; } = [];

        /// <summary>重新装回游戏目录的 MOD。</summary>
        public IReadOnlyList<string> Reinstalled { get; init; } = [];

        /// <summary>改为禁用的 MOD。</summary>
        public IReadOnlyList<string> Disabled { get; init; } = [];

        /// <summary>没做成的动作（给玩家看的说明）。</summary>
        public IReadOnlyList<string> Failures { get; init; } = [];
    }
}
