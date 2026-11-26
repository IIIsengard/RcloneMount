# Permanent delete (skip the recycle bin), per job

Date: 2026-11-26. Status: waiting for approval.

## Why

Sync, Move and replaced files go to the service's bin (MEGA rubbish bin, Google Drive trash)
and can keep using storage. The user wants the choice MEGA offers: delete for good.

rclone already has the setting per service. In the app it is only reachable through a remote's
advanced options (every use of that remote) or by typing a flag into "Extra rclone flags".

## Goals

- One checkbox on a transfer job: **Delete permanently (skip the recycle bin)**.
- Off by default. Nothing changes for existing jobs.
- The user always knows whether it applies to the services in that job.

## Not goals

- Emptying bins that already hold files.
- Changing a remote's own settings.
- Mounts (deleting in Explorer still follows the remote's settings).
- Bisync (see Constraints).

## Constraints (checked with rclone 1.76.0)

| Service type | How |
|---|---|
| mega, onedrive, b2, jottacloud, yandex, drime, quatrix, sugarsync | `--<type>-hard-delete` |
| drive, pikpak | `--<type>-use-trash=false` |
| sftp, local disk | already permanent, nothing to add |
| every other type, including dropbox | rclone offers no option |

- Passing such a flag (or its environment variable) to **bisync** changes the name rclone files
  the bisync history under. Tested on temp folders: the next run stops with "Must run --resync
  to recover". A resync can delete or bring back files, so the checkbox is not offered for bisync.
- The flags apply to every remote of that type within one run. This is harmless here: a run only
  deletes on the sides listed below.
- `crypt`, `alias`, `union` and other wrappers delete through the service underneath, so the
  type is taken from the remote underneath.

## Approaches

| | Approach | Trade-off |
|---|---|---|
| **A (chosen)** | Checkbox per job | Risk limited to the job; each job must be ticked |
| B | Setting per remote | One click per service, but affects every job and mount using it |
| C | Both | Most flexible, two places to check why a file is gone |

## Behaviour

**Where it deletes**

| Operation | Side that matters | What is deleted |
|---|---|---|
| Copy | destination | older versions of replaced files |
| Sync | destination | extra files and replaced versions |
| Move | source and destination | moved originals and replaced versions |
| Check, CryptCheck, Bisync | none | checkbox hidden |

**Dialog.** The checkbox is enabled when at least one side that matters supports the option.
A line under it says which service it applies to, and names any side where it does not
("Dropbox has no permanent delete in rclone"; "SFTP already deletes permanently").
When no side supports it, or the type cannot be determined, the box is disabled with the reason.

**Confirmation.** Start (not dry-run) asks: files deleted at <place> cannot be recovered.
This joins the existing Sync and Move warnings, and adds one for Copy. Saving a job that has
both this box and a schedule asks the same, because scheduled runs ask nothing.

**Jobs list.** Shows "· permanent delete" on such jobs.

**Running.** The saved choice is used by manual, scheduled, resumed and retried runs. If a
remote has since changed to a type without the option, the run deletes the normal
(recoverable) way and writes a warning to the activity log.

**Saved data.** New job field `HardDelete`, default false. Older app versions ignore it and
delete the recoverable way.

**Credit.** Ticking or unticking counts as an unsaved change, like dry-run.

## Left to the builder

Exact wording and layout; where the type-to-flag table lives.

## Done when

- Tests cover the flags per type and operation, wrappers, bisync and check excluded, the saved
  field reaching scheduled and retried runs, and both confirmations.
- A manual check is added to `docs/MANUAL-CHECKS.md`: one real Sync on a test folder, then the
  service's bin is inspected. Flags cannot be proven against MEGA or Drive in automated tests.
