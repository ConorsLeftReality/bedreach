using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace BedReach;

internal static class Program
{
    private const string Help = """
        BedReach agent (Linux / macOS)

          bedreach               run in the foreground (Ctrl+C to stop)
          bedreach install       copy itself to your user folder, start at login, and start now
          bedreach uninstall     stop starting at login (keeps your settings)
          bedreach status        show addresses, config file and paired phones
          bedreach remove NAME   unpair a phone
          bedreach help          this text
        """;

    private static int Main(string[] args)
    {
        var cmd = args.FirstOrDefault()?.TrimStart('-').ToLowerInvariant() ?? "run";
        try
        {
            switch (cmd)
            {
                case "run": return Run();
                case "install": return Installer.Install();
                case "uninstall": return Installer.Uninstall();
                case "status": return Status();
                case "remove": return Remove(string.Join(' ', args.Skip(1)));
                case "help" or "h": Console.WriteLine(Help); return 0;
                default: Console.Error.WriteLine($"Unknown command '{args[0]}'.\n\n{Help}"); return 2;
            }
        }
        catch (Exception e)
        {
            Console.Error.WriteLine("Error: " + e.Message);
            return 1;
        }
    }

    private static int Run()
    {
        if (OperatingSystem.IsWindows() && !TestMode.On)
        {
            Console.Error.WriteLine("On Windows, use BedReach.exe (the tray app) instead.");
            return 2;
        }

        using var single = new Mutex(true, "BedReach.Agent", out bool first);
        if (!first) { Console.Error.WriteLine("BedReach is already running."); return 1; }

        var cfg = Storage.LoadConfig();
        IPlatform platform = TestMode.On ? new NullPlatform()
            : OperatingSystem.IsMacOS() ? new MacPlatform()
            : new LinuxPlatform();

        var server = new RemoteServer(cfg, platform, (device, code) => Task.Run(() => Confirm.Pairing(device, code)));
        server.StatusChanged += () => { if (server.LastError != null) Console.Error.WriteLine(server.LastError); };
        server.Start();

        Console.WriteLine($"BedReach is running as \"{cfg.Name}\" on port {cfg.Port}.");
        Console.WriteLine("Open BedReach on your phone (same Wi-Fi) and tap this computer to pair.");
        foreach (var a in Addresses()) Console.WriteLine($"  address: {a}");
        if (platform is LinuxPlatform lp) Console.WriteLine("  " + lp.Describe());
        if (platform is MacPlatform && !MacPlatform.HasAccessibility())
            MacPlatform.Notify("Allow BedReach under System Settings → Privacy & Security → Accessibility so it can control the keyboard and mouse.");

        var done = new ManualResetEventSlim();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; done.Set(); };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => done.Set();
        done.Wait();
        server.Stop();
        return 0;
    }

    private static int Status()
    {
        var cfg = Storage.LoadConfig();
        Console.WriteLine($"Name:    {cfg.Name}");
        Console.WriteLine($"Port:    {cfg.Port}");
        foreach (var a in Addresses()) Console.WriteLine($"Address: {a}");
        Console.WriteLine($"Config:  {Storage.ConfigPath}");
        Console.WriteLine($"Starts at login: {(Installer.IsInstalled ? "yes" : "no")}");
        var devices = Storage.LoadDevices();
        Console.WriteLine(devices.Count == 0 ? "No phones paired." : "Paired phones:");
        foreach (var d in devices) Console.WriteLine($"  {d.Name}  (paired {d.PairedAt:d MMM yyyy}, last used {d.LastSeen?.ToString("d MMM HH:mm") ?? "never"})");
        return 0;
    }

    private static int Remove(string name)
    {
        int removed = 0;
        Storage.UpdateDevices(d => removed = d.RemoveAll(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase) || x.Id == name));
        Console.WriteLine(removed > 0 ? $"Removed {removed} phone(s)." : $"No paired phone called '{name}'. Run 'bedreach status' to see them.");
        return removed > 0 ? 0 : 1;
    }

    public static List<string> Addresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !a.Address.ToString().StartsWith("169.254."))
            .Select(a => a.Address.ToString()).Distinct().ToList();
}

/// <summary>Asks the person at the computer to approve a new phone.</summary>
internal static class Confirm
{
    public static bool Pairing(string device, string code)
    {
        if (TestMode.On)
        {
            File.WriteAllText(Path.Combine(Storage.Dir, "pair-code.txt"), code);
            return device != "DENY-ME";
        }

        var pretty = code[..3] + " " + code[3..];
        var safeDevice = new string(device.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_' or '\'' or '.').ToArray());
        var text = $"“{safeDevice}” wants to control this computer.\n\nOnly allow it if your phone shows this code:\n\n{pretty}";
        Console.WriteLine($"Pairing request from {safeDevice}, code {pretty}");

        if (OperatingSystem.IsMacOS())
        {
            var script = $"display dialog \"{text.Replace("\"", "'")}\" with title \"BedReach\" buttons {{\"Deny\", \"Allow\"}} default button \"Deny\" cancel button \"Deny\" giving up after 60 with icon caution";
            var (c, output) = Shell.Run("osascript", 65_000, "-e", script);
            return c == 0 && output.Contains("button returned:Allow") && !output.Contains("gave up:true");
        }

        if (Shell.Has("zenity"))
            return Shell.Run("zenity", 65_000, "--question", "--title=BedReach", $"--text={text}",
                "--ok-label=Allow", "--cancel-label=Deny", "--timeout=60", "--default-cancel").Code == 0;
        if (Shell.Has("kdialog"))
            return Shell.Run("kdialog", 65_000, "--title", "BedReach", "--yes-label", "Allow", "--no-label", "Deny", "--yesno", text).Code == 0;

        if (!Console.IsInputRedirected)
        {
            Console.Write("Type 'allow' to approve: ");
            var t = Task.Run(Console.ReadLine);
            return t.Wait(TimeSpan.FromSeconds(60)) && t.Result?.Trim().ToLowerInvariant() == "allow";
        }

        Console.Error.WriteLine("Can't show a dialog to approve the phone: install 'zenity' or 'kdialog', or run 'bedreach' in a terminal while pairing.");
        return false;
    }
}

/// <summary>Start-at-login: XDG autostart on Linux, a LaunchAgent on macOS.</summary>
internal static class Installer
{
    private static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    private static string InstalledExe => OperatingSystem.IsMacOS()
        ? Path.Combine(Home, "Library", "Application Support", "BedReach", "bedreach")
        : Path.Combine(Home, ".local", "bin", "bedreach");
    private static string LinuxAutostart => Path.Combine(Home, ".config", "autostart", "bedreach.desktop");
    private static string MacAgent => Path.Combine(Home, "Library", "LaunchAgents", "com.bedreach.agent.plist");

    public static bool IsInstalled => File.Exists(OperatingSystem.IsMacOS() ? MacAgent : LinuxAutostart);

    public static int Install()
    {
        if (OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("on Windows, use BedReach.exe");
        var self = Environment.ProcessPath ?? throw new InvalidOperationException("can't find my own path");
        Directory.CreateDirectory(Path.GetDirectoryName(InstalledExe)!);
        if (!string.Equals(Path.GetFullPath(self), InstalledExe, StringComparison.Ordinal))
        {
            File.Copy(self, InstalledExe, true);
            File.SetUnixFileMode(InstalledExe, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                                               UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }

        if (OperatingSystem.IsMacOS())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(MacAgent)!);
            var log = Path.Combine(Home, "Library", "Logs", "bedreach.log");
            File.WriteAllText(MacAgent, $"""
                <?xml version="1.0" encoding="UTF-8"?>
                <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
                <plist version="1.0">
                <dict>
                  <key>Label</key><string>com.bedreach.agent</string>
                  <key>ProgramArguments</key><array><string>{InstalledExe}</string></array>
                  <key>RunAtLoad</key><true/>
                  <key>KeepAlive</key><dict><key>SuccessfulExit</key><false/></dict>
                  <key>StandardOutPath</key><string>{log}</string>
                  <key>StandardErrorPath</key><string>{log}</string>
                </dict>
                </plist>
                """);
            Shell.Run("launchctl", 10_000, "unload", MacAgent);
            Shell.Run("launchctl", 10_000, "load", "-w", MacAgent);
            Console.WriteLine($"Installed to {InstalledExe} and started. It will start whenever you log in.");
            Console.WriteLine("Next: System Settings → Privacy & Security → Accessibility → allow 'bedreach'.");
        }
        else
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LinuxAutostart)!);
            File.WriteAllText(LinuxAutostart, $"""
                [Desktop Entry]
                Type=Application
                Name=BedReach
                Comment=Control this computer from your phone
                Exec="{InstalledExe}"
                Terminal=false
                X-GNOME-Autostart-enabled=true
                """);
            // Start now, detached from this terminal.
            Shell.Fire("setsid", "-f", InstalledExe);
            Console.WriteLine($"Installed to {InstalledExe} and started. It will start whenever you log in.");
        }
        Console.WriteLine("Open BedReach on your phone and tap this computer to pair.");
        return 0;
    }

    public static int Uninstall()
    {
        if (OperatingSystem.IsMacOS())
        {
            if (File.Exists(MacAgent)) { Shell.Run("launchctl", 10_000, "unload", "-w", MacAgent); File.Delete(MacAgent); }
        }
        else
        {
            if (File.Exists(LinuxAutostart)) File.Delete(LinuxAutostart);
            Shell.Run("pkill", 5000, "-x", "bedreach");
        }
        Console.WriteLine($"BedReach won't start at login any more. Settings and paired phones are kept in {Storage.Dir}.");
        return 0;
    }
}
