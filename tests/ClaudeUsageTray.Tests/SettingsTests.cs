using ClaudeUsageTray.Core;
using System.Linq;
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
            PaceColors = false,
            UseBetaReleases = true,
            ConfigPathOverride = @"C:\alt\.claude.json",
        };
        original.Save(path);
        var loaded = Settings.Load(path);

        Assert.Equal(DisplayMode.FiveHour, loaded.DisplayMode);
        Assert.Equal(30, loaded.Thresholds.Orange);
        Assert.Equal(60, loaded.Thresholds.Red);
        Assert.Equal(5, loaded.StalenessMinutes);
        Assert.False(loaded.RunAtStartup);
        Assert.False(loaded.PaceColors);
        Assert.True(loaded.UseBetaReleases);
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
        Assert.Contains("\"paceColors\": true", json);
        // Null, not false: "never chosen" is a state of its own, and the ring rules read it as
        // "follow the channel this build was installed from".
        Assert.Contains("\"useBetaReleases\": null", json);
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
        Assert.True(s.PaceColors);
        Assert.Null(s.UseBetaReleases);
        Assert.Null(s.ConfigPathOverride);
    }

    [Fact]
    public void Load_ConfigWithoutUseBetaReleases_RecordsNoChoice()
    {
        // A file written before the beta ring existed, or one with the key deleted, must not be read
        // as an opt-out: null is what lets UpdateRing follow the installed channel instead, so the
        // beta installer does not undo itself. See UpdateRingTests.
        var path = PathFor("pre-beta.json");
        File.WriteAllText(path, """{ "displayMode": "fiveHour", "runAtStartup": false }""");
        Assert.Null(Settings.Load(path).UseBetaReleases);
    }

    [Fact]
    public void Load_ExplicitOptOut_IsKeptApartFromNoChoice()
    {
        // The distinction the nullable exists for: this user chose stable, and that choice outranks a
        // beta-channel install.
        var path = PathFor("opted-out.json");
        File.WriteAllText(path, """{ "useBetaReleases": false }""");
        Assert.False(Settings.Load(path).UseBetaReleases);
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
        Assert.True(s.PaceColors);              // default
    }

    [Fact]
    public void Load_ConfigWithoutPaceColors_KeepsPaceColouringOn()
    {
        // A settings.json written before pace colouring existed must not opt out of it.
        var path = PathFor("pre-pace.json");
        File.WriteAllText(path, """{ "thresholds": { "orange": 30, "red": 60 }, "stalenessMinutes": 5 }""");
        var s = Settings.Load(path);
        Assert.True(s.PaceColors);
        Assert.Equal(30, s.Thresholds.Orange);
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

    [Fact]
    public void RoundTrip_PreservesDesktopKeys()
    {
        var path = PathFor("settings.json");
        new Settings
        {
            DesktopStalenessHours = 6,
            DesktopHistoryPathOverride = @"C:\alt\plan-usage-history.json",
        }.Save(path);
        var loaded = Settings.Load(path);

        Assert.Equal(6, loaded.DesktopStalenessHours);
        Assert.Equal(@"C:\alt\plan-usage-history.json", loaded.DesktopHistoryPathOverride);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-4")]
    public void Load_NonPositiveDesktopStaleness_ResetsToDefault(string value)
    {
        var path = PathFor("settings.json");
        File.WriteAllText(path, $$$"""{ "desktopStalenessHours": {{{value}}}, "stalenessMinutes": 7 }""");
        var loaded = Settings.Load(path);

        Assert.Equal(ThresholdRules.DefaultDesktopStalenessHours, loaded.DesktopStalenessHours);
        Assert.Equal(7, loaded.StalenessMinutes); // only the invalid field resets
    }

    [Fact]
    public void Load_FileWithoutDesktopKeys_UsesDefaults()
    {
        var path = PathFor("settings.json");
        File.WriteAllText(path, """{ "stalenessMinutes": 15 }""");
        var loaded = Settings.Load(path);

        Assert.Equal(3, loaded.DesktopStalenessHours);
        Assert.Null(loaded.DesktopHistoryPathOverride);
    }

    private static Settings LoadJson(string json)
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        File.WriteAllText(path, json);
        try { return Settings.Load(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void NoStatusSourcesKey_KeepsTodaysBehaviour()
    {
        var s = LoadJson("""{ "displayMode": "both" }""");
        var enabled = s.EnabledSources();
        Assert.Equal(["claude"], enabled.Select(e => e.Source.Id));
        Assert.Empty(enabled[0].Filter);
    }

    [Fact]
    public void EnablingOpenAi_UsesItsDefaultFilterWhenNoneIsGiven()
    {
        var s = LoadJson("""{ "statusSources": { "openai": { "enabled": true } } }""");
        var openAi = s.EnabledSources().Single(e => e.Source.Id == "openai");
        Assert.Equal(StatusSourceRegistry.OpenAi.DefaultComponents, openAi.Filter);
    }

    [Fact]
    public void GivenComponents_AreNormalized()
    {
        var s = LoadJson(
            """{ "statusSources": { "openai": { "enabled": true, "components": [" codex ", "", "CODEX"] } } }""");
        Assert.Equal(["codex"], s.EnabledSources().Single(e => e.Source.Id == "openai").Filter);
    }

    [Fact]
    public void UnknownSourceId_IsDropped()
    {
        var s = LoadJson("""{ "statusSources": { "gemini": { "enabled": true } } }""");
        Assert.Equal(["claude"], s.EnabledSources().Select(e => e.Source.Id));
    }

    /// <summary>Per-entry fallback: a malformed source entry must not reset unrelated settings the
    /// way a whole-file JsonException would.</summary>
    [Fact]
    public void MalformedEntry_ResetsThatEntryAlone()
    {
        var s = LoadJson("""
            {
              "stalenessMinutes": 42,
              "thresholds": { "orange": 30, "red": 70 },
              "statusSources": { "openai": { "enabled": "yes please", "components": 7 } }
            }
            """);
        Assert.Equal(42, s.StalenessMinutes);
        Assert.Equal(30, s.Thresholds.Orange);
        Assert.Equal(70, s.Thresholds.Red);
        Assert.Equal(["claude"], s.EnabledSources().Select(e => e.Source.Id));   // openai back to disabled
    }

    [Fact]
    public void StatusSources_RoundTripThroughSave()
    {
        var path = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName() + ".json");
        try
        {
            var s = new Settings();
            s.StatusSources["openai"] = new StatusSourceSettings { Enabled = true, Components = ["codex"] };
            s.Save(path);
            var loaded = Settings.Load(path);
            Assert.Equal(["claude", "openai"], loaded.EnabledSources().Select(e => e.Source.Id));
            Assert.Equal(["codex"], loaded.EnabledSources().Single(e => e.Source.Id == "openai").Filter);
        }
        finally { File.Delete(path); }
    }
}
