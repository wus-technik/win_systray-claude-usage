namespace ClaudeUsageTraySetupStub;

/// <summary>A WinExe has no stdout, so a silent run would otherwise show an operator nothing but an
/// exit code. Same rule as the app's fetch.log: outcomes, never credential material. Never throws.</summary>
public sealed class SetupLog(string path)
{
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ClaudeUsageTray", "setup.log");

    public void Write(string message)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:yyyy-MM-ddTHH:mm:ss}Z {message}{Environment.NewLine}");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // diagnostics must never break the installer
        }
    }
}
