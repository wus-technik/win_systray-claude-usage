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

    internal ReleaseNotesDialog(string title, string question, string notes)
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

        // Three rows in a grid rather than Top/Fill/Bottom docking: with docking, the Fill control
        // is laid out first and covers the other two, which is exactly the bug this replaced — a
        // window showing its notes and neither the question nor the buttons.
        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            RowCount = 3,
            Dock = DockStyle.Fill,
        };
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100f));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        var prompt = new Label
        {
            Name = "question",
            Text = question,
            AutoSize = true,
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
            Dock = DockStyle.Fill,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(0, 8, 0, 0),
        };
        buttons.Controls.Add(install);
        buttons.Controls.Add(later);

        layout.Controls.Add(prompt, 0, 0);
        layout.Controls.Add(body, 0, 1);
        layout.Controls.Add(buttons, 0, 2);
        Controls.Add(layout);

        AcceptButton = install;
        // Escape closes without installing — the same answer as Later.
        CancelButton = later;

        // A focused multiline TextBox selects everything it holds, which paints the notes as a block
        // of inverted blue. The button that answers the question is the better place for focus.
        ActiveControl = install;
        body.SelectionStart = 0;
        body.SelectionLength = 0;
    }
}
