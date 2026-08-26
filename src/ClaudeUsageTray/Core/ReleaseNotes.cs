namespace ClaudeUsageTray.Core;

/// <summary>The release notes Velopack packed with the update, prepared for display. Kept pure so
/// "there are no notes" is one decision in one place: the dialog asks for a string and shows the
/// plain restart prompt when it gets null.</summary>
public static class ReleaseNotes
{
    /// <summary>The notes as a read-only TextBox wants them — CRLF line endings, no surrounding
    /// blank lines — or null when the package carried nothing worth showing. Packages built before
    /// the release pipeline passed --releaseNotes have no notes at all, which is not an error.</summary>
    public static string? Format(string? notesMarkdown)
    {
        if (string.IsNullOrWhiteSpace(notesMarkdown)) return null;
        // Normalize via LF first, so notes that already use CRLF do not end up with doubled returns.
        var text = notesMarkdown.Replace("\r\n", "\n").Replace('\r', '\n').Trim();
        return text.Length == 0 ? null : text.Replace("\n", "\r\n");
    }
}
