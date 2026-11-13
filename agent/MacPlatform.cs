using System.Runtime.InteropServices;

namespace BedReach;

/// <summary>
/// macOS: keyboard/mouse via CoreGraphics CGEvents, media keys via NSEvent system-defined events,
/// volume via AppleScript, apps via `open`. Needs Accessibility permission
/// (System Settings → Privacy &amp; Security → Accessibility).
/// </summary>
internal sealed class MacPlatform : IPlatform
{
    private readonly DelayedShutdown _shutdown = new();

    // Virtual key codes (Carbon kVK_*).
    private static readonly Dictionary<string, (ushort Key, ulong Flags)> Keys = new()
    {
        ["space"] = (49, 0), ["enter"] = (36, 0), ["esc"] = (53, 0), ["backspace"] = (51, 0), ["tab"] = (48, 0),
        ["left"] = (123, 0), ["right"] = (124, 0), ["down"] = (125, 0), ["up"] = (126, 0),
        ["pageup"] = (116, 0), ["pagedown"] = (121, 0), ["home"] = (115, 0), ["end"] = (119, 0),
        ["f"] = (3, 0), ["m"] = (46, 0), ["k"] = (40, 0), ["j"] = (38, 0), ["l"] = (37, 0), ["c"] = (8, 0), ["s"] = (1, 0),
        ["f11"] = (3, Ctrl | Cmd),        // macOS full screen is ⌃⌘F
        ["f5"] = (15, Cmd),               // reload: ⌘R
        ["alttab"] = (48, Cmd),           // ⌘Tab
        ["alt_f4"] = (13, Cmd),           // close window: ⌘W
        ["browser_back"] = (33, Cmd),     // ⌘[
        ["shift_n"] = (45, Shift),
        ["win_d"] = (103, 0),             // F11 = Show Desktop
    };

    // NX_KEYTYPE_* for media keys.
    private static readonly Dictionary<string, int> MediaKeys = new() { ["playpause"] = 16, ["next"] = 17, ["prev"] = 18 };

    private const ulong Shift = 0x20000, Ctrl = 0x40000, Cmd = 0x100000;

    public static bool HasAccessibility()
    {
        try { return AXIsProcessTrusted(); } catch { return true; }
    }

    public bool PressKey(string name)
    {
        switch (name)
        {
            case "volup": Osa("set volume output volume ((output volume of (get volume settings)) + 6)"); return true;
            case "voldown": Osa("set volume output volume ((output volume of (get volume settings)) - 6)"); return true;
            case "mute": Osa("set volume output muted (not (output muted of (get volume settings)))"); return true;
            case "stop": name = "playpause"; break;
        }
        if (MediaKeys.TryGetValue(name, out var nx)) { MediaKey(nx); return true; }
        if (!Keys.TryGetValue(name, out var k)) return false;
        Key(k.Key, true, k.Flags);
        Key(k.Key, false, k.Flags);
        return true;
    }

    public void TypeText(string text)
    {
        foreach (var ch in text)
        {
            foreach (var down in new[] { true, false })
            {
                var ev = CGEventCreateKeyboardEvent(IntPtr.Zero, 0, down);
                CGEventKeyboardSetUnicodeString(ev, 1, [ch]);
                Post(ev);
            }
        }
    }

    public void MoveMouse(int dx, int dy)
    {
        var p = Cursor();
        var ev = CGEventCreateMouseEvent(IntPtr.Zero, 5 /* moved */, new CGPoint(p.X + dx, p.Y + dy), 0);
        Post(ev);
    }

    public void Click(string button)
    {
        var (down, up, btn) = button switch
        {
            "right" => (3u, 4u, 1u),
            "middle" => (25u, 26u, 2u),
            _ => (1u, 2u, 0u),
        };
        var p = Cursor();
        Post(CGEventCreateMouseEvent(IntPtr.Zero, down, p, btn));
        Post(CGEventCreateMouseEvent(IntPtr.Zero, up, p, btn));
    }

    public void Scroll(int amount)
    {
        int lines = Math.Max(1, Math.Abs(amount) / 120) * Math.Sign(amount);
        Post(CGEventCreateScrollWheelEvent2(IntPtr.Zero, 1 /* line */, 1, lines, 0, 0));
    }

    public void Launch(ServiceEntry s)
    {
        _ = Task.Run(() =>
        {
            var (code, _) = Shell.Run("open", 8000, s.Target);
            if (code != 0 && code != -1 && !string.IsNullOrEmpty(s.Fallback)) Shell.Fire("open", s.Fallback!);
        });
    }

    public void Power(string action)
    {
        switch (action)
        {
            case "sleep": Shell.Fire("pmset", "sleepnow"); break;
            case "lock": Shell.Fire("pmset", "displaysleepnow"); break; // locks when "require password" is on
            case "shutdown": _shutdown.Schedule(() => Osa("tell application \"System Events\" to shut down"), Notify); break;
            case "cancel": _shutdown.Cancel(); Notify("Shutdown cancelled."); break;
        }
    }

    public static void Notify(string msg)
    {
        Console.WriteLine(msg);
        Osa($"display notification \"{msg.Replace("\"", "'")}\" with title \"BedReach\"");
    }

    private static void Osa(string script) => Shell.Fire("osascript", "-e", script);

    // ---- installed apps: /Applications, /System/Applications, ~/Applications ----
    public string? FindApp(IReadOnlyList<string> names)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string[] dirs = ["/Applications", "/System/Applications", Path.Combine(home, "Applications")];
        foreach (var n in names)
        {
            if (n.Contains('/') || n.Contains("..")) continue;
            foreach (var d in dirs)
            {
                var p = Path.Combine(d, n + ".app");
                if (Directory.Exists(p)) return p;
            }
        }
        return null;
    }

    public void OpenApp(string appPath) => Shell.Fire("open", "-a", appPath);

    // ---------------------------------------------------------------- CoreGraphics

    private static void Key(ushort code, bool down, ulong flags)
    {
        var ev = CGEventCreateKeyboardEvent(IntPtr.Zero, code, down);
        if (flags != 0) CGEventSetFlags(ev, flags);
        Post(ev);
    }

    private static void Post(IntPtr ev)
    {
        if (ev == IntPtr.Zero) return;
        CGEventPost(0 /* kCGHIDEventTap */, ev);
        CFRelease(ev);
    }

    private static CGPoint Cursor()
    {
        var ev = CGEventCreate(IntPtr.Zero);
        var p = CGEventGetLocation(ev);
        CFRelease(ev);
        return p;
    }

    /// <summary>Media keys are "system defined" NSEvents (subtype 8), converted to a CGEvent and posted.</summary>
    private static void MediaKey(int nxKey)
    {
        EnsureAppKit();
        var cls = objc_getClass("NSEvent");
        var sel = sel_registerName("otherEventWithType:location:modifierFlags:timestamp:windowNumber:context:subtype:data1:data2:");
        var toCg = sel_registerName("CGEvent");
        foreach (var down in new[] { true, false })
        {
            long state = down ? 0xA : 0xB;
            var ns = NSEvent_other(cls, sel, 14 /* NSEventTypeSystemDefined */, new CGPoint(0, 0),
                (ulong)(state << 8), 0, 0, IntPtr.Zero, 8, ((long)nxKey << 16) | (state << 8), -1);
            if (ns == IntPtr.Zero) continue;
            var cg = objc_msgSend_ptr(ns, toCg);
            if (cg != IntPtr.Zero) CGEventPost(0, cg); // owned by the NSEvent: don't release
        }
    }

    private static bool _appKit;
    private static void EnsureAppKit()
    {
        if (_appKit) return;
        NativeLibrary.Load("/System/Library/Frameworks/AppKit.framework/AppKit");
        _appKit = true;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct CGPoint(double X, double Y);

    private const string CG = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CF = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const string AS = "/System/Library/Frameworks/ApplicationServices.framework/ApplicationServices";
    private const string ObjC = "/usr/lib/libobjc.A.dylib";

    [DllImport(CG)] private static extern IntPtr CGEventCreate(IntPtr source);
    [DllImport(CG)] private static extern CGPoint CGEventGetLocation(IntPtr ev);
    [DllImport(CG)] private static extern IntPtr CGEventCreateKeyboardEvent(IntPtr source, ushort key, [MarshalAs(UnmanagedType.I1)] bool down);
    [DllImport(CG)] private static extern void CGEventSetFlags(IntPtr ev, ulong flags);
    [DllImport(CG)] private static extern void CGEventKeyboardSetUnicodeString(IntPtr ev, nuint len, char[] chars);
    [DllImport(CG)] private static extern IntPtr CGEventCreateMouseEvent(IntPtr source, uint type, CGPoint pos, uint button);
    [DllImport(CG)] private static extern IntPtr CGEventCreateScrollWheelEvent2(IntPtr source, uint units, uint count, int w1, int w2, int w3);
    [DllImport(CG)] private static extern void CGEventPost(uint tap, IntPtr ev);
    [DllImport(CF)] private static extern void CFRelease(IntPtr obj);
    [DllImport(AS)] [return: MarshalAs(UnmanagedType.I1)] private static extern bool AXIsProcessTrusted();

    [DllImport(ObjC)] private static extern IntPtr objc_getClass(string name);
    [DllImport(ObjC)] private static extern IntPtr sel_registerName(string name);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")] private static extern IntPtr objc_msgSend_ptr(IntPtr self, IntPtr sel);
    [DllImport(ObjC, EntryPoint = "objc_msgSend")]
    private static extern IntPtr NSEvent_other(IntPtr cls, IntPtr sel, ulong type, CGPoint loc, ulong flags, double time,
        long window, IntPtr ctx, short subtype, long data1, long data2);
}
