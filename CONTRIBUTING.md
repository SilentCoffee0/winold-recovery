# Contributing to WinOld Recovery

WinOld Recovery recovers data from `Windows.old`. Design documents at the repository root are the product law: `PRODUCT_SPEC.md`, `SAFETY_MODEL.md` (I1–I16), `ARCHITECTURE.md`, `RECOVERY_RECIPES.md`, `UX_SPEC.md`, `IMPLEMENTATION_PLAN.md`.

Do not redesign the workflow, copy engine, or purge gates. Do not add network code. Do not decrypt Chromium secrets.

## How to add a classification rule

1. Edit the matching JSON file under `src/WinOldRecovery.Core/Classification/Rules/`.
2. Follow `docs/classification-rules.md`. Never set `suggestedDefault` to `LeaveBehind` on regeneratable or unknown data.
3. Add a unit test in `tests/WinOldRecovery.Core.Tests/Classification` with a positive and a negative path.
4. If the file is sensitive, set `"sensitive": true` so contents are never header-scanned or logged.

## How to add a recipe

1. Implement `IRecipe` in `src/WinOldRecovery.Recipes`. Recipes may reference Core only, never App.
2. Register the type in `RecipeCatalog.All`.
3. Detect through `SafeFs`. Plan every write. Execute only planned writes. Verify destinations exist.
4. Put secrets in files, never in `RecipeCard.Facts` or logs. Tests must use the canary `WINOLD_RECOVERY_CANARY_DO_NOT_LOG_7F3A91` and assert it never appears in card dumps.
5. Child processes go through `IProcessRunner`. Tests must not invoke real `git`, `wsl`, `ssh`, or `cleanmgr`.
6. Add a fixture in `tools/FixtureGen` when the recipe needs a realistic miniature.

## Tests

```
dotnet test WinOldRecovery.slnx -c Release
```

Release builds treat warnings as errors. Do not tag `v0.1.0-m0` while `docs/spikes/PENDING_MANUAL.md` is unfinished.

## Pull requests

Describe the invariant you touched (I1–I16). Include a support bundle when the change is about restore, verify, or purge failures—not raw vaults, cookies, or private keys.
