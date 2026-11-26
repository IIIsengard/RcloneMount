# RcloneMount work log

Running record of how this project is being finished: what was found, what was
decided and why, and what is done. Newest entries at the bottom of each section.
Plan: [docs/superpowers/plans/2026-11-24-rclonemount-completion.md](superpowers/plans/2026-11-24-rclonemount-completion.md).

## Path so far

### 2026-11-24 — graphify installed
- Built knowledge graph: 1082 nodes, 2729 edges, 53 communities (`graphify-out/`).
- `.graphifyignore` skips `src/RcloneMount/provider-icons/` and `scripts/app-icon-preview.png`
  (brand logos, no knowledge, one vision pass each).
- `graphify claude install` wrote `CLAUDE.md` + PreToolUse hook (`.claude/settings.json`, gitignored).
- `graphify hook install`: post-commit/checkout/merge/rewrite hooks + `graph.json` merge driver.

### 2026-11-24 — whole-repo audit (ponytail-audit)
Assumed load: one user, one process, on a PC that may have other programs and users.

| # | Finding | Group |
|---|---------|-------|
| 1 | Every rclone mount/job opens RC with `--rc-no-auth` on 127.0.0.1 → any local program/user can call `operations/*`, `config/dump` | Must |
| 2 | Failed `rclone config create/update` puts all args (`pass=…`, `token=…`) in the error → `activity.log` → diagnostic bundle | Must |
| 3 | Scheduler + resume build `JobSpec` by hand and drop `MaxDeletePct`, `CheckAccess`, `ConflictResolve`, `OneWay`, `SizeOnly` | Must |
| 4 | Quiet-stats "completion detector" sends `/core/quit` after 3 flat polls → long syncs cut short, can show Success | Must |
| 5 | Unparseable `settings.json` → empty settings → first-run save overwrites it, user not told | Must |
| 6 | No single-instance guard → two trays, double scheduled runs, Restore relaunch overlaps | Must |
| 7 | `FormClosing` is `async void` → Exit doesn't wait for unmount; job rclone not in job object | Must |
| 8 | Failed rclone update leaves `mountService` null; `DetectRcloneAsync` recreates the job queue | Should |
| 9 | rclone zip and WinFsp MSI (run elevated) not verified | Should |
| 10 | CLI pipe not `CurrentUserOnly` → another user can squat the name | Should |
| 11 | `rclone.conf` rename/prune/restore write in place, not atomic | Should |
| 12 | Settings saved from a thread-pool thread concurrently with UI saves | Should |
| 13 | No tests; riskiest code is log/flag/schedule parsing | Should |
| 14 | `.inspect/` scratch project with hardcoded user paths | Should (lean) |
| 15 | Dead `LastNonBlankLine` copy in `MainForm.Scheduler.cs` | Should (lean) |
| 16 | 4 copies of `RoundedRect` | Nice |
| 17 | 3 ways to find `rclone.conf` | Nice |

### 2026-11-24 — decisions
- **Scope (user):** fix audit findings, add Velopack installer + updater, a compile script, a
  release script that suggests the next version, keep CHANGELOG current, and let users reach
  every rclone function.
- **Language (user asked "Rust?"):** stay on C#. The app is a thin Windows shell around rclone
  (Go) and WinFsp; a rewrite buys users nothing and costs months. Rust GUI options (Tauri,
  egui, Slint) are weaker than WinForms for a native Windows tray app. If macOS/Linux is ever
  wanted, Avalonia (C#, reuses Services/) comes before Rust.
- **.NET 10:** .NET 8 support ended 2026-11-10. Retarget to `net10.0-windows` (LTS to 2028-11).
  SDK 10.0.400 is installed.
- **"All rclone functions" (ponytail ultra):** no per-command GUIs. Smallest complete reach:
  an "rclone terminal" button (any command, including interactive ones like `config`, `ncdu`)
  plus an "Extra rclone flags" field on saved jobs. Scheduled custom commands are deferred
  until someone asks.
- **Version suggestion:** derived from CHANGELOG `[Unreleased]`: `### Removed` or a line with
  `BREAKING` → major; `### Added` / `### Changed` → minor; else patch. Base = newest
  `## [x.y.z]` heading. Note: csproj says 1.0.22, changelog says 1.0.1 → next is 1.1.0
  either way.
- **Skills:** user rule — download/install any skill needed, then name it. None needed so far.
- **Product aim (user, 2026-11-24):** the best free rclone app for Windows, released as
  pay-what-you-like (donation link, no licence keys) with free code signing (SignPath
  Foundation for open-source projects; app is MIT; needs a public GitHub repo). Main rival:
  Rclone UI — win on reliability where it fails. No macOS/Linux/mobile.

## Publish plan (written 2026-11-25; Tasks 15–22 are done)
1. Get from user: OK to branch + commit a baseline; GitHub owner/name; results of manual checks
   (two launches, Restore, Exit with mounts, rclone terminal, Transfer dialog flags row, build menu).
2. Commit baseline on a branch, then one commit per task.
3. Build Tasks 15–22 (plan Phase 2) with parallel agents in private copies; start with 15
   (pending uploads), 16 (encrypted rclone.conf), 21 (sleep/shutdown guard).
4. Publish: create public repo → set `UpdateChecker.AppRepoUrl` → add "Support this app"
   link (About tab) → apply to SignPath Foundation → `.\scripts\build.ps1` option 1 with upload.

### 2026-11-24 — verified facts (rclone v1.76.0 at `C:\rclone\rclone.exe`)
- `rclone copy|sync|bisync --resync … --rc --rc-addr … -v --stats 1s` **exits by itself**
  (exit 0 within 1 s). The code comment "--rc keeps the process alive" is false, so the
  completion detector is pure risk → delete it (finding 4).
- `rclone rcd --rc-user rcm --rc-pass X`: `/core/stats` without auth → 401, with basic auth
  → 200; `/operations/list` without auth → 401. Fix for finding 1 confirmed.

## Status

| Task | State | Notes |
|------|-------|-------|
| Plan written | done | |
| 1. .NET 10, tests, build.ps1 | done | `net10.0-windows`; System.Management 10.0.15; `WFO1000` suppressed (UI is code-built, no designer); `Form.Closed` → `FormClosed`; `tests/RcloneMount.Tests` (xUnit 2.9.3); `build.ps1` green (1 test). |
| 2. RC basic auth | done | `Services/RcAuth.cs`; `--rc-no-auth` removed from mounts + jobs; 3 RC HttpClients send `Authorization: Basic`. Poller already treats non-2xx as "no stats". Real-rclone test: authed `/core/stats` 200, anon `/operations/list` 401. 5 tests green. |
| 3. Secrets out of logs | done | `RcloneConfigService.DescribeFailure` (verb only); authorize parse error drops stdout; bundle copies `activity.log` via `DiagnosticBundle.RedactLogLine`. 8 tests green. |
| 4. Saved jobs keep settings | done | `MainForm.BuildSpecFromSaved(TransferJob)` mirrors TransferDialog rules; used by scheduler + resume. 11 tests green. |
| 5. Jobs end when rclone ends | done | Test `RunAsync_WaitsThroughQuietRetryGap` (failing copy, `--retries 2 --retries-sleep 6s`) **failed on old code with exit 0** — detector killed it mid-retry and reported Success. Detector deleted (~50 lines); job processes in a static `ChildProcessJob`. 13 tests green. |
| 6. Broken settings.json | done | `SettingsService.LoadFrom(path, quarantine, out movedTo)`; parse error → `.bad-<ts>` + warning; read error (after 3 tries) → `LoadReadError`, saves disabled this run, warning. `LoadSync` never quarantines. 17 tests green. |
| 7. Single instance + user-only pipe | done (manual check pending) | Mutex `Local\RcloneMount.<SID>` in `Main`; second launch sends new `Command.Show` → `ShowFromTray`; `--restarted` waits 10 s (Restore passes it); `PipeOptions.CurrentUserOnly` both ends. **User to verify:** launch twice → one tray; Restore → app returns. Not run by Claude (would auto-mount real drives). |
| 8. Clean exit | done (manual check pending) | `FormClosing`: first pass `e.Cancel`, awaits teardown, then `BeginInvoke(Close)`; `WindowsShutDown` keeps best-effort path. Jobs deliberately not cancelled (keeps `InFlight` → resume prompt). **User to verify:** mount + tray Exit → `Get-Process rclone` empty. |
| 9. Atomic writes + serialized saves | done | `SettingsService.WriteAtomic(path, text)` (unique `.tmp` + `File.Move`); used by settings save, rclone.conf rename/prune, restore. `SaveAsync` behind `SemaphoreSlim(1,1)`. Scheduler completion marshals to UI thread. 18 tests green. |
| 10. Safe rclone update | done (merged) | SHA256SUMS check on versioned release zip (`FindSha256`), WinVerifyTrust on WinFsp MSI, update refused while a transfer runs, `finally` re-detect, queue kept while active (also in Locate-rclone handler — main session). Shortcuts: SHA256SUMS PGP signature not checked; any valid MSI signer passes. |
| 11. Velopack | done (merged) | Velopack 1.2.161; `VelopackApp.Build().OnBeforeUninstallFastCallback(AutostartService.Disable).Run()`; app update via `UpdateManager(GithubSource(AppRepoUrl))`; main session switched to `WaitExitThenApplyUpdates` + `Close()` so the clean exit path runs. `AppRepoUrl` still a placeholder. |
| 12. release.ps1 | done (merged) | Dot-sources `scripts/version.ps1`; `-Version/-SuggestOnly/-Upload/-RepoUrl/-SelfTest/-SkipCleanCheck`; publishes to `dist\publish`, `vpk pack --runtime win-x64`. Main session: dirty check skipped outside git. Verified in scratch copy: 34 tests, vpk verified `VelopackApp.Run()`, Setup.exe 62.3 MB, Portable.zip 55.1 MB, changelog rolled to `[1.1.0]`. |
| 13. Reach every rclone function | done (merged) | `RcloneConfigService.LaunchTerminal(args)`; tray "rclone terminal" + rail `>_`; `TransferJob.ExtraFlags` + dialog field; `SplitFlags` internal; finding 17 (config path) done; `SetRcloneExePath` added in Locate handler. **User to verify:** terminal opens with `rclone version`; Transfer dialog flags row layout. |
| 14. Cleanup + tests + README | done (merged) | `.inspect/` deleted; 4 `RoundedRect` → `UI/Drawing.cs`; parser tests (real rclone 1.76 lines); README; `.gitignore` += `graphify-out/`. Finding 15 removed by Task 13. |
| build menu | done | User request: `build.ps1` menu — 1) full build + Velopack publish (→ release.ps1, version prompt), 2) local test build (version-stamped, `dist\local\<v>\`). Shared rules in `scripts/version.ps1` (`BREAKING` case-sensitive). Stops with a message if RcloneMount runs from `bin\` (never kills it). Non-interactive paths verified; interactive menu (Read-Host) not runnable here. |
| build fix | done | User: "build errors and closes". Root cause: menu option 1 → release.ps1 stamped csproj+CHANGELOG, then build.ps1 -Mode Local hit the running app (PID 27100 from `bin\Release`) non-interactively → throw → window closed; release's undo hint (`git checkout`) would have wiped all uncommitted changelog work. Fixes: `trap` prints `BUILD FAILED: …` and waits for Enter in menu mode (success also waits); lock check before either mode; release.ps1 stamps only after build+publish+pack succeed (version via `-p:Version`), undo hint removed. Real repo restored: empty `[1.1.0]` heading removed, csproj back to 1.0.22. |
| keep last 3 | done | User: "trim to the last 3 versions at all times". `dist\local`: 3 newest folders kept (in-use folder skipped with warning). `dist\releases`: Velopack `download local` → `pack` → `upload local --keepMaxReleases 3` (feed rewritten by Velopack). GitHub releases never pruned. Verified in scratch copy: 4 old + 1 new local build → 3 kept; releases 1.1.0→1.1.3 → `dist\releases` holds 1.1.1–1.1.3 full + delta (deltas 0.1 MB), feed lists exactly those, 1.1.0 removed. |
| research | done | House rule: research similar apps. [docs/research/2026-11-24-similar-apps.md](research/2026-11-24-similar-apps.md) — 13 apps + 8 newer rclone GUIs. Top gaps (checked against code): pending VFS uploads invisible on Exit/update/recover; encrypted rclone.conf unsupported; failed scheduled jobs only logged; Google Drive client_id guidance (deadline unverified); 15 s dir-cache on every mount; no VFS cache size/location limits; no sleep/shutdown guard during work; daily-only schedules. Reddit blocked (403); some prices from snippets only. Added as plan Tasks 15–22. |
| 10–14 | merged | Patches applied to the real tree in order cleanup → release → reach → updates; no conflicts. 34 tests green. | 4 parallel agents in private copies: (10+11) update safety + Velopack, (12) release.ps1, (13) rclone terminal + extra flags, (14) cleanup + parser tests + README. Patches land in `scratchpad/agents/*.patch`; merge + changelog by main session. |
| — | note | User allowed delegation. Tree is uncommitted, so agents work in private copies under the scratchpad and return patches; main session merges and owns CHANGELOG/WORKLOG. |
| — | note | 2026-11-25: baseline (Tasks 1–14) committed as `b6a382d` on branch `release/1.1`, repo identity set to the GitHub noreply address. Phase 2 built by 5 parallel agents in git worktrees (harness `isolation: worktree` refused on this PC — drive-letter case mismatch — so worktrees were made by hand). Every task: implementer → spec+quality review → fix rounds → scoped re-review → squash-merge with CHANGELOG. Test count 34 → 261. |
| 15. Pending uploads guard | done (merged, manual check pending) | `vfs/stats` count on mount rows; one `UploadsGuardAsync` in front of every unmount path (Exit, updates, Restore, Remove/Rename/Delete, tray, row, auto-recover, CLI). Wait never treats "no answer" as "done"; CLI refuses with exit code 6. Review caught: Wait ended silently on RC timeout; false "resumes next mount" promise for Remove/Rename/Delete. |
| 16. Encrypted rclone.conf | done (merged, manual check pending) | DPAPI-stored password, `RCLONE_ASK_PASSWORD=false`, one `RcloneConfigPassword.StartInfo` helper at every launch site; saved only after it decrypts. Prune via `config unset` (rclone ≥ 1.75); rename refused when encrypted. Also fixed a data-loss bug: an unreadable config at startup deleted every mount from settings.json. |
| 17. Failed-job balloon | done (merged, manual check pending) | One balloon per failed run via `Notify`. Review caught: cancelling a *running* job ended Failed/Completed (balloon + wrong state) → now Cancelled. Tests were also writing the user's real `transfers.jsonl` → history path now redirected for the whole test assembly. |
| 18. Drive client_id banner | done (merged, manual check pending) | Banner on Drive / Google Photos remotes with no own client_id (service accounts excluded). Copy names no date: rclone says "during 2026", no primary source gives one. |
| 19. Per-backend dir-cache | done (merged) | ChangeNotify backends (drive, onedrive, dropbox, box, pcloud, pixeldrain, huaweidrive; wrappers follow what they wrap) get `--dir-cache-time 1000h --poll-interval 15s`; others keep 15 s. Non-recursive top-down `vfs/refresh` after jobs. The overview page has no ChangeNotify column — list taken from rclone v1.76.0 source + `backend features` probes. |
| 20. VFS cache limits | done (merged, manual check pending) | Per-mount cache folder / max size / min free space; size grammar checked against real rclone. |
| 21. Sleep/shutdown guard | done (merged, manual check pending) | Keep-awake while a job runs or uploads pend. Shutdown hold only while the window is visible (Windows ends hidden-window apps anyway); `WM_ENDSESSION` runs a bounded teardown. |
| 22. Richer schedules | done (merged, manual check pending) | `daily`, `weekdays`, day lists, `every Nh`; v1.0 strings keep behaviour (oracle test); DST-safe next-run; error shown in the dialog and Save blocked. Review caught: editing a job mid-run left `InFlight` stuck. |
| Final review | done | Whole-branch review (opus) of Phase 2 found 3 integration defects, all fixed in `8d0e3fa`: stats poller stayed bound to a disposed mount service after an rclone update (uploads count, keep-awake, shutdown hold silently stopped); a cancelled Windows shutdown left the app running with no tray/mounts/scheduler; an empty remote list deleted every mount at startup. Plus: banner link contrast, `every Nh` edit ran at once, totals save not atomic, Exit-guard exception left Exit dead. 273 tests, 0 warnings. **Parked:** stale `CloseReason` after a cancelled shutdown. **Not verified (manual):** shutdown hold/`WM_ENDSESSION` after "Shut down anyway", cancelled-shutdown trace, all new UI (banner, schedule dialog error label, mount editor, uploads dialog, password dialog). |
| — | note | 2026-11-26: `session-clock` skill added (`~/.claude/skills/session-clock/`) with a `UserPromptSubmit` hook in this project's `.claude/settings.local.json`; wrap-up phase verified live. |
| Publish prep | done (manual checks open) | 2026-11-26: Buy Me a Coffee link (About tab + README); `LICENSE` and notices ship with the app; `.vscode/settings.json` untracked (local auto-approve rules); 4 `cancel-test` rows removed from the local transfer history; public repo `IIIsengard/RcloneMount`. |
| UI check + 8 fixes | done (real-app check pending) | 2026-11-26: running local build inspected with Windows UI Automation (Playwright cannot drive WinForms). Found and fixed: startup entry pointing to a missing exe while the toggle showed on; `&` eaten in labels; `:` before local paths; cut-off checkbox; services-list horizontal scrollbar; clipped `>_`; About cards 1951 px wide; screen-reader names. Verified by tests (294) and offscreen renders. **Open:** layout is fixed pixels while the app is system-DPI aware, so 125%/150% scaling is unverified. |
