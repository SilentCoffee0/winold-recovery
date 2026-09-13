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
