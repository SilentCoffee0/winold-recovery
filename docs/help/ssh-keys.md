# SSH keys

WinOld Recovery copies `.ssh` from the old profile. Existing files on the new PC are kept; a conflict is written as `*.from-windows-old`.

If `Windows.old\ProgramData\ssh` has host keys, a separate **OpenSSH Server** card lists them (default Undecided). Restoring copies them to `Recovered\OpenSSH-Server` so you can review them before replacing the live `C:\ProgramData\ssh`. Host key material is never shown on the card.

Private keys, `config`, and `authorized_keys` get a user-only ACL after copy (SYSTEM and Administrators are allowed). Verify fails if Everyone, Users, or Authenticated Users still have Allow access. Keys previously loaded into ssh-agent are not in Windows.old in a usable form; run `ssh-add` again. If a restored OpenSSH key has no passphrase (`none` cipher in the file header), the card says so — add one with `ssh-keygen -p`. If a private key has the Windows EFS attribute, the card warns that the copy may be unreadable. If `ssh.exe` is present, verify runs `ssh -G localhost` against the restored `.ssh` folder and fails when that parse check exits non-zero. Execute still records the same request after copy.
