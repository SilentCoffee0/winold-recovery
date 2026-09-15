# Syncthing identity

WinOld Recovery restores `cert.pem` and `key.pem` so other devices still trust this PC, and rewrites `config.xml` so **every folder is paused**. Paths that pointed at the old profile are remapped to the new one. Synced folders that still sit under the old profile are badged `[Syncthing folder: <label>]` on the file tree. Preview **Edit mapping** lets you change those folder paths before restore; paths that already exist or are missing on this PC are labelled. The index database is never copied: restoring an index with incomplete folder data is how Syncthing can delete files on peers.

If a Syncthing home already exists on the new PC, the restore goes to `AppData\Local\Syncthing.recovered`. Point Syncthing at it with `--home`, or swap the folders after you have checked paths. Starting fresh means a new device ID and re-sharing every folder.
