namespace BedReach;

/// <summary>
/// Linux: keyboard/mouse via xdotool (X11) or ydotool (Wayland); media via playerctl;
/// volume via pactl or wpctl; apps via xdg-open; power via systemd/logind.
/// </summary>
internal sealed class LinuxPlatform : IPlatform
{
    private readonly bool _wayland =
        Environment.GetEnvironmentVariable("XDG_SESSION_TYPE") == "wayland" ||
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
    private readonly DelayedShutdown _shutdown = new();

    private enum Injector { None, Xdotool, Ydotool }

    private Injector Inject =>
        !_wayland && Shell.Has("xdotool") ? Injector.Xdotool :
        Shell.Has("ydotool") ? Injector.Ydotool :
        Shell.Has("xdotool") ? Injector.Xdotool : // XWayland apps still work with xdotool
        Injector.None;

    public string Describe()
    {
        var missing = new List<string>();
        if (Inject == Injector.None) missing.Add(_wayland ? "ydotool (keyboard and mouse on Wayland)" : "xdotool (keyboard and mouse)");
        if (!Shell.Has("playerctl")) missing.Add("playerctl (play/pause for media apps)");
        if (!Shell.Has("pactl") && !Shell.Has("wpctl")) missing.Add("pactl or wpctl (volume)");
        return missing.Count == 0 ? $"input: {Inject}, session: {(_wayland ? "Wayland" : "X11")}" : "Missing optional tools: " + string.Join(", ", missing);
    }

    // ---- keys ----
    private static readonly Dictionary<string, string> XKeys = new()
    {
        ["space"] = "space", ["enter"] = "Return", ["esc"] = "Escape", ["backspace"] = "BackSpace", ["tab"] = "Tab",
        ["left"] = "Left", ["up"] = "Up", ["right"] = "Right", ["down"] = "Down",
        ["pageup"] = "Prior", ["pagedown"] = "Next", ["home"] = "Home", ["end"] = "End",
        ["f"] = "f", ["m"] = "m", ["k"] = "k", ["j"] = "j", ["l"] = "l", ["c"] = "c", ["s"] = "s",
        ["f11"] = "F11", ["f5"] = "F5",
        ["playpause"] = "XF86AudioPlay", ["next"] = "XF86AudioNext", ["prev"] = "XF86AudioPrev", ["stop"] = "XF86AudioStop",
        ["volup"] = "XF86AudioRaiseVolume", ["voldown"] = "XF86AudioLowerVolume", ["mute"] = "XF86AudioMute",
        ["alttab"] = "alt+Tab", ["alt_f4"] = "alt+F4", ["browser_back"] = "alt+Left", ["shift_n"] = "shift+n", ["win_d"] = "super+d",
    };

    // Linux input-event key codes for ydotool (from linux/input-event-codes.h).
    private static readonly Dictionary<string, int[]> YKeys = new()
    {
        ["space"] = [57], ["enter"] = [28], ["esc"] = [1], ["backspace"] = [14], ["tab"] = [15],
        ["left"] = [105], ["up"] = [103], ["right"] = [106], ["down"] = [108],
        ["pageup"] = [104], ["pagedown"] = [109], ["home"] = [102], ["end"] = [107],
        ["f"] = [33], ["m"] = [50], ["k"] = [37], ["j"] = [36], ["l"] = [38], ["c"] = [46], ["s"] = [31],
        ["f11"] = [87], ["f5"] = [63],
        ["playpause"] = [164], ["next"] = [163], ["prev"] = [165], ["stop"] = [166],
        ["volup"] = [115], ["voldown"] = [114], ["mute"] = [113],
        ["alttab"] = [56, 15], ["alt_f4"] = [56, 62], ["browser_back"] = [56, 105], ["shift_n"] = [42, 49], ["win_d"] = [125, 32],
    };

    public bool PressKey(string name)
    {
        // Media and volume work best through the desktop's own services (and work on Wayland without ydotool).
        if (name is "playpause" or "next" or "prev" or "stop" && Shell.Has("playerctl"))
        {
            Shell.Fire("playerctl", name switch { "playpause" => "play-pause", "prev" => "previous", _ => name });
            return true;
        }
        if (name is "volup" or "voldown" or "mute")
        {
            if (Shell.Has("pactl"))
            {
                if (name == "mute") Shell.Fire("pactl", "set-sink-mute", "@DEFAULT_SINK@", "toggle");
                else Shell.Fire("pactl", "set-sink-volume", "@DEFAULT_SINK@", name == "volup" ? "+5%" : "-5%");
                return true;
            }
            if (Shell.Has("wpctl"))
            {
                if (name == "mute") Shell.Fire("wpctl", "set-mute", "@DEFAULT_AUDIO_SINK@", "toggle");
                else Shell.Fire("wpctl", "set-volume", "-l", "1.0", "@DEFAULT_AUDIO_SINK@", name == "volup" ? "5%+" : "5%-");
                return true;
            }
        }

        switch (Inject)
        {
            case Injector.Xdotool when XKeys.TryGetValue(name, out var ks):
                Shell.Fire("xdotool", "key", "--clearmodifiers", ks);
                return true;
            case Injector.Ydotool when YKeys.TryGetValue(name, out var codes):
                var seq = codes.Select(c => $"{c}:1").Concat(codes.Reverse().Select(c => $"{c}:0")).ToArray();
                Shell.Fire("ydotool", ["key", .. seq]);
                return true;
            case Injector.None:
                throw new InvalidOperationException(_wayland ? "install ydotool" : "install xdotool");
        }
        return false;
    }

    public void TypeText(string text)
    {
        if (text.Length == 0) return;
        switch (Inject)
        {
            case Injector.Xdotool: Shell.Fire("xdotool", "type", "--delay", "8", "--", text); break;
            case Injector.Ydotool: Shell.Fire("ydotool", "type", "--", text); break;
            default: throw new InvalidOperationException("install xdotool or ydotool");
        }
    }

    public void MoveMouse(int dx, int dy)
    {
        switch (Inject)
        {
            case Injector.Xdotool: Shell.Fire("xdotool", "mousemove_relative", "--", dx.ToString(), dy.ToString()); break;
            case Injector.Ydotool: Shell.Fire("ydotool", "mousemove", "-x", dx.ToString(), "-y", dy.ToString()); break;
        }
    }

    public void Click(string button)
    {
        switch (Inject)
        {
            case Injector.Xdotool: Shell.Fire("xdotool", "click", button switch { "right" => "3", "middle" => "2", _ => "1" }); break;
            case Injector.Ydotool: Shell.Fire("ydotool", "click", button switch { "right" => "0xC1", "middle" => "0xC2", _ => "0xC0" }); break;
        }
    }

    public void Scroll(int amount)
    {
        int notches = Math.Max(1, Math.Abs(amount) / 120);
        bool up = amount > 0;
        switch (Inject)
        {
            case Injector.Xdotool: Shell.Fire("xdotool", "click", "--repeat", notches.ToString(), up ? "4" : "5"); break;
            case Injector.Ydotool: Shell.Fire("ydotool", "mousemove", "--wheel", "-x", "0", "-y", (up ? notches : -notches).ToString()); break;
        }
    }

    public void Launch(ServiceEntry s)
    {
        // xdg-open reports failure (e.g. no handler for spotify:) through its exit code.
        _ = Task.Run(() =>
        {
            var (code, _) = Shell.Run("xdg-open", 8000, s.Target);
            if (code != 0 && code != -1 && !string.IsNullOrEmpty(s.Fallback)) Shell.Fire("xdg-open", s.Fallback!);
        });
    }

    public void Power(string action)
    {
        switch (action)
        {
            case "sleep": Shell.Fire("systemctl", "suspend"); break;
            case "lock":
                if (Shell.Has("loginctl")) Shell.Fire("loginctl", "lock-session");
                else Shell.Fire("xdg-screensaver", "lock");
                break;
            case "shutdown": _shutdown.Schedule(() => Shell.Fire("systemctl", "poweroff"), Notify); break;
            case "cancel": _shutdown.Cancel(); Notify("Shutdown cancelled."); break;
        }
    }

    public static void Notify(string msg)
    {
        Console.WriteLine(msg);
        if (Shell.Has("notify-send")) Shell.Fire("notify-send", "BedReach", msg);
    }

    // ---- installed apps: .desktop entries (system, user, Flatpak and Snap) ----
    private static IEnumerable<string> DesktopDirs()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        yield return Path.Combine(string.IsNullOrEmpty(dataHome) ? Path.Combine(home, ".local", "share") : dataHome, "applications");
        var dataDirs = Environment.GetEnvironmentVariable("XDG_DATA_DIRS");
        foreach (var d in (string.IsNullOrEmpty(dataDirs) ? "/usr/local/share:/usr/share" : dataDirs).Split(':'))
            if (d.Length > 0) yield return Path.Combine(d, "applications");
        yield return Path.Combine(home, ".local", "share", "flatpak", "exports", "share", "applications");
        yield return "/var/lib/flatpak/exports/share/applications";
        yield return "/var/lib/snapd/desktop/applications";
    }

    public string? FindApp(IReadOnlyList<string> names)
    {
        foreach (var dir in DesktopDirs().Distinct())
        {
            if (!Directory.Exists(dir)) continue;
            foreach (var file in Directory.EnumerateFiles(dir, "*.desktop"))
            {
                string? name = null;
                try
                {
                    foreach (var line in File.ReadLines(file))
                    {
                        if (line.StartsWith('[') && line != "[Desktop Entry]") break; // only the main section
                        if (line.StartsWith("Name=")) { name = line[5..].Trim(); break; }
                    }
                }
                catch { continue; }
                if (name != null && names.Any(n => n.Equals(name, StringComparison.OrdinalIgnoreCase))) return file;
            }
        }
        return null;
    }

    public void OpenApp(string desktopFile)
    {
        if (Shell.Has("gio")) Shell.Fire("gio", "launch", desktopFile);
        else if (Shell.Has("gtk-launch")) Shell.Fire("gtk-launch", Path.GetFileNameWithoutExtension(desktopFile));
        else if (Shell.Has("dex")) Shell.Fire("dex", desktopFile);
        else throw new InvalidOperationException("install gio (glib2) to open apps");
    }
}
