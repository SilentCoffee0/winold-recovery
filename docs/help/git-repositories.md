# Git repositories

`.gitconfig` is restored with credential-helper values hidden. Existing `.gitconfig` is kept; the recovered copy is `.gitconfig.from-windows-old`.

Repositories under `Projects` are copied as whole working trees including `.git`. Offline analysis can read `HEAD` and refs. Uncommitted or unpushed work is unknown until you choose Analyze, which runs `git.exe` through the process runner with `--no-optional-locks` and `HOME` set to the new profile so the old config is never consulted. Tests never launch a real `git.exe`.
