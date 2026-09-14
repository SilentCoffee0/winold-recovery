# Git repositories

`.gitconfig` is restored with credential-helper values hidden. Existing `.gitconfig` is kept; the recovered copy is `.gitconfig.from-windows-old`.

Repositories are copied as whole working trees including `.git`. Offline analysis reads `HEAD`, refs, packed-refs, remotes, stash, and index mtime without git.exe. Uncommitted and unpushed work stay **unknown (install Git to analyze)** until you choose **Analyze repositories**, which runs `git.exe` through the process runner with `--no-optional-locks` and `HOME` set to the new profile so the old config is never consulted. The card and tree then show `[Git: no remote]`, `[Git: local-only work]`, or `[Git: clean, pushed]`. After restore, verification checks that the copied `HEAD` matches the source. If `git.exe` is present it also compares `rev-parse HEAD` and porcelain status. Tests never launch a real `git.exe`.
