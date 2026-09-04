using System.Runtime.InteropServices;

namespace ClaudeUsageTraySetupStub;

/// <summary>Attaches to the parent's console when there is one, so `--help`, `--version` and silent
/// failures are visible to whoever ran the exe from a shell. Double-clicked, there is no console and
/// the caller falls back to a dialog or the log.</summary>
internal static class ConsoleOutput
{
    private const uint AttachParentProcess = 0xFFFFFFFF;
    private static bool? _attached;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint processId);

    public static bool TryWriteLine(string text)
    {
        _attached ??= AttachConsole(AttachParentProcess);
        if (_attached != true) return false;
        try
        {
            using var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            writer.WriteLine(text);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
    }
}
