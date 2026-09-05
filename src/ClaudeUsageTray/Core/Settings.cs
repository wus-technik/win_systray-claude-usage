using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeUsageTray.Core;

public enum DisplayMode { FiveHour, SevenDay, Both }

public sealed class Thresholds
{
    public int Orange { get; set; } = ThresholdRules.DefaultOrange;
    public int Red { get; set; } = ThresholdRules.DefaultRed;
}

/// <summary>The threshold invariant, kept out of both <see cref="Settings.NormalizeFields"/> and the
/// settings dialog so the file loader and the UI cannot disagree about what a valid pair is.</summary>
public static class ThresholdRules
{
    public const int DefaultOrange = 50;
    public const int DefaultRed = 85;
    public const int DefaultStalenessMinutes = 15;

    /// <summary>The desktop app samples usage only while someone works in it, so gaps of an hour are
    /// normal; a minutes-scale cutoff would flag a desktop-only user as stale most of the time.</summary>
    public const int DefaultDesktopStalenessHours = 3;

    public static bool IsValidPair(int orange, int red)
        => orange >= 0 && orange < red && red <= 100;

    /// <summary>The nearest valid pair, moving as little as possible. Orange is the anchor — in the
    /// dialog it is the value the user just set — so red yields to it, except at the very top where
    /// orange has to step back to leave red somewhere to go.</summary>
    public static (int Orange, int Red) Clamp(int orange, int red)
    {
        orange = Math.Clamp(orange, 0, 99);
        return (orange, Math.Clamp(red, orange + 1, 100));
    }
}

public sealed class Settings
{
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Both;
    public Thresholds Thresholds { get; set; } = new();
    public int StalenessMinutes { get; set; } = ThresholdRules.DefaultStalenessMinutes;

    /// <summary>Staleness allowance for the Claude Desktop history source, in hours. Separate from
    /// <see cref="StalenessMinutes"/> because the two sources have cadences an order of magnitude apart.</summary>
    public int DesktopStalenessHours { get; set; } = ThresholdRules.DefaultDesktopStalenessHours;

    public bool RunAtStartup { get; set; } = true;

    /// <summary>Colour bars and badges by usage against elapsed time rather than by raw percent.
    /// Off falls back to the pure Thresholds comparison everywhere.</summary>
    public bool PaceColors { get; set; } = true;

    /// <summary>Follow the beta ring (<see cref="UpdateRing.BetaChannel"/>) instead of stable, so
    /// pre-release builds are offered.
    ///
    /// Deliberately nullable: null means the user has never made the choice, and then the channel the
    /// build was installed from decides (<see cref="UpdateRing.For"/>) — a normal install stays on
    /// stable, one from the beta Setup.exe stays on betas instead of being offered stable as a
    /// downgrade on its first check. An explicit false is a real opt-out and does leave the ring.</summary>
    public bool? UseBetaReleases { get; set; }

    public string? ConfigPathOverride { get; set; }

    /// <summary>Explicit path to the desktop app's plan-usage-history.json. File-only, like
    /// <see cref="ConfigPathOverride"/>; two real locations already exist in the wild and a third
    /// should not need a release.</summary>
    public string? DesktopHistoryPathOverride { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "ClaudeUsageTray", "settings.json");

    public static Settings Load(string path)
    {
        var settings = new Settings();
        try
        {
            if (File.Exists(path))
                settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path), JsonOptions) ?? new Settings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // malformed/unreadable file → full defaults
        }
        settings.NormalizeFields();
        return settings;
    }

    public void Save(string path)
    {
        NormalizeFields();
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, JsonOptions));
        File.Move(temporary, path, overwrite: true);
    }

    /// <summary>Per-field fallback: each invalid field resets to its default, valid fields survive.</summary>
    private void NormalizeFields()
    {
        if (!Enum.IsDefined(DisplayMode)) DisplayMode = DisplayMode.Both;
        Thresholds ??= new Thresholds();
        // An inconsistent pair resets as a pair: the file gives no way to tell which of the two the
        // user meant, so guessing one and keeping the other would invent a threshold they never set.
        if (!ThresholdRules.IsValidPair(Thresholds.Orange, Thresholds.Red))
        {
            Thresholds.Orange = ThresholdRules.DefaultOrange;
            Thresholds.Red = ThresholdRules.DefaultRed;
        }
        if (StalenessMinutes < 0) StalenessMinutes = ThresholdRules.DefaultStalenessMinutes;
        if (DesktopStalenessHours <= 0) DesktopStalenessHours = ThresholdRules.DefaultDesktopStalenessHours;
    }
}
