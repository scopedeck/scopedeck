using System.Globalization;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.FileProviders;
using Siglent.Scope;
using Siglent.Simulator;

namespace Siglent.Server;

/// <summary>Builds the web app; used by the console host (Program.cs) and the desktop window.</summary>
public static class SiglentServer
{
    public static WebApplication Build(string[] args, Action<WebApplicationBuilder>? configure = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            // Lets the desktop exe find appsettings.json next to itself regardless of the working directory.
            ContentRootPath = AppContext.BaseDirectory,
        });
        builder.Services.AddSingleton<ScopeService>();
        builder.Services.AddSingleton<UserSettings>();
        builder.Services.AddSingleton<UpdateChecker>();
        builder.Services.AddProblemDetails();
        configure?.Invoke(builder);

        // --simulate starts a fake SDS1202X-E on 127.0.0.1:5025 for working without hardware.
        FakeScopeServer? simulator = builder.Configuration.GetValue<bool>("simulate") ? new FakeScopeServer() : null;
        string defaultHost = simulator is not null ? "127.0.0.1" : builder.Configuration["Scope:Host"] ?? "";

        var app = builder.Build();

        // The UI is embedded in this assembly (see Siglent.Server.csproj), so the exe is self-contained.
        var ui = new ManifestEmbeddedFileProvider(typeof(SiglentServer).Assembly, "wwwroot");
        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = ui });
        app.UseStaticFiles(new StaticFileOptions { FileProvider = ui });

        MapApi(app, defaultHost, simulated: simulator is not null);

        if (simulator is not null)
            app.Lifetime.ApplicationStopping.Register(() => simulator.DisposeAsync().AsTask().Wait());
        return app;
    }

    private static void MapApi(WebApplication app, string defaultHost, bool simulated)
    {
        var api = app.MapGroup("/api").AddEndpointFilter(async (context, next) =>
        {
            try
            {
                return await next(context);
            }
            catch (ScopeNotConnectedException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
            }
            catch (Exception e) when (e is IOException or SocketException or TimeoutException or InvalidDataException)
            {
                return Results.Problem($"Oscilloscope communication failed: {e.Message}", statusCode: StatusCodes.Status503ServiceUnavailable);
            }
            catch (Exception e) when (e is ArgumentException or FormatException)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status400BadRequest);
            }
            catch (InvalidOperationException e)
            {
                return Results.Problem(e.Message, statusCode: StatusCodes.Status409Conflict);
            }
        });

        api.MapGet("/update", (UpdateChecker updates) => updates.GetAsync());

        api.MapGet("/status", (ScopeService scope, UserSettings settings) => new
        {
            Version = UpdateChecker.CurrentVersion,
            Connected = scope.Identity is not null,
            // The simulator always uses its own address; otherwise prefer the last address that worked.
            Host = scope.Host ?? (simulated ? defaultHost : settings.LastHost ?? defaultHost),
            scope.Port,
            scope.Identity,
            ChannelCount = scope.Identity?.ChannelCount,
        });

        api.MapPost("/connect", async (ConnectRequest request, ScopeService scope, UserSettings settings, CancellationToken ct) =>
        {
            var identity = await scope.ConnectAsync(request.Host.Trim(), request.Port ?? ScpiConnection.DefaultPort, ct);
            if (!simulated) settings.LastHost = request.Host.Trim();
            return identity;
        });

        api.MapPost("/disconnect", async (ScopeService scope) =>
        {
            await scope.DisconnectAsync();
            return Results.NoContent();
        });

        api.MapGet("/settings", (ScopeService scope, CancellationToken ct) =>
            scope.RunAsync(s => s.GetSettingsAsync(ct)));

        api.MapGet("/frame", (ScopeService scope, int? maxPoints, double? start, double? end, CancellationToken ct) =>
            scope.RunAsync(s => s.GetFrameAsync(Math.Clamp(maxPoints ?? 2000, 100, 100_000), start ?? 0, end ?? 1, ct)));

        api.MapGet("/measurements/{channel:int}", (int channel, ScopeService scope, CancellationToken ct) =>
            scope.RunAsync(s => s.GetMeasurementsAsync(channel, ct)));

        api.MapGet("/capture", async (ScopeService scope, CancellationToken ct) =>
        {
            var frame = await scope.RunAsync(s => s.CaptureMemoryAsync(ct));
            if (frame.Channels.Count == 0)
                return Results.Problem("No enabled channel has data.", statusCode: StatusCodes.Status409Conflict);
            var name = $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.csv";
            return Results.Stream(stream => WriteCsvAsync(frame, stream), "text/csv", name);
        });

        api.MapGet("/screenshot", async (ScopeService scope, CancellationToken ct) =>
            Results.File(await scope.RunAsync(s => s.GetScreenshotAsync(ct)), "image/bmp", "screenshot.bmp"));

        api.MapPost("/channel/{channel:int}", async (int channel, ChannelUpdate update, ScopeService scope, CancellationToken ct) =>
        {
            await scope.RunAsync(s => s.UpdateChannelAsync(channel, update, ct));
            return Results.NoContent();
        });

        api.MapPost("/timebase", async (TimebaseUpdate update, ScopeService scope, CancellationToken ct) =>
        {
            await scope.RunAsync(s => s.UpdateTimebaseAsync(update, ct));
            return Results.NoContent();
        });

        api.MapPost("/trigger", async (TriggerUpdate update, ScopeService scope, CancellationToken ct) =>
        {
            await scope.RunAsync(s => s.UpdateTriggerAsync(update, ct));
            return Results.NoContent();
        });

        api.MapPost("/autosetup", async (ScopeService scope, CancellationToken ct) =>
        {
            await scope.RunAsync(s => s.AutoSetupAsync(ct));
            return Results.NoContent();
        });

        api.MapPost("/acquisition", async (AcquisitionUpdate update, ScopeService scope, CancellationToken ct) =>
        {
            await scope.RunAsync(s => s.UpdateAcquisitionAsync(update, ct));
            return Results.NoContent();
        });

        api.MapPost("/trigger/set50", async (ScopeService scope, CancellationToken ct) =>
        {
            await scope.RunAsync(s => s.SetTriggerLevelTo50PercentAsync(ct));
            return Results.NoContent();
        });

        // ---------- history ----------

        api.MapPost("/history", (HistoryRequest request, ScopeService scope, CancellationToken ct) =>
            scope.RunAsync(s => s.SetHistoryAsync(request.Enabled, ct)));

        api.MapPost("/history/frame", (HistoryFrameRequest request, ScopeService scope, CancellationToken ct) =>
            scope.RunAsync(s => s.SetHistoryFrameAsync(request.Frame, ct)));

        api.MapGet("/history/export", (ScopeService scope, int? maxPoints, CancellationToken ct) =>
            Results.Stream(async stream =>
            {
                await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1 << 16);
                bool header = true;
                await scope.RunAsync(s => s.ExportHistoryAsync(Math.Clamp(maxPoints ?? 2000, 100, 100_000), async (index, time, frame) =>
                {
                    if (header)
                    {
                        await writer.WriteAsync("frame,frame_time,time_s," + string.Join(',', frame.Channels.Select(c => $"C{c.Channel}_V")) + "\n");
                        header = false;
                    }
                    await WriteRowsAsync(writer, frame, $"{index},{time},");
                }, ct));
            }, "text/csv", $"history-{DateTime.Now:yyyyMMdd-HHmmss}.csv"));

        // ---------- setups ----------

        api.MapGet("/setup", async (ScopeService scope, CancellationToken ct) =>
        {
            var setup = await scope.RunAsync(s => s.GetSetupAsync(ct));
            bool xml = setup.AsSpan().StartsWith("<?xml"u8);
            return Results.File(setup, xml ? "application/xml" : "application/octet-stream",
                $"scope-setup-{DateTime.Now:yyyyMMdd-HHmmss}.{(xml ? "xml" : "bin")}");
        });

        api.MapPost("/setup", async (HttpRequest request, ScopeService scope, CancellationToken ct) =>
        {
            using var body = new MemoryStream();
            await request.Body.CopyToAsync(body, ct);
            await scope.RunAsync(s => s.LoadSetupAsync(body.ToArray(), ct));
            return Results.NoContent();
        });

        api.MapPost("/setup/memory/{slot:int}/save", async (int slot, ScopeService scope, CancellationToken ct) =>
        {
            await scope.RunAsync(s => s.SaveSetupToMemoryAsync(slot, ct));
            return Results.NoContent();
        });

        api.MapPost("/setup/memory/{slot:int}/recall", async (int slot, ScopeService scope, CancellationToken ct) =>
        {
            await scope.RunAsync(s => s.RecallSetupFromMemoryAsync(slot, ct));
            return Results.NoContent();
        });

        api.MapPost("/scpi", async (ScpiRequest request, ScopeService scope, CancellationToken ct) =>
            new { Response = await scope.RunAsync(s => s.SendRawAsync(request.Command.Trim(), ct)) });
    }

    /// <summary>Writes "time_s,C1_V,C2_V" rows; millions of rows, so values are formatted without allocations.</summary>
    internal static async Task WriteCsvAsync(WaveformFrame frame, Stream stream)
    {
        await using var writer = new StreamWriter(stream, new UTF8Encoding(false), bufferSize: 1 << 20);
        await writer.WriteAsync("time_s," + string.Join(',', frame.Channels.Select(c => $"C{c.Channel}_V")) + "\n");
        await WriteRowsAsync(writer, frame, "");
    }

    /// <summary>Writes one "prefix,time,C1,C2" row per sample.</summary>
    private static async Task WriteRowsAsync(StreamWriter writer, WaveformFrame frame, string prefix)
    {
        if (frame.Channels.Count == 0) return;
        var first = frame.Channels[0];
        int rows = frame.Channels.Max(c => c.Volts.Length);
        var line = new char[prefix.Length + 32 * (frame.Channels.Count + 1)];
        prefix.CopyTo(line);
        var inv = CultureInfo.InvariantCulture;
        for (int i = 0; i < rows; i++)
        {
            int n = prefix.Length;
            (first.FirstPointTime + i * first.SampleInterval).TryFormat(line.AsSpan(n), out int w, "E8", inv);
            n += w;
            foreach (var c in frame.Channels)
            {
                line[n++] = ',';
                if (i < c.Volts.Length)
                {
                    c.Volts[i].TryFormat(line.AsSpan(n), out w, "G5", inv);
                    n += w;
                }
            }
            line[n++] = '\n';
            await writer.WriteAsync(line.AsMemory(0, n));
        }
    }
}

public record ConnectRequest(string Host, int? Port);
public record ScpiRequest(string Command);
public record HistoryRequest(bool Enabled);
public record HistoryFrameRequest(int Frame);
