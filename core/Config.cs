using System.Text.Json;
using System.Text.Json.Serialization;

namespace BedReach;

public sealed class ServiceEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>
    /// Installed-app names to look for on this computer (as they appear in the Start menu,
    /// app launcher or Applications folder). If one is installed it opens instead of <see cref="Target"/>.
    /// </summary>
    public List<string> Apps { get; set; } = [];
    /// <summary>URL or app URI opened with the default handler when no app from <see cref="Apps"/> is installed.</summary>
    public string Target { get; set; } = "";
    /// <summary>Used if Target fails to open.</summary>
    public string? Fallback { get; set; }
    /// <summary>Button colour on the phone, #RRGGBB.</summary>
    public string Color { get; set; } = "#444444";
}

public sealed class AppConfig
{
    public string ServerId { get; set; } = Guid.NewGuid().ToString("N");
    public string? DisplayName { get; set; }
    public int Port { get; set; } = 47800;
    public int DiscoveryPort { get; set; } = 47801;
    public List<ServiceEntry> Services { get; set; } = DefaultServices();

    [JsonIgnore] public string Name => string.IsNullOrWhiteSpace(DisplayName) ? Environment.MachineName : DisplayName!;

    public static List<ServiceEntry> DefaultServices() =>
    [
        new() { Id = "netflix", Name = "Netflix", Apps = ["Netflix"], Target = "https://www.netflix.com/browse", Color = "#E50914" },
        new() { Id = "nowtv", Name = "NOW", Apps = ["NOW", "NOW TV", "NOW Player"], Target = "https://www.nowtv.com/", Color = "#14B8A6" },
        new() { Id = "disney", Name = "Disney+", Apps = ["Disney+", "Disney Plus"], Target = "https://www.disneyplus.com/", Color = "#113CCF" },
        new() { Id = "crunchyroll", Name = "Crunchyroll", Apps = ["Crunchyroll"], Target = "https://www.crunchyroll.com/", Color = "#F47521" },
        new() { Id = "youtube", Name = "YouTube", Apps = ["YouTube"], Target = "https://www.youtube.com/", Color = "#FF0033" },
        new() { Id = "spotify", Name = "Spotify", Apps = ["Spotify", "Spotify Music"], Target = "https://open.spotify.com/", Color = "#1DB954" },
        new() { Id = "prime", Name = "Prime Video", Apps = ["Prime Video", "Amazon Prime Video", "Prime Video for Windows"], Target = "https://www.primevideo.com/", Color = "#00A8E1" },
        new() { Id = "appletv", Name = "Apple TV", Apps = ["Apple TV", "TV"], Target = "https://tv.apple.com/", Color = "#333333" },
        new() { Id = "max", Name = "HBO Max", Apps = ["Max", "HBO Max"], Target = "https://play.max.com/", Color = "#5B2EFF" },
        new() { Id = "twitch", Name = "Twitch", Apps = ["Twitch"], Target = "https://www.twitch.tv/", Color = "#9146FF" },
        new() { Id = "plex", Name = "Plex", Apps = ["Plex", "Plex HTPC"], Target = "https://app.plex.tv/", Color = "#E5A00D" },
    ];

    /// <summary>
    /// Older config files have no "apps" lists: fill them in from the defaults (matched by id)
    /// without touching anything the user changed.
    /// </summary>
    public bool FillMissingAppLists()
    {
        bool changed = false;
        var defaults = DefaultServices().ToDictionary(s => s.Id);
        foreach (var s in Services)
            if (s.Apps.Count == 0 && defaults.TryGetValue(s.Id, out var d)) { s.Apps = d.Apps; changed = true; }
        return changed;
    }
}

public sealed class PairedDevice
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime PairedAt { get; set; }
    public DateTime? LastSeen { get; set; }
    /// <summary>
    /// Shared secret, wrapped by <see cref="Storage.Protect"/>: Windows DPAPI on Windows; on Linux/macOS
    /// the file itself is readable only by the owning user (mode 600).
    /// </summary>
    public string ProtectedSecret { get; set; } = "";

    public byte[] GetSecret() => Storage.Unprotect(Convert.FromBase64String(ProtectedSecret));
    public void SetSecret(byte[] s) => ProtectedSecret = Convert.ToBase64String(Storage.Protect(s));
}

public static class Storage
{
    /// <summary>Platform hook for encrypting secrets at rest. Default: no extra wrapping.</summary>
    public static Func<byte[], byte[]> Protect = b => b;
    public static Func<byte[], byte[]> Unprotect = b => b;

    public static readonly string Dir = TestMode.DataDir ?? DefaultDir();

    private static string DefaultDir()
    {
        if (OperatingSystem.IsMacOS())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "Application Support", "BedReach");
        if (OperatingSystem.IsWindows())
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "BedReach");
        var xdg = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
        return Path.Combine(string.IsNullOrEmpty(xdg) ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config") : xdg, "bedreach");
    }

    private static void RestrictToOwner(string path)
    {
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    public static string ConfigPath => Path.Combine(Dir, "config.json");
    private static string DevicesPath => Path.Combine(Dir, "devices.json");
    private static readonly object Lock = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public static AppConfig LoadConfig()
    {
        Directory.CreateDirectory(Dir);
        AppConfig cfg;
        try { cfg = File.Exists(ConfigPath) ? JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), Json) ?? new() : new(); }
        catch { cfg = new(); } // corrupt file → defaults (and we rewrite it below)
        cfg.Services ??= AppConfig.DefaultServices();
        foreach (var s in cfg.Services) s.Apps ??= [];
        cfg.FillMissingAppLists();
        File.WriteAllText(ConfigPath, JsonSerializer.Serialize(cfg, Json));
        return cfg;
    }

    public static List<PairedDevice> LoadDevices()
    {
        lock (Lock)
        {
            try { return File.Exists(DevicesPath) ? JsonSerializer.Deserialize<List<PairedDevice>>(File.ReadAllText(DevicesPath), Json) ?? [] : []; }
            catch { return []; }
        }
    }

    public static void SaveDevices(List<PairedDevice> devices)
    {
        lock (Lock)
        {
            Directory.CreateDirectory(Dir);
            var tmp = DevicesPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(devices, Json));
            RestrictToOwner(tmp);
            File.Move(tmp, DevicesPath, true);
        }
    }

    public static void UpdateDevices(Action<List<PairedDevice>> change)
    {
        lock (Lock) { var d = LoadDevices(); change(d); SaveDevices(d); }
    }
}

