using System.Diagnostics;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Siglent.Server;

namespace Siglent.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        var exePath = Environment.ProcessPath!;
        UpdateInstaller.CleanUp(exePath);

        // Set by an installed update: the exe to start once this instance has shut down.
        string? restartInto = null;
        MainForm? form = null;
        var host = new DesktopHost(exePath, path =>
        {
            restartInto = path;
            form?.BeginInvoke(form.Close);
        });

        // The web server runs inside this process on a free loopback port, private to this window.
        var app = SiglentServer.Build(args, builder =>
        {
            builder.WebHost.UseUrls("http://127.0.0.1:0");
            builder.Services.AddSingleton(host);
        });
        try
        {
            app.StartAsync().GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            MessageBox.Show($"Could not start: {e.Message}", "ScopeDeck", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        var url = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        form = new MainForm(new Uri(url));
        Application.Run(form);

        // Disposes the scope connection so the next session (or EasyScopeX, or the updated version) can connect.
        app.StopAsync().GetAwaiter().GetResult();
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();

        if (restartInto is not null)
            Process.Start(new ProcessStartInfo(restartInto) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(restartInto) });
    }
}
