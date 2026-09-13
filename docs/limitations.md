# Limitations (v0.1)

- **Chrome and Edge passwords, cookies, and payment cards** cannot be recovered after a clean reinstall.
- **No registry merge.** Old `NTUSER.DAT` is parsed offline for folder redirects only.
- **No whole-profile or whole-AppData restore.**
- **No program reinstall.** Shortcuts and installers are not a substitute for the vendor installer.
- **No move/rename restore.** Same-volume rename would mutate Windows.old (forbidden by I1).
- **Cloud placeholders** (OneDrive Files On-Demand) are listed, not hydrated.
- **EFS files** are flagged, not decrypted.
- **Symlinks** are listed and skipped.
- **No network.** The binary does not download updates or upload telemetry.
- **Unsigned builds** trigger SmartScreen until SignPath or Azure Trusted Signing is connected. See `docs/signing.md`.
- Windows may still delete Windows.old on its own ~10-day schedule. The tool warns; it does not rename the folder to defeat that task.
