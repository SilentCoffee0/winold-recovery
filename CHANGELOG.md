# Changelog

All notable changes to WinOld Recovery will be documented here.

## Unreleased

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
