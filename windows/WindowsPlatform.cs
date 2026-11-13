using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BedReach;

/// <summary>
/// Windows implementation of IPlatform (SendInput). There is deliberately no
/// "run command" or "open arbitrary URL": the worst a stolen key could do is press media keys.
/// </summary>
public sealed class WindowsPlatform : IPlatform
{
    // Whitelisted keys the phone may press (name → virtual-key code).
    private static readonly Dictionary<string, ushort> Keys = new()
    {
        ["space"] = 0x20, ["enter"] = 0x0D, ["esc"] = 0x1B, ["backspace"] = 0x08, ["tab"] = 0x09,
        ["left"] = 0x25, ["up"] = 0x26, ["right"] = 0x27, ["down"] = 0x28,
        ["pageup"] = 0x21, ["pagedown"] = 0x22, ["home"] = 0x24, ["end"] = 0x23,
        ["f"] = 0x46, ["m"] = 0x4D, ["k"] = 0x4B, ["j"] = 0x4A, ["l"] = 0x4C, ["c"] = 0x43, ["s"] = 0x53,
        ["f11"] = 0x7A, ["f5"] = 0x74,
        ["playpause"] = 0xB3, ["next"] = 0xB0, ["prev"] = 0xB1, ["stop"] = 0xB2,
        ["volup"] = 0xAF, ["voldown"] = 0xAE, ["mute"] = 0xAD,
    };

    // Shortcuts made of several keys.
    private static readonly Dictionary<string, ushort[]> Combos = new()
    {
        ["alttab"] = [0x12, 0x09],      // switch window
        ["alt_f4"] = [0x12, 0x73],      // close window
        ["browser_back"] = [0x12, 0x25],// alt+left
        ["shift_n"] = [0x10, 0x4E],     // YouTube next video
        ["win_d"] = [0x5B, 0x44],       // show desktop
    };

    private static readonly HashSet<ushort> Extended = [0x21, 0x22, 0x23, 0x24, 0x25, 0x26, 0x27, 0x28, 0x5B];

    public bool PressKey(string name)
    {
        if (Keys.TryGetValue(name, out var vk)) { Tap(vk); return true; }
        if (Combos.TryGetValue(name, out var combo))
        {
            var inputs = new List<INPUT>();
            foreach (var k in combo) inputs.Add(Key(k, false));
            for (int i = combo.Length - 1; i >= 0; i--) inputs.Add(Key(combo[i], true));
            Send(inputs.ToArray());
            return true;
        }
        return false;
    }

    private static void Tap(ushort vk) => Send(Key(vk, false), Key(vk, true));

    public void TypeText(string text)
    {
        if (text.Length > 500) text = text[..500];
        var inputs = new List<INPUT>();
        foreach (char ch in text)
        {
            if (ch == '\n') { inputs.Add(Key(0x0D, false)); inputs.Add(Key(0x0D, true)); continue; }
            inputs.Add(Unicode(ch, false));
            inputs.Add(Unicode(ch, true));
        }
        if (inputs.Count > 0) Send(inputs.ToArray());
    }

    public void MoveMouse(int dx, int dy)
    {
        dx = Math.Clamp(dx, -500, 500); dy = Math.Clamp(dy, -500, 500);
        Send(Mouse(MOUSEEVENTF_MOVE, dx, dy, 0));
    }

    public void Click(string button)
    {
        (uint down, uint up) = button switch
        {
            "right" => (MOUSEEVENTF_RIGHTDOWN, MOUSEEVENTF_RIGHTUP),
            "middle" => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _ => (MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP),
        };
        Send(Mouse(down, 0, 0, 0), Mouse(up, 0, 0, 0));
    }

    public void Scroll(int amount) =>
        Send(Mouse(MOUSEEVENTF_WHEEL, 0, 0, Math.Clamp(amount, -1200, 1200)));

    public void Launch(ServiceEntry s)
    {
        try { Process.Start(new ProcessStartInfo(s.Target) { UseShellExecute = true }); }
        catch when (!string.IsNullOrEmpty(s.Fallback))
        {
            Process.Start(new ProcessStartInfo(s.Fallback!) { UseShellExecute = true });
        }
    }

    // ---- installed apps (the same list as the Start menu: Store apps, desktop apps and installed web apps) ----
    private static readonly object AppsLock = new();
    private static List<(string Name, string Id)> _apps = [];
    private static DateTime _appsAt;

    public string? FindApp(IReadOnlyList<string> names)
    {
        var apps = StartApps();
        foreach (var n in names)
            foreach (var a in apps)
                if (a.Name.Equals(n, StringComparison.OrdinalIgnoreCase)) return a.Id;
        return null;
    }

    public void OpenApp(string id)
    {
        if (File.Exists(id)) Process.Start(new ProcessStartInfo(id) { UseShellExecute = true });
        else Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + id) { UseShellExecute = false });
    }

    private static List<(string Name, string Id)> StartApps()
    {
        lock (AppsLock)
        {
            if (DateTime.UtcNow - _appsAt < TimeSpan.FromSeconds(30)) return _apps;
            var list = new List<(string, string)>();
            // Shell COM objects want a single-threaded apartment.
            var t = new Thread(() =>
            {
                try
                {
                    dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
                    dynamic folder = shell.NameSpace("shell:AppsFolder");
                    foreach (dynamic item in folder.Items())
                    {
                        string? name = item.Name, path = item.Path;
                        if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(path)) list.Add((name, path));
                    }
                }
                catch { /* leave the list empty: everything falls back to websites */ }
            });
            t.SetApartmentState(ApartmentState.STA);
            t.Start();
            t.Join(TimeSpan.FromSeconds(10));
            _apps = list;
            _appsAt = DateTime.UtcNow;
            return _apps;
        }
    }

    public void Power(string action)
    {
        switch (action)
        {
            case "sleep": SetSuspendState(false, false, false); break;
            case "lock": LockWorkStation(); break;
            case "shutdown": Run("shutdown", "/s /t 60 /c \"BedReach: shutting down in 60 seconds\""); break;
            case "cancel": Run("shutdown", "/a"); break;
        }
    }

    private static void Run(string exe, string args) =>
        Process.Start(new ProcessStartInfo(exe, args) { CreateNoWindow = true, UseShellExecute = false });

    // ---- Win32 ----
    private const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_UNICODE = 0x4;
    private const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4,
        MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_MIDDLEDOWN = 0x20,
        MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_WHEEL = 0x800;

    private static INPUT Key(ushort vk, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wVk = vk, dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (Extended.Contains(vk) ? KEYEVENTF_EXTENDEDKEY : 0) } }
    };

    private static INPUT Unicode(char ch, bool up) => new()
    {
        type = INPUT_KEYBOARD,
        u = new InputUnion { ki = new KEYBDINPUT { wScan = ch, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } }
    };

    private static INPUT Mouse(uint flags, int dx, int dy, int data) => new()
    {
        type = INPUT_MOUSE,
        u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = unchecked((uint)data), dwFlags = flags } }
    };

    private static void Send(params INPUT[] inputs) =>
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public InputUnion u; }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr dwExtraInfo; }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [DllImport("user32.dll")] private static extern bool LockWorkStation();
    [DllImport("powrprof.dll")] private static extern bool SetSuspendState(bool hibernate, bool force, bool disableWake);
}
