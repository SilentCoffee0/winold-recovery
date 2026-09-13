namespace WinOldRecovery.FixtureGen;

public sealed record FixtureManifest(
    int Version,
    bool FullHazardsRequested,
    int NodeModulesFileCount,
    IReadOnlyDictionary<string, FixtureHazard> Hazards);

public sealed record FixtureHazard(
    string Status,
    string RelativePath,
    string? Detail = null);
