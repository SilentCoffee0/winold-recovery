# M0 spikes pending external or elevated testing

The M0 tag `v0.1.0-m0` must not be created until the checks below are completed
or explicitly accepted as blocked. CI and the tag-release workflow exist, but
they do not substitute for these tests.

The backup-privilege 100k FixtureGen self-check passed 15 Sep 2026 on this
development machine. The 200 MB VHDX preflight disk-full probe, 1400 MB runtime
disk-full pause/resume, published 1M `--scan` memory ceiling, published
mid-copy kill-and-resume, Explorer long-path `/select,`, and the FlaUI
scan→purge e2e on a browsed TEMP fixture also passed. SignPath and clean-VM
`cleanmgr`/startup still need signing credentials or a disposable Windows 11
VM. The current agent process is
not elevated (`S-1-16-8192`); the passing runs were launched through UAC
`RunAs` where elevation was required.

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
cleanup command was run here. On a disposable VM with a setup-created
`Windows.old`, run elevated:

`$env:WOR_CLEANMGR_CONFIRM='SETUP_CREATED_WINDOWS_OLD'; powershell -File tools/run-cleanmgr-spike.ps1`

The script refuses without that exact confirm string, arms only
`Previous Installations\StateFlags0777` (sage 777, same as `PurgeExecutor`),
runs `cleanmgr /sagerun:777`, polls folder disappearance, restores the flag,
and checks that a temp marker file and other `VolumeCaches` `StateFlags0777`
values are unchanged.

## Elevated WPF launch and startup measurement

Pending on a clean Windows 11 VM:

1. Launch both ReadyToRun and non-ReadyToRun application publishes.
2. Confirm UAC requests administrator elevation.
3. Confirm the empty Fluent window opens.
4. Confirm a session folder, schema-v1 database, and redacted log are created.
5. Measure cold and warm startup without pre-populated extraction caches.

On that clean VM, from an interactive desktop:

`powershell -File tools/run-startup-measure.ps1`

It publishes ReadyToRun and non-ReadyToRun x64 EXEs, clears `%TEMP%\.net\WinOldRecovery*`
extraction caches before each cold run, and records time until a main window
handle appears plus whether a session folder, `session.db`, and `log.txt` were
created. Numbers from this development machine (FlaUI 14.1 s cold / 1.0 s warm)
do not satisfy this row.

## Explorer long-path selection

Passed 15 Sep 2026 on this development machine (`tools/run-explorer-long-path.ps1`).
**The Explorer long-path row is complete.**

The probe created a 345-character file under `%TEMP%`, asked
`ExplorerSelect.BuildSelectArgument` for the `/select,` argument (same helper
Open Folder uses), launched `explorer.exe` with that argument, and confirmed
through `Shell.Application` that Explorer selected the nearest ancestor whose
path is 251 characters (limit 259). The leaf itself is longer than Explorer
accepts; the ancestor folder was highlighted. The probe then closed only that
Explorer window.

Evidence:

- Transcript: `%TEMP%\WinOldRecovery-explorer-longpath-f3b01ca150e84361b4df4dc92f248625.log`
- `Passed=true`, `SelectedLength=251`, `SelectedName=segment-0211-abcdefghijklmnopqrstuvwxyz`
- Leaf: 345 characters ending in `deep-file.txt`

Do not tag.

## Disk-full VHDX destination

Passed 15 Sep 2026 on this development machine (elevated `tools/run-disk-full-vhdx.ps1`). **8.3 preflight path is complete.**

Evidence:

- Transcript: `%TEMP%\WinOldRecovery-diskfull-9d1063d6ac094705a6509b402d932f7c.log`
- Probe: `Passed: true`, `Mode: PreflightBlocked`, `MarkerIntact: true`, `SourceUntouched: true`
- Volume free 196,231,168 bytes vs required 1,117,782,056 bytes (plan + 5 % + 1 GB I16 margin). Restore did not start and did not delete `keep-me.txt`.

Step 4 passed 15 Sep 2026 (`tools/run-runtime-disk-full-vhdx.ps1`). A 1400 MB VHDX passed I16 preflight (1.45 GB free vs 1.16 GB required), a filler then left 2 MB free, CopyTree paused `DiskFull`, the filler was deleted, and resume completed. `keep-me.txt` and the source were untouched.

Evidence:

- Transcript: `%TEMP%\WinOldRecovery-runtime-diskfull-8adf34fe4bd6482f8e045b659f267d62.log`
- Probe: `Passed: true`, `Mode: RuntimePausedThenResumed`, `PausedDiskFull: True`, `ResumedCompleted: True`, `MarkerIntact: True`, `SourceUntouched: True`

**8.3 preflight and runtime paths are complete.** Do not tag.

## Published kill-and-resume mid-copy

Passed 15 Sep 2026 on this development machine (`tools/run-kill-published-copy.ps1`). **8.4 headless path is complete.**

Evidence:

- Transcript: `%TEMP%\WinOldRecovery-kill-copy-e2b2930055f4487dab397a387aef55cc.log`
- First copy killed at **450 / 2000** files (pid 29248) via UAC `taskkill /PID` after Medium IL `Stop-Process` was denied
- Resume report: `Passed: true`, `Completed: True`, `DestinationFiles: 2000`, `PartialFiles: 0`
- Source/dest: `%TEMP%\WinOldRecovery-KillPub-e2b2930055f4487dab397a387aef55cc\`

The WPF Resume overlay was exercised by the FlaUI smoke below. Do not tag.

## FlaUI smoke on the published EXE

Overlay/Help/step navigation passed 15 Sep 2026. **Scan→decide→preview→restore→verify→purge
passed 16 Sep 2026** on this development machine (elevated testhost,
`tools/run-flaui-e2e.ps1`). **The FlaUI e2e row is complete.** It uses a browsed
TEMP `OldInstall` folder, refuses a volume-root `Windows.old*`, forces
PreferManualDelete, and never arms Previous Installations `cleanmgr`.
`C:\Windows.old` was left in place.

Evidence (scan→purge e2e):

- Transcript: `%TEMP%\WinOldRecovery-flaui-e2e-0a07126628c84f209a178dfbd6f18c3c.log`
- EXE: `%TEMP%\wor-publish-flaui\WinOldRecovery.exe` (single-file, self-contained, not ReadyToRun)
- Result: `Passed=true`, `failed: 0, succeeded: 1`, duration 23.6 s
- Manual purge of the six-item fixture ran on a background thread so the dispatcher could show `Purge finished`

WPF resume overlay after a mid-copy kill passed 16 Sep 2026 in the same elevated
`tools/run-flaui-e2e.ps1` run (`ResumeOverlay_AfterKilledPublishedRestore`).
Headless `--restore` was killed mid-copy, then the GUI showed
`ResumeInterruptedButton` with unfinished-item copy.

Evidence (overlay after kill):

- Transcript: `%TEMP%\WinOldRecovery-flaui-e2e-43bebc6bb91a48d5af0051ab245084e2.log`
- Result: `Passed=true`, `failed: 0, succeeded: 2`, duration 27.2 s

Evidence (overlay, 15 Sep 2026):

- Transcript: `%TEMP%\WinOldRecovery-flaui-elevated.txt`
- Result: `Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1, Duration: 6 s`

Two environment facts came out of the failures that preceded the pass:

- A Medium IL testhost cannot `Application.Attach` a `requireAdministrator` EXE (UIPI, `Win32Exception` 5). The test skips rather than fails in that case; the testhost must be elevated.
- The first launch of a freshly published single-file EXE took 14.1 s to show a window (bundle extraction plus an antimalware scan of ~130 MB); the next launch took 1.0 s. The window wait is now 5 minutes, overridable with `FLAUI_WINDOW_TIMEOUT_SECONDS`. These numbers are **not** the startup measurement below: this is not a clean VM and the caches were already warm.

## One-million-node scan memory ceiling

Passed 15 Sep 2026 on this development machine (published `WinOldRecovery.exe --scan`, not testhost). **8.2 is complete.**

Evidence:

- Report: `%TEMP%\WinOldRecovery-1m-scan-namefilter.txt`
- EXE: `%TEMP%\wor-publish-8.2-noprobe-largest\WinOldRecovery.exe` (pid 24672)
- Fixture: `%TEMP%\WinOldRecovery-1m-fixture-live` (`Users\Alice\Scale`, 1000×1000)
- `Passed: true`, `NodesVisited: 1001004`, `WalkSeconds: 46.198`, `ClassifySeconds: 3.940`, `ScanSeconds: 50.188`
- `PeakWorkingSetMiB: 103.7` (ceiling 1.5 GB), `TreePageRows: 1000` (page size 2000), `TreeParent: Scale`, `TreePageMilliseconds: 213.6`

Do not tag `v0.1.0-m0` until the other pending rows below pass.

## Attempt log — 15 Sep 2026

- Elevated `run-elevated-m0.ps1` failed at generate: `Child process 'net.exe' exceeded its 00:02:00 timeout` (script line 28). Cause: `net user /add` with a >14-character password prompts Y/N; redirected IO never answers. See `docs/spikes/NET_USER_ADD_PROMPT.md`.
- After removing `net.exe` and closing `ProcessRunner` stdin, two elevated 100k runs passed self-check (transcripts `WinOldRecovery-elevated-m0-3920d38c3bfe4dec894efb66a1d830ed.log` and `771b81a389904c61ab69a0c82a7c5809.log`). **8.1 passed.**
- Integrity: Medium (`S-1-16-8192`) for the agent console; the passing generate ran in an Administrator window (UAC RunAs).

## Attempt log — 15 Sep 2026 (1M published `--scan`)

Passed. Earlier hangs were classify (path-glob regex + `FileSystemName` on every `.txt`) and post-classify `GetLargest` mixed-decision CTEs. After those fixes, pid 24672 wrote `%TEMP%\WinOldRecovery-1m-scan-namefilter.txt` with `Passed: true` (103.7 MiB peak, 50.2 s scan). **8.2 passed.**

## Attempt log — 14 Sep 2026

Recorded from the development console session. This is not a pass.

- Integrity: Medium (`S-1-16-8192`). `BUILTIN\Administrators` is present as a deny-only SID. `net session` failed. The agent is **not elevated**.
- Interactive desktop: console logon for user `VJ` is present, but launching the `requireAdministrator` EXE still needs a UAC consent that this Medium IL process cannot complete by itself. FlaUI smoke uses `ProcessStartInfo.Verb = runas` when `RUN_FLAUI=1`.
- Explorer long paths: code selects the nearest ancestor whose path is at most 259 characters (and strips `\\?\`). Interactive confirmation passed 15 Sep 2026 (`tools/run-explorer-long-path.ps1`, transcript `%TEMP%\WinOldRecovery-explorer-longpath-f3b01ca150e84361b4df4dc92f248625.log`): Explorer selected the 251-character ancestor of a 345-character leaf.
- Crash-resume overlay: unit tests cover journal detection and the Resume prompt. Integration kills `RestoreHarness` mid-CopyTree of 50k files. Headless published kill-and-resume passed 15 Sep 2026 (450/2000 files killed, resume `Passed: true`). The WPF overlay was then dismissed by the FlaUI smoke on an elevated interactive desktop the same day.
- SignPath / Trusted Signing: no signing identity, API token, or `SIGNPATH_ENABLED` variable is configured. `release.yml` already gates SignPath on that variable; without it, release assets stay unsigned.
- `cleanmgr` and a setup-created Windows.old were not run. Use `tools/run-cleanmgr-spike.ps1` on a disposable VM (`WOR_CLEANMGR_CONFIRM=SETUP_CREATED_WINDOWS_OLD`). The 1,000,000-node published `--scan` later passed on 15 Sep 2026.

To run the backup-privilege FixtureGen spike, open an elevated PowerShell in the repo and run `tools/run-elevated-m0.ps1`. To run FlaUI scan→purge, from an **elevated** PowerShell (a Medium IL testhost cannot attach to the elevated GUI):

`powershell -File tools/run-flaui-e2e.ps1`
