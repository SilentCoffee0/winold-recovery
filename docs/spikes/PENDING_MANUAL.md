# M0 spikes pending external or elevated testing

The M0 tag `v0.1.0-m0` must not be created until the checks below are completed
or explicitly accepted as blocked. CI and the tag-release workflow exist, but
they do not substitute for these tests.

The backup-privilege 100k FixtureGen self-check passed 15 Sep 2026 on this
development machine. The remaining rows still need a disposable Windows 11 VM,
an interactive desktop, or signing credentials. The current agent process is
not elevated (`S-1-16-8192`).

## Backup-privilege enumeration and read

Passed 15 Sep 2026 on this development machine (not a clean VM). Two elevated
`tools/run-elevated-m0.ps1` runs (100,000 `node_modules` files) printed
`Elevated FixtureGen and self-check passed.` Build SHA `d3a17e1`.

Evidence:

- Transcript: `%TEMP%\WinOldRecovery-elevated-m0-771b81a389904c61ab69a0c82a7c5809.log`
- Fixture: `%TEMP%\WinOldRecovery-elevated-fixture-771b81a389904c61ab69a0c82a7c5809`
- Earlier same-day pass: `%TEMP%\WinOldRecovery-elevated-m0-3920d38c3bfe4dec894efb66a1d830ed.log`
- Manifest: `FullHazardsRequested: true`, `NodeModulesFileCount: 100000`, deny-ACL / EFS / orphan-SID (`S-1-5-21-2147483647-1-1-1001`) all `Created`.
- Self-check enabled backup privilege, listed deny-ACL and orphan-SID children with `FileSystemEnumerator`, read `protected.txt` through `BackupFile.OpenRead`, and armed a source `FileSystemWatcher`.

Portable fixture tests still do not substitute for this spike. **8.1 is complete.** Do not tag `v0.1.0-m0` until the other pending rows below pass.

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

To run the volume test, open an elevated PowerShell in the repo and run `tools/run-disk-full-vhdx.ps1`. It attaches a 200 MB VHDX, seeds `keep-me.txt`, and runs `RestoreHarness --probe-disk-full`. Preflight includes a 1 GB margin, so a 200 MB volume is expected to block before copy (`Mode: PreflightBlocked`) while leaving the marker and source files untouched.

## One-million-node scan memory ceiling

Pending on a machine with a large scratch volume:

1. Generate a 1,000,000-node fixture (or walk a tree of that size).
2. Scan with the published EXE.
3. Confirm working set stays under 1.5 GB and the Files tree stays responsive (SQLite paging, 2,000 children per page).
4. Record peak working set and duration.

CI expands a 100k-child node in under 300 ms, pages 1M synthetic SQLite children under 1.5 GB, and walks 1,000,000 on-disk empty files in the test process under 60 s / 1.5 GB. The published EXE probe is `tools/run-1m-scan.ps1` (`WinOldRecovery.exe --scan`). That check is not passed until the report file contains `Passed: true`.

## Attempt log — 15 Sep 2026

- Elevated `run-elevated-m0.ps1` failed at generate: `Child process 'net.exe' exceeded its 00:02:00 timeout` (script line 28). Cause: `net user /add` with a >14-character password prompts Y/N; redirected IO never answers. See `docs/spikes/NET_USER_ADD_PROMPT.md`.
- After removing `net.exe` and closing `ProcessRunner` stdin, two elevated 100k runs passed self-check (transcripts `WinOldRecovery-elevated-m0-3920d38c3bfe4dec894efb66a1d830ed.log` and `771b81a389904c61ab69a0c82a7c5809.log`). **8.1 passed.**
- Integrity: Medium (`S-1-16-8192`) for the agent console; the passing generate ran in an Administrator window (UAC RunAs).

## Attempt log — 15 Sep 2026 (1M published `--scan`)

Not a pass. A glob-cache published EXE (`%TEMP%\wor-publish-1m-globcache\WinOldRecovery.exe`, pid 23244, started 17:30) is still running after 20+ minutes at ~380 MB working set with no `%TEMP%\WinOldRecovery-1m-scan-globcache.txt`. Session `2026-09-15_173025_45d5e4d9` has a ~360 MB `session.db`. Leftover elevated EXEs from earlier hung `--scan` attempts (pids 3420, 10968, 15528) are still alive; Medium IL cannot stop them.

Do not mark the 1M memory ceiling complete until the report contains `Passed: true`.

## Attempt log — 14 Sep 2026

Recorded from the development console session. This is not a pass.

- Integrity: Medium (`S-1-16-8192`). `BUILTIN\Administrators` is present as a deny-only SID. `net session` failed. The agent is **not elevated**.
- Interactive desktop: console logon for user `VJ` is present, but launching the `requireAdministrator` EXE still needs a UAC consent that this Medium IL process cannot complete by itself. FlaUI smoke uses `ProcessStartInfo.Verb = runas` when `RUN_FLAUI=1`.
- Explorer long paths: code now selects the nearest ancestor whose path is at most 259 characters (and strips `\\?\`). Clicking the result in Explorer is still pending.
- Crash-resume overlay: unit tests cover journal detection and the Resume prompt. Integration kills `RestoreHarness` mid-CopyTree of 50k files. Relaunching the published elevated EXE after killing a restore mid-copy is still pending on an interactive desktop.
- SignPath / Trusted Signing: no signing identity, API token, or `SIGNPATH_ENABLED` variable is configured. Release assets stay unsigned.
- `cleanmgr`, 200 MB VHDX, and a 1,000,000-node tree were not run.

To run the backup-privilege FixtureGen spike, open an elevated PowerShell in the repo and run `tools/run-elevated-m0.ps1`. To run FlaUI, publish the x64 EXE, approve UAC, then:

`$env:RUN_FLAUI=1; $env:WINOLD_RECOVERY_EXE='<published exe>'; dotnet test tests/WinOldRecovery.App.Tests -c Release --filter FlaUiSmokeTests`
