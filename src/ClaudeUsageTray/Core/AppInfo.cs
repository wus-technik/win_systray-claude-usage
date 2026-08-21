namespace ClaudeUsageTray.Core;

/// <summary>What the app calls itself and who made it. One place, because the display name titles
/// three separate windows and the creator has to survive a WinForms label — three hand-typed copies
/// of either drift the moment one of them is edited.</summary>
public static class AppInfo
{
    /// <summary>The name in window titles. Deliberately shorter than the packed title ("Claude Usage
    /// Tray"), which names the installer's product rather than the window in front of the user.</summary>
    public const string Name = "Claude Usage";

    /// <summary>Matches the copyright holder in LICENSE and the &lt;Company&gt; in the csproj.</summary>
    public const string Creator = "W&S Technik GmbH";

    /// <summary>A title for one of the app's windows: "Claude Usage — Settings".</summary>
    public static string Window(string surface) => $"{Name} — {surface}";

    /// <summary>The creator with its ampersand doubled. A WinForms label reads a lone &amp; as a
    /// mnemonic prefix and would draw "WS Technik GmbH" with the S underlined.</summary>
    public static string CreatorForLabel => Creator.Replace("&", "&&");
}
