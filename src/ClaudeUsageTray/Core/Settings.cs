using System.Text.Json;
using System.Text.Json.Serialization;

namespace ClaudeUsageTray.Core;

public enum DisplayMode { FiveHour, SevenDay, Both }

public sealed class Thresholds
{
    public int Orange { get; set; } = 50;
    public int Red { get; set; } = 85;
}

public sealed class Settings
{
    public DisplayMode DisplayMode { get; set; } = DisplayMode.Both;
    public Thresholds Thresholds { get; set; } = new();
    public int StalenessMinutes { get; set; } = 15;
    public bool RunAtStartup { get; set; } = true;

    /// <summary>Colour bars and badges by usage against elapsed time rather than by raw percent.
    /// Off falls back to the pure Thresholds comparison everywhere.</summary>
    public bool PaceColors { get; set; } = true;
    public string? ConfigPathOverride { get; set; }

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
        if (Thresholds.Orange is < 0 or > 100) Thresholds.Orange = 50;
        // Red must be > Orange and ≤ 100; an inconsistent pair resets as a pair.
        if (Thresholds.Red <= Thresholds.Orange || Thresholds.Red > 100)
        {
            Thresholds.Orange = 50;
            Thresholds.Red = 85;
        }
        if (StalenessMinutes < 0) StalenessMinutes = 15;
    }
}
