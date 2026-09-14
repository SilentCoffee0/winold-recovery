using System.Collections.Concurrent;

namespace WinOldRecovery.Integration.Tests.Safety;

/// <summary>
/// Arms a <see cref="FileSystemWatcher"/> (ReadDirectoryChangesW) on a source root.
/// Any created, deleted, renamed, or last-write change fails the test.
/// </summary>
public sealed class SourceWatchdog : IDisposable
{
    private readonly FileSystemWatcher watcher;
    private readonly ConcurrentQueue<string> changes = new();
    private bool disposed;

    public SourceWatchdog(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (!Directory.Exists(root))
        {
            throw new DirectoryNotFoundException(root);
        }

        watcher = new FileSystemWatcher(root)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName
                | NotifyFilters.DirectoryName
                | NotifyFilters.Size
                | NotifyFilters.Attributes
                | NotifyFilters.Security,
            InternalBufferSize = 64 * 1024,
        };
        watcher.Changed += OnChanged;
        watcher.Created += OnChanged;
        watcher.Deleted += OnChanged;
        watcher.Renamed += OnRenamed;
        watcher.EnableRaisingEvents = true;
    }

    public void ThrowIfSourceChanged()
    {
        Thread.Sleep(250);
        if (changes.IsEmpty)
        {
            return;
        }

        string[] seen = [.. changes];
        throw new InvalidOperationException(
            "Source watchdog recorded writes under the fixture root: "
            + string.Join("; ", seen.Take(8)));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
    }

    private void OnChanged(object sender, FileSystemEventArgs args)
    {
        if (args.ChangeType == WatcherChangeTypes.Changed &&
            Directory.Exists(args.FullPath))
        {
            return;
        }

        changes.Enqueue(args.ChangeType + " " + args.FullPath);
    }

    private void OnRenamed(object sender, RenamedEventArgs args)
    {
        changes.Enqueue("Renamed " + args.OldFullPath + " -> " + args.FullPath);
    }
}
