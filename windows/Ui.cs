using System.Drawing.Drawing2D;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BedReach;

internal static class Theme
{
    public static readonly Color Bg = Color.FromArgb(17, 17, 27);
    public static readonly Color Card = Color.FromArgb(30, 30, 46);
    public static readonly Color Text = Color.FromArgb(235, 235, 245);
    public static readonly Color Muted = Color.FromArgb(150, 150, 175);
    public static readonly Color Accent = Color.FromArgb(139, 92, 246);
    public static readonly Color Accent2 = Color.FromArgb(236, 72, 153);
    public static readonly Color Danger = Color.FromArgb(70, 70, 90);

    public static Form NewForm(string title, int w, int h)
    {
        var f = new Form
        {
            Text = title, BackColor = Bg, ForeColor = Text, FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false, MinimizeBox = false, StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(w, h), Font = new Font("Segoe UI", 10f), Icon = AppIcon.Create(32),
            AutoScaleMode = AutoScaleMode.Dpi,
        };
        return f;
    }

    public static Label Label(string text, float size = 10f, bool bold = false, Color? color = null) => new()
    {
        Text = text, AutoSize = false, ForeColor = color ?? Text, BackColor = Color.Transparent,
        Font = new Font("Segoe UI", size, bold ? FontStyle.Bold : FontStyle.Regular), TextAlign = ContentAlignment.MiddleCenter,
    };

    public static Button Button(string text, Color bg) => new()
    {
        Text = text, BackColor = bg, ForeColor = Color.White, FlatStyle = FlatStyle.Flat,
        Font = new Font("Segoe UI Semibold", 10.5f), Cursor = Cursors.Hand,
        FlatAppearance = { BorderSize = 0 },
    };
}

/// <summary>Draws the app icon (gradient rounded square with a play symbol) at any size.</summary>
internal static class AppIcon
{
    public static Bitmap Bitmap(int size)
    {
        var bmp = new Bitmap(size, size);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        float r = size * 0.24f;
        using var path = new GraphicsPath();
        path.AddArc(0, 0, r * 2, r * 2, 180, 90);
        path.AddArc(size - r * 2 - 1, 0, r * 2, r * 2, 270, 90);
        path.AddArc(size - r * 2 - 1, size - r * 2 - 1, r * 2, r * 2, 0, 90);
        path.AddArc(0, size - r * 2 - 1, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        using var brush = new LinearGradientBrush(new Point(0, 0), new Point(size, size), Theme.Accent, Theme.Accent2);
        g.FillPath(brush, path);
        float cx = size * 0.54f, cy = size * 0.5f, t = size * 0.22f;
        var tri = new[] { new PointF(cx - t * 0.8f, cy - t), new PointF(cx - t * 0.8f, cy + t), new PointF(cx + t, cy) };
        g.FillPolygon(Brushes.White, tri);
        return bmp;
    }

    public static Icon Create(int size) => Icon.FromHandle(Bitmap(size).GetHicon());
}

/// <summary>"Allow this phone?" popup with the 6-digit comparison code.</summary>
internal sealed class PairDialog : Form
{
    public PairDialog(string deviceName, string code)
    {
        var f = this;
        Text = "BedReach – pair phone"; BackColor = Theme.Bg; ForeColor = Theme.Text;
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen; ClientSize = new Size(420, 300);
        Font = new Font("Segoe UI", 10f); TopMost = true; Icon = AppIcon.Create(32); ShowInTaskbar = true;

        var title = Theme.Label($"“{deviceName}” wants to control this PC", 12f, true);
        title.SetBounds(20, 20, 380, 50);
        var hint = Theme.Label("Only allow it if your phone shows this same code:", 10f, false, Theme.Muted);
        hint.SetBounds(20, 72, 380, 24);
        var codeLbl = Theme.Label(code[..3] + " " + code[3..], 34f, true, Color.White);
        codeLbl.SetBounds(20, 104, 380, 80);
        var allow = Theme.Button("Allow", Theme.Accent);
        allow.SetBounds(220, 220, 180, 48);
        allow.Click += (_, _) => { DialogResult = DialogResult.Yes; Close(); };
        var deny = Theme.Button("Deny", Theme.Danger);
        deny.SetBounds(20, 220, 180, 48);
        deny.Click += (_, _) => { DialogResult = DialogResult.No; Close(); };
        Controls.AddRange([title, hint, codeLbl, allow, deny]);
        AcceptButton = null; CancelButton = deny; // Enter does nothing, so a stray keypress can't approve

        var timer = new System.Windows.Forms.Timer { Interval = 60_000 };
        timer.Tick += (_, _) => { timer.Stop(); DialogResult = DialogResult.No; Close(); };
        timer.Start();
        Shown += (_, _) => { Activate(); deny.Focus(); };
    }
}

/// <summary>How-to-pair window with addresses for manual entry.</summary>
internal static class PairInfoWindow
{
    public static void Show(AppConfig cfg)
    {
        var f = Theme.NewForm("BedReach – pair a phone", 440, 330);
        var t = Theme.Label("Pair your phone", 15f, true);
        t.SetBounds(20, 16, 400, 36);
        var steps = Theme.Label(
            "1. Connect your phone to the same Wi-Fi as this PC.\n" +
            "2. Open BedReach on your phone and tap this PC.\n" +
            "3. Check the code matches, then click Allow here.",
            10f, false, Theme.Muted);
        steps.TextAlign = ContentAlignment.TopLeft;
        steps.SetBounds(30, 64, 390, 80);
        var addrTitle = Theme.Label("Not showing up? Add it manually with:", 10f, true);
        addrTitle.TextAlign = ContentAlignment.TopLeft;
        addrTitle.SetBounds(30, 150, 390, 24);
        var addrs = LocalAddresses();
        var addr = Theme.Label(addrs.Count == 0 ? "(no network connection)" : string.Join("\n", addrs.Select(a => $"{a}   port {cfg.Port}")), 12f, false, Color.White);
        addr.TextAlign = ContentAlignment.TopLeft;
        addr.Font = new Font("Consolas", 12f);
        addr.SetBounds(30, 178, 390, 80);
        var ok = Theme.Button("Done", Theme.Accent);
        ok.SetBounds(300, 270, 120, 42);
        ok.Click += (_, _) => f.Close();
        f.Controls.AddRange([t, steps, addrTitle, addr, ok]);
        f.Show();
        f.Activate();
    }

    public static List<string> LocalAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback
                        && !n.Description.Contains("Virtual", StringComparison.OrdinalIgnoreCase)
                        && !n.Description.Contains("Hyper-V", StringComparison.OrdinalIgnoreCase))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
            .Select(a => a.Address.ToString()).Distinct().ToList();
}

/// <summary>List of paired phones with a Remove button.</summary>
internal static class DevicesWindow
{
    public static void Show(Action changed)
    {
        var f = Theme.NewForm("BedReach – paired phones", 460, 340);
        var list = new ListBox
        {
            BackColor = Theme.Card, ForeColor = Theme.Text, BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 11f), ItemHeight = 28, DrawMode = DrawMode.Normal,
        };
        list.SetBounds(20, 20, 420, 240);
        void Reload()
        {
            list.Items.Clear();
            foreach (var d in Storage.LoadDevices())
                list.Items.Add(new Item(d.Id, $"{d.Name}  ·  last used {(d.LastSeen?.ToString("d MMM HH:mm") ?? "never")}"));
            if (list.Items.Count == 0) list.Items.Add(new Item("", "No phones paired yet"));
        }
        Reload();
        var remove = Theme.Button("Remove selected", Theme.Danger);
        remove.SetBounds(20, 278, 180, 42);
        remove.Click += (_, _) =>
        {
            if (list.SelectedItem is Item { Id.Length: > 0 } it)
            {
                Storage.UpdateDevices(d => d.RemoveAll(x => x.Id == it.Id));
                Reload();
                changed();
            }
        };
        var close = Theme.Button("Close", Theme.Accent);
        close.SetBounds(320, 278, 120, 42);
        close.Click += (_, _) => f.Close();
        f.Controls.AddRange([list, remove, close]);
        f.Show();
        f.Activate();
    }

    private sealed record Item(string Id, string Label) { public override string ToString() => Label; }
}
