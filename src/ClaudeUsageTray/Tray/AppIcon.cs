namespace ClaudeUsageTray.Tray;

/// <summary>The app's static identity icon, loaded once from the embedded <c>app.ico</c>.
///
/// Separate from <see cref="IconRenderer"/> on purpose: that one draws the usage badge that changes
/// every fetch, this one never changes and titles the windows. Loading it from an embedded resource
/// rather than from the exe's win32 icon group keeps it P/Invoke-free and gives WinForms the whole
/// multi-resolution icon, so a title bar and an Alt+Tab thumbnail each pick their own size.</summary>
public static class AppIcon
{
    private static readonly Icon? Loaded = Load();

    /// <summary>The icon, or null when the resource is missing. Null is a valid <see
    /// cref="Form.Icon"/> — a window without the icon still works, so this follows the read paths
    /// and degrades instead of throwing.</summary>
    public static Icon? Value => Loaded;

    private static Icon? Load()
    {
        try
        {
            using Stream? stream = typeof(AppIcon).Assembly
                .GetManifestResourceStream("ClaudeUsageTray.app.ico");
            return stream is null ? null : new Icon(stream);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
