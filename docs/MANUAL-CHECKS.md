# RcloneMount 1.1 manual checks

Purpose: things the 273 automated tests cannot prove. Run them on your own PC with real drives before release. Steps come from the agent reports (later fix rounds win) and CHANGELOG.md.
Record: write pass / fail / note on each "Result" line. Mark a failed check with the build version. Checks marked WARNING can shut the PC down or touch real data: read the line first.
Not done yet: nobody has launched the app for any check below. UI items are unverified.

**Open question that decides the shutdown design:** after "Shut down anyway", does Windows send WM_ENDSESSION to RcloneMount?
How to tell: run S7 (window hidden, idle, drive mounted). In that case the app does nothing at the shutdown query and unmounts only in its WM_ENDSESSION handler. After the restart, a clean unmount in the activity log means yes. No unmount lines means no (rclone was only killed by the job object). S5 cannot decide it, because the hold step already saves totals.
UNCLEAR: the reports do not name the exact log line. First do a normal tray Exit with a drive mounted and note what the activity log says; compare.

**Setup A (used by many checks).** Use a test remote that is safe to write to. Edit its mount: VFS cache mode `writes` or `full`, Extra flags `--vfs-write-back 60s`. Mount it. Copy a file onto the drive. Within about 2 s the row shows "1 upload pending" (amber). It disappears about 60 s later.

---
## 1. Shutdown and exit
WARNING (S4 to S9): these can shut down or sign out the PC. Save all work. Use a test remote and a harmless job (copy a large local folder to a temp folder).

**S1. Keep-awake while a job runs.** Proves the PC will not idle-sleep during a job.
Steps: start a long copy job. In an elevated prompt run `powercfg /requests`. Repeat with the window hidden in the tray. After the job ends wait 2 s and run it again.
Expected: SYSTEM section lists `[PROCESS] ...\RcloneMount.exe` during the job, in both window states; not listed after.
Result: ____

**S2. Keep-awake while uploads are pending.** Steps: Setup A, then `powercfg /requests` while the row shows "1 upload pending".
Expected: RcloneMount listed; gone about 2 s after the label clears.
Result: ____

**S3. Exit while busy releases the block.** Steps: exit the app while a job runs (the uploads prompt may appear; use Exit anyway if so), then `powercfg /requests`.
Expected: RcloneMount no longer listed.
Result: ____

**S4. Shutdown held, window open.** WARNING: real shutdown attempt.
Steps: start a job, keep the main window open, Start > Shut down. On the Windows screen click Cancel.
Expected: Windows lists RcloneMount with "A transfer job is running." App and job keep running. Activity log has "Windows shutdown held: ...".
Result: ____

**S5. "Shut down anyway", window open.** WARNING: the PC really shuts down.
Steps: repeat S4 but choose "Shut down anyway". After the reboot open `%AppData%\RcloneMount\transfer-totals.json`, then start the app.
Expected: `LastUpdatedUtc` is at about the shutdown time and the totals are not reset to 0. The job resume prompt appears. Drives were unmounted (best effort).
Result: ____

**S6. Hidden in the tray, busy.** WARNING: the PC really shuts down.
Steps: run a job, hide the window to the tray, choose Start > Shut down. Note whether Windows lists RcloneMount.
Expected: RcloneMount does NOT hold the shutdown (old best-effort path). Keep-awake still worked beforehand (S1 hidden).
Result: ____

**S7. Hidden, idle, drive mounted: real shutdown.** WARNING: the PC really shuts down. This answers the open question above.
Steps: mount a drive, no job, no uploads, hide the window. Shut down. After the restart read the activity log or rclone mount log, and check `transfer-totals.json`.
Expected: a clean unmount is logged; lifetime totals are intact (not reset to 0).
Result: ____ (WM_ENDSESSION received: yes / no)

**S8. Hidden, shutdown cancelled.** WARNING: starts a shutdown, then you cancel it.
Steps: mount a drive and hide the app to the tray. Open Notepad, type text, do not save. Start > Shut down. When Windows lists Notepad as blocking, click Cancel.
Expected: tray icon still there, drive still mounted, window opens from the tray, tray Exit still works.
Result: ____

**S9. Idle sign-out does not block.** WARNING: signs you out. Steps: no job, no pending uploads, window open, sign out or shut down.
Expected: RcloneMount does not block (old behaviour).
Result: ____

---
## 2. Uploads guard
WARNING: these touch real mounts and settings. Use Setup A with a test remote. In U3 and U5 click Cancel every time. Only suspend the rclone.exe that this one mount started (U4).

**U1. Row label.** Steps: Setup A. Then mount a second drive with cache mode `off` and copy a file.
Expected: label shows and clears as in Setup A; the `off` mount never shows it and nothing prompts.
Result: ____

**U2. Exit prompt.** Steps: with the label showing, tray > Exit. Click Cancel. Exit again, click Wait. Exit again, click "Exit anyway".
Expected: "Uploads still pending" dialog each time. Cancel: app and drive stay. Wait: text "Waiting for 1 upload to finish...", app exits by itself after the upload, file is on the remote. Exit anyway: app exits; next start the activity log shows "Exit went ahead with 1 mount upload(s) pending...".
Result: ____

**U3. Every unmount path shows the guard.** Steps: with the label showing, try each and click Cancel: row Unmount; tray Unmount; ACTIVE-row unmount; Settings > Restore (both entry points); Install rclone update; Locate rclone; Install RcloneMount update (only after the download; needs a newer release, otherwise skip).
Expected: the dialog appears each time, its destructive button reads "<Action> anyway", Cancel keeps the mount.
Result: ____

**U4. Rclone not answering is never "done".**
Steps: label showing. Suspend that mount's rclone.exe in Resource Monitor. Click row Unmount, then Wait. Resume the process.
Expected: dialog says "can't reach rclone" and Wait shows "Can't reach rclone right now. Still waiting..." and does not close. After resume it closes only once the upload finishes.
Result: ____

**U5. Remove mount, rename remote, delete remote.** Steps: label showing. Try each, then Cancel.
Expected: the dialog says "Going ahead now stops them. These files will NOT be uploaded; they stay only in rclone's local cache." Text fits (2 lines).
Result: ____

**U6. CLI refuses.** Steps: label showing. In a terminal: `RcloneMount.exe --unmount <name>` then `echo %ERRORLEVEL%`.
Expected: message "'X' at Y: still has uploads pending. Try again when they finish, or unmount it in the app."; exit code 6 (not 5); drive stays mounted.
Result: ____

**U7. Scheduler paused while the Exit prompt is open.** Steps: label showing, a scheduled job due within a minute. Open the Exit prompt and wait past the due time. Then click Cancel.
Expected: the job does not start while the prompt is open; after Cancel it starts on the next 30 s tick.
Result: ____

**U8. Prompt is visible.** Steps: (a) while the Exit prompt is open click tray > Exit again. (b) Hide the window to the tray, tray > Exit. (c) Minimise the window, copy a large file to the drive, tray > Exit.
Expected: (a) the same prompt comes to the front, no second prompt. (b) and (c) prompt appears on top, centred, visible.
Result: ____

---
## 3. Encrypted rclone.conf
WARNING: back up `%AppData%\rclone\rclone.conf` first (or use a test Windows account). This encrypts your config.

**E1. Unlock dialog.** Steps: back up the file. In a terminal run `rclone config encryption set`. Quit and start RcloneMount. In the "Unlock rclone.conf" dialog check Tab order, Alt+P, Esc. After Esc check Settings. Restart, then enter the right password with Enter.
Expected: Esc gives status "rclone.conf is encrypted and still locked. Click Refresh to enter its password." and all mounts are still listed in Settings (none deleted). Right password: remotes load, auto-mounts start.
Result: ____

**E2. No prompt next time; jobs run unattended.** Steps: restart the app. Run a scheduled or queued job.
Expected: no prompt (password stored); job succeeds.
Result: ____

**E3. Password changed while running.** Steps: in the rclone terminal change the password (`rclone config`, `s`, `a`). Click Refresh. Enter a wrong password. Click Refresh again and enter the right one. Optionally run a job before Refresh.
Expected: dialog appears once; a wrong entry gives the "still locked" status with no second dialog. Right password works. A job run before Refresh fails with rclone's decrypt error ending "Open RcloneMount and click Refresh to enter the config password again."
Result: ____

**E4. Rename locked, prune stays encrypted.** Steps: Edit a remote. Then switch an S3 remote's provider, save, run `rclone config show <name>`.
Expected: the name field is read-only with "rclone.conf is encrypted, so the name can't be changed here. Rename with 'rclone config' in the rclone terminal."; other edits are kept and nothing is unmounted. Old keys are gone and the file is still encrypted (needs rclone 1.75+; older rclone leaves them and the activity log warns "Old options were left in place").
Result: ____

**E5. Hidden start.** Steps: start with the window hidden (autostart or `--minimized`) with an encrypted config and no stored password.
Expected: the dialog shows in the taskbar and on top.
Cleanup: `rclone config encryption remove`, or restore your backup.
Result: ____

---
## 4. Schedules and failed-job balloon
WARNING: use a test profile or harmless jobs. Jobs really run when due.

**C1. Bad schedule in the job dialog.** Steps: new job, type `daily 25:00`, then `nonsense`, then clear the box. Check in light and dark themes.
Expected: red message under the box; Save shows a warning and does not save. `nonsense` message lists the formats. Cleared: error gone, Save works. The message is one full line, not cut off (it is 13 px high) and clear of the Extra flags box. Narrator announces the error on the Schedule box.
Result: ____

**C2. `every 1h` does not run at once.** Steps: fresh profile, save a job with `every 1h`.
Expected: no run starts; row shows `next run <weekday> HH:MM` about 1 h ahead (no "today"/"tomorrow").
Result: ____

**C3. Daily job fires once.** Steps: save `daily HH:MM` 2 minutes ahead. Wait. After it finishes edit the job and press Update.
Expected: enqueued once within about 30 s of that time; row then shows `next run <weekday> HH:MM`; Update does not fire it again.
Result: ____

**C4. Edit during a run.** Steps: during a scheduled run edit the job and press Update. Let the run finish. Restart the app.
Expected: row shows the result; restart does not offer to resume it.
Result: ____

**C5. Jobs row layout (eyeball).** Steps: Jobs tab, job with a last run and a schedule. Resize the window narrower and wider.
Expected: the last-run / next-run text does not run under the action buttons. UNCLEAR: reports give no width at which it overlaps.
Result: ____

**C6. Failed job balloon.** Steps: Notifications on. Run a job that fails (copy from a local folder that does not exist). Then give it `daily HH:MM` 2 minutes ahead and let it fail.
Expected: balloon "Transfer job failed" with the job name and rclone's last error line (at most 150 characters). Scheduled failure: exactly one balloon, not repeated on later queue updates.
Result: ____

**C7. No balloon cases.** Steps: (a) Notifications off (Settings or tray silence), fail the job. (b) Notifications on, cancel a long running job from its row.
Expected: (a) no balloon; activity log shows `Scheduled job '...' failed`. (b) row says Cancelled and no balloon.
Result: ____

**C8. Remote removed outside the app.** Steps: Notifications on. Save two jobs on a test remote, one with `daily HH:MM` 2 minutes ahead. Do not click Refresh. In the rclone terminal run `rclone config delete <name>` and wait for the scheduled time. Click Refresh twice. Create the remote again and press Run on a job.
Expected: at the scheduled time nothing is transferred; one "Saved jobs stopped" balloon and one activity log line name both jobs; both rows say the remote is no longer in the rclone configuration; no new notice on Refresh; Run still asks you to pick a remote.
Result: ____

---
## 5. Mount editor and cache
WARNING (M4): copies over 1 GB through a real drive. Use a test remote and check free disk space.

**M1. Editor layout.** Steps: Edit Mount in light and dark themes. Click Browse.
Expected: new rows (Cache folder, Max cache size, Keep disk free) fit; hints "e.g. 10G, 500M, off. A bare number is KiB." and "e.g. 20G. A bare number is KiB." are readable and not cut off; Browse picks a folder.
Result: ____

**M2. Validation.** Steps: Max cache size `10GB`, OK. Then `10GIB`, OK. Then `10G`, OK; reopen.
Expected: `10GB` and `10GIB` show a red message, the dialog stays, focus moves to the invalid box. `10G` saves and is kept on reopen (`10GiB`, `10gib`, `off` also accepted).
Result: ____

**M3. 125% scale (eyeball).** Steps: Windows display scale 125%, open Edit Mount (the form is 540 px tall).
Expected: nothing clipped, OK/Cancel reachable. UNCLEAR: reports do not say what a failure looks like beyond clipping, or what screen height is needed.
Result: ____

**M4. Cache cap.** Steps: Max cache size `1G`, mount, copy more than 1 GB through the drive.
Expected: cache folder stays near 1 GB (rclone says the cap is not a hard limit).
Result: ____

---
## 6. Drive banner (Task 18)
WARNING (D5): Reconnect re-authorises a real Google account. Use a test remote.

**D1. Banner shows only when it should.** Steps: select Drive and Google Photos remotes with no client_id; then a Drive remote with its own client_id, a service-account Drive, and a non-Google remote.
Expected: amber banner under the header only on the first two kinds. No banner on the others.
Result: ____

**D2. Link target.** Steps: click "Making your own client_id" on a Drive remote, then on a Photos remote.
Expected: browser opens the rclone Drive section, then the Photos section.
Result: ____

**D3. Text fits.** Steps: look at the banner at default width, then narrow the window and raise the Windows text scale, in light and dark themes.
Expected: 3 to 4 lines at default width; banner grows with the wrapped text and the last line is never clipped.
Result: ____

**D4. Live theme switch.** Steps: with the banner showing, switch theme (Settings or Windows dark/light). Look at the banner link and the About tab links.
Expected: link is readable in dark mode and both follow the new theme's colours at once.
Result: ____

**D5. Fix flow.** WARNING: see above. Steps: Edit the remote, paste client_id and secret, save, Reconnect.
Expected: banner disappears after refresh. UNCLEAR: reports did not check in a running UI that the editor shows client_id/client_secret fields for Drive/Photos.
Result: ____

---
## 7. Dir cache and vfs/refresh
**R1. Per-backend cache flags.** Steps: mount a Google Drive or OneDrive remote and an S3 or WebDAV remote. In Task Manager Details (add Command line column) read each rclone.exe command line.
Expected: Drive/OneDrive: `--dir-cache-time 1000h --poll-interval 15s`. S3/WebDAV: `--dir-cache-time 15s`.
Result: ____

**R2. New folder shows after a job.** Steps: mount a remote. Run a Copy job into a folder that does not exist yet on it. Watch Explorer without pressing F5. Then read the activity log.
Expected: the folder chain down to the job's target appears when the job ends. Deeper folders update at the next change check or 15 s. No "Could not refresh mount" line.
Result: ____
