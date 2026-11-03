using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;
using ClaudeUsageTray.Core;
using ClaudeUsageTray.Tray;
using Microsoft.Win32;
using Xunit;

namespace ClaudeUsageTray.Tests;

// Throwaway CI diagnostics for the EveryPageFitsWithoutScrolling flake. Always fails so its
// measurements show up in the CI log.
public class ZzProbeLayout
{
    [DllImport("user32.dll")] private static extern uint GetDpiForSystem();

    [Fact]
    public void Probe()
    {
        var lines = new List<string>();
        var start = Process.GetCurrentProcess().StartTime;
        for (int i = 0; i < 20; i++)
        {
            using var dialog = new SettingsDialog(new Settings(), false, true, _ => true,
                TestUpdateOptions.Inert() with { InstalledVersion = "0.6.0", IsInstalled = true, InitialState = UpdateAvailability.Unknown },
                true, new Dictionary<string, IReadOnlyList<string>>());
            dialog.StartPosition = FormStartPosition.Manual;
            dialog.Location = new Point(-4000, -4000);
            dialog.Show();
            var tabs = (TabControl)dialog.Controls.Find("tabs", true).Single();
            var widths = string.Join(",", tabs.TabPages.Cast<TabPage>().Select(p => p.Controls[0].PreferredSize.Width));
            var scale = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Accessibility", "TextScaleFactor", null);
            lines.Add($"t+{(DateTime.Now - start).TotalSeconds:F1}s display={tabs.DisplayRectangle.Size} pages={widths} " +
                $"measure={TextRenderer.MeasureText("Claude Desktop history stale after", Control.DefaultFont).Width} " +
                $"defaultFont={Control.DefaultFont.Name}/{Control.DefaultFont.SizeInPoints} dialogFont={dialog.Font.Name}/{dialog.Font.SizeInPoints} " +
                $"msgBox={SystemFonts.MessageBoxFont?.Name}/{SystemFonts.MessageBoxFont?.SizeInPoints} sysDpi={GetDpiForSystem()} " +
                $"devDpi={dialog.DeviceDpi} textScale={scale ?? "unset"}");
            Thread.Sleep(2000);
        }
        Assert.Fail("PROBE\n" + string.Join("\n", lines));
    }
}
