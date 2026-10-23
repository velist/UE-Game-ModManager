using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Localization;
using UEModManager.Models;

namespace UEModManager.Services;

/// <summary>
/// 切换方案 = 先把游戏目录部署成目标方案，成功了才把它设为当前方案。
///
/// <para>
/// 以前切换只改"当前方案"并保存，游戏目录里仍是旧方案的文件，界面却显示新方案；
/// 直到用户在新方案里点任意一个开关，才一次性触发整套增删，毫无心理准备。
/// </para>
///
/// <para>
/// 失败时不切换：部署事务自己会回滚，游戏目录回到切换前；当前方案保持不变，
/// 界面和游戏目录始终一致。还没配置 MOD 目录时（从没部署过任何东西）直接切换，
/// 否则这类用户会完全切不了方案。
/// </para>
/// </summary>
public sealed class ProfileSwitchService
{
    private readonly ProfileService _profiles;
    private readonly DeploymentPlanner _planner;
    private readonly DeploymentService _deployment;
    private readonly GameConfigService _gameConfig;
    private readonly ILogger<ProfileSwitchService> _logger;

    public ProfileSwitchService(
        ProfileService profiles,
        DeploymentPlanner planner,
        DeploymentService deployment,
        GameConfigService gameConfig,
        ILogger<ProfileSwitchService> logger)
    {
        _profiles = profiles;
        _planner = planner;
        _deployment = deployment;
        _gameConfig = gameConfig;
        _logger = logger;
    }

    /// <summary>部署并切换到目标方案。失败时保持原方案，游戏目录由事务回滚保持原样。</summary>
    public async Task<OperationResult> SwitchAsync(Guid profileId)
    {
        var target = _profiles.GetProfiles().FirstOrDefault(p => p.Id == profileId);
        if (target is null) return OperationResult.Fail(UiText.Get("找不到这个方案，可能已被删除。"));
        if (target.IsActive) return OperationResult.Ok();

        if (string.IsNullOrWhiteSpace(_gameConfig.CurrentModPath))
        {
            await _profiles.SwitchProfileAsync(profileId);
            return OperationResult.Ok();
        }

        DeploymentPlan plan;
        try
        {
            plan = await _planner.CreatePlanForProfileAsync(target);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "切换方案 '{Name}' 前计算部署计划失败", target.Name);
            return OperationResult.Fail(UiText.Interpolate($"没能切换到方案「{target.Name}」，仍在使用原方案。原因：{ex.Message}"));
        }

        if (plan.HasChanges)
        {
            var transaction = await _deployment.ExecuteAsync(plan);
            if (transaction.Status != DeploymentStatus.Committed)
            {
                _logger.LogWarning("切换方案 '{Name}' 时部署失败: {Error}", target.Name, transaction.ErrorMessage);
                return OperationResult.Fail(UiText.Interpolate(
                    $"没能切换到方案「{target.Name}」，游戏文件已恢复原样，仍在使用原方案。原因：{transaction.ErrorMessage}"));
            }

            _logger.LogInformation("切换方案 '{Name}' 已部署: +{Add} -{Remove} ~{Replace}",
                target.Name, plan.AddCount, plan.RemoveCount, plan.ReplaceCount);
        }

        await _profiles.SwitchProfileAsync(profileId);
        return OperationResult.Ok();
    }
}
