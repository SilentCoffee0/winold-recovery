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
