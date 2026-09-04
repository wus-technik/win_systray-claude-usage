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

## What the stub is not allowed to decide

The stub must not compare versions or decide a downgrade. `Core/UpdateRing.For` already owns that,
and its asymmetry is load-bearing: `AllowVersionDowngrade` is true **only** while a return to stable
is pending, and it is the only thing that gets a user down from a newer beta to the latest stable.
A stub that applied packages itself would have to restate that rule, which is precisely the split
`UpdateRing`'s own doc-comment warns about.

So the stub does exactly two things to an existing install: it reads `current/sq.version`, and it
writes `useBetaReleases` into `settings.json`. The move itself happens on the app's next check.

`Core/UpdateRing.cs` is linked into the stub as **shared source** (`<Compile Include=… Link=…/>`) —
it is pure and dependency-free. The stub uses `StableChannel`, `BetaChannel` and `IsBetaChannel`,
because every asset name derives from the channel string. It never calls `For`.

## Release resolution

The two rings are deliberately asymmetric, because stable needs no API call and beta cannot avoid one.

| Ring | How the newest release is found |
|---|---|
| stable | `…/releases/latest/download/WusTechnik.ClaudeUsageTray-win-Setup.exe`. GitHub's redirect *is* the version-independence. No API call, so no rate limit to hit. |
| beta | `GET /repos/wus-technik/win_systray-claude-usage/releases?per_page=30`, keep releases carrying `-win-beta-Setup.exe`, take the highest SemVer **by tag**. |

Beta is ordered by parsed SemVer, **not** by publish date: an out-of-order hotfix would otherwise
win. Prerelease identifiers compare numerically, so `beta.10 > beta.9` — the same rule
`build/release-ring.ps1` enforces when it rejects `-beta1`.

Unlike Velopack's `GithubSource`, the stub queries the releases API directly and so is not bound by
that 10-release lookback window. `per_page=30` is a generous ceiling on how far back a still-current
beta could sit, not a constraint inherited from Velopack.

**Beta fallback.** If the API fails or rate-limits (60 requests/h unauthenticated, per IP), fall back
to `…/releases/latest/download/WusTechnik.ClaudeUsageTray-win-beta-Setup.exe`. That asset exists on
every stable release because the `win-beta` mirror is mandatory, so the beta path degrades to "latest
stable" instead of failing outright. This is a **third** reason the mirror is load-bearing, alongside
the two in the beta-ring design; anyone tempted to drop it now breaks the beta installer too.

Asset names are derived, never hardcoded per ring: `WusTechnik.ClaudeUsageTray-{channel}-Setup.exe`.

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

Distinct exit codes, so a deployment script can tell the cases apart: `0` success or nothing to do,
`2` bad arguments, `3` resolution/download failure, `4` `Setup.exe` returned non-zero.

### A ring change always requires an explicit `--ring`

`--silent` with no `--ring` on a machine that already has a **beta** install must not write
`useBetaReleases: false` and drag that user down to stable. A default that silently reverses a
deliberate opt-in is the one failure mode worth designing against here, and unattended runs are
exactly where nobody would notice it happening.

Rule: `--silent` **and** no `--ring` **and** an existing install ⇒ report version and channel, change
nothing, exit `0`. The cost is that `--silent` alone will not repair or set a ring; that is accepted.

The rule is scoped to silent runs. An interactive run against an existing install may of course
change the ring without `--ring` — the wizard's radio buttons *are* the explicit choice, made by a
person looking at the current ring on screen. What is being prevented is an unattended default
silently reversing a deliberate opt-in.

On a machine with no install, no `--ring` in silent mode means stable — matching the documented
default for a normal install.

## Flows

**Fresh install, interactive.** Page 1: radio buttons, *Stable (recommended)* / *Beta (pre-release
builds)*. Page 2: download progress. Then hand off to `Setup.exe` and exit.

No settings file is written on a fresh install, and that is on purpose. An install from
`-win-beta-Setup.exe` records `<channel>win-beta</channel>` in its manifest, and
`UpdateRing.For(null, "win-beta")` already follows the installed channel when the setting is unset.
Writing `useBetaReleases: true` as well would only add a second, redundant source of truth — and an
explicit `false` behaves differently from an absent one, so writing the key gratuitously narrows what
the app can later infer.

**Already installed.** `%LOCALAPPDATA%\WusTechnik.ClaudeUsageTray\current\sq.version` is plain XML
carrying `<version>` and `<channel>`, so version and ring are both readable with no Velopack
dependency. Show both. If the requested ring differs, the stub changes it in this order, and the
order matters:

1. **Stop `ClaudeUsageTray.exe` if it is running.** A running app whose Settings dialog is saving
   would otherwise write the whole settings file back over the stub's change. Writing first and
   stopping second is a lost-update race.
2. Write `useBetaReleases`.
3. Relaunch `%LOCALAPPDATA%\WusTechnik.ClaudeUsageTray\current\ClaudeUsageTray.exe`, so the app reads
   the new setting; its next check performs the move, downgrade rule included.

If the app was not running, the stub writes the setting and leaves it stopped — starting an app the
user had closed is not the stub's call. Under `--silent` the same three steps run with no prompt.

`settings.json` is read-modify-written key-wise, never regenerated, so unrelated keys survive. A
malformed or missing `sq.version` is treated as "not installed" rather than throwing — the same
degrade-don't-die rule the app's read paths follow.

## Build and publish

NativeAOT, `win-x64`, with an application manifest declaring comctl32 v6 (needed for
`TaskDialogIndirect`). No WinForms: the wizard is Win32 task dialogs via P/Invoke, since radio
buttons and a progress bar are native task-dialog features. Expected size ~2 MB against `Setup.exe`'s
58 MB.

Two AOT consequences to honour in the implementation:

- GitHub API JSON needs a source-generated `JsonSerializerContext`; reflection-based
  `System.Text.Json` is not AOT-safe.
- **Publishing requires the MSVC linker, so the stub is built in CI only.** `dotnet build` does not
  need it — ILC runs at publish — so the test project can reference the stub project and run locally
  as usual. This was the accepted trade for the size.

`.github/workflows/setup-stub.yml` (`workflow_dispatch` + push on `src/ClaudeUsageTraySetupStub/**`)
publishes to a permanent `setup-stub` tag via `gh release upload --clobber`. The canonical link:

```
https://github.com/wus-technik/win_systray-claude-usage/releases/download/setup-stub/ClaudeUsageTraySetup.exe
```

`release.yml` **copies that asset** onto each release rather than rebuilding it: identical bytes
everywhere, no AOT cost per release, and the stub is rebuilt only when the stub's own source changes.
A fixed tag also decouples the canonical link from the app's release cadence — the reason it is
preferred over relying on `/releases/latest`, which by construction cannot serve a pre-release.

## Testing

Everything decidable is a pure function in the stub's own `Core`, exercised from
`tests/ClaudeUsageTray.Tests` with canned payloads and no network — the rule `UsageApiClientTests`
already follows:

- argument parsing, including unknown flags and `--ring` with a bad value
- SemVer prerelease ordering (`beta.10 > beta.9`, stable > any prerelease of the same version)
- release selection from a canned releases payload: out-of-order publish dates, releases missing the
  channel asset, an empty list, and the fallback URL when resolution yields nothing
- asset-name derivation per channel
- `sq.version` parsing: valid, absent, malformed XML, missing `<channel>`
- the settings read-modify-write preserving unrelated keys
- the safety rule: silent + no `--ring` + existing install ⇒ no write

The wizard itself is not unit-tested; it holds no decisions, only presentation.
