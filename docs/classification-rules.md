# Classification rule files

WinOld Recovery badges files from four embedded JSON files in `src/WinOldRecovery.Core/Classification/Rules/`:

- `HighValue.rules.json` — vaults, mail stores, databases, VM disks, editor settings, PowerShell profiles, Joplin/Logseq, NuGet/Maven/Gradle/Cargo config
- `Regeneratable.rules.json` — caches, `node_modules`, installers, Notion AppData caches (badge only)
- `GameSaves.rules.json` — Saved Games, My Games, Steam userdata, Unreal `Saved\SaveGames`, GOG Galaxy Applications
- `Sensitive.rules.json` — keys, `.env`, `.yarnrc.yml`, certs next to a key, cloud CLI credentials, GPG

Badges never decide. Regeneratable and unclassified nodes must not receive a suggested **Leave Behind**. The only built-in Leave Behind suggestion is the whole `Users\<name>\AppData` folder (individual folders under it can still be restored by the user).

## Rule object

| Field | Meaning |
|---|---|
| `id` | Stable id (used in tests and summaries) |
| `kind` | `HighValue`, `Regeneratable`, `GameSave`, or `Sensitive` |
| `badge` | Short label shown on the node |
| `why` | One-line explanation |
| `sensitive` | If true, contents are never hashed into logs or opened for header checks |
| `suggestedDefault` | `Restore` or `Undecided`. Do not use `LeaveBehind` on regeneratable rules |
| `nameGlobs` | Match the file or folder name (`*`, `?`) |
| `pathGlobs` | Match the source-relative path; `**` crosses folders |
| `directoryNames` | Exact directory names (case-insensitive) |
| `pathContains` | Substring of the relative path (`\Downloads\`) |
| `siblingGlobs` | Another name under the same parent must match |
| `childGlobs` | A direct child name must match |
| `minSize` | File size in bytes |
| `headerHex` | Optional magic bytes compared to the first 4 KB. Ignored when `sensitive` is true |

Non-empty matcher groups are combined with AND. Within a group, any glob may match.

Header checks never run on cloud placeholders, EFS, access-denied nodes, or sensitive-by-path files.
