# WinOld Recovery — UX Specification (v0.1)

This document describes the screens, interaction model, and copywriting rules for WinOld Recovery. It is written for the implementer; wireframes are ASCII and describe layout intent, not pixel design. The visual style is a plain, modern Windows 11 desktop app (WPF Fluent theme), light and dark, keyboard-navigable, high-contrast friendly.

Guiding rules:

1. The user always knows **which step** they are in and **what has and has not been touched on disk**.
2. Every meaningful item exposes the same four verbs: **Inspect**, **Open Folder**, **Restore**, **Leave Behind** (plus **Undecided** to clear).
3. Destructive or irreversible actions live on exactly one screen (Purge) and are gated.
4. Copy explains consequences in plain language. No jargon without a tooltip.

---

## 1. Application shell

```
┌──────────────────────────────────────────────────────────────────────────────┐
│ WinOld Recovery              Source: C:\Windows.old (created 12 Sep 2026)     │
│ [1 Scan] → [2 Decide] → [3 Preview] → [4 Restore] → [5 Verify] → [6 Purge]   │
├──────────────────────────────────────────────────────────────────────────────┤
│                                                                              │
│                          (step content)                                      │
│                                                                              │
├──────────────────────────────────────────────────────────────────────────────┤
│ ● Windows.old untouched   Selected: 41.2 GB of 127 GB free   [Log] [Help]    │
└──────────────────────────────────────────────────────────────────────────────┘
```

- **Step bar**: six tabs (Scan, Decide, Preview, Restore, Verify, Purge). Steps become enabled as prerequisites are met. Going back is always allowed except during a running restore. The Scan source picker is only on the Scan tab so Decide can use the full pane for cards and the file tree.
- **Status strip** (always visible):
  - Source-integrity indicator: "● Windows.old untouched" (green) until purge; during purge "Deleting…"; after purge "Windows.old removed".
  - Space budget: bytes selected for restore vs. free space on the destination volume(s). Turns amber at 90 % of free space minus the safety margin, red when it does not fit.
  - Log, About, and Help buttons. About shows the version (from the git tag when present), commit, build date, and the independent-project trademark note.
- **Window title** includes the source path and step, so screenshots posted in support threads are self-explanatory.
- **Elevation**: the app runs elevated. On launch it explains why in one sentence in the About/first-run dialog ("Files in Windows.old belong to a user account that no longer exists; reading them needs administrator rights.").

Keyboard: `R` = Restore, `L` = Leave Behind, `U` = Undecided, `I` = Inspect, `O` = Open Folder, `Ctrl+F` = search, `Space` = expand/collapse. Actions apply to the focused node or multi-selection.

## 2. Step 1 — Scan

### 2.1 Source picker

```
Where is the old Windows?

 (•) C:\Windows.old          created 12 Sep 2026 (0 days ago)     2 user profiles
 ( ) D:\Windows.old          created 3 Mar 2026                   1 user profile
 ( ) Another folder or drive…  [Browse]

 ⓘ Windows automatically deletes Windows.old about 10 days after setup.
   Estimated deletion: 22 Sep 2026. Finish recovery before then.

 Scan options
 [x] Compute folder sizes and counts (recommended)
 [ ] Hash files smaller than 64 MB during scan (slower; enables strong verify)

                                                   [Scan]
```

- Auto-detects `Windows.old` on every fixed volume, `Windows.old.000`-style variants, and any volume whose root contains `Users` and `Windows` (an old system disk). The user can browse to any folder.
- Source validation: must contain `Users\`; warns (does not block) if it does not look like a Windows installation.
- Age and estimated auto-deletion date shown when the source is a real `Windows.old`.

### 2.2 Scan progress

```
Scanning C:\Windows.old …

 Profiles found: VJ, trrr, SyncthingServiceAcct (service)
 Files: 612,340   Folders: 88,102   Size so far: 401.5 GB
 Skipped: 14 junctions, 3 cloud placeholders, 0 encrypted, 2 access denied

 Currently: Users\VJ\AppData\Local\Steam\…
 [Pause]  [Cancel]
```

- Scan is cancellable and resumable. Partial results are kept.
- "Skipped" counters are live; each has a tooltip explaining what the category means.
- After the scan a short summary card appears: "Windows.old holds 782,433 files (536 GB) across 2 profiles. We found 7 known apps and 23 high-value items. Nothing has been changed."

## 3. Step 2 — Decide

The core screen. Two panes: **Overview/cards** on the left, **detail** on the right. A top toggle switches the left pane between **Apps & folders** (known apps, then personal folders) and **All files** (the tree). **Restore** marks only the selection; items you did not select stay Undecided so you can restore them later. Leave Behind still does not delete.

### 3.1 Cards view

```
┌ Profile: VJ (last used 11 Sep 2026) ─────────────────────────────────────────┐
│                                                                              │
│ ┌ Personal folders ───────────┐ ┌ Firefox (2 profiles) ────────┐             │
│ │ Desktop 2.1 GB   ● Restore  │ │ Bookmarks, history, tabs,    │             │
│ │ Documents 14 GB  ● Restore  │ │ passwords, cookies, add-ons  │             │
│ │ Downloads 88 GB  ○ Undecided│ │ ● Restore  (as new profile)  │             │
│ │ Pictures 31 GB   ● Restore  │ │ [Inspect] [Open] [R] [L]     │             │
│ │ …                           │ └──────────────────────────────┘             │
│ └─────────────────────────────┘ ┌ Chrome (4 profiles) ─────────┐             │
│ ┌ Syncthing (2 identities) ──┐ │ Bookmarks, history, tabs,    │             │
│ │ Device ID ABCD…-XYZ (active)│ │ extensions list.             │             │
│ │ 6 folders, 4 devices        │ │ ⚠ Passwords & cookies cannot │             │
│ │ ● Restore identity+config   │ │   be recovered (encrypted).  │             │
│ └─────────────────────────────┘ └──────────────────────────────┘             │
│ ┌ SSH keys (absent) ──────────┐ ┌ WSL / Docker ────────────────┐             │
│ ┌ Git repositories (0) ───────┐ │ docker_data.vhdx 12.3 GB     │             │
│ ┌ Anki (User 1, 165 MB) ──────┐ │ ○ Undecided                  │             │
│ ┌ High-value items (23) ──────┐ └──────────────────────────────┘             │
│ ┌ Regeneratable (44 folders, 61 GB) ─────────────────────────────┐           │
└──────────────────────────────────────────────────────────────────────────────┘
```

Each **smart card** has a fixed anatomy (see §6):

- Title with instance count (profiles, identities, repos).
- One-line "what is this" and the decision chip (● Restore, ◌ Leave Behind, ○ Undecided; suggested defaults are drawn hollow with a "suggested" tooltip until the user confirms or changes them).
- Key facts (size, last used, counts).
- Warnings (⚠) for anything impossible or risky, phrased as consequences.
- Actions: Inspect, Open Folder, Restore, Leave Behind.

Cards for absent apps are collapsed to one line ("SSH keys: none found in this profile") so the user can see the tool looked.

### 3.2 Files view (tree)

```
 Search: [________]  View: (•) Tree ( ) Largest ( ) Recent ( ) Unknown ( ) Problems
─────────────────────────────────────────────────────────────────────────────
 Name                          Size      Files    Modified     Decision   Badges
 ▾ VJ                          536 GB    782,433  11 Sep 2026  mixed
   ▸ Desktop                   2.1 GB    1,204    10 Sep 2026  ● Restore
   ▾ Documents                 14 GB     40,881   11 Sep 2026  ● Restore
     ▸ Projects                9.8 GB    39,002   11 Sep 2026  ● Restore   [44× node_modules]
     ▸ Scans                   3.1 GB    412      2 Feb 2026   ● (inherited)
       secrets.kdbx            12 KB     1        3 Jan 2026   ● Restore   [KeePass] [High value]
   ▸ Downloads                 88 GB     9,340    11 Sep 2026  ○ Undecided [Installers 21 GB] [ISO 9 GB]
   ▸ AppData                   380 GB    690,120  11 Sep 2026  ◌ Leave     [covered by 7 cards]
   ▸ Sync                      22 GB     18,220   11 Sep 2026  ○ Undecided [Syncthing folder]
   ⊘ Application Data          junction  —        —            —           [points to new install]
```

- **Virtualized** tree; expanding a node with 50,000 children must not freeze. Children beyond 2,000 per node are paged with a "show all" affordance. Folders show ▸/▾ and indent like Explorer; Size and **% of parent** sit next to the name.
- **Decision column** shows the effective decision. Inherited decisions are dimmed and labelled "(inherited)". Mixed subtrees show "mixed" and a small stacked-bar (restore/leave/undecided by bytes).
- **Badges** are classification results, never decisions. Examples: `[KeePass]`, `[High value]`, `[Regeneratable]`, `[Installer]`, `[Cloud placeholder]`, `[Encrypted (EFS)]`, `[Access denied]`, `[Long path]`, `[Git repo: unpushed]`, `[Syncthing folder]`, `[Symlink]`.
- Junctions and symlinks appear greyed with ⊘ and cannot be selected for restore. Tooltip shows the target.
- Multi-select with Shift/Ctrl; a right-click context menu offers the four verbs plus "Restore only files newer than…", "Restore everything except regeneratable folders", "Copy path".
- Views:
  - **Largest**: flat list of the 500 largest folders and 500 largest files under the current node.
  - **Recent**: files modified in the last 7/30/90 days (selector), grouped by folder.
  - **Search**: name substring, glob (`*.kdbx`), or extension list; results are a flat list with a "Reveal in tree" action.
  - **Unknown**: folders that no classifier tagged and that are not standard folders, sorted by size. This is where users find things like `~\Cerebrax` or `~\med-platform`.
  - **Problems**: cloud placeholders, EFS-encrypted, access denied, long paths, invalid names, zero-byte stubs. Each row explains what it means and whether anything can be done.

### 3.3 Detail pane (Inspect)

Selecting a node shows:

- Path (source) and planned destination path (editable per top-level item; sub-items follow).
- Size, file count, oldest/newest modification, owner SID (as "old account, no longer exists"), attributes.
- Classification with explanation ("This folder contains a `.git` directory with 3 commits not on any remote.").
- For app components, the card's explanation block (§6).
- Conflict preview: "Destination already has 1,204 files; 37 differ" (computed on demand).
- Sensitive items show "This file contains secrets. Contents are never displayed or logged."

## 4. Step 3 — Preview

```
Restore plan                                                   [Re-check]

 Destination for profile "VJ":  C:\Users\VJ  (current user)   [Change…]
 Sub-folder policy:  (•) Merge into existing folders   ( ) Restore into C:\Users\VJ\Recovered\

 Items to restore: 1,912 folders, 118,420 files, 41.2 GB
 Free space on C: after restore: 84.6 GB  ✔

 Conflicts: 37 files already exist with different content   [Review]
   Policy: (•) Keep both (restored copy gets " (from Windows.old)" suffix)
           ( ) Skip existing
           ( ) Overwrite existing  ⚠ requires per-file confirmation list

 App recipes:
   ✔ Firefox: 2 profiles → new profiles "VJ (recovered)"; Firefox must be closed
   ✔ Syncthing: identity + config → %LOCALAPPDATA%\Syncthing; 6 folders will be PAUSED;
     2 folder paths remapped (C:\Users\VJ\Sync → C:\Users\VJ\Sync)  [Edit mapping]
   ✔ Anki: profile "User 1" → %APPDATA%\Anki2\User 1; Anki must be closed
   ⚠ Docker Desktop VHDX: 12.3 GB → D:\WSL\docker_data.vhdx; registration is manual

 Cannot be restored (shown for transparency):
   3 cloud-only placeholders (content lives in the cloud account)
   0 EFS-encrypted files
   2 folders with access denied   [Details]

 Undecided items remaining: 14 (96 GB)   [Review undecided]  — they will stay in Windows.old
                                                          until you purge it.

                                                       [Back]   [Start restore]
```

- Preview is a **read-only dry run**: it enumerates the plan, checks destination existence, computes conflicts, checks free space, verifies that target apps are closed, and validates recipe inputs (for example that the Syncthing config parses). It never writes.
- "Start restore" is disabled when the plan does not fit on disk, when any required app is still running, or when the destination is inside the source root.
- Overwrite policy requires reviewing the explicit file list; the button reads "Overwrite these 37 files".

## 5. Step 4 — Restore, Step 5 — Verify

### 5.1 Restore progress

```
Restoring…  38 %   12.4 GB of 41.2 GB   ~9 min left

 ✔ Personal folders › Desktop            1,204 files    done
 ▶ Personal folders › Documents         18,102 / 40,881 files
 ○ Firefox profiles                       waiting
 ○ Syncthing identity                     waiting
 ○ Anki                                   waiting

 Warnings (2)   [Show]
 [Pause]  [Cancel — keeps what is already copied]
```

- Each top-level item is a job with its own status; failures in one job do not cancel others.
- Cancel is safe: partially copied files are recorded in the journal as incomplete and are re-copied on resume. Windows.old is never affected by cancel.
- If the app crashes or the PC reboots, next launch shows "A restore was interrupted on 12 Sep 2026 at 20:14. Resume?" with the journal summary.
- Disk full mid-restore: the job pauses with "C: is full. Free 6.2 GB and click Resume, or Cancel." Nothing is deleted to make room.

### 5.2 Verify report

```
Verification                                                        [Re-run]

 118,420 files verified by size and timestamp   ✔
   4,120 files verified by SHA-256 (sample + all files under 64 MB in sensitive items)   ✔
 Firefox: 2 profiles registered in profiles.ini; places.sqlite opens; 1,932 bookmarks   ✔
 Syncthing: config parses; device ID matches certificate; 6 folders paused   ✔
 Anki: collection opens read-only; 41,208 notes; media folder 18,202 files   ✔
 Docker VHDX: size matches, SHA-256 matches   ✔

 0 failures, 2 warnings   [Details]

 You can now open the restored files and apps and confirm they work.
 Purge is available once every restore job is verified.        [Go to Purge]
```

- A verify failure marks the item and blocks the purge gate until the item is re-restored, re-verified, or explicitly acknowledged (with a reason recorded).

## 6. Smart card anatomy

Every known app or high-value data type is rendered by the same card template with recipe-provided content. The copy must answer the six questions in this order:

```
┌ Syncthing — 2 identities found ──────────────────────────────────────────────┐
│ What this is     Syncthing's device identity (cert.pem/key.pem) and its      │
│                  configuration (config.xml: folders, devices, settings).     │
│ Why it matters   Other devices trust this identity. Without it you must       │
│                  re-pair every device and re-share every folder.             │
│ What we restore  Identity + config. Folders will be added PAUSED so nothing  │
│                  syncs until you check paths. Index database is regenerated. │
│ Cloud restorable No. Identity exists only on this machine.                   │
│ Regenerable      Identity: no. Config: by hand. Index: yes (automatic).      │
│ If left behind   New Syncthing install gets a new device ID; you re-pair.    │
├──────────────────────────────────────────────────────────────────────────────┤
│ Instances                                                                    │
│  (•) %LOCALAPPDATA%\Syncthing   last active 11 Sep 2026  6 folders 4 devices │
│  ( ) ProgramData\Syncthing      last active 10 Aug 2024  2 folders 1 device  │
│ Components                                                                   │
│  [x] Identity (cert.pem, key.pem)          ● Restore                         │
│  [x] Configuration (config.xml)            ● Restore (paused folders)        │
│  [ ] GUI TLS certificate (https-*.pem)     ◌ Leave (regenerated)             │
│  [–] Index database                        regenerated automatically         │
│                                                                              │
│ [Inspect]  [Open Folder]  [Restore]  [Leave Behind]                          │
└──────────────────────────────────────────────────────────────────────────────┘
```

- Components are individually decidable; some are fixed ("regenerated automatically", "cannot be recovered") and rendered without a decision control.
- Impossible items are stated as facts with a reason and, where one exists, the alternative ("Sign in to your Google account to sync passwords").
- Cards never show secret values. They may show derived, non-secret identifiers (Syncthing device ID, SSH public key fingerprint, Git remote URL with credentials stripped).

## 7. Step 6 — Purge

```
Delete C:\Windows.old

 Before you continue
  ✔ All 6 restore jobs verified (12 Sep 2026 20:41)
  ✔ Restored files opened at least once?  [ ] I have checked my restored files
  ⚠ 14 items are still Undecided (96 GB). They will be deleted with Windows.old.
     [Review undecided]   [ ] I understand these will be lost
  ✔ Free space after purge: 663 GB

 How to delete
  (•) Use Windows "Previous Windows installation(s)" cleanup (recommended)
  ( ) Delete the folder directly (slower; use if Windows cleanup is unavailable)

 Type the folder name to confirm:  [                    ]   (Windows.old)

 A copy of your decisions, the restore log, and the verify report is kept at
 %LOCALAPPDATA%\WinOldRecovery\sessions\2026-09-12\.

                                                  [Cancel]   [Delete Windows.old]
```

- The button is disabled until: every job verified (or acknowledged failures with reasons), the "checked my files" box is ticked, any Undecided items are acknowledged, and the typed name matches.
- Purge runs the Windows cleanup handler when possible; otherwise a manual deletion that takes ownership and removes the tree, with progress and the ability to cancel (leaving a partially deleted tree, clearly reported).
- After purge, the status strip switches to "Windows.old removed" and the session becomes read-only.

## 8. Copy and tone

- Second person, present tense, short sentences. "Firefox must be closed before restore." not "It is required that the browser be terminated."
- Say what happens, then why. "Folders are added paused so nothing syncs before you check the paths."
- Never say "safe to delete". Say "regeneratable" or "re-downloadable" with what regenerates it.
- Numbers are formatted with units and thousands separators; dates are absolute ("11 Sep 2026"), relative in parentheses only for age.
- Words to avoid: "junk", "garbage", "migrate", "clean up" (for user data). Use "regeneratable", "leave behind", "restore".

## 9. Accessibility and resilience

- All controls reachable by keyboard; tree supports type-ahead.
- Minimum window 1024×640; panes collapse to tabs below 1200 px.
- Long paths and very long file names are middle-ellipsized with full path in tooltip and Inspect pane.
- Text scales with system DPI and font size.
- The UI must remain responsive with 1,000,000 nodes: all heavy work is off the UI thread, tree data is loaded from SQLite on demand.

## 10. First-run and Help

- First run: a one-screen explanation of the six steps and the two promises ("We never change Windows.old until you choose to delete it in the last step. We never overwrite your files silently.").
- Help links open local Markdown rendered in-app (no network): "What can and cannot be recovered", "Browser passwords after reinstall", "Syncthing identity", "Git repositories", "WSL".
