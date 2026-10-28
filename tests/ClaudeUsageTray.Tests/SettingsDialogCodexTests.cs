using System.Windows.Forms;
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class SettingsDialogCodexTests
{
    private static SettingsDialog NewDialog(CodexState state, Settings? settings = null)
    {
        var dialog = new SettingsDialog(settings ?? new Settings(), false, false, _ => true,
            TestUpdateOptions.Inert(), false, new Dictionary<string, IReadOnlyList<string>>(), state);
        dialog.StartPosition = FormStartPosition.Manual;
        dialog.Location = new System.Drawing.Point(-4000, -4000);
        dialog.Show();
        return dialog;
    }

    private static TabControl Tabs(Form dialog)
        => dialog.Controls.Find("tabs", true).OfType<TabControl>().Single();

    [Theory]
    [InlineData(CodexState.Unknown)]
    [InlineData(CodexState.Unavailable)]
    public void WithoutCodex_NoCodexTab(CodexState state)
    {
        using var dialog = NewDialog(state);
        Assert.DoesNotContain(Tabs(dialog).TabPages.Cast<TabPage>(), p => p.Text == "Codex");
    }

    [Fact]
    public void WithCodex_HasACodexTab()
    {
        using var dialog = NewDialog(CodexState.Available);
        Assert.Equal(new[] { "General", "Appearance", "Status", "Codex", "About" },
            Tabs(dialog).TabPages.Cast<TabPage>().Select(p => p.Text));
    }

    [Fact]
    public void CodexPage_FitsWithoutScrollingAndHeadersFit()
    {
        using var dialog = NewDialog(CodexState.Available);
        var tabs = Tabs(dialog);
        var page = tabs.TabPages.Cast<TabPage>().Single(p => p.Text == "Codex");
        var needed = page.Controls[0].PreferredSize;
        Assert.True(needed.Height + page.Padding.Vertical <= tabs.DisplayRectangle.Height);
        Assert.True(needed.Width + page.Padding.Horizontal <= tabs.DisplayRectangle.Width);
        Assert.True(Enumerable.Range(0, tabs.TabCount).Sum(i => tabs.GetTabRect(i).Width) <= tabs.Width);
    }

    [Theory]
    [InlineData(CodexState.Available)]
    [InlineData(CodexState.Unavailable)]
    public void Save_RoundTripsCodexEvenWithNoPage(CodexState state)
    {
        var settings = new Settings
        {
            CodexDisplayMode = DisplayMode.SevenDay,
            CodexNotifications = new UsageNotificationSettings { Enabled = false, Level = NotifyLevel.Orange },
        };
        using var dialog = NewDialog(state, settings);
        var draft = dialog.Draft();
        Assert.Equal(DisplayMode.SevenDay, draft.CodexDisplayMode);
        Assert.False(draft.CodexNotifications.Enabled);
        Assert.Equal(NotifyLevel.Orange, draft.CodexNotifications.Level);
        Assert.NotSame(settings.CodexNotifications, draft.CodexNotifications);
    }

    [Fact]
    public void CodexRadios_AreExclusiveAndDoNotChangeClaude()
    {
        using var dialog = NewDialog(CodexState.Available,
            new Settings { DisplayMode = DisplayMode.FiveHour, CodexDisplayMode = DisplayMode.Both });
        var five = dialog.Controls.Find("codexModeFive", true).OfType<RadioButton>().Single();
        var both = dialog.Controls.Find("codexModeBoth", true).OfType<RadioButton>().Single();
        five.Checked = true;
        Assert.False(both.Checked);
        Assert.Equal(DisplayMode.FiveHour, dialog.Draft().DisplayMode);
        Assert.Equal(DisplayMode.FiveHour, dialog.Draft().CodexDisplayMode);
    }

    [Fact]
    public void AutoEnabledOpenAi_ShowsCheckedWithoutMakingAnExplicitChoice()
    {
        using var dialog = NewDialog(CodexState.Available);
        Assert.True(dialog.Controls.Find("watchOpenAi", true).OfType<CheckBox>().Single().Checked);
        Assert.Null(dialog.Draft().StatusSources["openai"]!.Enabled);
    }

    [Fact]
    public void CodexNotificationSwitch_DisablesOnlyItsLevel()
    {
        using var dialog = NewDialog(CodexState.Available);
        var notify = dialog.Controls.Find("codexNotify", true).OfType<CheckBox>().Single();
        notify.Checked = false;
        Assert.False(dialog.Controls.Find("codexNotifyLevel", true).OfType<ComboBox>().Single().Enabled);
        Assert.True(dialog.Draft().UsageNotifications.Enabled);
    }
}
