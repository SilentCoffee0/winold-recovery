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
