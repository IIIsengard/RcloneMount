# RcloneMount

RcloneMount is a Windows tray app for [rclone](https://rclone.org). It does these things:

- Mounts rclone remotes as drive letters through [WinFsp](https://winfsp.dev).
- Mounts the remotes again after sleep or a lost network connection.
- Runs a transfer queue with copy, sync, move, bisync and check jobs.
- Runs saved jobs on a daily schedule.
- Backs up its settings and `rclone.conf`, and restores them.
- Exports a diagnostic bundle with secrets removed, to attach to a bug report.

## Requirements

- Windows 10 or 11, x64.
- WinFsp, to mount drives.
- rclone 1.76 or later.

If WinFsp or rclone is missing, the app offers to install it. rclone goes to
`%LocalAppData%\Programs\rclone\rclone.exe`. The WinFsp installer asks for administrator rights.

## Install

Download one of these from the Releases page:

- `Setup.exe`: installs for the current user and updates itself from inside the app.
- The portable zip: extract it anywhere and run `RcloneMount.exe`. It does not update itself.

## Reaching any rclone feature

The app has screens for the common tasks only. For everything else:

- **rclone terminal** (tray menu and the Remotes toolbar) opens a console with rclone on
  `PATH` and the app's `rclone.conf` selected. Any rclone command works there, including
  interactive ones such as `rclone config` and `rclone ncdu`.
- **Extra rclone flags** on a saved job adds any rclone flags to that job, for example
  `--bwlimit 10M --exclude "*.tmp"`.

## Command line

The commands are sent to the running tray app.

```
RcloneMount.exe                   Launch the tray UI (default).
RcloneMount.exe --startup         Launch minimised to tray (used by autostart).
RcloneMount.exe --list            Print each configured mount and its state.
RcloneMount.exe --mount NAME      Ask the tray to mount NAME.
RcloneMount.exe --unmount NAME    Ask the tray to unmount NAME.
RcloneMount.exe --help            Show this message.
```

Exit codes:

| Code | Meaning |
|------|---------|
| 0 | Success. |
| 2 | Unknown mount name. |
| 3 | Mount did not become ready in time. |
| 4 | Tray process not running. Start RcloneMount first. |
| 5 | Other error (details on stderr). |

## Where data lives

All files are in `%AppData%\RcloneMount\`:

| File | Contents |
|------|----------|
| `settings.json` | Mounts, saved jobs, schedules and options. |
| `activity.log` | Recent app events. |
| `transfers.jsonl` | Per-file transfer history. |
| `transfer-totals.json` | Total bytes uploaded and downloaded. |
| `backups\` | Zip backups of `settings.json` and `rclone.conf`. You can choose another folder in Settings. |

Remote definitions and their credentials stay in rclone's own `rclone.conf`. The backups contain
a copy of it, so keep the backups folder private.

## Build

You need the .NET 10 SDK. From the repo root:

Run `.\scripts\build.ps1` with no arguments for a menu. It shows the last release, the project
version and the suggested next version, then asks which build you want and which version:

1. **Full build + Velopack publish.** Runs `release.ps1` (below) with the version you pick.
2. **Local test build.** Compiles, runs the tests, and puts a runnable copy in
   `dist\local\<version>\`. The default version is `<suggested>-local.<yyyyMMddHHmm>`.
   Nothing is published, and the project file and changelog are not changed.

Without the menu:

```powershell
.\scripts\build.ps1 -Mode Local                        # local test build, default version
.\scripts\build.ps1 -Mode Local -Version 1.1.0-rc.1    # local test build, your version
.\scripts\build.ps1 -Mode Release -Version 1.1.0       # full build + Velopack publish
.\scripts\build.ps1 -Mode Release -Version 1.1.0 -Upload
.\scripts\build.ps1 -SelfTest                          # test the version rules
```

If RcloneMount is running from the build folder, the build stops and asks you to exit it from the
tray first. It never closes the app for you.

Tests that need rclone use `C:\rclone\rclone.exe`. Set the `RCLONE_EXE` environment variable
to use a different file. If rclone is not there, these tests pass without running.

## Release

```powershell
.\scripts\release.ps1 -SuggestOnly      # print the suggested next version and stop
.\scripts\release.ps1                   # release with the suggested version
.\scripts\release.ps1 -Version 1.2.0    # release with a version you choose
.\scripts\release.ps1 -Upload           # also publish to GitHub (needs GITHUB_TOKEN)
.\scripts\release.ps1 -SelfTest         # test the version suggestion logic
```

The script suggests the next version from the `[Unreleased]` section of `CHANGELOG.md`:

- `### Removed` or a line with `BREAKING` gives a major version.
- `### Added` or `### Changed` gives a minor version.
- Anything else gives a patch version.

A release builds and tests the app and sets the version in the project file. It also moves the
`[Unreleased]` notes under the new version heading. It writes `Setup.exe` and the portable zip
to `dist\releases\`.

## License

MIT. See [LICENSE](LICENSE). rclone and WinFsp are separate programs with their own licenses.
See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
