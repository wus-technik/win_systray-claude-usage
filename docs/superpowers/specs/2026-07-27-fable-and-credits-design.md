# Model-scoped windows and credit usage in the popup

Design for [issue #3](https://github.com/wus-technik/win_systray-claude-usage/issues/3).
Date: 2026-07-27.

## Problem

The tray surfaces only the two generic rolling windows, `five_hour` and `seven_day`. Two
things the account is actually limited by are invisible:

1. **Per-model weekly usage** (today: Fable), so heavy use of one model is visible before
   it throttles.
2. **Credit usage against the credit limit**, so paid extra-usage spend is visible.

## Payload findings

The issue listed the JSON field names as unverified and warned against probing the
rate-limited endpoint. No probe was needed: `~/.claude.json` →
`cachedUsageUtilization.utilization` **is** the API response body verbatim — the cache
reader reads the fields under that key while the API client reads the same fields at the
response root. Reading the local file resolved every open question.

### There is no Fable field

Every flat per-model slot is `null`: `seven_day_opus`, `seven_day_sonnet`,
`seven_day_oauth_apps`, `seven_day_cowork`, `seven_day_omelette`, `tangelo`,
`iguana_necktie`, `omelette_promotional`, `nimbus_quill`, `cinder_cove`, `amber_ladder`.

Per-model usage lives in a `limits` array instead:

```json
"limits": [
  { "kind": "session",       "group": "session", "percent": 0,   "severity": "normal",
    "resets_at": null, "scope": null, "is_active": false },
  { "kind": "weekly_all",    "group": "weekly",  "percent": 90,  "severity": "critical",
    "resets_at": "2026-07-27T16:00:00.081713+00:00", "scope": null, "is_active": false },
  { "kind": "weekly_scoped", "group": "weekly",  "percent": 100, "severity": "critical",
    "resets_at": "2026-07-27T16:00:00.081947+00:00", "is_active": true,
    "scope": { "model": { "id": null, "display_name": "Fable" }, "surface": null } }
]
```

`limits` is a **superset** of what the app parses today: `kind: "session"` mirrors
`five_hour` and `kind: "weekly_all"` mirrors `seven_day`.

### Credits appear twice

Money-typed `spend`, and legacy `extra_usage`:

```json
"spend": {
  "used":  { "amount_minor": 4001, "currency": "EUR", "exponent": 2 },
  "limit": { "amount_minor": 4000, "currency": "EUR", "exponent": 2 },
  "percent": 100, "severity": "critical", "enabled": true, "disabled_reason": null,
  "cap": { "money": { "amount_minor": 4000, "currency": "EUR", "exponent": 2 }, "credits": null },
  "balance": null, "auto_reload": null, "can_purchase_credits": false, "can_toggle": false
},
"extra_usage": {
  "is_enabled": true, "monthly_limit": 4000, "used_credits": 4001, "utilization": 100,
  "currency": "EUR", "decimal_places": 2, "disabled_reason": null, "user_disabled": false,
  "spend_limit_reached": false, "credits_ever_enabled": true, "daily": null, "weekly": null
}
```

This settles the issue's open questions:

- Credits are **both** an absolute amount and a percent, so the display can show both.
- The currency is account-scoped and **not** necessarily USD — the observed account is EUR,
  so the issue's `$12.40 / $50.00` mockup would render the wrong symbol.
- `extra_usage` is **also in minor units**: `used_credits: 4001` / `monthly_limit: 4000`
  are byte-identical to `spend`'s `amount_minor` values at the same exponent. The issue
  flagged these units as ambiguous; they are not.

### No plan detection is required

There is no plan, tier, or subscription field anywhere in the payload. None is needed:
`limits` is already plan-shaped — an entry exists only when that limit applies to the
account. Absent Fable window means no entry, which means no row. The issue's "option 1"
(derive the plan) is impossible and its "option 2" (infer from field presence) becomes
data-driven rather than a guess.

## Design

### Data model — `Core/UsageSnapshot.cs`

```csharp
/// <summary>One model-scoped weekly limit from limits[] (e.g. Fable). Label comes from the payload.</summary>
public sealed record ModelLimit(string Label, int Percent, DateTimeOffset? ResetsAt);

/// <summary>Extra-usage credit spend, in the payload's own minor-unit money encoding.</summary>
public sealed record CreditUsage(
    long UsedMinor, long LimitMinor, string Currency, int Exponent, int Percent, bool Enabled);

public sealed record UsageSnapshot(
    DateTimeOffset FetchedAt,
    WindowUsage? FiveHour,
    WindowUsage? SevenDay,
    IReadOnlyList<ModelLimit> ModelLimits,   // empty == absent; never null
    CreditUsage? Credits);
```

`ModelLimits` is non-optional and non-nullable. For a collection, "absent" and "empty" are
the same thing to a renderer, so nullability would only add a `?? []` at every read site.
C# cannot default a parameter to `[]`, so this deliberately breaks the 3-argument
constructor and lets the compiler enumerate the construction sites (API client, cache
reader, and the test files that build snapshots) — preferred over silent nullability.

`Core/SnapshotPrecedence.cs` needs no change: it swaps whole snapshots by `FetchedAt`, so
new members ride along.

### Parsing — `Core/UsageJson.cs`

Both new readers take the container element, so the API path (fields at the response root)
and the cache path (fields under `utilization`) share one implementation, exactly as
`ReadWindow` does today.

`ReadModelLimits(JsonElement parent) → IReadOnlyList<ModelLimit>`

- Returns empty when `limits` is missing or is not an array.
- Keeps an entry only when `group == "weekly"` **and** `scope.model.display_name` is a
  non-empty string. This excludes `session` and excludes `weekly_all` (whose `scope` is
  null), so the generic windows are never duplicated as model rows.
- Reads `percent` with the same double-then-round handling as `utilization`, and `resets_at`
  with the same ISO parse.
- A malformed entry is skipped individually; its siblings still parse.
- Ignores `is_active`. Its semantics are undocumented, and a scoped limit at 40% is real
  usage whether or not it is the currently binding one.
- Generic by design: labels come from the payload, so a renamed or additional model appears
  with no code change. The payload's rotating codenames (`omelette`, `tangelo`,
  `nimbus_quill`, `cinder_cove`) are direct evidence that hardcoding `"Fable"` would rot.

`ReadCredits(JsonElement parent) → CreditUsage?` = `ReadSpend(parent) ?? ReadExtraUsage(parent)`

- `ReadSpend` requires both `used.amount_minor` and `limit.amount_minor` to parse, else
  returns null. Currency and exponent come from `used`; percent from `spend.percent`;
  `Enabled` from `spend.enabled`.
- `ReadExtraUsage` maps `used_credits` → `UsedMinor`, `monthly_limit` → `LimitMinor`,
  `currency` → `Currency`, `decimal_places` → `Exponent`, `utilization` → `Percent`,
  `is_enabled` → `Enabled`.
- The fallback exists because the cache is written by whatever Claude Code version the user
  runs; an older one may emit only the legacy shape, and the fallback avoids a blank row.

Targeted cleanup while in this file: percent-and-reset parsing is now needed in two shapes
(`utilization` + `resets_at` for windows, `percent` + `resets_at` for limits). Factor out
`ReadRoundedPercent(element, propertyName)` and `ReadResetsAt(element)` so `ReadWindow` and
`ReadModelLimits` share them instead of duplicating the rounding and the
`AssumeUniversal | AdjustToUniversal` ISO parse.

### Formatting — `Core/CreditFormat.cs` (new)

```csharp
/// <summary>"40.01 / 40.00 EUR (100%)" — amount_minor scaled by 10^exponent.</summary>
public static string Describe(CreditUsage c);
```

A pure function in Core, so it is unit-testable without WinForms — the reason the credit row
will not end up as untested as the existing popup rows.

Two deliberate calls:

- **ISO code, not currency symbol.** A code-to-symbol table is wrong for every code not in
  it, and `CultureInfo.CurrentCulture` describes the *user's* locale, not the *account's*
  currency — it would print `$` for a EUR account. The ISO code is unambiguous and never
  wrong.
- **`Enabled == false` shows the row with ` · disabled` appended**, not hidden. Credit
  already consumed is real information and suppressing it would under-report spend. The
  observed account illustrates why the two are independent: `spend.enabled: true` while
  `cachedExtraUsageDisabledReason` is `"org_spend_cap_reached"`.

### Rendering — `Tray/UsagePopup.cs`

Placement is the left-click popup, not the context menu. The popup already renders windows
as severity-colored bars with reset countdowns, which is what these numbers need; the
context menu stays a pure action menu. This departs from the issue's "menu-only" wording —
the issue predates treating the popup as the display surface.

```
5-hour window — 0%
7-day window — 90% · resets in 1h 20m
Fable weekly — 100% · resets in 1h 20m      ← one row per ModelLimit
Credits — 40.01 / 40.00 EUR (100%)          ← when Credits is not null
Last updated 2 min ago
```

- Model limits reuse `AddWindowRow` verbatim by wrapping each `ModelLimit` in a
  `WindowUsage` — same bar, same severity colors, same countdown, no new drawing code.
  Label is `$"{l.Label} weekly"`.
- The credit row gets its own builder but colors from
  `SeverityRules.For(c.Percent, settings.Thresholds.Orange, settings.Thresholds.Red)`, not
  the payload's own `severity` string, so one user-configurable threshold pair governs every
  bar in the popup.
- Empty `ModelLimits` and null `Credits` render nothing — no placeholder, no `0%`, no
  "no data" line. This satisfies the issue's "hidden on plans without a Fable window"
  criterion with no plan detection.
- Bars are a fixed 240 px and the form is `AutoSize`, so additional rows only grow height.
- Icon text and the display modes (`Show 5h` / `Show 7d` / `Show both`) are untouched.

### Tests

`UsageCacheReaderTests` — fixtures cut from the real payload shape:

- Full payload: the Fable limit and `spend` both parse.
- No `limits` and no `spend` keys: empty list, null credits, no crash.
- One malformed limit entry among valid ones: siblings survive.
- A `weekly_all` entry with `scope: null` and a `weekly_scoped` entry missing
  `display_name`: both excluded.
- A `session`-kind entry: excluded by the `group == "weekly"` filter.
- `extra_usage` present without `spend`: the fallback fires.

`UsageApiClientTests` — the same fixtures at root nesting, proving the shared readers work
on both paths.

`CreditFormatTests` (new) — `Describe` at exponent 2 and exponent 0, over-limit (percent
above 100), and `Enabled: false`.

Existing 5h/7d tests stay green unchanged; that is the no-regression check.

## Acceptance criteria

- [ ] Model-scoped weekly limits and credit usage parse from both the API and the
      `.claude.json` cache, degrading to empty/null when absent.
- [ ] The popup shows one bar per model limit and a credit row, each hidden when its data is
      unavailable.
- [ ] A model limit's label comes from the payload, so a renamed model needs no code change.
- [ ] No model row is ever rendered as `0%` or `—` when the limit does not apply.
- [ ] Credits render with the account's own currency code and exponent, not an assumed `$`.
- [ ] Tests cover: field present, field absent, malformed entry, legacy-only credit shape,
      the non-Max/Pro case (no scoped entry → no row), and credit formatting.
- [ ] No regression to the existing 5h/7d rows, icon text, or display modes.

## Out of scope

- A display mode that puts a model window or credits in the icon text.
- Using the payload's own `severity` strings in place of `SeverityRules`.
- Replacing the flat `five_hour` / `seven_day` reads with `limits`-derived values. Coherent
  long-term, but it rewrites working parse paths and risks the regression the issue forbids.
- `spend.balance`, `spend.cap`, `auto_reload`, `can_purchase_credits`, and the
  `overageCreditGrantCache` block.
