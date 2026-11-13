namespace BedReach;

/// <summary>
/// What the PC can do for the phone. Each OS implements this. The server only ever calls these
/// methods, and only with values from the whitelists below.
/// </summary>
public interface IPlatform
{
    /// <summary>Press a key or shortcut named in <see cref="Keys.All"/>. Returns false if unsupported.</summary>
    bool PressKey(string name);
    void TypeText(string text);
    void MoveMouse(int dx, int dy);
    void Click(string button);
    /// <summary>Wheel delta: 120 = one notch, positive = up.</summary>
    void Scroll(int amount);
    void Launch(ServiceEntry service);
    /// <summary>
    /// Looks for an installed app whose name matches one of <paramref name="names"/> (case-insensitive).
    /// Returns a platform-specific handle to pass to <see cref="OpenApp"/>, or null if none is installed.
    /// </summary>
    string? FindApp(IReadOnlyList<string> names);
    void OpenApp(string handle);
    /// <summary>sleep | lock | shutdown (in 60 s) | cancel</summary>
    void Power(string action);
}

public static class Keys
{
    /// <summary>Every key name the protocol allows (see PROTOCOL.md).</summary>
    public static readonly HashSet<string> All =
    [
        "space", "enter", "esc", "backspace", "tab", "left", "up", "right", "down",
        "pageup", "pagedown", "home", "end", "f", "m", "k", "j", "l", "c", "s", "f11", "f5",
        "playpause", "next", "prev", "stop", "volup", "voldown", "mute",
        "alttab", "alt_f4", "browser_back", "shift_n", "win_d",
    ];

    public static readonly HashSet<string> PowerActions = ["sleep", "lock", "shutdown", "cancel"];

    public static string Clamp(string text) => text.Length > 500 ? text[..500] : text;
}
