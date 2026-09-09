using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using UEModManager.Models;
using UEModManager.Services.Persistence;

namespace UEModManager.Services;

/// <summary>
/// Durable ownership per installation. Directory names alone never grant ownership of their contents.
/// Original files live outside transaction cleanup, so disabling a config restores the game's original file.
/// </summary>
public sealed class DeploymentStateStore
{
    private readonly string _directory;
    private readonly ILogger<DeploymentStateStore> _logger;
    private readonly string? _legacyTransactionDirectory;
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(StringComparer.OrdinalIgnoreCase);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };

    public DeploymentStateStore(ILogger<DeploymentStateStore> logger)
        : this(logger, Path.Combine(Infrastructure.AppPaths.DataDirectory, "DeploymentState"),
            Infrastructure.AppPaths.DeploymentBackupsDirectory) { }

    public DeploymentStateStore(ILogger<DeploymentStateStore> logger, string stateDirectory)
        : this(logger, stateDirectory, null) { }

    public DeploymentStateStore(ILogger<DeploymentStateStore> logger, string stateDirectory, string? legacyTransactionDirectory)
    {
        _logger = logger;
        _directory = Path.GetFullPath(stateDirectory);
        _legacyTransactionDirectory = legacyTransactionDirectory == null ? null : Path.GetFullPath(legacyTransactionDirectory);
    }

    public async Task<DeploymentState> ReadAsync(string hostGameName, string gameRootPath, string modRootPath)
    {
        var empty = new DeploymentState
        {
            HostGameName = hostGameName,
            GameRootPath = NormalizeRoot(gameRootPath),
            ModRootPath = NormalizeRoot(modRootPath)
        };
        var path = GetPath(empty);
        if (!File.Exists(path)) return empty;
        try
        {
            var state = JsonSerializer.Deserialize<DeploymentState>(await File.ReadAllTextAsync(path), JsonOptions)
                ?? throw new InvalidDataException("部署文件清单为空");
            if (ScopeKey(state) != ScopeKey(empty))
                throw new InvalidDataException("部署文件清单与当前游戏目录不匹配");
            Validate(state);
            return state;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "无法读取部署归属清单: {Path}", path);
            throw new InvalidDataException("部署文件清单读取失败；为保护游戏文件，已停止部署。", ex);
        }
    }

    internal async Task<IDisposable> LockAsync(DeploymentState state)
    {
        var path = GetPath(state);
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            // File sharing also excludes a second manager process; a process-local semaphore alone cannot protect the manifest.
            var handle = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            return new GateLease(gate, handle);
        }
        catch
        {
            gate.Release();
            throw;
        }
    }

    internal async Task EnsureCurrentAsync(DeploymentState expected)
    {
        var current = await ReadAsync(expected.HostGameName, expected.GameRootPath, expected.ModRootPath);
        if (current.Revision != expected.Revision)
            throw new InvalidOperationException("部署状态已变化，请重新生成部署计划。");
    }

    internal Task WriteAsync(DeploymentState state)
    {
        Validate(state);
        return AtomicFileWriter.WriteAllTextAsync(GetPath(state), JsonSerializer.Serialize(state, JsonOptions));
    }

    internal async Task<string> PreserveOriginalAsync(DeploymentState state, string sourcePath)
    {
        ValidateTarget(state, sourcePath);
        return await PreserveFileAsync(state, sourcePath);
    }

    internal async Task<string?> PreserveLegacyOriginalAsync(DeploymentState state, string? backupPath)
    {
        if (_legacyTransactionDirectory == null || backupPath == null
            || !IsInside(_legacyTransactionDirectory, backupPath) || !File.Exists(backupPath)) return null;
        return await PreserveFileAsync(state, backupPath);
    }

    private async Task<string> PreserveFileAsync(DeploymentState state, string sourcePath)
    {
        var directory = Path.Combine(_directory, ScopeKey(state), "Originals");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            string hash;
            // Share read only while making the baseline, so its name hashes the exact bytes being preserved.
            await using (var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            await using (var copy = new FileStream(temp, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await source.CopyToAsync(copy);
                await copy.FlushAsync();
                copy.Position = 0;
                hash = Convert.ToHexString(await SHA256.HashDataAsync(copy));
            }
            var path = Path.Combine(directory, hash);
            File.Move(temp, path, overwrite: true);
            return path;
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    internal async Task<List<DeploymentTransaction>> ReadLegacyTransactionsAsync(DeploymentState state)
    {
        var transactions = new List<DeploymentTransaction>();
        if (_legacyTransactionDirectory == null || !Directory.Exists(_legacyTransactionDirectory)) return transactions;
        foreach (var directory in Directory.EnumerateDirectories(_legacyTransactionDirectory))
        {
            if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0) continue;
            var path = Path.Combine(directory, "transaction.json");
            if (!File.Exists(path)) continue;
            try
            {
                var transaction = JsonSerializer.Deserialize<DeploymentTransaction>(await File.ReadAllTextAsync(path), JsonOptions)
                    ?? throw new InvalidDataException("Empty deployment transaction");
                if (string.Equals(transaction.HostGameName, state.HostGameName, StringComparison.OrdinalIgnoreCase))
                    transactions.Add(transaction);
            }
            catch (Exception ex)
            {
                // Skipping an unreadable later transaction could incorrectly re-claim an earlier version of its files.
                _logger.LogWarning(ex, "历史部署记录不完整，保留所有无归属清单的文件: {Path}", path);
                return [];
            }
        }
        return transactions.OrderBy(t => t.CreatedAt).ThenBy(t => t.CompletedAt).ThenBy(t => t.Id).ToList();
    }

    internal void Validate(DeploymentState state)
    {
        if (state.Files == null) throw new InvalidDataException("部署文件清单缺少 Files");
        if (state.Files.Select(f => f.TargetPath).Distinct(StringComparer.OrdinalIgnoreCase).Count() != state.Files.Count)
            throw new InvalidDataException("部署文件清单包含重复路径");
        foreach (var file in state.Files)
        {
            ValidateTarget(state, file.TargetPath);
            if (string.IsNullOrEmpty(file.FileHash)) throw new InvalidDataException("部署归属记录缺少内容哈希");
            if (file.OriginalFilePath != null && !IsInside(
                Path.Combine(_directory, ScopeKey(state), "Originals"), file.OriginalFilePath))
                throw new InvalidDataException("原始文件备份路径越界");
        }
    }

    internal static void ValidateTarget(DeploymentState state, string path)
    {
        if (!IsInside(state.GameRootPath, path) && !IsInside(state.ModRootPath, path))
            throw new InvalidDataException($"部署目标越出游戏目录: {path}");
        // A junction/symlink below the selected roots would turn a contained lexical path into an outside write.
        var root = NormalizeRoot(IsInside(state.ModRootPath, path) ? state.ModRootPath : state.GameRootPath);
        for (var current = Path.GetFullPath(path); !string.Equals(current, root, StringComparison.OrdinalIgnoreCase);
            current = Path.GetDirectoryName(current) ?? root)
        {
            if ((File.Exists(current) || Directory.Exists(current))
                && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"部署目标包含链接或联接目录，已停止以保护外部文件: {current}");
        }
    }

    internal static bool IsInside(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(root) || !Path.IsPathFullyQualified(path)) return false;
        var prefix = NormalizeRoot(root);
        if (!Path.EndsInDirectorySeparator(prefix)) prefix += Path.DirectorySeparatorChar;
        return Path.GetFullPath(path).StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal static string NormalizeRoot(string root)
        => string.IsNullOrWhiteSpace(root) ? "" : Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

    private string GetPath(DeploymentState state) => Path.Combine(_directory, ScopeKey(state), "state.json");

    private static string ScopeKey(DeploymentState state)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{state.HostGameName.ToUpperInvariant()}\n{NormalizeRoot(state.GameRootPath).ToUpperInvariant()}\n{NormalizeRoot(state.ModRootPath).ToUpperInvariant()}")));

    private sealed class GateLease(SemaphoreSlim gate, FileStream handle) : IDisposable
    {
        public void Dispose()
        {
            handle.Dispose();
            gate.Release();
        }
    }
}
