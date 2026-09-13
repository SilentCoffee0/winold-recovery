# M0 spike: single-file Registry and SQLite dependencies

## Assumptions

- `Registry` 2026.5.0 can parse a Windows 11 `NTUSER.DAT` from code bundled
  into a self-contained single-file executable.
- `Microsoft.Data.Sqlite` 10.0.12 and SQLitePCLRaw 3.0.5 can load the native
  SQLite library from the same kind of bundle.

## Test

1. `FixtureGen` copied the clean local `C:\Users\Default\NTUSER.DAT` into a
   temporary directory using `SafeFs` and `CreateFileW` backup-mode reads.
2. `OfflineRegistryHive` opened only that copy and looked up `Software`.
3. `SessionDb` created a WAL database, applied schema v1, inserted a session,
   and read the row through a separate read-only connection.
4. `FixtureGen` was published for `win-x64` as a compressed, self-contained,
   single-file executable with native libraries enabled for self-extraction.
5. The published executable ran `--bundle-self-test`.

Command:

```powershell
dotnet publish tools/FixtureGen/FixtureGen.csproj -c Release -r win-x64 `
  --self-contained -p:PublishSingleFile=true `
  -p:IncludeNativeLibrariesForSelfExtract=true `
  -p:EnableCompressionInSingleFile=true -p:PublishReadyToRun=false
FixtureGen.exe --bundle-self-test
```

## Observed result

- Registry parse: passed; the copied hive's `Software` key was found.
- SQLite native load and round trip: passed.
- Self-test work after process startup: 101 ms on this machine.
- Non-ReadyToRun bundle size: 39,194,438 bytes.

## Resulting implementation decision

Keep `Registry`, `Microsoft.Data.Sqlite`, and
`SQLitePCLRaw.bundle_e_sqlite3`. No fallback REGF parser or alternate database
is needed based on this spike. The WPF application publish still requires its
own elevated clean-VM launch check.
