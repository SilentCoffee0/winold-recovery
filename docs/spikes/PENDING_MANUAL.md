# M0 spikes pending external or elevated testing

The M0 tag `v0.1.0-m0` must not be created until the checks below are completed
or explicitly accepted as blocked. CI and the tag-release workflow exist, but
they do not substitute for these tests.

These checks are not marked passed. The current agent process is not elevated
and no disposable clean Windows 11 VM is attached.

## Backup-privilege enumeration and read

Pending:

1. Run full elevated `FixtureGen` on NTFS with the default 100,000-file count.
2. Confirm the self-check creates the deny-ACL and orphan-SID hazards.
3. Enumerate both directories using `FileSystemEnumerator` after
   `Privileges.EnableBackupAndRestore()`.
4. Read their protected files through `BackupFile.OpenRead`.
5. Confirm a source watcher reports no changes.

Portable fixture tests do not create these privileged hazards and therefore do
not satisfy this spike.

## Windows Previous Installations cleanup handler

Pending on a disposable Windows 11 25H2 VM containing a real setup-created
`Windows.old`:

1. Set only the test `StateFlags` value for `Previous Installations`.
2. Run `cleanmgr /sagerun` for that flag.
3. Record process-exit timing and poll folder disappearance.
4. Confirm unrelated cleanup categories and files are untouched.
5. Remove the temporary `StateFlags` value.

A fake folder on the development machine is not an adequate safety test, so no
cleanup command was run here.

## Elevated WPF launch and startup measurement

Pending on a clean Windows 11 VM:

1. Launch both ReadyToRun and non-ReadyToRun application publishes.
2. Confirm UAC requests administrator elevation.
3. Confirm the empty Fluent window opens.
4. Confirm a session folder, schema-v1 database, and redacted log are created.
5. Measure cold and warm startup without pre-populated extraction caches.

## Explorer long-path selection

Pending interactive test:

1. Use a fixture path longer than 260 characters.
2. Run the application's eventual Open Containing Folder action.
3. Confirm whether `explorer.exe /select,` selects it.
4. If not, implement and test opening the nearest shorter ancestor.

## Disk-full VHDX destination

Pending elevated test:

1. Attach a 200 MB VHDX as the restore destination.
2. Start a restore larger than free space.
3. Confirm the job pauses, Windows.old is untouched, and already-copied destination files are not deleted to make room.
4. Free space and resume.

The unit suite maps `ERROR_DISK_FULL` (112) to `Paused(DiskFull)` and is not a substitute for this volume test.

## One-million-node scan memory ceiling

Pending on a machine with a large scratch volume:

1. Generate a 1,000,000-node fixture (or walk a tree of that size).
2. Scan with the published EXE.
3. Confirm working set stays under 1.5 GB and the Files tree stays responsive (SQLite paging, 2,000 children per page).
4. Record peak working set and duration.

CI expands a 100k-child node in under 300 ms and pages 1M synthetic SQLite children under 1.5 GB. It does not generate a million on-disk files or scan them with the published EXE.

## Attempt log — 14 Sep 2026

Recorded from the development console session. This is not a pass.

- Integrity: Medium (`S-1-16-8192`). `BUILTIN\Administrators` is present as a deny-only SID. `net session` failed. The agent is **not elevated**.
- Interactive desktop: console logon for user `VJ` is present, but launching the `requireAdministrator` EXE still needs a UAC consent that this Medium IL process cannot complete by itself. FlaUI smoke uses `ProcessStartInfo.Verb = runas` when `RUN_FLAUI=1`.
- Explorer long paths: code now selects the nearest ancestor whose path is at most 259 characters (and strips `\\?\`). Clicking the result in Explorer is still pending.
- Crash-resume overlay: unit tests cover journal detection and the Resume prompt. Relaunching the published elevated EXE after killing a restore mid-copy is still pending on an interactive desktop.
- SignPath / Trusted Signing: no signing identity, API token, or `SIGNPATH_ENABLED` variable is configured. Release assets stay unsigned.
- `cleanmgr`, 200 MB VHDX, and a 1,000,000-node tree were not run.

To run the backup-privilege FixtureGen spike, open an elevated PowerShell in the repo and run `tools/run-elevated-m0.ps1`. To run FlaUI, publish the x64 EXE, approve UAC, then:

`$env:RUN_FLAUI=1; $env:WINOLD_RECOVERY_EXE='<published exe>'; dotnet test tests/WinOldRecovery.App.Tests -c Release --filter FlaUiSmokeTests`
