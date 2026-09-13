namespace WinOldRecovery.FixtureGen;

public sealed record FixtureOptions(
    string TargetRoot,
    int NodeModulesFileCount = 100_000,
    bool PortableMode = false)
{
    public const string ManifestFileName = "fixture-manifest.json";
}
