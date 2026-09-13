# FixtureGen

`FixtureGen` creates the synthetic `Windows.old` tree used by integration tests.
It never replaces a non-empty target directory.

Full fixture generation must run from an elevated terminal:

```powershell
dotnet run --project tools/FixtureGen -- C:\fixtures\Windows.old
```

The default creates 100,000 files under `node_modules` and includes junctions,
symlinks, a deny-ACL directory, an orphan-SID-owned file, an EFS file, a
300-character path, an OFFLINE-attributed placeholder stand-in, an
invalid-destination-name file, and recipe shells with canary secrets.

For fast non-elevated development tests:

```powershell
dotnet run --project tools/FixtureGen -- C:\fixtures\Windows.old --files 128 --portable
```

Portable mode explicitly records deny-ACL, orphan-SID, and EFS hazards as
unavailable. It must not be used to claim the full fixture acceptance test
passed.

Run the self-check again without changing the fixture:

```powershell
dotnet run --project tools/FixtureGen -- --self-check-only C:\fixtures\Windows.old
```
