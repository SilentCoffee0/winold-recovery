# GPG keyring

WinOld Recovery copies `AppData\Roaming\gnupg` (and `\.gnupg` if present) except `random_seed`, sockets (`S.*`), `*.lock`, and `crls.d`. After a scan it also lists other folders that contain `pubring.kbx`, `secring.gpg`, or `private-keys-v1.d`. Passphrase-protected keys stay protected.

If this PC already has a `gnupg` folder, the restore goes to `gnupg.from-windows-old`. Import those keys with `gpg --import` after you have checked the files. Close `gpg-agent` first (`gpgconf --kill all`). Verify uses `gpg --list-secret-keys` with `GNUPGHOME` on the restored folder; tests never launch `gpg.exe` against Windows.old.
