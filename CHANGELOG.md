# Changelog

All notable changes to WinOld Recovery will be documented here.

## Unreleased

### Fixed

- Keep-both verification hashed the restored copy instead of the preexisting destination; CopyTree resume no longer Keep-Boths files that already match; leftover `.winold-partial` cleanup no longer follows destination junctions; `RestoreRunner.Completed` is false when any item Failed; purge consults stored verify rows and the copy journal and fails closed if those fields are omitted; `cleanmgr /sagerun:777` runs only after Previous Installations `StateFlags0777` is armed. See `BUG_AUDIT.md`.
- CopyTree and verify skip the same Offline/EFS/reparse entries, so a restore of a mixed folder can complete and verify without requiring cloud placeholders.
- `.gitignore` no longer matches `src/.../Sessions` source; session workspace and session-record export are tracked.

### Added

- Milestone 0 .NET 10 solution and project dependency structure.
- Empty WPF shell using the system Fluent theme.
- Administrator, long-path-aware, PerMonitorV2 application manifest.
- Self-contained x64 single-file ReadyToRun publish profile.
- Backup and restore privilege enablement at application startup.
- Backup-mode source reads that refuse reparse-backed, offline, and encrypted files.
- Canonicalized, reparse-aware source-root write protection through `SourceGuard` and `SafeFs`.
- Root-scoped purge authorization and source-safety unit tests.
- SQLite session schema v1 covering scan, decision, plan, journal, and verification state.
- WAL-backed single-writer database channel, read-only query connections, and migration scaffold.
- Synthetic Windows.old fixture generator with a self-check, canary recipe shells, reparse/path hazards, and explicit elevated-only hazards.
- Central child-process runner with argument-list, timeout, cancellation, and output capture support.
- Extended-path handling that preserves trailing-space names instead of normalizing them onto another path.
- Dated per-run session workspace with database, log, exports, and temporary directories.
- Rolling structured text logger with sensitive-path and secret-literal redaction.
- Application startup initialization of privileges, session state, and an auditable untouched-source log entry.
- Offline registry parsing from a session-local copy made with backup-mode reads.
- Single-file dependency self-test proving bundled Registry and SQLite operation.
- GitHub Actions CI workflow that builds, tests, and uploads an unsigned x64 single-file artifact.
- Tag-triggered release workflow that attaches the executable and a SHA-256 checksum.
- Read-only Windows.old source discovery across fixed volumes, numbered `Windows.old*` variants, old system volumes, and browsed folders.
- Reparse-aware `FileSystemWalker` that records junctions/symlinks as leaves, classifies cloud/EFS/long-path/invalid-name problems, stores post-order aggregates, and resumes from top-level directory checkpoints.
- Profile detection for human, service, and Public (shared) accounts, with standard-folder matching by name and offline `User Shell Folders` redirects.
- Decision engine with User/SuggestedDefault storage, inherited effective decisions, mixed-subtree summaries, and a 50-step undo stack.
- WPF Scan/Decide shell with a step bar, untouched-source status strip, Decide Cards, Files view switcher, virtualized files list with badges and context menu, Inspect pane, and Open Folder through the process runner.
- Optional post-scan SHA-256 hashing of clean files at most 64 MB, stored per node in the session database.
- Classification rules (high-value, regeneratable, game saves, sensitive) with badges, suggested defaults, and High-value / Regeneratable summary cards. Regeneratable and unknown items are never auto-left-behind.
- Restore `PlanBuilder` that emits copy-tree and copy-file items, rejects source/destination containment, and never plans whole AppData folders.
- Journaled copy engine using `.winold-partial` then rename, keep-both conflicts, preflight space margin, and L0–L2 verification reports.
- `IRecipe` host with SSH, Chrome, Edge, Firefox, and Git first-slice recipes: Netscape bookmark export, Firefox `*-recovered` transplant plus `profiles.ini` registration, scrubbed Git config, whole-tree Git repo restore, and a SmartCard six-question pane. Canary secrets stay out of card facts.
- Chromium History/SNSS/extension-store exports and Firefox `places.sqlite` bookmark/history exports via read-only SQLite copies in the session temp folder; `ssh -G` and git.exe analyze requests through `IProcessRunner`; help pages under `docs/help/`.
- Syncthing recipe: device ID from cert DER, rewritten config with every folder paused and old-profile paths remapped, GUI secrets scrubbed from cards, index never restored.
- Anki profile restore (WAL + media, no trash/media index), WSL VHDX copy with header verify, GPG keyring restore excluding `random_seed`.
- R9 detector cards: KeePass vault+keyfile copy, Outlook PST restore / OST leave, Thunderbird allow-list transplant (no panacea/global-messages-db), VS Code settings plus `install-extensions.cmd`, Windows Terminal `settings.from-windows-old.json`, Obsidian vault copy.
- Gated purge: I10 gates, `PurgeToken` minted only after they pass, preferred `cleanmgr /sagerun:777` via `IProcessRunner`, reparse-aware manual delete, session purge manifest and support bundle.
- Preview conflict list with per-file overwrite confirmation (I2); copy resume after a leftover `.winold-partial`; runtime disk-full pause that does not delete existing destination files.
- Chromium autofill CSV from `Web Data` (never `credit_cards`); optional bookmark transplant behind `WINOLD_RECOVERY_CHROMIUM_TRANSPLANT=1`.
- Narrator names and automation ids on the six-step bar; redacted unhandled-exception dialog; CONTRIBUTING and support-bundle bug template; FAQ/limitations/recipes/signing docs; ARM64 publish profile and release assets; FlaUI smoke skipped unless `RUN_FLAUI=1`.
- Handled scan/restore/verify/purge failures show a redacted explanation and the session log path; first-run promises copy; file-list type-ahead; action-button narrator names; NodeBrowser paging truncation test.
- First-run overlay explaining the six steps and the two promises; in-app Help from bundled local Markdown (F1 / Help); Log opens the session log in Explorer; help files cannot leave the help folder.
- In-app redacted session log viewer; middle-ellipsis long paths with full path in Inspect; panes collapse to Cards/Files/Inspect tabs below 1200 px; space budget says when a plan will not fit or is near the limit.
- Scan source picker shows created/estimated-deletion dates, Browse for any folder, Pause/Resume with skipped-item counters, and thousands-separated sizes in the Files list.
- Explorer Open Folder walks up from paths longer than 259 characters; FlaUI smoke requests UAC (`runas`) when `RUN_FLAUI=1`; elevated FixtureGen helper script `tools/run-elevated-m0.ps1`.
- Decide Files paging past 2,000 children, Recent 7/30/90-day selector, Reveal in tree, purge source-integrity text, and blocked step navigation during restore.
- Decide shortcuts ignore typing in text boxes; Restore/Leave Behind keep the expanded tree; overwrite ticks update the button; glob search is case-insensitive; Recent uses the same UTC timestamp format as scan inserts.
- A new Scan clears the previous tree and relocks Purge (I10); Cancel discards the in-flight scan, Pause still resumes.
- Decide Files shows mixed subtrees, Restore/Leave Behind apply to the extended selection, and the context menu can restore except regeneratable or only files inside the Recent window.
- Next launch reopens an interrupted restore session (journal Started/Paused/Failed) with a Resume overlay; Decide Cards collapse apps that were looked for and not found.
- Inspect can edit a planned destination per folder (children follow); Preview exposes the restore destination folder; mixed decision cells include a restore/leave/undecided byte bar; a disk-full pause names the volume and offers Resume or Cancel without deleting anything.
- Preview lists recipes, unrestorable counts, and remaining Undecided items; Start restore stays off while a required app is running; restore shows progress and can be cancelled without touching Windows.old; Verify includes recipe checks.
- Scan reports Setup Cleanup task presence and next run from a read-only `schtasks /Query` (never `/Change`).
- Decide Undo (Ctrl+Z), per-component recipe decisions, and a session record export before purge.
- After purge the session is read-only; Verify can be re-run or failed jobs acknowledged with a typed reason (I10); Restore lists each plan item as a job.
- Smart-card Open Folder reveals the recipe source path in Explorer.
- Restore Pause keeps already-copied files and journals `Paused`; resuming a paused CopyTree does not Keep Both files that already match. Verify offers Go to Purge.
- Inspect shows how many destination files already exist and how many differ; Preview can Re-check the plan.
- Restore progress shows percent, copied bytes, and a remaining-time estimate; Verify lists size/time and SHA-256 file counts.
- Inspect shows owner SID (or "old account, no longer exists"), file attributes, and thousands-separated sizes.
- Restore lists skipped junctions/cloud/EFS files as Warnings (N) with a Show toggle.
- Space expands or collapses the focused tree folder; Ctrl+F focuses Search. Inspect shows oldest/newest modification and the matching classification Why.
- Folder-scoped tree queries (Largest/Recent/Search under a node, Inspect mtime range) match descendants: the LIKE prefix now treats `\` as a literal before the `%` wildcard.
- Purge tokens are minted only after `SessionDb` shows a settled journal and verify store for that session; caller-supplied verify/journal flags cannot authorize those gates.
- Purge step lists verified job count, remaining Undecided items, estimated free space after delete, and the session folder (UX 7).
- Purge offers Windows cleanup vs direct delete, a Delete Windows.old button, live delete progress, and Cancel that leaves a partial tree.
- Scan options on the Scan step: compute folder sizes (default on) and optional hashing of files smaller than 64 MB.
- Recipe cards expose Inspect, Open Folder, Restore, and Leave Behind in that order (UX 6).
- Files tree greys junctions/symlinks with ⊘, a target tooltip, and dash columns; Problems rows explain what can be done.
- Decision chips use ● / ◌ / ○; suggested defaults stay hollow with a confirm tooltip; inherited decisions are dimmed.
- Files view uses Tree / Largest / Recent / Unknown / Problems radios (UX 3.2); Search stays a search box.
- Decide Cards expose Inspect / Open Folder / Restore / Leave Behind on each overview card (UX 3.1); absent, profile, and summary cards stay one-line.
- Recent files view inserts a folder-path header above each parent group (UX 3.2); headers cannot be restored.
- Problems column shows the per-row explanation, not the internal problem enum.
- Status strip uses system colors for source integrity and space-budget amber/red; hardcoded Gray text is gone. Scan summary always names known apps and high-value items.
- Scan progress lists profiles found, file/folder counts, size so far, and skip counters (UX 2.2).
- The shell uses the system message font and size so text follows Windows DPI and accessibility text size (UX 9).
- Files-tree expansion no longer walks mixed subtrees or counts children for every file leaf. The parent-name index is `COLLATE NOCASE` (schema v3) so a 100k-child folder's first page stays under 300 ms.
