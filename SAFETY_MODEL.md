# WinOld Recovery — Safety Model

WinOld Recovery is data-loss-sensitive software. It reads the only remaining copy of a user's data and, at the very end, deletes it. This document defines the invariants the implementation must uphold, the threat model behind them, and how each invariant is enforced and tested. Anything here overrides convenience, performance, and feature requests.

---

## 1. Invariants (non-negotiable)

| # | Invariant | Enforcement | Test |
|---|---|---|---|
| I1 | **The source root is read-only until Purge.** No file or directory under the source root is created, modified, renamed, ACL-changed, owned, or deleted by any code path other than the Purge executor. | `SourceGuard`: every write-capable file API in the codebase goes through `SafeFs`, which refuses any path under any registered source root unless the caller holds a `PurgeToken`. Robocopy is invoked only with the source as `<source>` and never with `/MOV`, `/MOVE`, `/PURGE`, `/MIR`. Registry hives are parsed offline, never loaded with `RegLoadKey`. | Filesystem watcher (`ReadDirectoryChangesW`) on a fixture source root during every integration test; any change event fails the test. Static test: grep for forbidden robocopy switches and forbidden APIs (`File.Delete`, `Directory.Delete`, `File.Move`, `SetAccessControl`, `RegLoadKey`) outside `SafeFs`/`Purge`. |
| I2 | **No silent overwrite.** A destination file that already exists is never replaced unless the user chose Overwrite for that specific file list in Preview. Default policy: keep both. | Copy engine opens destinations with `CREATE_NEW` semantics unless the plan entry carries an explicit `OverwriteApproved` flag that was set by the Preview UI from an explicit per-file list. Robocopy is invoked with `/XC /XN /XO` (skip changed/newer/older) unless overwrite is approved, and in that case with the exact file list. | Fixture with pre-existing destination files; assert content unchanged under default policy; assert " (from Windows.old)" suffix copies exist. |
| I3 | **No `/MIR`, no `/PURGE`, no `/MOV`, no `/MOVE`.** | Robocopy argument builder is a typed model with no way to express these; a startup self-test asserts the literal strings are absent from the builder's output. | Unit test on builder + static grep. |
| I4 | **No whole-AppData restore.** `AppData` is never a plan entry as a unit. Individual subfolders may be. | Plan validator rejects entries whose relative path is exactly `AppData`, `AppData\Local`, `AppData\Roaming`, `AppData\LocalLow` for any profile. | Unit test. |
| I5 | **No old registry hive merge.** The tool never loads, imports, or merges `NTUSER.DAT`, `UsrClass.dat`, `SOFTWARE`, `SYSTEM`. Hives are only parsed offline, read-only, for discovery. | No `reg.exe`, `RegLoadKey`, `RegRestoreKey`, `.reg` import anywhere. | Static grep; code review checklist. |
| I6 | **No automatic deletion or automatic "Leave Behind" of unknown data.** Classification produces badges; only the user produces decisions. Suggested defaults never apply Leave Behind to anything except `AppData` as a whole (which is not restorable anyway, I4) and explicitly regenerable app components (indexes, caches) that the recipe knows are regenerated. | Decision engine's default table is data (see PRODUCT_SPEC §7); a test asserts no default of `LeaveBehind` exists for unclassified nodes or junk-badged nodes. | Unit test over default table. |
| I7 | **No cloud uploads, no network.** The binary makes no outbound connections. | No `HttpClient`, sockets, or update checks in v0.1. Store links are opened via `ShellExecute` in the user's browser, never fetched. | Static grep; integration test with network monitor (or `netsh` firewall rule) asserting zero connections. |
| I8 | **No secret values in logs, UI, crash dumps, or state DB.** | Nodes flagged `Sensitive` are logged by path and size only. Recipes never read secret file contents into strings that outlive parsing; where parsing is needed (e.g. `config.xml`), the parsed model excludes secret fields (API keys in Syncthing config are replaced with `***`). Global exception handler redacts paths under known secret folders. No memory dumps are written by the app. | Log scanner test: run every recipe over fixtures containing canary secrets (unique strings); assert canaries never appear in logs, DB, or UI text dumps. |
| I9 | **Restore is previewable and verifiable.** Every operation in a plan can be rendered before execution and checked after. No recipe may perform a write that is not in the plan. | Recipes implement `Plan()` (pure) and `Execute(plan)`; `Execute` is checked against the plan by the journal (unexpected writes are a bug and fail verification). | Recipe tests compare planned vs. actual file sets. |
| I10 | **Purge only after verified recovery, and only by explicit gated action.** | Purge screen is unreachable unless `VerifyReport.AllJobsVerifiedOrAcknowledged == true`. Purge requires typed folder name. Purge executor takes a `PurgeToken` minted only by the Purge view-model after all gates pass. | UI automation test that gates are enforced; unit test that `PurgeToken` cannot be constructed elsewhere (internal constructor + `InternalsVisibleTo` limited to tests). |
| I11 | **Never follow reparse points in the source.** Junctions, symlinks, mount points, cloud-file reparse points, AppExecLinks and WSL LX symlinks are enumerated as leaf entries and never descended or dereferenced. | Enumeration uses `FileSystemEnumerator` with reparse points reported as leaves; copy engine passes `/XJ /SL`-equivalent behavior (skip junctions; symlinks not copied in v0.1). | Fixture with junction pointing at the destination profile and a junction loop; assert scan terminates and the destination is untouched. |
| I12 | **Never hydrate cloud placeholders.** Files with `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS`, `RECALL_ON_OPEN`, or `OFFLINE` are never opened for read. | Enumeration captures attributes; `SafeFs.OpenRead` refuses these attributes; scan and copy skip and report them. | Fixture files with the attributes set via `FSCTL`/`attrib`; assert never opened (ETW or hook), listed under Problems. |
| I13 | **Destination is never inside the source, and the source is never inside the destination.** | Plan validator normalizes both with `\\?\` long-path canonical form and rejects containment. | Unit test. |
| I14 | **Restored files get fresh ACLs inherited from the destination.** Old ACLs, owners, and SACLs are never copied. | Copy engine uses `CopyFileEx` without `COPY_FILE_COPY_SYMLINK`, never `SetAccessControl` from source; robocopy uses `/COPY:DAT /DCOPY:DAT`, never `/COPYALL`, `/SEC`, `/COPY:…S…O…U…`. Recipes that need *tighter* ACLs (SSH) set them explicitly on the destination. | Builder unit test; ACL assertion on restored fixtures. |
| I15 | **Crash safety.** A crash, power loss, or cancel at any point leaves (a) the source untouched (I1), (b) the destination with only complete files or clearly-marked incomplete temporaries, (c) a journal that allows resume. | Files are copied to `<name>.winold-partial` in the destination directory and renamed on success (`MoveFileEx` with `REPLACE_EXISTING` only for the temp→final rename); journal entries are written before and after each file with SQLite WAL and `synchronous=NORMAL`. Robocopy jobs are journaled per directory and re-run idempotently (robocopy skips same-size/same-time files). | Kill-process tests at random points during restore; assert invariants. |
| I16 | **Disk-full is not data loss.** When the destination reports insufficient space (before or during restore), the job pauses. Nothing is deleted to make room. | Pre-flight: `GetDiskFreeSpaceEx` vs. plan bytes + 5 % margin + 1 GB; runtime: `ERROR_DISK_FULL`/`ERROR_HANDLE_DISK_FULL` mapped to `Paused(DiskFull)`. | Test with a small VHDX destination. |

## 2. Threat model (what we defend against)

- **User error**: choosing the wrong destination, overwriting newer files with older ones, deleting Windows.old before checking the results, misreading "Leave Behind" as "delete now".
- **Tool bugs**: a path bug that writes under the source, a recipe that "cleans up" old files, a copy routine that truncates, an enumeration that follows a junction into the live profile and starts "restoring" it onto itself.
- **Environment hazards**: junctions to the live install (observed in every real Windows.old), cloud placeholders, EFS files, access-denied trees, path lengths beyond 260, disk-full mid-copy, Windows deleting Windows.old on its own schedule, antivirus locking files, the browser or Syncthing running during restore.
- **Secrets exposure**: keys, tokens, passwords, and cookies in logs, screenshots, bug reports, crash dumps, or the state database that a user might share when asking for help.

Not in the threat model: a malicious Windows.old (crafted paths, malicious reparse targets) attacking the tool. The tool runs elevated and reads attacker-controlled data (SQLite, JSON, XML, LevelDB, SNSS). Parsers must be defensive (size limits, no external entity resolution in XML, no code execution), but active anti-exploitation hardening is out of scope for v0.1.

## 3. Why some things are deliberately not done

### 3.1 No DPAPI / App-Bound decryption of Chromium secrets

Chrome and Edge (127+) protect cookies, passwords and payment data with App-Bound Encryption: the key is wrapped by the SYSTEM account's DPAPI master key and released only by an elevation service that validates the calling binary. After a clean reinstall the SYSTEM master keys are new. The old user's password does not help. Even for older `v10` blobs (user DPAPI), recovery would require offline master-key cracking with the exact old password, tooling that is flagged by antivirus and that fails for Microsoft-account logins. WinOld Recovery therefore does not attempt any of it and tells users to sign in to Google/Microsoft account sync. See RESEARCH.md §2.

### 3.2 No "Move" restore mode in v0.1

A same-volume rename would let users "restore" hundreds of gigabytes instantly with no extra space, which is attractive when Windows.old holds 536 GB and 127 GB is free. But rename mutates the source root (breaks I1), destroys the ability to re-run or re-verify against the original, and turns a partial failure into a split dataset. V0.1 stays copy-only with a space budget. A future "Move mode" would need: its own explicit opt-in screen, per-item journaling of `(old path → new path)` with an "undo" that renames back, exclusion of any item covered by a recipe, and a separate invariant set. It is recorded as a v0.2 decision for the product owner, not silently implemented.

### 3.3 No automatic protection against Windows' 10-day auto-cleanup

Windows deletes Windows.old on its own schedule via `SetupCleanupTask`. Renaming Windows.old defeats the task but is a source mutation (I1) and can surprise other tools and the user. V0.1 only **warns** with the estimated date. A future opt-in "extend" feature must be researched separately.

### 3.4 No registry restore

Old hives contain paths, SIDs, and machine-specific state; merging them is the classic way to corrupt a fresh install. Hives are parsed offline only to discover redirected shell folders, WSL distro registrations, and app install locations.

### 3.5 No whole-profile or whole-AppData restore

It defeats the product's purpose and is how stale, machine-bound state (DPAPI blobs, `Secure Preferences` HMACs, service configs) leaks into the new install.

### 3.6 No symlink recreation in v0.1

Symlinks inside profiles usually point at machine-specific targets. They are listed and skipped; the user can recreate them.

## 4. Purge design

Purge is the only destructive operation. Its design:

1. **Gates** (all required): every restore job verified or explicitly acknowledged with a typed reason; "I have checked my restored files" ticked; Undecided items acknowledged; typed folder name matches; source root is a genuine previous-installation folder (name matches `Windows.old*` on a fixed volume, or the user re-confirms for custom roots with an extra warning).
2. **Preferred method**: the Windows "Previous Windows installation(s)" cleanup handler (Disk Cleanup / Storage settings), invoked via `cleanmgr` with the `Previous Installations` volume cache flag, so Windows removes the folder the way it does itself. If the handler is unavailable (custom root, non-system volume), fall back to manual deletion.
3. **Manual deletion**: enable backup/restore privileges, take ownership recursively, grant Administrators full control, then delete bottom-up without following reparse points (delete the junction, not its target). Progress is shown and the operation can be cancelled; a cancelled purge is reported as "partially deleted" with the remaining size.
4. **Records kept**: the session database, decisions, restore log, verify report, and a manifest of what was purged (paths, sizes, no content) are copied to `%LOCALAPPDATA%\WinOldRecovery\sessions\<date>\` before deletion starts.
5. **Anti-footgun**: Purge cannot target any path that is not the registered source root; it cannot run while a restore job is active; it refuses if the root contains the running executable, the session database, or any destination path from the plan.

## 5. Secrets handling

- **Sensitive classification** covers: private keys (`id_*`, `*.pem`, `*.key`, `*.ppk`, `*.pfx`, `*.p12`, `key.pem`), `.git-credentials`, `.env*`, `*.kdbx`/`*.kdb`/`*.key`, `.netrc`, `.npmrc`/`.pypirc` with tokens, cloud CLI credential files (`.aws\credentials`, `.kube\config`, `.azure\*`, `.docker\config.json`), browser `Login Data`/`Cookies`/`key4.db`/`logins.json`, Syncthing `key.pem`, GPG `private-keys-v1.d`, Windows Credential vault files, DPAPI `Protect` folders, `NTUSER.DAT`.
- Sensitive files are: never opened for display; never hashed into logs (hashes of secrets are still fingerprints, keep them in the DB only, keyed by node, and exclude from exported reports); shown in the UI as "sensitive, contents hidden".
- Recipes that must parse a sensitive file (Syncthing `config.xml` for folder paths; `.gitconfig` for remotes) keep the parsed model minimal and scrub known secret fields before anything reaches the UI or log.
- Exported reports (verify report, bookmarks HTML, history CSV) live in the session folder and are explicitly listed to the user with a note that history exports are personal data.

## 6. Elevation and privilege use

- The process runs as administrator (manifest). At startup it enables `SeBackupPrivilege` and `SeRestorePrivilege` on its token. Backup privilege makes reading access-denied source files possible without changing ACLs (I1). Restore privilege is used only by Purge (ownership) and by the SSH recipe to set ACLs on *destination* files.
- The tool never launches other processes elevated except `robocopy.exe`, `cleanmgr.exe` (purge), `wsl.exe` (WSL registration, with user confirmation), `git.exe` (read-only status, with `GIT_OPTIONAL_LOCKS=0` and `-c safe.directory=*` so it neither writes the index nor refuses old-SID repos), and `explorer.exe` (Open Folder). All child process arguments are logged verbatim except when they would contain secrets (never the case by construction).

## 7. Verification levels

| Level | What it checks | When |
|---|---|---|
| L0 Existence | Destination path exists, is a file/dir as planned | Always |
| L1 Size + time | Size equal; last-write time within 2 s | Always |
| L2 Hash | SHA-256 of source and destination equal | All sensitive items; all files < 64 MB in recipe outputs; random 2 % sample of other files (min 200 files); everything if "strong verify" was enabled at scan |
| L3 Semantic | Recipe-specific: SQLite `PRAGMA integrity_check` on copied databases, XML parse of config, device ID derivation from cert, `profiles.ini` registration, VHDX header magic and size, `git rev-parse` in restored repo | Every recipe |

A job's status is the minimum of its items' statuses. A verify report is immutable once produced; re-running creates a new report.

## 8. Testing obligations

Every invariant has at least one automated test named after it (`I1_SourceIsNeverWritten`, …). The integration suite runs against a generated fixture Windows.old that contains: two profiles, legacy junctions pointing at a live-profile fixture, a junction loop, symlinks, cloud-placeholder attributes, EFS-encrypted files, an access-denied directory, 300-character paths, files with invalid destination names, a `node_modules` tree with 100k files, every known-app fixture (browser profiles with canary secrets, Syncthing home, `.ssh`, git repos in each risk state, `ext4.vhdx` stub, Anki profile, KeePass file). A `ReadDirectoryChangesW` watcher on the fixture root is armed for the whole suite and any event fails the run.
