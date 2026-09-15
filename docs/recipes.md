# App recipes

Smart cards explain what was found in Windows.old. Leave Behind never deletes; only Purge does. Detected apps also badge matching tree nodes (`[Firefox: fixture]`, `[KeePass: vault.kdbx]`, `[Syncthing folder: Photos]`).

| App | What comes back | Honest limit |
|---|---|---|
| Chrome / Edge | Bookmarks HTML, history, open tabs, extensions list, autofill CSV | Passwords and cookies cannot be recovered |
| Firefox | Profile transplant (`*-recovered`) including session backups, extensions, and site storage; bookmarks; history; open tabs; extensions list; logins | Close Firefox first |
| SSH | Keys with keep-both naming and tight ACLs | Unencrypted keys stay unencrypted |
| Git | Scrubbed `.gitconfig`, repository trees | Credential helpers are stripped from the restored config |
| Syncthing | Device ID + paused config | Index is not copied |
| WSL | `ext4.vhdx` copy | Import is optional and never targets Windows.old |
| Anki | Collection, WAL, media | Trash and media index rebuild |
| GPG | Keyring without `random_seed` | Stop gpg-agent first |
| KeePass, Outlook PST, Thunderbird, VS Code, Terminal, Obsidian | Copy-based restore | OST is left behind (server rebuilds it) |

Deep dives: [browser-passwords.md](help/browser-passwords.md), [ssh-keys.md](help/ssh-keys.md), [git-repositories.md](help/git-repositories.md), [syncthing-identity.md](help/syncthing-identity.md), [wsl.md](help/wsl.md), [anki.md](help/anki.md).
