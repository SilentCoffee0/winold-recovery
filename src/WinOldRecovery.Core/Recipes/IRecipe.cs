using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Processes;

namespace WinOldRecovery.Core.Recipes;

public interface IRecipe
{
    string Id { get; }

    DetectResult Detect(ProfileContext context);

    PlanResult Plan(CardDecisions decisions, DestinationContext destination);

    Task ExecuteAsync(PlanResult plan, IRecipeJournal journal, CancellationToken cancellationToken = default);

    RecipeVerifyResult Verify(PlanResult plan);

    Task<RecipeVerifyResult> VerifyAsync(PlanResult plan, CancellationToken cancellationToken = default)
        => Task.FromResult(Verify(plan));

    IReadOnlyList<Prerequisite> Prerequisites(PlanResult plan);
}

public sealed record ProfileContext(
    string ProfileName,
    string OldProfileRoot,
    string DestinationProfileRoot,
    string SessionTemporaryDirectory,
    string SessionExportsDirectory,
    SafeFs SafeFs,
    IProcessRunner ProcessRunner,
    RecipeIndex? Index = null);

public sealed record DestinationContext(
    string DestinationProfileRoot,
    string SessionExportsDirectory,
    SafeFs SafeFs,
    IProcessRunner ProcessRunner,
    string? SessionTemporaryDirectory = null);

public enum RecipeWriteKind
{
    CopyFile,
    CopyTree,
    WriteContent,
}

public sealed record RecipeComponent(
    string Key,
    string Title,
    string Summary,
    Decision SuggestedDefault,
    bool Fixed,
    string? FixedReason,
    bool Sensitive);

public sealed record RecipeCard(
    string RecipeId,
    string Title,
    string What,
    string WhyItMatters,
    string WhatIsRestored,
    string CloudAlternative,
    string Regeneratable,
    string IfLeftBehind,
    IReadOnlyList<RecipeComponent> Components,
    string InstanceKey,
    IReadOnlyDictionary<string, string> Facts);

public sealed record DetectResult(
    IReadOnlyList<RecipeCard> Cards,
    IReadOnlyList<(string RelativePath, string Kind, string Detail)> Badges);

public sealed record CardDecisions(
    RecipeCard Card,
    IReadOnlyDictionary<string, Decision> ComponentDecisions);

public sealed record RecipeWrite(
    RecipeWriteKind Kind,
    string? SourcePath,
    string DestinationPath,
    string? Utf8Content,
    long Bytes,
    string ComponentKey);

public sealed record PlanResult(
    RecipeCard Card,
    IReadOnlyList<RecipeWrite> Writes,
    DestinationContext? Destination = null);

public sealed record RecipeVerifyResult(bool Ok, string Detail);

public sealed record Prerequisite(string ProcessName, string Message);

public interface IRecipeJournal
{
    Task StartedAsync(string key, CancellationToken cancellationToken = default);

    Task CompletedAsync(string key, CancellationToken cancellationToken = default);

    Task FailedAsync(string key, string error, CancellationToken cancellationToken = default);
}
