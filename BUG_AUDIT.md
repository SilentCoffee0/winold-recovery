# Bug audit — 14 Sep 2026

Adversarial review of WinOld Recovery against SAFETY_MODEL I1–I16. Automated tests
were used as evidence, not as proof that the product is safe. Elevated and real
Windows.old checks in `docs/spikes/PENDING_MANUAL.md` remain **not passed**.
This document does **not** authorize tagging `v0.1.0` or `v0.1.0-m0`.

## Bugs found and fixed

### P0 — `.gitignore` `sessions/` hid Core.Sessions source

- **Location:** `.gitignore` had a bare `sessions/` rule meant for user session data.
- **Reproduction:** Git treats that as any path component named `sessions`. `SessionWorkspace.cs`, `SessionRecordExport.cs`, and `SessionWorkspaceTests.cs` existed on disk and compiled locally but were never committed. A clean clone would fail to build `SessionWorkspace`.
- **Fix:** Ignore only `/sessions/` and `**/WinOldRecovery/sessions/` (recovery data), then add the Core.Sessions source and tests.
- **Test:** files appear in `git ls-files src/WinOldRecovery.Core/Sessions`.

### P0 — `cleanmgr /sagerun:777` without Previous Installations StateFlags

- **Location:** `src/WinOldRecovery.Core/Purge/PurgeExecutor.cs` (before this audit).
- **Reproduction:** Prefer Disk Cleanup, run purge. The executor launched
  `cleanmgr.exe /sagerun:777` without writing
  `HKLM\...\VolumeCaches\Previous Installations\StateFlags0777=2`. Sage 777 could
  be empty, leftover from an earlier Disk Cleanup session, or include unrelated
  categories.
- **Invariant:** I10 / SAFETY_MODEL §4 / ARCHITECTURE § purge — only Previous
  Installations may be armed.
- **Fix:** `ICleanupSage` / `RegistryCleanupSage` arms that value, runs cleanmgr
  only if arming succeeded, then disarms. If arming fails, purge falls back to
  the reparse-aware manual delete.
- **Test:** `Cleanmgr_IsNotInvokedWhenPreviousInstallationsFlagCannotBeArmed`;
  existing preferred-handler test injects an armed sage; `SageFlagName_PadsToFourDigits`.

### P0 — resume leftover scan followed destination junctions

- **Location:** `CopyEngine.DeletePartial` used `Directory.EnumerateFiles(..., SearchOption.AllDirectories)`.
- **Reproduction:** Destination tree contains a junction to another folder that
  holds `trap.winold-partial`. Resume a `Started` CopyTree. The old enumerator
  followed the junction and deleted the trap file.
- **Invariant:** I11 (do not follow reparse points) applied to destination
  cleanup; otherwise files outside the restore tree can be deleted.
- **Fix:** Walk destination directories the same way as source enumeration:
  no recursion into reparse points.
- **Test:** `DeletePartial_DoesNotFollowDestinationJunctions`.

### P1 — keep-both verify inspected the preexisting file

- **Location:** `Verifier.VerifyAsync` called `KeepBothPath`, which returns the
  *next free* name, and preferred `File.Exists(plannedDest)` even when that file
  was the user's original.
- **Reproduction:** Destination `note.txt` already has different content; restore
  Keep Both writes `note (from Windows.old).txt`. Verify hashed the original
  `note.txt`, failed L2, blocked purge after a successful restore. Same-size
  preexisting files could also false-pass L1 for large files.
- **Invariant:** I9 (verify the restored bytes) and I2 (keep-both must not be
  treated as overwrite).
- **Fix:** `CopyEngine.FindRestoredPath` prefers keep-both candidates that match
  source size/time before the planned destination. L0 now requires
  `LooksLikeSuccessfulCopy`, not merely that some file exists.
- **Test:** `Verifier_KeepBoth_ChecksTheRestoredCopyNotThePreexistingFile`;
  `Verifier_DoesNotPassWhenOnlyThePreexistingDestinationExists`.

### P1 — CopyTree resume duplicated already-copied files

- **Location:** `CopyEngine.CopyOneFile` Keep Both whenever the destination
  existed, including after a crash with journal `Started` and complete files.
- **Reproduction:** CopyTree writes `Desktop\a.txt`, crash before `Completed`,
  resume. Second run created `a (from Windows.old).txt`.
- **Invariant:** I15 idempotent resume; I2 keep-both only for a true conflict.
- **Fix:** Resume skips a planned destination that already matches. First-run
  Keep Both still copies when the existing dest matches size/time but not
  content (do not treat the user's file as "already restored").
- **Test:** `Resume_CopyTree_DoesNotDuplicateAlreadyCopiedFiles`;
  `KeepBoth_DoesNotTreatMatchingSizeTimeExistingDestAsAlreadyCopied`.

### P1 — restore reported completed when items Failed

- **Location:** `RestoreRunner.RunAsync` always returned `Completed: true` after
  the loop unless disk-full paused.
- **Reproduction:** Delete the source after planning; copy journals `Failed`;
  runner still said completed. UI could offer Verify as if the job finished.
- **Invariant:** restore state must match the journal (no fake success).
- **Fix:** `Completed` only if every item is `Completed` or `Skipped`. Cancel
  clears `restoreCompleted`.
- **Test:** `RestoreRunner_FailedItem_IsNotReportedCompleted`.

### P1 — purge gates trusted UI booleans only

- **Location:** `ShellViewModel.ExecutePurgeAsync` passed in-memory
  `verifyCompleted` into `PurgeAuthorization`. Journal `Started`/`Failed` was
  not consulted. `PurgeAuthorization.IsInsideSource` treated canonicalize
  failures as “not inside source”. New journal/verify-store fields defaulted
  to **true**, so callers that omitted them were authorized.
- **Reproduction:** Construct `PurgeGateRequest` without the last two fields, or
  unlock-style UI true with no stored verify rows / leftover `Started` journal.
- **Invariant:** I10 — the service must reject unverified / unfinished restore,
  not only the step bar. Fail closed.
- **Fix:** `SessionDb.LastVerifyReportAllOk` and `RestoreJournalSettled`; extra
  gates `verify-store` and `journal` default **false**; containment checks also
  use lexical paths and fail closed on unparseable paths.
- **Test:** `EachGateIndividuallyBlocks` covers `journal`, `verify-store`, and
  omitted defaults.

## Tests added

- `CopyEngineTests.Resume_CopyTree_DoesNotDuplicateAlreadyCopiedFiles`
- `CopyEngineTests.DeletePartial_DoesNotFollowDestinationJunctions`
- `CopyEngineTests.Verifier_KeepBoth_ChecksTheRestoredCopyNotThePreexistingFile`
- `CopyEngineTests.RestoreRunner_FailedItem_IsNotReportedCompleted`
- `PurgeExecutorTests.Cleanmgr_IsNotInvokedWhenPreviousInstallationsFlagCannotBeArmed`
- `PurgeAuthorizationTests` journal / verify-store cases
- `CopyEngineTests.KeepBoth_DoesNotTreatMatchingSizeTimeExistingDestAsAlreadyCopied`
- `CopyEngineTests.Verifier_DoesNotPassWhenOnlyThePreexistingDestinationExists`
- `CopyEngineTests.SageFlagName_PadsToFourDigits`
- `PurgeAuthorizationTests` omitted journal/verify-store defaults fail closed
- Preview summary tests from the in-flight M7 tenth slice (still present)
- `CopyEngineTests.UserPause_StopsBeforeLaterPlanItems_AndResumeCopiesTheRest`
- `CopyEngineTests.Resume_CopyTree_AfterPaused_DoesNotKeepBothAlreadyCopiedFiles`

### P1 — resume after `Paused` treated the tree as a first run

- **Location:** `CopyEngine.CopyAsync` set `resume` only when the latest journal was `Started`.
- **Reproduction:** Disk-full or user Pause journals `Paused`. Resume copied already-written tree files as Keep Both.
- **Fix:** Treat `Started` and `Paused` as resume; delete leftover `.winold-partial` for both.
- **Test:** `Resume_CopyTree_AfterPaused_DoesNotKeepBothAlreadyCopiedFiles`.

Deliberate breakage: restoring `Directory.EnumerateFiles(..., SearchOption.AllDirectories)` in
`DeletePartial` makes `DeletePartial_DoesNotFollowDestinationJunctions` fail (trap file under a
destination junction is deleted). That mutation was applied, observed red, then reverted.

## Unresolved risks (not claimed fixed)

- L2 hashing is skipped for files larger than 64 MB; keep-both matching uses
  size+mtime, not a hash, so identical size/time collisions remain possible.
- `PurgeToken` is minted only when `PurgeAuthorization.Evaluate` sees a settled
  journal and verify store on `SessionDb` for that session id. Caller-supplied
  `true` flags no longer authorize those two gates. `PurgeToken`'s constructor
  stays `internal` (Core tests can still construct one for executor/source-guard
  fixtures).
- `RegistryCleanupSage` cannot be proven on this Medium IL console (no HKLM
  write). Real `cleanmgr` on a setup-created `Windows.old` is still pending.
- Kill-process CopyTree of 50,000 files is covered in the integration suite
  (`tools/RestoreHarness`). Published elevated EXE mid-copy kill-and-resume
  passed 15 Sep 2026 (`tools/run-kill-published-copy.ps1`, 450/2000 then resume
  2000 files, `%TEMP%\WinOldRecovery-kill-copy-e2b2930055f4487dab397a387aef55cc.log`).
  The WPF Resume overlay was dismissed by the FlaUI smoke on 15 Sep 2026.
- FlaUI overlay smoke passed 15 Sep 2026 against the published EXE from an elevated
  testhost (`%TEMP%\WinOldRecovery-flaui-elevated.txt`). The same test now drives
  scan→purge on a browsed TEMP fixture (`WINOLD_RECOVERY_SMOKE_SOURCE` /
  `WINOLD_RECOVERY_SMOKE_DEST`, PreferManualDelete) and refuses a volume-root
  `Windows.old*`. It stays skipped without `RUN_FLAUI=1`, and skips instead of
  failing when a Medium IL testhost is blocked by UIPI. The elevated scan→purge
  pass is still required (`tools/run-flaui-e2e.ps1`).
- Elevated deny-ACL FixtureGen, the 200 MB VHDX *preflight* disk-full probe, and
  the 1400 MB runtime disk-full pause/resume passed 15 Sep 2026. Published 1M-node
  `--scan` memory passed the same day (`Passed: true`, 103.7 MiB peak, report
  `%TEMP%\WinOldRecovery-1m-scan-namefilter.txt`). SignPath and the rest: see
  `docs/spikes/PENDING_MANUAL.md`.

## Still needs a disposable Windows 11 VM

Everything listed in `PENDING_MANUAL.md`, plus: arm `StateFlags0777`, run
`cleanmgr /sagerun:777` against a **setup-created** Windows.old, confirm unrelated
cleanup categories stay off, then disarm. Confirm the WPF interrupt overlay on
an elevated relaunch after a mid-copy kill (headless `--restore` resume already
passed).

Do not treat a green unit suite as a safety sign-off.
