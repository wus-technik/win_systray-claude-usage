using System.Diagnostics;
using ClaudeUsageTray.Core;

namespace ClaudeUsageTray.Tray;

/// <summary>Compact popup near the tray: windows, scoped limits, and credits as colored bars with
/// countdowns, plus a last-updated line.</summary>
public sealed class UsagePopup : Form
{
    public UsagePopup(DisplayChoice choice, Settings settings, DateTimeOffset now,
        IReadOnlyList<SourceView>? statusSources = null, string? lastFetchStatus = null, string? noDataText = null)
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

        AddPlatformStatus(layout, statusSources, settings, now);

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

    /// <summary>One block per watched source, in registry order. Each banner is the page's own
    /// wording, verbatim — exactly what the user would see on the status page — so two healthy
    /// sources produce two lines rather than one merged sentence neither page wrote. Disruptions sit
    /// above the usage rows and still render in the no-data state.</summary>
    private static void AddPlatformStatus(TableLayoutPanel layout, IReadOnlyList<SourceView>? sources,
        Settings settings, DateTimeOffset now)
    {
        if (sources is null) return;
        foreach (var view in sources)
        {
            var status = view.Status;
            bool stale = status is not null
                && now - status.FetchedAt > TimeSpan.FromMinutes(settings.StalenessMinutes);
            bool relevant = status is not null && StatusDetail.IsRelevant(status, view.Filter);

            layout.Controls.Add(WrappingLabel(
                StatusDetail.Header(view.Source, status, relevant, stale),
                Colour(StatusDetail.Emphasis(status, relevant)),
                new Padding(0, 0, 0, 2)));

            if (status is null || !relevant) continue;

            const int MaxRows = 3;
            foreach (var row in StatusDetail.Rows(status, view.Filter, now, MaxRows))
            {
                layout.Controls.Add(WrappingLabel(row.Text, SystemColors.ControlText, new Padding(0)));
                if (row.Link is not { } link) continue;
                var details = new LinkLabel { Text = "Details", AutoSize = true, Margin = new Padding(0) };
                details.LinkClicked += (_, _) => OpenUrl(link);
                layout.Controls.Add(details);
            }

            int hidden = StatusDetail.HiddenCount(status, view.Filter, MaxRows);
            if (hidden > 0)
            {
                layout.Controls.Add(new Label
                {
                    Text = $"+{hidden} more",
                    AutoSize = true,
                    ForeColor = SystemColors.GrayText,
                    Margin = new Padding(0, 2, 0, 0),
                });
            }

            var page = new LinkLabel
            {
                Text = view.Source.PageLabel, AutoSize = true, Margin = new Padding(0, 2, 0, 4),
            };
            var url = view.Source.PageUrl;
            page.LinkClicked += (_, _) => OpenUrl(url);
            layout.Controls.Add(page);
        }
    }

    /// <summary>DarkOrange for a minor banner; Firebrick for major/critical and for any indicator we
    /// do not recognise, which the Degraded rule already treats as a disruption.</summary>
    private static Color Colour(StatusEmphasis emphasis) => emphasis switch
    {
        StatusEmphasis.Warning => Color.DarkOrange,
        StatusEmphasis.Alert => Color.Firebrick,
        _ => SystemColors.GrayText,
    };

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
