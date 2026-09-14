using System.ComponentModel;
using System.Globalization;
using System.IO.Enumeration;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;
using WinOldRecovery.Native;

namespace WinOldRecovery.Core.Scan;

public sealed class FileSystemWalker
{
    public const int InsertBatchSize = 5000;
    public static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private const FileAttributes RecallOnOpen = (FileAttributes)0x00040000;
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private const FileAttributes CloudAttributes =
        FileAttributes.Offline | RecallOnOpen | RecallOnDataAccess;
    private const int MaxPathLength = 260;

    private static readonly EnumerationOptions EnumerationOptions = new()
    {
        RecurseSubdirectories = false,
        AttributesToSkip = 0,
        IgnoreInaccessible = false,
        ReturnSpecialDirectories = false,
        BufferSize = 64 * 1024,
    };

    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly SessionDb sessionDb;
    private readonly List<PersistedNode> pendingNodes = [];
    private readonly List<NodeAggregateUpdate> pendingUpdates = [];
    private readonly List<NodeBadgeRow> pendingBadges = [];

    public FileSystemWalker(SessionDb sessionDb)
    {
        this.sessionDb = sessionDb ?? throw new ArgumentNullException(nameof(sessionDb));
    }

    public async Task<WalkResult> WalkAsync(
        WalkRequest request,
        IProgress<WalkProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SessionId);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.SourceRoot);

        string sourceRoot = PathCanonicalizer.NormalizeLexically(request.SourceRoot);
        if (!Directory.Exists(sourceRoot))
        {
            throw new DirectoryNotFoundException(
                $"The scan root does not exist: '{request.SourceRoot}'.");
        }

        IReadOnlyList<WalkScope> scopes = request.Scopes is { Count: > 0 }
            ? request.Scopes
            : [new WalkScope(string.Empty)];

        List<string> completed = [];
        WalkCounters counters = new();

        foreach (WalkScope scope in scopes)
        {
            WalkState state = await CreateScopeStateAsync(
                request.SessionId,
                sourceRoot,
                scope,
                request.Resume,
                cancellationToken)
                .ConfigureAwait(false);
            Report(progress, force: true, counters, state);

            try
            {
                DirectoryWalkResult result = await WalkDirectoryAsync(
                        state,
                        counters,
                        progress,
                        state.RootNodeId,
                        state.DirectoryPath,
                        state.RelPath,
                        isScopeRoot: true,
                        cancellationToken)
                    .ConfigureAwait(false);

                pendingUpdates.Add(
                    new NodeAggregateUpdate(
                        state.RootNodeId,
                        result.AggSize,
                        result.AggFiles,
                        result.Problem));
                await FlushAsync(state, cancellationToken).ConfigureAwait(false);
                completed.AddRange(state.CompletedTopLevel);
            }
            catch (OperationCanceledException)
            {
                await FlushAsync(state, CancellationToken.None).ConfigureAwait(false);
                completed.AddRange(state.CompletedTopLevel);
                throw;
            }
        }

        return new WalkResult(
            counters.NodesVisited,
            counters.BytesSeen,
            completed,
            Completed: true,
            counters.JunctionsSkipped,
            counters.CloudSkipped,
            counters.EncryptedSkipped,
            counters.AccessDenied);
    }

    private async Task<WalkState> CreateScopeStateAsync(
        string sessionId,
        string sourceRoot,
        WalkScope scope,
        bool resume,
        CancellationToken cancellationToken)
    {
        string relativeRoot = NormalizeRelative(scope.RelativeRoot);
        string directoryPath = string.IsNullOrEmpty(relativeRoot)
            ? sourceRoot
            : CombinePath(sourceRoot, relativeRoot);
        string checkpointKey = CheckpointKey(relativeRoot);

        WalkerCheckpoint? checkpoint = resume
            ? SessionDb.DeserializeWalkerCheckpoint(sessionDb.GetKv(sessionId, checkpointKey))
            : null;

        if (checkpoint is not null)
        {
            HashSet<string> completed = new(
                checkpoint.CompletedTopLevelDirectories,
                StringComparer.OrdinalIgnoreCase);
            foreach (string childName in sessionDb.GetChildNames(sessionId, checkpoint.RootNodeId))
            {
                if (!completed.Contains(childName))
                {
                    await sessionDb.DeleteNodeSubtreeAsync(
                            sessionId,
                            CombineRelative(relativeRoot, childName),
                            cancellationToken)
                        .ConfigureAwait(false);
                }
            }

            long resumedNextId = sessionDb.GetMaxNodeId(sessionId) + 1;
            return new WalkState(
                sessionId,
                scope.ProfileId,
                directoryPath,
                relativeRoot,
                checkpointKey,
                checkpoint.RootNodeId,
                resumedNextId,
                completed,
                new WalkerCheckpoint(checkpoint.RootNodeId, resumedNextId, [.. completed]));
        }

        long nextNodeId = sessionDb.GetMaxNodeId(sessionId) + 1;
        string name = string.IsNullOrEmpty(relativeRoot)
            ? Path.GetFileName(Path.TrimEndingDirectorySeparator(sourceRoot))
            : Path.GetFileName(relativeRoot);
        if (string.IsNullOrEmpty(name))
        {
            name = sourceRoot;
        }

        FileAttributes attributes = TryGetAttributes(directoryPath, out FileAttributes found)
            ? found
            : FileAttributes.Directory;
        long rootNodeId = nextNodeId++;
        pendingNodes.Add(
            new PersistedNode(
                rootNodeId,
                sessionId,
                scope.ProfileId,
                ParentId: null,
                name,
                relativeRoot,
                NodeKind.Directory,
                Size: 0,
                AggSize: 0,
                AggFiles: 0,
                TryGetLastWriteTimeUtc(directoryPath),
                (int)attributes,
                ClassifyNameAndPathProblem(directoryPath, relativeRoot, size: 0, isDirectory: true)));

        return new WalkState(
            sessionId,
            scope.ProfileId,
            directoryPath,
            relativeRoot,
            checkpointKey,
            rootNodeId,
            nextNodeId,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            new WalkerCheckpoint(rootNodeId, nextNodeId, []));
    }

    private async Task<DirectoryWalkResult> WalkDirectoryAsync(
        WalkState state,
        WalkCounters counters,
        IProgress<WalkProgress>? progress,
        long directoryId,
        string directoryPath,
        string relativePath,
        bool isScopeRoot,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        counters.NodesVisited++;
        counters.CurrentRelativePath = relativePath;
        Report(progress, force: false, counters, state);

        long aggSize = 0;
        long aggFiles = 0;
        if (isScopeRoot && state.CompletedTopLevel.Count > 0)
        {
            ChildAggregate existing = sessionDb.GetChildAggregates(state.SessionId, directoryId);
            aggSize = existing.AggSize;
            aggFiles = existing.AggFiles;
        }

        try
        {
            using DirectoryEntryEnumerator enumerator = new(directoryPath);
            while (enumerator.TryGetNext(out EnumeratedFsEntry entry))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (isScopeRoot && state.CompletedTopLevel.Contains(entry.Name))
                {
                    continue;
                }

                string childRelative = CombineRelative(relativePath, entry.Name);
                string childPath = CombinePath(directoryPath, entry.Name);
                counters.CurrentRelativePath = childRelative;

                NodeClassification classification = Classify(childPath, childRelative, entry);
                NoteSkip(counters, classification);
                if (classification.Kind == NodeKind.Directory && !classification.IsLeaf)
                {
                    long childId = state.NextNodeId++;
                    pendingNodes.Add(
                        CreateNode(
                            childId,
                            state,
                            directoryId,
                            entry,
                            childRelative,
                            NodeKind.Directory,
                            size: 0,
                            aggSize: 0,
                            aggFiles: 0,
                            classification.Problem));
                    await FlushIfNeededAsync(state, cancellationToken).ConfigureAwait(false);

                    DirectoryWalkResult child = await WalkDirectoryAsync(
                            state,
                            counters,
                            progress,
                            childId,
                            childPath,
                            childRelative,
                            isScopeRoot: false,
                            cancellationToken)
                        .ConfigureAwait(false);

                    aggSize += child.AggSize;
                    aggFiles += child.AggFiles;
                    pendingUpdates.Add(
                        new NodeAggregateUpdate(
                            childId,
                            child.AggSize,
                            child.AggFiles,
                            child.Problem));
                }
                else
                {
                    long childId = state.NextNodeId++;
                    long size = classification.CountsAsFile ? entry.Length : 0;
                    long childAggFiles = classification.CountsAsFile ? 1 : 0;
                    pendingNodes.Add(
                        CreateNode(
                            childId,
                            state,
                            directoryId,
                            entry,
                            childRelative,
                            classification.Kind,
                            size,
                            size,
                            childAggFiles,
                            classification.Problem));
                    if (!string.IsNullOrEmpty(classification.ReparseDetail))
                    {
                        pendingBadges.Add(
                            new NodeBadgeRow(childId, "Reparse", classification.ReparseDetail));
                    }

                    aggSize += size;
                    aggFiles += childAggFiles;
                    counters.BytesSeen += size;
                    counters.NodesVisited++;
                }

                await FlushIfNeededAsync(state, cancellationToken).ConfigureAwait(false);

                if (isScopeRoot)
                {
                    state.CompletedTopLevel.Add(entry.Name);
                    await FlushAsync(state, cancellationToken).ConfigureAwait(false);
                }

                Report(progress, force: isScopeRoot, counters, state);
            }

            return new DirectoryWalkResult(aggSize, aggFiles, NodeProblem.None);
        }
        catch (Exception exception) when (IsAccessDenied(exception))
        {
            counters.AccessDenied++;
            return new DirectoryWalkResult(aggSize, aggFiles, NodeProblem.AccessDenied);
        }
    }

    private static PersistedNode CreateNode(
        long id,
        WalkState state,
        long parentId,
        EnumeratedFsEntry entry,
        string relativePath,
        NodeKind kind,
        long size,
        long aggSize,
        long aggFiles,
        NodeProblem problem)
    {
        return new PersistedNode(
            id,
            state.SessionId,
            state.ProfileId,
            parentId,
            entry.Name,
            relativePath,
            kind,
            size,
            aggSize,
            aggFiles,
            entry.LastWriteTimeUtc,
            (int)entry.Attributes,
            problem);
    }

    private async Task FlushIfNeededAsync(WalkState state, CancellationToken cancellationToken)
    {
        if (pendingNodes.Count + pendingUpdates.Count < InsertBatchSize)
        {
            return;
        }

        await FlushAsync(state, cancellationToken).ConfigureAwait(false);
    }

    private async Task FlushAsync(WalkState state, CancellationToken cancellationToken)
    {
        if (pendingNodes.Count > 0)
        {
            await sessionDb.InsertNodesAsync(pendingNodes, cancellationToken).ConfigureAwait(false);
            pendingNodes.Clear();
        }

        if (pendingUpdates.Count > 0)
        {
            await sessionDb.UpdateNodeAggregatesAsync(pendingUpdates, cancellationToken)
                .ConfigureAwait(false);
            pendingUpdates.Clear();
        }

        if (pendingBadges.Count > 0)
        {
            await sessionDb.InsertBadgesAsync(pendingBadges, cancellationToken).ConfigureAwait(false);
            pendingBadges.Clear();
        }

        state.Checkpoint = new WalkerCheckpoint(
            state.RootNodeId,
            state.NextNodeId,
            [.. state.CompletedTopLevel]);
        await sessionDb.SetKvAsync(
                state.SessionId,
                state.CheckpointKey,
                SessionDb.SerializeWalkerCheckpoint(state.Checkpoint),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void NoteSkip(WalkCounters counters, NodeClassification classification)
    {
        if (classification.Kind is NodeKind.Junction or NodeKind.Symlink or NodeKind.MountPoint)
        {
            counters.JunctionsSkipped++;
        }

        if (classification.Problem == NodeProblem.CloudOnly)
        {
            counters.CloudSkipped++;
        }

        if (classification.Problem == NodeProblem.EfsEncrypted)
        {
            counters.EncryptedSkipped++;
        }
    }

    private static NodeClassification Classify(
        string path,
        string relativePath,
        EnumeratedFsEntry entry)
    {
        bool isDirectory = (entry.Attributes & FileAttributes.Directory) != 0;
        bool isReparse = (entry.Attributes & FileAttributes.ReparsePoint) != 0;
        bool isCloud = (entry.Attributes & CloudAttributes) != 0;
        bool isEncrypted = (entry.Attributes & FileAttributes.Encrypted) != 0;
        NodeProblem problem = ClassifyNameAndPathProblem(
            path,
            relativePath,
            entry.Length,
            isDirectory);

        if (isCloud)
        {
            return new NodeClassification(
                NodeKind.CloudPlaceholder,
                RankProblem(problem, NodeProblem.CloudOnly),
                IsLeaf: true,
                CountsAsFile: true,
                ReparseDetail: string.Empty);
        }

        if (isEncrypted)
        {
            problem = RankProblem(problem, NodeProblem.EfsEncrypted);
        }

        if (isReparse)
        {
            ReparsePointInfo info = TryReadReparse(path);
            return new NodeClassification(
                KindFromReparse(info),
                problem,
                IsLeaf: true,
                CountsAsFile: !isDirectory,
                ReparseDetail: FormatReparseDetail(info));
        }

        return new NodeClassification(
            isDirectory ? NodeKind.Directory : NodeKind.File,
            problem,
            IsLeaf: !isDirectory || isEncrypted,
            CountsAsFile: !isDirectory,
            ReparseDetail: string.Empty);
    }

    private static NodeProblem ClassifyNameAndPathProblem(
        string fullPath,
        string relativePath,
        long size,
        bool isDirectory)
    {
        _ = size;
        _ = isDirectory;
        string name = Path.GetFileName(relativePath.Replace('/', '\\'));
        NodeProblem problem = HasInvalidDestinationName(name)
            ? NodeProblem.InvalidDestName
            : NodeProblem.None;

        string unprefixed = StripExtendedPrefix(fullPath);
        if (unprefixed.Length > MaxPathLength || relativePath.Length > MaxPathLength)
        {
            problem = RankProblem(problem, NodeProblem.LongPath);
        }

        return problem;
    }

    private static bool HasInvalidDestinationName(string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        if (name.EndsWith(' ') || name.EndsWith('.'))
        {
            return true;
        }

        int dot = name.IndexOf('.');
        string stem = dot >= 0 ? name[..dot] : name;
        if (ReservedNames.Contains(name) || ReservedNames.Contains(stem))
        {
            return true;
        }

        foreach (char character in name)
        {
            if (character < 32 || "<>:\"/\\|?*".Contains(character, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static NodeKind KindFromReparse(ReparsePointInfo info)
    {
        if (info.Tag is ReparsePoint.TagSymlink or ReparsePoint.TagLxSymlink)
        {
            return NodeKind.Symlink;
        }

        if (info.Tag == ReparsePoint.TagMountPoint)
        {
            if (info.Target.Contains(@"Volume{", StringComparison.OrdinalIgnoreCase))
            {
                return NodeKind.MountPoint;
            }

            return NodeKind.Junction;
        }

        return NodeKind.Unknown;
    }

    private static string FormatReparseDetail(ReparsePointInfo info)
    {
        string tag = $"0x{info.Tag.ToString("X8", CultureInfo.InvariantCulture)}";
        return string.IsNullOrEmpty(info.Target) ? tag : $"{tag} -> {info.Target}";
    }

    private static ReparsePointInfo TryReadReparse(string path)
    {
        try
        {
            return ReparsePoint.Read(PathCanonicalizer.ToExtendedPath(path));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or Win32Exception)
        {
            return new ReparsePointInfo(0, string.Empty);
        }
    }

    private static NodeProblem RankProblem(NodeProblem current, NodeProblem candidate)
    {
        return Rank(candidate) < Rank(current) ? candidate : current;
    }

    private static int Rank(NodeProblem problem)
    {
        return problem switch
        {
            NodeProblem.AccessDenied => 0,
            NodeProblem.EfsEncrypted => 1,
            NodeProblem.CloudOnly => 2,
            NodeProblem.InvalidDestName => 3,
            NodeProblem.LongPath => 4,
            NodeProblem.ZeroByteStub => 5,
            _ => 6,
        };
    }

    private static bool IsAccessDenied(Exception exception)
    {
        for (Exception? current = exception; current is not null; current = current.InnerException)
        {
            if (current is UnauthorizedAccessException)
            {
                return true;
            }

            if (current is Win32Exception win32 && win32.NativeErrorCode is 5 or 1307 or 1314)
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryGetAttributes(string path, out FileAttributes attributes)
    {
        try
        {
            attributes = File.GetAttributes(path);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            attributes = FileAttributes.Directory;
            return false;
        }
    }

    private static DateTime? TryGetLastWriteTimeUtc(string path)
    {
        try
        {
            return Directory.GetLastWriteTimeUtc(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string NormalizeRelative(string? relativeRoot)
    {
        if (string.IsNullOrWhiteSpace(relativeRoot) || relativeRoot is "." or @".\")
        {
            return string.Empty;
        }

        return relativeRoot.Replace('/', '\\').Trim('\\');
    }

    private static string CombineRelative(string parent, string name)
    {
        return string.IsNullOrEmpty(parent) ? name : parent + "\\" + name;
    }

    private static string CombinePath(string parent, string name)
    {
        return PathCanonicalizer.ToExtendedPath(Path.Combine(parent, name));
    }

    private static string StripExtendedPrefix(string path)
    {
        const string extended = @"\\?\";
        const string extendedUnc = @"\\?\UNC\";
        if (path.StartsWith(extendedUnc, StringComparison.OrdinalIgnoreCase))
        {
            return @"\\" + path[extendedUnc.Length..];
        }

        return path.StartsWith(extended, StringComparison.Ordinal)
            ? path[extended.Length..]
            : path;
    }

    private static string CheckpointKey(string relativeRoot)
    {
        return "walker.checkpoint:" + relativeRoot;
    }

    private static void Report(
        IProgress<WalkProgress>? progress,
        bool force,
        WalkCounters counters,
        WalkState state)
    {
        if (progress is null)
        {
            return;
        }

        DateTimeOffset now = DateTimeOffset.UtcNow;
        if (!force && now < counters.NextProgress)
        {
            return;
        }

        counters.NextProgress = now + ProgressInterval;
        progress.Report(
            new WalkProgress(
                counters.NodesVisited,
                counters.BytesSeen,
                counters.CurrentRelativePath,
                [.. state.CompletedTopLevel],
                counters.JunctionsSkipped,
                counters.CloudSkipped,
                counters.EncryptedSkipped,
                counters.AccessDenied));
    }

    private sealed class WalkCounters
    {
        public int NodesVisited { get; set; }
        public long BytesSeen { get; set; }
        public string CurrentRelativePath { get; set; } = string.Empty;
        public int AccessDenied { get; set; }
        public int JunctionsSkipped { get; set; }
        public int CloudSkipped { get; set; }
        public int EncryptedSkipped { get; set; }
        public DateTimeOffset NextProgress { get; set; }
    }

    private sealed class WalkState
    {
        public WalkState(
            string sessionId,
            long? profileId,
            string directoryPath,
            string relPath,
            string checkpointKey,
            long rootNodeId,
            long nextNodeId,
            HashSet<string> completedTopLevel,
            WalkerCheckpoint checkpoint)
        {
            SessionId = sessionId;
            ProfileId = profileId;
            DirectoryPath = directoryPath;
            RelPath = relPath;
            CheckpointKey = checkpointKey;
            RootNodeId = rootNodeId;
            NextNodeId = nextNodeId;
            CompletedTopLevel = completedTopLevel;
            Checkpoint = checkpoint;
        }

        public string SessionId { get; }
        public long? ProfileId { get; }
        public string DirectoryPath { get; }
        public string RelPath { get; }
        public string CheckpointKey { get; }
        public long RootNodeId { get; }
        public long NextNodeId { get; set; }
        public HashSet<string> CompletedTopLevel { get; }
        public WalkerCheckpoint Checkpoint { get; set; }
    }

    private readonly record struct DirectoryWalkResult(
        long AggSize,
        long AggFiles,
        NodeProblem Problem);

    private readonly record struct NodeClassification(
        NodeKind Kind,
        NodeProblem Problem,
        bool IsLeaf,
        bool CountsAsFile,
        string ReparseDetail);

    private sealed class DirectoryEntryEnumerator : FileSystemEnumerator<EnumeratedFsEntry>
    {
        public DirectoryEntryEnumerator(string directory)
            : base(directory, EnumerationOptions)
        {
        }

        protected override EnumeratedFsEntry TransformEntry(ref FileSystemEntry entry)
        {
            return new EnumeratedFsEntry(
                entry.FileName.ToString(),
                entry.Attributes,
                entry.Length,
                entry.LastWriteTimeUtc.UtcDateTime);
        }

        public bool TryGetNext(out EnumeratedFsEntry entry)
        {
            if (MoveNext())
            {
                entry = Current;
                return true;
            }

            entry = default;
            return false;
        }
    }

    private readonly record struct EnumeratedFsEntry(
        string Name,
        FileAttributes Attributes,
        long Length,
        DateTime LastWriteTimeUtc);
}
