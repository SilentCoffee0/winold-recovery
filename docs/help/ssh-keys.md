# SSH keys

WinOld Recovery copies `.ssh` from the old profile. Existing files on the new PC are kept; a conflict is written as `*.from-windows-old`.

Private keys, `config`, and `authorized_keys` get a user-only ACL after copy. Keys previously loaded into ssh-agent are not in Windows.old in a usable form; run `ssh-add` again. If `ssh.exe` is present, the tool runs `ssh -G localhost` as a config parse check.
