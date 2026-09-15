# WSL disks

WSL 2 distros live in an `ext4.vhdx`. WinOld Recovery copies that file to `AppData\Local\wsl\recovered\<name>\` and checks the `vhdxfile` header. The card records file size, allocated size, and last-modified time. It never runs `wsl.exe` against Windows.old.

Registration (`wsl --shutdown` then `wsl --import-in-place`) is optional and goes through the process runner so tests never launch WSL. WSL 1 `rootfs` trees are detected as manual-only: a normal copy drops Linux metadata. Docker Desktop disks are copied only, default Undecided, with no automatic Docker registration.
