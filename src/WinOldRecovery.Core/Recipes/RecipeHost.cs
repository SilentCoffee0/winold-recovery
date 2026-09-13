using System.Text.Json;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Restore;
using WinOldRecovery.Core.Scan;

namespace WinOldRecovery.Core.Recipes;

public sealed class RecipeHost
{
    private readonly SessionDb sessionDb;
    private readonly SafeFs safeFs;
    private readonly IProcessRunner processRunner;
    private readonly IReadOnlyList<IRecipe> recipes;
    private readonly CopyEngine copyEngine;

    public RecipeHost(
        SessionDb sessionDb,
        SafeFs safeFs,
        IProcessRunner processRunner,
        IReadOnlyList<IRecipe> recipes)
    {
        this.sessionDb = sessionDb ?? throw new ArgumentNullException(nameof(sessionDb));
        this.safeFs = safeFs ?? throw new ArgumentNullException(nameof(safeFs));
        this.processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
        this.recipes = recipes ?? throw new ArgumentNullException(nameof(recipes));
        copyEngine = new CopyEngine(sessionDb, safeFs);
    }

    public IRecipe? Find(string recipeId)
    {
        return recipes.FirstOrDefault(recipe => recipe.Id.Equals(recipeId, StringComparison.Ordinal));
    }

    public async Task<IReadOnlyList<RecipeCard>> DetectAsync(
        string sessionId,
        IReadOnlyList<DetectedProfile> profiles,
        string destinationUserProfile,
        string sessionTemporaryDirectory,
        string sessionExportsDirectory,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ProfileRecord> storedProfiles = sessionDb.ListProfiles(sessionId);
        List<RecipeCard> cards = [];
        List<PersistedRecipeCard> rows = [];
        foreach (DetectedProfile profile in profiles)
        {
            long? profileId = storedProfiles
                .FirstOrDefault(item => item.Name.Equals(profile.Name, StringComparison.OrdinalIgnoreCase))
                ?.Id;
            ProfileContext context = new(
                profile.Name,
                profile.SourcePath,
                destinationUserProfile,
                sessionTemporaryDirectory,
                sessionExportsDirectory,
                safeFs,
                processRunner);
            foreach (IRecipe recipe in recipes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                DetectResult detected = recipe.Detect(context);
                foreach (RecipeCard card in detected.Cards)
                {
                    cards.Add(card);
                    rows.Add(
                        new PersistedRecipeCard(
                            sessionId,
                            recipe.Id,
                            profileId,
                            card.Title,
                            JsonSerializer.Serialize(card),
                            Components: card.Components));
                }
            }
        }

        await sessionDb.ReplaceRecipeCardsAsync(sessionId, rows, cancellationToken).ConfigureAwait(false);
        return cards;
    }

    public PlanResult PlanCard(IRecipe recipe, RecipeCard card, DestinationContext destination)
    {
        Dictionary<string, Decision> decisions = card.Components.ToDictionary(
            static component => component.Key,
            static component => component.Fixed ? Decision.Undecided : component.SuggestedDefault);
        return recipe.Plan(new CardDecisions(card, decisions), destination);
    }

    public async Task ExecuteAsync(
        string sessionId,
        IRecipe recipe,
        PlanResult plan,
        CancellationToken cancellationToken = default)
    {
        List<PlanItem> items = [];
        foreach (RecipeWrite write in plan.Writes)
        {
            items.Add(
                new PlanItem(
                    sessionId,
                    JobId: 100,
                    write.Kind == RecipeWriteKind.CopyTree ? PlanOperation.CopyTree : PlanOperation.CopyFile,
                    write.SourcePath ?? write.DestinationPath,
                    write.DestinationPath,
                    write.Bytes,
                    ConflictPolicy.KeepBoth,
                    OverwriteApproved: false,
                    recipe.Id));
        }

        IReadOnlyList<PlanItem> stored = await sessionDb.InsertPlanItemsAsync(items, cancellationToken)
            .ConfigureAwait(false);
        RecipeJournal journal = new(sessionDb, sessionId);
        int index = 0;
        foreach (RecipeWrite write in plan.Writes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PlanItem item = stored[index++];
            await journal.StartedAsync(write.ComponentKey, cancellationToken).ConfigureAwait(false);
            try
            {
                if (write.Kind == RecipeWriteKind.WriteContent)
                {
                    safeFs.WriteAllText(write.DestinationPath, write.Utf8Content ?? string.Empty);
                    await sessionDb.AppendJournalAsync(item.Id!.Value, "Completed", cancellationToken: cancellationToken)
                        .ConfigureAwait(false);
                }
                else if (write.SourcePath is not null)
                {
                    RestoreItemResult copied = await copyEngine.CopyAsync(
                            item with { SourcePath = write.SourcePath, DestinationPath = write.DestinationPath },
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (copied.State is not "Completed" and not "Skipped")
                    {
                        throw new IOException("Recipe copy " + copied.State);
                    }
                }

                await journal.CompletedAsync(write.ComponentKey, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                await journal.FailedAsync(write.ComponentKey, exception.Message, cancellationToken)
                    .ConfigureAwait(false);
                throw;
            }
        }

        await recipe.ExecuteAsync(plan, journal, cancellationToken).ConfigureAwait(false);
    }
}

public sealed record PersistedRecipeCard(
    string SessionId,
    string RecipeId,
    long? ProfileId,
    string Title,
    string Json,
    long? Id = null,
    IReadOnlyList<RecipeComponent>? Components = null);

internal sealed class RecipeJournal(SessionDb sessionDb, string sessionId) : IRecipeJournal
{
    public Task StartedAsync(string key, CancellationToken cancellationToken = default)
    {
        return sessionDb.SetKvAsync(sessionId, "recipe.started." + key, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
    }

    public Task CompletedAsync(string key, CancellationToken cancellationToken = default)
    {
        return sessionDb.SetKvAsync(sessionId, "recipe.completed." + key, DateTimeOffset.UtcNow.ToString("O"), cancellationToken);
    }

    public Task FailedAsync(string key, string error, CancellationToken cancellationToken = default)
    {
        return sessionDb.SetKvAsync(sessionId, "recipe.failed." + key, error, cancellationToken);
    }
}
