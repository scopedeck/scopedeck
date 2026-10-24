using Siglent.Server;

namespace Siglent.Scope.Tests;

public sealed class UpdateInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"scopedeck-install-{Guid.NewGuid():N}");
    private readonly string _app;
    private readonly string _new;

    public UpdateInstallerTests()
    {
        _app = Directory.CreateDirectory(Path.Combine(_root, "app")).FullName;
        _new = Directory.CreateDirectory(Path.Combine(_root, "new")).FullName;
        File.WriteAllText(Path.Combine(_app, "ScopeDeck.exe"), "old exe");
        File.WriteAllText(Path.Combine(_app, "appsettings.json"), "user settings");
        File.WriteAllText(Path.Combine(_new, "ScopeDeck.exe"), "new exe");
        File.WriteAllText(Path.Combine(_new, "appsettings.json"), "default settings");
        File.WriteAllText(Path.Combine(_new, "extra.dll"), "new file");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private string Read(string name) => File.ReadAllText(Path.Combine(_app, name));

    [Fact]
    public void ReplacesExeKeepsSettingsAndLeavesOldExeForCleanUp()
    {
        var exe = Path.Combine(_app, "ScopeDeck.exe");

        UpdateInstaller.Install(_new, exe);

        Assert.Equal("new exe", Read("ScopeDeck.exe"));
        Assert.Equal("user settings", Read("appsettings.json"));
        Assert.Equal("new file", Read("extra.dll"));
        Assert.Equal("old exe", Read("ScopeDeck.exe.old"));

        UpdateInstaller.CleanUp(exe);
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public void RestoresTheRunningVersionWhenCopyingFails()
    {
        var exe = Path.Combine(_app, "ScopeDeck.exe");
        File.Delete(Path.Combine(_new, "ScopeDeck.exe")); // makes the copy fail

        Assert.ThrowsAny<IOException>(() => UpdateInstaller.Install(_new, exe));

        Assert.Equal("old exe", Read("ScopeDeck.exe"));
        Assert.False(File.Exists(exe + ".old"));
    }

    [Fact]
    public void PicksTheDownloadMatchingTheInstalledVariant()
    {
        UpdateChecker.Asset A(string name) => new(name, "https://example/" + name, 1, "sha256:00");
        UpdateChecker.Asset[] assets =
        [
            A("ScopeDeck-0.2.0-windows-x64.zip"), A("ScopeDeck-0.2.0-windows-x64-standalone.zip"),
            A("ScopeDeck-server-0.2.0-win-x64.zip"),
        ];

        Assert.Equal("ScopeDeck-0.2.0-windows-x64-standalone.zip", UpdateInstaller.PickAsset(assets, standalone: true)?.Name);
        Assert.Equal("ScopeDeck-0.2.0-windows-x64.zip", UpdateInstaller.PickAsset(assets, standalone: false)?.Name);
    }
}
