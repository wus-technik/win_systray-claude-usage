# Security audit — Claude Usage Tray

- **Scope:** `main` @ `590937f` (v0.7.3-beta.4), audited in worktree `win_systray-claude-usage-security-audit` (branch `audit/security`)
- **Date:** 2026-09-11
- **Method:** full source review of both executables (tray app + setup stub), all GitHub Actions workflows, build scripts; secret-pattern sweep across the repo; GitHub Advisory Database check for dependencies; repository protection check via `gh api`; full Release test suite run in the worktree (**859/859 pass**: 715 app + 144 stub)

## Findings

### 1. [Medium] Update chain is anchored to the GitHub org, not to a key

Nothing shipped is code-signed (documented in `src/ClaudeUsageTraySetupStub/Downloader.cs:51`), so neither the Velopack auto-update nor the setup stub can cryptographically verify what it downloads and executes. Protection is TLS to github.com plus GitHub's per-asset sha256 digest — which GitHub itself computed, so it defends against transport tampering only. A compromised org account or any write-access collaborator can publish a malicious `v*` release; every installed client auto-downloads it, and applying it is one user click away.

**Mitigations:** code-sign the app and the stub (Velopack then verifies Authenticode signatures on update), or at minimum gate the release path (see finding 2).

### 2. [Medium] `main` has no branch protection and the repo has no rulesets (no tag protection)

Verified via API: `GET /repos/wus-technik/win_systray-claude-usage/branches/main/protection` → 404, `GET /repos/.../rulesets` → empty. Anyone with repo write access can:

- push to `main` (re-publishes the setup stub to the permanent `setup-stub` release), and
- push a `v*` tag (triggers `release.yml` → update for all installed users).

**Recommendation:** branch protection on `main` (required review, 2FA required) plus a `v*` tag protection rule. This directly bounds the blast radius of finding 1.

### 3. [Low] Status-page `shortlink` is opened via shell execute without validation

`src/ClaudeUsageTray/Core/StatusDetailRows.cs:98` takes `incident.Shortlink` straight from the unauthenticated status-page payload and returns it as `StatusRow.Link`; `src/ClaudeUsageTray/Tray/UsagePopup.cs:190` opens it with `Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })`. A compromised or malicious status payload could offer any scheme or host. Requires an explicit user click on the "Details" link.

**Recommendation:** restrict to `https://` on the source's own host (`status.claude.com`, `status.openai.com`), or drop the link.

### 4. [Low] Stub `--token <t>` puts the GitHub PAT on the process command line

The token is visible via command-line inspection (same user, admins). Logging is clean — `src/ClaudeUsageTraySetupStub/Program.cs:23` writes only `token=yes/no`. The `GH_TOKEN` environment variable path already exists and avoids the exposure.

**Recommendation:** steer fleet operators (`--ring beta` rollouts) to `GH_TOKEN` with a fine-grained read-only token in the usage docs.

### 5. [Info] Third-party status text is displayed verbatim and written to `fetch.log`

Banner wording, incident names, and component names from the status pages reach the UI and `fetch.log` as-is. No injection: the toast XML is escaped (`SecurityElement.Escape`, `src/ClaudeUsageTray/Tray/ToastPresenter.cs:159`), the log is a plain-text append, and the labels are WinForms (no HTML rendering). Noted so a log reader knows what to expect.

## Verified clean

- **Credential handling** — `CredentialsReader` is read-only (32 MiB cap, never throws); the OAuth token is sent only as a Bearer header to `api.anthropic.com` and is never logged (every `fetch.log` call site checked); the in-memory `_rejectedToken` compare in `TrayApp` is the only retention.
- **No secrets in the repo** — pattern sweep (api key / secret / password / private key / PAT shapes) found only `secrets.GITHUB_TOKEN` in workflows and a `ghp_abc` test fixture. Workflows use only the default token with least-privilege `permissions`, pinned action majors, and concurrency guards.
- **Network** — only hardcoded HTTPS endpoints (`api.anthropic.com`, `status.claude.com`, `status.openai.com`, github.com); no user-supplied fetch URLs (status sources are a curated registry; path overrides are read-only file reads); no TLS bypass or certificate overrides anywhere; no telemetry.
- **Setup stub** — refuses SYSTEM/session 0; MZ-magic plus fail-closed sha256 check before executing the downloaded installer; random-GUID temp dir with cleanup; no command injection (fixed argument vectors, quoted paths); per-user scope; settings edit is atomic and refuses malformed files rather than destroying them.
- **Robustness** — all read paths swallow IO/JSON errors with size caps; settings save is tmp+move atomic; session-local single-instance mutex; HKCU-only registry writes (no admin rights).
- **Dependencies** — one shipped package (Velopack 1.2.0), zero advisories in the GitHub Advisory Database; test-only packages (xunit, Microsoft.NET.Test.Sdk) never ship.

## Bottom line

No exploitable issues in the shipped code. The two medium findings are about the release path around the app (unsigned updates + unprotected repo), not the app itself.
