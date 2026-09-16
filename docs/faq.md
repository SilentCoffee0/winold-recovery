# FAQ — recover files from Windows.old

## What is Windows.old?

After you reinstall, reset, or upgrade Windows, the previous installation is kept in `C:\Windows.old` for about ten days. Explorer can copy files out of it. WinOld Recovery scans it, classifies what is valuable, restores what you choose, verifies the copies, and only then offers a gated purge.

## How do I recover files from Windows.old?

1. Run WinOld Recovery as administrator.
2. Scan the `Windows.old` folder.
3. Mark Desktop, Documents, and apps Restore for this pass, or Later (Undecided) if you might want them afterwards. Leave Behind does **not** delete anything.
4. Preview the plan. Existing files are kept; restored copies get a `(from Windows.old)` suffix unless you confirm overwrite per file.
5. Restore, then Verify.
6. Purge only after verify and after typing the folder name.

## Can I recover Chrome passwords after a Windows reinstall?

No. Chrome and Edge encrypt passwords with keys that only existed on the old installation. Sign in to Chrome or Edge sync if it was enabled. Firefox passwords can come back with the profile transplant. See [browser-passwords.md](help/browser-passwords.md).

## How do I restore a WSL distro from Windows.old?

The `ext4.vhdx` is copied out of Windows.old and the header is checked. Registration with `wsl --import-in-place` is optional and never runs against the file still inside Windows.old. See [wsl.md](help/wsl.md).

## How do I restore Syncthing after a reinstall?

Restore the device certificate and a rewritten `config.xml` with every folder paused. The index is not copied. See [syncthing-identity.md](help/syncthing-identity.md).

## How do I delete Windows.old safely?

Do not use Explorer “Delete” first. Finish restore and verify, then use the Purge step. The tool prefers Windows Disk Cleanup (Previous Installations) and falls back to a reparse-aware delete. Session logs stay in `%LOCALAPPDATA%\WinOldRecovery\sessions`.

## Why does the EXE ask for administrator?

Files in Windows.old still belong to the old user SID. Backup privilege is required to read them. The source folder stays read-only until Purge.
