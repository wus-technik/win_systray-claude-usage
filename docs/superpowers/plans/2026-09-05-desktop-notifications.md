# Desktop Notifications Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Raise exactly one Windows toast when a usage limit crosses into red (or orange, if configured) and one each way when a watched status page changes state, from the data the tray already computes, without a single false toast at startup, on a settings edit, or from clock flapping.

**Architecture:** Two pure/clock-free types in `Core/` decide everything: `UsageValues` enumerates the notifiable values of a snapshot with the *same* severity the popup and badge draw (and both are rewired onto it, so the toast cannot disagree with the bar), and `NotificationRules` remembers what it last saw, owns arming, hysteresis, the settings fingerprint and the per-source status state, and returns zero or one `Notification` per evaluation. `Tray/ToastPresenter` shows a real WinRT toast under Velopack's shortcut AUMID and routes a click to `TrayApp.ShowPopup()`; it decides nothing and never throws. `TrayApp.Render()` is the single call site.

**Tech Stack:** .NET 10 on TFM `net10.0-windows10.0.19041.0` (app + its test project only), C# 13, WinForms, `Windows.UI.Notifications` via the Windows SDK projection, `System.Text.Json`, xUnit.

**Spec:** `docs/superpowers/specs/2026-09-05-desktop-notifications-design.md` — read it first. Every rule below is argued there; this plan only sequences it.

**Issue:** [#14](https://github.com/wus-technik/win_systray-claude-usage/issues/14). Every commit body ends with `Refs #14`; the last task's commit uses `Closes #14`.

## Global Constraints

- **Pure logic goes in `src/ClaudeUsageTray/Core/`**, WinForms/WinRT only in `src/ClaudeUsageTray/Tray/`. No clocks and no threads in `Core/`: every time-dependent function takes a caller-supplied `DateTimeOffset now`.
- **Nothing throws.** `Settings.Load` keeps its per-field fallback. `ToastPresenter` wraps every WinRT call and degrades to a permanent no-op on failure; a `dotnet run` from source (unregistered AUMID) must stay silent.
- **The token is read-only and never logged.** `fetch.log` carries percentages, outcomes and the fixed keys `5h`, `7d`, `credits` — **never scoped-limit labels** (they are account-specific model names), money or currency. Notifications about scoped limits are logged by count.
- **Labels come from the payload.** Toast text uses `UsageValues` labels and the status page's own words; no hardcoded model or component list.
- **Absent data means no row**, and therefore no notifiable value.
- **Keys** are `"5h"`, `"7d"`, `"credits"` and each `ScopedLimit.Label`, compared `OrdinalIgnoreCase`.
- **Level semantics:** `NotifyLevel.Red` = crossing into `Severity.Red`; `NotifyLevel.Orange` = crossing into Orange *or* Red, once. Re-arm only on return to `Green`.
- **Fingerprint** = `Thresholds.Orange`, `Thresholds.Red`, `PaceColors`, `StalenessMinutes`, `DesktopStalenessHours`, `UsageNotifications.Level`. The on/off switches are *not* in it.
- **Arming:** `Snapshot`, `Unauthorized`, `NoToken` arm; `RateLimited`, `Failed` do not; the third concluded outcome of any kind arms. Arming clears all usage state so the next evaluation is a baseline.
- **Status state** is `StatusDetail.IsRelevant(status, filter)`, never `status.Degraded`; a null `Status` is not a reading.
- **AUMID** is `"velopack." + AppInfo.PackId`; `Group = "claudeusagetray"`; `Tag = "usage"` or `"status:{sourceId}"`; `Argument = "open-popup"`.
- **TFM override** lives in exactly two csproj files (`src/ClaudeUsageTray`, `tests/ClaudeUsageTray.Tests`). `Directory.Build.props` and the setup-stub projects stay on `net10.0-windows`.
- **Record shapes deviate from the spec in two additive ways**, decided here so every task agrees: `UsageValue` carries a fifth field `DateTimeOffset? ResetsAt` (the toast expiry needs it), and `Notification` carries `string Tag` and `DateTimeOffset? ExpiresAt` after the spec's three fields (the presenter needs both and must not compute either). Record this in the spec in Task 8.
- **Test commands:** `dotnet test` (all), `dotnet test --filter FullyQualifiedName~ClassName` (one class). CI runs `dotnet test -c Release`.
- **Style:** match surrounding code; XML doc comments explain *why*. No linter. Commits: conventional prefix, `Refs #14` in the body, no AI co-author trailer.

---

### Task 1: TFM bump, `AppInfo.PackId` / `Aumid`, and the pack-id drift test

**Files:**
- Modify: `src/ClaudeUsageTray/ClaudeUsageTray.csproj:2-4`
- Modify: `tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj:2-5`
- Modify: `src/ClaudeUsageTray/Core/AppInfo.cs`
- Test: `tests/ClaudeUsageTray.Tests/AppInfoTests.cs`

**Interfaces:**
- Produces: `AppInfo.PackId` (`const string`, `"WusTechnik.ClaudeUsageTray"`), `AppInfo.Aumid` (`const string`, `"velopack.WusTechnik.ClaudeUsageTray"`).

- [ ] **Step 1: Write the failing tests**

Append to `tests/ClaudeUsageTray.Tests/AppInfoTests.cs` inside the class:

```csharp
    [Fact]
    public void TheAumidIsVelopackDotPackId()
        => Assert.Equal("velopack.WusTechnik.ClaudeUsageTray", AppInfo.Aumid);

    /// <summary>Velopack writes "velopack.{packId}" onto the Start Menu shortcut, and that string is
    /// the only thing that lets a toast show. Nothing else checks that the id in AppInfo and the id
    /// handed to vpk agree, and a mismatch costs every notification with no other symptom.</summary>
    [Theory]
    [InlineData("build/build-release.ps1")]
    [InlineData(".github/workflows/release.yml")]
    public void ThePackIdMatchesWhatVpkIsGiven(string relativePath)
    {
        var text = File.ReadAllText(Path.Combine(RepoRoot(), relativePath));
        Assert.Contains($"--packId {AppInfo.PackId} ", text);
    }

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "build", "build-release.ps1")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("repository root not found above " + AppContext.BaseDirectory);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~AppInfoTests`
Expected: build error `'AppInfo' does not contain a definition for 'Aumid'` / `'PackId'`.

- [ ] **Step 3: Add the constants**

In `src/ClaudeUsageTray/Core/AppInfo.cs`, after `ProjectUrl`:

```csharp
    /// <summary>The Velopack pack id — `vpk pack --packId`. Must equal the id in build-release.ps1 and
    /// release.yml; AppInfoTests reads both files to make sure. Also the install directory name under
    /// %LOCALAPPDATA% and the SingleInstance mutex suffix.</summary>
    public const string PackId = "WusTechnik.ClaudeUsageTray";

    /// <summary>The Application User Model ID Velopack stamps on the Start Menu shortcut. Toasts for
    /// an unpackaged app only show under a shortcut's AUMID, and this is the one we already have.</summary>
    public const string Aumid = "velopack." + PackId;
```

- [ ] **Step 4: Bump the two TFMs**

`src/ClaudeUsageTray/ClaudeUsageTray.csproj`, first `<PropertyGroup>`, add as the first child:

```xml
    <!-- Windows SDK projection for toast notifications (Windows.UI.Notifications). Overrides the
         repo default in Directory.Build.props deliberately: the NativeAOT setup stub has no WinForms
         and no notifications, and must not pull the SDK into its ILC build. Costs ~24 MB in the
         self-contained publish (Microsoft.Windows.SDK.NET.dll), paid once thanks to Velopack deltas;
         sets the supported-OS floor at Windows 10 2004. -->
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
```

`tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj`, first `<PropertyGroup>`, add as the first child:

```xml
    <!-- Only because it references the app; nothing here touches WinRT. -->
    <TargetFramework>net10.0-windows10.0.19041.0</TargetFramework>
```

- [ ] **Step 5: Run the whole suite**

Run: `dotnet test`
Expected: restore pulls `Microsoft.Windows.SDK.NET.Ref`; all tests pass, including the two new ones. If restore fails offline, run `dotnet restore` once online — the projection is a NuGet package.

Also confirm the stub is untouched: `dotnet build src/ClaudeUsageTraySetupStub` still reports `net10.0-windows` in its output path.

- [ ] **Step 6: Commit**

```bash
git add src/ClaudeUsageTray/ClaudeUsageTray.csproj tests/ClaudeUsageTray.Tests/ClaudeUsageTray.Tests.csproj src/ClaudeUsageTray/Core/AppInfo.cs tests/ClaudeUsageTray.Tests/AppInfoTests.cs
git commit -m "build: target the Windows 10.0.19041 SDK for toasts; pin the pack id in AppInfo" -m "The AUMID a toast needs is velopack.<packId>, so the pack id becomes a tested constant that must match build-release.ps1 and release.yml." -m "Refs #14"
```

---

### Task 2: Settings — `NotifyLevel`, `usageNotifications`, per-source `notify`

**Files:**
- Modify: `src/ClaudeUsageTray/Core/Settings.cs`
- Test: `tests/ClaudeUsageTray.Tests/SettingsTests.cs`

**Interfaces:**
- Produces: `enum NotifyLevel { Orange, Red }`; `sealed class UsageNotificationSettings { bool Enabled = true; NotifyLevel Level = NotifyLevel.Red; }`; `Settings.UsageNotifications` (non-null after `Load`/`NormalizeFields`); `StatusSourceSettings.Notify` (`bool`, default `true`); `Settings.NotifyFor(string sourceId)` → `bool`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/ClaudeUsageTray.Tests/SettingsTests.cs` inside the class:

```csharp
    [Fact]
    public void Notifications_DefaultOn_RedOnly_PerSourceOn()
    {
        var s = LoadJson("""{ "displayMode": "both" }""");
        Assert.True(s.UsageNotifications.Enabled);
        Assert.Equal(NotifyLevel.Red, s.UsageNotifications.Level);
        Assert.True(s.StatusSources["claude"]!.Notify);
        Assert.True(s.StatusSources["openai"]!.Notify);
        Assert.True(s.NotifyFor("claude"));
    }

    [Fact]
    public void Notifications_RoundTripThroughSave()
    {
        var path = PathFor("settings.json");
        var s = new Settings { UsageNotifications = new UsageNotificationSettings { Enabled = false, Level = NotifyLevel.Orange } };
        s.StatusSources["openai"] = new StatusSourceSettings { Enabled = false, Notify = false, Components = ["codex"] };
        s.Save(path);
        var json = File.ReadAllText(path);
        Assert.Contains("\"usageNotifications\"", json);
        Assert.Contains("\"level\": \"orange\"", json);
        Assert.Contains("\"notify\": false", json);

        var loaded = Settings.Load(path);
        Assert.False(loaded.UsageNotifications.Enabled);
        Assert.Equal(NotifyLevel.Orange, loaded.UsageNotifications.Level);
        // Stored independently of enabled: turning the source off and on again keeps the choice.
        Assert.False(loaded.StatusSources["openai"]!.Enabled);
        Assert.False(loaded.StatusSources["openai"]!.Notify);
        Assert.Equal(["codex"], loaded.StatusSources["openai"]!.Components);
    }

    [Fact]
    public void Notifications_InvalidLevel_ResetsOnlyTheLevel()
    {
        var s = LoadJson("""
            { "stalenessMinutes": 42,
              "usageNotifications": { "enabled": false, "level": "purple" } }
            """);
        Assert.Equal(NotifyLevel.Red, s.UsageNotifications.Level);   // invalid → default
        Assert.False(s.UsageNotifications.Enabled);                  // valid sibling → preserved
        Assert.Equal(42, s.StalenessMinutes);                        // unrelated → preserved
    }

    [Fact]
    public void Notifications_MalformedObject_FallsBackToDefaultsAlone()
    {
        var s = LoadJson("""{ "stalenessMinutes": 42, "usageNotifications": 7 }""");
        Assert.True(s.UsageNotifications.Enabled);
        Assert.Equal(NotifyLevel.Red, s.UsageNotifications.Level);
        Assert.Equal(42, s.StalenessMinutes);
    }

    [Fact]
    public void SourceNotify_MissingOrMalformed_IsTrue()
    {
        Assert.True(LoadJson("""{ "statusSources": { "openai": { "enabled": true } } }""")
            .StatusSources["openai"]!.Notify);
        // A malformed entry resets that entry alone (existing rule), and the reset entry notifies.
        Assert.True(LoadJson("""{ "statusSources": { "openai": { "enabled": true, "notify": "maybe" } } }""")
            .StatusSources["openai"]!.Notify);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SettingsTests`
Expected: build errors for `NotifyLevel`, `UsageNotifications`, `Notify`, `NotifyFor`.

- [ ] **Step 3: Implement**

In `src/ClaudeUsageTray/Core/Settings.cs`:

After `public enum DisplayMode { FiveHour, SevenDay, Both }` add:

```csharp
/// <summary>The lowest severity a usage crossing must reach to notify. Not <see cref="Severity"/>:
/// its Green member would be a meaningless notification level.</summary>
public enum NotifyLevel { Orange, Red }

/// <summary>The usage trigger's switch and level. Its own object because it carries a level; the
/// status triggers are a plain bool on each source.</summary>
public sealed class UsageNotificationSettings
{
    public bool Enabled { get; set; } = true;
    public NotifyLevel Level { get; set; } = NotifyLevel.Red;
}

/// <summary>Reads usageNotifications field by field so a bad level resets the level alone. The
/// default enum converter would throw JsonException for "purple", and Settings.Load answers a
/// JsonException with full defaults — which would reset thresholds and display mode for a typo.</summary>
public sealed class TolerantUsageNotificationsConverter : JsonConverter<UsageNotificationSettings>
{
    public override UsageNotificationSettings Read(ref Utf8JsonReader reader, Type typeToConvert,
        JsonSerializerOptions options)
    {
        var result = new UsageNotificationSettings();
        if (reader.TokenType != JsonTokenType.StartObject) { reader.Skip(); return result; }

        using var doc = JsonDocument.ParseValue(ref reader);
        foreach (var property in doc.RootElement.EnumerateObject())
        {
            if (property.NameEquals("enabled") && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                result.Enabled = property.Value.GetBoolean();
            else if (property.NameEquals("level") && property.Value.ValueKind == JsonValueKind.String
                && Enum.TryParse<NotifyLevel>(property.Value.GetString(), ignoreCase: true, out var level)
                && Enum.IsDefined(level))
                result.Level = level;
        }
        return result;
    }

    public override void Write(Utf8JsonWriter writer, UsageNotificationSettings value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteBoolean("enabled", value.Enabled);
        writer.WriteString("level", JsonNamingPolicy.CamelCase.ConvertName(value.Level.ToString()));
        writer.WriteEndObject();
    }
}
```

In `StatusSourceSettings` add after `Enabled`:

```csharp
    /// <summary>Toast when this source's relevant state flips. Defaults on: whoever enabled a page
    /// did it to hear about its outages. Independent of Enabled so an off/on cycle keeps the choice.</summary>
    public bool Notify { get; set; } = true;
```

In `Settings`, after the `StatusSources` property:

```csharp
    /// <summary>The usage trigger. Non-null after Load; the converter keeps a bad level from resetting
    /// unrelated settings.</summary>
    [JsonConverter(typeof(TolerantUsageNotificationsConverter))]
    public UsageNotificationSettings UsageNotifications { get; set; } = new();
```

In `NormalizeFields()`, after the `DesktopStalenessHours` line:

```csharp
        UsageNotifications ??= new UsageNotificationSettings();
        if (!Enum.IsDefined(UsageNotifications.Level)) UsageNotifications.Level = NotifyLevel.Red;
```

In the `sources[source.Id] = entry is null ? ... : new StatusSourceSettings { ... }` rebuild, add `Notify = entry.Notify,` to the non-null branch (the null branch's default `true` already applies).

After `EnabledSources()` add:

```csharp
    /// <summary>Whether a source's transitions should toast. Unknown ids are silent.</summary>
    public bool NotifyFor(string sourceId)
        => StatusSources.TryGetValue(sourceId, out var entry) && entry is { Notify: true };
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter FullyQualifiedName~SettingsTests`
Expected: all pass. The malformed-`notify` case works because the whole entry fails to deserialize (existing tolerant converter), arrives null, and is rebuilt with the default.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/Settings.cs tests/ClaudeUsageTray.Tests/SettingsTests.cs
git commit -m "feat: notification settings — usageNotifications {enabled, level} and per-source notify" -m "Per-field fallback throughout: an invalid level resets to red, a malformed notify to true, and every other setting survives untouched." -m "Refs #14"
```

---

### Task 3: `UsageValues` — one severity computation, and the popup and badge rewired onto it

**Files:**
- Create: `src/ClaudeUsageTray/Core/UsageValues.cs`
- Modify: `src/ClaudeUsageTray/Core/SeverityRules.cs` (add `FromPayload`)
- Modify: `src/ClaudeUsageTray/Tray/UsagePopup.cs:97-100, 192-195, 204-205, 222-223, 235-241`
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs:325-330`
- Test: `tests/ClaudeUsageTray.Tests/UsageValuesTests.cs`

**Interfaces:**
- Consumes: `SeverityRules.ForSettings(Settings, int, double?)`, `TimeMarker.ElapsedFraction`, `UsageSnapshot`.
- Produces:
  - `SeverityRules.FromPayload(string? payloadSeverity)` → `Severity?` (`"critical"`→Red, `"warning"`→Orange, `"normal"`→Green, else null). Moved from `UsagePopup.ParseSeverity`.
  - `record UsageValue(string Key, string Label, int Percent, Severity Severity, DateTimeOffset? ResetsAt)`.
  - `UsageValues.FiveHourPeriod`, `.SevenDayPeriod` (`TimeSpan`), `.KeyComparer` (`StringComparer.OrdinalIgnoreCase`).
  - `UsageValues.WindowSeverity(WindowUsage usage, TimeSpan period, Settings settings, DateTimeOffset now)` → `Severity`.
  - `UsageValues.ScopedSeverity(ScopedLimit limit, Settings settings, DateTimeOffset now)` → `Severity`.
  - `UsageValues.CreditSeverity(CreditUsage credits, Settings settings)` → `Severity`.
  - `UsageValues.Enumerate(UsageSnapshot snapshot, Settings settings, DateTimeOffset now)` → `IReadOnlyList<UsageValue>`.

- [ ] **Step 1: Write the failing tests**

Create `tests/ClaudeUsageTray.Tests/UsageValuesTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

public class UsageValuesTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    private static Settings NoPace() => new() { PaceColors = false };

    [Fact]
    public void AbsentValuesProduceNoRow()
    {
        var values = UsageValues.Enumerate(new UsageSnapshot(Now, null, null), new Settings(), Now);
        Assert.Empty(values);
    }

    [Fact]
    public void KeysAndLabelsAreTheDocumentedOnes()
    {
        var snapshot = new UsageSnapshot(Now,
            new WindowUsage(10, Now.AddHours(4)),
            new WindowUsage(20, Now.AddDays(6)),
            [new ScopedLimit("Fable", "claude-fable", 30, Now.AddDays(6), true)],
            new CreditUsage(null, null, 40, null, new CreditState(true, null, false)));

        var values = UsageValues.Enumerate(snapshot, NoPace(), Now);

        Assert.Equal(["5h", "7d", "Fable", "credits"], values.Select(v => v.Key));
        Assert.Equal(["5-hour window", "7-day window", "Fable weekly", "Credits"], values.Select(v => v.Label));
        Assert.Equal([10, 20, 30, 40], values.Select(v => v.Percent));
        Assert.Equal(Now.AddHours(4), values[0].ResetsAt);
        Assert.Null(values[3].ResetsAt);
    }

    [Fact]
    public void EveryScopedLimitIsEnumerated_NotJustThePopupsVisibleFour()
    {
        var limits = Enumerable.Range(0, 6)
            .Select(i => new ScopedLimit($"Model{i}", null, 10 * i, Now.AddDays(3), i % 2 == 0))
            .ToList();
        var values = UsageValues.Enumerate(new UsageSnapshot(Now, null, null, limits), NoPace(), Now);
        Assert.Equal(6, values.Count);   // PopupRows caps at four; notifications must not
    }

    [Fact]
    public void WindowSeverityFollowsSeverityRulesForSettings_WithTheWindowsOwnPeriod()
    {
        // 60 % used with 5.5 of 7 days gone is Green under pace, Orange without it.
        var usage = new WindowUsage(60, Now.AddDays(1.5));
        Assert.Equal(Severity.Green, UsageValues.WindowSeverity(usage, UsageValues.SevenDayPeriod, new Settings(), Now));
        Assert.Equal(Severity.Orange, UsageValues.WindowSeverity(usage, UsageValues.SevenDayPeriod, NoPace(), Now));
        // The same numbers measured against a 5-hour period are far ahead of pace: 60 % with
        // 1.5 days of a 5-hour window "remaining" has no valid elapsed fraction → absolute → Orange.
        Assert.Equal(Severity.Orange, UsageValues.WindowSeverity(usage, UsageValues.FiveHourPeriod, new Settings(), Now));
    }

    [Fact]
    public void CreditsPreferThePayloadSeverityOverTheThresholds()
    {
        // 10 % would be Green by every threshold; the payload says the cap is already reached.
        var credits = new CreditUsage(null, null, 10, "critical", new CreditState(true, null, true));
        Assert.Equal(Severity.Red, UsageValues.CreditSeverity(credits, new Settings()));

        var silent = new CreditUsage(null, null, 90, null, new CreditState(true, null, false));
        Assert.Equal(Severity.Red, UsageValues.CreditSeverity(silent, new Settings()));   // fallback: 90 > 85

        var unknownWord = new CreditUsage(null, null, 60, "meh", new CreditState(true, null, false));
        Assert.Equal(Severity.Orange, UsageValues.CreditSeverity(unknownWord, new Settings()));
    }

    [Theory]
    [InlineData("critical", Severity.Red)]
    [InlineData("warning", Severity.Orange)]
    [InlineData("normal", Severity.Green)]
    public void FromPayloadMapsTheThreeKnownWords(string word, Severity expected)
        => Assert.Equal(expected, SeverityRules.FromPayload(word));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("CRITICAL")]   // the popup never matched case-insensitively; keep that
    public void FromPayloadIsNullForAnythingElse(string? word)
        => Assert.Null(SeverityRules.FromPayload(word));
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~UsageValuesTests`
Expected: build errors — `UsageValues`, `SeverityRules.FromPayload` missing.

- [ ] **Step 3: Add `SeverityRules.FromPayload`**

In `src/ClaudeUsageTray/Core/SeverityRules.cs`, after `ForSettings`:

```csharp
    /// <summary>The usage payload's own severity word for credits, or null when it says nothing we
    /// recognise. Exact match on purpose: the three words are the API's, and a new one must fall
    /// through to the thresholds rather than be guessed at.</summary>
    public static Severity? FromPayload(string? payloadSeverity) => payloadSeverity switch
    {
        "critical" => Red,
        "warning" => Orange,
        "normal" => Green,
        _ => null,
    };
```

(`Red`, `Orange`, `Green` need `Severity.` prefixes — write `Severity.Red` etc.)

- [ ] **Step 4: Create `UsageValues`**

Create `src/ClaudeUsageTray/Core/UsageValues.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>One notifiable value of a snapshot. Key is stable across polls and is what the
/// notifier files its state under; Label is the popup's caption text, so a toast and the row it
/// refers to cannot be worded differently.</summary>
public sealed record UsageValue(string Key, string Label, int Percent, Severity Severity, DateTimeOffset? ResetsAt);

/// <summary>The severity every surface draws for a value — badge, popup bar, toast — computed in
/// exactly one place. This type exists to prevent drift, not to save typing: three copies of "which
/// severity is this" would make the toast's agreement with the bar beside it a coincidence.</summary>
public static class UsageValues
{
    public static readonly TimeSpan FiveHourPeriod = TimeSpan.FromHours(5);
    public static readonly TimeSpan SevenDayPeriod = TimeSpan.FromDays(7);

    /// <summary>Matches the scoped-limit dedup in UsageJson.ReadScopedLimits, so a label whose case
    /// changed does not read as one key vanishing and another appearing.</summary>
    public static readonly StringComparer KeyComparer = StringComparer.OrdinalIgnoreCase;

    public static Severity WindowSeverity(WindowUsage usage, TimeSpan period, Settings settings, DateTimeOffset now)
        => SeverityRules.ForSettings(settings, usage.Percent,
            TimeMarker.ElapsedFraction(usage.ResetsAt, period, now));

    /// <summary>Scoped limits are weekly, whatever model or surface they are scoped to.</summary>
    public static Severity ScopedSeverity(ScopedLimit limit, Settings settings, DateTimeOffset now)
        => SeverityRules.ForSettings(settings, limit.Percent,
            TimeMarker.ElapsedFraction(limit.ResetsAt, SevenDayPeriod, now));

    /// <summary>Credits prefer the payload's own severity: it can encode account state — a spend cap
    /// already reached — that a percentage cannot express. Only when the payload says nothing do the
    /// configured thresholds decide, and then purely absolute: credits carry no reset time.</summary>
    public static Severity CreditSeverity(CreditUsage credits, Settings settings)
        => SeverityRules.FromPayload(credits.PayloadSeverity)
           ?? SeverityRules.ForSettings(settings, credits.Percent, null);

    /// <summary>Every value the snapshot has, in popup order. Absent values produce no row. All scoped
    /// limits are included, not just the four the popup draws: a limit pushed below a display cap says
    /// nothing about whether it matters.</summary>
    public static IReadOnlyList<UsageValue> Enumerate(UsageSnapshot snapshot, Settings settings, DateTimeOffset now)
    {
        var values = new List<UsageValue>();
        if (snapshot.FiveHour is { } five)
            values.Add(new("5h", "5-hour window", five.Percent,
                WindowSeverity(five, FiveHourPeriod, settings, now), five.ResetsAt));
        if (snapshot.SevenDay is { } seven)
            values.Add(new("7d", "7-day window", seven.Percent,
                WindowSeverity(seven, SevenDayPeriod, settings, now), seven.ResetsAt));
        foreach (var limit in snapshot.ScopedLimits)
            values.Add(new(limit.Label, $"{limit.Label} weekly", limit.Percent,
                ScopedSeverity(limit, settings, now), limit.ResetsAt));
        if (snapshot.Credits is { } credits)
            values.Add(new("credits", "Credits", credits.Percent, CreditSeverity(credits, settings), null));
        return values;
    }
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test --filter FullyQualifiedName~UsageValuesTests`
Expected: all pass.

- [ ] **Step 6: Rewire `UsagePopup`**

In `src/ClaudeUsageTray/Tray/UsagePopup.cs`:

- `AddWindowRow` (lines 97-100): replace the `elapsed`/`AddBar` pair with

```csharp
        var elapsed = TimeMarker.ElapsedFraction(usage.ResetsAt, period, now);
        AddCaption(layout, $"{title} — {usage.Percent}%{resets}{PaceSuffix(usage.Percent, elapsed, settings)}");
        AddBar(layout, usage.Percent, UsageValues.WindowSeverity(usage, period, settings, now), elapsed);
```

- The two `AddWindowRow` calls (lines 44-45) pass `UsageValues.FiveHourPeriod` and `UsageValues.SevenDayPeriod` instead of `TimeSpan.FromHours(5)` / `TimeSpan.FromDays(7)`.
- `AddScopedRow` (192-195): `TimeSpan.FromDays(7)` → `UsageValues.SevenDayPeriod`; the `AddBar` severity argument → `UsageValues.ScopedSeverity(limit, settings, now)`.
- `AddCreditRow` (204-205): the `AddBar` call becomes `AddBar(layout, credits.Percent, UsageValues.CreditSeverity(credits, settings));` and the three-line comment above it is replaced by `// Severity computed by UsageValues so the toast and this bar cannot disagree; see its doc comment.`
- Delete `SeverityFor` (222-223) and `ParseSeverity` (235-241).

- [ ] **Step 7: Rewire `TrayApp.Apply`**

In `src/ClaudeUsageTray/Tray/TrayApp.cs` lines 327-330 replace with:

```csharp
            var elapsed = TimeMarker.ElapsedFraction(usage.ResetsAt, period, now);
            var severity = UsageValues.WindowSeverity(usage, period, _settings, now);
```

and in `Render()` (lines 292, 295) pass `UsageValues.FiveHourPeriod` / `UsageValues.SevenDayPeriod` in place of the `TimeSpan.From…` literals.

- [ ] **Step 8: Run the whole suite**

Run: `dotnet test`
Expected: all pass — `UsageBarTests`, `IconRendererTests`, the `UsagePopup*Tests` and `SmokeTests` cover the rewired surfaces.

- [ ] **Step 9: Commit**

```bash
git add src/ClaudeUsageTray/Core/UsageValues.cs src/ClaudeUsageTray/Core/SeverityRules.cs src/ClaudeUsageTray/Tray/UsagePopup.cs src/ClaudeUsageTray/Tray/TrayApp.cs tests/ClaudeUsageTray.Tests/UsageValuesTests.cs
git commit -m "refactor: UsageValues computes every value's severity once; popup and badge use it" -m "Enumerates the notifiable values of a snapshot with stable keys and the popup's own labels. Credits keep their payload-severity preference. Prepares the notifier so a toast can never disagree with the bar beside it." -m "Refs #14"
```

---

### Task 4: `NotificationRules` — usage transitions, arming, hysteresis, fingerprint

**Files:**
- Create: `src/ClaudeUsageTray/Core/NotificationRules.cs`
- Test: `tests/ClaudeUsageTray.Tests/NotificationRulesUsageTests.cs`

**Interfaces:**
- Consumes: `UsageValues.Enumerate`, `UsageValue`, `DisplayChoice`, `Settings.UsageNotifications`, `NotifyLevel`, `UsageSource`.
- Produces (this task; Task 5 adds the status half to the same class):
  - `enum LiveOutcome { Snapshot, Unauthorized, RateLimited, Failed, NoToken }`
  - `record Notification(string Title, string Body, string Argument, string Tag, DateTimeOffset? ExpiresAt)`
  - `record NotificationOutcome(Notification? Notification, IReadOnlyList<string> Log, bool RemoveUsageToast)`
  - `NotificationRules.UsageTag = "usage"`, `NotificationRules.OpenPopupArgument = "open-popup"`, `NotificationRules.DefaultUsageToastLifetime = TimeSpan.FromHours(6)`
  - `NotificationRules.NoteLiveOutcome(LiveOutcome outcome)` → `string?` (a log line when it armed, else null)
  - `NotificationRules.IsArmed` → `bool`
  - `NotificationRules.OnUsage(DisplayChoice choice, Settings settings, DateTimeOffset now)` → `NotificationOutcome`

**Logging contract** (the `Log` list): one line per *event*, never per tick. Lines are prefixed `notify[usage]:` and name the reason with the spec's words — `armed`, `unarmed baseline`, `stale`, `fingerprint change`, `source switch`, `hysteresis`, `switched off`, `emitted`. Scoped-limit keys appear in the log **only as a count** (`scoped=2`); the fixed keys `5h`, `7d`, `credits` may be named.

- [ ] **Step 1: Write the failing tests**

Create `tests/ClaudeUsageTray.Tests/NotificationRulesUsageTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>The whole of the usage-notification behaviour: baseline, arming, hysteresis, the settings
/// fingerprint, the source switch, eviction, coalescing, and the on/off switch. No clock — every test
/// hands in its own now.</summary>
public class NotificationRulesUsageTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);

    /// <summary>Absolute thresholds only, so a test's percent is its colour and the clock is inert.</summary>
    private static Settings Absolute(NotifyLevel level = NotifyLevel.Red, bool enabled = true) => new()
    {
        PaceColors = false,
        UsageNotifications = new UsageNotificationSettings { Enabled = enabled, Level = level },
    };

    private static UsageSnapshot Snap(int five, DateTimeOffset at, int? seven = null,
        UsageSource source = UsageSource.ClaudeCode, params ScopedLimit[] scoped)
        => new(at, new WindowUsage(five, at.AddHours(2)), seven is { } s ? new WindowUsage(s, at.AddDays(3)) : null, scoped)
            { Source = source };

    private static DisplayChoice Fresh(UsageSnapshot s) => new(s, Stale: false);
    private static DisplayChoice Stale(UsageSnapshot s) => new(s, Stale: true);

    /// <summary>Armed by a successful fetch, then baselined at 10 %.</summary>
    private static NotificationRules ArmedAt(int five, Settings? settings = null)
    {
        var rules = new NotificationRules();
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        rules.OnUsage(Fresh(Snap(five, T0)), settings ?? Absolute(), T0);
        return rules;
    }

    // ---- baseline & arming ----

    [Fact]
    public void FirstSightIsBaseline_EvenWhenAlreadyRed()
    {
        var rules = new NotificationRules();
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        var outcome = rules.OnUsage(Fresh(Snap(95, T0)), Absolute(), T0);
        Assert.Null(outcome.Notification);
    }

    [Fact]
    public void UnarmedCacheReadThenFirstLiveFetchRevealingRed_NotifiesNothing()
    {
        // The startup storm: cache says 40 %, first live fetch says 91 %.
        var rules = new NotificationRules();
        Assert.Null(rules.OnUsage(Fresh(Snap(40, T0)), Absolute(), T0).Notification);
        Assert.False(rules.IsArmed);

        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        Assert.Null(rules.OnUsage(Fresh(Snap(91, T0.AddSeconds(5), seven: 10)), Absolute(), T0.AddSeconds(5)).Notification);

        // A second live reading that crosses does notify — for the key that crossed, not the one
        // that was red at the baseline.
        var crossed = rules.OnUsage(Fresh(Snap(91, T0.AddMinutes(5), seven: 90)), Absolute(), T0.AddMinutes(5));
        Assert.NotNull(crossed.Notification);
        Assert.Contains("7-day window", crossed.Notification!.Body);
        Assert.DoesNotContain("5-hour", crossed.Notification.Body);   // 5h was already red at baseline
    }

    [Theory]
    [InlineData(LiveOutcome.RateLimited)]
    [InlineData(LiveOutcome.Failed)]
    public void RateLimitOrNetworkErrorDoesNotArm(LiveOutcome first)
    {
        var rules = new NotificationRules();
        rules.OnUsage(Fresh(Snap(40, T0)), Absolute(), T0);       // cache
        Assert.Null(rules.NoteLiveOutcome(first));
        Assert.False(rules.IsArmed);
        rules.OnUsage(Fresh(Snap(40, T0.AddMinutes(1))), Absolute(), T0.AddMinutes(1));

        // The first *successful* fetch minutes later reveals red — still a baseline.
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        Assert.True(rules.IsArmed);
        Assert.Null(rules.OnUsage(Fresh(Snap(91, T0.AddMinutes(6))), Absolute(), T0.AddMinutes(6)).Notification);
    }

    [Theory]
    [InlineData(LiveOutcome.Unauthorized)]
    [InlineData(LiveOutcome.NoToken)]
    [InlineData(LiveOutcome.Snapshot)]
    public void TerminalOutcomesArmImmediately(LiveOutcome outcome)
    {
        var rules = new NotificationRules();
        Assert.NotNull(rules.NoteLiveOutcome(outcome));
        Assert.True(rules.IsArmed);
    }

    [Fact]
    public void ThirdConcludedAttemptArmsRegardless()
    {
        var rules = new NotificationRules();
        Assert.Null(rules.NoteLiveOutcome(LiveOutcome.Failed));
        Assert.Null(rules.NoteLiveOutcome(LiveOutcome.RateLimited));
        Assert.False(rules.IsArmed);
        Assert.NotNull(rules.NoteLiveOutcome(LiveOutcome.Failed));
        Assert.True(rules.IsArmed);
    }

    [Fact]
    public void ArmingIsABaseline_TheFirstEvaluationAfterItNeverNotifies()
    {
        var rules = new NotificationRules();
        rules.OnUsage(Fresh(Snap(40, T0)), Absolute(), T0);
        rules.NoteLiveOutcome(LiveOutcome.Failed);
        rules.NoteLiveOutcome(LiveOutcome.Failed);
        rules.NoteLiveOutcome(LiveOutcome.Failed);              // backstop arms
        Assert.Null(rules.OnUsage(Fresh(Snap(95, T0.AddMinutes(20))), Absolute(), T0.AddMinutes(20)).Notification);
    }

    // ---- crossings ----

    [Fact]
    public void CrossingIntoRedNotifiesOnce_ThenStaysSilentWhileRed()
    {
        var rules = ArmedAt(10);
        var first = rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(5))), Absolute(), T0.AddMinutes(5));
        Assert.NotNull(first.Notification);
        Assert.Equal(NotificationRules.UsageTag, first.Notification!.Tag);
        Assert.Equal(NotificationRules.OpenPopupArgument, first.Notification.Argument);
        Assert.Equal("5-hour window (90 %) is now red", first.Notification.Body);

        for (int i = 1; i <= 20; i++)
            Assert.Null(rules.OnUsage(Fresh(Snap(90 + i % 3, T0.AddMinutes(5 + i * 5))), Absolute(), T0.AddMinutes(5 + i * 5)).Notification);
    }

    [Fact]
    public void LeavingRedIsSilent_ReturningAfterGreenNotifiesAgain()
    {
        var rules = ArmedAt(90);
        var left = rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(5))), Absolute(), T0.AddMinutes(5));
        Assert.Null(left.Notification);
        Assert.True(left.RemoveUsageToast);
        Assert.NotNull(rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(10))), Absolute(), T0.AddMinutes(10)).Notification);
    }

    [Fact]
    public void Hysteresis_RedToOrangeToRedIsOneToast_RedToGreenToRedIsTwo()
    {
        var rules = ArmedAt(10);
        Assert.NotNull(rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(1))), Absolute(), T0.AddMinutes(1)).Notification);
        Assert.Null(rules.OnUsage(Fresh(Snap(70, T0.AddMinutes(2))), Absolute(), T0.AddMinutes(2)).Notification);   // orange
        Assert.Null(rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(3))), Absolute(), T0.AddMinutes(3)).Notification);   // red again: silent
        Assert.Null(rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(4))), Absolute(), T0.AddMinutes(4)).Notification);   // green re-arms
        Assert.NotNull(rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(5))), Absolute(), T0.AddMinutes(5)).Notification);
    }

    [Fact]
    public void ClockOnlyCrossing_TheSameSnapshotTurnsRedAsTheWindowElapses()
    {
        // Pace on (50/85). 30 % used, 5-hour window resetting at T0 + 4 h 30. Twenty minutes before
        // T0 the elapsed fraction is 0.033 — inside SeverityRules' dead zone, so the absolute
        // thresholds decide: Green. At T0 the fraction reaches 0.10, the ratio is 30 / 10 = 3.0 ≥
        // 1.75: Red. Nothing but the clock moved. This is the case the deleted timestamp rule would
        // have made unnotifiable. By minute 60 the ratio is back to 1.0 (Green), which re-arms.
        var settings = new Settings();
        var snapshot = new UsageSnapshot(T0, new WindowUsage(30, T0.AddHours(4.5)), null);
        var rules = new NotificationRules();
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        Assert.Null(rules.OnUsage(Fresh(snapshot), settings, T0.AddMinutes(-20)).Notification);

        int toasts = 0;
        for (int minute = -15; minute <= 60; minute += 5)
            if (rules.OnUsage(Fresh(snapshot), settings, T0.AddMinutes(minute)).Notification is not null) toasts++;
        Assert.Equal(1, toasts);
    }

    [Fact]
    public void SeveralCrossingsCoalesceIntoOneToast_ScopedLimitsNamedInTheToastButCountedInTheLog()
    {
        // Every key must exist at the baseline: a key seen for the first time is a baseline, not a crossing.
        var rules = new NotificationRules();
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        rules.OnUsage(Fresh(Snap(10, T0, seven: 10, scoped: new ScopedLimit("Fable", null, 10, T0.AddDays(3), true))), Absolute(), T0);
        var fable = new ScopedLimit("Fable", null, 92, T0.AddDays(3), true);
        var outcome = rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(5), seven: 91, scoped: fable)), Absolute(), T0.AddMinutes(5));
        Assert.NotNull(outcome.Notification);
        Assert.Equal("5-hour window (90 %), 7-day window (91 %) and Fable weekly (92 %) are now red", outcome.Notification!.Body);
        var emitted = Assert.Single(outcome.Log, l => l.Contains("emitted"));
        Assert.Contains("5h", emitted);
        Assert.Contains("scoped=1", emitted);
        Assert.DoesNotContain("Fable", emitted);
    }

    [Fact]
    public void ExpiryIsTheLatestKnownReset_OrSixHours()
    {
        var rules = ArmedAt(10);
        var at = T0.AddMinutes(5);
        var withReset = rules.OnUsage(Fresh(Snap(90, at)), Absolute(), at).Notification!;
        Assert.Equal(at.AddHours(2), withReset.ExpiresAt);

        var rules2 = ArmedAt(10);
        var credits = new UsageSnapshot(at, new WindowUsage(10, at.AddHours(2)), null,
            Credits: new CreditUsage(null, null, 5, "critical", new CreditState(true, null, true)));
        rules2.OnUsage(Fresh(new UsageSnapshot(T0, new WindowUsage(10, T0.AddHours(2)), null,
            Credits: new CreditUsage(null, null, 5, "normal", new CreditState(true, null, false)))), Absolute(), T0);
        var noReset = rules2.OnUsage(Fresh(credits), Absolute(), at).Notification!;
        Assert.Equal(at + NotificationRules.DefaultUsageToastLifetime, noReset.ExpiresAt);
    }

    // ---- levels ----

    [Fact]
    public void LevelOrange_GreenToRedFiresOnce_OrangeToRedIsSilent()
    {
        var settings = Absolute(NotifyLevel.Orange);
        var rules = ArmedAt(10, settings);
        var toRed = rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(1))), settings, T0.AddMinutes(1));
        Assert.NotNull(toRed.Notification);
        Assert.EndsWith("is now red", toRed.Notification!.Body);

        var rules2 = ArmedAt(60, settings);   // baseline orange
        Assert.Null(rules2.OnUsage(Fresh(Snap(90, T0.AddMinutes(1))), settings, T0.AddMinutes(1)).Notification);
    }

    [Fact]
    public void LevelOrange_GreenToOrangeNotifies_LevelRedDoesNot()
    {
        var orange = Absolute(NotifyLevel.Orange);
        var rules = ArmedAt(10, orange);
        var outcome = rules.OnUsage(Fresh(Snap(60, T0.AddMinutes(1))), orange, T0.AddMinutes(1));
        Assert.Equal("5-hour window (60 %) is now orange", outcome.Notification!.Body);

        Assert.Null(ArmedAt(10).OnUsage(Fresh(Snap(60, T0.AddMinutes(1))), Absolute(), T0.AddMinutes(1)).Notification);
    }

    // ---- guards ----

    [Fact]
    public void StaleRecordsNothing_AndACrossingAcrossTheGapFiresOnce()
    {
        var rules = ArmedAt(10);
        Assert.Null(rules.OnUsage(Stale(Snap(90, T0.AddMinutes(1))), Absolute(), T0.AddMinutes(1)).Notification);
        Assert.Null(rules.OnUsage(Stale(Snap(90, T0.AddMinutes(2))), Absolute(), T0.AddMinutes(2)).Notification);
        Assert.NotNull(rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(3))), Absolute(), T0.AddMinutes(3)).Notification);
    }

    [Fact]
    public void UnarmedStartupNeverBaselinesAgainstAStaleCache()
    {
        var rules = new NotificationRules();
        rules.OnUsage(Stale(Snap(10, T0.AddHours(-5))), Absolute(), T0);     // hours-old cache: nothing recorded
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        Assert.Null(rules.OnUsage(Fresh(Snap(95, T0)), Absolute(), T0).Notification);   // first sight → baseline
    }

    [Fact]
    public void NoSnapshotIsNotAReading()
    {
        var rules = ArmedAt(10);
        Assert.Null(rules.OnUsage(new DisplayChoice(null, false), Absolute(), T0.AddMinutes(1)).Notification);
        Assert.NotNull(rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(2))), Absolute(), T0.AddMinutes(2)).Notification);
    }

    [Fact]
    public void SourceSwitchRebaselinesSilently_BothDirections()
    {
        var rules = ArmedAt(10);
        var desktopRed = Snap(90, T0.AddMinutes(1), source: UsageSource.DesktopHistory);
        var switched = rules.OnUsage(Fresh(desktopRed), Absolute(), T0.AddMinutes(1));
        Assert.Null(switched.Notification);
        Assert.Contains(switched.Log, l => l.Contains("source switch"));

        var backRed = Snap(90, T0.AddMinutes(2));
        Assert.Null(rules.OnUsage(Fresh(backRed), Absolute(), T0.AddMinutes(2)).Notification);
    }

    [Fact]
    public void KeyThatVanishesAndReturnsIsRebaselined()
    {
        var fable = new ScopedLimit("Fable", null, 10, T0.AddDays(3), true);
        var rules = ArmedAt(10);
        rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(1), scoped: fable)), Absolute(), T0.AddMinutes(1));   // Fable appears green
        rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(2))), Absolute(), T0.AddMinutes(2));                  // Fable gone → evicted
        var back = new ScopedLimit("Fable", null, 92, T0.AddDays(3), true);
        Assert.Null(rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(3), scoped: back)), Absolute(), T0.AddMinutes(3)).Notification);
    }

    [Fact]
    public void KeysCompareCaseInsensitively()
    {
        var rules = ArmedAt(10);
        rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(1), scoped: new ScopedLimit("Fable", null, 10, T0.AddDays(3), true))), Absolute(), T0.AddMinutes(1));
        var crossed = rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(2), scoped: new ScopedLimit("FABLE", null, 92, T0.AddDays(3), true))), Absolute(), T0.AddMinutes(2));
        Assert.NotNull(crossed.Notification);   // same key, so a real crossing — not a re-baseline
    }

    // ---- configuration changes ----

    [Fact]
    public void LoweringRedUnderAnExistingValueIsSilent()
    {
        var rules = ArmedAt(60);   // orange at 50/85
        var lowered = Absolute();
        lowered.Thresholds = new Thresholds { Orange = 20, Red = 50 };
        var outcome = rules.OnUsage(Fresh(Snap(60, T0.AddMinutes(1))), lowered, T0.AddMinutes(1));
        Assert.Null(outcome.Notification);
        Assert.Contains(outcome.Log, l => l.Contains("fingerprint change"));
        // …and the new baseline holds: a later real crossing still fires.
        Assert.Null(rules.OnUsage(Fresh(Snap(10, T0.AddMinutes(2))), lowered, T0.AddMinutes(2)).Notification);
        Assert.NotNull(rules.OnUsage(Fresh(Snap(60, T0.AddMinutes(3))), lowered, T0.AddMinutes(3)).Notification);
    }

    [Fact]
    public void TogglingPaceColoursIsSilent()
    {
        var pace = new Settings();
        var rules = new NotificationRules();
        rules.NoteLiveOutcome(LiveOutcome.Snapshot);
        // 84 %: Green under pace late in the window (reset in 20 min of 5 h → elapsed 0.93, ratio 0.9), Orange absolute.
        var snap = new UsageSnapshot(T0, new WindowUsage(84, T0.AddMinutes(20)), null);
        rules.OnUsage(Fresh(snap), pace, T0);
        var absolute = new Settings { PaceColors = false, UsageNotifications = new() { Level = NotifyLevel.Orange } };
        Assert.Null(rules.OnUsage(Fresh(snap), absolute, T0.AddSeconds(30)).Notification);
    }

    [Fact]
    public void ChangingLevelToOrangeWhileAlreadyOrangeIsSilent()
    {
        var rules = ArmedAt(60);
        Assert.Null(rules.OnUsage(Fresh(Snap(60, T0.AddMinutes(1))), Absolute(NotifyLevel.Orange), T0.AddMinutes(1)).Notification);
    }

    [Fact]
    public void RaisingStalenessSoAnOldSnapshotBecomesFreshIsSilent()
    {
        // Stale is decided by the caller (SourceSelection); here it flips from Stale to Fresh with a
        // changed fingerprint at the same time, and the first fresh evaluation is a baseline.
        var rules = ArmedAt(10);
        var old = Snap(90, T0.AddMinutes(-30));
        rules.OnUsage(Stale(old), Absolute(), T0.AddMinutes(1));
        var wider = Absolute();
        wider.StalenessMinutes = 120;
        Assert.Null(rules.OnUsage(Fresh(old), wider, T0.AddMinutes(2)).Notification);
    }

    // ---- switch ----

    [Fact]
    public void SwitchedOff_StillEvaluates_SoSwitchingBackOnCannotReplayAMissedCrossing()
    {
        var rules = ArmedAt(10);
        var off = Absolute(enabled: false);
        var suppressed = rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(1))), off, T0.AddMinutes(1));
        Assert.Null(suppressed.Notification);
        Assert.Contains(suppressed.Log, l => l.Contains("switched off"));
        Assert.Null(rules.OnUsage(Fresh(Snap(90, T0.AddMinutes(2))), Absolute(), T0.AddMinutes(2)).Notification);
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~NotificationRulesUsageTests`
Expected: build errors — `NotificationRules`, `LiveOutcome`, `Notification`, `NotificationOutcome` missing.

- [ ] **Step 3: Implement**

Create `src/ClaudeUsageTray/Core/NotificationRules.cs`:

```csharp
namespace ClaudeUsageTray.Core;

/// <summary>How one live usage fetch concluded, as far as arming cares. Relayed by TrayApp from the
/// outcomes it already distinguishes; it never decides anything about them.</summary>
public enum LiveOutcome { Snapshot, Unauthorized, RateLimited, Failed, NoToken }

/// <summary>What to show. Argument is the toast's activation payload; Tag is the Action Center slot
/// (a newer toast of a kind replaces its predecessor); ExpiresAt is when Action Center should stop
/// asserting it, null for the platform default.</summary>
public sealed record Notification(string Title, string Body, string Argument, string Tag, DateTimeOffset? ExpiresAt);

/// <summary>One evaluation's result. Log carries one line per decision event — never per tick — so
/// "I never got a toast" can be answered from fetch.log. RemoveUsageToast is set when a notified
/// usage value left the level, so the presenter can retract a claim that is no longer true.</summary>
public sealed record NotificationOutcome(Notification? Notification, IReadOnlyList<string> Log, bool RemoveUsageToast);

/// <summary>
/// Remembers what it last saw and turns readings into transitions. Stateful but clock-free: the
/// caller supplies now, so every rule below is a unit test. Two independent halves — usage (this
/// file's first part) and status (the second) — that share only the Notification type.
///
/// Usage rules, in order: stale → nothing recorded; fingerprint change → clear; source switch →
/// clear; unarmed → record baseline; per key, notify on crossing from below the level to at/above it,
/// re-armed only on return to Green; absent keys evicted; crossings coalesced into one toast; the
/// on/off switch suppresses only the output, never the recording.
/// </summary>
public sealed partial class NotificationRules
{
    public const string UsageTag = "usage";
    public const string OpenPopupArgument = "open-popup";

    /// <summary>Toast lifetime for a value with no known reset (credits), so Action Center does not
    /// assert a red credit line for days.</summary>
    public static readonly TimeSpan DefaultUsageToastLifetime = TimeSpan.FromHours(6);

    /// <summary>Above: at or above the configured level at the last evaluation. Notified: has fired
    /// and not yet returned to Green — the hysteresis latch.</summary>
    private sealed class KeyState
    {
        public bool Above;
        public bool Notified;
    }

    /// <summary>The settings that can change a verdict without the value moving. The on/off switches
    /// are deliberately absent: they change no verdict.</summary>
    private sealed record Fingerprint(int Orange, int Red, bool PaceColors, int StalenessMinutes,
        int DesktopStalenessHours, NotifyLevel Level)
    {
        public static Fingerprint Of(Settings s) => new(s.Thresholds.Orange, s.Thresholds.Red, s.PaceColors,
            s.StalenessMinutes, s.DesktopStalenessHours, s.UsageNotifications.Level);
    }

    private readonly Dictionary<string, KeyState> _keys = new(UsageValues.KeyComparer);
    private Fingerprint? _fingerprint;
    private UsageSource? _source;
    private bool _armed;
    private int _concludedAttempts;
    private bool _loggedUnarmedBaseline;
    private bool _lastWasStale;

    public bool IsArmed => _armed;

    /// <summary>Arms on the first terminal outcome — a snapshot, a rejected token, or no token at all
    /// — or on the third concluded attempt of any kind, so a permanently offline machine still gets
    /// usage toasts from cache. 429 and network errors are not terminal: the documented common case is
    /// a rate-limited first fetch, and arming on it would baseline from the cache and then toast about
    /// a limit that was already red when the first successful fetch arrives. Arming clears all usage
    /// state, so the next evaluation is an ordinary first-sight baseline. Returns a log line when it
    /// armed, else null.</summary>
    public string? NoteLiveOutcome(LiveOutcome outcome)
    {
        if (_armed) return null;
        _concludedAttempts++;
        bool terminal = outcome is LiveOutcome.Snapshot or LiveOutcome.Unauthorized or LiveOutcome.NoToken;
        if (!terminal && _concludedAttempts < 3) return null;

        _armed = true;
        _keys.Clear();
        return $"notify[usage]: armed on {(terminal ? outcome.ToString() : $"attempt {_concludedAttempts} ({outcome})")}; next reading is the baseline";
    }

    public NotificationOutcome OnUsage(DisplayChoice choice, Settings settings, DateTimeOffset now)
    {
        var log = new List<string>();

        // Rule 1 — first, before anything else: nothing recorded, so a crossing during the gap is
        // still compared against the pre-stale state, and an unarmed startup can never baseline
        // against an hours-old cache.
        if (choice.Snapshot is not { } snapshot || choice.Stale)
        {
            if (choice.Snapshot is not null && !_lastWasStale) log.Add("notify[usage]: stale; recording nothing until fresh data returns");
            _lastWasStale = choice.Snapshot is not null;
            return new(null, log, false);
        }
        _lastWasStale = false;

        // Rule 2 — the user's own edit is never news.
        var fingerprint = Fingerprint.Of(settings);
        if (_fingerprint is not null && fingerprint != _fingerprint)
        {
            _keys.Clear();
            log.Add("notify[usage]: fingerprint change (thresholds/pace/staleness/level); rebaselining");
        }
        _fingerprint = fingerprint;

        // Rule 3 — Claude Code and the Desktop history measure different things.
        if (_source is not null && snapshot.Source != _source)
        {
            _keys.Clear();
            log.Add($"notify[usage]: source switch {_source} → {snapshot.Source}; rebaselining");
        }
        _source = snapshot.Source;

        var values = UsageValues.Enumerate(snapshot, settings, now);
        var level = settings.UsageNotifications.Level;

        // Rule 6 — eviction first, so a vanished key cannot leave a latch behind.
        var present = new HashSet<string>(values.Select(v => v.Key), UsageValues.KeyComparer);
        foreach (var gone in _keys.Keys.Where(k => !present.Contains(k)).ToList()) _keys.Remove(gone);

        var crossed = new List<UsageValue>();
        bool exited = false;
        foreach (var value in values)
        {
            bool above = AtOrAbove(value.Severity, level);
            if (!_keys.TryGetValue(value.Key, out var state))
            {
                // First sight is always baseline: startup into red, or a limit the payload only just
                // began reporting. Notified mirrors Above so a pre-existing red cannot toast on flap.
                _keys[value.Key] = new KeyState { Above = above, Notified = above };
                continue;
            }
            if (above && !state.Above && !state.Notified && _armed) crossed.Add(value);
            if (!above && state.Above && state.Notified) exited = true;
            if (value.Severity == Severity.Green) state.Notified = false;   // hysteresis exit
            else if (above && !state.Above && _armed) state.Notified = true;
            state.Above = above;
        }

        // Rule 4 — unarmed: everything above recorded, nothing said.
        if (!_armed)
        {
            if (!_loggedUnarmedBaseline && values.Count > 0)
            {
                _loggedUnarmedBaseline = true;
                log.Add($"notify[usage]: unarmed baseline from {snapshot.Source}; waiting for a terminal live outcome");
            }
            return new(null, log, false);
        }

        bool anyNotifiedAbove = _keys.Values.Any(k => k.Above && k.Notified);
        bool remove = exited && !anyNotifiedAbove;

        if (crossed.Count == 0) return new(null, log, remove);

        var notification = Compose(crossed, now);
        if (!settings.UsageNotifications.Enabled)
        {
            log.Add($"notify[usage]: switched off; suppressed crossing of {Describe(crossed)}");
            return new(null, log, remove);
        }
        log.Add($"notify[usage]: emitted for {Describe(crossed)}");
        return new(notification, log, false);
    }

    private static bool AtOrAbove(Severity severity, NotifyLevel level) => level switch
    {
        NotifyLevel.Orange => severity >= Severity.Orange,
        _ => severity == Severity.Red,
    };

    /// <summary>"5-hour window (90 %) and Fable weekly (92 %) are now red". Mixed severities under
    /// level Orange become one sentence per colour, red first.</summary>
    private static Notification Compose(IReadOnlyList<UsageValue> crossed, DateTimeOffset now)
    {
        var sentences = new List<string>();
        foreach (var severity in new[] { Severity.Red, Severity.Orange })
        {
            var group = crossed.Where(v => v.Severity == severity).ToList();
            if (group.Count == 0) continue;
            var names = group.Select(v => $"{v.Label} ({v.Percent} %)").ToList();
            var joined = names.Count == 1 ? names[0]
                : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[^1];
            sentences.Add($"{joined} {(names.Count == 1 ? "is" : "are")} now {severity.ToString().ToLowerInvariant()}");
        }

        var worst = crossed.Any(v => v.Severity == Severity.Red) ? "red" : "orange";
        var title = crossed.Count == 1 ? $"Usage limit {worst}" : $"Usage limits {worst}";

        DateTimeOffset? latestReset = crossed.Select(v => v.ResetsAt).Where(r => r is { } d && d > now).Max();
        var expires = latestReset ?? now + DefaultUsageToastLifetime;
        return new Notification(title, string.Join(" · ", sentences), OpenPopupArgument, UsageTag, expires);
    }

    /// <summary>Log-safe: the fixed keys by name, scoped limits by count only — their labels are the
    /// account-specific model names fetch.log must never carry.</summary>
    private static string Describe(IReadOnlyList<UsageValue> values)
    {
        var fixedKeys = values.Where(v => v.Key is "5h" or "7d" or "credits").Select(v => $"{v.Key}={v.Percent}%");
        int scoped = values.Count(v => v.Key is not ("5h" or "7d" or "credits"));
        var parts = fixedKeys.ToList();
        if (scoped > 0) parts.Add($"scoped={scoped}");
        return string.Join(" ", parts);
    }
}
```

- [ ] **Step 4: Run the tests**

Run: `dotnet test --filter FullyQualifiedName~NotificationRulesUsageTests`
Expected: all pass. If `ClockOnlyCrossing…` fails, check the arithmetic against `SeverityRules`: dead zone `0.10`, floor `20`, `RedRatio 1.75`; at `now = T0` with reset `T0+4.5h` the elapsed fraction is exactly `0.10` and `ratio = 30 / 10 = 3.0`. Adjust `now` by a minute rather than changing the rules.

- [ ] **Step 5: Commit**

```bash
git add src/ClaudeUsageTray/Core/NotificationRules.cs tests/ClaudeUsageTray.Tests/NotificationRulesUsageTests.cs
git commit -m "feat: NotificationRules — usage crossings with arming, hysteresis and a settings fingerprint" -m "Stateful, clock-free. Stale readings record nothing; a settings edit or a source switch rebaselines; arming waits for a terminal live outcome so the startup cache cannot produce a false toast; a notified key re-arms only on return to green." -m "Refs #14"
```

---

### Task 5: `NotificationRules` — status transitions, and `StatusDetail.Summary`

**Files:**
- Create: `src/ClaudeUsageTray/Core/NotificationRules.Status.cs` (second half of the `partial class`)
- Modify: `src/ClaudeUsageTray/Core/StatusDetailRows.cs` (add `Summary`)
- Test: `tests/ClaudeUsageTray.Tests/NotificationRulesStatusTests.cs`

**Interfaces:**
- Consumes: `SourceView(StatusSource Source, PlatformStatus? Status, IReadOnlyList<string> Filter)`, `StatusDetail.IsRelevant`, `StatusDetail.Header`, `Settings.NotifyFor(string)`, `Notification`, `NotificationOutcome`.
- Produces:
  - `StatusDetail.Summary(PlatformStatus status, IReadOnlyList<string> filter)` → `string`: watched incident names joined by `", "`; when no watched incident, watched components as `"Name — Status"` joined by `", "`; empty when nothing identifies what is affected.
  - `NotificationRules.StatusTag(string sourceId)` → `"status:" + sourceId`.
  - `NotificationRules.OnStatus(IReadOnlyList<SourceView> views, Settings settings)` → `IReadOnlyList<NotificationOutcome>` — at most one per view, in view order; an outcome with a null `Notification` and an empty `Log` is omitted. Sources absent from `views` drop their state.

- [ ] **Step 1: Write the failing tests**

Create `tests/ClaudeUsageTray.Tests/NotificationRulesStatusTests.cs`:

```csharp
using ClaudeUsageTray.Core;
using Xunit;

namespace ClaudeUsageTray.Tests;

/// <summary>Status transitions per source: the state is IsRelevant under the watch filter, a null
/// status is not a reading, first reading is baseline, and a disabled source or a changed filter
/// re-baselines. Recovery text must never claim a page is healthy while it still reports a
/// disruption.</summary>
public class NotificationRulesStatusTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly StatusSource Claude = StatusSourceRegistry.Claude;
    private static readonly StatusSource OpenAi = StatusSourceRegistry.OpenAi;

    private static PlatformStatus Ok(StatusSource s) => new(s.Id, T0, "none", "All Systems Operational", [], []);

    private static PlatformStatus Down(StatusSource s, string indicator = "major",
        PlatformIncident[]? incidents = null, PlatformComponent[]? components = null)
        => new(s.Id, T0, indicator, "Partial System Outage", incidents ?? [], components ?? []);

    private static PlatformIncident Incident(string name, params string[] components)
        => new(name, "investigating", "major", "https://stspg.io/x", T0, components);

    private static SourceView View(StatusSource s, PlatformStatus? status, params string[] filter) => new(s, status, filter);

    private static Settings WithNotify(string sourceId, bool notify)
    {
        var settings = new Settings();
        settings.StatusSources[sourceId] = new StatusSourceSettings { Enabled = true, Notify = notify, Components = [] };
        return settings;
    }

    private static Notification? Single(IReadOnlyList<NotificationOutcome> outcomes)
        => outcomes.Select(o => o.Notification).SingleOrDefault(n => n is not null);

    [Fact]
    public void NullStatusIsNotAReading_ThenDegradedIsABaseline()
    {
        var rules = new NotificationRules();
        Assert.Null(Single(rules.OnStatus([View(Claude, null)], new Settings())));
        Assert.Null(Single(rules.OnStatus([View(Claude, Down(Claude))], new Settings())));
    }

    [Fact]
    public void LaunchingIntoADegradedPageIsSilent_RecoveryThenNotifies()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(Claude, Down(Claude))], new Settings());
        var recovered = Single(rules.OnStatus([View(Claude, Ok(Claude))], new Settings()));
        Assert.NotNull(recovered);
        Assert.Equal("Claude status recovered", recovered!.Title);
        Assert.Equal("Claude status: All Systems Operational", recovered.Body);
        Assert.Equal("status:claude", recovered.Tag);
        Assert.Null(recovered.ExpiresAt);
    }

    [Fact]
    public void GoingDownNamesTheIncidentsInThePagesOwnWords()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(Claude, Ok(Claude))], new Settings());
        var down = Single(rules.OnStatus([View(Claude, Down(Claude,
            incidents: [Incident("Elevated errors on Claude Code", "Claude Code"), Incident("Login issues")]))], new Settings()));
        Assert.Equal("Claude status: Partial System Outage", down!.Title);
        Assert.Equal("Elevated errors on Claude Code, Login issues", down.Body);
    }

    [Fact]
    public void WithoutIncidents_ComponentsWithTheirStatusesAreTheBody()
    {
        // The OpenAI shape: no incidents array, only component statuses.
        var rules = new NotificationRules();
        rules.OnStatus([View(OpenAi, Ok(OpenAi), "codex")], new Settings());
        var down = Single(rules.OnStatus([View(OpenAi, Down(OpenAi, "minor",
            components: [new("Codex API", "partial_outage"), new("Sora", "major_outage")]), "codex")], new Settings()));
        Assert.Equal("Codex API — Partial outage", down!.Body);   // Sora is unwatched
    }

    [Fact]
    public void UnclassifiableDisruption_FallsBackToTheBanner()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(OpenAi, Ok(OpenAi), "codex")], new Settings());
        var down = Single(rules.OnStatus([View(OpenAi, Down(OpenAi), "codex")], new Settings()));
        Assert.NotNull(down);   // IsRelevant fails towards visible
        Assert.Equal("Partial System Outage", down!.Body);
    }

    [Fact]
    public void DegradedToDifferentlyDegradedIsSilent()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(Claude, Ok(Claude))], new Settings());
        rules.OnStatus([View(Claude, Down(Claude, incidents: [Incident("A")]))], new Settings());
        Assert.Null(Single(rules.OnStatus([View(Claude, Down(Claude, "critical", [Incident("A"), Incident("B")]))], new Settings())));
    }

    [Fact]
    public void OutsideTheWatchFilterIsSilent_InsideNotifies()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(OpenAi, Ok(OpenAi), "codex")], new Settings());
        Assert.Null(Single(rules.OnStatus([View(OpenAi, Down(OpenAi, components: [new("Sora", "major_outage")]), "codex")], new Settings())));
        Assert.NotNull(Single(rules.OnStatus([View(OpenAi, Down(OpenAi, components: [new("Sora", "major_outage"), new("Codex Web", "degraded_performance")]), "codex")], new Settings())));
    }

    [Fact]
    public void RecoveryOfTheWatchedPart_WhilePageStillDegraded_DoesNotClaimAllClear()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(OpenAi, Ok(OpenAi), "codex")], new Settings());
        rules.OnStatus([View(OpenAi, Down(OpenAi, components: [new("Codex API", "partial_outage"), new("Sora", "major_outage")]), "codex")], new Settings());
        var partial = Single(rules.OnStatus([View(OpenAi, Down(OpenAi, components: [new("Sora", "major_outage")]), "codex")], new Settings()));
        Assert.NotNull(partial);
        Assert.Equal("OpenAI status recovered", partial!.Title);
        Assert.Equal("OpenAI status: Partial System Outage · outside your watched components", partial.Body);
        Assert.DoesNotContain("operational", partial.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void SourcesAreIndependent()
    {
        var rules = new NotificationRules();
        var settings = new Settings();
        rules.OnStatus([View(Claude, Ok(Claude)), View(OpenAi, Ok(OpenAi))], settings);
        var outcomes = rules.OnStatus([View(Claude, Down(Claude)), View(OpenAi, Ok(OpenAi))], settings);
        var n = Single(outcomes);
        Assert.Equal("status:claude", n!.Tag);
        // Claude going down did not touch OpenAI's baseline: OpenAI going down next is its own toast.
        Assert.Equal("status:openai", Single(rules.OnStatus([View(Claude, Down(Claude)), View(OpenAi, Down(OpenAi))], settings))!.Tag);
    }

    [Fact]
    public void DisabledSourceDropsItsState_ReenablingRebaselines()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(Claude, Ok(Claude)), View(OpenAi, Ok(OpenAi))], new Settings());
        rules.OnStatus([View(Claude, Ok(Claude))], new Settings());                       // openai disabled
        Assert.Null(Single(rules.OnStatus([View(Claude, Ok(Claude)), View(OpenAi, Down(OpenAi))], new Settings())));
    }

    [Fact]
    public void WideningTheFilterOverAnUnchangedPayloadIsSilent()
    {
        var rules = new NotificationRules();
        var sora = Down(OpenAi, components: [new("Sora", "major_outage")]);
        rules.OnStatus([View(OpenAi, Ok(OpenAi), "codex")], new Settings());
        rules.OnStatus([View(OpenAi, sora, "codex")], new Settings());              // not relevant
        var widened = rules.OnStatus([View(OpenAi, sora, "codex", "sora")], new Settings());   // now relevant — but by the filter
        Assert.Null(Single(widened));
        Assert.Contains(widened.SelectMany(o => o.Log), l => l.Contains("filter change"));
    }

    [Fact]
    public void ChangingNotifyRebaselines_AndOffSuppressesOnlyTheOutput()
    {
        var rules = new NotificationRules();
        rules.OnStatus([View(Claude, Ok(Claude))], WithNotify("claude", true));
        var off = rules.OnStatus([View(Claude, Down(Claude))], WithNotify("claude", false));   // notify changed → rebaseline
        Assert.Null(Single(off));
        var recovered = rules.OnStatus([View(Claude, Ok(Claude))], WithNotify("claude", false));   // a real transition, switched off
        Assert.Null(Single(recovered));
        Assert.Contains(recovered.SelectMany(o => o.Log), l => l.Contains("switched off"));
        // Switching back on: notify changed → rebaseline, so the missed transition is not replayed.
        Assert.Null(Single(rules.OnStatus([View(Claude, Ok(Claude))], WithNotify("claude", true))));
        Assert.NotNull(Single(rules.OnStatus([View(Claude, Down(Claude))], WithNotify("claude", true))));
    }

    [Fact]
    public void Summary_PrefersWatchedIncidents_ThenWatchedComponents_ThenNothing()
    {
        var withIncident = Down(Claude, incidents: [Incident("Elevated errors", "API"), Incident("Sora slow", "Sora")],
            components: [new("API", "degraded_performance")]);
        Assert.Equal("Elevated errors", StatusDetail.Summary(withIncident, ["api"]));
        Assert.Equal("Elevated errors, Sora slow", StatusDetail.Summary(withIncident, []));

        var componentsOnly = Down(OpenAi, components: [new("Codex API", "partial_outage"), new("Sora", "major_outage")]);
        Assert.Equal("Codex API — Partial outage", StatusDetail.Summary(componentsOnly, ["codex"]));
        Assert.Equal("Codex API — Partial outage, Sora — Major outage", StatusDetail.Summary(componentsOnly, []));

        Assert.Equal("", StatusDetail.Summary(Down(OpenAi), ["codex"]));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~NotificationRulesStatusTests`
Expected: build errors — `OnStatus`, `StatusDetail.Summary` missing.

- [ ] **Step 3: Add `StatusDetail.Summary`**

In `src/ClaudeUsageTray/Core/StatusDetailRows.cs`, after `HiddenCount`:

```csharp
    /// <summary>The disruption in one line and the page's own words, for a toast body: watched
    /// incident names; failing those, watched components with their statuses (the OpenAI shape,
    /// which sends no incidents); empty when the payload identifies nothing. Same selection as
    /// <see cref="Rows"/> minus the age text, which would be stale the moment a toast persists.</summary>
    public static string Summary(PlatformStatus status, IReadOnlyList<string> filter)
    {
        var incidents = status.Incidents.Where(i => IncidentWatched(i, filter)).Select(i => i.Name).ToList();
        if (incidents.Count > 0) return string.Join(", ", incidents);
        return string.Join(", ", status.Components
            .Where(c => ComponentFilter.Matches(c.Name, filter))
            .Select(c => $"{c.Name} — {Unfold(c.Status)}"));
    }
```

- [ ] **Step 4: Implement the status half**

Create `src/ClaudeUsageTray/Core/NotificationRules.Status.cs`:

```csharp
namespace ClaudeUsageTray.Core;

public sealed partial class NotificationRules
{
    public static string StatusTag(string sourceId) => "status:" + sourceId;

    /// <summary>Per-source memory. Relevant is the state being watched — IsRelevant under the watch
    /// filter, not Degraded — and Filter/Notify are what re-baselines it when they change.</summary>
    private sealed class SourceState
    {
        public bool Relevant;
        public IReadOnlyList<string> Filter = [];
        public bool Notify;
    }

    private readonly Dictionary<string, SourceState> _sources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>One outcome per source that has something to say — a notification or a log line —
    /// in view order. Sources not in <paramref name="views"/> are disabled and drop their state, so
    /// re-enabling one re-baselines instead of announcing an outage that began while it was off.</summary>
    public IReadOnlyList<NotificationOutcome> OnStatus(IReadOnlyList<SourceView> views, Settings settings)
    {
        var present = new HashSet<string>(views.Select(v => v.Source.Id), StringComparer.OrdinalIgnoreCase);
        foreach (var gone in _sources.Keys.Where(k => !present.Contains(k)).ToList()) _sources.Remove(gone);

        var outcomes = new List<NotificationOutcome>();
        foreach (var view in views)
        {
            var outcome = OnStatus(view, settings.NotifyFor(view.Source.Id));
            if (outcome.Notification is not null || outcome.Log.Count > 0) outcomes.Add(outcome);
        }
        return outcomes;
    }

    private NotificationOutcome OnStatus(SourceView view, bool notify)
    {
        var id = view.Source.Id;
        var log = new List<string>();

        // A null Status is not a reading: Render() runs before the first fetch completes, and
        // collapsing null into "not relevant" would make the first fetch of an already-degraded page
        // a false → true transition at startup.
        if (view.Status is not { } status) return new(null, log, false);

        bool relevant = StatusDetail.IsRelevant(status, view.Filter);

        if (!_sources.TryGetValue(id, out var state))
        {
            _sources[id] = new SourceState { Relevant = relevant, Filter = view.Filter, Notify = notify };
            return new(null, log, false);   // first reading is baseline
        }

        // StatusMonitor.ApplyEnabled keeps a source's PlatformStatus across a settings change, so a
        // widened filter would flip IsRelevant against an unchanged payload. The user's edit is not news.
        bool filterChanged = !state.Filter.SequenceEqual(view.Filter, StringComparer.OrdinalIgnoreCase);
        if (filterChanged || state.Notify != notify)
        {
            log.Add($"notify[{StatusTag(id)}]: {(filterChanged ? "filter change" : "notify change")}; rebaselining");
            state.Relevant = relevant;
            state.Filter = view.Filter;
            state.Notify = notify;
            return new(null, log, false);
        }

        if (relevant == state.Relevant) return new(null, log, false);
        state.Relevant = relevant;

        var notification = relevant ? Degraded(view.Source, status, view.Filter) : Recovered(view.Source, status);
        var direction = relevant ? "degraded" : "recovered";
        if (!notify)
        {
            log.Add($"notify[{StatusTag(id)}]: switched off; suppressed {direction}");
            return new(null, log, false);
        }
        log.Add($"notify[{StatusTag(id)}]: emitted {direction} (indicator={status.Indicator})");
        return new(notification, log, false);
    }

    private static Notification Degraded(StatusSource source, PlatformStatus status, IReadOnlyList<string> filter)
    {
        var summary = StatusDetail.Summary(status, filter);
        var body = summary.Length > 0 ? summary
            : string.IsNullOrWhiteSpace(status.Description) ? status.Indicator : status.Description;
        return new Notification(StatusDetail.Header(source, status, relevant: true, stale: false), body,
            OpenPopupArgument, StatusTag(source.Id), null);
    }

    /// <summary>The watched disruption is over. The body is the popup's own header for this state,
    /// which distinguishes a clear page ("Claude status: All Systems Operational") from one that is
    /// still degraded outside the watch filter — saying "all clear" there would be a falsehood about a
    /// page the user can go and read.</summary>
    private static Notification Recovered(StatusSource source, PlatformStatus status)
        => new($"{source.DisplayName} status recovered",
            StatusDetail.Header(source, status, relevant: false, stale: false),
            OpenPopupArgument, StatusTag(source.Id), null);
}
```

- [ ] **Step 5: Run the tests**

Run: `dotnet test --filter FullyQualifiedName~NotificationRules`
Expected: both test classes pass.

- [ ] **Step 6: Commit**

```bash
git add src/ClaudeUsageTray/Core/NotificationRules.Status.cs src/ClaudeUsageTray/Core/StatusDetailRows.cs tests/ClaudeUsageTray.Tests/NotificationRulesStatusTests.cs
git commit -m "feat: NotificationRules — per-source status transitions on IsRelevant under the watch filter" -m "A null status is not a reading, the first reading is a baseline, a disabled source or a changed filter re-baselines, and a recovery toast uses the popup's header so it never claims a still-degraded page is clear. StatusDetail.Summary composes the body in the page's own words." -m "Refs #14"
```

---

### Task 6: `Tray/ToastPresenter`

**Files:**
- Create: `src/ClaudeUsageTray/Tray/ToastPresenter.cs`

No unit test: this type decides nothing, and WinRT is not available to the test host under an unregistered AUMID. It is verified by hand in Task 10. The one thing to check now is that it compiles and that `dotnet run` stays silent.

**Interfaces:**
- Consumes: `Notification`, `AppInfo.Aumid`, a WinForms `Control` for marshalling.
- Produces: `ToastPresenter(string aumid, Control sync, Action onActivated, Action<string> log)`; `void Show(Notification notification)`; `void Remove(string tag)`; `void Dispose()`.

- [ ] **Step 1: Create the presenter**

Create `src/ClaudeUsageTray/Tray/ToastPresenter.cs`:

```csharp
using System.Security;
using ClaudeUsageTray.Core;
using Windows.Data.Xml.Dom;
using Windows.UI.Notifications;

namespace ClaudeUsageTray.Tray;

/// <summary>
/// Shows a real Windows toast under Velopack's shortcut AUMID and routes a click to the popup.
/// Decides nothing: what to show, when, and with which tag is all NotificationRules' business.
///
/// Every WinRT call is wrapped. Failure is graduated: a notifier that cannot be created (the
/// unregistered-AUMID case of a dotnet run — measured to surface as either a COMException or a
/// silent no-op, so neither can be relied on) disables the presenter permanently; a failing Show()
/// disables it after three consecutive failures. Either way nothing throws and nothing else is
/// affected — the "nothing in the read paths throws" invariant applied to a write path.
///
/// Live toast objects are held until a terminal event so a click on a toast the GC collected cannot
/// silently do nothing; one per tag, and replacing a tag drops the previous reference rather than
/// waiting for a Dismissed a replacement may never deliver. Activated/Dismissed/Failed arrive off the
/// UI thread and are marshalled through the same BeginInvoke path TrayApp uses for fetch completions.
/// </summary>
public sealed class ToastPresenter : IDisposable
{
    public const string Group = "claudeusagetray";
    private const int MaxConsecutiveShowFailures = 3;

    private readonly string _aumid;
    private readonly Control _sync;
    private readonly Action _onActivated;
    private readonly Action<string> _log;
    private readonly ToastNotifier? _notifier;
    private readonly Dictionary<string, ToastNotification> _live = new(StringComparer.Ordinal);
    private int _consecutiveShowFailures;
    private bool _disabled;

    public ToastPresenter(string aumid, Control sync, Action onActivated, Action<string> log)
    {
        _aumid = aumid;
        _sync = sync;
        _onActivated = onActivated;
        _log = log;
        try
        {
            // Toasts persist across process exit. One left over from a previous process would
            // activate the shortcut on click, launch a second instance, and have SingleInstance exit
            // it — a click that appears to do nothing. Clearing first means no toast in Action Center
            // is ever older than the running process.
            ToastNotificationManager.History.Clear(aumid);
            _notifier = ToastNotificationManager.CreateToastNotifier(aumid);
            if (_notifier.Setting != NotificationSetting.Enabled)
                _log($"toast: disabled on the Windows side ({_notifier.Setting}); toasts will not show");
        }
        catch (Exception e)
        {
            _notifier = null;
            _disabled = true;
            _log($"toast: notifier unavailable ({e.GetType().Name}); notifications off for this session");
        }
    }

    public void Show(Notification notification)
    {
        if (_disabled || _notifier is null) return;
        try
        {
            var xml = new XmlDocument();
            xml.LoadXml(
                $"<toast launch=\"{Escape(notification.Argument)}\" activationType=\"foreground\">" +
                "<visual><binding template=\"ToastGeneric\">" +
                $"<text>{Escape(notification.Title)}</text>" +
                $"<text>{Escape(notification.Body)}</text>" +
                "</binding></visual></toast>");

            var toast = new ToastNotification(xml) { Group = Group, Tag = notification.Tag };
            if (notification.ExpiresAt is { } expires) toast.ExpirationTime = expires;

            var tag = notification.Tag;
            toast.Activated += (_, _) => Marshal(() => { Forget(tag, toast); _onActivated(); });
            toast.Dismissed += (_, _) => Marshal(() => Forget(tag, toast));
            toast.Failed += (_, e) => Marshal(() =>
            {
                Forget(tag, toast);
                _log($"toast: show failed after display ({e.ErrorCode?.GetType().Name})");
            });

            _live[tag] = toast;   // replaces a predecessor's reference; Windows replaces it on screen
            _notifier.Show(toast);
            _consecutiveShowFailures = 0;
        }
        catch (Exception e)
        {
            _consecutiveShowFailures++;
            _log($"toast: show failed ({e.GetType().Name}), failure {_consecutiveShowFailures} of {MaxConsecutiveShowFailures}");
            if (_consecutiveShowFailures >= MaxConsecutiveShowFailures)
            {
                _disabled = true;
                _log("toast: three consecutive failures; notifications off for this session");
            }
        }
    }

    /// <summary>Retracts a toast whose claim is no longer true (a value that left red). The silent
    /// exit stays silent; it just stops leaving a false line in Action Center.</summary>
    public void Remove(string tag)
    {
        if (_notifier is null) return;
        try
        {
            _live.Remove(tag);
            ToastNotificationManager.History.Remove(tag, Group, _aumid);
        }
        catch (Exception e)
        {
            _log($"toast: remove failed ({e.GetType().Name})");
        }
    }

    private void Forget(string tag, ToastNotification toast)
    {
        if (_live.TryGetValue(tag, out var current) && ReferenceEquals(current, toast)) _live.Remove(tag);
    }

    private void Marshal(Action action)
    {
        try { _sync.BeginInvoke(action); }
        catch (InvalidOperationException) { /* app shutting down */ }
    }

    private static string Escape(string text) => SecurityElement.Escape(text) ?? "";

    public void Dispose() => _live.Clear();
}
```

- [ ] **Step 2: Build and run from source**

Run: `dotnet build src/ClaudeUsageTray`
Expected: no errors, no warnings about `Windows.UI.Notifications`. If `XmlDocument` is ambiguous, keep the `Windows.Data.Xml.Dom` using and do not add `System.Xml`.

Then `dotnet run --project src/ClaudeUsageTray` for ~10 s and quit via the tray menu. Nothing is wired yet, so the only observable is the absence of an exception dialog. (The constructor is exercised in Task 7.)

- [ ] **Step 3: Commit**

```bash
git add src/ClaudeUsageTray/Tray/ToastPresenter.cs
git commit -m "feat: ToastPresenter shows WinRT toasts under the Velopack AUMID and never throws" -m "Clears stale history at startup, holds live toast objects until a terminal event, marshals activation onto the UI thread, and degrades to a no-op — permanently if the notifier cannot be created, after three consecutive Show failures otherwise." -m "Refs #14"
```

---

### Task 7: Wire `TrayApp` — one call site in `Render()`, arming relays, log lines

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/TrayApp.cs`

**Interfaces:**
- Consumes: `NotificationRules` (`OnUsage`, `OnStatus`, `NoteLiveOutcome`), `ToastPresenter`, `LiveOutcome`, `NotificationRules.UsageTag`.

- [ ] **Step 1: Add the fields**

After the `_statusMonitor` field (line 35):

```csharp
    // Desktop notifications. Every decision — baseline, arming, hysteresis, fingerprint, per-source
    // status state — lives in the clock-free NotificationRules; the presenter only shows what it is
    // handed. One call site, in Render(): every path that changes data ends there, and the rules
    // make re-evaluating unchanged data a no-op, so a single site cannot be forgotten.
    private readonly NotificationRules _notifications = new();
    private readonly ToastPresenter _toasts;
```

In the constructor, after `_statusMonitor = new StatusMonitor(...)`:

```csharp
        _toasts = new ToastPresenter(AppInfo.Aumid, _sync, ShowPopup,
            message => _log.Write(DateTimeOffset.UtcNow, message));
```

- [ ] **Step 2: Relay the arming outcomes**

In `StartApiFetch`, inside `if (token is null) { ... }` before `return;`:

```csharp
            // No usable token is a terminal outcome for arming: on a desktop-only machine this is
            // the permanent state, and waiting for a fetch that will never happen would leave usage
            // notifications off forever.
            if (_notifications.NoteLiveOutcome(LiveOutcome.NoToken) is { } armed) _log.Write(now, armed);
```

In `OnApiFetchCompleted`, add one line inside each branch, right after the existing `_log.Write(...)`:

- success branch: `Arm(LiveOutcome.Snapshot, now);`
- `Unauthorized` branch: `Arm(LiveOutcome.Unauthorized, now);`
- `RateLimited` branch: `Arm(LiveOutcome.RateLimited, now);`
- else branch: `Arm(LiveOutcome.Failed, now);`

and add the helper after `OnApiFetchCompleted`:

```csharp
    private void Arm(LiveOutcome outcome, DateTimeOffset now)
    {
        if (_notifications.NoteLiveOutcome(outcome) is { } line) _log.Write(now, line);
    }
```

(The `_rejectedToken` skip in `StartApiFetch` needs no relay: it is only set by `OnApiFetchCompleted`, which has by then already armed.)

- [ ] **Step 3: Evaluate in `Render()`**

In `Render()`, immediately after `LogSourceChange(choice, now);`:

```csharp
        Notify(choice, now);
```

and add the method after `Render()`:

```csharp
    /// <summary>The single notification call site. Runs on every Render — startup, the 30 s tick, the
    /// watcher debounce, both fetch completions, a settings save, a manual refresh. The 30 s tick is
    /// required, not merely tolerated: pace severity moves with the clock, so a value crossing into
    /// red because the window elapsed is a real crossing, and the rules' hysteresis is what stops the
    /// same clock from flapping it.</summary>
    private void Notify(DisplayChoice choice, DateTimeOffset now)
    {
        var usage = _notifications.OnUsage(choice, _settings, now);
        foreach (var line in usage.Log) _log.Write(now, line);
        if (usage.RemoveUsageToast) _toasts.Remove(NotificationRules.UsageTag);
        if (usage.Notification is { } toast) _toasts.Show(toast);

        foreach (var outcome in _notifications.OnStatus(_statusMonitor.Sources(), _settings))
        {
            foreach (var line in outcome.Log) _log.Write(now, line);
            if (outcome.Notification is { } status) _toasts.Show(status);
        }
    }
```

- [ ] **Step 4: Copy the new settings in `ApplySettings`**

After `_settings.StatusSources = edited.StatusSources;` (line 547):

```csharp
        _settings.UsageNotifications = edited.UsageNotifications;
```

(`StatusSources` is copied whole, so each entry's `Notify` travels with it; the dialog's `Draft()` is what has to preserve it — Task 8.)

- [ ] **Step 5: Dispose**

In `Dispose(bool disposing)`, before `_sync.Dispose();`: `_toasts.Dispose();`

- [ ] **Step 6: Build, test, run**

Run: `dotnet test`
Expected: all pass.

Run: `dotnet run --project src/ClaudeUsageTray`, wait ~30 s, quit. Then inspect the tail of `%APPDATA%\ClaudeUsageTray\fetch.log`:

```powershell
Get-Content "$env:APPDATA\ClaudeUsageTray\fetch.log" -Tail 30
```

Expected: a `toast: notifier unavailable (...)` **or** no toast line at all (the two measured failure modes of an unregistered AUMID), a `notify[usage]: unarmed baseline …` line if a cache exists, and after the first fetch outcome an `armed on …` line. No exception, no toast.

- [ ] **Step 7: Commit**

```bash
git add src/ClaudeUsageTray/Tray/TrayApp.cs
git commit -m "feat: evaluate notifications on every Render and relay live-fetch outcomes for arming" -m "One call site; the rules make repeated evaluation of unchanged data a no-op. Every emitted or suppressed notification is one fetch.log line naming the reason." -m "Refs #14"
```

---

### Task 8: Settings dialog — the Notifications group and the three copy paths

**Files:**
- Modify: `src/ClaudeUsageTray/Tray/SettingsDialog.cs` (fields ~64-68, `BuildLayout` ~140-148, tab order ~338-340, `LoadFrom` ~358-362, `WireLiveSync` ~397, `Draft` ~427-431, `Clone` ~481-488)
- Test: `tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs`

**Interfaces:**
- Consumes: `Settings.UsageNotifications`, `NotifyLevel`, `StatusSourceSettings.Notify`.
- Produces: controls named `notifyUsage` (CheckBox), `notifyLevel` (ComboBox, items `"Red only"`, `"Orange and red"`), `notifyClaude` (CheckBox), `notifyOpenAi` (CheckBox).

- [ ] **Step 1: Write the failing tests**

Append inside `SettingsDialogTests`:

```csharp
    private static ComboBox Combo(SettingsDialog d, string name) => Find<ComboBox>(d, name);
    private static CheckBox Check(SettingsDialog d, string name) => Find<CheckBox>(d, name);

    [Fact]
    public void Notifications_ReflectSettings_AndDriveTheDraft()
    {
        var settings = new Settings { UsageNotifications = new UsageNotificationSettings { Enabled = false, Level = NotifyLevel.Orange } };
        settings.StatusSources["claude"] = new StatusSourceSettings { Enabled = true, Notify = false, Components = [] };
        settings.StatusSources["openai"] = new StatusSourceSettings { Enabled = true, Notify = false, Components = ["codex"] };
        var dialog = Dialog(settings);

        Assert.False(Check(dialog, "notifyUsage").Checked);
        Assert.Equal("Orange and red", Combo(dialog, "notifyLevel").SelectedItem);
        Assert.False(Check(dialog, "notifyClaude").Checked);
        Assert.False(Check(dialog, "notifyOpenAi").Checked);

        Check(dialog, "notifyUsage").Checked = true;
        Combo(dialog, "notifyLevel").SelectedIndex = 0;   // "Red only"
        Check(dialog, "notifyClaude").Checked = true;
        Check(dialog, "notifyOpenAi").Checked = true;

        var draft = dialog.Draft();
        Assert.True(draft.UsageNotifications.Enabled);
        Assert.Equal(NotifyLevel.Red, draft.UsageNotifications.Level);
        Assert.True(draft.StatusSources["claude"]!.Notify);
        Assert.True(draft.StatusSources["openai"]!.Notify);
        Assert.Equal(["codex"], draft.StatusSources["openai"]!.Components);   // untouched by the notify edit

        // The live settings were never touched.
        Assert.False(settings.UsageNotifications.Enabled);
        Assert.False(settings.StatusSources["openai"]!.Notify);
    }

    [Fact]
    public void Notifications_DefaultsRoundTripUnchanged()
    {
        // The three copy paths (Draft rebuilds openai, Clone copies field by field, ApplySettings
        // copies across) each silently drop a field they were not taught about.
        var draft = Dialog(new Settings()).Draft();
        Assert.True(draft.UsageNotifications.Enabled);
        Assert.Equal(NotifyLevel.Red, draft.UsageNotifications.Level);
        Assert.True(draft.StatusSources["claude"]!.Notify);
        Assert.True(draft.StatusSources["openai"]!.Notify);
    }

    [Fact]
    public void OpenAiNotify_IsOnlyEnabledWhileWatching_ButKeepsItsValue()
    {
        var dialog = Dialog(new Settings());
        Assert.False(Check(dialog, "notifyOpenAi").Enabled);
        WatchOpenAi(dialog).Checked = true;
        Assert.True(Check(dialog, "notifyOpenAi").Enabled);
        Check(dialog, "notifyOpenAi").Checked = false;
        WatchOpenAi(dialog).Checked = false;
        Assert.False(dialog.Draft().StatusSources["openai"]!.Notify);   // the choice survives the off cycle
    }

    [Fact]
    public void LevelIsOnlyEnabledWhileUsageNotificationsAreOn()
    {
        var dialog = Dialog(new Settings());
        Assert.True(Combo(dialog, "notifyLevel").Enabled);
        Check(dialog, "notifyUsage").Checked = false;
        Assert.False(Combo(dialog, "notifyLevel").Enabled);
    }

    [Fact]
    public void ResetLeavesNotificationsAlone()
    {
        var dialog = Dialog(new Settings { UsageNotifications = new UsageNotificationSettings { Enabled = false, Level = NotifyLevel.Orange } });
        Button(dialog, "reset").PerformClick();
        Assert.False(dialog.Draft().UsageNotifications.Enabled);
        Assert.Equal(NotifyLevel.Orange, dialog.Draft().UsageNotifications.Level);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test --filter FullyQualifiedName~SettingsDialogTests`
Expected: the five new tests fail with `Sequence contains no elements` (controls not found) or missing-member build errors.

- [ ] **Step 3: Add the controls**

After the `_openAiComponentsCaption` field:

```csharp
    private readonly CheckBox _notifyUsage = new()
        { Name = "notifyUsage", Text = "Notify when a limit turns", AutoSize = true };
    private readonly ComboBox _notifyLevel = new()
        { Name = "notifyLevel", DropDownStyle = ComboBoxStyle.DropDownList, Width = 130 };
    private readonly CheckBox _notifyClaude = new()
        { Name = "notifyClaude", Text = "Notify when Claude platform status changes", AutoSize = true };
    private readonly CheckBox _notifyOpenAi = new()
        { Name = "notifyOpenAi", Text = "Notify when OpenAI platform status changes", AutoSize = true };

    /// <summary>Combo rows in NotifyLevel order, so SelectedIndex casts straight to the enum.</summary>
    private static readonly string[] LevelLabels = ["Red only", "Orange and red"];
```

Note `LevelLabels` is indexed by `NotifyLevel` **as displayed**, not by enum value: index 0 is `Red`, index 1 is `Orange`. Use the two helpers below rather than a cast.

- [ ] **Step 4: Lay them out**

In `BuildLayout`, after `layout.Controls.Add(Indent(_previewCaption));` and before the `Refresh` heading:

```csharp
        layout.Controls.Add(Heading("Notifications"));
        _notifyLevel.Items.AddRange(LevelLabels);
        var usageRow = new FlowLayoutPanel { AutoSize = true, WrapContents = false, Margin = new Padding(16, 0, 0, 2) };
        _notifyUsage.Margin = new Padding(0, 4, 4, 0);
        _notifyLevel.Margin = new Padding(0);
        usageRow.Controls.Add(_notifyUsage);
        usageRow.Controls.Add(_notifyLevel);
        layout.Controls.Add(usageRow);
        layout.Controls.Add(Indent(_notifyClaude));
        layout.Controls.Add(Indent(_notifyOpenAi));
```

In `BuildButtons`, extend the tab-order array: after `_openAiComponents,` insert `_notifyUsage, _notifyLevel, _notifyClaude, _notifyOpenAi,`.

- [ ] **Step 5: Load, sync, draft, clone**

In `LoadFrom`, after `_openAiComponents.Enabled = _watchOpenAi.Checked;`:

```csharp
        _notifyUsage.Checked = source.UsageNotifications.Enabled;
        _notifyLevel.SelectedIndex = IndexOf(source.UsageNotifications.Level);
        _notifyLevel.Enabled = _notifyUsage.Checked;
        _notifyClaude.Checked = source.StatusSources.GetValueOrDefault("claude")?.Notify ?? true;
        _notifyOpenAi.Checked = openAi?.Notify ?? true;
        _notifyOpenAi.Enabled = _watchOpenAi.Checked;
```

In `WireLiveSync`, replace the `_watchOpenAi.CheckedChanged` line with:

```csharp
        _watchOpenAi.CheckedChanged += (_, _) =>
        {
            _openAiComponents.Enabled = _watchOpenAi.Checked;
            _notifyOpenAi.Enabled = _watchOpenAi.Checked;   // disabled, not unchecked: the choice survives
        };
        _notifyUsage.CheckedChanged += (_, _) => _notifyLevel.Enabled = _notifyUsage.Checked;
```

In `Draft()`, replace the `draft.StatusSources["openai"] = ...` block and add the usage object:

```csharp
        draft.UsageNotifications = new UsageNotificationSettings
        {
            Enabled = _notifyUsage.Checked,
            Level = LevelAt(_notifyLevel.SelectedIndex),
        };
        // The typed filter and the notify choice are kept even when unchecked, so turning the source
        // back on does not lose either.
        draft.StatusSources["openai"] = new StatusSourceSettings
        {
            Enabled = _watchOpenAi.Checked,
            Notify = _notifyOpenAi.Checked,
            Components = [.. ComponentFilter.Parse(_openAiComponents.Text)],
        };
        // Claude has no enabled/components controls; only its notify flag is edited here.
        var claude = draft.StatusSources.GetValueOrDefault("claude");
        draft.StatusSources["claude"] = new StatusSourceSettings
        {
            Enabled = claude?.Enabled ?? true,
            Notify = _notifyClaude.Checked,
            Components = claude?.Components is null ? null : [.. claude.Components],
        };
```

Add the two helpers next to `PreviewFraction()`:

```csharp
    private static int IndexOf(NotifyLevel level) => level == NotifyLevel.Orange ? 1 : 0;
    private static NotifyLevel LevelAt(int index) => index == 1 ? NotifyLevel.Orange : NotifyLevel.Red;
```

In `Clone`, add after `DesktopHistoryPathOverride = ...`:

```csharp
        UsageNotifications = new UsageNotificationSettings
        {
            Enabled = source.UsageNotifications.Enabled,
            Level = source.UsageNotifications.Level,
        },
```

and inside the `StatusSources` clone's `new StatusSourceSettings { ... }`, add `Notify = e.Value.Notify,`.

- [ ] **Step 6: Run the tests**

Run: `dotnet test --filter FullyQualifiedName~SettingsDialog`
Expected: all pass, including the pre-existing `ClaudeFilter_SurvivesTheRoundTrip` (the new claude rebuild must carry `Components` through).

Then `dotnet test` for everything.

- [ ] **Step 7: Commit**

```bash
git add src/ClaudeUsageTray/Tray/SettingsDialog.cs tests/ClaudeUsageTray.Tests/SettingsDialogTests.cs
git commit -m "feat: Notifications group in Settings — usage level, Claude and OpenAI status toggles" -m "Draft, Clone and the live copy all carry the new fields; the OpenAI toggle is disabled rather than cleared while the source is off, so the choice survives." -m "Refs #14"
```

---

### Task 9: Docs — README, CHANGELOG, CLAUDE.md, spec deviations

**Files:**
- Modify: `README.md` (Settings paragraph ~204-208, key table ~233-244, `statusSources` block ~246-270)
- Modify: `CHANGELOG.md` (new section at the top)
- Modify: `CLAUDE.md` (Architecture → Core list; a new invariant; TFM note)
- Modify: `docs/superpowers/specs/2026-09-05-desktop-notifications-design.md` (record the two additive record fields)

- [ ] **Step 1: README**

In the Settings paragraph (line ~204), after "the **Watch OpenAI status** checkbox with its comma-separated component field" insert: ", and a **Notifications** group — whether a limit turning red (or orange) raises a Windows toast, and whether a Claude or OpenAI status change does".

Add two rows to the key table after `statusSources`:

```markdown
| `usageNotifications` | `{ "enabled": true, "level": "red" }` — toast when a limit crosses into `red`, or into `orange` or red with `"level": "orange"` | as shown |
```

and change the `statusSources` row's meaning to `which status pages to watch, which of their components matter, and whether a change should toast`.

In the `statusSources` JSON block add `"notify": true` to both entries, and add a bullet:

```markdown
- `notify` — raise a Windows toast when the page's *watched* state changes, in both directions. On by
  default. Kept when `enabled` is off, so turning a page off and on again keeps the choice.
```

Add a new subsection after the `statusSources` block:

```markdown
### Notifications

Two moments raise a real Windows toast — it lands in Action Center, obeys Focus Assist, and has its own
entry under *Settings → System → Notifications* as **Claude Usage Tray**, so quiet hours are Windows'
job and not duplicated here. Clicking a toast opens the usage popup.

- **A limit turns red.** Whichever rule decided the colour — the pace ratio or the absolute ceiling —
  and for every value the popup can show: the two windows, each scoped weekly limit, and credits.
  Exactly one toast per red period: a value hovering at the pace boundary as the clock moves does not
  toast again until it has been back to green. Leaving red is silent — the window reset, which the
  popup already predicted. `"level": "orange"` notifies on the crossing into orange instead (once,
  even if it goes straight to red).
- **A watched status page changes state**, both ways, naming the incident in the page's own words.
  For OpenAI only the watched components count, so a Sora outage stays quiet while a Codex one does
  not. A recovery toast never claims a page is healthy while it still reports a disruption you are
  not watching.

Launching into an already-red or already-degraded state raises nothing, and neither does editing
settings — thresholds, pace colouring, staleness, the level, the watch filter — whatever it does to
the colours on screen. Every emitted or suppressed toast is one line in `fetch.log` naming why.

Toasts need the Start Menu shortcut the installer creates; a `dotnet run` from source shows none.
```

- [ ] **Step 2: CHANGELOG**

Above `## [0.7.3-beta.2] - 2026-09-05` add:

```markdown
## [Unreleased]

### Added

- **Desktop notifications.** A Windows toast when a usage limit turns red — whichever rule coloured
  it — and one each way when a watched status page goes down or recovers, in the page's own words.
  Exactly one toast per red period, none at launch into an already-red state, none for a settings
  edit, and a recovery toast that never claims a page is healthy while it still reports a disruption
  outside your watched components. Toasts land in Action Center and follow Focus Assist; clicking one
  opens the popup. New **Notifications** group in Settings, plus `usageNotifications` and a per-source
  `notify` key in `settings.json`, all on by default.

### Changed

- The installed size grows by about 24 MB (the Windows SDK projection that toasts need). Velopack
  deltas mean you download it once. The supported Windows floor is now Windows 10 2004 (build 19041).
```

The release commit renames `[Unreleased]` to the version being shipped; `changelog-section.ps1` needs the exact version heading.

- [ ] **Step 3: CLAUDE.md**

In *Architecture* → the `Core/` bullet, extend the list of pulled-out state machines: `… \`TimeMarker\`, \`SnapshotPrecedence\`, \`UsageValues\`, and \`NotificationRules\` exist as separate state machines …`.

In *Data flow*, after the three sources, add:

```markdown
**Notifications** are evaluated once per `Render()` by `NotificationRules` (usage crossings with
arming, hysteresis and a settings fingerprint; per-source status transitions on `IsRelevant`) and
shown by `Tray/ToastPresenter`, which decides nothing and never throws. Every value's severity comes
from `UsageValues` — the badge, the popup bar and the toast all call it, so they cannot disagree. See
`docs/superpowers/specs/2026-09-05-desktop-notifications-design.md` before touching any of this.
```

Under *Non-negotiable invariants* add:

```markdown
- **Toasts never name scoped limits in the log.** `fetch.log` may carry `5h`, `7d`, `credits` and
  counts; a scoped limit's label is an account-specific model name and is logged as `scoped=N`.
- **The app and its tests target `net10.0-windows10.0.19041.0`; nothing else does.** The setup stub
  stays on `net10.0-windows` — pulling the Windows SDK projection into a NativeAOT build buys nothing.
```

- [ ] **Step 4: Spec deviations**

In the spec, under *`Core/UsageValues.cs`* replace the record line with `public sealed record UsageValue(string Key, string Label, int Percent, Severity Severity, DateTimeOffset? ResetsAt);` and add one sentence: "`ResetsAt` travels with the value so the toast expiry (*Usage toasts expire*) is a pure function of the crossing rather than a second lookup in the presenter."

Under *`Core/NotificationRules.cs`* replace the record line with `public sealed record Notification(string Title, string Body, string Argument, string Tag, DateTimeOffset? ExpiresAt);` and add: "`Tag` and `ExpiresAt` are decided here, not in `ToastPresenter`, which must decide nothing. `OnUsage` returns them inside a `NotificationOutcome(Notification?, Log, RemoveUsageToast)` so the log lines and the toast retraction on leaving red are also decisions made in `Core`."

- [ ] **Step 5: Commit**

```bash
git add README.md CHANGELOG.md CLAUDE.md docs/superpowers/specs/2026-09-05-desktop-notifications-design.md
git commit -m "docs: desktop notifications — README, changelog, CLAUDE.md, spec record shapes" -m "Refs #14"
```

---

### Task 10: Hand verification against an installed build

**Files:** none — this task produces a checked list in the PR description and, if anything fails, a follow-up fix commit.

`ToastPresenter` is not unit-testable, and everything it does depends on the Velopack shortcut's AUMID. Follow *Testing a build in the real installed app* in CLAUDE.md exactly, then work through this list. Do **not** hammer the usage endpoint to force a crossing — edit `thresholds` in `settings.json` and *then* watch a natural poll, or lower `thresholds.red` below the current 5-hour percent, save, and wait for the next tick (the save itself must **not** toast — that is check 6).

- [ ] **Step 1: Build and install**

```powershell
dotnet publish src/ClaudeUsageTray -c Release -r win-x64 --self-contained -o artifacts\publish
dnx vpk --version 1.2.0 pack --packId WusTechnik.ClaudeUsageTray --packVersion 0.7.3-local.1 --packDir artifacts\publish --mainExe ClaudeUsageTray.exe --channel win-beta
Stop-Process -Name ClaudeUsageTray -Force
& "$env:LOCALAPPDATA\WusTechnik.ClaudeUsageTray\Update.exe" apply --package (Get-ChildItem Releases\*.nupkg | Select-Object -Last 1).FullName
& explorer.exe "$env:LOCALAPPDATA\WusTechnik.ClaudeUsageTray\current\ClaudeUsageTray.exe"
```

Use `--channel win-beta` only if the installed app is on the beta ring (check `useBetaReleases` in settings.json); otherwise drop the flag. Confirm the publish output is roughly 143 MB and contains `Microsoft.Windows.SDK.NET.dll`.

- [ ] **Step 2: Work through the checks and record each result**

1. `fetch.log` shows no `toast: notifier unavailable` line and no `disabled on the Windows side` line.
2. Lower `thresholds.red` in `settings.json` below the current 5-hour percent while the app runs, save, wait for the 30 s tick: **no toast**, and `fetch.log` has `fingerprint change … rebaselining`.
3. Restore the threshold, wait one tick, then lower it again through the **Settings dialog** and save: still no toast (dialog path = same fingerprint rule). Then raise the threshold *above* the percent, wait a tick (value goes green; log may show nothing), lower it again below, wait a tick: **one toast**, titled `Usage limit red`, body naming `5-hour window (NN %) is now red`.
4. Wait through several more ticks: no second toast.
5. Click the toast banner: the popup opens near the cursor, takes focus, closes on click-away. Repeat from Action Center (Win+N) for a toast that has slid off screen.
6. Open Settings, untick **Notify when a limit turns**, save, then do the step-3 cross again: no toast, `fetch.log` says `switched off; suppressed`. Re-tick and save: still no toast (the crossing while off is not replayed).
7. Quit the app while a toast is still in Action Center, relaunch detached: the old toast is gone.
8. Windows *Settings → System → Notifications → Claude Usage Tray* → off: cross again; `fetch.log` has `disabled on the Windows side (DisabledForApplication)` once, no exception. Turn it back on.
9. Status: temporarily set `"claude": { "enabled": true, "notify": true, "components": ["nonexistent-component-xyz"] }` — this narrows relevance to nothing — then remove the filter again while the page is healthy: no toast either way (filter change re-baselines). A real page transition cannot be forced; verify the degraded text once against a recorded outage in `fetch.log` if one occurs during the beta, otherwise rely on `NotificationRulesStatusTests`.
10. `dotnet run --project src/ClaudeUsageTray` from the worktree: no toast, no exception, log line for the unavailable notifier or none.

- [ ] **Step 3: Paste the completed list into the PR description**

Anything that failed gets a fix commit on this branch (`fix: …`, `Refs #14`) and the check re-run before the PR is marked ready.

- [ ] **Step 4: Final commit if the checks required no code change**

If steps 2–3 turned up nothing, there is nothing to commit here; the PR closes the issue. If a follow-up was needed, its last commit body uses `Closes #14` instead of `Refs #14`.

---

## Self-review against the spec

- **Where the decisions live** → Tasks 3, 4, 5 (`Core`), 6 (`Tray`). ✔
- **`UsageValues`: keys, labels, OrdinalIgnoreCase, all scoped limits, credits' payload preference, popup and `TrayApp` rewired, `ParseSeverity` moved to Core** → Task 3. ✔
- **Usage rules 1–7, no `FetchedAt` rule, first sight baseline, eviction on rename** → Task 4. ✔
- **Arming** (terminal outcomes, 429/network do not arm, third attempt backstop, arming is a baseline, `NoToken` relayed from `StartApiFetch`) → Tasks 4 and 7. ✔
- **Hysteresis** (re-arm only on Green) → Task 4. ✔
- **Configuration changes** (fingerprint of six settings; switches excluded; leaving is silent; level semantics) → Tasks 4 and 2. ✔
- **Status transitions** (IsRelevant, null not a reading, first reading baseline, disabled drops state, filter change re-baselines, failed fetch needs no rule) → Task 5; disabled sources are absent from `Sources()`, which is what `OnStatus` uses to drop state. ✔
- **Text** (page's own words, `Summary`, recovery never overclaims via `Header(relevant:false)`) → Task 5. ✔
- **Logging** (per event, reasons named, scoped by count, Windows-side disabled) → Tasks 4, 5, 6, 7. ✔
- **Settings** (`notify` default true, independent of `enabled`; `usageNotifications`; per-field fallback; camelCase enum) → Task 2. ✔
- **Dialog** (three controls, OpenAI toggle disabled with the watch checkbox; the three copy paths) → Task 8. ✔
- **`ToastPresenter`** (TFM bump, AUMID from `PackId` with drift test, marshalled activation, live objects held, history cleared at startup, expiry, graduated failure, Windows-side setting logged, group/tag, click → `ShowPopup`) → Tasks 1, 6, 7. ✔
- **Where it runs** (single site in `Render()`) → Task 7. ✔
- **Error handling** (in-memory state, restart re-baselines) → inherent in Task 4; no persistence anywhere. ✔
- **Testing list** → every bullet has a named test in Tasks 1–5, 8; the `ToastPresenter` hand list is Task 10. ✔
- **Wiring the settings through** (`Draft`, `Clone`, `ApplySettings`) → Tasks 7 and 8. ✔

**Type consistency check:** `NotificationOutcome(Notification? Notification, IReadOnlyList<string> Log, bool RemoveUsageToast)` is what Tasks 4, 5 and 7 all use; `NotifyFor(string)` is defined in Task 2 and consumed in Task 5; `UsageValues.KeyComparer` is defined in Task 3 and consumed in Task 4; `AppInfo.Aumid` is defined in Task 1 and consumed in Task 7; `NotificationRules.UsageTag`/`OpenPopupArgument`/`StatusTag` are defined in Tasks 4–5 and consumed in Task 7 and the tests.
