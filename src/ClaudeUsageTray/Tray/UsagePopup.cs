using ClaudeUsageTray.Core;

namespace ClaudeUsageTray.Tray;

/// <summary>Compact popup near the tray: windows, scoped limits, and credits as colored bars with
/// countdowns, plus a last-updated line.</summary>
public sealed class UsagePopup : Form
{
    public UsagePopup(UsageSnapshot? snapshot, Settings settings, DateTimeOffset now, string? lastFetchStatus = null)
    {
        FormBorderStyle = FormBorderStyle.FixedToolWindow;
        Text = "Claude Usage";
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

        if (snapshot is null)
        {
            layout.Controls.Add(new Label
            {
                Text = "No Claude usage data yet — run Claude Code.",
                AutoSize = true,
            });
        }
        else
        {
            bool stale = now - snapshot.FetchedAt > TimeSpan.FromMinutes(settings.StalenessMinutes);
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

            var updated = $"Last updated {RelativeTime.Ago(snapshot.FetchedAt, now)}" + (stale ? " · stale" : "");
            layout.Controls.Add(new Label
            {
                Text = updated,
                AutoSize = true,
                ForeColor = stale ? Color.Firebrick : SystemColors.GrayText,
                Margin = new Padding(0, 8, 0, 0),
            });
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
        => settings.PaceColors
            ? SeverityRules.ForPace(percent, elapsedFraction, settings.Thresholds.Orange, settings.Thresholds.Red)
            : SeverityRules.For(percent, settings.Thresholds.Orange, settings.Thresholds.Red);

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

    /// <summary>Custom-drawn bar (ProgressBar can't be recolored per-severity). elapsedFraction, when
    /// non-null, marks how far the clock has moved through the limit's reset period: fill short of the
    /// marker is burning slower than the clock, fill past it is on track to hit the cap early.</summary>
    private static void AddBar(TableLayoutPanel layout, int percent, Severity severity,
        double? elapsedFraction = null)
    {
        var barColor = severity switch
        {
            Severity.Red => Color.FromArgb(224, 68, 68),
            Severity.Orange => Color.FromArgb(232, 150, 40),
            _ => Color.FromArgb(64, 184, 96),
        };
        var bar = new Panel { Width = 240, Height = 12, Margin = new Padding(0, 0, 0, 4) };
        var filled = Math.Clamp(percent, 0, 100);
        bar.Paint += (_, e) =>
        {
            e.Graphics.FillRectangle(SystemBrushes.ControlLight, 0, 0, bar.Width, bar.Height);
            using var brush = new SolidBrush(barColor);
            e.Graphics.FillRectangle(brush, 0, 0, bar.Width * filled / 100, bar.Height);
            if (elapsedFraction is { } fraction)
            {
                // A full-height band, not the inset notch this started as: the border overdraws the
                // outer row of anything touching an edge, so 3px ticks left just 4 black pixels, which
                // read as grey at 1:1 on a 96-DPI screen however dark the pen. Width is filled as a
                // rectangle rather than stroked with a wide pen because GDI+ centres pen strokes on the
                // coordinate, which would put a column outside the range checked below.
                const int markerWidth = 2;
                // Span markerWidth columns inside 1..Width-2: the border drawn below owns x=0 and
                // x=Width-1 and would otherwise swallow the marker at fraction 0.0 and 1.0.
                var x = 1 + (int)Math.Round((bar.Width - 2 - markerWidth) * fraction);
                using var markerBrush = new SolidBrush(SystemColors.ControlText);
                e.Graphics.FillRectangle(markerBrush, x, 0, markerWidth, bar.Height);
            }
            e.Graphics.DrawRectangle(SystemPens.ControlDark, 0, 0, bar.Width - 1, bar.Height - 1);
        };
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
