namespace ClaudeUsageTray.Tray;

/// <summary>What is about to be installed, and the last chance to decline it. Shows the release notes
/// Velopack packed with the target version — as plain text, because WinForms has no markdown renderer
/// and the changelog sections are bullet lists that read fine raw.</summary>
public sealed class ReleaseNotesDialog : Form
{
    /// <summary>Shows the notes and returns true when the user chose to install now.</summary>
    public static bool Confirm(IWin32Window? owner, string title, string question, string notes)
    {
        using var dialog = new ReleaseNotesDialog(title, question, notes);
        return dialog.ShowDialog(owner) == DialogResult.OK;
    }

    private ReleaseNotesDialog(string title, string question, string notes)
    {
        Text = title;
        FormBorderStyle = FormBorderStyle.Sizable;
        MinimizeBox = false;
        MaximizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        MinimumSize = new Size(420, 300);
        ClientSize = new Size(520, 380);
        Padding = new Padding(12);

        var prompt = new Label
        {
            Name = "question",
            Text = question,
            AutoSize = true,
            Dock = DockStyle.Top,
            Margin = new Padding(0, 0, 0, 8),
        };

        // Read-only rather than disabled: a disabled TextBox greys its text out and stops scrolling,
        // which is the opposite of what notes are for.
        var body = new TextBox
        {
            Name = "notes",
            Text = notes,
            ReadOnly = true,
            Multiline = true,
            WordWrap = true,
            ScrollBars = ScrollBars.Vertical,
            Dock = DockStyle.Fill,
            BackColor = SystemColors.Window,
            Font = new Font("Consolas", 9f),
        };

        var install = new Button
        {
            Name = "install",
            Text = "Update and restart",
            AutoSize = true,
            DialogResult = DialogResult.OK,
        };
        var later = new Button
        {
            Name = "later",
            Text = "Later",
            AutoSize = true,
            DialogResult = DialogResult.Cancel,
        };

        var buttons = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.RightToLeft,
            Dock = DockStyle.Bottom,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(install);
        buttons.Controls.Add(later);

        // Order matters for Dock: the fill control is added last so it takes what the others leave.
        Controls.Add(prompt);
        Controls.Add(buttons);
        Controls.Add(body);

        AcceptButton = install;
        // Escape closes without installing — the same answer as Later.
        CancelButton = later;
    }
}
