using Microsoft.Win32;

namespace BedReach;

public static class AutoStart
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string Name = "BedReach";
    private static string Command => $"\"{Environment.ProcessPath}\" --background";

    public static bool IsEnabled
    {
        get
        {
            using var k = Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(Name) is string;
        }
    }

    public static void Set(bool on)
    {
        using var k = Registry.CurrentUser.CreateSubKey(RunKey);
        if (on) k.SetValue(Name, Command); else k.DeleteValue(Name, false);
    }

    /// <summary>If enabled, keep the registered path pointing at this exe (in case it was moved).</summary>
    public static void Refresh()
    {
        if (IsEnabled) Set(true);
    }
}

/// <summary>
/// The app was called "Couch Remote" in early builds. Carry its settings over once and remove its
/// startup entry, so upgrading doesn't leave two copies starting with Windows.
/// (Pairings are not carried over: the protocol labels changed with the name, so phones pair again.)
/// </summary>
internal static class Legacy
{
    private const string OldName = "Couch" + "Remote";

    public static void Migrate()
    {
        try
        {
            var oldConfig = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), OldName, "config.json");
            if (!File.Exists(Storage.ConfigPath) && File.Exists(oldConfig))
            {
                Directory.CreateDirectory(Storage.Dir);
                File.Copy(oldConfig, Storage.ConfigPath);
            }
            using var k = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);
            k?.DeleteValue(OldName, false);
        }
        catch
        {
            // Best effort only.
        }
    }
}
