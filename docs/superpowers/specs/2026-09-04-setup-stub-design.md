# Setup stub — design

Issue: [#21](https://github.com/wus-technik/win_systray-claude-usage/issues/21)

## Problem

The only entry point is `WusTechnik.ClaudeUsageTray-win-Setup.exe` on the latest release. Two things
are wrong with it:

- **The link is tied to a release.** Anything that pins a tag rots; `/releases/latest/download/…`
  survives, but only for stable.
- **The beta ring has no entry point at all.** Betas are uploaded with `--pre`, so they are GitHub
  pre-releases and `/releases/latest` skips them by definition. Today the only way onto the beta ring
  is to install stable and then tick a checkbox.

Wanted: one permanent URL, a wizard that offers stable or beta, and flags that make it usable for
unattended deployment.

## Decision

A separate `ClaudeUsageTraySetup.exe` — a **thin launcher**. It resolves the newest release for the
chosen ring, downloads that release's channel `Setup.exe`, and runs it. Velopack keeps owning every
part of installing: directory layout, shortcuts, uninstall registration, the `current/` swap.

The rejected alternative was a self-installing stub that pulls `releases.{channel}.json` plus the
`.nupkg` and installs via the Velopack library. It saves one download, but it duplicates install
logic that must then track every Velopack version bump — and the stub is the one component that
cannot auto-update itself, so it is the worst possible place to put logic that ages.

A static download page (GitHub Pages, two buttons) was also considered and rejected as a
*replacement*. It solves the permanent-URL and ring-choice half at a fraction of the cost, but it
cannot satisfy `--silent` unattended deployment, which is a first-class requirement here. It remains
a reasonable thing to add later *alongside* the stub; it is out of scope for this design.

## What the stub is not allowed to decide

The stub must not compare versions or decide a downgrade. `Core/UpdateRing.For` already owns that,
and its asymmetry is load-bearing: `AllowVersionDowngrade` is true **only** while a return to stable
is pending, and it is the only thing that gets a user down from a newer beta to the latest stable.
A stub that applied packages itself would have to restate that rule, which is precisely the split
`UpdateRing`'s own doc-comment warns about.

So the stub does exactly two things to an existing install: it reads `current/sq.version`, and it
writes `useBetaReleases` into `settings.json`.

`Core/UpdateRing.cs` is linked into the stub as **shared source** (`<Compile Include=… Link=…/>`) —
it is pure and dependency-free. The stub uses `StableChannel`, `BetaChannel` and `IsBetaChannel`,
because every asset name derives from the channel string. It never calls `For`.

## What a ring switch actually does — and does not do

Verified against the code, because the first draft of this design got it wrong:

`UpdateCheck.RunPeriodicAsync` is "check on launch and every 6 h; **download only**. Never terminate
the tray process" (`UpdateCheck.cs:91`), and applying is `RestartToApply`, "**explicit user action
only**" (`UpdateCheck.cs:151`).

So writing `useBetaReleases` and restarting the app **stages** a cross-ring package. It does not move
the user to it. The move completes when the user chooses **Restart to update** — the same path every
ordinary update takes. The stub must say so rather than imply the switch is done:

> Beta releases enabled. Claude Usage Tray will download the next beta build in the background and
> offer **Restart to update** when it is ready.

Getting this wrong in the UI would be worse than not shipping the feature: a user told "you are now
on beta" who is still running the stable build has no reason to look for the restart prompt.

## Release resolution

The two rings are deliberately asymmetric, because stable needs no API call and beta cannot avoid one.

| Ring | How the newest release is found |
|---|---|
| stable | `…/releases/latest/download/WusTechnik.ClaudeUsageTray-win-Setup.exe`. GitHub's redirect *is* the version-independence. No API call, so no rate limit to hit. |
| beta | `GET /repos/wus-technik/win_systray-claude-usage/releases`, keep releases carrying `-win-beta-Setup.exe`, take the highest SemVer **by tag**. |

Beta is ordered by parsed SemVer, **not** by publish date: an out-of-order hotfix would otherwise
win. Prerelease identifiers compare numerically, so `beta.10 > beta.9` — the same rule
`build/release-ring.ps1` enforces when it rejects `-beta1`.

**Tags that do not parse as SemVer are skipped, not fatal.** The `setup-stub` tag (below) is itself
such a release, so this is load-bearing rather than defensive.

**Paging.** Follow the API's `Link: rel="next"` until exhausted or a hard cap of 5 pages
(500 releases), whichever comes first, and honour `x-ratelimit-remaining`. A fixed one-page window
would just replace Velopack's 10-release limit with a different arbitrary one.

**Two distinct failure kinds, which must not be collapsed:**

- *API unavailable* (network, 5xx, rate-limited at 60 req/h unauthenticated) → the fallback below.
- *API fine, no release carries the asset* → a hard error. There is nothing to fall back to, and
  pretending otherwise would install the wrong ring.

**Beta fallback, and its honest limits.** On *API unavailable*, fall back to
`…/releases/latest/download/WusTechnik.ClaudeUsageTray-win-beta-Setup.exe`. That asset exists on
every stable release because the `win-beta` mirror is mandatory, so the beta path degrades to "latest
stable, on the beta channel" rather than failing outright. This is a **third** reason the mirror is
load-bearing, alongside the two in the beta-ring design; anyone tempted to drop it now breaks the
beta installer too.

But that build is only *channel* beta — its content is stable. If a newer prerelease exists, the user
asked for beta and silently got stable. So:

- **Interactive:** the wizard names the resolved version before downloading, and says plainly when it
  is the stable mirror rather than a prerelease.
- **Silent:** `--ring beta` with the API unavailable **fails closed** rather than installing content
  from the other ring behind an operator's back.

Asset names are derived, never hardcoded per ring: `WusTechnik.ClaudeUsageTray-{channel}-Setup.exe`.

## Integrity and trust

Stated rather than assumed, since the stub downloads and executes another executable:

**Nothing this project ships is code-signed today** — there is no signing step in `release.yml` or
`build/`. So "verify the Authenticode signature" is not implementable as things stand, and a design
that claimed it would be fiction. The actual trust anchor is TLS to `github.com` plus the repository's
own access control.

What the stub *can* do, and must:

- **Verify the asset digest** when the release came from the API, which now reports a `digest`
  (`sha256:…`) per asset. Mismatch aborts before execution.
- The stable path uses the `/releases/latest/download` redirect and so has no digest to check. It may
  optionally confirm via one API call; a failed *confirmation* must not block install, or stable would
  inherit beta's rate-limit exposure for no gain.
- Refuse to execute a zero-length or non-PE download.

Code signing for both the stub and `Setup.exe` is the real fix for SmartScreen and for tamper
evidence, and adding a second unsigned launcher makes the reputation problem slightly worse. It is a
purchasing decision outside this design; recorded here as the known gap, and revisit
`docs/superpowers/specs/` when a certificate exists.

## CLI surface

```
ClaudeUsageTraySetup.exe [--ring stable|beta] [--silent] [--installto DIR] [--help]
```

- `--ring` picks the ring without asking, and is the **only** way to change the ring of an existing
  install.
- `--silent` suppresses the stub's own wizard and is passed through to `Setup.exe`.
- `--installto DIR` is passed through unchanged.

Verified against the shipped Setup 1.2.0 binary rather than the docs: it accepts `--silent` ("Hides
all dialogs and answers 'yes' to all prompts"), `--installto DIR`, `--debug`, and trailing
`-- EXE_ARGS`.

### Exit codes

`Setup.exe`'s own exit code is **propagated verbatim** when it ran and failed, so nothing about the
installer's reporting is lost. The stub's own failures therefore need a range that cannot be confused
with a child code:

| Code | Meaning |
|---|---|
| `0` | Success, or the requested state already held |
| *child* | `Setup.exe` ran and returned non-zero — propagated unchanged |
| `3001` | Bad arguments |
| `3002` | Ring resolution failed (API unavailable with no usable fallback, or no release carries the asset) |
| `3003` | Download or digest verification failed |
| `3004` | No-op: `--silent` with no `--ring` against an existing install (see below) |
| `3005` | Could not stop or relaunch the running app, or the settings write did not persist |

`3004` exists because "did nothing" and "converged successfully" must not both be `0` — a deployment
script has to be able to tell them apart.

### A ring change always requires an explicit `--ring`

`--silent` with no `--ring` on a machine that already has a **beta** install must not write
`useBetaReleases: false` and drag that user down to stable. A default that silently reverses a
deliberate opt-in is the one failure mode worth designing against here, and unattended runs are
exactly where nobody would notice it happening.

Rule: `--silent` **and** no `--ring` **and** an existing install ⇒ report version and channel, change
nothing, exit `3004`.

The rule is scoped to silent runs. An interactive run against an existing install may of course
change the ring without `--ring` — the wizard's radio buttons *are* the explicit choice, made by a
person looking at the current ring on screen. What is being prevented is an unattended default
silently reversing a deliberate opt-in.

On a machine with no install, no `--ring` in silent mode means stable — matching the documented
default for a normal install.

## Flows

### Fresh install, interactive

Page 1: radio buttons, *Stable (recommended)* / *Beta (pre-release builds)*. Page 2: the resolved
version, then download progress. Then hand off to `Setup.exe` and exit.

No settings file is written on a fresh install, and that is on purpose. An install from
`-win-beta-Setup.exe` records `<channel>win-beta</channel>` in its manifest, and `Program.cs:50`
resolves an unset `UseBetaReleases` from that channel — so the ring is already correct, and the
Settings checkbox already shows it ticked. Writing the key as well would only add a second source of
truth, and an explicit `false` behaves differently from an absent one, so writing it gratuitously
narrows what the app can later infer.

### Already installed

`%LOCALAPPDATA%\WusTechnik.ClaudeUsageTray\current\sq.version` is plain XML carrying `<version>` and
`<channel>`, so version and ring are both readable with no Velopack dependency.

Determining the *current* ring needs both sources, in this order: an explicit `useBetaReleases` in
settings wins; otherwise the manifest channel decides. A `null` in the file must not be read as
"stable" — `Program.cs:50` deliberately leaves it unpersisted ("Not written to disk until something
else saves"), so `useBetaReleases: null` is a normal on-disk state for a beta install.

If the requested ring differs, the stub changes it in this order, and the order matters:

1. **Stop `ClaudeUsageTray.exe`.** Ask it to close, wait 5 s, then terminate; if it still runs, abort
   with `3005` rather than write into a race. A running app whose Settings dialog is saving calls
   `PersistSettings` (`TrayApp.cs:487`), which writes the *whole* file back and would clobber the
   stub's change. Writing first and stopping second is a lost-update race. Nothing saves settings on
   exit, so stopping first is safe.
2. **Write `useBetaReleases`**, then read the file back and confirm the value persisted.
3. **Relaunch** `%LOCALAPPDATA%\WusTechnik.ClaudeUsageTray\current\ClaudeUsageTray.exe`. Required,
   not cosmetic: settings are read once at `Program.cs:31`, and `Program.cs:24` holds a single-instance
   lock, so the old process must genuinely be gone first.

Then tell the user what will actually happen — staging, not a completed move (see above).

If the app was not running, the stub writes the setting and leaves it stopped; starting an app the
user had closed is not the stub's call. Under `--silent` the same steps run with no prompt.

Choosing the ring the machine is already on is a no-op: report and exit `0`.

### Editing settings.json

The file is `%APPDATA%\ClaudeUsageTray\settings.json` (`Settings.DefaultPath`), key `useBetaReleases`,
camelCase, JSON `true`/`false`.

**The stub must edit it as a `JsonNode` DOM, not by deserialising into `Settings`.** `Settings.Save`
serialises the whole object and the type has no unknown-key preservation, so a round-trip through it
would silently drop any key this stub's copy predates — and the stub is the one component that cannot
auto-update, so it will routinely be older than the app. It would also run `NormalizeFields()`,
rewriting values it was never asked to touch. Editing the DOM also avoids needing an AOT-safe
serialiser contract for the whole `Settings` shape.

Write via temp file plus atomic replace, matching `Settings.Save`. A missing file is created with just
that one key; a malformed file is **not** silently replaced — abort with `3005` and say so, because
overwriting it would destroy the user's other settings.

### Not covered

- **Portable-zip users** have no `sq.version`, so they read as "not installed" and the stub will
  install the per-user app alongside the portable copy, the two then sharing `%APPDATA%` settings.
  Accepted; the stub warns when it sees a running `ClaudeUsageTray.exe` outside the install tree.
- **A malformed or absent `sq.version`** is treated as "not installed", following the app's own
  degrade-don't-die rule for read paths.

## Build and publish

NativeAOT, `win-x64`, with an application manifest declaring comctl32 v6 (needed for
`TaskDialogIndirect`). No WinForms: the wizard is Win32 task dialogs via P/Invoke, since radio
buttons and a progress bar are native task-dialog features. Expected size ~2 MB against `Setup.exe`'s
58 MB.

`win-x64` only, matching the app itself; on ARM64 it runs under x64 emulation and installs the x64
app, which is the same thing a user gets today. Wizard strings are English-only, like the app.

Two AOT consequences to honour in the implementation:

- GitHub API JSON needs a source-generated `JsonSerializerContext`; reflection-based
  `System.Text.Json` is not AOT-safe.
- **Publishing requires the MSVC linker, so the stub is built in CI only.** `dotnet build` does not
  need it — ILC runs at publish — so the test project can reference the stub project and run locally
  as usual. This was the accepted trade for the size.

Network behaviour: default system proxy (so corporate proxies and TLS inspection work), 30 s
connect / 10 min total timeout, 3 retries with exponential backoff on transient failures, download to
a `%TEMP%\ClaudeUsageTraySetup-<guid>\` directory deleted on success *and* on failure. No resume —
a 58 MB retry is cheaper than the state to track it.

### The canonical URL, and the trap in it

`.github/workflows/setup-stub.yml` (`workflow_dispatch` + push on `src/ClaudeUsageTraySetupStub/**`)
publishes to a permanent `setup-stub` tag via `gh release upload --clobber`:

```
https://github.com/wus-technik/win_systray-claude-usage/releases/download/setup-stub/ClaudeUsageTraySetup.exe
```

**That release must never become GitHub's `/releases/latest`.** GitHub picks the newest non-draft,
non-prerelease release as `latest`, so a plain `setup-stub` release would take the title — breaking
the README's install link *and* the stub's own stable resolution, which is built on
`/releases/latest/download/…`. The stub would break the exact thing it exists to fix, and only when
the stub is republished, which is the worst possible time to find out.

Therefore: create it with `--latest=false` (`make_latest: false`), and have `release.yml` assert that
`/releases/latest` resolves to a `v*` tag. The beta resolver's skip-non-SemVer-tags rule is the second
guard.

`release.yml` **copies that asset** onto each release rather than rebuilding it: identical bytes
everywhere, no AOT cost per release, and the stub is rebuilt only when its own source changes.

**Staleness guard.** Because the stub links `Core/UpdateRing.cs` as shared source, a change there does
not rebuild the stub. So the stub is stamped with a hash of its inputs (its own sources plus every
linked file), and `release.yml` recomputes that hash and fails the release if the canonical asset was
built from different inputs. Silent drift in shared logic is otherwise invisible until a user hits it.

## Testing

Everything decidable is a pure function in the stub's own `Core`, exercised from
`tests/ClaudeUsageTray.Tests` with canned payloads and no network — the rule `UsageApiClientTests`
already follows:

- argument parsing, including unknown flags and `--ring` with a bad value
- SemVer prerelease ordering (`beta.10 > beta.9`, stable > any prerelease of the same version)
- release selection from a canned releases payload: out-of-order publish dates, releases missing the
  channel asset, non-SemVer tags such as `setup-stub`, an empty list, and paging
- the two failure kinds staying distinct: *API unavailable* → fallback URL, *no asset anywhere* →
  `3002`
- asset-name derivation per channel
- `sq.version` parsing: valid, absent, malformed XML, missing `<channel>`
- current-ring resolution from the settings/manifest pair, including `null` settings on a `win-beta`
  manifest reading as beta
- the settings DOM edit preserving unrelated and unknown keys, and refusing a malformed file
- the safety rule: silent + no `--ring` + existing install ⇒ no write, exit `3004`

The wizard itself is not unit-tested; it holds no decisions, only presentation.
