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
