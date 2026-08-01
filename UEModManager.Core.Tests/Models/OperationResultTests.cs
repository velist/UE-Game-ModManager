using System;
using System.Collections.Generic;
using UEModManager.Models;

namespace UEModManager.Core.Tests.Models;

/// <summary>
/// OperationResult 的行为测试。
/// 这个类型存在的唯一目的是把失败原因带到 View 层，所以"原因不能丢"和
/// "取消不能被当成失败"是两条必须钉死的性质。
/// </summary>
public class OperationResultTests
{
    // ── 三种状态互斥 ──────────────────────────────────

    [Fact]
    public void Ok_成功且无错误信息且非取消()
    {
        var result = OperationResult.Ok();

        Assert.True(result.Success);
        Assert.Null(result.Error);
        Assert.False(result.IsCancelled);
    }

    [Fact]
    public void Fail_不成功且保留原始错误信息()
    {
        var result = OperationResult.Fail("目标文件被游戏占用");

        Assert.False(result.Success);
        Assert.Equal("目标文件被游戏占用", result.Error);
        Assert.False(result.IsCancelled);
    }

    [Fact]
    public void Cancel_不成功但也不是失败且无错误信息()
    {
        var result = OperationResult.Cancel();

        Assert.False(result.Success);
        Assert.True(result.IsCancelled);
        Assert.Null(result.Error);
    }

    // ── 原因缺失时的兜底 ──────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Fail_原因为空时回落兜底文案_绝不留空白提示框(string? error)
    {
        // DeploymentTransaction.ErrorMessage 可能为 null，直接透传会弹出空白对话框
        var result = OperationResult.Fail(error);

        Assert.False(result.Success);
        Assert.Equal(OperationResult.DefaultError, result.Error);
        Assert.False(string.IsNullOrWhiteSpace(result.Error));
    }

    // ── 批量结果合并 ──────────────────────────────────

    [Fact]
    public void Aggregate_空集合视为成功()
    {
        var result = OperationResult.Aggregate(Array.Empty<OperationResult>());

        Assert.True(result.Success);
    }

    [Fact]
    public void Aggregate_全部成功则成功()
    {
        var result = OperationResult.Aggregate(new[]
        {
            OperationResult.Ok(), OperationResult.Ok(), OperationResult.Ok()
        });

        Assert.True(result.Success);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Aggregate_有一个失败就算失败并带上原因()
    {
        var result = OperationResult.Aggregate(new[]
        {
            OperationResult.Ok(),
            OperationResult.Fail("磁盘空间不足"),
            OperationResult.Ok()
        });

        Assert.False(result.Success);
        Assert.Contains("磁盘空间不足", result.Error);
    }

    [Fact]
    public void Aggregate_多个不同失败原因逐行列出()
    {
        var result = OperationResult.Aggregate(new[]
        {
            OperationResult.Fail("磁盘空间不足"),
            OperationResult.Fail("文件被占用")
        });

        Assert.False(result.Success);
        Assert.Contains("磁盘空间不足", result.Error);
        Assert.Contains("文件被占用", result.Error);
    }

    [Fact]
    public void Aggregate_相同失败原因去重_批量操作不会刷屏()
    {
        // 批量禁用 50 个 MOD 时若都因同一个原因失败，不该弹出 50 行一模一样的文字
        var results = new List<OperationResult>();
        for (var i = 0; i < 50; i++)
            results.Add(OperationResult.Fail("游戏正在运行，无法修改文件"));

        var result = OperationResult.Aggregate(results);

        Assert.Equal("游戏正在运行，无法修改文件", result.Error);
    }

    [Fact]
    public void Aggregate_全部取消则结果为取消_不弹错误框()
    {
        var result = OperationResult.Aggregate(new[]
        {
            OperationResult.Cancel(), OperationResult.Cancel()
        });

        Assert.True(result.IsCancelled);
        Assert.False(result.Success);
    }

    [Fact]
    public void Aggregate_取消与成功混合视为成功()
    {
        var result = OperationResult.Aggregate(new[]
        {
            OperationResult.Ok(), OperationResult.Cancel()
        });

        Assert.True(result.Success);
        Assert.False(result.IsCancelled);
    }

    [Fact]
    public void Aggregate_取消不会掩盖同批次里的真实失败()
    {
        var result = OperationResult.Aggregate(new[]
        {
            OperationResult.Cancel(),
            OperationResult.Fail("权限不足")
        });

        Assert.False(result.Success);
        Assert.False(result.IsCancelled);
        Assert.Contains("权限不足", result.Error);
    }

    [Fact]
    public void Aggregate_传入null抛异常()
    {
        Assert.Throws<ArgumentNullException>(() => OperationResult.Aggregate(null!));
    }
}
