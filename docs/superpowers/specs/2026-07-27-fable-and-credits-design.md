# Model-scoped windows and credit usage in the popup

Design for [issue #3](https://github.com/wus-technik/win_systray-claude-usage/issues/3).
Date: 2026-07-27. Revised after adversarial review (see *Review departures* at the end).

## Problem

The tray surfaces only the two generic rolling windows, `five_hour` and `seven_day`. Two
things the account is actually limited by are invisible:

1. **Per-model weekly usage** (today: Fable), so heavy use of one model is visible before
   it throttles.
2. **Credit usage against the credit limit**, so paid extra-usage spend is visible.

## Payload observations

The issue listed the JSON field names as unverified and warned against probing the
rate-limited endpoint. No probe was needed: reading `~/.claude.json` →
`cachedUsageUtilization.utilization` answered every open question.

> **Evidence strength.** Everything in this section comes from **one account, one plan, one
> currency (EUR), one Claude Code version, on 2026-07-27**. Treat it as an observation, not
> a verified contract. In particular: that the cached `utilization` object is byte-identical
> to the API response body is inferred from the two existing readers agreeing on field names
> at different nesting levels, not from a diffed response. Every rule below is therefore
> written to degrade to "hide the row" rather than to assume the shape holds.

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

`limits` overlaps what the app parses today: `kind: "session"` mirrors `five_hour` and
`kind: "weekly_all"` mirrors `seven_day`.

Note `scope.model.id` is `null` while `display_name` is populated — so **`id` is the
unreliable field here and `display_name` the dependable one**, which is the opposite of what
a stable-identifier-first rule would assume.

### What `is_active` appears to mean

`weekly_all` sits at 90% with `is_active: false`, and the app already displays that window
as unquestionably real. So `is_active: false` **cannot** mean "does not apply". The
consistent reading is that it marks the *currently binding* cap — the one limit closest to
stopping you — which on this account is the scoped Fable entry at 100%.

This is a reading, not a documented fact. The design therefore **parses and retains
`is_active` but does not filter on it**. Filtering to `is_active == true` would have hidden
the 90% weekly window, which is the concrete failure this observation rules out.

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

This settles part of the issue's open questions:

- Credits are **both** an absolute amount and a percent, so the display can show both.
- The currency is account-scoped and **not** necessarily USD — the observed account is EUR,
  so the issue's `$12.40 / $50.00` mockup would render the wrong symbol.

It does **not** settle the units of the legacy block. An earlier draft of this spec claimed
`extra_usage` is "also in minor units, settling the issue's unit ambiguity". That claim is
withdrawn. The observed equality of `used_credits: 4001` with `spend.used.amount_minor: 4001`
proves only that these two blocks agree **on this one account**. Against it:

- The field is named `used_credits`, not an amount.
- `spend.cap` carries **separate `money` and `credits` slots**, which is direct evidence in
  this very payload that Anthropic models money and credits as distinct units.
- `decimal_places: 0` would make `4001` ambiguous between €4001 and 4001 credits.

So the legacy block is treated as percent-and-state only. See *Credits* below.

### No plan detection is required

There is no plan, tier, or subscription field anywhere in the observed payload. None is
needed: a limit the account does not have produces no `limits` entry, so an absent Fable
window means an absent row. The issue's "option 1" (derive the plan) has nothing to read and
its "option 2" (infer from field presence) becomes data-driven rather than a guess.

## Design

### Data model — `Core/UsageSnapshot.cs`

```csharp
/// <summary>An amount in the payload's own money encoding: minor units + ISO code + exponent.</summary>
public sealed record Money(long AmountMinor, string Currency, int Exponent);

/// <summary>One model-scoped weekly limit from limits[] (e.g. Fable).
/// IsActive is retained but never filtered on — see "What is_active appears to mean".</summary>
public sealed record ModelLimit(
    string Label, string? ModelId, int Percent, DateTimeOffset? ResetsAt, bool IsActive);

/// <summary>Credit state beyond the percentage. A bool cannot express the observed case of
/// enabled == true alongside cachedExtraUsageDisabledReason == "org_spend_cap_reached".</summary>
public sealed record CreditState(bool Enabled, string? DisabledReason, bool LimitReached);

/// <summary>Extra-usage credits. Used/Limit are null when only the legacy block is available,
/// because its units are unverified — Percent is then the only trustworthy figure.</summary>
public sealed record CreditUsage(Money? Used, Money? Limit, int Percent, string? PayloadSeverity, CreditState State);

public sealed record UsageSnapshot(
    DateTimeOffset FetchedAt,
    WindowUsage? FiveHour,
    WindowUsage? SevenDay,
    IReadOnlyList<ModelLimit>? ModelLimits = null,
    CreditUsage? Credits = null)
{
    /// <summary>Empty means absent. Never null to consumers, whatever the caller passed.</summary>
    public IReadOnlyList<ModelLimit> ModelLimits { get; init; } = ModelLimits ?? [];
}
```

The explicit property suppresses the auto-generated one and normalizes null to empty in a
single place, so consumers never write `?? []` at a read site. Both new parameters are
**optional**, which keeps the existing three-argument constructor calls — two in production
plus the snapshot-building tests — compiling untouched; only the two readers that actually
have the new data pass it. The parameter is nullable while the property is not, which is the
whole point: laxity at the boundary, a guarantee on the inside.

If the compiler rejects the nullability mismatch between the positional parameter and the
explicit property (records require the two to agree closely enough for the generated
`Deconstruct`), fall back to naming the parameter differently — e.g. a `modelLimits`
constructor parameter with a distinct `ModelLimits` property — rather than reverting to a
nullable public surface. Verify this at implementation time; it has not been compiled.

`Core/SnapshotPrecedence.cs` needs no change: it swaps whole snapshots by `FetchedAt`, so
new members ride along.

### Parsing — `Core/UsageJson.cs`

Both new readers take the container element, so the API path (fields at the response root)
and the cache path (fields under `utilization`) share one implementation, exactly as
`ReadWindow` does today.

#### `ReadModelLimits(JsonElement parent) → IReadOnlyList<ModelLimit>`

Returns empty when `limits` is missing or is not an array. An entry is **renderable** when
all of these hold:

1. `group == "weekly"`. Excludes `session`.
2. `scope` is an object containing a `model` object. Excludes `weekly_all` (whose `scope` is
   `null`), so the generic 7-day window is never duplicated as a model row.
3. A label can be derived: `scope.model.display_name` if a non-empty string, else
   `scope.model.id` if a non-empty string. **No label → skip the entry**, because a bar
   captioned with nothing is worse than a missing bar.

When `scope.surface` is also a non-empty string, the label becomes `"{model} ({surface})"`
so a model capped only on one surface is not silently conflated with an account-wide model
cap.

**Surface-only entries (`scope.model` null, `scope.surface` set) are excluded** — rule 2
requires a model. This is deliberate: no surface-scoped entry has been observed, the app has
no vocabulary for surfaces, and inventing a label for an unverified shape risks presenting a
limit the user cannot interpret. Recorded under *Out of scope*.

`percent` is read with the same double-then-round handling as `utilization`; `resets_at` with
the same ISO parse. A malformed entry is skipped individually; its siblings still parse.

**Identity and deduplication.** Two entries can describe the same thing (same model, or the
same model twice with and without a surface). Identity is
`(ModelId ?? Label, Surface)`, compared case-insensitively. On a collision, **keep the entry
with the higher `Percent`** — the more constraining figure, so a dedup never makes usage look
lower than it is. Ties keep the first occurrence, making the result order-stable.

**Ordering.** Descending `Percent`, then `Label` ascending as a stable tiebreak. The most
constraining limit is the one worth seeing first, and it makes the row cap below meaningful.

#### `ReadCredits(JsonElement parent) → CreditUsage?`

`spend` is authoritative and is tried first; `extra_usage` is a degraded fallback, not an
equal. They are never merged — a payload carrying both disagreeing values yields `spend`'s
numbers, with no reconciliation attempted.

`ReadSpend` requires `used.amount_minor` **and** `limit.amount_minor` to parse, else returns
null so the fallback can run. Currency and exponent come from `used`; `Percent` from
`spend.percent`; `PayloadSeverity` from `spend.severity`; `CreditState` from `spend.enabled`,
`spend.disabled_reason`, and — since `spend` has no equivalent —
`extra_usage.spend_limit_reached` when present, else false.

`ReadExtraUsage` yields **`Used = null`, `Limit = null`**, `Percent` from `utilization`, and
`CreditState` from `is_enabled` / `disabled_reason` / `spend_limit_reached`. It deliberately
does not map `used_credits` and `monthly_limit` to money, for the unit reasons above. A
percent with no amount is honest; a possibly-wrong currency amount is not.

The fallback exists at all because the cache is written by whatever Claude Code version the
user runs, and an older one may emit only the legacy shape.

`can_purchase_credits` and `can_toggle` are **not** parsed. The tray is read-only and offers
no purchase or toggle action, so they would be unused fields (YAGNI).

**Targeted cleanup while in this file:** percent-and-reset parsing is now needed in two
shapes (`utilization` + `resets_at` for windows, `percent` + `resets_at` for limits). Factor
out `ReadRoundedPercent(element, propertyName)` and `ReadResetsAt(element)` so `ReadWindow`
and `ReadModelLimits` share them instead of duplicating the rounding and the
`AssumeUniversal | AdjustToUniversal` ISO parse.

#### Precedence between the flat fields and `limits`

The flat `five_hour` and `seven_day` fields remain the **sole** source for those two rows.
The `limits` entries that mirror them (`group == "session"`, and `weekly_all` via its null
`scope`) are excluded by the renderable rules above and never consulted, so a disagreement
between the two representations cannot produce two contradictory rows. This is a deliberate
precedence rule, not a side effect of the filter — stated here so a future change to those
filters does not silently start double-reporting.

### Formatting — `Core/CreditFormat.cs` (new)

```csharp
/// <summary>"40.01 / 40.00 EUR (100%)", or "100%" when only a percentage is known.</summary>
public static string Describe(CreditUsage c);
```

A pure function in Core, so it is unit-testable without WinForms — the reason the credit row
will not end up as untested as the existing popup rows.

Three deliberate calls:

- **ISO code, not currency symbol.** A code-to-symbol table is wrong for every code not in
  it, and `CultureInfo.CurrentCulture` describes the *user's* locale, not the *account's*
  currency — it would print `$` for a EUR account. The ISO code is unambiguous and never
  wrong.
- **Amounts are omitted entirely when `Used`/`Limit` are null**, leaving the percentage. This
  is the legacy-block path.
- **Credit state is rendered as its own text, not a suffix.** `Enabled == false` produces a
  distinct state line rather than appending ` · disabled` to an otherwise normal usage row:
  "disabled" and "limit reached" mean different things, and `DisabledReason` carries which.
  An earlier draft appended a suffix; that could not distinguish user-disabled from
  org-cap-reached, which the observed account shows are independent of `spend.enabled`.

### Rendering — `Tray/UsagePopup.cs`

Placement is the left-click popup, not the context menu. The popup already renders windows
as severity-colored bars with reset countdowns, which is what these numbers need; the
context menu stays a pure action menu. This departs from the issue's "menu-only" wording —
the issue predates treating the popup as the display surface.

```
5-hour window — 0%
7-day window — 90% · resets in 1h 20m
Fable weekly — 100% · resets in 1h 20m      ← one row per ModelLimit, highest percent first
Credits — 40.01 / 40.00 EUR (100%)          ← when Credits is not null
  limit reached                             ← only when CreditState says so
Last updated 2 min ago
```

- **Extract the bar primitive, not the whole row.** `AddWindowRow` currently owns both the
  caption and the custom-drawn bar. Split out `AddBar(layout, percent, severity)` and give
  windows, model limits, and credits their own caption logic. Model limits are *not* wrapped
  in a `WindowUsage` to reuse the row — that would discard `ModelId` and `IsActive` before
  rendering and make any later distinction between binding and non-binding caps a parsing
  change rather than a rendering one.
- **Severity source differs by row type, on purpose.** Windows and model limits use
  `SeverityRules.For(percent, settings.Thresholds.Orange, settings.Thresholds.Red)` — the
  thresholds are a deliberate user-facing setting, and mixing sources would put a
  user-green 7-day bar beside a server-red Fable bar in one popup. Credits prefer
  `PayloadSeverity` when present, falling back to `SeverityRules`, because credit severity
  can encode account state that a percentage cannot express.
- **`IsActive` is not currently rendered.** It is parsed and available, so distinguishing the
  binding cap later is a popup-only change. Nothing is drawn from it until its semantics are
  confirmed against more payloads.
- **Model rows are capped at 4**, sorted by descending percent, with a `"+N more"` grey text
  row when more exist. A row cap bounds the popup's height deterministically; the
  alternative — a scrollable panel plus a working-area height clamp — is more machinery for a
  case no observed payload reaches. Note `PositionNearCursor` clamps the popup's *position*
  to the working area but not its *size*, so an unbounded row count would clip off-screen.
- Empty `ModelLimits` and null `Credits` render nothing — no placeholder, no `0%`, no
  "no data" line. This satisfies the issue's "hidden on plans without a Fable window"
  criterion with no plan detection.
- Icon text and the display modes (`Show 5h` / `Show 7d` / `Show both`) are untouched.

### Diagnostics and privacy

Skipped malformed entries and a `spend`-absent fallback are **not** logged. `FetchLog`
records fetch outcomes and is written to `%APPDATA%\ClaudeUsageTray\fetch.log` for users to
share when reporting staleness; money amounts, currency, org spend caps, and account-specific
model labels must never enter it. If a parse-failure counter is ever wanted, it logs counts
and field names only — never values.

### Tests

`UsageCacheReaderTests` — fixtures cut from the observed payload shape:

- Full payload: the Fable limit and `spend` both parse.
- No `limits` and no `spend` keys: empty list, null credits, no crash.
- One malformed limit entry among valid ones: siblings survive.
- `weekly_all` with `scope: null`: excluded (no duplicate of the 7-day row).
- `session` kind: excluded by the `group == "weekly"` filter.
- `scope.model.display_name` missing but `id` present: parsed, labelled from `id`.
- Neither `display_name` nor `id`: skipped.
- `scope.model` null with `scope.surface` set: excluded.
- Model plus surface: labelled `"Model (surface)"`.
- Two entries for the same model at different percents: deduped to the higher one.
- Ordering: three limits returned highest-percent first.
- `is_active: false` on a scoped entry: parsed as false and **still present** in the list.
- `spend` and `extra_usage` both present and disagreeing: `spend` wins.
- `extra_usage` only: `Used`/`Limit` are null, `Percent` populated.
- `decimal_places: 0` in an `extra_usage`-only payload: no money rendered, so no
  ambiguous-unit output.

`UsageApiClientTests` — the same fixtures at root nesting, proving the shared readers work
on both paths.

`CreditFormatTests` (new) — `Describe` with money at exponent 2 and exponent 0, percent-only
(null amounts), over-limit (percent above 100), `Enabled: false` with a `DisabledReason`, and
`LimitReached: true`.

Popup row-capping and ordering are covered at the `ModelLimits` level (parsing) rather than
through WinForms; the cap constant and the "+N more" text are asserted against a helper that
takes the list and returns the rows to draw, keeping the Form itself free of testable logic.

Existing 5h/7d tests stay green unchanged; that is the no-regression check.

## Acceptance criteria

- [ ] Model-scoped weekly limits and credit usage parse from both the API and the
      `.claude.json` cache, degrading to empty/null when absent.
- [ ] The popup shows one bar per model limit (capped, highest first) and a credit row, each
      hidden when its data is unavailable.
- [ ] A model limit's label comes from the payload, so a renamed model needs no code change.
- [ ] No model row is ever rendered as `0%` or `—` when the limit does not apply.
- [ ] No model row duplicates the 5-hour or 7-day window.
- [ ] Credits render with the account's own currency code and exponent, not an assumed `$`,
      and render percent-only when the amount's units are unverified.
- [ ] Credit state (disabled, limit reached) is distinguishable from ordinary usage.
- [ ] The popup cannot grow past the row cap regardless of how many limits the payload has.
- [ ] Tests cover: field present, field absent, malformed entry, missing label, surface-only
      scope, duplicate models, ordering, `is_active: false` retained, legacy-only credit
      shape, `spend`/`extra_usage` disagreement, and credit formatting.
- [ ] No regression to the existing 5h/7d rows, icon text, or display modes.

## Out of scope

- A display mode that puts a model window or credits in the icon text.
- Surface-scoped limits with no model (`scope.model` null). Revisit if one is ever observed.
- Rendering `is_active` as a visual distinction, pending confirmation of its semantics.
- Mapping `extra_usage` amounts to money, pending confirmation of its units.
- Replacing the flat `five_hour` / `seven_day` reads with `limits`-derived values. Coherent
  long-term, but it rewrites working parse paths and risks the regression the issue forbids.
- `spend.balance`, `spend.cap`, `auto_reload`, `can_purchase_credits`, `can_toggle`, and the
  `overageCreditGrantCache` block.
- Telemetry or a diagnostics counter for skipped entries and credit-source disagreement.

## Review departures

An adversarial review raised nine findings. Seven are applied above. Two are recorded as
deliberate departures:

1. **"Filter model rows to `is_active == true`."** Not applied. The review's own cited
   evidence refutes it: `weekly_all` is `is_active: false` at 90% and is a real, displayed
   limit, so filtering on the flag would hide genuine usage. The field is parsed and retained
   instead, and the reasoning is now written down rather than assumed.
2. **"Prefer the payload's `severity` over local thresholds."** Applied for credits only, not
   for windows or model limits. The orange/red thresholds are a user-configurable setting;
   letting server severity win for some bars and user thresholds for others would make one
   popup internally inconsistent.

A third is narrowed: the review asked for `CanPurchase` and `CanToggle` in `CreditState`.
The tray is read-only and exposes no such action, so they are omitted as YAGNI.
