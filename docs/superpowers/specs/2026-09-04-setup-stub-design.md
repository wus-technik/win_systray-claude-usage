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

So against an existing install the stub does exactly three things: it reads `current/sq.version`, it
writes `useBetaReleases` into `settings.json`, and it stops and relaunches the tray process.

`Core/UpdateRing.cs` is linked into the stub as **shared source** (`<Compile Include=… Link=…/>`) —
it is pure and dependency-free. The stub uses `StableChannel`, `BetaChannel` and `IsBetaChannel`,
because every asset name derives from the channel string. It never calls `For`.

## What a ring switch actually does — and does not do

Verified against the code, because the first draft of this design got it wrong:

`UpdateCheck.RunPeriodicAsync` is "check on launch and every 6 h; **download only**. Never terminate
the tray process" (`UpdateCheck.cs:90-91`), and applying is `RestartToApply`, "**explicit user action
only**" (`UpdateCheck.cs:151`).

So writing `useBetaReleases` and restarting the app **stages** a cross-ring package. It does not move
the user to it. The move completes when the user chooses **Restart to update** — the same path every
ordinary update takes. The stub must say so rather than imply the switch is done. Both directions
need their own wording, and the second is the downgrade case:

> Beta releases enabled. Claude Usage Tray will download the next beta build in the background and
> offer **Restart to update** when it is ready.

> Beta releases disabled. Claude Usage Tray will return to the latest stable build — which may be an
> older version than the beta you are running — and offer **Restart to update** when it is ready.

Getting this wrong in the UI would be worse than not shipping the feature: a user told "you are now
on beta" who is still running the stable build has no reason to look for the restart prompt.

## The stale settings file

**This is the sharpest edge in the whole design, and it makes "a fresh install needs no settings
write" false.**

Uninstall removes the install tree, but the only uninstall hook is
`OnBeforeUninstallFastCallback(_ => StartupRegistration.Disable())` (`Program.cs:23`) — nothing
removes `%APPDATA%\ClaudeUsageTray\settings.json`. That is the **roaming** folder
(`Settings.cs:63-65`), so the file also follows the user to a machine that never had the app. And an
explicit `false` really does get persisted, by `ApplySettings` → `PersistSettings`
(`TrayApp.cs:471,486`).

Failure scenario: a user once unticked the beta box, uninstalled, and later runs the stub and picks
**beta**. No `sq.version`, so the stub reads "not installed", installs `-win-beta-Setup.exe`, and the
manifest says `win-beta` — but the surviving settings say explicit `false`. Then
`UpdateRing.For(false, "win-beta")` returns channel `win` with `AllowVersionDowngrade: true`
(`UpdateRing.cs:53-58`), so the very first check offers stable as a downgrade. The mirror case
(leftover `true`, user picks stable) installs stable and immediately stages a beta.

Either way the stub fails to deliver the ring the user just chose — and this is byte-for-byte the
"beta installer undoes itself" bug the beta-ring design exists to prevent, re-entering through a file
this design had decided not to touch.

**Rule:** on every run, including a fresh install, the stub reconciles the settings file with the
chosen ring. If `useBetaReleases` is present and explicitly contradicts the chosen ring, it is
corrected. If the key is absent or `null`, it stays that way — that case is genuinely handled by
`Program.cs:50` resolving an unset value from the installed channel, and writing it would only add a
second source of truth.

## Release resolution

The two rings are deliberately asymmetric, because stable needs no API call and beta cannot avoid one.

| Ring | How the newest release is found |
|---|---|
| stable | `…/releases/latest/download/WusTechnik.ClaudeUsageTray-win-Setup.exe`. GitHub's redirect *is* the version-independence. No API call, so no rate limit to hit. |
| beta | `GET /repos/wus-technik/win_systray-claude-usage/releases?per_page=100`, keep releases carrying `-win-beta-Setup.exe`, take the highest SemVer **by tag**. |

Beta is ordered by parsed SemVer, **not** by publish date: an out-of-order hotfix would otherwise
win. Prerelease identifiers compare numerically, so `beta.10 > beta.9` — the same rule
`build/release-ring.ps1` enforces when it rejects `-beta1`.

**Tags that do not parse as SemVer are skipped, not fatal.** The `setup-stub` tag (below) is itself
such a release, so this is load-bearing rather than defensive.

One page of 100 is the whole query. No paging: at this project's cadence that is years of releases,
and a still-current beta older than 100 releases is not a case worth code.

**Two distinct failure kinds, which must not be collapsed:**

- *API unavailable* (network, 5xx, rate-limited) → the fallback below.
- *API fine, no release carries the asset* → a hard error. There is nothing to fall back to, and
  pretending otherwise would install the wrong ring. This is also the honest answer when a ring has
  no release at all yet.

**Beta fallback, and its honest limits.** On *API unavailable*, fall back to
`…/releases/latest/download/WusTechnik.ClaudeUsageTray-win-beta-Setup.exe`. That asset exists on
every stable release because the `win-beta` mirror is mandatory, so the beta path degrades to "latest
stable, on the beta channel" rather than failing outright. This is a **third** reason the mirror is
load-bearing, alongside the two in the beta-ring design; anyone tempted to drop it now breaks the
beta installer too.

But that build is only *channel* beta — its content is stable. If a newer prerelease exists, the user
asked for beta and silently got stable. So:

- **Interactive:** the wizard names the resolved version before downloading, and says plainly when it
  is the stable mirror rather than a prerelease. That wording is a decision, so it is a pure function
  (`ResolvedBuild.Describe`), not string-building in the dialog code.
- **Silent:** `--ring beta` with the API unavailable **fails closed** rather than installing content
  from the other ring behind an operator's back.

**Rate limiting is per source IP, not per machine.** The unauthenticated limit (60 req/h) is shared
across everything behind one NAT, so a fleet rollout of `--silent --ring beta` starts failing closed
after ~60 machines. `--token` (also read from `GH_TOKEN`) raises it for fleet use; the alternative is
to roll out via the stable path and let the in-app checkbox move people to beta. Worth knowing: the
app's own updater has the same exposure.

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
- The stable path uses the `/releases/latest/download` redirect and so has no digest to check.
  Accepted as-is; adding an API call to fetch one would hand stable the rate-limit exposure it
  currently avoids, for a check that TLS already largely covers.
- Refuse to execute a zero-length or non-PE download.

Code signing for both the stub and `Setup.exe` is the real fix for SmartScreen and for tamper
evidence, and adding a second unsigned launcher makes the reputation problem slightly worse. It is a
purchasing decision outside this design; recorded here as the known gap.

## CLI surface

```
ClaudeUsageTraySetup.exe [--ring stable|beta] [--silent] [--token <t>] [--version] [--help]
```

- `--ring` picks the ring without asking. In silent mode it is the only way to change the ring of an
  existing install; interactively the wizard's radio buttons serve that purpose.
- `--silent` suppresses the stub's own wizard and is passed through to `Setup.exe`.
- `--version` prints the stub's own version and its build commit. This is the one component that
  cannot auto-update, so support will need to ask what a user is holding.

Verified against the shipped Setup 1.2.0 binary rather than the docs: it accepts `--silent` ("Hides
all dialogs and answers 'yes' to all prompts"), `--installto DIR`, `--debug`, and trailing
`-- EXE_ARGS`.

`ClaudeUsageTraySetup.exe` is a WinExe: a shell that invokes it directly (`&` in pwsh, a bare call
in cmd) does not block and does not see its exit code, so a caller that needs the result — CI, a
deployment script — must wait explicitly; deployment agents (Intune, SCCM, PSAppDeployToolkit)
already do this.

**`--installto` is deliberately not exposed.** Nothing in the app supports a relocated install: the
stub's own detection and relaunch read a fixed `%LOCALAPPDATA%\WusTechnik.ClaudeUsageTray`, so an
install placed elsewhere would read as "not installed" on the next run and be installed a second time
in the default location. Passing the flag through would create that trap for no known use.

### User context only

A per-user Velopack install run as SYSTEM lands in
`C:\Windows\System32\config\systemprofile\AppData\Local`, writes the Run key into SYSTEM's registry
hive, and exits 0 — a silent, complete, useless install. Intune Win32 apps and SCCM programs default
to SYSTEM context, so this *will* happen unless prevented.

The stub therefore detects a SYSTEM or session-0 context and refuses with `3001`. For detection rules,
deployment tooling should check for
`%LOCALAPPDATA%\WusTechnik.ClaudeUsageTray\current\ClaudeUsageTray.exe` or the Velopack uninstall key
under `HKCU\Software\Microsoft\Windows\CurrentVersion\Uninstall`.

### Exit codes

`Setup.exe`'s own exit code is **propagated verbatim** when it ran and failed, so nothing about the
installer's reporting is lost. The stub's own failures therefore need a range that cannot be confused
with a child code:

| Code | Meaning |
|---|---|
| `0` | The requested state now holds — installed, ring changed, or already correct |
| *child* | `Setup.exe` ran and returned non-zero — propagated unchanged |
| `3001` | Bad arguments, or a non-interactive/SYSTEM context |
| `3002` | Ring resolution failed (API unavailable with no usable fallback, or no release carries the asset) |
| `3003` | Download or digest verification failed |
| `3004` | Ambiguous request: `--silent` with no `--ring` against an existing install |
| `3005` | Could not stop or relaunch the app, or the settings write did not persist |
| `3006` | The user cancelled the wizard |

`0` means *converged*, so choosing the ring a machine is already on is a success, not a no-op —
running the stub repeatedly with the same `--ring` is idempotent and that is the point. `3004` is not
"did nothing"; it is "the operator never said what the desired state was", which is the one case
tooling cannot treat as convergence. Silent deployments should therefore always pass `--ring`.

### A ring change in silent mode always requires an explicit `--ring`

`--silent` with no `--ring` on a machine that already has a **beta** install must not write
`useBetaReleases: false` and drag that user down to stable. A default that silently reverses a
deliberate opt-in is the one failure mode worth designing against here, and unattended runs are
exactly where nobody would notice it happening.

Rule: `--silent` **and** no `--ring` **and** an existing install ⇒ report version and channel, change
nothing, exit `3004`.

On a machine with no install, no `--ring` in silent mode means stable — matching the documented
default for a normal install.

## Flows

### Fresh install, interactive

Page 1: radio buttons, *Stable (recommended)* / *Beta (pre-release builds)*. Page 2: the resolved
version, then download progress. Then reconcile the settings file (see "The stale settings file"),
hand off to `Setup.exe`, and exit.

### Already installed

`%LOCALAPPDATA%\WusTechnik.ClaudeUsageTray\current\sq.version` is plain XML carrying `<version>` and
`<channel>`, so version and ring are both readable with no Velopack dependency. A missing or
malformed `sq.version` falls back to the `HKCU` uninstall key before concluding "not installed" —
concluding it wrongly would run `Setup.exe`, which per `CLAUDE.md` silently no-ops on an existing
install, and the stub would report success having changed nothing.

Determining the *current* ring needs both sources, in this order: an explicit `useBetaReleases` in
settings wins; otherwise the manifest channel decides. A `null` in the file must not be read as
"stable" — `Program.cs:50` deliberately leaves it unpersisted ("Not written to disk until something
else saves"), so `useBetaReleases: null` is a normal on-disk state for a beta install.

**Refuse to run while an update is mid-apply.** If an `Update.exe` from the install tree is running,
the user has just clicked *Restart to update* and `current/` is about to be swapped; killing the app
would accelerate that swap underneath us, and the relaunch would race a directory being replaced.
Exit `3005`. A package already staged in `packages/` survives a kill untouched, so nothing is lost by
refusing.

If the requested ring differs, the stub changes it in this order, and the order matters:

1. **Stop the tray process.** `TrayApp` is an `ApplicationContext` with no main window
   (`TrayApp.cs:5`), so there is nothing to ask politely — `CloseMainWindow` cannot work and the only
   graceful exit is the Quit menu item. The stop is therefore a terminate, followed by
   `WaitForExit`. Waiting is not optional: `Program.cs:26` acquires the mutex
   `Local\WusTechnik.ClaudeUsageTray`, so a relaunch that races the dying process finds the mutex
   held and exits silently, leaving no tray at all. That same mutex is the reliable "is it running in
   this session" probe, better than a process-name scan, and it also distinguishes the installed copy
   from a portable one.

   Terminating skips the app's deliberate `DisposeNotifyIcon` calls (`TrayApp.cs:382-388`), so a ghost
   tray icon may linger until moused over. Accepted: it is cosmetic and self-clearing.

   Stopping first is what avoids a lost update — a running app whose Settings dialog is saving calls
   `PersistSettings` (`TrayApp.cs:486`), which writes the *whole* file back and would clobber the
   stub's change. Nothing saves settings on exit (`TrayApp.Dispose`, `TrayApp.cs:517`), so stopping
   first is safe.
2. **Write `useBetaReleases`**, then read the file back and confirm the value persisted.
3. **Relaunch** `%LOCALAPPDATA%\WusTechnik.ClaudeUsageTray\current\ClaudeUsageTray.exe`, detached.

Then report what will actually happen — staging, not a completed move (see above).

**If any step after the stop fails, relaunch the old app before exiting non-zero.** Otherwise a failed
settings write leaves the user with no tray until their next login: they ran an installer and lost the
app. The failure mode has to be "nothing changed".

If the app was not running, the stub writes the setting and leaves it stopped; starting an app the
user had closed is not the stub's call. Under `--silent` the same steps run with no prompt.

### Launch children detached

`CLAUDE.md` already documents this trap for `Update.exe apply`: a child launched from a shell dies
with that shell's job object, which looks exactly like a startup crash. The stub's relaunch — and
whatever `Setup.exe` starts after a silent install — inherits the job of the deployment agent or
shell that ran the stub. Launch with `CREATE_BREAKAWAY_FROM_JOB`, falling back to the
`explorer.exe <path>` indirection `CLAUDE.md` uses when breakaway is disallowed.

### Editing settings.json

The file is `%APPDATA%\ClaudeUsageTray\settings.json` (`Settings.DefaultPath`), key `useBetaReleases`,
JSON `true`/`false`.

**The stub must edit it as a `JsonNode` DOM, not by deserialising into `Settings`.** `Settings.Save`
serialises the whole object and the type has no unknown-key preservation, so a round-trip through it
would silently drop any key this stub's copy predates — and the stub is the one component that cannot
auto-update, so it will routinely be older than the app. It would also run `NormalizeFields()`,
rewriting values it was never asked to touch. Editing the DOM also avoids needing an AOT-safe
serialiser contract for the whole `Settings` shape.

Match the key **case-insensitively** and rewrite the node in place. `Settings.Load` uses
`JsonSerializerDefaults.Web` (`Settings.cs:57`), so the app honours `UseBetaReleases` in any casing;
an exact-match edit would add a second key and leave which one wins to chance.

Write via temp file plus atomic replace, matching `Settings.Save`. A missing file is created with just
that one key. A malformed file, or a `useBetaReleases` of the wrong JSON type, is **not** silently
replaced — abort with `3005` and say so, because overwriting it would destroy the user's other
settings. (Note that such a file already costs the user every setting at app launch:
`Settings.Load` falls back to full defaults, `Settings.cs:73-78`.)

### Explicitly not covered

- **Portable-zip users** have no `sq.version` and no uninstall key, so they read as "not installed"
  and the stub will install the per-user app alongside the portable copy, the two then sharing
  `%APPDATA%` settings. Accepted; the stub warns when the single-instance mutex is held by a process
  outside the install tree.
- **`runAtStartup` needs no stub involvement.** `OnFirstRun` (`Program.cs:14-22`) sets the Run key on
  install and `Program.cs:33-44` reconciles it at every installed launch, so a kill-and-relaunch
  self-heals.
- **Multiple user accounts** are fine untouched: install, settings, and Run key are all per-user.

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

**Diagnostics.** A `WinExe` has no stdout, so a silent run would otherwise show an operator nothing
but `3002`. The stub appends to `%APPDATA%\ClaudeUsageTray\setup.log`: ring, resolved version, URL,
HTTP status, outcome — under the same rule as `fetch.log`, never credential material.

### The canonical URL, and the trap in it

`.github/workflows/setup-stub.yml` publishes to a permanent `setup-stub` tag via
`gh release upload --clobber`:

```
https://github.com/wus-technik/win_systray-claude-usage/releases/download/setup-stub/ClaudeUsageTraySetup.exe
```

It triggers on `workflow_dispatch` and on push to **`main` only**, for paths
`src/ClaudeUsageTraySetupStub/**` *and* `src/ClaudeUsageTray/Core/UpdateRing.cs` — the linked shared
source, which would otherwise change without rebuilding the stub. Without the branch filter, a push
on any feature branch would `--clobber` the canonical production asset.

**That release must never become GitHub's `/releases/latest`.** GitHub picks the newest non-draft,
non-prerelease release as `latest`, so a plain `setup-stub` release would take the title — breaking
the README's install link *and* the stub's own stable resolution, which is built on
`/releases/latest/download/…`. The stub would break the exact thing it exists to fix, and only when
the stub is republished, which is the worst possible time to find out.

Therefore: create it with `--latest=false` (`make_latest: false`), and have `release.yml` assert that
`/releases/latest` resolves to a `v*` tag. The beta resolver's skip-non-SemVer-tags rule is the second
guard.

`release.yml` **copies that asset** onto each release rather than rebuilding it: identical bytes
everywhere, no AOT cost per release, and the stub is rebuilt only when its own inputs change. The
`paths:` filter above is what keeps that true, so no build stamp or hash comparison is needed.

## Testing

The stub's tests go in a **separate `tests/ClaudeUsageTraySetupStub.Tests` project** referencing only
the stub, added to the solution so `dotnet test` picks it up. It cannot share
`tests/ClaudeUsageTray.Tests`: that project references `ClaudeUsageTray.csproj`, which exports
`ClaudeUsageTray.Core.UpdateRing`, and the stub linking the same source exports it too — referencing
both yields CS0433 on every use of the type.

Everything decidable is a pure function, exercised with canned payloads and no network — the rule
`UsageApiClientTests` already follows:

- argument parsing: unknown flags, `--ring` with a bad value, `--silent` without `--ring`
- SemVer prerelease ordering (`beta.10 > beta.9`, stable > any prerelease of the same version)
- release selection from a canned payload: out-of-order publish dates, releases missing the channel
  asset, non-SemVer tags such as `setup-stub`, an empty list
- the two failure kinds staying distinct: *API unavailable* → fallback URL, *no asset anywhere* →
  `3002`
- `ResolvedBuild.Describe`: prerelease vs stable-mirror wording, and both switch messages
- asset-name derivation per channel
- `sq.version` parsing: valid, absent, malformed XML, missing `<channel>`
- current-ring resolution from the settings/manifest pair, including `null` settings on a `win-beta`
  manifest reading as beta
- the settings DOM edit: preserving unrelated and unknown keys, matching `UseBetaReleases` in any
  casing without adding a second key, refusing a malformed file and a wrong-typed value
- **the stale-settings reconciliation**: an explicit `false` plus a chosen beta ring must be corrected
- digest mismatch → `3003` and no execution; a non-PE or zero-length download rejected
- the safety rule: silent + no `--ring` + existing install ⇒ no write, exit `3004`

The wizard itself is not unit-tested; with `Describe` extracted it holds no decisions, only layout.
