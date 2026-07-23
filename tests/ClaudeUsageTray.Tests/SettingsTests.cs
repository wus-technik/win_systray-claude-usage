using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class SettingsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("cut-settings-").FullName;
    private string PathFor(string name) => Path.Combine(_dir, name);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact]
    public void RoundTrip_PreservesAllValues()
    {
        var path = PathFor("sub/settings.json"); // Save must create the directory
        var original = new Settings
        {
            DisplayMode = DisplayMode.FiveHour,
            Thresholds = new Thresholds { Orange = 30, Red = 60 },
            StalenessMinutes = 5,
            RunAtStartup = false,
            ConfigPathOverride = @"C:\alt\.claude.json",
        };
        original.Save(path);
        var loaded = Settings.Load(path);

        Assert.Equal(DisplayMode.FiveHour, loaded.DisplayMode);
        Assert.Equal(30, loaded.Thresholds.Orange);
        Assert.Equal(60, loaded.Thresholds.Red);
        Assert.Equal(5, loaded.StalenessMinutes);
        Assert.False(loaded.RunAtStartup);
        Assert.Equal(@"C:\alt\.claude.json", loaded.ConfigPathOverride);
    }

    [Fact]
    public void Save_WritesDocumentedCamelCaseKeys()
    {
        var path = PathFor("settings.json");
        new Settings().Save(path);
        var json = File.ReadAllText(path);
        Assert.Contains("\"displayMode\": \"both\"", json);
        Assert.Contains("\"orange\": 50", json);
        Assert.Contains("\"red\": 85", json);
        Assert.Contains("\"stalenessMinutes\": 15", json);
        Assert.Contains("\"runAtStartup\": true", json);
    }

    [Fact]
    public void Load_MissingFile_ReturnsDefaults()
    {
        var s = Settings.Load(PathFor("nope.json"));
        Assert.Equal(DisplayMode.Both, s.DisplayMode);
        Assert.Equal(50, s.Thresholds.Orange);
        Assert.Equal(85, s.Thresholds.Red);
        Assert.Equal(15, s.StalenessMinutes);
        Assert.True(s.RunAtStartup);
        Assert.Null(s.ConfigPathOverride);
    }

    [Fact]
    public void Load_PartialFile_FillsDefaultsForMissingKeys()
    {
        var path = PathFor("partial.json");
        File.WriteAllText(path, """{ "displayMode": "sevenDay" }""");
        var s = Settings.Load(path);
        Assert.Equal(DisplayMode.SevenDay, s.DisplayMode);
        Assert.Equal(50, s.Thresholds.Orange);  // default
        Assert.Equal(15, s.StalenessMinutes);   // default
        Assert.True(s.RunAtStartup);            // default
    }

    [Fact]
    public void Load_MalformedFile_ReturnsDefaults()
    {
        var path = PathFor("broken.json");
        File.WriteAllText(path, "{ nope");
        Assert.Equal(DisplayMode.Both, Settings.Load(path).DisplayMode);
    }

    [Theory]
    [InlineData("{ \"thresholds\": null }")]
    [InlineData("{ \"displayMode\": 999 }")]
    [InlineData("{ \"thresholds\": { \"orange\": 90, \"red\": 50 } }")] // inconsistent pair resets as a pair
    [InlineData("{ \"stalenessMinutes\": -1 }")]
    public void Load_InvalidValues_ResetToSafeDefaults(string json)
    {
        var path = PathFor("invalid.json");
        File.WriteAllText(path, json);
        var s = Settings.Load(path);
        Assert.Equal(DisplayMode.Both, s.DisplayMode);
        Assert.Equal(50, s.Thresholds.Orange);
        Assert.Equal(85, s.Thresholds.Red);
        Assert.Equal(15, s.StalenessMinutes);
    }

    [Fact]
    public void Load_InvalidField_ResetsOnlyThatField()
    {
        // One typo must not nuke the user's other hand-edited settings.
        var path = PathFor("mixed.json");
        File.WriteAllText(path, """
            { "displayMode": "fiveHour", "stalenessMinutes": -1,
              "thresholds": { "orange": 30, "red": 60 },
              "runAtStartup": false, "configPathOverride": "C:\\alt\\.claude.json" }
            """);
        var s = Settings.Load(path);
        Assert.Equal(15, s.StalenessMinutes);                     // invalid → default
        Assert.Equal(DisplayMode.FiveHour, s.DisplayMode);        // valid → preserved
        Assert.Equal(30, s.Thresholds.Orange);                    // valid → preserved
        Assert.Equal(60, s.Thresholds.Red);                       // valid → preserved
        Assert.False(s.RunAtStartup);                             // valid → preserved
        Assert.Equal(@"C:\alt\.claude.json", s.ConfigPathOverride); // valid → preserved
    }

    [Fact]
    public void DefaultPath_IsUnderAppData()
        => Assert.Equal(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "ClaudeUsageTray", "settings.json"),
            Settings.DefaultPath);
}
