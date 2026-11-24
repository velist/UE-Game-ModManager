using System;
using System.Collections.Generic;

namespace UEModManager.Models;

/// <summary>Exact managed files for one game installation, independent of the currently selected Profile.</summary>
public sealed class DeploymentState
{
    public string HostGameName { get; init; } = "";
    public string GameRootPath { get; init; } = "";
    public string ModRootPath { get; init; } = "";

    /// <summary>
    /// 插件 / 配置包的落盘基准（游戏本体所在目录）。多数游戏与 <see cref="GameRootPath"/> 相同；
    /// 启动壳与本体不在一处的游戏（影之刃零）才不同。不参与清单的身份：身份仍按游戏路径 + MOD 路径算，
    /// 这样老版本按游戏路径装下的文件仍归同一份清单管，下一次部署就能把它们挪到正确位置。
    /// </summary>
    public string PluginRootPath { get; init; } = "";
    public Guid Revision { get; init; }
    public List<ManagedDeploymentFile> Files { get; init; } = [];
}

public sealed class ManagedDeploymentFile
{
    public string TargetPath { get; init; } = "";
    public string RelativeTargetPath { get; init; } = "";
    public string PackageKey { get; init; } = "";
    public string PackageDisplayName { get; init; } = "";
    public PackageKind Kind { get; init; }
    public string FileHash { get; init; } = "";
    public long FileSize { get; init; }

    /// <summary>An immutable copy of a pre-existing unmanaged file. Restore it when this target leaves the view.</summary>
    public string? OriginalFilePath { get; set; }
}
