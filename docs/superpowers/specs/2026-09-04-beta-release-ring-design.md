# Beta release ring — design

Issue: [#20](https://github.com/wus-technik/win_systray-claude-usage/issues/20)

## Problem

Ship pre-release builds (`0.7.2-beta.1`, `0.7.2-beta.2`, …) to users who ask for them, and to nobody
else. One checkbox in Settings, no reinstall, and a way back to stable.

## Decision

Two Velopack **channels**: `win` (stable) and `win-beta`. `UseBetaReleases` in `settings.json`
selects the ring at runtime through `UpdateOptions.ExplicitChannel`.

Versions are SemVer 2 with **dot-numbered** prerelease identifiers: `0.7.2-beta.1`, `0.7.2-beta.2`,
then stable `0.7.2`. Not `-beta1`: a numeric identifier is compared numerically, so `beta.10 >
beta.9`, while `beta10 < beta9` as a string. Tags stay `v<Version>`, so `release.yml`'s tag/csproj
check needs no change.

The alternative — one channel plus the GitHub pre-release flag — was rejected: it is a smaller
change, but the whole isolation then rests on remembering `--pre`, and `vpk upload` builds
`releases.win.json` from the local `Releases` directory that the delta-baseline download also fills,
so a stray prerelease package can leak a beta into the stable index. A stable client never even
requests `releases.win-beta.json`, which no mistake in a workflow can undo.

## Velopack 1.2.0 behaviour this design rests on

Verified against the 1.2.0 sources, not the docs:

- `UpdateManager.CheckForUpdatesAsync` does **no** prerelease filtering: it takes
  `feed.Where(Type == Full).MaxBy(Version)` and offers it when it is newer. All ring filtering has to
  come from the source — the channel's index file name, and the GitHub prerelease flag.
- `GithubSource.GetReleases` reads only the **first page of 10** releases, newest-published first,
  then drops GitHub pre-releases unless `prerelease: true`.
- `GitBase.GetReleaseFeed` walks *every* surviving release and merges the contents of each one's
  `releases.{channel}.json` (`CoreUtil.GetVeloReleaseIndexName`), skipping releases that do not carry
  that asset. The feed is therefore a union over the window, not "the latest release".
- The channel is **baked into the installed package**: `WindowsVelopackLocator.Channel` comes from
  the installed manifest (verified: `<channel>win</channel>` is present in `sq.version` even for
  packages packed without `--channel`), and `UpdateManager.DefaultChannel` is that value.
- Downgrades: a lower remote version is offered only with `AllowVersionDowngrade`; an *equal*
  `Major.Minor.Patch` on another channel needs `AllowVersionDowngrade` **and** `IsNonDefaultChannel`
  (`SemanticVersion.CompareByVersion` ignores the prerelease part).

## Consequences

**Every stable release must also be published to `win-beta`.** A `win-beta` client never reads
`releases.win.json`, so without a mirror, beta users stall after each stable release until the next
beta. The mirror goes into the *same* GitHub release (`vpk upload github --channel win-beta --merge`),
which also keeps a `win-beta` index inside the 10-release lookback window — the second reason it is
mandatory rather than nice to have.

**Beta releases are uploaded with `--pre`** so they are GitHub pre-releases. Belt and braces: the
stable client keeps `prerelease: false` and so cannot even see them, whatever the channel indexes
say. The beta client uses `prerelease: true`, and still sees the mirrored stable builds because those
live in the ordinary (non-prerelease) release.

## The downgrade rule

`AllowVersionDowngrade` is a per-`UpdateManager` client flag, so the app decides it per check.
`Core/UpdateRing.For(useBetaReleases, installedChannel)` is the single place that decides:

| `useBetaReleases` | installed channel | channel requested | prereleases | downgrade |
|---|---|---|---|---|
| `false` | `win` | `win` | no | no |
| `false` | `win-beta` | `win` | no | **yes** |
| `true` | `win` or `win-beta` | `win-beta` | yes | no |

Enabled **only while a return to stable is pending**, because:

- Opting **in** never needs it: a beta is offered only when strictly newer, and the mandatory stable
  mirror means the beta ring is never behind the stable one. Enabling it in this direction would let
  a lagging beta index drag an opted-in user backwards.
- Opting **out** needs exactly one of the two downgrade branches: latest stable `<` the installed
  beta hits the plain branch; the same `Major.Minor.Patch` on the other channel (installed
  `win-beta` 0.7.2 mirror → `win` 0.7.2) hits the "equal version, different channel" branch, which
  requires the flag *and* a non-default channel — both true here.
- It is **self-healing**: once the stable package is applied, the manifest channel is `win` again and
  the condition turns itself off. Downgrades are never enabled in steady state, so a retracted
  in-ring release still cannot roll anyone back.

An unknown or absent installed channel (dev runs, `dotnet run`, portable builds) counts as *not*
beta, so it never enables a downgrade.

## Failure mode to accept

If no release in the newest 10 carries the requested channel's index, the ring looks *up to date*
rather than broken — Velopack cannot distinguish "nothing newer" from "no feed here". The stable
mirror per release is what keeps this from happening in practice.
