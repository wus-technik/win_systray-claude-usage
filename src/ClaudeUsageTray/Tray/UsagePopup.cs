using ClaudeUsageTray.Core;

namespace ClaudeUsageTray.Tray;

/// <summary>Compact popup near the tray: both windows as colored bars, countdowns, last-updated line.</summary>
public sealed class UsagePopup : Form
{
    public UsagePopup(UsageSnapshot? snapshot, Settings settings, DateTimeOffset now)
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
            AddWindowRow(layout, "5-hour window", snapshot.FiveHour, settings, now);
            AddWindowRow(layout, "7-day window", snapshot.SevenDay, settings, now);

            var updated = $"Last updated {RelativeTime.Ago(snapshot.FetchedAt, now)}" + (stale ? " · stale" : "");
            layout.Controls.Add(new Label
            {
                Text = updated,
                AutoSize = true,
                ForeColor = stale ? Color.Firebrick : SystemColors.GrayText,
                Margin = new Padding(0, 8, 0, 0),
            });
        }

        Controls.Add(layout);
        PositionNearCursor();
    }

    private static void AddWindowRow(TableLayoutPanel layout, string title, WindowUsage? usage,
        Settings settings, DateTimeOffset now)
    {
        if (usage is null)
        {
            layout.Controls.Add(new Label { Text = $"{title}: no data", AutoSize = true });
            return;
        }

        var severity = SeverityRules.For(usage.Percent, settings.Thresholds.Orange, settings.Thresholds.Red);
        var barColor = severity switch
        {
            Severity.Red => Color.FromArgb(224, 68, 68),
            Severity.Orange => Color.FromArgb(232, 150, 40),
            _ => Color.FromArgb(64, 184, 96),
        };
        var resets = usage.ResetsAt is { } r ? $" · resets in {RelativeTime.In(r, now)}" : "";

        layout.Controls.Add(new Label
        {
            Text = $"{title} — {usage.Percent}%{resets}",
            AutoSize = true,
            Margin = new Padding(0, 6, 0, 2),
        });

        // Custom-drawn bar (ProgressBar can't be recolored per-severity).
        var bar = new Panel { Width = 240, Height = 12, Margin = new Padding(0, 0, 0, 4) };
        int percent = Math.Clamp(usage.Percent, 0, 100);
        bar.Paint += (_, e) =>
        {
            e.Graphics.FillRectangle(SystemBrushes.ControlLight, 0, 0, bar.Width, bar.Height);
            using var brush = new SolidBrush(barColor);
            e.Graphics.FillRectangle(brush, 0, 0, bar.Width * percent / 100, bar.Height);
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
