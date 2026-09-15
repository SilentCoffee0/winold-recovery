# WinOld Recovery — Research

Consolidated findings behind the product, UX, architecture, recipes, and safety documents. Research was done in September 2026 against vendor documentation (Microsoft Learn, Chromium/Google security blog, Mozilla support, Syncthing docs and source, Anki manual, Win32-OpenSSH wiki, git-scm.com, GnuPG manual, Docker docs) and against a **real Windows.old** on the development machine (Windows 11 25H2, reinstalled 12 Sep 2026). Claims are tagged **[verified]** (read from the cited primary source), **[ground truth]** (observed on the real machine), or **[unverified]** (secondary sources or inference; to be confirmed during implementation).

---

## 1. Ground truth from a real Windows.old

The development machine's `C:\Windows.old` was inspected read-only (directory structure only, no file contents). Observations that shaped the design:

| Observation | Design consequence |
|---|---|
| Top level: `$Recycle.Bin`, `inetpub`, `PerfLogs`, `Program Files`, `Program Files (x86)`, `ProgramData`, `Recovery`, `Users`, `Windows`. `Users` held `Default`, `Public`, `VJ`, `trrr`, `SyncthingServiceAcct`, plus `All Users`/`Default User` junctions. | Multiple profiles are normal; service accounts exist and must be separated from human profiles. |
| The profile `VJ` held **782,433 files, 107,678 folders, 536 GB**; a plain `robocopy /L` enumeration took **18 s** on NVMe. The system volume had **127 GB free**. | Enumeration via normal directory APIs is fast enough (no MFT parser needed). Copy-based restore of everything is impossible on this machine, so the **space budget** is a first-class UI element and the "move" question is a real product decision (deferred; see SAFETY_MODEL §3.2). |
| Legacy junctions inside the old profile (`Application Data`, `Local Settings`, `My Documents`, `Cookies`, `NetHood`, `PrintHood`, `Recent`, `SendTo`, `Start Menu`, `Templates`, `AppData\Local\Application Data`, `History`, `Temporary Internet Files`) all had **absolute targets in `C:\Users\VJ\…`, i.e. the live, new profile**. `iCloudDrive` was a reparse point with no printable target (cloud files). | Following reparse points would scan the new install and could "restore" it onto itself. Invariant I11. |
| Three Syncthing homes at once: `AppData\Local\Syncthing` (active, `index-v2\`, SyncTrayzor-managed), `AppData\Roaming\SyncTrayzor` (SyncTrayzor's own config + `syncthing.exe`), `ProgramData\Syncthing` (2024 service install, `index-v0.14.0.db\`, run by `SyncthingServiceAcct`), with **different certificates**. | The Syncthing card must list instances with last-activity and device ID, not assume one home. |
| Anki: `Anki2\prefs21.db`, `addons21\`, profile `User 1` with `collection.anki2` (165 MB), `collection.anki2-wal` (0 B), `collection.media\`, `collection.media.db2`, `backups\`, `media.trash\`, `deleted.txt`, plus add-on databases inside the profile. | Recipe R8 file list. |
| Firefox: two installations (two `[Install…]` sections), three profiles, one empty; profiles contained `key4.db`, `logins.json`, `logins-backup.json` **and `logins.db`**, `sessionstore.jsonlz4`, `storage-sync-v2.sqlite`. | Firefox allow-list must include `logins.db` (newer logins storage; **[unverified]** exact version that introduced it). |
| Chrome: `Default`, `Profile 2`, `Profile 3`, `Profile 6`, `Local State`, `Sessions\`, `Login Data`, `Network\`, `Secure Preferences`, and unrelated files dropped into `User Data` (`7z.exe`, `wtime.cmd`, …). Edge similar. | Browser recipes copy **allow-listed files only**, never whole directories. |
| Docker Desktop: `AppData\Local\Docker\wsl\disk\docker_data.vhdx` (12.3 GB) and `wsl\main\ext4.vhdx`. No Store WSL packages, no `AppData\Local\wsl`. WSL was not installed on the new system; neither was Git. | WSL/Git recipes must work without `wsl.exe`/`git.exe` present (detect, copy, defer registration/analysis). |
| `.ssh`, `.gitconfig`, `gnupg` absent; `.docker`, `.config`, `.vscode`, `Saved Games` (17 publishers), `Calibre Library`, `Zotero`, `Sync`, many custom root folders present. | "Unknown" view and custom-folder handling matter as much as known apps. |
| 44 `node_modules` directories outside AppData. | Regeneratable badges are common; never auto-decide them. |
| `SetupCleanupTask` present, enabled, next run the following morning; `VolumeCaches\Previous Installations` registry key present with `SetupPrevInst=1`; `cleanmgr.exe` present; `LongPathsEnabled=0`. | 10-day warning is real; OS cleanup handler is usable for purge; long paths must not depend on the registry setting. |
| Robocopy on 25H2 supports `/XJ /XJD /XJF /SL /COPY: /DCOPY: /UNILOG /BYTES /MT /IoMaxSize /B /ZB /EFSRAW /LFSM /XC /XN /XO /IM /IT /256 /NOOFFLOAD`. | The optional robocopy adapter can rely on these. |

## 2. Browsers

### 2.1 Chromium (Chrome, Edge)

- Profile root `%LOCALAPPDATA%\Google\Chrome\User Data\` and `%LOCALAPPDATA%\Microsoft\Edge\User Data\`; profiles `Default`, `Profile N`; `Local State` holds `profile.info_cache` and the `os_crypt` keys. **[verified]** Chromium docs: https://chromium.googlesource.com/chromium/src/+/HEAD/docs/user_data_dir.md
- **Passwords, cookies, payment cards are not recoverable after a reinstall.** Chrome 80–126 wrapped the AES key in `Local State` with **user DPAPI** (`v10` blobs); recovery would need the old master key plus the old password, via credential-extraction tooling, and fails for Microsoft-account logins. Chrome/Edge **127+ (30 July 2024)** use **App-Bound Encryption** (`v20` blobs): the key is wrapped by **SYSTEM DPAPI** and released only by an elevation service that validates the calling binary. SYSTEM keys are new after reinstall and never password-derived, so this is irrecoverable by any supported means. **[verified]** Google security blog https://security.googleblog.com/2024/07/improving-security-of-chrome-cookies-on.html ; Edge policy page states data is portable only when ABE is disabled https://learn.microsoft.com/en-us/deployedge/microsoft-edge-browser-policies/applicationboundencryptionenabled ; enterprise migration vendors confirm DPAPI-protected browser passwords are lost on account change https://kb.powersyncpro.com/en_US/migration-agent/browser-passwords-and-bookmarks
- Recoverable: `Bookmarks` (JSON; Chrome imports only Netscape HTML, so the tool converts), `History` (SQLite; no file import exists, export only), `Sessions\Session_*`/`Tabs_*` (SNSS: magic `SNSS`, int32 version 1 or 3, records `uint16 size, uint8 id, pickle`), `Web Data` autofill (except cards), `Extensions\<id>\<ver>\manifest.json` (list + store links), `Local Storage\leveldb`, `IndexedDB`. **[verified/secondary]** SNSS parsers: https://github.com/lemnos/chrome-session-dump , https://github.com/cclgroupltd/ccl-ssns
- Do not copy `Secure Preferences`: HMACs are keyed to the SID; copying yields "Some settings were reset". **[verified]** https://www.cse.chalmers.se/~andrei/cans20.pdf
- Profile version guard: an older Chrome refuses a profile written by a newer one. **[secondary]**

### 2.2 Firefox

- `%APPDATA%\Mozilla\Firefox\profiles.ini` + `installs.ini`; profiles under `Profiles\`. Mozilla supports Windows→Windows profile transplant by copying the profile while Firefox is closed. **[verified]** https://support.mozilla.org/en-US/kb/back-and-restore-information-firefox-profiles , https://support.mozilla.org/en-US/kb/profiles-where-firefox-stores-user-data
- **Firefox does not use DPAPI**; `key4.db` + `logins.json` are protected by the Primary Password (empty by default), so copying both recovers passwords. The DPAPI request (Bugzilla 719548) was never implemented. **[verified]** https://bugzilla.mozilla.org/show_bug.cgi?id=719548
- mozLz4 = `mozLz40\0` + uint32 LE size + LZ4 block (sessions, bookmark backups, `search.json.mozlz4`). **[secondary]** https://blog.dend.ro/decoding-firefox-session-store-data/
- Firefox 135+ profile groups: a `StoreID=` line in `profiles.ini` or `installs.ini` is treated as groups mode. Transplant still copies the profile folder; `profiles.ini` is not appended. The user registers it in `about:profiles`. **[verified in-product]** Mozilla's current KB still documents classic `[ProfileN]` registration; groups UI remains **[secondary]**.

## 3. Syncthing

- Default Windows home `%LOCALAPPDATA%\Syncthing`; `%APPDATA%\Syncthing` only when `LocalAppData` is unset. Files: `config.xml`, `cert.pem`, `key.pem`, `https-cert.pem`, `https-key.pem`, `index-v2\` (2.0+, SQLite) or `index-v0.14.0.db\` (1.x, LevelDB), `csrftokens.txt`, logs, `syncthing.lock`. **[verified]** https://docs.syncthing.net/users/syncthing.html , https://raw.githubusercontent.com/syncthing/syncthing/main/lib/locations/locations.go , https://github.com/syncthing/syncthing/releases/tag/v2.0.0
- Device ID = SHA-256 of the DER certificate, base32, Luhn-grouped; restoring `cert.pem`+`key.pem` keeps the ID; IDs are not secret, `key.pem` is. **[verified]** https://docs.syncthing.net/dev/device-ids.html , https://docs.syncthing.net/users/faq.html
- Folder marker: missing `.stfolder` stops the folder; once it reappears "missing files will be considered deletions". Hence: pause all folders, restore data before unpausing, never restore the index with incomplete data. **[verified]** https://docs.syncthing.net/intro/gui.html , FAQ
- `paused="true"` attribute on `<folder>`; `<defaults><folder path>` replaced `options/defaultFolderPath`; older binary refuses newer config unless `--allow-newer-config`; 2.x requires double-dash options. **[verified]** https://docs.syncthing.net/users/config.html , forum thread quoting the error https://forum.syncthing.net/t/config-file-version-37-is-newer-than-supported-version-35/20827
- SyncTrayzor: own config in `%APPDATA%\SyncTrayzor`, Syncthing home `%LOCALAPPDATA%\Syncthing` by default, custom home path configurable; project unmaintained (community v2 fork). **[verified]** https://github.com/canton7/SyncTrayzor . Syncthing Windows Setup (Bill Stewart) administrative install: `C:\ProgramData\Syncthing`, service account `SyncthingServiceAcct`. **[verified]** https://github.com/Bill-Stewart/SyncthingWindowsSetup

## 4. Anki

- Base `%APPDATA%\Anki2` (override `-b` / `ANKI_BASE`); never copy while Anki is open. **[verified]** https://docs.ankiweb.net/files.html
- Backups in `<profile>\backups\*.colpkg`; restore via File → Switch Profile → Open Backup or File → Import; automatic backups exclude media; restoring from backup disables auto-sync/backups until re-enabled. 2.1.50+ backups are not readable by older Anki; older Anki refuses newer collections (use "Downgrade & Quit" beforehand). **[verified]** https://docs.ankiweb.net/backups.html , https://betas.ankiweb.net/anki2.1.50.html
- `collection.anki2-wal` must travel with the collection; `collection.media.db2` and `media.trash` are regenerable (Tools → Check Media). **[ground truth + secondary]**

## 5. WSL and Docker Desktop

- Locations: Store distros `%LOCALAPPDATA%\Packages\<PFN>\LocalState\ext4.vhdx`; tar-based distros (WSL 2.4.4+) under `%LOCALAPPDATA%\wsl\{GUID}\`; imported distros anywhere; Docker Desktop `%LOCALAPPDATA%\Docker\wsl` (`disk\docker_data.vhdx`, `main\ext4.vhdx`). **[verified]** https://learn.microsoft.com/en-us/windows/wsl/disk-space , https://learn.microsoft.com/en-us/windows/wsl/build-custom-distro , https://docs.docker.com/desktop/features/wsl/ ; the `{GUID}` layout is **[user-reported]** https://github.com/microsoft/WSL/issues/14181
- Registry `HKCU\Software\Microsoft\Windows\CurrentVersion\Lxss\{GUID}` (`DistributionName`, `BasePath`, `Version`, `DefaultUid`, `Flags`, `PackageFamilyName`), readable offline from `NTUSER.DAT`. **[verified for DistributionName/BasePath; other values secondary]**
- Commands: `wsl --install --no-distribution`; `wsl --import-in-place <Name> <vhdx>` (0.58+, registers in place, WSL 2 only); `wsl --import <Name> <Location> <vhdx> --vhd` (copies); default user after import is root; fix via `/etc/wsl.conf` `[user] default=` (all versions) or `wsl --manage <d> --set-default-user` (2.4.4+, applicability to arbitrary imported VHDX **[unverified]**). **[verified]** https://learn.microsoft.com/en-us/windows/wsl/basic-commands , https://learn.microsoft.com/en-us/windows/wsl/use-custom-distro , https://learn.microsoft.com/en-us/windows/wsl/wsl-config , https://github.com/microsoft/WSL/releases/tag/0.58.0 , https://github.com/microsoft/WSL/releases/tag/2.4.4
- WSL 1 `rootfs` directories carry Linux metadata in NTFS extended attributes; Windows copies lose it; Microsoft forbids touching them with Windows tools. Manual/unsupported in v0.1. **[verified]** https://devblogs.microsoft.com/commandline/do-not-change-linux-files-using-windows-apps-and-tools/
- Docker Desktop's data disk cannot be `--import-in-place`d as a normal distro; the community practice is a stopped-Docker file swap. **[unverified]** Copy-only in v0.1.

## 6. SSH, Git, GPG

- Win32-OpenSSH: private keys and the user's `config` must be owned by the user with no other user access; `Repair-UserKeyPermission` in the OpenSSHUtils module does the fix; ssh-agent keys live DPAPI-protected in `HKCU\Software\OpenSSH\Agent\Keys` and are not recoverable. **[verified]** https://github.com/PowerShell/Win32-OpenSSH/wiki/Security-protection-of-various-files-in-Win32-OpenSSH , https://github.com/PowerShell/Win32-OpenSSH/wiki/OpenSSH-utility-scripts-to-fix-file-permissions ; agent storage **[secondary]** https://blog.ropnop.com/extracting-ssh-private-keys-from-windows-10-ssh-agent/
- Git: `.git-credentials` is plaintext by design; GCM tokens are in Windows Credential Manager (DPAPI, unrecoverable). `safe.directory` is honoured in command scope (`-c`), `*` allows all; `git status --porcelain=v2 --branch --show-stash` is stable; `--no-optional-locks` avoids writing the refreshed index; `git log --branches --not --remotes` lists unpushed commits; `%(upstream:track)` shows ahead/behind/gone. **[verified]** https://git-scm.com/docs/git-credential-store , https://git-scm.com/docs/git-config , https://git-scm.com/docs/git-status , https://git-scm.com/docs/git-for-each-ref , https://git-scm.com/docs/git-rev-list
- LibGit2Sharp has recurring single-file publish failures (issues 1754/1767/1798/1899); not used. **[verified issue titles]**
- GnuPG home `%APPDATA%\gnupg` (`GNUPGHOME` override); back up `private-keys-v1.d`, `pubring.kbx`, `trustdb.gpg` (or ownertrust), `openpgp-revocs.d`, `trustlist.txt`, configs; skip `random_seed`. **[verified]** https://www.gnupg.org/documentation/manuals/gnupg/GPG-Configuration.html , https://www.gnupg.org/documentation/manuals/gnupg/Agent-Configuration.html

## 7. Windows platform facts

- **Backup semantics instead of ACL surgery.** `SeBackupPrivilege` + `FILE_FLAG_BACKUP_SEMANTICS` bypass DACLs for read without changing anything. .NET's `FileStream`/`FileOptions` cannot pass that flag (dotnet/runtime #27086, open), so file opens on the source use P/Invoke `CreateFileW`; directory enumeration through `FileSystemEnumerator` already opens directories with backup semantics. **[verified]** https://github.com/dotnet/runtime/issues/27086 , https://github.com/dotnet/runtime/blob/main/src/libraries/System.Private.CoreLib/src/System/IO/Enumeration/FileSystemEnumerator.Windows.cs
- **Reparse points.** Default `EnumerationOptions.AttributesToSkip` is `Hidden | System` and does not skip reparse points. Cloud placeholders carry `FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS` (0x400000) / `RECALL_ON_OPEN` (0x40000) / `OFFLINE` (0x1000); opening them triggers hydration; `FILE_FLAG_OPEN_NO_RECALL` avoids it. AppExecLink (0x8000001B) and LX symlink (0xA000001D) tags exist inside profiles. **[verified]** https://learn.microsoft.com/en-us/windows/win32/fileio/reparse-point-tags , https://learn.microsoft.com/en-us/windows/compatibility/placeholder-files
- **EFS.** `FileAttributes.Encrypted`; unreadable without the old certificate whose private key is DPAPI-protected; `/EFSRAW` preserves ciphertext only. Flag and skip in v0.1. **[verified]**
- **Long paths.** .NET (Core) performs no `MAX_PATH` check, but the OS still needs either `LongPathsEnabled=1` + `longPathAware` manifest **or** the `\\?\` prefix. The prefix works regardless of policy; the tool always uses it. **[verified]** https://learn.microsoft.com/en-us/dotnet/standard/io/file-path-formats , https://learn.microsoft.com/en-us/windows/win32/fileio/maximum-file-path-limitation
- **Elevation.** `requireAdministrator`; UIPI blocks drag-drop from non-elevated Explorer; `explorer.exe /select,` from an elevated process opens at normal integrity. **[verified/secondary]**
- **Enumeration speed.** `FileSystemEnumerator<T>` with a 64 KB buffer reads attributes/size/times from the directory buffer without per-file syscalls; comparable to `FindFirstFileEx` with `FIND_FIRST_EX_LARGE_FETCH` (~50–150k entries/s on NVMe). MFT parsing (`FSCTL_ENUM_USN_DATA`) is faster but has no trusted managed library and is NTFS-only; deferred. **[verified/secondary]**
- **Robocopy.** Exit codes: `<8` success, bit 8 = some files failed, ≥16 fatal; output is localized (parse exit codes and `/BYTES` values, not messages); no verify option; `/COPY:DAT /DCOPY:DAT` copies no ACL/owner; `/B` backup mode; `/LFSM` incompatible with `/MT`. **[verified]** https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/robocopy , https://learn.microsoft.com/en-us/troubleshoot/windows-server/backup-and-storage/return-codes-used-robocopy-utility
- **Purge.** Disk Cleanup handler "Previous Installations": set `StateFlagsNNNN=2` under `HKLM\…\Explorer\VolumeCaches\Previous Installations` and run `cleanmgr /sagerun:NNNN`; `cleanmgr /autoclean` also removes upgrade leftovers. **[verified]** https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/cleanmgr , https://learn.microsoft.com/en-us/troubleshoot/windows-server/backup-and-storage/automating-disk-cleanup-tool
- **10-day auto-delete.** Microsoft: "Ten days after you upgrade to Windows, your previous version of Windows is automatically deleted." Mechanism `\Microsoft\Windows\Setup\SetupCleanupTask` **[verified + ground truth]**; the window was 30 days before Windows 10 1607 **[secondary]**; the task frequently fails to fire **[Q&A threads]**; the only extension technique is editing the task trigger (unsupported) **[secondary]** https://support.microsoft.com/en-us/windows/deployment/install-upgrade/delete-your-previous-version-of-windows , https://learn.microsoft.com/en-us/archive/blogs/uktechnet/extend-the-automatic-deletion-of-windows-10-feature-update-backups
- **Windows.old naming.** `Windows.old`, `Windows.old.000`, …; `$WINDOWS.~BT`/`$WINDOWS.~WS` are setup temp folders and not sources; may be on another volume; BitLocker only matters for other volumes. **[verified/secondary]**
- **Windows.old creation.** Upgrades, custom installs without formatting, reinstall over existing, and Reset "Keep my files" (community-verified; not in Microsoft's OEM docs). Microsoft's own "retrieve files" article is a 10-step Explorer walkthrough that ignores AppData and wrongly implies Windows.old never exists after clean install or Reset. **[verified]** https://support.microsoft.com/en-us/windows/retrieve-files-from-the-windows-old-folder-after-a-windows-upgrade-f668ada4-701b-204a-73c3-952bc5ceb1c8

## 8. Stack research

- .NET 10 released 11 Nov 2025, LTS to 10 Nov 2028. **[verified]** https://devblogs.microsoft.com/dotnet/announcing-dotnet-10/
- WPF on .NET 10: supported, Fluent theme continues to expand (`ThemeMode` in XAML; code-setting is behind `WPF0001`); single-file self-contained publish works; **trimming is disabled for WPF** (dotnet/wpf#3811), so the EXE is 70–150 MB. **[verified]** https://learn.microsoft.com/en-us/dotnet/core/deploying/trimming/incompatibilities
- WinUI 3: elevation is unreliable for self-contained unpackaged apps, pickers crash elevated, single-file requires self-extraction. Rejected. **[verified/community]**
- Avalonia: viable, trimmable, but tree virtualization needs `TreeDataGrid`; cross-platform not needed. Second choice.
- SQLite via `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlite3` works in single-file with native extraction; LiteDB is the zero-native fallback if Defender false positives on extraction become a problem (dotnet/runtime #46312). **[verified]**
- `Registry` NuGet (Eric Zimmerman): MIT, offline hive parser, 2026.x releases target net10. `RegLoadKey`/`RegLoadAppKey` write to the hive/logs. **[verified]** https://github.com/EricZimmerman/Registry , https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regloadkeyw
- Code signing: SignPath Foundation (free for OSS) or Azure Trusted Signing; unsigned elevated single-file EXEs trigger SmartScreen. **[verified]** https://signpath.io/solutions/open-source-community

## 9. Prior art

| Tool | What it is | Windows.old? | Selective triage? | App-data aware? | Notes |
|---|---|---|---|---|---|
| USMT | Microsoft CLI migration engine (ADK) | Yes: `ScanState /OfflineWinOld:<dir>` | XML rules only | Only apps in `MigApp.xml`; no passwords | Error 36/71 traps; hard-link stores from Windows.old fail against a different live OS; whole-profile mindset. https://learn.microsoft.com/en-us/windows/deployment/usmt/offline-migration-reference |
| SuperGrate | GPL-3 C# WinForms GUI over USMT for IT admins (belowaverage-org) | No | Per user | No | Last release Dec 2024. https://github.com/belowaverage-org/SuperGrate |
| Migrate-WindowsUserProfile | GPL-3 PowerShell USMT wrapper | No | Folder list | No | Dead since 2020. https://github.com/nickrod518/Migrate-WindowsUserProfile |
| ReinstallSafe, MigrationMerlin | **Not found** anywhere; treated as non-existent | — | — | — | If the user has URLs, check directly. |
| Transwiz / User Profile Wizard (ForensiT) | Freeware/commercial live-profile movers | No | No | No | ZIP-the-profile model. |
| PCmover Professional (Laplink) | Commercial, $59.95 | **Yes** ("Upgrade Assistant") | App/file lists | No | Claims program restore; opaque; no verify/purge. |
| Zinstall Migration Kit Pro | Commercial | **Yes** ("In-place Upgrade from Windows.old") | Advanced menu | No | Program-centric. |
| Windows built-ins | "Go back", Storage cleanup, Windows Backup app | Rollback/delete only | No | No | 10-day timer users cannot see. |
| GitHub profile scripts | Copy-the-six-folders PowerShell | No | No | No | Stale. |

**Gap (confirmed):** no open-source or commercial tool scans Windows.old, classifies contents, keeps restore/leave/undecided decisions, previews conflicts, verifies, and gates the purge; none understands SSH keys, Syncthing identity, WSL disks, or Anki; none is honest up front about unrecoverable browser secrets.

**Risks learned from others:** the Windows 10 1809 Known Folder Redirection deletion (never delete "empty duplicates"; read the old `User Shell Folders`), Reset "Keep my files" losing Downloads (scan everything, not six folders), USMT LoadState half-failures (never write `ProfileList`, never load hives), `/COPYALL` dragging orphan SIDs into the new profile, copying SQLite while the app is open (check running apps, run integrity checks), junction recursion creating deep trees in destinations.

## 10. SEO and positioning

Queries people use: "recover files from windows.old", "windows.old restore", "restore files from windows.old folder", "reinstalled windows lost files", "recover files after reinstalling windows 11", "windows.old not deleting after 10 days", "delete windows.old windows 11", "you require permission from trustedinstaller windows.old"; developer long tail: "recover wsl distro from windows.old", "ext4.vhdx windows.old", "syncthing restore cert.pem after reinstall", "ssh keys windows.old", "chrome passwords windows.old". Today's results are data-recovery vendor pages (EaseUS, MiniTool, Recoverit, HandyRecovery) with thin, sometimes outdated content, plus Microsoft's Explorer walkthrough. No open-source project ranks. A README and docs answering "what is in Windows.old, what can and cannot come back, how to purge safely", with per-app pages (WSL, Syncthing, SSH, Anki, Firefox vs Chrome), targets an uncontested niche.

## 11. Open items carried into implementation

| Item | Where used | Plan |
|---|---|---|
| Firefox `logins.db` semantics and profile-groups `profiles.ini` changes | R3 | Test against current Firefox in Milestone 4; fall back to export-only registration guidance. |
| Chrome accepting a new profile dir containing only `Bookmarks` + `info_cache` entry | R2 | Test in Milestone 4; ship export-only if it fails. |
| `wsl --manage --set-default-user` on imported VHDX | R7 | Try, then fall back to `wsl.conf`. |
| Docker Desktop data-disk swap procedure | R7 | Copy-only; manual instructions. |
| `cleanmgr /sagerun` timing on 25H2 | Purge | Poll for folder disappearance with timeout; fallback to manual deletion. |
| `Registry` package inside single-file bundle | Scan | Milestone 0 spike. |
| Extending the 10-day cleanup task | Product | Not in v0.1; research a reversible, clearly-labelled opt-in for v0.2. |
