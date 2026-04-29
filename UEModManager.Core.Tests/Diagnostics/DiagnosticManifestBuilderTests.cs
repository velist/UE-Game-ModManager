using UEModManager.Diagnostics;

namespace UEModManager.Core.Tests.Diagnostics;

public class DiagnosticManifestBuilderTests
{
    [Fact]
    public void Build_EmptyInputs_ReturnsManifestWithMetadataOnly()
    {
        var m = DiagnosticManifestBuilder.Build(
            logFiles: [], dataFiles: [], recentTransactionFiles: [],
            appVersion: "1.0.0", osVersion: "Windows 10",
            dotNetVersion: "8.0.0", currentGame: null);

        Assert.Empty(m.Entries);
        Assert.Equal("1.0.0", m.AppVersion);
        Assert.Equal("(none)", m.CurrentGame);
    }

    [Fact]
    public void Build_LogFiles_ProducesLogsPrefixedEntries()
    {
        var m = DiagnosticManifestBuilder.Build(
            logFiles: [@"C:\app\console.log", @"C:\app\console_20260428.log"],
            dataFiles: [], recentTransactionFiles: [],
            appVersion: "x", osVersion: "x", dotNetVersion: "x", currentGame: "Demo");

        Assert.Equal(2, m.Entries.Count);
        Assert.All(m.Entries, e => Assert.StartsWith("logs/", e.ZipEntryName));
        Assert.All(m.Entries, e => Assert.True(e.RequiresRedaction));
    }

    [Fact]
    public void Build_DataFiles_RequiresRedaction_GoesToDataPrefix()
    {
        var m = DiagnosticManifestBuilder.Build(
            logFiles: [],
            dataFiles: [@"C:\app\Data\demo_profiles.json", @"C:\app\Data\demo_packages.json"],
            recentTransactionFiles: [],
            appVersion: "x", osVersion: "x", dotNetVersion: "x", currentGame: "demo");

        Assert.Equal(2, m.Entries.Count);
        Assert.All(m.Entries, e => Assert.StartsWith("data/", e.ZipEntryName));
        Assert.All(m.Entries, e => Assert.True(e.RequiresRedaction));
    }

    [Fact]
    public void Build_TransactionFiles_NoRedaction_ContainsTxDirName()
    {
        var m = DiagnosticManifestBuilder.Build(
            logFiles: [],
            dataFiles: [],
            recentTransactionFiles: [@"C:\app\Data\Backups\abc-123\transaction.json"],
            appVersion: "x", osVersion: "x", dotNetVersion: "x", currentGame: null);

        var entry = Assert.Single(m.Entries);
        Assert.False(entry.RequiresRedaction);
        Assert.StartsWith("transactions/", entry.ZipEntryName);
        Assert.Contains("abc-123", entry.ZipEntryName);
    }

    [Fact]
    public void ToMetadataText_ContainsAllFields()
    {
        var m = DiagnosticManifestBuilder.Build(
            logFiles: [@"C:\a\console.log"],
            dataFiles: [],
            recentTransactionFiles: [],
            appVersion: "2.0.1",
            osVersion: "Windows 11",
            dotNetVersion: "8.0.5",
            currentGame: "Stellar Blade");

        var text = m.ToMetadataText();

        Assert.Contains("App Version: 2.0.1", text);
        Assert.Contains("OS: Windows 11", text);
        Assert.Contains(".NET: 8.0.5", text);
        Assert.Contains("Stellar Blade", text);
        Assert.Contains("logs/console.log", text);
        Assert.Contains("[redacted]", text);
    }

    [Fact]
    public void ToMetadataText_NullCurrentGame_RenderedAsNone()
    {
        var m = DiagnosticManifestBuilder.Build(
            logFiles: [], dataFiles: [], recentTransactionFiles: [],
            appVersion: "x", osVersion: "x", dotNetVersion: "x",
            currentGame: null);

        Assert.Contains("Current Game: (none)", m.ToMetadataText());
    }

    [Fact]
    public void Build_NullInputs_TreatedAsEmpty()
    {
        var m = DiagnosticManifestBuilder.Build(
            logFiles: null!,
            dataFiles: null!,
            recentTransactionFiles: null!,
            appVersion: "x", osVersion: "x", dotNetVersion: "x", currentGame: null);

        Assert.Empty(m.Entries);
    }
}
