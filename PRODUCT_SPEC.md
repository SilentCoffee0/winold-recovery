# WinOld Recovery — Product Specification (v0.1)

> **Recover what matters from Windows.old. Leave the old Windows behind.**
>
> WinOld Recovery is an open-source Windows.old recovery tool. It helps you recover files from Windows.old after reinstalling Windows: personal files, browser bookmarks, SSH keys, Syncthing identity, WSL distros, Anki decks and other valuable data, without dragging the old Windows profile, its registry, or its junk into the clean install.

Status: **design document, pre-implementation**. Companion documents: [RESEARCH.md](RESEARCH.md), [UX_SPEC.md](UX_SPEC.md), [ARCHITECTURE.md](ARCHITECTURE.md), [RECOVERY_RECIPES.md](RECOVERY_RECIPES.md), [SAFETY_MODEL.md](SAFETY_MODEL.md), [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md), [OPUS_HANDOFF.md](OPUS_HANDOFF.md).

WinOld Recovery is an independent community project. It is not affiliated with, endorsed by, or supported by Microsoft. "Windows" is a trademark of Microsoft Corporation.

---

## 1. Problem

When Windows is reinstalled, upgraded in place, or reset with "Keep my files", Setup moves the previous installation to `C:\Windows.old`. Everything is still there: user profiles, AppData, Program Files, ProgramData. Windows then deletes the folder automatically after about 10 days, or the user deletes it with Disk Cleanup.

People in this situation want two contradictory things:

1. **Get back what is valuable.** Documents, photos, downloads they never sorted, SSH keys, browser bookmarks, a Syncthing device identity that ten other machines trust, a WSL distro with a year of setup, an Anki collection with 40,000 reviews, a KeePass database, game saves.
2. **Keep the new install clean.** No old registry, no stale AppData, no 200 GB of `node_modules`, caches, installers, and game files that can be regenerated or re-downloaded.

Today the options are poor:

- **Manual copying in Explorer**: slow, error-prone, blind to what matters, defeated by junctions, permissions, long paths, and 700,000 files.
- **USMT / SuperGrate / profile migration tools**: designed for IT admins to migrate *whole* profiles between machines. They restore everything, including the cruft. They need the ADK, XML rules, and do not explain what they are doing. They cannot recover browser passwords after a reinstall either, but they do not tell you that.
- **Commercial "PC transfer" products**: opaque, closed, expensive, and they also restore the old mess.
- **"Go back" in Settings**: restores the old Windows wholesale. The opposite of the goal.

Nobody offers a **triage** tool: scan Windows.old, tell me what is in there in human terms, let me decide item by item what comes forward, do it safely, prove it worked, and only then let me purge the old install.

## 2. Product statement

WinOld Recovery is a **portable, single-EXE Windows utility** that:

- **Scans** a `Windows.old` folder (or any old Windows volume with a `Users` folder) read-only.
- **Classifies** what it finds: user profiles, standard folders, known applications (browsers, Syncthing, SSH, Git, WSL, Anki, …), high-value "unknown" data (KeePass, GPG, mail stores, VM disks, `.env` files, vaults), and regeneratable junk.
- Lets the user mark every meaningful item **Restore**, **Leave Behind**, or **Undecided**.
- **Previews** exactly what will be written where, how much space it needs, and what conflicts exist.
- **Restores** using app-specific recipes for known apps and a robust copy engine for files.
- **Verifies** the restore (size, timestamp, and hash spot-checks; app-specific health checks).
- Only then offers a **final purge** of Windows.old, gated behind a checklist and explicit confirmation.

**"Leave Behind" never deletes anything.** Windows.old is not modified until the final purge step, which is a separate, deliberate action that runs only after the restore has been verified.

## 3. Target users

| Persona | Situation | What they need |
|---|---|---|
| **Power user / developer** (primary) | Clean-reinstalled Windows 11; has `.ssh`, Git repos with unpushed work, WSL, Docker, Syncthing, dev caches | Find the irreplaceable things fast; do not restore 50 GB of `node_modules`; confirm nothing unpushed is lost |
| **Everyday user** | Upgraded or reset PC; "my photos and documents are gone" | Find the standard folders, see sizes, restore them, get bookmarks back |
| **Helper / technician** | Fixing someone else's PC | Trustworthy checklist, logs, no surprises, no destructive defaults |

Non-goals for users: enterprise fleet migration, domain profiles, migrating between two live machines over a network, restoring installed programs.

## 4. Workflow

```
SCAN → CLASSIFY → DECIDE (Restore / Leave Behind / Undecided) → PREVIEW → RESTORE → VERIFY → FINAL PURGE
```

1. **Scan.** Pick the source (auto-detected `C:\Windows.old`, or browse to another folder or drive). Enumerate every user profile and file. Never follow junctions or symlinks. Never hydrate cloud placeholders. Never modify the source. Results persist in a local SQLite database so a closed app resumes where it left off.
2. **Classify.** Attach meaning: which profile, which standard folder, which known app, which "important unknown" pattern, which regeneratable-junk pattern, cloud placeholder, EFS-encrypted, unreadable.
3. **Decide.** Every meaningful node (profile, standard folder, subfolder, file, app card, app component) has a decision: Restore, Leave Behind, or Undecided. Decisions inherit downward and can be overridden at any depth. Nothing is decided for the user except a few safe, visible defaults (see §7).
4. **Preview.** A restore plan: destination path per item, byte count, free-space budget, conflicts (file already exists at destination), app-specific warnings (browser must be closed, Syncthing folder path remap), items that cannot be restored (EFS, placeholders, access denied). The user can adjust and re-preview.
5. **Restore.** Execute the plan. Resumable, cancellable, logged. Conflicts follow the chosen policy (default: keep both, restored copy renamed). Never overwrite silently.
6. **Verify.** Per item: exists, size, timestamps, optional hash comparison. App recipes run health checks (for example, `ssh -G` parses the config; the copied Syncthing config parses and the device ID matches). The verify report is the evidence the purge step depends on.
7. **Final purge.** Available only when every Restore item is verified and no Undecided item remains (or the user explicitly acknowledges them). Shows what will be deleted, requires typing the folder name, prefers the Windows built-in "Previous Windows installation(s)" cleanup, and keeps the WinOld Recovery log and decision record.

## 5. Scope for V0.1

### 5.1 In scope

**A. Personal files (file tree)**
- Detect old user profiles under `<root>\Users\*` (skip `Default`, `Public`-as-profile, `All Users`, `Default User`, service accounts flagged separately).
- Standard folders: Desktop, Documents, Downloads, Pictures, Videos, Music, Saved Games, Favorites, Contacts, Links, Searches, plus every non-standard top-level folder in the profile ("custom folders", e.g. `~\Sync`, `~\Calibre Library`, `~\Zotero`).
- Redirected standard folders detected from the old `NTUSER.DAT` "User Shell Folders" values (read offline, without loading the hive).
- Expandable tree with size, file count, newest modification date, decision state, and classification badges per node.
- Decisions at any depth: profile, folder, subfolder, file.
- Actions on every node: Inspect, Open Folder / Open Containing Folder (in Explorer, read-only), Restore, Leave Behind, Undecided.
- Views: Largest (folders and files), Recent (by modification date), Search (name substring / glob), Unknown (nothing classified it), Problems (unreadable, EFS, placeholders, long paths).
- Regeneratable-junk detection with **badge only, never auto-decided**: `node_modules`, `.venv`/`venv`, `__pycache__`, `target`, `bin`/`obj` next to project files, `.gradle`, `.m2`, `.nuget/packages`, `.cache`, `pip`/`npm`/`yarn`/`uv` caches, `*.iso`, installers (`*.exe`/`*.msi` in Downloads), `Package Cache`, browser caches, `Temp`.

**B. Browsers: Chrome, Edge, Firefox** (smart cards, per profile)
- Recover: bookmarks, history, open tabs/sessions, extension list, autofill (non-card), and for Firefox additionally preferences, form history, site storage, and add-ons via profile transplant. Chromium site storage (Local Storage/IndexedDB) is not restored in v0.1: it is worthless without the cookies that cannot be recovered.
- Firefox passwords and cookies (`logins.json` + `key4.db`; `cookies.sqlite`): recoverable because Firefox does not tie them to the Windows account by default. Primary-password-protected profiles need that password in Firefox afterwards.
- Chromium (Chrome/Edge) passwords, cookies, payment methods: **not recoverable** after a clean reinstall (DPAPI + App-Bound Encryption; see RESEARCH.md). The card says so plainly and points to Google/Microsoft account sync.
- Restore methods: (a) create a *new* browser profile from the old one with an allow-listed file set, (b) export bookmarks to a Netscape HTML file for manual import, (c) show the extension list with store links.
- Requires the browser to be closed; the tool checks.

**C. Syncthing** (smart card)
- Detect all Syncthing homes: `%LOCALAPPDATA%\Syncthing`, `%APPDATA%\Syncthing`, SyncTrayzor-managed homes, `ProgramData\Syncthing` (service installs), plus service accounts.
- Restore identity (`cert.pem`, `key.pem`), configuration (`config.xml` with folder path remapping and all folders **paused**), and optionally GUI TLS certs. Never restore the index database; mark it "regenerate".
- Show device ID (derived from the certificate), folder list, device list, last-used time, and which home was most recently active.

**D. SSH + Git** (smart cards)
- `.ssh`: keys, `config`, `known_hosts`, `authorized_keys`. Restore with correct restrictive ACLs. Warn about passphrase-less private keys. Never display key material.
- Git: `.gitconfig`, `.config\git\*`, and `.git-credentials` (flagged as plaintext secret; default Undecided).
- Git repositories: discover every `.git`, then compute risk with an offline read of `HEAD`/refs/packed-refs/config, and if `git.exe` is available, run read-only status: uncommitted changes, untracked files, unpushed commits, local-only branches, stashes. A repo is never called "safe to leave" unless all four are clean **and** a remote exists; otherwise it is "Has local-only work".

**E. WSL** (smart card)
- Detect distros: `%LOCALAPPDATA%\wsl\<guid>\ext4.vhdx`, Store package `LocalState\ext4.vhdx`, custom `--import` locations recorded in the old registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss` (read offline), Docker Desktop's `docker_data.vhdx`.
- Restore = copy the VHDX out of Windows.old to a chosen location, verify size and hash, then register it with `wsl --import-in-place` (WSL 2) and set the default user. WSL 1 rootfs directories are detected but marked "manual / unsupported in v0.1".

**F. Anki** (smart card)
- Detect `%APPDATA%\Anki2` (and custom base if recorded). Per profile: `collection.anki2`, `collection.media`, `backups`, add-ons, `prefs21.db`. Restore whole profile folders while Anki is closed, or restore only the latest `.colpkg` backup. Mark `collection.media.db2`, `-wal`, `media.trash` as regenerable. Mention AnkiWeb sync as the alternative.

**G. Important unknown data** (detectors → cards or badges)
- KeePass (`*.kdbx`, `*.kdb`, `*.key`), GPG (`%APPDATA%\gnupg`), Outlook PST/OST, Thunderbird profiles, `.env` files, certificates and keys (`*.pfx`, `*.p12`, `*.pem`, `*.key`, `*.ppk`), database files (`*.sqlite`, `*.db`, `*.mdb`, `*.accdb`), VM disks (`*.vhd`, `*.vhdx`, `*.vmdk`, `*.vdi`, `*.qcow2`), Obsidian vaults (`.obsidian` folder), VS Code user settings (`settings.json`, `keybindings.json`, `snippets`, extension list), game saves (`Saved Games`, `Documents\My Games`, known publisher folders), password manager exports, cloud CLI credentials (`.aws`, `.kube`, `.azure`, `.docker\config.json`), shell configs (`.bashrc`, PowerShell profile), Windows Terminal settings.
- These get a "High value" badge and default **Undecided**. They are never classified as junk.

**H. Safety, verification, purge, logging**
- Read-only scan; no writes into the source root, ever, until purge.
- Local SQLite state; crash-safe restore journal; resume.
- Verify report; purge gate; secret-free logs.

### 5.2 Explicitly out of scope for V0.1 (candidates for later)

- Registry migration of any kind (no hive merge, no per-app registry settings).
- Restoring installed programs or Program Files.
- Whole-AppData restore, whole-profile restore.
- Other browsers (Brave, Vivaldi, Opera, LibreWolf, Zen, Tor) — the Chromium/Gecko recipes are written generically so these can be added by adding paths.
- Chromium password/cookie decryption of any kind, including "enter your old password" DPAPI offline decryption. See SAFETY_MODEL.md for why.
- WSL 1 rootfs migration, Hyper-V VMs, VirtualBox/VMware VM registration (disks are detected and copied as files only).
- Outlook profile reconstruction (PST files are copied; the user opens them in Outlook).
- Network sources, other PCs, backups on external media (any local path with a `Users` folder works, but there is no network share support).
- "Move instead of copy" restore mode. See §8 and SAFETY_MODEL.md.
- Scheduled/unattended mode, CLI automation (a minimal `--scan-only --json` may be considered for testing only).
- Localization beyond English.

## 6. Key product principles

1. **Triage, not migration.** The user decides. The tool informs.
2. **Explain everything.** Every card answers: What is this? Why does it matter? What exactly will be restored? Can it come from the cloud instead? Can it be regenerated? What happens if I leave it behind?
3. **Honest limits.** If something cannot be recovered (Chrome passwords, EFS files without the old key, OneDrive placeholders), the UI says so up front, not after a failed attempt.
4. **Nothing is destroyed casually.** Leave Behind is a label. Purge is a separate, gated, typed-confirmation action that runs last.
5. **Never overwrite silently.** Conflicts are shown in Preview and resolved by an explicit policy; the default keeps both.
6. **Resumable and verifiable.** Everything is journaled; a crash or reboot mid-restore is recoverable; verification is evidence, not a green checkmark.
7. **Secrets stay secret.** The tool never displays, logs, or uploads key material, passwords, or tokens. It reports that they exist.
8. **No network.** No telemetry, no update checks, no uploads in v0.1.

## 7. Defaults and decision policy

The tool proposes; the user disposes. Defaults are visible as "suggested" and colored differently from user-made decisions.

| Category | Suggested default | Rationale |
|---|---|---|
| Standard folders (Desktop, Documents, Pictures, Videos, Music, Saved Games) | Restore | Almost always wanted |
| Downloads | Undecided | Mixed value; often huge |
| Custom top-level profile folders | Undecided | Unknown intent |
| `.ssh`, `.gitconfig`, GPG, KeePass, Obsidian vaults | Restore (secrets: Restore, displayed as sensitive) | High value, small |
| `.git-credentials`, `.env`, cloud CLI tokens | Undecided (sensitive) | Plaintext secrets; user should re-authenticate if possible |
| Git repos with local-only work | Restore | Irreplaceable |
| Git repos clean and pushed | Undecided | Re-clonable, but user may want them |
| Browser bookmarks/history/sessions | Restore | Safe, small |
| Browser passwords/cookies (Chromium) | Not offered | Impossible |
| Firefox passwords/cookies | Restore | Recoverable |
| Syncthing identity + config | Restore | Irreplaceable identity |
| Syncthing index DB | Not offered (Regenerate) | Regenerated by Syncthing |
| WSL VHDX | Restore | High value |
| Docker Desktop data VHDX | Undecided | Large; images re-pullable, volumes may matter |
| Anki collection + media + backups | Restore | High value |
| Regeneratable junk badges | Undecided (never auto Leave) | User rule: never auto-delete |
| AppData (everything not covered by a card) | Leave Behind (suggested) with per-folder override | Whole-AppData restore is prohibited; individual folders can still be restored by the user |
| Cloud placeholders, EFS, unreadable | Cannot restore (shown in Problems) | Impossible without external resources |

## 8. Known hard constraints and how V0.1 handles them

- **Disk space.** Windows.old commonly lives on the same volume as the new install. A real profile in this project's test machine holds 782,000 files and 536 GB while the volume has 127 GB free. Copy-based restore of everything is impossible there. V0.1 shows a live **space budget** (selected bytes vs. free bytes minus a safety margin) and refuses to start a restore that does not fit. Same-volume "move" (rename) restore would avoid the double storage but violates the "source is read-only until purge" invariant; it is deferred and documented as a v0.2 decision in SAFETY_MODEL.md.
- **10-day auto-delete.** Windows schedules `\Microsoft\Windows\Setup\SetupCleanupTask` to remove Windows.old about 10 days after setup. The tool shows the age of Windows.old and the estimated deadline prominently. It never renames or disables anything without asking; an optional "protect from auto-cleanup" action is a v0.2 candidate after research confirms a safe method.
- **Encrypted browser secrets.** Not recoverable for Chromium browsers; recoverable for Firefox. No workaround is shipped.
- **Junctions to the live install.** Legacy junctions inside old profiles (`Application Data`, `My Documents`, …) point to `C:\Users\<name>\…` on the **new** install. Following them would scan or, worse, "restore" the live profile onto itself. They are never followed and never copied.
- **Old SIDs and ACLs.** Files in Windows.old are owned by SIDs that no longer exist. The tool runs elevated with backup privilege to read them, and restores files with **fresh ACLs inherited from the destination**, never the old ones.
- **Elevation.** The app requires administrator rights (manifest `requireAdministrator`). Explorer drag-and-drop into the app is therefore not supported; paths are typed or browsed.

## 9. Success criteria for V0.1

- A user with a typical profile (100k–1M files) can scan, triage, restore the standard folders and all detected known-app data, verify, and purge within a single session, with the app remaining responsive.
- Every safety invariant in SAFETY_MODEL.md has an automated test.
- Every "cannot be restored" case is shown before restore, not discovered during it.
- The browser card never claims to recover Chromium passwords or cookies.
- No test scenario, including crash mid-restore and disk-full, results in a modified Windows.old or a corrupted destination.
- The purge step cannot be reached without a completed verify report.

## 10. Naming, positioning, discoverability

- Product name: **WinOld Recovery**. Repository: `winold-recovery`. Executable: `WinOldRecovery.exe`.
- README and repo description use the phrases people search for: "Windows.old recovery", "Windows.old restore", "recover files after reinstalling Windows", "recover files from Windows.old", "Windows recovery tool", "selectively restore from Windows.old", "delete Windows.old safely".
- Trademark note in README and About: independent project, no Microsoft affiliation.
- License: MIT (permissive, encourages contribution of recipes). Third-party licenses listed in `THIRD_PARTY_NOTICES.md`.

## 11. Glossary

- **Source root**: the `Windows.old` folder (or any folder containing a `Users` folder from an old Windows) being scanned. Read-only.
- **Node**: a scanned directory or file, or a synthetic item (app card, app component).
- **Decision**: Restore / Leave Behind / Undecided, with inheritance and explicit overrides.
- **Recipe**: app-specific detection + restore + verify logic (for example the Syncthing recipe).
- **Plan**: the concrete list of copy and recipe operations produced by Preview.
- **Journal**: the crash-safe record of plan execution.
- **Purge**: deletion of the source root. Last step, gated.
