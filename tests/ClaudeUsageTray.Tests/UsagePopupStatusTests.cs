using System.Drawing;
using System.Windows.Forms;
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Rendered offscreen with CreateControl(), never Show(): UsagePopup.OnDeactivate calls
/// Close(), and with no message loop Show() disposes the form before anything can be inspected.</summary>
public class UsagePopupStatusTests : IDisposable
{
    private static readonly DateTimeOffset Now = new(2026, 8, 26, 12, 0, 0, TimeSpan.Zero);
    private readonly List<UsagePopup> _open = [];

    public void Dispose() { foreach (var popup in _open) popup.Dispose(); }

    private List<Label> Labels(params SourceView[] sources)
    {
        // No usage snapshot: the status blocks come first in the layout, so they are labels[0..n]
        // and the single no-data sentence follows them.
        var popup = new UsagePopup(new DisplayChoice(null, Stale: false), new Settings(), Now, sources);
        _open.Add(popup);
        popup.CreateControl();
        return popup.Controls.Cast<Control>()
            .SelectMany(c => c.Controls.Cast<Control>())
            .OfType<Label>()
            .Where(l => l is not LinkLabel)
            .ToList();
    }

    private static SourceView View(StatusSource source, string indicator, string description,
        PlatformComponent[]? components = null, IReadOnlyList<string>? filter = null)
        => new(source, new PlatformStatus(source.Id, Now, indicator, description, [], components ?? []),
            filter ?? []);

    [Fact]
    public void BothSources_RenderClaudeFirst()
    {
        var labels = Labels(
            View(StatusSourceRegistry.Claude, "none", "All Systems Operational"),
            View(StatusSourceRegistry.OpenAi, "none", "All Systems Operational"));
        Assert.Equal("Claude status: All Systems Operational", labels[0].Text);
        Assert.Equal("OpenAI status: All Systems Operational", labels[1].Text);
    }

    [Fact]
    public void RelevantDisruption_IsColouredAndListsWatchedComponents()
    {
        var labels = Labels(View(StatusSourceRegistry.OpenAi, "minor", "Partial System Outage",
            [new("Codex API", "partial_outage"), new("Sora", "major_outage")], ["codex"]));
        Assert.Equal("OpenAI status: Partial System Outage", labels[0].Text);
        Assert.Equal(Color.DarkOrange, labels[0].ForeColor);
        Assert.Equal("Codex API — Partial outage", labels[1].Text);
        Assert.DoesNotContain(labels, l => l.Text.Contains("Sora"));
    }

    [Fact]
    public void FilteredOutDisruption_StaysMutedAndSaysWhy()
    {
        var labels = Labels(View(StatusSourceRegistry.OpenAi, "minor", "Partial System Outage",
            [new("Sora", "major_outage")], ["codex"]));
        Assert.Equal("OpenAI status: Partial System Outage · outside your watched components", labels[0].Text);
        Assert.Equal(SystemColors.GrayText, labels[0].ForeColor);
        Assert.DoesNotContain(labels.Skip(1), l => l.Text.Contains("Sora"));   // labels[1] is the no-data sentence
    }

    [Fact]
    public void UnclassifiableDisruption_IsColouredWithNoRows()
    {
        var labels = Labels(View(StatusSourceRegistry.OpenAi, "major", "Service Disruption",
            components: [], filter: ["codex"]));
        Assert.Equal("OpenAI status: Service Disruption", labels[0].Text);
        Assert.Equal(Color.Firebrick, labels[0].ForeColor);
        Assert.Equal(NoDataReason.Default, labels[1].Text);                      // nothing between header and no-data
        Assert.Equal(2, labels.Count);
    }

    [Fact]
    public void NoStatusYet_SaysUnavailable()
    {
        var labels = Labels(new SourceView(StatusSourceRegistry.Claude, null, []));
        Assert.Equal("Claude status: unavailable", labels[0].Text);
    }
}
