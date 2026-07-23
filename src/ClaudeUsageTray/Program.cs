namespace ClaudeUsageTray;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        // TrayApp is wired up in a later task; for now the scaffold just exits.
    }
}
