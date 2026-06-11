using Microsoft.Extensions.Logging.Abstractions;
using UEModManager.Services;

namespace UEModManager.Tests.Services;

public sealed class GameConfigServiceTests : IDisposable
{
    private readonly string _gameRoot = Path.Combine(
        Path.GetTempPath(),
        "UEModManager.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void AutoDetectExecutable_Expedition33_PrefersShippingExecutableOverEnshrouded()
    {
        var shippingDirectory = Path.Combine(_gameRoot, "Sandfall", "Binaries", "Win64");
        Directory.CreateDirectory(shippingDirectory);
        File.WriteAllText(Path.Combine(_gameRoot, "enshrouded.exe"), string.Empty);
        File.WriteAllText(Path.Combine(shippingDirectory, "Expedition33Steam-Win64-Shipping.exe"), string.Empty);

        var service = new GameConfigService(NullLogger<GameConfigService>.Instance);

        var executable = service.AutoDetectExecutable(_gameRoot, "光与影：33号远征队");

        Assert.Equal("Expedition33Steam-Win64-Shipping.exe", executable);
    }

    public void Dispose()
    {
        if (Directory.Exists(_gameRoot))
        {
            Directory.Delete(_gameRoot, recursive: true);
        }
    }
}
