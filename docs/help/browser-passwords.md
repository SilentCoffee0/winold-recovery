# Browser passwords after a reinstall

Chrome and Edge store passwords, cookies, and payment cards in files that are encrypted with keys that only existed on the old Windows installation. After a clean reinstall those keys are gone. WinOld Recovery does not decrypt them and cannot restore them.

Sign in to the same Google account (Chrome) or Microsoft account (Edge) if sync was enabled on the old PC.

Firefox is different: `logins.json` and `key4.db` are restored together into a new profile. If you set a Primary Password, Firefox will ask for it. The tool never exports those passwords as text.
