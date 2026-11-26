# Changelog

All notable changes to RcloneMount will be documented here.

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).
Versioning follows [Semantic Versioning](https://semver.org/spec/v2.0.0.html):
bump patch for fixes, minor for user-visible features, major for breaking
changes (e.g. `settings.json` shape changes that need a migration).

## [Unreleased]

### Security
- **rclone's remote-control port now requires a password.** Every mount and
  transfer job started rclone with `--rc-no-auth`, so any program on the PC,
  any other Windows user, or a web page able to reach localhost could call
  `operations/*` (delete files) or `config/*` (read tokens) on 127.0.0.1.
  Each app run now generates a random RC password, passes it to rclone with
  `--rc-user` / `--rc-pass`, and sends it on every stats/quit call.
- **Passwords and tokens no longer reach the activity log or bug-report
  bundle.** A failed `rclone config create/update` put every argument —
  including `pass=…` and `token=…` — in the error message, which was shown,
  saved to `activity.log`, and zipped into diagnostic bundles. Errors now
  name only the command, and the bundle redacts secrets from older log
  lines as it copies them.
- **rclone downloads are verified.** Installing or updating rclone now
  downloads the versioned release zip and checks it against that release's
  `SHA256SUMS`; a mismatch stops the install.
- **The WinFsp installer must be digitally signed before it runs as
  administrator.** An unsigned or tampered MSI is refused.
- **The CLI pipe only accepts the current Windows user.** The default pipe
  permissions let other users read it, and its name is predictable, so
  another account could create it first and answer your scripts.

### Fixed
- `--unmount` now reports a failure when another rclone process still holds the
  drive, instead of saying it was unmounted.
- The message after renaming a remote counts only the drives that came back.
- **Changes made in quick succession are saved correctly.** When two saves overlapped, the
  settings file could get half-finished changes or the save could fail, and a mount running
  on a temporary drive letter could show the wrong letter.
- **Restoring a backup stays restored.** The closing app could write its old settings or
  transfer totals back over the restored files before restarting.
- **"Backup now" no longer collides with a save in progress.**
- **`--mount` picks another drive letter when yours is taken**, like the Mount button does.
- **You are told when settings cannot be saved.** The failure appears in the log and as a
  tray notice, once until saving works again.
- **Restore stops transfers and mounts first.** It tells you they will be cancelled, and
  does not start while an rclone terminal opened from the app is still open.
- **A restore that fails changes nothing.** A damaged backup, or a file that cannot be
  replaced, leaves your settings and rclone.conf as they were.
- **Transfer totals keep counting** after restoring a backup that does not include them.
- **Mounting from the tray picks another drive letter when yours is taken.**
- **A mount that fails to start shows your chosen drive letter again**, not the substitute.
- **Unmounting a drive that used a substitute letter** no longer claims another rclone still
  holds it, and Explorer is told about the right letter.
- **`--unmount` also stops a pending remount**, so the drive does not come back.
- **"Backup now" reports when no backup could be written**, and a restore does not start if
  your current settings cannot be backed up first.
- **"Last run" on the Jobs tab updates after every run.** Only scheduled runs recorded it;
  jobs started by hand stayed on "never run".
- **"Last run" only counts runs of the job as saved.** Starting a saved job after changing
  its settings without saving, or after its remote was removed, no longer marks that job as
  run. Runs are also no longer missed when many jobs finish at once or rclone fails to start.
- **A removed remote is no longer replaced by another one.** The transfer window selected
  local disk or the first remote instead. It now names the missing remote and waits for you
  to pick one before you can start or save.
- **Remotes with a one-letter name are refused.** Windows reads `q:` as drive Q:, so a job,
  mount or connection test used the local drive instead of the remote. Jobs, mounts and tests
  now ask you to rename the remote, and new or renamed remotes need two or more characters.
- **Deleting a job closes its open window**, so Update there can no longer bring the job back.
- **Deleting a job cancels its queued and running transfers.** A sync or move could still run
  after the job was gone. The confirmation says so.
- **Renaming a remote updates saved jobs**, queued transfers and open transfer windows, not
  only mounts.
- **Deleting a remote names the saved jobs that use it** before you confirm. Those jobs stop
  running, also on their schedule, until you edit each one and pick a remote, even if a new
  remote gets the old name. The Jobs list says why.
- **Renaming a remote also updates remotes that wrap it** (crypt, alias, union, combine and
  others) in `rclone.conf`. For an encrypted config, the edit window lists the ones to change.
- **Retry no longer re-runs a deleted job.**
- **Screen readers** now read the selected value of the app's drop-down lists.
- **The transfer window no longer calls a remote missing when the remote list could not be
  read.** It tells you to refresh instead.
- **Every failed job shows its notification**, also when many jobs finish at once.
- **A saved job opens in one window only.** Opening it again brings that window forward.
- **Saving a new job twice no longer creates a duplicate.**
- **`&` shows in names and headings.** "US & Canada" and "Startup & Updates" lost the `&`.
- **Local paths in job rows** no longer start with a stray `:`.
- **Layout:** the server-side transfer checkbox is no longer cut off (shorter label), the
  services list has no horizontal scrollbar, the `>_` button shows in full, and the About
  cards fit the window.
- **Screen readers** read transfer history rows and the sidebar buttons by name.
- **"Start with Windows" repairs a broken entry.** The toggle showed on while the
  startup entry pointed to an app file that no longer existed. At startup the app now
  points such an entry to itself and logs it.
- **"View LICENSE file" works in an installed copy.** The app now ships `LICENSE.txt` and
  `THIRD-PARTY-NOTICES.md`.
- **The About tab's privacy text was wrong.** It said the app transmits no
  data, but by default it checks GitHub for new RcloneMount, rclone and
  WinFsp releases at startup. The About tab and the README now say what is
  contacted (`github.com`, `api.github.com`), when, and how to turn the
  startup check off.
- **A failed automatic update now appears in the activity log**, not only
  in the status bar.
- **A locked, unreadable or empty `rclone.conf` no longer deletes your
  mounts.** When the remote list could not be read at startup (for
  example an encrypted config), or came back empty (for example after
  picking a portable rclone with another config), every mount was removed
  from `settings.json` as "orphaned". The repair now runs only when the
  list loaded and has remotes in it.
- **A cancelled Windows shutdown no longer leaves RcloneMount half dead.**
  It used to unmount, remove the tray icon and stop the scheduler when the
  shutdown was asked for, and kept running that way if you cancelled it.
  The cleanup now waits until Windows confirms the shutdown.
- **Upload checks, keep-awake and live stats survive an rclone update.**
  After an rclone update, Locate rclone or an automatic update, the
  pending-uploads count, keep-awake, live speeds and lifetime totals
  stopped until you restarted the app.
- **Changing a job to `every N hours` no longer runs it within seconds.**
  The interval now starts from the save.
- **Lifetime transfer totals are saved atomically**, so a shutdown can no
  longer reset them to zero.
- **rclone no longer waits forever on a hidden password prompt.** A missing
  or wrong config password now fails fast, and RcloneMount asks again once.
- **Cancelling a running job now shows it as Cancelled.** It used to end
  as Failed or Completed, depending on how rclone exited.
- **A schedule the app cannot read is no longer silently ignored.** The
  job dialog shows what is wrong and will not save until the schedule is
  fixed or cleared. A bad schedule that is already saved is reported in
  the activity log.
- **Editing a saved job no longer resets its run record.** After an edit,
  a daily job could run a second time the same day, and editing a job
  while it was running lost that run's result.
- **A failed rclone update no longer leaves the app unable to mount.**
  Mounting stayed disabled until restart if the download or file swap
  failed; rclone is now re-detected either way. Updates also wait while a
  transfer is running, and running transfers no longer disappear from the
  queue when rclone is re-detected or located.
- **Crash-safe writes for `rclone.conf` and settings.** Renaming a remote,
  pruning its options, and restoring a backup rewrote `rclone.conf` /
  `settings.json` in place, so a crash mid-write could truncate the file and
  lose every remote. All of these now write a temp file and swap it in.
  Settings saves are also serialised, so a scheduled job finishing at the
  same moment as a UI save can no longer make one of them fail silently.
- **Exit now waits for drives to unmount cleanly.** The close handler gave
  up control at its first `await`, so the app quit before rclone was asked
  to stop and mounts were killed mid-flight. Exit now finishes unmounting
  and saving totals before the app closes. Running transfers still stop
  with the app and are offered for resume on the next launch.
- **Only one RcloneMount runs at a time.** Launching it again (for example
  autostart plus a double-click) used to start a second tray that
  auto-mounted again, ran every scheduled job a second time, and fought over
  `settings.json`. A second launch now brings the running window forward
  and exits; Restore's relaunch waits for the old instance to close.
- **A damaged settings file no longer wipes your mounts and jobs.** If
  `settings.json` could not be parsed, the app silently started empty and
  its first save overwrote the file. It is now kept as
  `settings.json.bad-<date>-<time>` and a warning points you to Restore. If
  the file exists but is locked or unreadable, the app warns and does not
  save at all that session.
- **Scheduled and resumed jobs now run with all their saved settings.** The
  scheduler rebuilt each job by hand and dropped bisync's `--max-delete`,
  `--check-access` and `--conflict-resolve`, and check's `--one-way` /
  `--size-only` — so a bisync saved with a 10 % delete limit ran overnight
  with rclone's 50 % default and no access check.
- **Jobs are no longer stopped early or reported as succeeded when they
  failed.** A "completion detector" sent `/core/quit` whenever rclone's stats
  stayed flat for a few seconds, on the belief that `--rc` keeps rclone
  alive. It doesn't (verified on rclone 1.76: copy, sync and bisync exit by
  themselves). The detector cut slow listings and retry pauses short — a
  copy that failed and was waiting to retry came back as exit 0 "Success",
  and an SFTP sync could end before looking at a file. It is gone: a job
  now ends when rclone ends, with rclone's own exit code. Transfer
  processes are also tied to the app, so they stop if it exits or crashes.
- **ACTIVE strip no longer flickers on every stats tick.** The list was
  disposing and rebuilding every row once per second — the ProgressBar
  visibly flashed to zero and re-filled each time. RefreshActiveList now
  matches existing controls by JobRun.Id / drive-letter key and re-binds
  in place; only new rows are constructed, only vanished rows disposed.
- **A restore always keeps a way back.** When the settings file was missing (for example after
  a damaged one was moved aside), a restore could replace `rclone.conf` without backing it up.
- **A failed backup no longer leaves half a backup behind.** If a file such as `rclone.conf` was
  locked, the unfinished zip was listed with the good ones and restored only part of your setup.
- **A drive you unmount stays unmounted.** Waking the PC, a network change or an rclone update
  could mount an auto-mount drive again; now it waits until you mount it or restart the app.
- **Renaming a remote can no longer create two with the same name.** The name was not checked
  against remotes listed after the renamed one in `rclone.conf`.
- **Deleting a remote cancels its queued and running transfers**, also those that reach it
  through a crypt, alias or union remote. A sync could keep deleting files after the remote
  was gone. The confirmation says so.
- **A remote edit that fails remounts your drives.** If a rename or the update after it failed,
  the drives unmounted for the rename stayed down.
- **`--unmount` tells Explorer the drive is gone** and shows your chosen drive letter at once.
- **Command-line requests no longer wait forever on a busy app.** If the app cannot take a
  `--list`, `--mount` or `--unmount` within 10 seconds, it answers that it is busy and does not
  run it later.
- **Asking to mount a drive you had unmounted counts even if the mount cannot start yet**, so
  automatic remounting can bring it up later.
- **A skipped automatic backup appears in the log**, for example while `rclone.conf` is locked
  by another program. Your settings are still saved.

### Added
- **Support link.** The About tab and the README link to Buy Me a Coffee.
- **Mounts no longer lose pending uploads.** Mount rows show "N uploads
  pending" while rclone still holds write-backs that are not in the cloud
  yet. Exit, Unmount, Remove mount, Restore, Locate rclone and both
  updates now ask first: Wait until they finish, go ahead anyway, or
  cancel. Waiting never treats an rclone that stops answering as "done".
  Removing a mount, or renaming or deleting a remote, says plainly that
  its pending files will not be uploaded. Auto-update is skipped until
  the next check while uploads are pending, and the auto-recover remount
  waits instead of cutting uploads off, with one balloon if it has waited
  more than 10 minutes.
  `RcloneMount.exe --unmount NAME` refuses with exit code 6 while uploads
  are pending.
- **Keeps the PC awake and holds shutdown during work.** While a transfer
  job runs or mount uploads are pending, the PC will not idle-sleep. If
  you shut down or sign out while the RcloneMount window is open, Windows
  lists it with the reason and you can still choose "Shut down anyway".
  While RcloneMount sits hidden in the tray, Windows does not allow an
  app to hold shutdown, so only the keep-awake applies.
- **Encrypted `rclone.conf` support.** RcloneMount asks for the config
  password once and keeps it protected for your Windows account (DPAPI,
  under `%AppData%\RcloneMount`). It passes the password to every mount,
  job and the rclone terminal through the child's environment, never on a
  command line or in a log, so scheduled jobs run unattended. Renaming a
  remote is refused on an encrypted config (use `rclone config`), and
  removing old options after a provider switch needs rclone 1.75 or later.
- **More schedule types.** Besides `daily HH:MM`, jobs now accept
  `weekdays HH:MM`, chosen days such as `mon,wed 21:30`, and `every 6h`.
  The Jobs list shows each job's next run. Daily and day-based schedules
  run at most once a day, and `every Nh` keeps an exact cadence that
  starts when it is first saved, so an upgrade never starts a run by
  surprise. Existing `daily HH:MM` schedules keep working unchanged.
- **Tray notification when a transfer job fails.** A queued or scheduled
  job that fails now shows a balloon with the job name and rclone's last
  error line, once per run. It follows the Notifications setting.
- **VFS cache limits per mount.** The mount editor can set the cache
  folder, a maximum cache size (for example `10G`) and the minimum free
  disk space to keep. Values are checked against rclone's own size rules
  before saving; blank leaves rclone's default.
- **New files from transfer jobs show up on mounted drives at once.**
  When a job finishes, any mount of the remote it wrote to refreshes the
  folders from the mount root down to the job's target folder. Deeper
  folders update at the next change check or the 15 s re-list.
- **Help with Google's shared client_id retirement.** rclone's shared
  Google client_id will stop working during 2026. Google Drive and Google
  Photos remotes that still use it now show a warning with a link to
  rclone's "Making your own client_id" steps, where to paste the id and
  secret (Edit, then Reconnect), and a reminder to publish the Google app,
  because sign-ins for apps left in Testing expire after a week.
- **Installer with in-app updates.** RcloneMount now ships as a Velopack
  `Setup.exe` (plus a portable zip). Settings → **Install Update** downloads
  the new version, closes cleanly (unmounting drives), installs and
  restarts. Uninstalling removes the autostart entry.
- **rclone terminal.** A console opened from the tray menu or the `>_`
  button, with the app's `rclone.exe` and `rclone.conf` already set up, so
  any rclone command — including interactive ones like `config` or
  `ncdu` — is one click away.
- **Extra rclone flags on transfers and saved jobs** (for example
  `--fast-list --transfers 8`), also used by scheduled and resumed runs.
- **Silent-success diagnostic in the activity log.** When a job exits
  successfully having moved zero bytes and done zero checks, the last
  ~20 lines of rclone's stderr are now logged (Info level; Warn on
  non-zero exit). Makes it possible to tell "nothing to sync" from
  "rclone hung on the SFTP handshake" without re-running with a debugger.

### Changed
- **Smarter folder caching on mounts.** Mounts of remotes that report
  their own changes (Google Drive, OneDrive, Dropbox, Box, pCloud, and
  crypt/alias remotes on top of them) now keep folder listings for 1000 h
  and check for changes every 15 s, instead of re-listing every folder
  every 15 s. Other remotes keep the 15 s listing cache. A
  `--dir-cache-time` or `--poll-interval` in a mount's Extra flags still
  wins.
- **Runs on .NET 10.** .NET 8 support ended on 2026-11-10; the app now
  targets `net10.0-windows` (LTS, supported to November 2028). New
  `scripts/build.ps1` compiles the solution and runs the new test project.
- **Build and release scripts.** `scripts/build.ps1` opens a menu: a full
  build + Velopack publish, or a local test build stamped with a version
  (runnable copy in `dist\local\<version>\`). The next version is suggested
  from this changelog's `[Unreleased]` headings. `scripts/release.ps1`
  builds `Setup.exe` and the portable zip, then stamps the version and
  rolls the changelog only once everything succeeded, and can publish to
  GitHub with `-Upload`. Only the 3 newest local builds and the 3 newest
  releases in `dist\releases` are kept. From the menu, errors stay on
  screen until you press Enter, and a copy of the app running from the
  build folder is reported instead of failing the build.
- **QUEUE and MOUNT ACTIVITY merged into one ACTIVE panel.** The Transfers
  tab now has a single unified list — every JobRun and every mount VFS
  read in one place, per-row type chip disambiguates. JOB rows keep
  Cancel/Retry/Remove. MOUNT rows get two new actions: **Unmount drive**
  (the honest coarse cancel — kills every I/O on the drive after a
  confirm, using the graceful UnmountAsync path) and **Copy locally**
  (spawns a JobRun that pulls the file to a picked local folder so it's
  cached instead of re-fetched on every open — direct fix for the mega
  quota loop pattern). MaxConcurrent still governs JOBs only; MOUNT rows
  can't be queued, they run when the OS asks.
- **JOBS moved to its own tab.** Saved templates now live on a dedicated
  Jobs tab; the Transfers tab is purely live activity — QUEUE, MOUNT
  ACTIVITY, HISTORY — with more vertical room per section. Templates
  change rarely, live transfers change constantly; different cadence
  earns different tabs. `+ New job`, Run, Edit, Delete all migrated.

### Added
- **Unified transfer queue (Phase 1).** Every transfer now flows through a
  single `JobQueueService` with a FIFO queue and MaxConcurrent = 1 by
  default. Sources: TransferDialog Start, right-click-mount ad-hoc
  transfer, and scheduled runs. The Transfers tab gains a **QUEUE** panel
  showing every `JobRun` with a colour-coded state chip
  (Queued / Running / Done / Failed / Cancelled), live progress + ETA,
  and per-row **Cancel** / **Retry** / **Remove** buttons. The old
  ACTIVE panel is renamed **MOUNT ACTIVITY** — it still shows VFS reads
  through drive letters (a separate beast that can't be cancelled without
  unmounting). Motivation: user couldn't see or kill an in-progress
  transfer that had failed and been silently retrying; the parallel
  modeless-`TransferDialog`-per-run pattern was invisible and
  unmanageable on limited uplinks.
- **Dry-run report in the Transfer dialog.** Previously a dry-run finished
  with no visible output — the whole point (previewing changes) was
  invisible. `RcloneJobService` now parses rclone's `NOTICE: … Skipped …
  as --dry-run is set` lines into a `DryRunAction` list returned in
  `JobResult`, and the dialog shows them in a scrollable list beneath the
  buttons with a "Save report…" button that writes a tab-separated `.txt`.
  The dialog header updates to "Dry-run finished — N would-be actions"
  (or "no changes" if the sides already match). History is untouched —
  dry-runs still don't pollute the completed-work feed.

### Changed
- **TransferDialog is now setup-only** — Start enqueues the run onto the
  queue and closes the dialog. There is no more in-dialog live progress,
  in-dialog Cancel, or in-dialog dry-run report; visibility and control
  moved to the QUEUE panel. Full swap (no dual code path).

### Fixed
- **History tab now updates live after a transfer job finishes.**
  Previously only mount-side completions from the stats poller pushed
  rows into the UI; rows written by `RcloneJobService` (Transfer-dialog
  runs and scheduled runs) landed on disk but the tab didn't notice
  until the app restarted. `TransferHistoryService.AppendAsync` now
  raises an `EntriesAppended` event, the Transfers tab subscribes on
  build and prepends the rows on the UI thread, and the mount-side path
  drops its duplicate local prepend so there's one code path for both
  producers.

### Changed
- **Job summary row in History now shows an uploaded / downloaded /
  deleted breakdown, and is written for scheduled runs too.** Previously
  only Transfer-dialog runs got the `Job: <name> · N files` summary; jobs
  fired from the scheduler produced only per-file rows, so browsing
  History back to a scheduled run looked like a single file name instead
  of a job's-eye view. The summary emit is now inside `RcloneJobService`
  itself and uses per-verb counts from the `-v` INFO parser, so both
  entry points get the same row and the breakdown reads e.g. `Job: Backup
  · 47 uploaded · 3 deleted` instead of just `47 files`.
- **Per-file transfer-history entries now come from rclone's `-v` stderr
  instead of `/core/transferred`.** The RC endpoint was returning empty
  or bytes-less results for SFTP (and possibly other backends), so the
  History tab was missing rows for files that actually uploaded and
  showing `bytes:0` for the ones it did capture. The new parser watches
  each `INFO : <name>: Copied (…)` / `Deleted` / `Moved` line rclone
  prints, enqueues a history row per match, and drains the queue every
  2s to `transfers.jsonl`. Byte counts for local-source jobs come from
  the source file's on-disk size (accurate); remote-source jobs still
  log the file name + operation but leave `size:0`. **Deletions are now
  recorded** (`dir:"del"` on the row) — sync/move jobs will show the
  deleted files in history too.
- **Mount directory-listing cache time dropped from rclone's 5-minute
  default to 15 seconds** (`--dir-cache-time 15s`). New files written to
  the remote from outside the mount (a transfer job, another rclone
  process, an FTP client) now show up in the mounted drive within 15s
  instead of up to 5 minutes. rclone only re-lists directories the user
  actually opens, so this doesn't scale with tree size.

### Added
- **Per-file history entries for transfer jobs, stamped with the job
  name.** Jobs now poll their own `/core/transferred` alongside stats and
  append each completed file to `transfers.jsonl` with a new `job` field.
  The Transfers → History row shows `Job: <name> · …` in the meta line so
  you can tell at a glance which job produced any given file. Empty for
  mount-side rows (no owning job); empty-string for ad-hoc runs launched
  without a job name. Check / cryptcheck runs are skipped (no bytes
  moved). Old lines without the field deserialize with `job = ""`.
- **Resume-after-crash for scheduled jobs** — each scheduled run now
  persists an `InFlight` marker before it starts and clears it when it
  finishes. On next launch, any job still flagged as in-flight prompts
  "Resume?" (with a Dismiss option). Resume re-invokes the same rclone
  command; because rclone's differential scan skips files already at
  the destination, this works uniformly across every backend — the only
  difference being whether a mid-flight single file resumes at the byte
  level (chunked backends like S3/GCS/Azure/B2) or restarts (single-shot
  backends like FTP/WebDAV/SFTP/local).
- **Integrity check** transfer ops — `check` and `cryptcheck` are now
  first-class operations in the Transfer dialog and via a "Check
  against… (read-only)" item on each mount's right-click menu. Read-only:
  no dry-run, no destructive confirmation. New per-op toggles: **One-way**
  (ignore files only on destination) and **Size only** (skip hashing for
  speed). After the run finishes, a summary line shows the match / differ
  / missing-on-src / missing-on-dst / errors counts, and an **Open report**
  button reveals the per-run temp folder containing the raw file lists
  rclone wrote (`differ.txt`, `missing-on-*.txt`, `errors.txt`,
  `match.txt`).
- Check / cryptcheck jobs are saveable and schedulable like transfer jobs
  (new `OneWay` / `SizeOnly` fields on `TransferJob`).
- **Bisync** — two-way sync as a first-class operation in the Transfer
  dialog and via a "Bisync with… (two-way)" item on each mount's
  right-click menu. Separate **Initialise (resync)** button gates the
  destructive `--resync` first-run path behind an explicit confirmation.
  New per-op controls: **Conflict resolve** dropdown
  (none/newer/older/larger/smaller → `--conflict-resolve`),
  **RCLONE_TEST marker** checkbox (`--check-access`), and **Max delete %**
  field (`--max-delete`). Bisync jobs are saveable and schedulable
  (new `ConflictResolve` / `CheckAccess` / `MaxDeletePct` fields on
  `TransferJob`).

## [1.0.1] - 2026-07-16

### Added
- **Reconnect** button in the remote detail header for OAuth backends
  (Drive, OneDrive, Dropbox, Box, pCloud, Yandex, HiDrive, premiumize.me,
  put.io, ShareFile, Jottacloud, SugarSync, Zoho). Passes the remote's
  stored params through to `rclone authorize` as a base64-JSON blob, so
  scope / region / custom `client_id` survive into the new token.
- Mount-event balloons on manual mount / unmount (window buttons and tray
  menu). Gated by the Notifications toggle.
- **Diagnostic bundle export** on the About tab — zips `diagnostic.txt`
  (versions/OS/rclone/WinFsp), a redacted `settings.json`, `activity.log`,
  and `transfers.jsonl`. `ExtraFlags` values matching
  `--*(password|passwd|pass|secret|token|key|auth|credential|bearer)` and
  HTTP `Authorization: (Bearer|Basic|Token|Digest) …` payloads are replaced
  with `[REDACTED]`.
- **Transfer history** — the Transfers tab now shows completed transfers
  below the active list. Backed by `%APPDATA%\RcloneMount\transfers.jsonl`
  (bounded at 5000 lines), sourced from rclone's `core/transferred` RC
  endpoint.
- **In-app update check** — new version notification on startup and a
  "Check for updates" button on the Settings tab.
- **"Other S3 Compliant Server"** row in the S3 section of the provider
  picker (was buried in Cloud storage).
- **Archive** backend now appears under "Virtual (wrappers)" in the
  provider picker.

### Changed
- Provider icons renamed to match rclone's sub-provider names verbatim
  (`arvancloud`, `bizflycloud`, `digitalocean`, `exaba`, `fastly`,
  `rackcorp`, `seaweedfs`); `LyveCloud` mapped to `seagate.png`.

### Fixed
- Sub-provider switch mid-edit no longer leaves stale keys from the
  previous flavour in `rclone.conf`. New `PruneRemoteAsync` deletes the
  orphaned lines after `rclone config update`.
- Bottom-bar mount count no longer includes orphan mounts (whose remote
  was deleted). The count now matches what's visible in the rail.
- Settings card for OAuth-only remotes (no user-visible params) no longer
  reappears on every mount-state tick / stats poll.

## [1.0.0] - 2026-07-11

- Initial release.
