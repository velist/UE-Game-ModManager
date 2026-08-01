using UEModManager.Services;

namespace UEModManager.Tests.Services;

/// <summary>
/// 组合内容哈希：文件哈希改为并发计算后，结果必须与串行版本逐字节一致，
/// 否则已入库包的 ContentHash 全部失效，重复包检测会误判。
/// </summary>
public sealed class ObjectStoreContentHashTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "UEModManager.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task ComputeContentHashAsync_InputOrderDoesNotMatter()
    {
        var files = CreateFiles(("a.pak", "AAA"), ("b.pak", "BBB"), ("c.pak", "CCC"));

        var forward = await ObjectStore.ComputeContentHashAsync(files);
        var reversed = await ObjectStore.ComputeContentHashAsync(files.Reverse().ToArray());
        var shuffled = await ObjectStore.ComputeContentHashAsync(new[] { files[1], files[2], files[0] });

        Assert.Equal(forward, reversed);
        Assert.Equal(forward, shuffled);
    }

    [Fact]
    public async Task ComputeContentHashAsync_RepeatedCalls_AreStable()
    {
        // 并发回填若有竞态，多跑几次就会露出来
        var files = CreateFiles(
            ("a.pak", new string('a', 200_000)),
            ("b.pak", new string('b', 300_000)),
            ("c.pak", new string('c', 150_000)),
            ("d.pak", new string('d', 250_000)),
            ("e.pak", new string('e', 100_000)),
            ("f.pak", new string('f', 50_000)));

        var expected = await ObjectStore.ComputeContentHashAsync(files);
        for (var i = 0; i < 5; i++)
            Assert.Equal(expected, await ObjectStore.ComputeContentHashAsync(files));
    }

    [Fact]
    public async Task ComputeContentHashAsync_MatchesSerialReferenceImplementation()
    {
        // 把"排序 → 逐个哈希 → 用 | 拼接 → 再哈希一次"这套算法钉死，
        // 避免以后有人调整并发实现时顺手改了拼接顺序或分隔符
        var files = CreateFiles(("b.pak", "BBB"), ("a.pak", "AAA"), ("c.pak", "CCC"));

        var hashes = new List<string>();
        foreach (var path in files.OrderBy(p => p))
            hashes.Add(await ObjectStore.ComputeFileHashAsync(path));

        var combined = string.Join("|", hashes);
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(combined)))[..16].ToLowerInvariant();

        Assert.Equal(expected, await ObjectStore.ComputeContentHashAsync(files));
    }

    [Fact]
    public async Task ComputeContentHashAsync_ContentChange_ChangesHash()
    {
        var files = CreateFiles(("a.pak", "AAA"), ("b.pak", "BBB"));
        var before = await ObjectStore.ComputeContentHashAsync(files);

        await File.WriteAllTextAsync(files[1], "CHANGED");

        Assert.NotEqual(before, await ObjectStore.ComputeContentHashAsync(files));
    }

    [Fact]
    public async Task ComputeContentHashAsync_SingleFileAndEmptySet()
    {
        var files = CreateFiles(("only.pak", "X"));

        Assert.False(string.IsNullOrEmpty(await ObjectStore.ComputeContentHashAsync(files)));

        // 空集合不抛异常，且结果稳定（导入无文件的包时会走到这里）
        var empty1 = await ObjectStore.ComputeContentHashAsync(Array.Empty<string>());
        var empty2 = await ObjectStore.ComputeContentHashAsync(new List<string>());
        Assert.Equal(empty1, empty2);
    }

    [Fact]
    public async Task ComputeContentHashAsync_MoreFilesThanConcurrency_AllFilesCounted()
    {
        // 文件数超过并发度时要靠取索引的循环把剩余文件领完，漏一个就会和逐文件计算的结果对不上
        var specs = Enumerable.Range(0, 20)
            .Select(i => ($"f{i:D2}.pak", $"content-{i}"))
            .ToArray();
        var files = CreateFiles(specs);

        var hashes = new List<string>();
        foreach (var path in files.OrderBy(p => p))
            hashes.Add(await ObjectStore.ComputeFileHashAsync(path));
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(string.Join("|", hashes))))[..16].ToLowerInvariant();

        Assert.Equal(expected, await ObjectStore.ComputeContentHashAsync(files));
    }

    [Fact]
    public async Task ComputeContentHashAsync_NullInput_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => ObjectStore.ComputeContentHashAsync(null!));
    }

    private string[] CreateFiles(params (string Name, string Content)[] specs)
    {
        Directory.CreateDirectory(_root);
        var paths = new string[specs.Length];
        for (var i = 0; i < specs.Length; i++)
        {
            paths[i] = Path.Combine(_root, specs[i].Name);
            File.WriteAllText(paths[i], specs[i].Content);
        }
        return paths;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
