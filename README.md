# WinOld Recovery

**Recover what matters from Windows.old. Leave the old Windows behind.**

WinOld Recovery is a free, open-source **Windows.old recovery tool**. After you reinstall, upgrade, or reset Windows, your previous installation sits in `C:\Windows.old` for about ten days. WinOld Recovery scans it, shows you what is in there in plain language, lets you decide item by item what to restore and what to leave behind, restores it safely, verifies the result, and only then helps you delete Windows.old.

> **Status: Milestone 3 in progress.** Scan, Decide UI, classification, and PlanBuilder are implemented. Copy engine, verify, recipes, and purge remain. Interactive FlaUI smoke and elevated/manual M0 checks are still pending, so the M0 tag has not been created.

## What it does

- **Recover files from Windows.old** selectively: Desktop, Documents, Downloads, Pictures, Videos, Music, Saved Games, and any custom folder, with sizes, dates, and Largest / Recent / Search / Unknown views.
- **Understand app data** through smart cards: browsers (Chrome, Edge, Firefox), Syncthing device identity, SSH keys, Git configuration and repositories (with unpushed-work detection), WSL distributions, Anki collections, GPG keys, KeePass databases, mail stores, VM disks, VS Code settings, game saves, and more.
- **Tell the truth about limits**: Chrome and Edge passwords and cookies cannot be recovered after a clean reinstall (they are encrypted with keys that only existed on the old installation). Firefox passwords can. Cloud-only OneDrive placeholders and EFS-encrypted files are flagged, not silently skipped.
- **Keep the new Windows clean**: no registry merging, no whole-profile restore, no dragging along `node_modules`, caches, installers, and other regeneratable data unless you choose to.
- **Stay safe**: Windows.old is read-only until the final purge step, nothing is ever overwritten silently, every restore is previewed, journaled, resumable, and verified, and the purge is gated behind verification and a typed confirmation.

## Workflow

`Scan → Classify → Decide (Restore / Leave Behind / Undecided) → Preview → Restore → Verify → Final purge of Windows.old`

## Documents

| Document | Contents |
|---|---|
| [PRODUCT_SPEC.md](PRODUCT_SPEC.md) | Problem, product statement, v0.1 scope, defaults, constraints |
| [RESEARCH.md](RESEARCH.md) | Evidence with citations: browsers, Syncthing, Anki, WSL, SSH/Git/GPG, Windows platform, prior art |
| [UX_SPEC.md](UX_SPEC.md) | Screens, tree and card behaviour, purge gates, copy rules |
| [ARCHITECTURE.md](ARCHITECTURE.md) | Stack decision, process model, data model, scan and copy engines |
| [RECOVERY_RECIPES.md](RECOVERY_RECIPES.md) | Per-app detect / plan / execute / verify logic |
| [SAFETY_MODEL.md](SAFETY_MODEL.md) | The sixteen invariants, threat model, purge design, secrets handling |
| [IMPLEMENTATION_PLAN.md](IMPLEMENTATION_PLAN.md) | Milestones with work, tests, acceptance criteria |
| [docs/classification-rules.md](docs/classification-rules.md) | Contributor schema for high-value and regeneratable rule files |

## Planned stack

C# on .NET 10 LTS, WPF, portable single-file EXE (no runtime install needed on a fresh Windows), SQLite for scan and decision state, an in-process journaled copy engine, offline registry parsing. Windows 10 (1809+) and Windows 11, x64 and ARM64.

## Related searches this project answers

Windows.old recovery · Windows.old restore · recover files after reinstalling Windows · recover files from Windows.old · restore files from Windows.old folder · delete Windows.old safely · Windows recovery tool · recover WSL distro from Windows.old · restore Syncthing identity after reinstall · SSH keys in Windows.old · Chrome passwords after Windows reinstall

## Disclaimer

WinOld Recovery is an independent community project. It is not affiliated with, endorsed by, or supported by Microsoft Corporation. Windows is a trademark of Microsoft Corporation. Chrome, Edge, Firefox, Syncthing, Anki, and other product names are trademarks of their respective owners.

## License

MIT (to be added with the first code commit).
