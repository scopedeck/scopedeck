using Siglent.Server;

namespace Siglent.Scope.Tests;

public class UpdateCheckerTests
{
    [Theory]
    [InlineData("0.1.0-rc.2", "0.1.0-rc.3", -1)]
    [InlineData("0.1.0-rc.10", "0.1.0-rc.9", 1)]   // numeric, not alphabetical
    [InlineData("0.1.0-rc.3", "0.1.0", -1)]        // the release is newer than its candidates
    [InlineData("0.1.0", "0.1.1-rc.1", -1)]
    [InlineData("0.2.0", "0.10.0", -1)]
    [InlineData("1.0.0", "1.0.0", 0)]
    [InlineData("1.0.0+build5", "1.0.0", 0)]       // build metadata is ignored
    [InlineData("0.1.0-alpha", "0.1.0-beta", -1)]
    public void ComparesVersions(string a, string b, int expected) =>
        Assert.Equal(expected, Math.Sign(SemVer.Compare(a, b)));

    private static UpdateChecker.Release R(string tag, bool pre = false, bool draft = false) =>
        new(tag, $"https://github.com/scopedeck/scopedeck/releases/tag/{tag}", pre, draft);

    private static readonly UpdateChecker.Release[] Releases =
    [
        R("v0.1.0-rc.1", pre: true), R("v0.1.0-rc.3", pre: true), R("v0.1.0-rc.2", pre: true),
        R("v0.2.0-rc.1", pre: true, draft: true), R("not-a-version"),
    ];

    [Fact]
    public void PreReleaseBuildIsOfferedNewestPreRelease() =>
        Assert.Equal("v0.1.0-rc.3", UpdateChecker.Pick("0.1.0-rc.2", Releases)?.TagName);

    [Fact]
    public void NewestBuildGetsNothing() => Assert.Null(UpdateChecker.Pick("0.1.0-rc.3", Releases));

    [Fact]
    public void ReleaseBuildIgnoresPreReleases()
    {
        Assert.Null(UpdateChecker.Pick("0.1.0", [.. Releases, R("v0.1.1-rc.1", pre: true)]));
        Assert.Equal("v0.1.1", UpdateChecker.Pick("0.1.0", [.. Releases, R("v0.1.1")])?.TagName);
    }

    [Fact]
    public void PreReleaseBuildIsOfferedTheFinalRelease() =>
        Assert.Equal("v0.1.0", UpdateChecker.Pick("0.1.0-rc.3", [.. Releases, R("v0.1.0")])?.TagName);
}
