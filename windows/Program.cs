using System.Diagnostics;

namespace BedReach;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        using var mutex = new Mutex(true, @"Local\BedReach.SingleInstance", out bool first);
        if (!first) return; // already running in the tray

        // Pairing secrets are encrypted with DPAPI so only this Windows user can read them.
        Storage.Protect = b => System.Security.Cryptography.ProtectedData.Protect(b, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);
        Storage.Unprotect = b => System.Security.Cryptography.ProtectedData.Unprotect(b, null, System.Security.Cryptography.DataProtectionScope.CurrentUser);

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApp(background: args.Contains("--background")));
    }
}

internal sealed class TrayApp : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly Control _ui = new();
    private readonly RemoteServer _server;
    private readonly AppConfig _cfg;
    private readonly ToolStripMenuItem _status = new() { Enabled = false };
    private readonly ToolStripMenuItem _autostart;

    public TrayApp(bool background)
    {
        _ui.CreateControl();
        if (!TestMode.On) Legacy.Migrate();
        _cfg = Storage.LoadConfig();

        bool firstRun = !TestMode.On && !File.Exists(Path.Combine(Storage.Dir, ".firstrun-done"));
        if (firstRun)
        {
            AutoStart.Set(true); // the whole point is that it's there when the PC turns on
            File.WriteAllText(Path.Combine(Storage.Dir, ".firstrun-done"), DateTime.Now.ToString("O"));
        }
        if (!TestMode.On) AutoStart.Refresh();

        _autostart = new ToolStripMenuItem("Start with Windows") { Checked = AutoStart.IsEnabled, CheckOnClick = true };
        _autostart.CheckedChanged += (_, _) => AutoStart.Set(_autostart.Checked);

        var menu = new ContextMenuStrip { ShowImageMargin = false, Font = new Font("Segoe UI", 10f) };
        menu.Items.Add(new ToolStripMenuItem($"BedReach · {_cfg.Name}") { Enabled = false, Font = new Font("Segoe UI Semibold", 10f) });
        menu.Items.Add(_status);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Pair a phone…", null, (_, _) => PairInfoWindow.Show(_cfg));
        menu.Items.Add("Paired phones…", null, (_, _) => DevicesWindow.Show(UpdateStatus));
        menu.Items.Add(_autostart);
        menu.Items.Add("Edit app shortcuts (restart needed)…", null, (_, _) =>
            Process.Start(new ProcessStartInfo("notepad.exe", $"\"{Storage.ConfigPath}\"")));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        _tray = new NotifyIcon { Icon = AppIcon.Create(32), ContextMenuStrip = menu, Visible = true, Text = "BedReach" };
        _tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) PairInfoWindow.Show(_cfg); };

        _server = new RemoteServer(_cfg, new WindowsPlatform(), ConfirmPairAsync);
        _server.StatusChanged += () => _ui.BeginInvoke(UpdateStatus);
        _server.Start();
        UpdateStatus();

        if (!TestMode.On && (firstRun || !background))
        {
            _tray.ShowBalloonTip(5000, "BedReach is running",
                firstRun ? "Open BedReach on your phone to pair. It will start with Windows from now on."
                         : "It lives here in the tray. Click it to pair a phone.", ToolTipIcon.Info);
            if (firstRun) PairInfoWindow.Show(_cfg);
        }
    }

    private void UpdateStatus()
    {
        int n = _server.ConnectedCount;
        int paired = Storage.LoadDevices().Count;
        string s = _server.LastError ?? (n > 0 ? $"● {n} phone{(n == 1 ? "" : "s")} connected"
                                              : paired == 0 ? "No phones paired yet" : "Waiting for your phone");
        _status.Text = s;
        _tray.Text = ("BedReach – " + s).Length > 63 ? "BedReach" : "BedReach – " + s;
    }

    private Task<bool> ConfirmPairAsync(string device, string code)
    {
        if (TestMode.On)
        {
            File.WriteAllText(Path.Combine(Storage.Dir, "pair-code.txt"), code);
            return Task.FromResult(device != "DENY-ME");
        }
        var tcs = new TaskCompletionSource<bool>();
        _ui.BeginInvoke(() =>
        {
            System.Media.SystemSounds.Asterisk.Play();
            using var dlg = new PairDialog(device, code);
            tcs.TrySetResult(dlg.ShowDialog() == DialogResult.Yes);
        });
        return tcs.Task;
    }

    private void Quit()
    {
        _server.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        ExitThread();
    }
}
