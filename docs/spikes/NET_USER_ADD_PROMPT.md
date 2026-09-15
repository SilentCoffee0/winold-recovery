# Spike: `net.exe user /add` hang (15 Sep 2026)

## Failure

Elevated `tools/run-elevated-m0.ps1` died at FixtureGen generate (script line 28) with:

```
Child process 'net.exe' exceeded its 00:02:00 timeout.
FixtureGen failed.
```

That 2-minute figure is `ProcessRunner`'s default timeout (no per-call `Timeout` was set on the old `RunRequiredAsync`).

## Exact command (removed)

Orphan-SID setup used to run:

```
net.exe user WORFix<18-char-prefix> Wor!<32-hex>aA7 /add /expires:never /passwordchg:no
```

then `icacls /setowner`, then `net.exe user <name> /delete`.

The password was always longer than 14 characters (`Wor!` + `Guid.N` + `aA7`).

## Cause

Not UAC, not an unrealistic timeout, not a deadlock in `WaitForExit` by itself.

`net user … /add` with a password longer than 14 characters prints:

```
The password entered is longer than 14 characters.  Computers
with Windows prior to Windows 2000 will not be able to use
this account. Do you want to continue this operation? (Y/N) [Y]:
```

`ProcessRunner` redirected stdout/stderr and left stdin inherited. It never wrote `Y`.

Whether that hangs depends on what stdin is:

- **Administrator console** (`tools/run-elevated-m0.ps1`): the child inherits `CONIN$` and waits for Y/N until the default 2-minute timeout. That is the 8.1 failure. Not UAC, not a `WaitForExit` deadlock, not a command that needs more than 2 minutes of CPU.
- **Testhost / agent pipe**: stdin is a pipe that is already at EOF, so `net.exe` prints the prompt and exits immediately with `No valid response was provided.` Direct reproduction from Medium IL always takes this path (prompt text confirmed; hang not reachable without an interactive console).

Closing `ProcessRunner` stdin after `Start` makes the elevated case fail fast the same way. FixtureGen still must not call `net.exe`.

## Fix

Do not call `net.exe`. Assign an unmapped SID with `FileSecurity.SetOwner` (`AssignUnmappedOwner`). EFS uses `File.Encrypt`, not `cipher.exe`. `ProcessRunner` now redirects and closes stdin so a reintroduced prompt cannot inherit the parent console.

Regression: `ProcessRunnerTests.RunAsync_NetUserAddWithLongPasswordPromptsForYN` and `FixtureGeneratorSource_DoesNotInvokeNetExe`.

This does **not** by itself pass the elevated 100k self-check. That check passed on 15 Sep 2026 after this fix; see `docs/spikes/PENDING_MANUAL.md`.
