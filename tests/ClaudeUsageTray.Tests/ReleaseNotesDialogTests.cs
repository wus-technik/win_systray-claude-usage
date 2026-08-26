using System.Windows.Forms;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The window shown before a restart. These are layout tests on purpose: the first version
/// docked the notes box over the question and both buttons, which every behavioural test passed
/// happily because the controls existed — they were just invisible.</summary>
public class ReleaseNotesDialogTests : IDisposable
{
    private readonly List<Form> _open = [];

    public void Dispose() { foreach (var form in _open) form.Dispose(); }

    private const string Notes = "### Changed\r\n\r\n- One thing\r\n- Another thing";

    private ReleaseNotesDialog Dialog(string notes = Notes)
    {
        var dialog = new ReleaseNotesDialog("Claude Usage — Update",
            "Version 0.7.1 is ready. Restart now to install it?", notes);
        _open.Add(dialog);
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new System.Drawing.Point(-4000, -4000);
        dialog.Show();
        return dialog;
    }

    private static Control Find(Form dialog, string name)
        => dialog.Controls.Find(name, searchAllChildren: true).Single();

    [Fact]
    public void TheNotesAreShownVerbatim()
        => Assert.Equal(Notes, ((TextBox)Find(Dialog(), "notes")).Text);

    [Fact]
    public void TheNotesAreReadOnlyButStillScrollable()
    {
        var notes = (TextBox)Find(Dialog(), "notes");
        Assert.True(notes.ReadOnly);
        Assert.True(notes.Enabled); // a disabled TextBox greys out and stops scrolling
        Assert.Equal(ScrollBars.Vertical, notes.ScrollBars);
    }

    [Fact]
    public void TheQuestionNamesTheVersion()
        => Assert.Contains("0.7.1", Find(Dialog(), "question").Text);

    /// <summary>Every control has to be inside the client area and clear of the notes box, or the
    /// user cannot answer the question the window is asking.</summary>
    [Theory]
    [InlineData("question")]
    [InlineData("install")]
    [InlineData("later")]
    public void EveryControlIsActuallyVisible(string name)
    {
        var dialog = Dialog();
        var control = Find(dialog, name);
        var notes = Find(dialog, "notes");

        Assert.True(control.Visible, $"{name} is not visible");
        Assert.True(control.Width > 0 && control.Height > 0, $"{name} has no size");

        var bounds = dialog.RectangleToClient(control.RectangleToScreen(control.ClientRectangle));
        Assert.True(dialog.ClientRectangle.Contains(bounds), $"{name} lies outside the client area");

        var notesBounds = dialog.RectangleToClient(notes.RectangleToScreen(notes.ClientRectangle));
        Assert.False(notesBounds.IntersectsWith(bounds), $"the notes box overlaps {name}");
    }

    [Fact]
    public void InstallIsTheDefaultAndEscapeDeclines()
    {
        var dialog = Dialog();
        Assert.Same(Find(dialog, "install"), dialog.AcceptButton);
        Assert.Same(Find(dialog, "later"), dialog.CancelButton);
        Assert.Equal(DialogResult.OK, ((Button)Find(dialog, "install")).DialogResult);
        Assert.Equal(DialogResult.Cancel, ((Button)Find(dialog, "later")).DialogResult);
    }

    /// <summary>A focused multiline TextBox selects its whole content, which renders the notes as a
    /// block of inverted blue. Nothing here is meant to be copied by default, so the install button
    /// takes focus and the notes open unselected at the top.</summary>
    [Fact]
    public void TheNotesOpenUnselectedAndAtTheTop()
    {
        var dialog = Dialog();
        var notes = (TextBox)Find(dialog, "notes");
        Assert.Equal(0, notes.SelectionLength);
        Assert.Equal(0, notes.SelectionStart);
        Assert.Same(Find(dialog, "install"), dialog.ActiveControl);
    }
}
