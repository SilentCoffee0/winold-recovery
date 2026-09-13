using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Registry;

public sealed class OfflineRegistryHive
{
    private readonly global::Registry.RegistryHiveOnDemand hive;

    private OfflineRegistryHive(string copiedHivePath)
    {
        CopiedHivePath = copiedHivePath;
        hive = new global::Registry.RegistryHiveOnDemand(copiedHivePath);
    }

    public string CopiedHivePath { get; }

    public static async Task<OfflineRegistryHive> OpenCopyAsync(
        string sourceHivePath,
        string sessionTemporaryDirectory,
        SafeFs safeFs,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceHivePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionTemporaryDirectory);
        ArgumentNullException.ThrowIfNull(safeFs);

        safeFs.CreateDirectory(sessionTemporaryDirectory);
        string copyPath = Path.Combine(
            sessionTemporaryDirectory,
            $"hive-{Guid.NewGuid():N}.dat");

        await using (FileStream source = safeFs.OpenRead(sourceHivePath))
        await using (FileStream destination = safeFs.OpenWrite(copyPath, FileMode.CreateNew))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        return new OfflineRegistryHive(copyPath);
    }

    public bool ContainsKey(string keyPath)
    {
        ArgumentNullException.ThrowIfNull(keyPath);
        return hive.GetKey(keyPath) is not null;
    }

    public IReadOnlyDictionary<string, string> GetStringValues(string keyPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyPath);

        global::Registry.Abstractions.RegistryKey? key = hive.GetKey(keyPath);
        if (key is null)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
        foreach (global::Registry.Abstractions.KeyValue value in key.Values)
        {
            if (string.IsNullOrEmpty(value.ValueName) || value.ValueData is null)
            {
                continue;
            }

            values[value.ValueName] = value.ValueData;
        }

        return values;
    }
}
