# Third-Party Notices

RcloneMount orchestrates two third-party tools that are installed separately on
the user's system. It invokes them as external processes; it does not link,
bundle, or modify their code.

## rclone

- Home: https://rclone.org/
- Source: https://github.com/rclone/rclone
- License: MIT License
- Copyright © the rclone authors and contributors
- License text: https://github.com/rclone/rclone/blob/master/COPYING

RcloneMount launches `rclone.exe` as a child process to perform mount, config,
and stats operations. When rclone.exe is not installed, RcloneMount can offer
to download the official release from rclone's GitHub releases and install it
locally to the user's machine. RcloneMount does not modify rclone.

## WinFsp

- Home: https://winfsp.dev/
- Source: https://github.com/winfsp/winfsp
- License: GPL v3 (a commercial licence is also available from the author)
- Copyright © Bill Zissimopoulos
- License text: https://github.com/winfsp/winfsp/blob/master/License.txt

WinFsp is the Windows file-system driver rclone uses to expose a mount as a
drive letter. RcloneMount detects WinFsp on the system and, when it is missing,
offers to launch the official WinFsp MSI installer. RcloneMount does not
bundle, redistribute, or modify WinFsp binaries or code. rclone itself is the
component that interfaces with WinFsp; RcloneMount only communicates with
rclone.

## Note on GPL v3 and derivative works

Because RcloneMount does not link to or modify WinFsp and does not distribute
WinFsp binaries, it is not a derivative work of WinFsp under the GPL. The user
installs WinFsp separately, and its own licence continues to govern its use.
This aligns with the Free Software Foundation's treatment of subprocess use as
"mere aggregation" rather than combination.
