# RcloneMount Completion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make RcloneMount safe to ship (audit fixes), shippable (Velopack installer, build + release scripts with version suggestion), and complete (every rclone function reachable).

**Architecture:** Keep the existing WinForms + Services layout. Fix bugs in the shared service code, add one small test project, retarget to .NET 10, wrap publish in `vpk pack`, and reach rclone's long tail through a terminal launcher and an extra-flags field instead of per-command GUIs.

**Tech Stack:** C# / .NET 10 WinForms, xUnit, Velopack (`vpk` CLI + `Velopack` NuGet), PowerShell 5.1 scripts, rclone ≥ 1.76.

**Spec:** [docs/WORKLOG.md](../../WORKLOG.md) — audit table (findings 1–17) and the decisions section.

## Global Constraints

- Target `net10.0-windows`; `System.Management` → 10.0.x.
- New dependencies allowed: `Velopack` (user request), xUnit test packages. Nothing else.
- Every RC call sends HTTP basic auth; no spawned rclone gets `--rc-no-auth`.
- `settings.json` stays backward compatible: new fields optional with defaults; old files load.
- Each user-visible change adds a line to CHANGELOG `[Unreleased]` under Added / Changed / Fixed / Security / Removed.
- Update the Status table in `docs/WORKLOG.md` when a task finishes.
- Real-rclone tests use `C:\rclone\rclone.exe` (override with env `RCLONE_EXE`); they return early when it is missing.

## Review Focus

1. A mount left running by an older app version (started with `--rc-no-auth`, no creds) → stats poller gets 401 → row shows no stats, no crash, no error spam. (Task 2 test.)
2. Restore relaunch while the old instance still holds the single-instance mutex → the new instance waits and starts, not "Show" + exit. (Task 7 manual check.)
3. Settings written by v1.0.x (no `ExtraFlags` on jobs) → loads, jobs keep all fields. (Task 13 test.)
4. Unparseable `settings.json` at startup → theme load (`LoadSync`) must not quarantine; only `LoadAsync` does, exactly once. (Task 5 test.)
5. Installed (Velopack) build with autostart on → registry points at the stable `…\current\RcloneMount.exe`, survives an update. (Task 11 manual check.)

---

### Task 1: .NET 10 retarget, test project, `scripts/build.ps1`

**Files:**
- Modify: `src/RcloneMount/RcloneMount.csproj` (TargetFramework, System.Management 10.0.x, `InternalsVisibleTo RcloneMount.Tests`)
- Create: `tests/RcloneMount.Tests/RcloneMount.Tests.csproj` (net10.0-windows, UseWindowsForms, xunit, ProjectReference to app)
- Create: `tests/RcloneMount.Tests/UpdateCheckerTests.cs`
- Modify: `RcloneMount.sln` (add test project)
- Create: `scripts/build.ps1` — `dotnet build -c Release` then `dotnet test`; non-zero exit on failure
- Modify: `scripts/release.ps1` publishDir `net8.0-windows` → `net10.0-windows`

- [ ] Write `UpdateCheckerTests.Compare_OrdersVersions`: `Compare("1.0.9","1.0.10") == -1`, `Compare("v1.76.0","1.76") == 0`, `Compare("x","1") == null`.
- [ ] Run `.\scripts\build.ps1` → build OK, 1 test passes.
- [ ] CHANGELOG `### Changed`: "Runs on .NET 10 (.NET 8 support ended 2026-11-10)."

### Task 2: RC basic auth for every rclone child (finding 1)

**Files:**
- Create: `src/RcloneMount/Services/RcAuth.cs`
- Modify: `Services/RcloneMountService.cs:81-84,19`, `Services/RcloneJobService.cs:180-183,138`, `Services/RcloneStatsPoller.cs:79`
- Test: `tests/RcloneMount.Tests/RcAuthTests.cs`

**Interfaces:**
- Produces: `internal static class RcAuth { const string User = "rcm"; static string Pass { get; } /* 32 random bytes, base64url, per app run */; static void AddServerArgs(Collection<string> args); static AuthenticationHeaderValue Header { get; } }`

- [ ] Tests: `AddServerArgs` adds `--rc-user rcm --rc-pass <Pass>` and never `--rc-no-auth`; `Pass.Length >= 32`; `Header.Scheme == "Basic"` and decodes to `rcm:<Pass>`.
- [ ] Replace `--rc-no-auth` lines with `RcAuth.AddServerArgs(psi.ArgumentList)`; set `DefaultRequestHeaders.Authorization = RcAuth.Header` on the 3 RC HttpClients.
- [ ] Review Focus 1: 401 from a poll is treated like "no stats" (check `RcloneStatsPoller` non-success path).
- [ ] Manual: mount a remote, stats tile updates; `curl -X POST http://127.0.0.1:<port>/core/stats` → 401.
- [ ] CHANGELOG `### Security`.

### Task 3: Stop secrets reaching logs (finding 2)

**Files:** `Services/RcloneConfigService.cs:194,395-400`, `Services/DiagnosticBundle.cs:13-15,34`, test `ConfigServiceTests.cs`, `DiagnosticBundleTests.cs`

**Interfaces:**
- Produces: `internal static string RcloneConfigService.DescribeFailure(string[] args, string stderr)` → `"rclone {args[0]} {args[1]} failed: {stderr}"` (fewer args → join what exists); `internal static string DiagnosticBundle.RedactLogLine(string line)` → replaces values of `\b\w*(pass|password|token|secret|key|credential)\w*\s*=\s*\S+` with `[REDACTED]`.

- [ ] Tests: `DescribeFailure(["config","create","x","sftp","pass=hunter2"],"boom")` has no `hunter2`, contains `config create` and `boom`; `RedactLogLine("… pass=hunter2 token={\"a\":1} host=h")` keeps `host=h`, drops both secrets.
- [ ] `AuthorizeAsync` parse failure message no longer appends `stdout`.
- [ ] Bundle copies `activity.log` through `RedactLogLine` (old logs may already hold secrets); fix the false comment at lines 13-15.
- [ ] CHANGELOG `### Security`.

### Task 4: Saved jobs run with all their settings (finding 3)

**Files:** `MainForm.Scheduler.cs:72-97`, `MainForm.Transfers.cs:539` (make `ParseOp` internal), test `ScheduledSpecTests.cs`

**Interfaces:**
- Produces: `internal static JobSpec MainForm.BuildSpecFromSaved(TransferJob job)` — mirrors `TransferDialog.cs:397-416` rules: `DryRun` only for non-check, `ServerSideAcrossConfigs` only for copy/sync/move with two remotes, `OneWay`/`SizeOnly` only for check/cryptcheck, bisync fields only for bisync, `Resync=false`, filters via `FilterRules.EmitArgs()`.

- [ ] Test: Bisync job `{MaxDeletePct=10, CheckAccess=true, ConflictResolve="newer"}` → spec carries all three; Check job `{OneWay=true, SizeOnly=true, DryRun=true}` → `OneWay`, `SizeOnly` true, `DryRun` false.
- [ ] `FireScheduledAsync` (scheduled + resume) uses it.
- [ ] CHANGELOG `### Fixed`.

### Task 5: Jobs end when rclone ends (finding 4, part of 7)

**Files:** `Services/RcloneJobService.cs:133-135,241-247,311,407-481`, test `JobServiceIntegrationTests.cs`

- [ ] Integration test `RunAsync_CopyExitsOnItsOwn`: temp src with 2 files → `RunAsync(copy spec)` returns `ExitCode 0` within 10 s, both files in dst. Early-return if no rclone.
- [ ] Delete the quiet-stats detector (`quitSent`, `stablePolls`, `sawActivity`, `lastStderrTicks` param) — poll only reports progress; completion = process exit. Fix comments that claim `--rc` keeps rclone alive.
- [ ] Attach job processes to a `ChildProcessJob` so app exit/crash kills them.
- [ ] CHANGELOG `### Fixed` (supersedes the Unreleased "slow initial listing" entry — reword that entry).

### Task 6: Never silently drop a broken settings.json (finding 5)

**Files:** `Services/SettingsService.cs:137-170`, `MainForm.cs:465`, test `SettingsLoadTests.cs`

**Interfaces:**
- Produces: `internal static AppSettings SettingsService.LoadFrom(string path, bool quarantine, out string? quarantinedTo)`; `public static string? LastQuarantinedPath { get; }` set by `LoadAsync`.

- [ ] Tests: invalid JSON + `quarantine:true` → defaults returned, file renamed `settings.json.bad-yyyyMMdd-HHmmss`, `quarantinedTo` set; `quarantine:false` → file untouched (Review Focus 4); missing file → defaults, no rename.
- [ ] `LoadSync` passes `false`, `LoadAsync` passes `true`; `MainForm_LoadCore` shows a warning naming the bad file and pointing at Settings → Backups → Restore.
- [ ] CHANGELOG `### Fixed`.

### Task 7: One instance, user-only pipe (findings 6, 10)

**Files:** `Program.cs:11-68`, `Services/CliRelay.cs:27,70-75,151`, `MainForm.Cli.cs:32-45`, `MainForm.Backup.cs:126-134,165-172`

- [ ] `Main`: named mutex `Local\RcloneMount.<user SID>`; not acquired → `CliRelay.SendAsync(new Request(Command.Show, null))`, exit 0. With `--restarted`, `WaitOne(10 s)` first (Review Focus 2); `AbandonedMutexException` = acquired.
- [ ] `Command.Show` → `BeginInvoke(ShowFromTray)` (`MainForm.Tray.cs:35`).
- [ ] Restore relaunches with `--restarted`.
- [ ] `PipeOptions.CurrentUserOnly` on server and client.
- [ ] Manual: launch twice → one tray, window comes forward; Restore → app comes back.
- [ ] CHANGELOG `### Fixed` + `### Security`.

### Task 8: Exit waits for a clean shutdown (finding 7)

**Files:** `MainForm.cs:540-564`

- [ ] First close: `e.Cancel = true`; guard re-entry; await the existing teardown (unmount all, cancel running queue runs, save totals); then set `shutdownComplete` and `Close()` again.
- [ ] Manual: mount + running copy job → tray Exit → no `rclone.exe` left (`Get-Process rclone`).
- [ ] CHANGELOG `### Fixed`.

### Task 9: Atomic writes + serialized saves (findings 11, 12)

**Files:** `Services/SettingsService.cs:172-213,330-409`, `Services/RcloneConfigService.cs:57-94,227-278`, `MainForm.Scheduler.cs:110-132`, test `AtomicWriteTests.cs`

**Interfaces:**
- Produces: `internal static Task SettingsService.WriteAtomicAsync(string path, string text)` (UTF-8 no BOM, `.tmp` sibling + `File.Move(overwrite)`); `SaveAsync` guarded by a `SemaphoreSlim(1,1)`.

- [ ] Test: write to temp path → content matches, no `.tmp` left; overwrite existing works.
- [ ] Use it in `SaveAsync`, `RenameRemoteAsync`, `PruneRemoteAsync` (join lines with `\r\n` + trailing newline), restore of `settings.json` / `rclone.conf`.
- [ ] Scheduler completion: marshal job-state mutation + save to the UI thread via `BeginInvoke`.
- [ ] CHANGELOG `### Fixed`.

### Task 10: Safe rclone update path (findings 8, 9)

**Files:** `MainForm.Updates.cs:78-122,263-324`, `MainForm.cs:566-615`, `Services/PrerequisiteInstaller.cs`, test `ChecksumTests.cs`

**Interfaces:**
- Produces: `internal static string? PrerequisiteInstaller.FindSha256(string sha256sums, string fileName)`; `InstallRcloneAsync(string targetExePath, LatestRelease release, IProgress<string>?)` downloads the release's `rclone-v*-windows-amd64.zip` asset and verifies it against the release's `SHA256SUMS` asset; `static bool IsTrustedSignature(string msiPath)` via `WinVerifyTrust`.

- [ ] Test `FindSha256` on a sample SUMS text (hit, miss, PGP-clearsigned wrapper).
- [ ] Update refuses while a queue run is `Running`; `finally` re-runs `DetectRcloneAsync` when `mountService` is null; `DetectRcloneAsync` creates the queue only when none exists or none is active.
- [ ] MSI launch refused when `IsTrustedSignature` is false.
- [ ] `// shortcut: SHA256SUMS is checked, its PGP signature is not; add PGP verify if rclone downloads move off GitHub.`
- [ ] CHANGELOG `### Security` + `### Fixed`.

### Task 11: Velopack installer + in-app updates

**Files:** `RcloneMount.csproj` (PackageReference `Velopack`, version = installed `vpk`), `Program.cs` (first line of `Main`), `Services/UpdateChecker.cs` (drop app-release code), `MainForm.Updates.cs:165-217`, Settings-tab install button handler

**Interfaces:**
- Produces: `public const string UpdateChecker.AppRepoUrl` (placeholder until the GitHub repo exists — single place to change).

- [ ] `VelopackApp.Build().OnBeforeUninstallFastCallback(_ => AutostartService.Disable()).Run();` before CLI parsing.
- [ ] App update: `new UpdateManager(new GithubSource(AppRepoUrl, null, false))`; not `IsInstalled` → label "Updates are available in the installed version"; button → `DownloadUpdatesAsync` → clean shutdown (Task 8 path) → `ApplyUpdatesAndRestart`.
- [ ] Manual (Review Focus 5): install `Setup.exe`, enable autostart, apply an update → registry path is `…\current\RcloneMount.exe`.
- [ ] CHANGELOG `### Added`.

### Task 12: `scripts/release.ps1` with version suggestion + Velopack pack

**Files:** `scripts/release.ps1`

**Interfaces:**
- Produces: `Get-SuggestedVersion -Changelog <text>` → `[string]`; params `-Version`, `-Upload` (needs env `GITHUB_TOKEN`), `-SelfTest`, `-SkipCleanCheck`.

- [ ] `-SelfTest` asserts: base 1.0.1 + only `### Fixed` → 1.0.2; + `### Added` → 1.1.0; + `### Removed` → 2.0.0; + `BREAKING` line → 2.0.0.
- [ ] Flow: suggest (or `-Version`) → write csproj `<Version>` → move `[Unreleased]` body under `## [v] - yyyy-MM-dd`, leave an empty `[Unreleased]` → `build.ps1` → `dotnet publish -c Release -r win-x64 --self-contained` (folder, not single-file) → `vpk pack --packId RcloneMount --packVersion v --packDir <publish> --mainExe RcloneMount.exe --icon src\RcloneMount\app.ico --packTitle RcloneMount --releaseNotes <section file> -o dist\releases` → with `-Upload`: `vpk upload github --repoUrl <AppRepoUrl> --token $env:GITHUB_TOKEN --publish`.
- [ ] Run `.\scripts\release.ps1 -SelfTest` → all pass; dry run on a clean tree produces `Setup.exe` + portable zip.

### Task 13: Reach every rclone function

**Files:** `Services/RcloneConfigService.cs:96-104`, `MainForm.RemoteOps.cs:163,226`, `MainForm.Tray.cs` (menu), `Models/TransferJob.cs`, `UI/TransferDialog.cs`, `Services/RcloneMountService.cs:255` (make `SplitFlags` internal), test `SplitFlagsTests.cs`, `SettingsLoadTests.cs`

**Interfaces:**
- Produces: `public void RcloneConfigService.LaunchTerminal(string? args = null)` — `cmd.exe /k` with rclone's folder first on `PATH`, `RCLONE_CONFIG` set to the resolved config path, runs `rclone <args>` or prints `rclone help` banner; `LaunchInteractiveConfig()` becomes `LaunchTerminal("config")`. `TransferJob.ExtraFlags` (string, default `""`).

- [ ] Tests: `SplitFlags("--a 1 \"x y\"") == ["--a","1","x y"]`; v1.0.x settings JSON without `ExtraFlags` loads with `""` (Review Focus 3).
- [ ] "rclone terminal" in tray menu and Remotes toolbar.
- [ ] TransferDialog "Extra rclone flags" box → saved on the job → appended to `ExtraArgs` (dialog + `BuildSpecFromSaved`).
- [ ] Deferred (ask first): saved "custom command" jobs that the scheduler can run.
- [ ] CHANGELOG `### Added`.

### Task 14: Lean cleanup, parser tests, README

**Files:** delete `.inspect/`; `MainForm.Scheduler.cs:198-208`; the 4 `RoundedRect` copies → one `internal static GraphicsPath Drawing.RoundedRect(RectangleF, float)` in `UI/Drawing.cs`; `RcloneConfigService` uses `SettingsService.ResolveRcloneConfigPath()`; `Services/RcloneJobService.cs:543-555` (regexes internal); create `README.md`; `.gitignore` add `graphify-out/`; tests `ParserTests.cs`

- [ ] Tests with real rclone 1.76 lines: `InfoOpRe` on `2026/11/24 10:00:00 INFO  : a.txt: Copied (new)` → name `a.txt`, verb `Copied`; `DryRunSkipRe` on a NOTICE skip line; `TryParseDailySchedule("daily 03:00")` → 03:00, `"daily 24:00"` → false; `RedactSensitiveFlags("--sftp-pass x --drive-token=y")` hides both.
- [ ] README: what it is, install (Setup.exe / portable), CLI usage + exit codes, build (`build.ps1`) and release (`release.ps1`).
- [ ] `.\scripts\build.ps1` green.
- [ ] CHANGELOG `### Removed` not needed (internal only).

---

## Phase 2 — gaps from similar-apps research

Source: [docs/research/2026-11-24-similar-apps.md](../../research/2026-11-24-similar-apps.md). Ranked by user impact. Each task: test first, CHANGELOG line, WORKLOG row.

### Task 15: Never unmount with uploads still pending
`RcloneStatsPoller` reads `vfs/stats` (`uploadsInProgress`, `uploadsQueued`); mount row shows "N uploads pending". Exit, app update, rclone update and auto-recover unmount ask first (or wait) when any count > 0. Test: parser for a real `vfs/stats` JSON sample.

### Task 16: Encrypted rclone.conf
Detect `RCLONE_ENCRYPT_V0:` header; ask for the password once, store with DPAPI (`ProtectedData`, CurrentUser) in `%AppData%\RcloneMount`, pass `RCLONE_CONFIG_PASS` to every child via one shared `ProcessStartInfo` helper. Test: DPAPI round-trip; real-rclone `config dump` on an encrypted temp config.

### Task 17: Notify on failed jobs
Scheduled/queued job failure → tray balloon via existing `Notify` (respects the Notifications toggle). Test: none (UI glue) — manual check.

### Task 18: Google Drive client_id guidance
Drive remotes with empty `client_id` get a warning chip + link to rclone's "Making your own client_id" steps. Verify the shared-ID deadline before writing copy.

### Task 19: Per-backend dir-cache
Mounts of backends with change polling (drive, onedrive, dropbox, box…) use `--dir-cache-time 1000h --poll-interval 15s`; others keep 15 s. After a job writes to a mounted remote, call `vfs/refresh`. Test: backend → flags mapping.

### Task 20: VFS cache limits
Per-mount (or global default) `--cache-dir`, `--vfs-cache-max-size`, `--vfs-cache-min-free-space`, shown in the mount editor. Test: args builder.

### Task 21: Sleep/shutdown guard
While a job runs or uploads are pending: `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED)` and `ShutdownBlockReasonCreate` with a reason string; cleared when idle.

### Task 22: Richer schedules
`TryParseDailySchedule` → `Schedule.TryParse` supporting `daily HH:MM`, `weekdays HH:MM`, `mon,wed HH:MM`, `every Nh`; invalid text shown as an error in the dialog (no silent ignore); Jobs list shows "next run". Test: parser table + next-run calculation across midnight/DST.
