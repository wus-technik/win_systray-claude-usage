# Claude Desktop usage history as a fallback source

Date: 2026-09-05
Issue: [#5](https://github.com/wus-technik/win_systray-claude-usage/issues/5)

## Problem

Both of the tray's usage sources are written by the Claude Code CLI: the `cachedUsageUtilization`
key in `~\.claude.json`, and the OAuth token in `~\.claude\.credentials.json` that the live
`oauth/usage` fetch needs. A user who only uses the Claude Desktop app never gets either, so the
icons stay at `—` and the popup says *run Claude Code* forever.

Three read-only probes on the issue thread changed the picture of what "desktop-only" looks like on
disk. The desktop app installs its own Claude Code binary for its built-in integration, so a
desktop-only machine **does** have `~\.claude.json` (with `oauthAccount` and `userID`, but no
`cachedUsageUtilization`), a populated `~\.claude\`, and **no** credentials file. The current
"no `.claude.json` → run Claude Code" hint therefore never fires for the very user it was written
for. The probes also showed that the CLI's own cache degrades on machines with an actively used
CLI: one had the key seven days stale in a file that churns every few minutes, another had no key
at all.

The desktop app keeps a local usage history that needs no token. This design adds it as a fallback
source and replaces the single "run Claude Code" hint with messages that name what is missing.

## Data source

`plan-usage-history.json`, written by the desktop app. Two locations exist in the wild and the
install kind does not predict which one is used (all three probed machines were MSIX installs of
the same package family, `Claude_pzs8sxrjxfjjc`):

| Desktop version | Writes to |
|---|---|
| 1.32885.1.0 | `%APPDATA%\Claude\plan-usage-history.json` |
| 1.44121.4.0, 1.46388.2.0 | `%LOCALAPPDATA%\Packages\Claude_<publisherhash>\LocalCache\Roaming\Claude\plan-usage-history.json` |

Verified shape (org uuid redacted; `samples[]` is ascending on all three machines, but the reader
must not depend on that):

```json
{"version":2,"samples":[
  {"t":1785247200000,"org":"<uuid>","u":{"fh":63,"sd":29,"xu":66.68333333333332}},
  {"t":1785247500144,"org":"<uuid>","u":{"fh":64,"sd":29}}
]}
```

Field semantics, **inferred** from observation on three machines and not documented by Anthropic:

- `t` — epoch milliseconds of the sample.
- `u.fh` — five-hour window utilization, 0…100, resets to 0 at window boundaries.
- `u.sd` — seven-day window utilization, 0…100 (the observed maximum is machine-specific).
- `u.xu` — extra-usage (credits) utilization, 0…100, present only while credits are enabled and
  nonzero. It is the only figure that carries decimals, but roughly a quarter of its values are
  whole numbers, so nothing may key on decimal-ness.
- `org` — org uuid. Two machines had samples from two orgs (org switches). The newest sample is
  the current one regardless of org; per-org handling would be wrong.
- Sample keys are exactly `org, t, u`; `u` keys are exactly `fh, sd, xu`. Nothing else appears.

Absent compared to the `oauth/usage` payload: reset timestamps, scoped per-model limits, credit
money amounts. The source yields percentages only.

**Cadence.** Nominally about five minutes with tens of seconds of jitter, but only while someone is
working in the desktop app. Measured densities were 16, 22 and 179 samples per day; one machine had
a mean gap of 66 minutes and an 80-minute-old newest sample while the app was running. A 15-minute
staleness cutoff would therefore flag a desktop-only user as stale most of the time. One machine
also showed the sampler stopping 20 days before the app was last used, so "stale" cannot be
attributed to a cause and the messaging does not try.

**Rejected alternative.** Reading the desktop app's own OAuth token. There is no Credential Manager
entry on any probed machine; the token lives in Electron's DPAPI-encrypted storage inside the
package container. Decrypting another app's session store is fragile and against this tool's
read-only, non-invasive design. Not pursued.

## Design

### Core (pure, unit-tested)

**`Core/DesktopHistoryPath.cs`**

```csharp
public static IReadOnlyList<string> Candidates(string? overridePath, string appData, string localAppData);
public static IReadOnlyList<string> ByFreshness(IEnumerable<string> candidates);
```

- With a non-blank `overridePath`, `Candidates` returns only that path.
- Otherwise: `<appData>\Claude\plan-usage-history.json`, then every
  `<localAppData>\Packages\Claude_*\LocalCache\Roaming\Claude\plan-usage-history.json` found by
  enumerating `Packages` for directories matching `Claude_*` (the publisher hash is not hardcoded).
  A missing or unreadable `Packages` directory contributes nothing.
- `Freshest` is replaced by `ByFreshness(candidates)`: the existing candidates ordered by
  `LastWriteTimeUtc`, newest first. The caller tries `DesktopUsageReader.TryRead` on each in turn
  and keeps the first snapshot, so a half-written or malformed newer file cannot mask an older
  usable one. The ordering key is the **usage file's own** write time, never its directory's: on
  one machine `%APPDATA%\Claude` is an orphaned profile whose subdirectories are still touched
  while the usage file inside it is weeks old.
- Never throws. Every candidate is probed under its own guard for `IOException`,
  `UnauthorizedAccessException`, `ArgumentException`, `NotSupportedException` and
  `PathTooLongException`; a candidate that fails is dropped, the others survive. An invalid
  override path yields an empty list. Enumerating `Packages` is guarded the same way (an
  unpackaged process may lack access to some package directories).
- The existence of `%APPDATA%\Claude` is never used as a signal that the desktop app is installed.
  Finding a usage-history file *is* the detection.

**`Core/DesktopUsageReader.cs`**

```csharp
public static UsageSnapshot? TryRead(string path);
```

- Same never-throw contract and IO discipline as `UsageCacheReader`: `File.Exists` check, a size
  guard (16 MiB; the largest observed file is 172 KB), `FileShare.ReadWrite`, and the same
  exception filter. Any failure returns null.
- Requires a root object with a `samples` array. `version` is read for nothing; an unknown version
  is parsed on a best-effort basis rather than rejected, because a version bump that keeps the field
  names should not blank the display.
- Selects the sample with the **maximum `t`** across the whole array in one pass (the array is a
  few hundred to a few thousand elements; no materialisation). Array position and `org` are
  ignored. A sample is skipped, never fatal, when it is not an object, has no `t` that
  `TryGetInt64` accepts, or has no `u` object. Percent fields are read with
  `UsageJson.ReadRoundedPercent`, which already tolerates out-of-range and non-numeric values by
  returning null.
- `FiveHour` = `WindowUsage(Round(fh), ResetsAt: null)`, `SevenDay` likewise; a missing or
  non-numeric field yields null for that window. The popup then shows the existing
  *5-hour window: no data* label, exactly as it does today for a CLI snapshot missing a window;
  this design does not change that behaviour. Rounding is away-from-zero to an integer.
- `Credits` = `CreditUsage(Used: null, Limit: null, Percent: Round(xu), PayloadSeverity: null,
  State: new CreditState(Enabled: true, DisabledReason: null, LimitReached: false))` when `xu` is
  present; null otherwise. Percent-only, no money, and the existing credit row already renders a
  percent-only `CreditUsage` (that is the legacy `extra_usage` path).
- `ScopedLimits` empty. `FetchedAt` = the selected sample's `t`. `Source` = `DesktopHistory`.
- If no sample qualifies, returns null.

**`Core/UsageSnapshot.cs`** — gains

```csharp
public enum UsageSource { ClaudeCode, DesktopHistory }
```

and an init-only `Source` property defaulting to `ClaudeCode`, so every existing constructor call
and test is unchanged. The cache reader and the API client both produce `ClaudeCode`; the
distinction that matters to the user is "Claude Code's data" versus "the desktop app's data".

**`Core/SourceSelection.cs`**

```csharp
public sealed record DisplayChoice(UsageSnapshot? Snapshot, bool Stale);
public static DisplayChoice Choose(UsageSnapshot? cli, UsageSnapshot? desktop, DateTimeOffset now, Settings settings);
```

Fallback-only precedence, with `age = now - FetchedAt`:

1. CLI snapshot present and `age <= StalenessMinutes` → CLI, not stale.
2. Else desktop snapshot present and `age <= DesktopStalenessHours` → desktop, not stale.
3. Else whichever of the two is present, or the newer by `FetchedAt` when both are → that one, stale.
4. Neither → `(null, false)`.

A snapshot whose `FetchedAt` lies more than five minutes in the **future** (clock skew, or a bad
sample) never satisfies rules 1 or 2: it is treated as stale, so it can only be shown under rule
3 and is flagged. Skew of up to five minutes counts as an age of zero. `RelativeTime.Ago`
already renders any negative elapsed time as *just now*.

Rule 1 keeps the richer live/cache snapshot (resets, scoped limits, money) whenever it is current.
Rule 2 is what fixes the issue and also what replaces a frozen seven-day-old CLI cache by design
rather than by accident. Rule 3 keeps the "a dead source degrades to stale, never to blank"
behaviour the app already has.

**Snapshot lifetime.** `Choose` is pure; the two slots it reads are owned by `TrayApp`:

- `_cliSnapshot` keeps today's rules unchanged: cache reads replace it only via
  `SnapshotPrecedence.IsNewer`; a live result likewise; a missing `.claude.json` or three
  consecutive failed reads clear it **only once it is already past `StalenessMinutes`** (the
  `_consecutiveReadFailures` / `_retry` logic moves over untouched).
- `_desktopSnapshot` follows the same shape with its own allowance: a successful read replaces it
  via `IsNewer`; when no candidate yields a snapshot the last value is kept while its age is within
  `DesktopStalenessHours` and cleared once past it. A transient read failure therefore never blanks
  a desktop-only user's display mid-session.

**`Core/NoDataReason.cs`**

```csharp
public enum ConfigStatus { Missing, NoUsageKey, Unreadable }        // Unreadable = key present, parse failed
public enum CredentialStatus { Missing, Unusable, Valid }           // Unusable = file present, no valid token
public enum DesktopHistoryStatus { NotFound, Unreadable, NoSamples }
public sealed record NoDataFacts(ConfigStatus Config, CredentialStatus Credentials, DesktopHistoryStatus Desktop);
public static string Describe(NoDataFacts facts);
```

Used only when `SourceSelection` yields no snapshot. One sentence per state, first match wins:

| State | Text |
|---|---|
| desktop `NoSamples` | *Claude Desktop history found, but no samples yet.* |
| desktop `Unreadable` | *Claude Desktop history found, but it could not be read.* |
| config `Missing` (desktop `NotFound`) | *No usage data yet — open Claude Code or Claude Desktop.* |
| config `NoUsageKey`, credentials `Missing` | *Claude Code has not cached usage data, and there is no credentials file for a live fetch.* |
| config `NoUsageKey`, credentials `Unusable` | *Claude Code has not cached usage data, and its credentials are not usable for a live fetch.* |
| config `NoUsageKey`, credentials `Valid` | *Claude Code has not cached usage data yet — waiting for the first live fetch.* |
| config `Unreadable` | *Claude Code's cached usage data could not be read.* |

Only the third state tells the user to open something. The facts come from the callers that
already touch the files: `UsageCacheReader` gains `Status(path)` (existence plus a guarded
string-contains check for `cachedUsageUtilization`), `CredentialsReader` gains `Status(path, now)`
(existence plus the existing token validation), and `DesktopUsageReader` gains a `TryRead`
overload that reports why it returned null. All three keep the never-throw contract.

### Tray

- `TrayApp` holds `_cliSnapshot` and `_desktopSnapshot` with the lifetimes defined above.
  `Refresh()` additionally walks `DesktopHistoryPath.ByFreshness(Candidates(...))` and reads until
  one candidate parses. Both happen on every refresh (the 30 s tick, watcher events, manual
  refresh). No `FileSystemWatcher` on the desktop file: it sits in a package container, is at most
  a few hundred KB, and the tick already exists as the recovery path for the CLI file.
- `Render()` and `UsagePopup` take the `DisplayChoice`. The `Stale` flag replaces the inline
  `StalenessMinutes` comparisons for the usage snapshot (platform status keeps its own).
- When the shown snapshot is `DesktopHistory`, the popup's last-updated line reads
  *Claude Desktop history · updated 40m ago* (plus *· stale* when flagged) and the tooltip's
  updated part says *Claude Desktop history · updated 40m ago*. The icon itself does not change;
  with `ResetsAt` null the time marker is simply absent and pace colouring falls back to the
  absolute thresholds, which `SeverityRules.ForPace` and `TimeMarker` already do (verified: both
  are exercised with null reset times by the existing tests). The credit row renders a percent-only
  `CreditUsage` as *NN%* with no state line, which is the existing legacy `extra_usage` path.
- Live path: when `CredentialsReader` returns null, `_lastFetchStatus` becomes
  *no credentials file · live fetch off* when the file is absent and *no valid credentials · live
  fetch off* when it exists, so the popup's Fetch line explains why the live path is silent. The
  existing `fetch.log` skip line stays.
- `fetch.log` gets one line when the selected source changes, and one when the desktop sample
  advances: `desktop: adopted 5h=7% 7d=17% age=80m` / `source: desktop history (cli stale 7d)`.
  Percentages and ages only, no org uuid, nothing account-specific.

### Settings

| Key | Meaning | Default |
|---|---|---|
| `desktopStalenessHours` | hours before desktop-history data is flagged stale | `3` |
| `desktopHistoryPathOverride` | explicit path to `plan-usage-history.json`; file-only, re-read at launch | unset |

`desktopStalenessHours` appears in the settings dialog as a second spinner under the existing
staleness one, labelled *Claude Desktop history stale after (h)*, range 1…168, default 3, and
takes part in every path the dialog already has for `StalenessMinutes`: `Clone`, `Draft`,
`ApplySettings`, *Reset to defaults*, and tab order. A negative or zero file value normalises to
the default like `stalenessMinutes`. `desktopHistoryPathOverride` is file-only like
`configPathOverride`, and it exists because two real paths already exist in the wild and a third
should not need a release.

README: the settings table gains both rows, and the "no data" explanation is rewritten around the
five states above.

## Testing

All new logic is in `Core/` and tested against temp directories and fixtures; nothing needs the
tray.

- **`DesktopHistoryPathTests`**: classic only; container only; both, classic fresher; both,
  container fresher (order asserted, not just the head); neither; override set (only the override
  is returned, even when the defaults exist); invalid override path (empty list, no throw); two
  `Claude_*` package directories; `Packages` missing. Freshness is asserted by setting
  `File.SetLastWriteTimeUtc`, and one case gives the *directory* of the older file a newer write
  time to prove the ordering reads the file. Access-denied enumeration cannot be provoked portably
  in a unit test and is covered by the exception filter alone.
- **`DesktopUsageReaderTests`**: a fixture in the real shape; descending and shuffled `samples`
  (max-by-`t` wins); multi-org where the newest sample belongs to the minority org; `xu` absent
  (no credits); `xu` non-integer (rounded) and integer; `fh` missing (five-hour null, seven-day
  present); empty `samples` (status `NoSamples`); `samples` not an array; malformed JSON (status
  `Unreadable`); missing file (status `NotFound`); over-size file; a sample that is not an object,
  one without `t`, one with a `t` out of `Int64` range, one without `u`, all skipped while a
  valid sibling is still selected; `version` other than 2 still parsed.
- **`SourceSelectionTests`**: fresh CLI beats fresh desktop; stale CLI yields to fresh desktop;
  both stale → newer wins and is flagged; only CLI stale; only desktop stale; neither; the
  desktop allowance is read from `DesktopStalenessHours`, not `StalenessMinutes`; a `FetchedAt`
  four minutes in the future counts as fresh; one an hour in the future counts as stale and loses
  to a fresh alternative.
- **`NoDataReasonTests`**: one case per row of the table, and the first-match precedence when
  several facts are true.
- **`SettingsTests`**: the two new keys round-trip; zero and negative `desktopStalenessHours`
  normalise; an old settings file without the keys loads with defaults.
- **`SettingsDialogTests`**: the new spinner round-trips through `Draft`, applies, and resets with
  the defaults.
- **`UsagePopupWidthTests`** gains a desktop-source snapshot to check the longer last-updated line
  still fits, and a desktop snapshot with one window null plus a percent-only credit row to pin the
  render.
- **TrayApp lifetime**: the clearing rules are exercised indirectly through `SourceSelection`;
  the slot rules themselves are two `if`s in `Refresh()` and are reviewed, not unit-tested, like
  the existing `_consecutiveReadFailures` logic.

## Out of scope

- Any access to the desktop app's token or encrypted storage.
- Diagnosing why the CLI stops maintaining `cachedUsageUtilization`. It affects the fallback rule
  only in that it makes the rule fire more often, and it belongs in its own issue.
- Deriving reset times or window boundaries from the history (e.g. detecting `fh` dropping to 0).
  The pace fallback to absolute thresholds is the intended behaviour for this source.
