using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Siglent.Server;

/// <summary>
/// Registered by the Windows desktop app only: where the running exe is, and how to start the new one and quit.
/// Without it, updates are offered as a link to the release page.
/// </summary>
public sealed record DesktopHost(string ExePath, Action<string> RestartInto);

public sealed record UpdateProgress(string Phase, int Percent, string? Error);

/// <summary>
/// Installs a newer release over the running desktop app: download the matching zip, check GitHub's SHA-256,
/// unpack, rename the running exe to *.old (Windows allows renaming a running exe, not overwriting it), copy the
/// new files in and restart. On failure the old exe is put back. The user's appsettings.json is kept.
/// </summary>
public sealed class UpdateInstaller(UpdateChecker checker, ILogger<UpdateInstaller> logger, DesktopHost? host = null)
{
    private static readonly string WorkDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "ScopeDeck", "update");

    private readonly Lock _lock = new();
    private UpdateProgress _progress = new("idle", 0, null);
    private Task? _running;

    public UpdateProgress Progress { get { lock (_lock) return _progress; } }

    /// <summary>Standalone exes carry their own runtime; the small ones use the installed .NET.</summary>
    internal static bool IsStandalone =>
        !RuntimeEnvironment.GetRuntimeDirectory().Contains(Path.Combine("shared", "Microsoft.NETCore.App"), StringComparison.OrdinalIgnoreCase);

    internal static UpdateChecker.Asset? PickAsset(IEnumerable<UpdateChecker.Asset> assets, bool standalone) =>
        assets.FirstOrDefault(a => a.Name.EndsWith(standalone ? "-windows-x64-standalone.zip" : "-windows-x64.zip", StringComparison.OrdinalIgnoreCase));

    /// <summary>Why an automatic update is not possible, or null if it is.</summary>
    public string? WhyNot(UpdateInfo info)
    {
        if (host is null) return "Automatic updates are available in the Windows desktop app.";
        if (!info.Available) return "No newer version.";
        if (PickAsset(info.Assets, IsStandalone) is not { Digest: not null }) return "The release has no matching download with a checksum.";
        if (!CanWrite(Path.GetDirectoryName(host.ExePath)!)) return "ScopeDeck's folder is not writable; update it manually.";
        return null;
    }

    public UpdateProgress Start()
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false }) return _progress;
            _progress = new("starting", 0, null);
            _running = Task.Run(RunAsync);
            return _progress;
        }
    }

    private void Report(string phase, int percent, string? error = null)
    {
        lock (_lock) _progress = new(phase, percent, error);
    }

    private async Task RunAsync()
    {
        try
        {
            var info = await checker.GetAsync();
            if (WhyNot(info) is { } reason) throw new InvalidOperationException(reason);
            var asset = PickAsset(info.Assets, IsStandalone)!;

            Directory.CreateDirectory(WorkDir);
            var zip = Path.Combine(WorkDir, asset.Name);
            await DownloadAsync(asset, zip);

            Report("verifying", 100);
            await using (var file = File.OpenRead(zip))
            {
                var hash = "sha256:" + Convert.ToHexStringLower(await SHA256.HashDataAsync(file));
                if (!string.Equals(hash, asset.Digest, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("The download does not match the published checksum.");
            }

            var unpacked = Path.Combine(WorkDir, info.Latest!);
            if (Directory.Exists(unpacked)) Directory.Delete(unpacked, recursive: true);
            ZipFile.ExtractToDirectory(zip, unpacked);
            var newExe = Path.Combine(unpacked, Path.GetFileName(host!.ExePath));
            if (!File.Exists(newExe)) throw new InvalidDataException($"The download does not contain {Path.GetFileName(host.ExePath)}.");

            Report("installing", 100);
            Install(unpacked, host.ExePath);
            // Leftovers are harmless; the update itself has succeeded at this point.
            try
            {
                File.Delete(zip);
                Directory.Delete(unpacked, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }

            Report("restarting", 100);
            logger.LogInformation("Updated to {Version}; restarting.", info.Latest);
            host.RestartInto(host.ExePath);
        }
        catch (Exception e)
        {
            logger.LogWarning(e, "Update failed.");
            Report("failed", 0, e.Message);
        }
    }

    private async Task DownloadAsync(UpdateChecker.Asset asset, string path)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"ScopeDeck/{UpdateChecker.CurrentVersion}");
        using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        long total = response.Content.Headers.ContentLength ?? asset.Size;
        await using var source = await response.Content.ReadAsStreamAsync();
        await using var target = File.Create(path);
        var buffer = new byte[1 << 16];
        long done = 0;
        int read;
        while ((read = await source.ReadAsync(buffer)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read));
            done += read;
            Report("downloading", total > 0 ? (int)(done * 100 / total) : 0);
        }
    }

    /// <summary>Replaces the running exe and copies the other files, keeping an existing appsettings.json.</summary>
    internal static void Install(string from, string exePath)
    {
        var dir = Path.GetDirectoryName(exePath)!;
        var old = exePath + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(exePath, old);
        try
        {
            File.Copy(Path.Combine(from, Path.GetFileName(exePath)), exePath);
            foreach (var file in Directory.GetFiles(from))
            {
                var name = Path.GetFileName(file);
                var dest = Path.Combine(dir, name);
                if (dest.Equals(exePath, StringComparison.OrdinalIgnoreCase)) continue;
                if (name.Equals("appsettings.json", StringComparison.OrdinalIgnoreCase) && File.Exists(dest)) continue;
                File.Copy(file, dest, overwrite: true);
            }
        }
        catch
        {
            // Put the running version back so the app still starts next time.
            if (File.Exists(exePath)) File.Delete(exePath);
            File.Move(old, exePath);
            throw;
        }
    }

    /// <summary>Deletes the previous version left behind by an update (it was still running then).</summary>
    public static void CleanUp(string exePath)
    {
        try
        {
            if (File.Exists(exePath + ".old")) File.Delete(exePath + ".old");
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static bool CanWrite(string dir)
    {
        try
        {
            var probe = Path.Combine(dir, $".write-test-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
