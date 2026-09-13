# M0 spike: ReadyToRun size and startup

## Assumption

ReadyToRun may reduce first-start latency enough to justify a larger recovery
utility executable.

## Test

The `FixtureGen --bundle-self-test` harness was published twice as a compressed,
self-contained `win-x64` single-file executable, once with ReadyToRun disabled
and once enabled. Each bundle parsed an offline registry hive and opened
SQLite. Three consecutive process runs were measured.

## Observed result

- ReadyToRun off: 39,194,438 bytes; measured runs 194 ms, 174 ms, and
  176 ms.
- ReadyToRun on: 42,615,399 bytes; measured runs 4,956 ms, 227 ms, and
  270 ms.

The first ReadyToRun run included first-use native extraction and is not
directly comparable with the already-warmed non-ReadyToRun bundle. Warm runs
were slightly slower with ReadyToRun in this console harness, while the file
was 3,420,961 bytes (8.7%) larger.

## Resulting implementation decision

This harness does not represent WPF initialization, and the WPF executable
cannot be launched unattended from this non-elevated environment because its
manifest correctly requests administrator elevation. Keep the existing WPF
ReadyToRun profile until the clean-VM startup test can compare both application
builds. Do not claim an application startup result from this console-only
measurement.
