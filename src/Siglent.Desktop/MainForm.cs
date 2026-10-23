using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Siglent.Desktop;

/// <summary>A window that shows the SDS Remote UI in WebView2 (the Edge engine built into Windows).</summary>
internal sealed class MainForm : Form
{
    private static readonly string DataFolder =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SDS Remote");

    private readonly WebView2 _web;
    private readonly Uri _url;

    public MainForm(Uri url)
    {
        _url = url;
        Text = "SDS Remote";
        Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
        BackColor = Color.FromArgb(13, 16, 20);
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(1440, 900);
        MinimumSize = new Size(900, 600);

        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(_web);
        Load += async (_, _) => await InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        try
        {
            // Keep browser data (e.g. the last scope address) in %LocalAppData%, not next to the exe.
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: Path.Combine(DataFolder, "WebView2"));
            await _web.EnsureCoreWebView2Async(env);
        }
        catch (WebView2RuntimeNotFoundException)
        {
            MessageBox.Show(this,
                "The Microsoft Edge WebView2 Runtime is required. It is part of Windows 11; on Windows 10 install it from https://go.microsoft.com/fwlink/p/?LinkId=2124703",
                "SDS Remote", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Close();
            return;
        }

        var core = _web.CoreWebView2;
        core.Settings.IsStatusBarEnabled = false;
        core.Settings.IsZoomControlEnabled = true;
        core.DocumentTitleChanged += (_, _) => Text = core.DocumentTitle;
        // External links open in the default browser instead of a new WebView window.
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(e.Uri) { UseShellExecute = true });
        };
        _web.Source = _url;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // Dark title bar to match the UI (Windows 10 20H1+ / Windows 11).
        int dark = 1;
        _ = DwmSetWindowAttribute(Handle, DwmwaUseImmersiveDarkMode, ref dark, sizeof(int));
    }

    private const int DwmwaUseImmersiveDarkMode = 20;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
