using System.Diagnostics;
using ClaudeUsageTray.Core;

namespace ClaudeUsageTray.Tray;

/// <summary>Compact popup near the tray: windows, scoped limits, and credits as colored bars with
/// countdowns, plus a last-updated line.</summary>
public sealed class UsagePopup : Form
{
    public UsagePopup(DisplayChoice choice, Settings settings, DateTimeOffset now,
        PlatformStatus? platformStatus = null, string? lastFetchStatus = null, string? noDataText = null)
    {
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Text = AppInfo.Name;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Padding = new Padding(12);

        var layout = new TableLayoutPanel
        {
            ColumnCount = 1,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
        };

        AddPlatformStatus(layout, platformStatus, settings, now);

        if (choice.Snapshot is not { } snapshot)
        {
            // Sentences here name what is missing and can run long; wrap at the bar width like the
            // status banner does rather than stretching the form.
            layout.Controls.Add(WrappingLabel(noDataText ?? NoDataReason.Default,
                SystemColors.ControlText, new Padding(0)));
        }
        else
        {
            // Staleness is decided by SourceSelection with each source's own allowance; recomputing
            // it here against StalenessMinutes would flag a desktop-only user most of the time.
            bool stale = choice.Stale;
            AddWindowRow(layout, "5-hour window", snapshot.FiveHour, TimeSpan.FromHours(5), settings, now);
            AddWindowRow(layout, "7-day window", snapshot.SevenDay, TimeSpan.FromDays(7), settings, now);

            var rows = PopupRows.ForScopedLimits(snapshot.ScopedLimits);
            foreach (var limit in rows.Visible) AddScopedRow(layout, limit, settings, now);
            if (rows.HiddenCount > 0)
            {
                layout.Controls.Add(new Label
                {
                    Text = $"+{rows.HiddenCount} more",
                    AutoSize = true,
                    ForeColor = SystemColors.GrayText,
                    Margin = new Padding(0, 2, 0, 0),
                });
            }

            if (snapshot.Credits is { } credits) AddCreditRow(layout, credits, settings);

            var ago = RelativeTime.Ago(snapshot.FetchedAt, now);
            var updated = snapshot.Source == UsageSource.DesktopHistory
                ? $"Claude Desktop history · updated {ago}"
                : $"Last updated {ago}";
            if (stale) updated += " · stale";
            // Long enough with the desktop-source prefix and "· stale" together to exceed the bar
            // width; wrap it like the other page-supplied text rather than stretching the form.
            var updatedLabel = WrappingLabel(updated, stale ? Color.Firebrick : SystemColors.GrayText,
                new Padding(0, 8, 0, 0));
            layout.Controls.Add(updatedLabel);
        }

        if (!string.IsNullOrEmpty(lastFetchStatus))
        {
            layout.Controls.Add(new Label
            {
                Text = $"Fetch: {lastFetchStatus}",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 2, 0, 0),
            });
        }

        Controls.Add(layout);
        PositionNearCursor();
    }

    private static void AddWindowRow(TableLayoutPanel layout, string title, WindowUsage? usage,
        TimeSpan period, Settings settings, DateTimeOffset now)
    {
        if (usage is null)
        {
            layout.Controls.Add(new Label { Text = $"{title}: no data", AutoSize = true });
            return;
        }
        var resets = usage.ResetsAt is { } r ? $" · resets in {RelativeTime.In(r, now)}" : "";
        var elapsed = TimeMarker.ElapsedFraction(usage.ResetsAt, period, now);
        AddCaption(layout, $"{title} — {usage.Percent}%{resets}{PaceSuffix(usage.Percent, elapsed, settings)}");
        AddBar(layout, usage.Percent, SeverityFor(usage.Percent, settings, elapsed), elapsed);
    }

    /// <summary>The page's own banner is the single source of truth — exactly what the user would
    /// see at status.claude.com — so it is shown verbatim. A disruption is the first thing seen,
    /// hence above the usage rows, and it still renders in the no-data state.</summary>
    private static void AddPlatformStatus(TableLayoutPanel layout, PlatformStatus? status,
        Settings settings, DateTimeOffset now)
    {
        bool stale = status is not null
            && now - status.FetchedAt > TimeSpan.FromMinutes(settings.StalenessMinutes);

        string header;
        Color color;
        if (status is null)
        {
            header = "Claude status: unavailable";
            color = SystemColors.GrayText;
        }
        else if (status.Degraded)
        {
            header = $"Claude status: {StatusText(status)}";
            // DarkOrange for a minor banner; Firebrick for major/critical and for any unknown
            // indicator, which the Degraded rule already treats as a disruption.
            color = status.Indicator == "minor" ? Color.DarkOrange : Color.Firebrick;
        }
        else
        {
            header = $"Claude status: {StatusText(status)}";
            color = SystemColors.GrayText;
        }
        if (stale) header += " · stale";

        layout.Controls.Add(WrappingLabel(header, color, new Padding(0, 0, 0, 2)));

        if (status is not { Degraded: true }) return;

        var shown = status.Incidents.Take(3).ToList();
        foreach (var incident in shown)
        {
            layout.Controls.Add(WrappingLabel(DescribeIncident(incident, now),
                SystemColors.ControlText, new Padding(0, 0, 0, 0)));
            if (incident.Shortlink is { } link)
            {
                var details = new LinkLabel { Text = "Details", AutoSize = true, Margin = new Padding(0, 0, 0, 0) };
                details.LinkClicked += (_, _) => OpenUrl(link);
                layout.Controls.Add(details);
            }
        }
        if (status.Incidents.Count > shown.Count)
        {
            layout.Controls.Add(new Label
            {
                Text = $"+{status.Incidents.Count - shown.Count} more",
                AutoSize = true,
                ForeColor = SystemColors.GrayText,
                Margin = new Padding(0, 2, 0, 0),
            });
        }
        var page = new LinkLabel { Text = "status.claude.com", AutoSize = true, Margin = new Padding(0, 2, 0, 4) };
        page.LinkClicked += (_, _) => OpenUrl("https://status.claude.com");
        layout.Controls.Add(page);
    }

    /// <summary>A label for page-supplied text, which has no length limit we control: banner wording
    /// and incident detail wrap at the bar width and grow downwards instead of stretching the
    /// AutoSize form sideways. The bar width is the anchor because it is the popup's one fixed
    /// column — PositionNearCursor clamps the form's position but not its size, so a single long
    /// incident would otherwise push the popup off the screen edge.</summary>
    private static Label WrappingLabel(string text, Color color, Padding margin) => new()
    {
        Text = text,
        AutoSize = true,
        MaximumSize = new Size(UsageBar.DefaultWidth, 0),
        ForeColor = color,
        Margin = margin,
    };

    /// <summary>The page's banner text, verbatim; the indicator name only when the banner is
    /// empty, which the live page does not send but the parser tolerates.</summary>
    private static string StatusText(PlatformStatus status)
        => string.IsNullOrWhiteSpace(status.Description) ? status.Indicator : status.Description;

    /// <summary>One incident row: name, status with initial capital, impact when not
    /// none/missing, affected components, and age.</summary>
    private static string DescribeIncident(PlatformIncident incident, DateTimeOffset now)
    {
        var parts = new List<string> { $"{incident.Name} — {Capitalize(incident.Status)}" };
        if (!string.IsNullOrEmpty(incident.Impact) && incident.Impact != "none")
            parts.Add(incident.Impact);
        if (incident.Components.Count > 0)
            parts.Add(string.Join(", ", incident.Components));
        if (incident.UpdatedAt is { } updated)
            parts.Add($"updated {RelativeTime.Ago(updated, now)}");
        return string.Join(" · ", parts);
    }

    private static string Capitalize(string s)
        => string.IsNullOrEmpty(s) ? s : char.ToUpperInvariant(s[0]) + s[1..];

    private static void OpenUrl(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch { /* a dead link must never take the popup down */ }
    }

    /// <summary>A model- or surface-scoped weekly limit. Not routed through AddWindowRow via a
    /// WindowUsage: that would discard ModelId and IsActive before rendering, making any later
    /// distinction between binding and non-binding caps a parsing change rather than a drawing one.</summary>
    private static void AddScopedRow(TableLayoutPanel layout, ScopedLimit limit,
        Settings settings, DateTimeOffset now)
    {
        var resets = limit.ResetsAt is { } r ? $" · resets in {RelativeTime.In(r, now)}" : "";
        var elapsed = TimeMarker.ElapsedFraction(limit.ResetsAt, TimeSpan.FromDays(7), now);
        AddCaption(layout, $"{limit.Label} weekly — {limit.Percent}%{resets}{PaceSuffix(limit.Percent, elapsed, settings)}");
        AddBar(layout, limit.Percent, SeverityFor(limit.Percent, settings, elapsed), elapsed);
    }

    private static void AddCreditRow(TableLayoutPanel layout, CreditUsage credits, Settings settings)
    {
        AddCaption(layout, $"Credits — {CreditFormat.Describe(credits)}");
        // Credits prefer the payload's own severity: it can encode account state, such as a cap
        // already being reached, that a percentage alone cannot express. Windows and scoped limits
        // deliberately keep the user's configurable thresholds instead.
        AddBar(layout, credits.Percent,
            ParseSeverity(credits.PayloadSeverity) ?? SeverityFor(credits.Percent, settings));

        if (CreditFormat.DescribeState(credits.State) is { } state)
        {
            layout.Controls.Add(new Label
            {
                Text = state,
                AutoSize = true,
                ForeColor = Color.Firebrick,
                Margin = new Padding(0, 0, 0, 4),
            });
        }
    }

    private static void AddCaption(TableLayoutPanel layout, string text)
        => layout.Controls.Add(new Label { Text = text, AutoSize = true, Margin = new Padding(0, 6, 0, 2) });

    private static Severity SeverityFor(int percent, Settings settings, double? elapsedFraction = null)
        => SeverityRules.ForSettings(settings, percent, elapsedFraction);

    /// <summary>Names the number behind the colour, but only when pace is what decided it: a colour
    /// that no longer means "percent used" reads as a bug unless the caption says so.</summary>
    private static string PaceSuffix(int percent, double? elapsedFraction, Settings settings)
    {
        if (!settings.PaceColors) return "";
        var described = PaceFormat.Describe(
            SeverityRules.PaceRatio(percent, elapsedFraction, settings.Thresholds.Red));
        return described.Length == 0 ? "" : $" · {described}";
    }

    private static Severity? ParseSeverity(string? payloadSeverity) => payloadSeverity switch
    {
        "critical" => Severity.Red,
        "warning" => Severity.Orange,
        "normal" => Severity.Green,
        _ => null,
    };

    private static void AddBar(TableLayoutPanel layout, int percent, Severity severity,
        double? elapsedFraction = null)
    {
        var bar = new Panel
        {
            Width = UsageBar.DefaultWidth,
            Height = UsageBar.DefaultHeight,
            Margin = new Padding(0, 0, 0, 4),
        };
        bar.Paint += (_, e) => UsageBar.Paint(e.Graphics, bar.Width, bar.Height, percent, severity, elapsedFraction);
        layout.Controls.Add(bar);
    }

    private void PositionNearCursor()
    {
        // Measure the auto-sized form instead of guessing: long countdown strings, large
        // system fonts, or high DPI would overflow hardcoded estimates.
        PerformLayout();
        var size = PreferredSize;
        var cursor = Cursor.Position;
        var area = Screen.FromPoint(cursor).WorkingArea;
        // Above/centered on the cursor, clamped to the working area (tray is bottom-right).
        var x = Math.Max(area.Left, Math.Min(cursor.X - size.Width / 2, area.Right - size.Width));
        var y = Math.Max(area.Top, Math.Min(cursor.Y - size.Height - 8, area.Bottom - size.Height));
        Location = new Point(x, y);
    }

    protected override void OnDeactivate(EventArgs e)
    {
        base.OnDeactivate(e);
        Close();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        base.OnFormClosed(e);
        Dispose();
    }
}
