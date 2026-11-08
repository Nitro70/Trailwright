using System.Net;
using System.Net.Sockets;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Trailwright.App;

static class Program
{
    /// <summary>
    /// Trailwright.exe              the app window
    /// Trailwright.exe --headless   no window: UI server + AI port only (for testing), --ui-port N, --ai on
    /// </summary>
    [STAThread]
    static int Main(string[] args)
    {
        bool headless = args.Contains("--headless");
        int uiPort = ArgInt(args, "--ui-port") ?? FreePort();
        var settings = Settings.Load();
        settings.Transient = headless || args.Any(a => a is "--ai" or "--ai-port" or "--folder" or "--selftest");
        if (ArgValue(args, "--ai") == "on") settings.AiEnabled = true;
        if (ArgInt(args, "--ai-port") is int ap) settings.AiPort = ap;
        if (ArgValue(args, "--folder") is { } folder) settings.TrailmakersFolder = folder;
        var state = new AppState(settings);
        if (ArgValue(args, "--open") is { } openPath)
            state.Editor.Replace(state.Library.Open(openPath), Path.GetFullPath(openPath));
        var ui = UiServer.Start(state, uiPort).GetAwaiter().GetResult();
        if (settings.AiEnabled) state.Mcp.SetEnabled(true).GetAwaiter().GetResult();

        if (headless)
        {
            Console.WriteLine($"Trailwright UI on http://127.0.0.1:{uiPort}/  AI port: {state.Mcp.Url ?? "off"} {state.Mcp.Error}");
            var done = new ManualResetEventSlim();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
            done.Wait();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        var form = new MainForm(state, $"http://127.0.0.1:{uiPort}/", selfTest: args.Contains("--selftest"));
        Application.Run(form);
        state.Mcp.SetEnabled(false).GetAwaiter().GetResult();
        ui.StopAsync().GetAwaiter().GetResult();
        if (form.SelfTestResult != null)
        {
            var report = Path.Combine(AppContext.BaseDirectory, "selftest.txt");
            File.WriteAllText(report, form.SelfTestResult);
            return form.SelfTestResult.StartsWith("PASS") ? 0 : 1;
        }
        return 0;
    }

    static string? ArgValue(string[] a, string name) { int i = Array.IndexOf(a, name); return i >= 0 && i + 1 < a.Length ? a[i + 1] : null; }
    static int? ArgInt(string[] a, string name) => int.TryParse(ArgValue(a, name), out var v) ? v : null;

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }
}

sealed class MainForm : Form
{
    readonly AppState _state;
    readonly bool _selfTest;
    public string? SelfTestResult { get; private set; }
    protected override bool ShowWithoutActivation => _selfTest; // the self-test never takes focus

    public MainForm(AppState state, string url, bool selfTest = false)
    {
        _state = state;
        _selfTest = selfTest;
        Text = "Trailwright";
        Width = 1500;
        Height = 920;
        StartPosition = FormStartPosition.CenterScreen;
        if (selfTest)
        {
            // the real window and WebView2, but off-screen and out of the taskbar (no popup)
            StartPosition = FormStartPosition.Manual;
            Location = new Point(-32000, -32000);
            ShowInTaskbar = false;
            Opacity = 0;
        }
        BackColor = Color.FromArgb(0x16, 0x18, 0x1d);
        try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { }
        var web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = BackColor };
        Controls.Add(web);
        Load += async (_, _) =>
        {
            try
            {
                var env = await CoreWebView2Environment.CreateAsync(null,
                    Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Trailwright", "WebView2"));
                await web.EnsureCoreWebView2Async(env);
                web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                web.CoreWebView2.Settings.IsStatusBarEnabled = false;
                web.CoreWebView2.NewWindowRequested += (_, e) => e.Handled = true;
                if (selfTest)
                {
                    web.CoreWebView2.NavigationCompleted += async (_, e) =>
                    {
                        string result = "FAIL navigation " + e.WebErrorStatus;
                        if (e.IsSuccess)
                        {
                            for (int i = 0; i < 60; i++)
                            {
                                await Task.Delay(250);
                                var js = await web.CoreWebView2.ExecuteScriptAsync(
                                    "JSON.stringify({parts: document.querySelectorAll('.part').length, swatches: document.querySelectorAll('#swatches i').length, gl: !!document.getElementById('view').getContext('webgl2'), title: document.title, build: document.getElementById('build').textContent, blocks: window.__tw ? window.__tw.S.scene.blocks.length : -1, modelGroups: window.__tw ? window.__tw.models.length : -1, boxes: window.__tw && window.__tw.boxes ? window.__tw.boxes.count : 0, errors: window.__twErrors})");
                                if (js.Contains("\\\"parts\\\":0") || (i < 40 && js.Contains("\\\"modelGroups\\\":0") && !js.Contains("\\\"blocks\\\":0"))) continue;
                                result = (js.Contains("\\\"gl\\\":true") && js.Contains("\\\"errors\\\":[]") ? "PASS " : "FAIL ") + js;
                                break;
                            }
                        }
                        SelfTestResult = result;
                        _state.Editor.MarkSaved();
                        Close();
                    };
                }
                web.CoreWebView2.Navigate(url);
            }
            catch (Exception ex)
            {
                if (selfTest) { SelfTestResult = "FAIL " + ex.Message; Close(); return; }
                MessageBox.Show(this, "Trailwright needs the Microsoft Edge WebView2 Runtime.\n\n" + ex.Message, "Trailwright", MessageBoxButtons.OK, MessageBoxIcon.Error);
                Close();
            }
        };
        FormClosing += (_, e) =>
        {
            if (_state.Editor.Dirty &&
                MessageBox.Show(this, "The build has unsaved changes. Close anyway?", "Trailwright", MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                e.Cancel = true;
        };
    }
}
