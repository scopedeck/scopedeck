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

        // The web server runs inside this process on a free loopback port, private to this window.
        var app = SiglentServer.Build(args, builder => builder.WebHost.UseUrls("http://127.0.0.1:0"));
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
        Application.Run(new MainForm(new Uri(url)));

        // Disposes the scope connection so the next session (or EasyScopeX) can connect.
        app.StopAsync().GetAwaiter().GetResult();
        app.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
