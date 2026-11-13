using System.Diagnostics;

namespace BedReach;

/// <summary>
/// Runs helper programs with an argument list (never through a shell, so nothing the phone
/// sends can be interpreted as a command).
/// </summary>
internal static class Shell
{
    private static readonly Dictionary<string, bool> Found = new();

    public static bool Has(string tool)
    {
        lock (Found)
        {
            if (Found.TryGetValue(tool, out var f)) return f;
            var path = Environment.GetEnvironmentVariable("PATH") ?? "";
            f = path.Split(Path.PathSeparator).Any(d => d.Length > 0 && File.Exists(Path.Combine(d, tool)));
            return Found[tool] = f;
        }
    }

    /// <summary>Starts a program and returns immediately.</summary>
    public static void Fire(string exe, params string[] args)
    {
        var p = Process.Start(Info(exe, args)) ?? throw new InvalidOperationException($"could not start {exe}");
        // Reap it in the background so we don't leave zombies.
        _ = p.WaitForExitAsync().ContinueWith(_ => p.Dispose());
    }

    /// <summary>Runs a program and returns its exit code and stdout (or -1 on timeout).</summary>
    public static (int Code, string Output) Run(string exe, int timeoutMs, params string[] args)
    {
        using var p = Process.Start(Info(exe, args)) ?? throw new InvalidOperationException($"could not start {exe}");
        var outTask = p.StandardOutput.ReadToEndAsync();
        if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch { } return (-1, ""); }
        return (p.ExitCode, outTask.Result.Trim());
    }

    private static ProcessStartInfo Info(string exe, string[] args)
    {
        var psi = new ProcessStartInfo(exe)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }
}

/// <summary>A 60-second delayed shutdown that can be cancelled from the phone.</summary>
internal sealed class DelayedShutdown
{
    private CancellationTokenSource? _pending;

    public void Schedule(Action shutdown, Action<string> notify)
    {
        Cancel();
        var cts = _pending = new CancellationTokenSource();
        notify("Shutting down in 60 seconds. Cancel it from the phone's power menu.");
        _ = Task.Delay(TimeSpan.FromSeconds(60), cts.Token).ContinueWith(t => { if (!t.IsCanceled) shutdown(); });
    }

    public void Cancel()
    {
        _pending?.Cancel();
        _pending = null;
    }
}

/// <summary>Used in automated tests: does nothing.</summary>
internal sealed class NullPlatform : IPlatform
{
    public bool PressKey(string name) => true;
    public void TypeText(string text) { }
    public void MoveMouse(int dx, int dy) { }
    public void Click(string button) { }
    public void Scroll(int amount) { }
    public void Launch(ServiceEntry service) { }
    public string? FindApp(IReadOnlyList<string> names) => null;
    public void OpenApp(string handle) { }
    public void Power(string action) { }
}
