namespace BedReach;

/// <summary>
/// Automated-test hooks. Only compiled into Debug builds; Release builds always return "off".
/// Set BEDREACH_TEST_DIR to a folder to: store data there, listen on 127.0.0.1 only,
/// auto-approve pairing, skip autostart and pop-ups, and ignore input commands.
/// </summary>
public static class TestMode
{
#if DEBUG
    public static readonly string? DataDir = Environment.GetEnvironmentVariable("BEDREACH_TEST_DIR");
#else
    public static readonly string? DataDir = null;
#endif
    public static bool On => DataDir != null;
}
