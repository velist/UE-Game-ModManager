using System;
using System.Collections.Generic;

namespace UEModManager.Models;

/// <summary>Exact managed files for one game installation, independent of the currently selected Profile.</summary>
public sealed class DeploymentState
{
    public string HostGameName { get; init; } = "";
    public string GameRootPath { get; init; } = "";
    public string ModRootPath { get; init; } = "";
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
