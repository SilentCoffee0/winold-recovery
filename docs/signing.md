# Code signing

Elevated unsigned EXEs trigger SmartScreen. v0.1 CI uploads **unsigned** artifacts until a signing identity is approved.

## SignPath Foundation (preferred for OSS)

1. Apply at https://signpath.io/solutions/open-source-community with this repository.
2. When the project is accepted, add the SignPath GitHub Action after `dotnet publish` and before `gh release create`.
3. Sign both `win-x64` and `win-arm64` `WinOldRecovery.exe`.
4. Attach the signed binaries and SHA-256 checksums.

Do not commit certificates or SignPath secrets. Do not tag `v0.1.0-m0` as a substitute for signing.

## Azure Trusted Signing

An organization Azure Trusted Signing account can replace SignPath. The same rule applies: publish only after the file is signed, and keep credentials in GitHub Actions secrets.
