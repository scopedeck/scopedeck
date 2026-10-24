using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;

namespace Siglent.Server;

public sealed record UpdateInfo(string Current, string? Latest, string? Url, bool Available)
{
    /// <summary>Files of the newer release (not sent to the UI).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<UpdateChecker.Asset> Assets { get; init; } = [];
}

/// <summary>
/// Asks GitHub once per run whether a newer release exists. Pre-release builds are offered newer pre-releases
/// and releases; release builds only releases. Local builds ("-dev") and disabled checks never contact GitHub.
/// Failures (offline, rate limit) simply report no update.
/// </summary>
public sealed class UpdateChecker(IConfiguration config, ILogger<UpdateChecker> logger)
{
    private const string ReleasesUrl = "https://api.github.com/repos/scopedeck/scopedeck/releases?per_page=20";

    private readonly Lazy<Task<UpdateInfo>> _result = new(() => CheckAsync(config, logger));

    public static string CurrentVersion { get; } =
        typeof(UpdateChecker).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

    public Task<UpdateInfo> GetAsync() => _result.Value;

    private static async Task<UpdateInfo> CheckAsync(IConfiguration config, ILogger logger)
    {
        var none = new UpdateInfo(CurrentVersion, null, null, false);
        if (!config.GetValue("UpdateCheck:Enabled", true) || CurrentVersion.EndsWith("-dev", StringComparison.Ordinal))
            return none;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"ScopeDeck/{CurrentVersion}");
            http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
            var releases = await http.GetFromJsonAsync<Release[]>(ReleasesUrl) ?? [];
            return Pick(CurrentVersion, releases) is { } newest
                ? new UpdateInfo(CurrentVersion, newest.TagName.TrimStart('v'), newest.HtmlUrl, true) { Assets = newest.Assets ?? [] }
                : none;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            logger.LogInformation("Update check skipped: {Message}", e.Message);
            return none;
        }
    }

    /// <summary>The newest release that is newer than <paramref name="current"/>, or null.</summary>
    internal static Release? Pick(string current, IEnumerable<Release> releases)
    {
        bool wantPre = current.Contains('-');
        return releases
            .Where(r => !r.Draft && (wantPre || !r.Prerelease) && SemVer.TryParse(r.TagName.TrimStart('v'), out _))
            .Where(r => SemVer.Compare(r.TagName.TrimStart('v'), current) > 0)
            .OrderByDescending(r => r.TagName.TrimStart('v'), SemVer.Comparer)
            .FirstOrDefault();
    }

    internal sealed record Release(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("html_url")] string HtmlUrl,
        [property: JsonPropertyName("prerelease")] bool Prerelease,
        [property: JsonPropertyName("draft")] bool Draft,
        [property: JsonPropertyName("assets")] Asset[]? Assets = null);

    /// <param name="Digest">"sha256:…", published by GitHub for each release file.</param>
    public sealed record Asset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string Url,
        [property: JsonPropertyName("size")] long Size,
        [property: JsonPropertyName("digest")] string? Digest);
}

/// <summary>Semantic version ordering: 0.1.0-rc.2 &lt; 0.1.0-rc.10 &lt; 0.1.0 &lt; 0.1.1.</summary>
internal static class SemVer
{
    public static readonly IComparer<string> Comparer = Comparer<string>.Create(Compare);

    public static bool TryParse(string version, out (int[] Core, string[] Pre) parsed)
    {
        parsed = default;
        var plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];
        var dash = version.IndexOf('-');
        var core = (dash < 0 ? version : version[..dash]).Split('.');
        if (core.Length != 3) return false;
        var numbers = new int[3];
        for (int i = 0; i < 3; i++)
            if (!int.TryParse(core[i], out numbers[i])) return false;
        parsed = (numbers, dash < 0 ? [] : version[(dash + 1)..].Split('.'));
        return true;
    }

    public static int Compare(string? a, string? b)
    {
        if (!TryParse(a ?? "", out var x)) return TryParse(b ?? "", out _) ? -1 : 0;
        if (!TryParse(b ?? "", out var y)) return 1;
        for (int i = 0; i < 3; i++)
            if (x.Core[i] != y.Core[i]) return x.Core[i].CompareTo(y.Core[i]);
        // A version without pre-release identifiers is newer than one with them.
        if (x.Pre.Length == 0 && y.Pre.Length == 0) return 0;
        if (x.Pre.Length == 0) return 1;
        if (y.Pre.Length == 0) return -1;
        for (int i = 0; i < Math.Min(x.Pre.Length, y.Pre.Length); i++)
        {
            bool xn = int.TryParse(x.Pre[i], out int xi), yn = int.TryParse(y.Pre[i], out int yi);
            int c = xn && yn ? xi.CompareTo(yi) : xn ? -1 : yn ? 1 : string.CompareOrdinal(x.Pre[i], y.Pre[i]);
            if (c != 0) return c;
        }
        return x.Pre.Length.CompareTo(y.Pre.Length);
    }
}
