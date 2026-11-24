using UEModManager.Localization;
using UEModManager.Services.InstallLayout;

namespace UEModManager.Services
{
    /// <summary>
    /// 把「这个包装不到游戏会读取的位置」翻译成玩家能看懂、知道下一步怎么办的话。
    /// 导入结果与体检共用同一套措辞。
    /// </summary>
    public static class InstallPlacementText
    {
        /// <summary>没有问题时返回 null。</summary>
        public static string? Describe(PlacementIssue? issue)
        {
            if (issue == null) return null;
            return issue.Kind switch
            {
                PlacementIssueKind.ChoiceVariants => UiText.Interpolate(
                    $"「{issue.DisplayName}」里是 {issue.Variants.Count} 套只能选一套的配置（{string.Join(UiText.Get("、"), issue.Variants)}），管理器没法替你决定用哪一套，所以没有把它装进游戏。请按 MOD 作者的说明选一套，手动放到游戏读取配置的目录。"),
                PlacementIssueKind.LooseLoaderScripts => UiText.Interpolate(
                    $"「{issue.DisplayName}」里只有零散的脚本文件，缺少 MOD 文件夹，游戏不会加载它。请删除后重新导入 MOD 作者提供的完整压缩包或整个 MOD 文件夹。"),
                _ => null
            };
        }
    }
}
