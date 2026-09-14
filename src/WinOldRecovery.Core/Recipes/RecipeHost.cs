using System.Text.Json;
using WinOldRecovery.Core.Decisions;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Core.Planning;
using WinOldRecovery.Core.Processes;
using WinOldRecovery.Core.Restore;
using WinOldRecovery.Core.Scan;
using WinOldRecovery.Core.Verify;

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
        List<NodeBadgeRow> badges = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
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
                    string identity = recipe.Id + "\0" + card.InstanceKey;
                    if (!seen.Add(identity))
                    {
                        continue;
                    }

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

                foreach ((string relativePath, string kind, string detail) in detected.Badges)
                {
                    string nodeRel = string.IsNullOrWhiteSpace(relativePath)
                        ? profile.RelativePath
                        : Path.Combine(profile.RelativePath, relativePath.Replace('/', '\\'));
                    long? nodeId = sessionDb.FindNodeId(sessionId, nodeRel);
                    if (nodeId is long id)
                    {
                        badges.Add(new NodeBadgeRow(id, kind, detail));
                    }
                }
            }
        }

        await sessionDb.ReplaceRecipeCardsAsync(sessionId, rows, cancellationToken).ConfigureAwait(false);
        await sessionDb.InsertBadgesAsync(badges, cancellationToken).ConfigureAwait(false);
        return cards;
    }

    public PlanResult PlanCard(
        IRecipe recipe,
        RecipeCard card,
        DestinationContext destination,
        string? sessionId = null)
    {
        RecipeCard plannedCard = OverlayFolderMap(card, sessionId);
        Dictionary<string, Decision> decisions = sessionId is null
            ? plannedCard.Components.ToDictionary(
                static component => component.Key,
                static component => component.Fixed ? Decision.Undecided : component.SuggestedDefault)
            : StoredRecipeDecisions.Load(sessionDb, sessionId, plannedCard);
        PlanResult planned = recipe.Plan(new CardDecisions(plannedCard, decisions), destination);
        return planned with { Destination = destination };
    }

    private RecipeCard OverlayFolderMap(RecipeCard card, string? sessionId)
    {
        if (sessionId is null)
        {
            return card;
        }

        string? stored = sessionDb.GetKv(sessionId, RecipeFolderMap.KvKey(card.InstanceKey));
        if (string.IsNullOrWhiteSpace(stored))
        {
            return card;
        }

        Dictionary<string, string> facts = new(card.Facts, StringComparer.Ordinal);
        facts[RecipeFolderMap.FactKey] = stored;
        return card with { Facts = facts };
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
            await sessionDb.SetKvAsync(
                    sessionId,
                    ComponentKeyKv(item.Id!.Value),
                    write.ComponentKey,
                    cancellationToken)
                .ConfigureAwait(false);
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

    public async Task<IReadOnlyList<VerifyResultRow>> CollectLevel3Async(
        string sessionId,
        string reportId,
        IReadOnlyList<RecipeCard> cards,
        DestinationContext destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(destination);

        IReadOnlyList<PlanItem> items = sessionDb.ListPlanItems(sessionId);
        Dictionary<string, List<PlanItem>> byRecipe = items
            .Where(static item => item.RecipeId is not null && item.Id is not null)
            .GroupBy(static item => item.RecipeId!, StringComparer.Ordinal)
            .ToDictionary(static group => group.Key, static group => group.ToList(), StringComparer.Ordinal);
        PlanItem? fallback = items.FirstOrDefault(static item => item.Id is not null);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        List<VerifyResultRow> rows = [];
        foreach (RecipeCard card in cards)
        {
            IRecipe? recipe = Find(card.RecipeId);
            if (recipe is null)
            {
                continue;
            }

            if (!byRecipe.TryGetValue(card.RecipeId, out List<PlanItem>? owned) || owned.Count == 0)
            {
                if (fallback?.Id is not long fallbackId)
                {
                    continue;
                }

                RecipeVerifyResult skipped = await recipe.VerifyAsync(
                        new PlanResult(card, [], destination),
                        cancellationToken)
                    .ConfigureAwait(false);
                rows.Add(
                    new VerifyResultRow(
                        fallbackId,
                        reportId,
                        3,
                        skipped.Ok,
                        card.Title + ": " + skipped.Detail,
                        now));
                continue;
            }

            List<RecipeWrite> writes = [];
            foreach (PlanItem item in owned)
            {
                string component = sessionDb.GetKv(sessionId, ComponentKeyKv(item.Id!.Value)) ?? "files";
                writes.Add(
                    new RecipeWrite(
                        item.Operation == PlanOperation.CopyTree
                            ? RecipeWriteKind.CopyTree
                            : RecipeWriteKind.CopyFile,
                        item.SourcePath,
                        item.DestinationPath,
                        null,
                        item.Bytes,
                        component));
            }

            RecipeVerifyResult result = await recipe.VerifyAsync(
                    new PlanResult(card, writes, destination),
                    cancellationToken)
                .ConfigureAwait(false);
            rows.Add(
                new VerifyResultRow(
                    owned[0].Id!.Value,
                    reportId,
                    3,
                    result.Ok,
                    card.Title + ": " + result.Detail,
                    now));
        }

        return rows;
    }

    private static string ComponentKeyKv(long planItemId)
    {
        return "recipe.component." + planItemId.ToString(System.Globalization.CultureInfo.InvariantCulture);
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
