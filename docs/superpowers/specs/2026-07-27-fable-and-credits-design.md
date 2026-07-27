# Scoped usage limits and credit usage in the popup

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

This is a reading, not a documented fact — and it is under-determined. The observed payload
proves only that `is_active: false` does **not** mean "not a real limit". It does not prove
the flag means "currently binding". Payloads that would falsify that reading: two scoped
entries both `is_active: true`; a scoped entry at 100% with `is_active: false` that still
throttles; a low-percent active entry beside a higher-percent inactive one.

The design therefore **parses and retains `is_active`, never filters on it, and uses it only
where a wrong reading is cheap**: it sorts active rows first and exempts them from the row
cap (see *Rendering*). Filtering to `is_active == true` would have hidden the 90% weekly
window — the concrete failure this observation rules out. Drawing a "binding cap" badge would
assert the unproven half of the reading. Sorting and cap-exemption are the two uses where
being wrong costs only a suboptimal row order or a taller popup.

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

/// <summary>One scoped weekly limit from limits[] — scoped to a model (e.g. Fable), a
/// surface, or both. Label is payload-derived and is also the dedup key.
/// IsActive is retained but never filtered on — see "What is_active appears to mean".</summary>
public sealed record ScopedLimit(
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
    IReadOnlyList<ScopedLimit>? ScopedLimits = null,
    CreditUsage? Credits = null)
{
    /// <summary>Empty means absent. Never null to consumers, whatever the caller passed.</summary>
    public IReadOnlyList<ScopedLimit> ScopedLimits { get; init; } = ScopedLimits ?? [];
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
constructor parameter with a distinct `ScopedLimits` property — rather than reverting to a
nullable public surface. Verify this at implementation time; it has not been compiled.

`Core/SnapshotPrecedence.cs` needs no change: it swaps whole snapshots by `FetchedAt`, so
new members ride along.

### Parsing — `Core/UsageJson.cs`

Both new readers take the container element, so the API path (fields at the response root)
and the cache path (fields under `utilization`) share one implementation, exactly as
`ReadWindow` does today.

#### `ReadScopedLimits(JsonElement parent) → IReadOnlyList<ScopedLimit>`

Returns empty when `limits` is missing or is not an array. An entry is **renderable** when
both of these hold:

1. `group == "weekly"`. Excludes `session`.
2. `scope` is a non-null object **and a label can be derived from it**, by the first of these
   that yields a non-empty string:
   - `scope.model.display_name` → label `"{model}"`
   - `scope.model.id` → label `"{id}"`
   - `scope.surface` → label `"{surface}"` with underscores replaced by spaces
   When both a model and a surface are present, the label is `"{model} ({surface})"`, so a
   model capped on one surface is not conflated with an account-wide model cap.

Requiring a *label* rather than a *model* is what excludes `weekly_all` (whose `scope` is
`null`, yielding nothing), so the generic 7-day window is never duplicated as a scoped row —
while still admitting **surface-only entries** (`scope.model` null, `scope.surface` set).

An earlier draft excluded surface-only entries on the grounds that the app has no vocabulary
for surfaces. That was wrong in the dangerous direction: combined with the row cap below, it
gave two independent ways to hide a limit that is actively throttling the user. A row reading
`claude code weekly — 100%` is a degraded label but a truthful one; a hidden 100% cap is
neither. No surface-only entry has been observed, so including them costs nothing today and
cannot hide a throttle tomorrow.

`percent` is read with the same double-then-round handling as `utilization`; `resets_at` with
the same ISO parse. A malformed entry is skipped individually; its siblings still parse.

**Identity and deduplication.** Identity is the **normalized label** — trimmed,
case-insensitive — and nothing else. Because the label already embeds the surface when one is
present, this is equivalent to keying on (model, surface) without needing to carry `Surface`
as a field.

Keying on `ModelId ?? Label` would be broken: an entry with
`{ id: "claude-fable", display_name: "Fable" }` and one with `{ id: null, display_name: "Fable" }`
would produce the keys `claude-fable` and `Fable`, so both rows survive — burning one of the
capped slots on a duplicate and possibly pushing a real limit out of view. Since `display_name`
is the field observed to be reliably populated (and `id` the one observed null), the label is
the sounder key.

On a collision, **keep the entry with the higher `Percent`** — the more constraining figure,
so dedup never makes usage look lower than it is; carry over the non-null `ModelId` from
either entry. Ties keep the first occurrence, making the result order-stable.

Residual limitation, accepted: an entry labelled from `display_name` ("Fable") and another
labelled from `id` ("claude-fable") for the same model will not dedup. Closing that needs an
id↔name map the payload does not provide.

**Ordering.** `IsActive` descending **first**, then `Percent` descending, then `Label`
ascending as a stable tiebreak. Active-first matters because of the row cap: sorting on
percent alone could bury an active 70% cap behind four inactive rows at 80–100%, hiding the
one limit closest to stopping the user — the exact failure this ordering exists to prevent.

#### `ReadCredits(JsonElement parent) → CreditUsage?`

`spend` is authoritative and is tried first; `extra_usage` is a degraded fallback, not an
equal. They are never merged — a payload carrying both disagreeing values yields `spend`'s
numbers, with no reconciliation attempted.

`ReadSpend` requires `used.amount_minor` **and** `limit.amount_minor` to parse, else returns
null so the fallback can run. Currency and exponent come from `used`; `Percent` from
`spend.percent`; `PayloadSeverity` from `spend.severity`; `Enabled` and `DisabledReason` from
`spend.enabled` and `spend.disabled_reason`.

`LimitReached` is derived **from `spend` itself** as `used.amount_minor >= limit.amount_minor`.
An earlier draft imported `extra_usage.spend_limit_reached` here, which contradicted this
section's own "never merged" rule and was wrong on the observed payload: `spend.used` is 4001
against a `spend.limit` of 4000 at `percent: 100`, while `extra_usage.spend_limit_reached` is
`false`. That would have rendered `40.01 / 40.00 EUR (100%)` with no "limit reached" line —
the authoritative block saying over-limit and the legacy flag overruling it.

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
and `ReadScopedLimits` share them instead of duplicating the rounding and the
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
Fable weekly — 100% · resets in 1h 20m      ← one row per ScopedLimit, active first
Credits — 40.01 / 40.00 EUR (100%)          ← when Credits is not null
  limit reached                             ← only when CreditState says so
Last updated 2 min ago
```

- **Extract the bar primitive, not the whole row.** `AddWindowRow` currently owns both the
  caption and the custom-drawn bar. Split out `AddBar(layout, percent, severity)` and give
  windows, scoped limits, and credits their own caption logic. Scoped limits are *not* wrapped
  in a `WindowUsage` to reuse the row — that would discard `ModelId` and `IsActive` before
  rendering and make any later distinction between binding and non-binding caps a parsing
  change rather than a rendering one.
- **Severity source differs by row type, on purpose.** Windows and scoped limits use
  `SeverityRules.For(percent, settings.Thresholds.Orange, settings.Thresholds.Red)` — the
  thresholds are a deliberate user-facing setting, and mixing sources would put a
  user-green 7-day bar beside a server-red Fable bar in one popup. Credits prefer
  `PayloadSeverity` when present, falling back to `SeverityRules`, because credit severity
  can encode account state that a percentage cannot express.
- **`IsActive` affects ordering and cap exemption, but draws nothing.** No badge, colour, or
  caption derives from it, because its semantics are unconfirmed. It is used only where being
  wrong is harmless: an unhelpful sort order and a slightly taller popup are recoverable,
  whereas a mislabelled "binding" badge would assert something the payload has not
  established.
- **Scoped rows are capped at 4**, in the parser's active-first / percent-descending order,
  with a `"+N more"` grey text row when more exist. A row cap bounds the popup's height
  deterministically; the alternative — a scrollable panel plus a working-area height clamp —
  is more machinery for a case no observed payload reaches. Note `PositionNearCursor` clamps
  the popup's *position* to the working area but not its *size*, so an unbounded row count
  would clip off-screen.
- **`IsActive` rows are exempt from the cap.** If more than four limits are active, all of
  them render. The cap exists to stop an unbounded list of *background* limits from growing
  the popup off-screen; it must never be the reason the cap that is actually throttling the
  user is invisible. `"+N more"` then counts only the hidden inactive rows. This bounds height
  in every payload observed or plausible, while making the pathological case (many
  simultaneously-active caps) fail toward showing too much rather than too little.
- Empty `ScopedLimits` and null `Credits` render nothing — no placeholder, no `0%`, no
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
- `scope.model` null with `scope.surface: "claude_code"`: included, labelled
  `"claude code"`.
- Model plus surface: labelled `"Model (surface)"`.
- Two entries for the same model at different percents: deduped to the higher one, and the
  non-null `ModelId` survives from whichever entry carried it.
- Two entries whose labels differ only by case or surrounding whitespace: deduped.
- One entry with an `id` and one without, same `display_name`: deduped (the regression the
  `ModelId ?? Label` key would have caused).
- Ordering: an active entry at 70% sorts ahead of an inactive entry at 100%.
- `is_active: false` on a scoped entry: parsed as false and **still present** in the list.
- `spend` and `extra_usage` both present and disagreeing: `spend` wins, including
  `LimitReached` — a payload with `spend.used > spend.limit` and
  `extra_usage.spend_limit_reached: false` must yield `LimitReached: true`.
- `extra_usage` only: `Used`/`Limit` are null, `Percent` populated.
- `decimal_places: 0` in an `extra_usage`-only payload: no money rendered, so no
  ambiguous-unit output.

`UsageApiClientTests` — the same fixtures at root nesting, proving the shared readers work
on both paths.

`CreditFormatTests` (new) — `Describe` with money at exponent 2 and exponent 0, percent-only
(null amounts), over-limit (percent above 100), `Enabled: false` with a `DisabledReason`, and
`LimitReached: true`.

Popup row-capping and ordering are covered at the `ScopedLimits` level (parsing) rather than
through WinForms; the cap constant and the "+N more" text are asserted against a helper that
takes the list and returns the rows to draw, keeping the Form itself free of testable logic.
That helper's cases: six inactive limits → four rows plus "+2 more"; six limits of which five
are active → all five active rows render and "+1 more" counts only the inactive one; exactly
four limits → no "+N more" row at all.

Existing 5h/7d tests stay green unchanged; that is the no-regression check.

## Acceptance criteria

- [ ] Model-scoped weekly limits and credit usage parse from both the API and the
      `.claude.json` cache, degrading to empty/null when absent.
- [ ] The popup shows one bar per scoped limit (capped, highest first) and a credit row, each
      hidden when its data is unavailable.
- [ ] A scoped limit's label comes from the payload, so a renamed model needs no code change.
- [ ] No scoped row is ever rendered as `0%` or `—` when the limit does not apply.
- [ ] No scoped row duplicates the 5-hour or 7-day window.
- [ ] Credits render with the account's own currency code and exponent, not an assumed `$`,
      and render percent-only when the amount's units are unverified.
- [ ] Credit state (disabled, limit reached) is distinguishable from ordinary usage, and
      `LimitReached` is derived from `spend` alone — never from a legacy flag that can
      contradict it.
- [ ] No limit is hidden by the row cap while `is_active` is true.
- [ ] No limit is hidden for lacking a model scope; a surface-only cap still renders.
- [ ] Inactive rows beyond the cap are bounded, so the popup cannot grow off-screen on any
      observed or plausible payload.
- [ ] Tests cover: field present, field absent, malformed entry, missing label, surface-only
      scope, duplicate models (including the id/no-id pair), ordering with an active
      low-percent entry, `is_active: false` retained, legacy-only credit shape,
      `spend`/`extra_usage` disagreement including `LimitReached`, and credit formatting.
- [ ] No regression to the existing 5h/7d rows, icon text, or display modes.

## Out of scope

- A display mode that puts a model window or credits in the icon text.
- Rendering `is_active` as a visual distinction, pending confirmation of its semantics. It
  still drives ordering and cap exemption.
- Parsing `severity` on scoped limits. It would be an unused field: scoped-limit bars colour from the
  user's thresholds, so nothing would read it (YAGNI). Revisit if evidence appears that model
  severity encodes state a percentage cannot express — the case already granted for credits.
- Deduplicating two entries for one model when one is labelled from `display_name` and the
  other from `id`; the payload provides no id↔name map.
- Mapping `extra_usage` amounts to money, pending confirmation of its units.
- Replacing the flat `five_hour` / `seven_day` reads with `limits`-derived values. Coherent
  long-term, but it rewrites working parse paths and risks the regression the issue forbids.
- `spend.balance`, `spend.cap`, `auto_reload`, `can_purchase_credits`, `can_toggle`, and the
  `overageCreditGrantCache` block.
- Telemetry or a diagnostics counter for skipped entries and credit-source disagreement.

## Review departures

### Round 2

A second adversarial pass on the revision raised four new findings; all four are applied:

- **Dedup key was broken.** `ModelId ?? Label` mixed two key spaces, so `{id: "claude-fable",
  display_name: "Fable"}` and `{id: null, display_name: "Fable"}` produced different keys and
  both survived. Now keyed on the normalized label alone.
- **`LimitReached` read a legacy field on the authoritative path**, contradicting this spec's
  own "never merged" rule — and wrong on the observed payload, where `spend` is over limit
  while `extra_usage.spend_limit_reached` is `false`. Now derived from `spend`.
- **Percent-only ordering plus a hard cap could bury an active limit.** Now active-first, and
  active rows are exempt from the cap.
- **Excluding surface-only entries was the second way to hide a throttling cap.** Now
  included with a degraded label.

The round-2 suggestion to parse `severity` on scoped limits is declined as YAGNI — nothing
would read it while scoped-limit bars colour from the user's thresholds. It is recorded under *Out of
scope* with the condition that would reopen it.

### Round 1

The first adversarial review raised nine findings. Seven were applied. Two are recorded as
deliberate departures:

1. **"Filter scoped rows to `is_active == true`."** Not applied. The review's own cited
   evidence refutes it: `weekly_all` is `is_active: false` at 90% and is a real, displayed
   limit, so filtering on the flag would hide genuine usage. The field is parsed and retained
   instead, and the reasoning is now written down rather than assumed.
2. **"Prefer the payload's `severity` over local thresholds."** Applied for credits only, not
   for windows or scoped limits. The orange/red thresholds are a user-configurable setting;
   letting server severity win for some bars and user thresholds for others would make one
   popup internally inconsistent.

A third is narrowed: the review asked for `CanPurchase` and `CanToggle` in `CreditState`.
The tray is read-only and exposes no such action, so they are omitted as YAGNI.
