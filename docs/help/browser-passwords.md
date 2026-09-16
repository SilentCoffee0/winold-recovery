# Browser passwords after a reinstall

Chrome and Edge store passwords, cookies, and payment cards in files that are encrypted with keys that only existed on the old Windows installation. After a clean reinstall those keys are gone. WinOld Recovery does not decrypt them and cannot restore them.

Sign in to the same Google account (Chrome) or Microsoft account (Edge) if sync was enabled on the old PC.

Firefox is different: `logins.json` (or `logins.db` on newer profiles) and `key4.db` are restored together into a new profile. Thunderbird copies the same pair. The Firefox card reports whether a Primary Password is set, not set, or could not be determined from `key4.db` metadata. If one is set, Firefox will ask for it after restore. The tool never exports those passwords as text. Verify requires `key4.db` to sit next to `logins.json` or `logins.db`.

If `WINOLD_RECOVERY_CHROMIUM_TRANSPLANT=1` is set, Chrome and Edge can copy `Bookmarks` into a new `Profile N` folder and register it in `Local State` (`info_cache` name `<old name> (recovered)`). A `Local State.winold-bak` copy is written first. That option is disabled when this PC has no `Last Version` or `Local State`, or when the installed browser is older than the one in Windows.old.

If the old or new `profiles.ini` has a `StoreID` (Firefox 135+ profile groups), WinOld Recovery still copies the recovered profile folder but does not edit `profiles.ini`. Use **about:profiles → Create a new profile**, then point it at the recovered folder or copy the files in.
