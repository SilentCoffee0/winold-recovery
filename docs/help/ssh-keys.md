# SSH keys

WinOld Recovery copies `.ssh` from the old profile. Existing files on the new PC are kept; a conflict is written as `*.from-windows-old`.

If `Windows.old\ProgramData\ssh` has host keys, a separate **OpenSSH Server** card lists them (default Undecided). Restoring copies them to `Recovered\OpenSSH-Server` so you can review them before replacing the live `C:\ProgramData\ssh`. Host key material is never shown on the card.

Private keys, `config`, and `authorized_keys` get a user-only ACL after copy. Keys previously loaded into ssh-agent are not in Windows.old in a usable form; run `ssh-add` again. If a restored OpenSSH key has no passphrase (`none` cipher in the file header), the card says so — add one with `ssh-keygen -p`. If `ssh.exe` is present, the tool runs `ssh -G localhost` as a config parse check.
