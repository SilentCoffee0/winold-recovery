# Browser passwords after a reinstall

Chrome and Edge store passwords, cookies, and payment cards in files that are encrypted with keys that only existed on the old Windows installation. After a clean reinstall those keys are gone. WinOld Recovery does not decrypt them and cannot restore them.

Sign in to the same Google account (Chrome) or Microsoft account (Edge) if sync was enabled on the old PC.

Firefox is different: `logins.json` and `key4.db` are restored together into a new profile. The card reports whether a Primary Password is set, not set, or could not be determined from `key4.db` metadata. If one is set, Firefox will ask for it after restore. The tool never exports those passwords as text.

Open tabs are listed in `tabs.html` from the newest `sessionstore*.jsonlz4`. Installed add-ons are listed in `extensions.html` with addons.mozilla.org search links. Builtin Firefox add-ons are omitted.
