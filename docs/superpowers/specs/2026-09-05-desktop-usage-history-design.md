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
public static string? Freshest(IEnumerable<string> candidates);
```

- With a non-blank `overridePath`, `Candidates` returns only that path.
- Otherwise: `<appData>\Claude\plan-usage-history.json`, then every
  `<localAppData>\Packages\Claude_*\LocalCache\Roaming\Claude\plan-usage-history.json` found by
  enumerating `Packages` for directories matching `Claude_*` (the publisher hash is not hardcoded).
  A missing or unreadable `Packages` directory contributes nothing.
- `Freshest` returns the existing candidate with the newest `LastWriteTimeUtc`, or null. The
  tie-break is the **usage file's own** write time, never its directory's: on one machine
  `%APPDATA%\Claude` is an orphaned profile whose subdirectories are still touched while the usage
  file inside it is weeks old.
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
- Selects the sample with the **maximum `t`** across the whole array. Array position and `org` are
  ignored. Samples without a numeric `t` or without a `u` object are skipped.
- `FiveHour` = `WindowUsage(Round(fh), ResetsAt: null)`, `SevenDay` likewise; a missing or
  non-numeric field yields null for that window (no row is rendered, per the "absent data means no
  row" invariant). Rounding is `UsageJson.ReadRoundedPercent`, i.e. away-from-zero to an integer.
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

Fallback-only precedence:

1. CLI snapshot present and `now - FetchedAt <= StalenessMinutes` → CLI, not stale.
2. Else desktop snapshot present and `now - FetchedAt <= DesktopStalenessHours` → desktop, not stale.
3. Else whichever of the two is present, or the newer by `FetchedAt` when both are → that one, stale.
4. Neither → `(null, false)`.

Rule 1 keeps the richer live/cache snapshot (resets, scoped limits, money) whenever it is current.
Rule 2 is what fixes the issue and also what replaces a frozen seven-day-old CLI cache by design
rather than by accident. Rule 3 keeps the "a dead source degrades to stale, never to blank"
behaviour the app already has.

**`Core/NoDataReason.cs`**

```csharp
public sealed record NoDataFacts(bool ConfigExists, bool ConfigHasUsageKey, bool CredentialsExist, bool DesktopHistoryFound);
public static string Describe(NoDataFacts facts);
```

Used only when `SourceSelection` yields no snapshot. Exactly one sentence per state, in this order:

| State | Text |
|---|---|
| desktop history found (but yielded no snapshot) | *Claude desktop usage history found, but it holds no samples yet.* |
| no `.claude.json`, no desktop history | *No Claude usage data yet — run Claude Code or the Claude desktop app.* |
| `.claude.json` without the usage key, no credentials | *Claude Code has not cached usage data, and there is no credentials file for a live fetch.* |
| `.claude.json` without the usage key, credentials present | *Claude Code has not cached usage data yet — waiting for the first live fetch.* |
| `.claude.json` with the key (but it did not parse) | *Claude Code's cached usage data could not be read.* |

Only the second state tells the user to run something. `ConfigHasUsageKey` is a cheap
string-contains check on the file done by the caller; the reader's parse result is the authority on
whether it is usable. `UsageCacheReader` gains a small `HasUsageKey(path)` helper for this so the
check has the same IO guards.

### Tray

- `TrayApp` holds `_cliSnapshot` (cache and live, merged via `SnapshotPrecedence` exactly as
  today) and `_desktopSnapshot`. `Refresh()` additionally resolves
  `DesktopHistoryPath.Freshest(Candidates(...))` and reads it. Both happen on every refresh (the
  30 s tick, watcher events, manual refresh). No `FileSystemWatcher` on the desktop file: it sits in
  a package container, is at most a few hundred KB, and the tick already exists as the recovery path
  for the CLI file.
- `Render()` and `UsagePopup` take the `DisplayChoice`. The `Stale` flag replaces the inline
  `StalenessMinutes` comparisons for the usage snapshot (platform status keeps its own).
- When the shown snapshot is `DesktopHistory`, the popup's last-updated line reads
  *Desktop app history · updated 40 min ago* (plus *· stale* when flagged) and the tooltip's
  updated part says *desktop app history · updated 40 min ago*. The icon itself does not change;
  with `ResetsAt` null the time marker is simply absent and pace colouring falls back to the
  absolute thresholds, which `SeverityRules.ForPace` already does.
- Live path: when `CredentialsReader` returns null, `_lastFetchStatus` becomes
  *no credentials file · live fetch off* so the popup's Fetch line explains why the live path is
  silent. The existing `fetch.log` skip line stays.
- `fetch.log` gets one line when the selected source changes, and one when the desktop sample
  advances: `desktop: adopted 5h=7% 7d=17% age=80m` / `source: desktop history (cli stale 7d)`.
  Percentages and ages only, no org uuid, nothing account-specific.

### Settings

| Key | Meaning | Default |
|---|---|---|
| `desktopStalenessHours` | hours before desktop-history data is flagged stale | `3` |
| `desktopHistoryPathOverride` | explicit path to `plan-usage-history.json`; file-only, re-read at launch | unset |

`desktopStalenessHours` appears in the settings dialog next to the existing staleness field; a
negative value normalises to the default like `stalenessMinutes`. `desktopHistoryPathOverride` is
file-only like `configPathOverride`, and it exists because two real paths already exist in the
wild and a third should not need a release.

README: the settings table gains both rows, and the "no data" explanation is rewritten around the
five states above.

## Testing

All new logic is in `Core/` and tested against temp directories and fixtures; nothing needs the
tray.

- **`DesktopHistoryPathTests`**: classic only; container only; both with the classic file fresher;
  both with the container file fresher; neither; override set (only the override is returned, even
  when the defaults exist); two `Claude_*` package directories; `Packages` missing. Freshness is
  asserted by setting `File.SetLastWriteTimeUtc`, and one case gives the *directory* of the older
  file a newer write time to prove the tie-break reads the file.
- **`DesktopUsageReaderTests`**: a fixture in the real shape; descending and shuffled `samples`
  (max-by-`t` wins); multi-org where the newest sample belongs to the minority org; `xu` absent
  (no credits); `xu` non-integer (rounded) and integer; `fh` missing (five-hour null, seven-day
  present); empty `samples`; `samples` not an array; malformed JSON; missing file; a sample without
  `t` or `u` skipped; `version` other than 2 still parsed.
- **`SourceSelectionTests`**: fresh CLI beats fresh desktop; stale CLI yields to fresh desktop;
  both stale → newer wins and is flagged; only CLI stale; only desktop stale; neither; the
  desktop allowance is read from `DesktopStalenessHours`, not `StalenessMinutes`.
- **`NoDataReasonTests`**: one case per state, and the precedence when several facts are true.
- **`SettingsTests`**: the two new keys round-trip; a negative `desktopStalenessHours` normalises;
  an old settings file without the keys loads with defaults.
- **`UsagePopupWidthTests`** gains a desktop-source snapshot to check the longer last-updated line
  still fits.

## Out of scope

- Any access to the desktop app's token or encrypted storage.
- Diagnosing why the CLI stops maintaining `cachedUsageUtilization`. It affects the fallback rule
  only in that it makes the rule fire more often, and it belongs in its own issue.
- Deriving reset times or window boundaries from the history (e.g. detecting `fh` dropping to 0).
  The pace fallback to absolute thresholds is the intended behaviour for this source.
