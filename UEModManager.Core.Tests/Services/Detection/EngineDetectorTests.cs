using System;
using System.Collections.Generic;
using UEModManager.Models;
using UEModManager.Services.Detection;

namespace UEModManager.Core.Tests.Services.Detection;

public class EngineDetectorTests
{
    /// <summary>用闭包构造一组 Probe，模拟"哪些路径命中"。</summary>
    private static (Func<string, bool> dir, Func<string, bool> file, Func<string, bool> dirGlob) Probes(
        ISet<string>? dirs = null,
        ISet<string>? files = null,
        ISet<string>? dirGlobs = null)
    {
        return (
            d => dirs != null && dirs.Contains(d),
            p => files != null && files.Contains(p),
            p => dirGlobs != null && dirGlobs.Contains(p));
    }

    [Fact]
    public void Detect_NoneHit_ReturnsUnknown()
    {
        var (dir, file, dirGlob) = Probes();
        Assert.Equal(EngineType.Unknown, EngineDetector.Detect(dir, file, dirGlob));
    }

    // ─── UE ───

    [Fact]
    public void Detect_UE_ContentPaks_DirectoryHit()
    {
        var (dir, file, dirGlob) = Probes(dirs: new HashSet<string> { "Content/Paks" });
        Assert.Equal(EngineType.UnrealEngine, EngineDetector.Detect(dir, file, dirGlob));
    }

    [Fact]
    public void Detect_UE_EngineDirectory()
    {
        var (dir, file, dirGlob) = Probes(dirs: new HashSet<string> { "Engine" });
        Assert.Equal(EngineType.UnrealEngine, EngineDetector.Detect(dir, file, dirGlob));
    }

    [Fact]
    public void Detect_UE_UProjectFile()
    {
        var (dir, file, dirGlob) = Probes(files: new HashSet<string> { "*.uproject" });
        Assert.Equal(EngineType.UnrealEngine, EngineDetector.Detect(dir, file, dirGlob));
    }

    // ─── Unity ───

    [Fact]
    public void Detect_Unity_UnityPlayerDll()
    {
        var (dir, file, dirGlob) = Probes(files: new HashSet<string> { "UnityPlayer.dll" });
        Assert.Equal(EngineType.Unity, EngineDetector.Detect(dir, file, dirGlob));
    }

    [Fact]
    public void Detect_Unity_DataDirectoryGlob()
    {
        var (dir, file, dirGlob) = Probes(dirGlobs: new HashSet<string> { "*_Data" });
        Assert.Equal(EngineType.Unity, EngineDetector.Detect(dir, file, dirGlob));
    }

    // ─── REEngine ───

    [Fact]
    public void Detect_REEngine_NativesDirectory()
    {
        var (dir, file, dirGlob) = Probes(dirs: new HashSet<string> { "natives" });
        Assert.Equal(EngineType.REEngine, EngineDetector.Detect(dir, file, dirGlob));
    }

    [Fact]
    public void Detect_REEngine_ReChunkPak()
    {
        var (dir, file, dirGlob) = Probes(files: new HashSet<string> { "re_chunk_*.pak" });
        Assert.Equal(EngineType.REEngine, EngineDetector.Detect(dir, file, dirGlob));
    }

    // ─── Godot ───

    [Fact]
    public void Detect_Godot_PckFile()
    {
        var (dir, file, dirGlob) = Probes(files: new HashSet<string> { "*.pck" });
        Assert.Equal(EngineType.Godot, EngineDetector.Detect(dir, file, dirGlob));
    }

    // ─── Decima ───

    [Fact]
    public void Detect_Decima_CoreFile()
    {
        var (dir, file, dirGlob) = Probes(files: new HashSet<string> { "*.core" });
        Assert.Equal(EngineType.Decima, EngineDetector.Detect(dir, file, dirGlob));
    }

    [Fact]
    public void Detect_Decima_InitialDat()
    {
        var (dir, file, dirGlob) = Probes(files: new HashSet<string> { "initial.dat" });
        Assert.Equal(EngineType.Decima, EngineDetector.Detect(dir, file, dirGlob));
    }

    [Fact]
    public void Detect_Decima_PrefetchDirectory()
    {
        var (dir, file, dirGlob) = Probes(dirs: new HashSet<string> { "Prefetch" });
        Assert.Equal(EngineType.Decima, EngineDetector.Detect(dir, file, dirGlob));
    }

    // ─── 优先级 ───

    [Fact]
    public void Detect_UE_BeatsUnity_WhenBothHit()
    {
        // 同时命中 UE 和 Unity 特征 → UE 先返回
        var (dir, file, dirGlob) = Probes(
            dirs: new HashSet<string> { "Content/Paks" },
            files: new HashSet<string> { "UnityPlayer.dll" });

        Assert.Equal(EngineType.UnrealEngine, EngineDetector.Detect(dir, file, dirGlob));
    }

    [Fact]
    public void Detect_Unity_BeatsRE()
    {
        var (dir, file, dirGlob) = Probes(
            dirs: new HashSet<string> { "natives" },
            files: new HashSet<string> { "UnityPlayer.dll" });

        Assert.Equal(EngineType.Unity, EngineDetector.Detect(dir, file, dirGlob));
    }

    [Fact]
    public void Detect_RE_BeatsGodot()
    {
        var (dir, file, dirGlob) = Probes(
            dirs: new HashSet<string> { "natives" },
            files: new HashSet<string> { "*.pck" });

        Assert.Equal(EngineType.REEngine, EngineDetector.Detect(dir, file, dirGlob));
    }

    // ─── 参数校验 ───

    [Fact]
    public void Detect_NullDirExists_Throws()
    {
        Func<string, bool> ok = _ => false;
        Assert.Throws<ArgumentNullException>(() => EngineDetector.Detect(null!, ok, ok));
    }

    [Fact]
    public void Detect_NullFileMatch_Throws()
    {
        Func<string, bool> ok = _ => false;
        Assert.Throws<ArgumentNullException>(() => EngineDetector.Detect(ok, null!, ok));
    }

    [Fact]
    public void Detect_NullDirMatch_Throws()
    {
        Func<string, bool> ok = _ => false;
        Assert.Throws<ArgumentNullException>(() => EngineDetector.Detect(ok, ok, null!));
    }
}
