# WSL disks

WSL 2 distros live in an `ext4.vhdx`. WinOld Recovery copies that file to `AppData\Local\wsl\recovered\<name>\` and checks the `vhdxfile` header. The card records file size, allocated size, last-modified time, and (when the old hive has `Lxss`) the distro name, WSL version, and DefaultUid. It may ask the process runner whether `wsl.exe --version` succeeds on this PC. It never runs `wsl --import-in-place` against Windows.old.

Registration (`wsl --shutdown` then `wsl --import-in-place`) is optional and goes through the process runner so tests never launch WSL. WSL 1 `rootfs` trees are detected as manual-only: a normal copy drops Linux metadata. Docker Desktop disks are copied only, default Undecided, with no automatic Docker registration.
