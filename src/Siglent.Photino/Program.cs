using System.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Photino.NET;
using Siglent.Server;

namespace Siglent.Photino;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // The web server runs inside this process on a free loopback port, private to this window.
        var app = SiglentServer.Build(args, builder => builder.WebHost.UseUrls("http://127.0.0.1:0"));
        NativeBridge.Map(app);
        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"ScopeDeck could not start: {e.Message}");
            Environment.Exit(1);
        }

        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var window = new PhotinoWindow()
            .SetLogVerbosity(0)
            .SetTitle("ScopeDeck")
            .SetUseOsDefaultSize(false)
            .SetSize(1440, 900)
            .SetMinSize(900, 600)
            .Center()
            .SetContextMenuEnabled(false)
            // Keeps the web view's cache next to the other app data instead of the working directory.
            .SetTemporaryFilesPath(Path.Combine(NativeBridge.DataFolder, "WebView"));
        var icon = Path.Combine(AppContext.BaseDirectory, "app.png");
        if (OperatingSystem.IsLinux() && File.Exists(icon)) window.SetIconFile(icon);
        window.Load(new Uri(url));
        window.WaitForClose();

        // Disposes the scope connection so the next session (or EasyScopeX) can connect.
        app.StopAsync().GetAwaiter().GetResult();
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

/// <summary>
/// What a browser does for the UI but the bare system web view does not: saving downloads (to the Downloads folder,
/// like a browser; Photino's GTK save dialog cannot suggest a file name) and opening links in the default browser.
/// Requests must carry the X-ScopeDeck header, which pages from other origins cannot send to this server (no CORS).
/// </summary>
internal static class NativeBridge
{
    public static readonly string DataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "ScopeDeck");

    private static readonly HttpClient Http = new() { Timeout = Timeout.InfiniteTimeSpan };

    public static void Map(WebApplication app)
    {
        var desktop = app.MapGroup("/desktop").AddEndpointFilter(async (context, next) =>
            context.HttpContext.Request.Headers.ContainsKey("X-ScopeDeck") ? await next(context) : Results.StatusCode(403));

        // A file made in the page (CSV, PNG, macros).
        desktop.MapPost("/save", async (HttpRequest request, string name, CancellationToken ct) =>
        {
            var temp = Path.GetTempFileName();
            await using (var file = File.Create(temp))
                await request.Body.CopyToAsync(file, ct);
            return Save(temp, name);
        });

        // A download from this server (capture, history export, setup), named by the server.
        desktop.MapPost("/save-url", async (SaveUrlRequest body, HttpRequest request, CancellationToken ct) =>
        {
            if (!body.Url.StartsWith("/api/", StringComparison.Ordinal)) return Results.BadRequest();
            using var response = await Http.GetAsync($"{request.Scheme}://{request.Host}{body.Url}", HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return Results.Problem(await response.Content.ReadAsStringAsync(ct), statusCode: (int)response.StatusCode);
            var temp = Path.GetTempFileName();
            await using (var file = File.Create(temp))
                await response.Content.CopyToAsync(file, ct);
            var name = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? body.Name ?? "download";
            return Save(temp, name);
        });

        desktop.MapPost("/log", async (HttpRequest request, ILogger<PhotinoWindow> log) =>
        {
            using var reader = new StreamReader(request.Body);
            log.LogWarning("Page: {Message}", await reader.ReadToEndAsync());
            return Results.NoContent();
        });

        desktop.MapPost("/open", (OpenRequest body) =>
        {
            if (!Uri.TryCreate(body.Url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                return Results.BadRequest();
            var opener = OperatingSystem.IsMacOS() ? "open" : OperatingSystem.IsLinux() ? "xdg-open" : null;
            Process.Start(opener is null
                ? new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }
                : new ProcessStartInfo(opener, [uri.AbsoluteUri]));
            return Results.NoContent();
        });
    }

    /// <summary>Moves the downloaded file into the Downloads folder, numbered like a browser does if the name is taken.</summary>
    private static IResult Save(string temp, string name)
    {
        try
        {
            name = string.Join("_", Path.GetFileName(name).Split(Path.GetInvalidFileNameChars()));
            if (name.Length == 0) name = "download";
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var folder = Path.Combine(home, "Downloads");
            Directory.CreateDirectory(folder);
            var path = Path.Combine(folder, name);
            for (int n = 1; File.Exists(path); n++)
                path = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(name)} ({n}){Path.GetExtension(name)}");
            File.Move(temp, path);
            // The temporary file was private to this user; a saved file gets the usual permissions.
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
            return Results.Ok(new SaveResult(path));
        }
        finally
        {
            if (File.Exists(temp)) File.Delete(temp);
        }
    }
}

internal sealed record SaveUrlRequest(string Url, string? Name);
internal sealed record OpenRequest(string Url);
/// <param name="Path">Where the file was saved.</param>
internal sealed record SaveResult(string? Path);
