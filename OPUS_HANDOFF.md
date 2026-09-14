# OPUS_HANDOFF — Build brief for WinOld Recovery v0.1

You are implementing **WinOld Recovery**, an open-source Windows utility that selectively recovers valuable data from `C:\Windows.old` after a Windows reinstall and then, only after verified recovery and explicit confirmation, purges Windows.old. The research and design phase is complete. Read the documents in this order before writing code:

1. [PRODUCT_SPEC.md](PRODUCT_SPEC.md) — what the product is, v0.1 scope, defaults.
2. [SAFETY_MODEL.md](SAFETY_MODEL.md) — invariants I1–I16. These are law.
3. [ARCHITECTURE.md](ARCHITECTURE.md) — stack, layout, data model, engines.
4. [RECOVERY_RECIPES.md](RECOVERY_RECIPES.md) — per-app detection/plan/execute/verify.
5. [UX_SPEC.md](UX_SPEC.md) — screens, card anatomy, copy rules.
6. [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) — milestones, tests, acceptance.
7. [RESEARCH.md](RESEARCH.md) — evidence and citations; consult when a recipe detail is unclear.

## The product in one paragraph

Scan → Classify → Decide (Restore / Leave Behind / Undecided) → Preview → Restore → Verify → Final Purge. Windows.old is read-only until the purge step. "Leave Behind" is a label, never a deletion. Known apps (Chrome, Edge, Firefox, Syncthing, SSH, Git, WSL, Anki, plus high-value detectors such as KeePass, GPG, mail stores, VM disks, VS Code) appear as smart cards that explain what the data is, why it matters, what is restored, whether the cloud can restore it, whether it regenerates, and what happens if it is left behind. Personal files appear in a virtualized tree with decisions at any depth. Chromium passwords/cookies are stated as unrecoverable; Firefox secrets are recoverable.

## Final stack (do not reopen)

- C# 14, **.NET 10 LTS**, `net10.0-windows`, **WPF** with Fluent theme (`ThemeMode` set in XAML), CommunityToolkit.Mvvm.
- **Portable single-file self-contained EXE**, x64 (ARM64 second), `requireAdministrator` + `longPathAware` manifest, `IncludeNativeLibrariesForSelfExtract=true`, `EnableCompressionInSingleFile=true`. WPF cannot be trimmed; a 70–150 MB EXE is accepted.
- **SQLite** (`Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3`, WAL) for all scan/decision/plan/journal/verify state.
- **In-process copy engine** (P/Invoke `CreateFileW` with backup semantics for source reads, `FileStream` writes to `.winold-partial` then rename, timestamps preserved, no ACL copy, parallel workers, inline SHA-256, journal). Robocopy is **not** the primary engine; an optional adapter behind `IRestoreCopier` may be added later.
- **Offline registry parsing** with the `Registry` NuGet package (Eric Zimmerman, MIT). Never `RegLoadKey`.
- Parsers: `System.Text.Json`, `Microsoft.Data.Sqlite` (read-only on copies), `System.Xml.Linq` with DTD prohibited, `K4os.Compression.LZ4` for mozLz4, in-house SNSS reader.
- Tests: xUnit + FluentAssertions, `tools/FixtureGen`, FlaUI smoke tests. CI on GitHub Actions `windows-latest`.
- License MIT. No network code of any kind in v0.1.

## Repository structure

```
winold-recovery/
  README.md  LICENSE  CHANGELOG.md  THIRD_PARTY_NOTICES.md  CONTRIBUTING.md
  docs/                       (move the eight design docs here in M0 and fix README links; add help pages rendered in-app and docs/spikes/)
  src/WinOldRecovery.Native/  src/WinOldRecovery.Core/  src/WinOldRecovery.Recipes/  src/WinOldRecovery.App/
  tests/WinOldRecovery.Core.Tests/  tests/WinOldRecovery.Recipes.Tests/
  tests/WinOldRecovery.Integration.Tests/  tests/WinOldRecovery.App.Tests/
  tools/FixtureGen/
  build/  (app.manifest, publish profiles, signing scripts)
  .github/workflows/  (ci.yml, release.yml)
```

Dependency direction: `App → Recipes → Core → Native`. Recipes never reference App; Core never references Recipes.

## Final v0.1 scope

In: source discovery and read-only scan; profile and standard-folder detection (including redirected folders from the old hive); virtualized file tree with Largest/Recent/Search/Unknown/Problems views; decisions with inheritance; badges for high-value and regeneratable data (badges never decide); smart cards and recipes for Firefox, Chrome, Edge, Syncthing, SSH, Git (config + repository risk analysis), WSL (VHDX copy + optional registration), Docker Desktop disks (copy only), Anki, GPG, and copy-based detectors for KeePass, Thunderbird, Outlook PST, VS Code, Windows Terminal, Obsidian, game saves, `.env`/certs/cloud CLI credentials (sensitive, Undecided); Preview with conflicts, space budget, prerequisites; resumable journaled restore; verification L0–L3; gated purge via the Windows cleanup handler with a manual fallback; session records; support bundle.

Out (do not build, do not leave hooks that pretend to): any Chromium password/cookie decryption; registry merging; whole-profile or whole-AppData restore; program restoration; move/rename restore mode; extending the 10-day cleanup task; WSL 1 migration; Docker Desktop registration; other browsers; symlink recreation; network sources; CLI automation; localisation.

## Safety invariants (summary; full text in SAFETY_MODEL.md)

I1 source read-only until purge (enforced by `SafeFs`/`SourceGuard` + `PurgeToken`) · I2 no silent overwrite (default keep-both; overwrite only with per-file approval) · I3 no `/MIR`, `/PURGE`, `/MOV`, `/MOVE` anywhere · I4 no whole-AppData plan items · I5 no hive loading or merging · I6 no automatic Leave Behind or deletion of unknown/regeneratable data · I7 no network · I8 no secret values in logs/UI/DB exports (canary tests) · I9 every write is in the plan and verifiable · I10 purge only after verify, typed confirmation, `PurgeToken` · I11 never follow reparse points · I12 never open cloud placeholders · I13 destination never inside source and vice versa · I14 fresh destination ACLs, never copied · I15 crash-safe partial-file + journal + resume · I16 disk-full pauses, never deletes.

Each invariant gets a named test. The integration suite arms a `ReadDirectoryChangesW` watchdog on the fixture source for its whole duration; any event fails the run.

## Implementation order

Follow IMPLEMENTATION_PLAN.md: **M0 foundation and spikes → M1 scan + tree → M2 classification → M3 plan/copy/verify → M4 recipes (Firefox, Chromium, SSH, Git) → M5 recipes (Syncthing, Anki, WSL/Docker, GPG and detectors) → M6 purge → M7 hardening and release.** Do not start a recipe before M3's engine and M4's `IRecipe` infrastructure exist. Do not build the purge before verification exists.

## Start here: Milestone 0

1. Create the solution and projects exactly as listed; add `Directory.Build.props` with `net10.0-windows`, nullable enabled, warnings as errors; add the manifest and single-file publish profile; confirm `dotnet publish` produces one EXE that launches elevated.
2. Implement `Privileges.EnableBackupAndRestore()` and `SafeFs`/`SourceGuard` with unit tests (path normalisation with `\\?\`, containment, refusal of writes under registered source roots without a token).
3. Implement `SessionDb` schema v1 and the writer channel.
4. Write `tools/FixtureGen` (see IMPLEMENTATION_PLAN M0 item 6) and its self-check.
5. Run the six spikes and record results in `docs/spikes/`. In particular: enumerate the fixture's deny-ACL and orphan-SID folders with backup privilege; read a file inside them via `CreateFileW`; parse the fixture `NTUSER.DAT` with the `Registry` package from inside the published single-file EXE; load SQLite inside the bundle; measure startup and size.
6. Set up CI. Tag `v0.1.0-m0`.

## Current implementation handoff — 13 Sep 2026

- **Current item:** interactive FlaUI on a desktop (`RUN_FLAUI=1`), SignPath, manual VM matrix, 1M-node published-EXE memory ceiling, and elevated/manual M0 checks. Do not tag `v0.1.0-m0` or `v0.1.0` while those remain pending.
- **Complete:** Automated M0 coding through CI/release (M0 tag withheld). M1–M6. M7 slices: narrator names, redacted failures, Help/first-run, log viewer, compact layout, scan picker dates/Browse/Pause/skipped counters, ARM64 publish, restore percent/ETA, verify file-count report, Inspect owner SID, restore skip warnings, Space collapse, Ctrl+F search focus, Inspect mtime range and classification Why, purge token mint from session store, purge checklist (jobs/undecided/free space), purge cleanup-vs-direct radios, Delete Windows.old, cancel-during-purge with progress, Scan options (folder sizes + hash), recipe-card Inspect/Open/Restore/Leave Behind, grey reparse rows with ⊘ and problem explanations, decision chips and suggested-default tooltips, Files view radios, overview-card Inspect/Open/Restore/Leave Behind, Recent folder-group headers, Problems column explanations, system-color status strip, scan summary known-apps/high-value, live scan progress with profiles and file/folder counts, system message font/size for DPI and accessibility text size. M1 tree-scale tests: 100k-child expand under 300 ms, 1M synthetic SQLite children paged under 1.5 GB (schema v3 NOCASE parent index), and a 1,000,000-file on-disk walker scan under 60 s / 1.5 GB in the test process. I1 source watchdog on integration scans; I15 kill-process CopyTree of 50k files via RestoreHarness (published EXE kill still pending). Named I1–I16 tests, including I4/I9–I14 and window title with source path and step. Recipe L3 rows persist on the verify report so I10 consults the store. Git repos are detected anywhere under the profile (outermost card) with tree badges. Verify hashes VHDX past 64 MB; Git config restore includes ignore files. Analyze repositories runs git.exe through `IProcessRunner` and updates Git risk badges from porcelain output. Preview Edit mapping persists Syncthing folder path overrides into the rewritten paused config.
- **Remaining M0 (explicitly pending, not claimed passed):** elevated deny-ACL/orphan-SID enumeration and backup-mode reads; full 100,000-file elevated FixtureGen self-check (`tools/run-elevated-m0.ps1`); clean-Windows-11 elevated WPF launch; real-VM `cleanmgr` handler; interactive Explorer confirmation of long-path selection (code fallback exists); 200 MB VHDX disk-full volume; 1,000,000-node memory ceiling. See `docs/spikes/PENDING_MANUAL.md`.
- **Verified tests:** strict Release build has 0 warnings/errors. Do not tag `v0.1.0-m0` while those external checks remain pending.
- **Next exact implementation step:** run FlaUI on an interactive elevated desktop when a published EXE is available. Do not tag `v0.1.0-m0`.

## What not to redesign

- The seven-step workflow and the three-state decision model.
- The invariants and the `SafeFs`/`SourceGuard`/`PurgeToken` enforcement pattern.
- The stack (WPF, single-file, SQLite, in-process copy engine, offline registry parser).
- The card anatomy (six questions, instances, components, four verbs) and the copy rules in UX_SPEC §8.
- The honest browser stance: Chromium secrets are unrecoverable, Firefox secrets are recoverable, no DPAPI tooling.
- The Syncthing safety recipe (identity + rewritten config with every folder paused, index never restored).
- The purge gates and the preference for the Windows cleanup handler.
- Suggested defaults in PRODUCT_SPEC §7 (regeneratable and unknown data are never auto-Left-Behind).

You may change: internal class names, file layout inside a project, the exact SQL schema, the WPF control implementation of the tree, rule-file syntax, and any detail marked "[verify]" or "[unverified]" in the docs once you have tested the real behaviour. When a verified fact contradicts a design detail, update the document in the same commit.

## Conventions

- Every child process invocation goes through `IProcessRunner` (mockable; tests never run `wsl.exe`, `git.exe`, `cleanmgr.exe`).
- Every file operation goes through `SafeFs`. Grep-based static tests enforce that `System.IO.File`/`Directory` write members and the forbidden robocopy switches appear only in `SafeFs` and the purge executor.
- Secrets: nodes flagged `Sensitive` are referenced by id in logs; recipe models scrub API keys, tokens, and credentials before leaving the recipe.
- UI copy follows UX_SPEC §8: say what happens, then why; never "safe to delete"; absolute dates.
- Commit messages: `M<n>: <area>: <change>`; each milestone ends with a tag and a CHANGELOG entry.

## Definition of done for v0.1

All milestones accepted; manual matrix in IMPLEMENTATION_PLAN M7 passed; README and docs published; release with checksums; no open issue labelled `safety`.
