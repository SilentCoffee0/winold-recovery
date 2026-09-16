using System.Text.Json;
using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Sessions;

public sealed record CompletedScanPointer(
    string SessionId,
    string WorkspaceRoot,
    string DatabasePath,
    string SourceRoot,
    DateTimeOffset CompletedAt,
    int NodesVisited,
    long BytesSeen,
    int ProfileCount,
    int CardCount);

public static class CompletedScan
{
    public const string FileName = "last-scan.json";
    public const string SourceRootKey = "scan.sourceRoot";
    public const string CompletedKey = "scan.completed";
    public const string NodesVisitedKey = "scan.nodesVisited";
    public const string BytesSeenKey = "scan.bytesSeen";
    public const string HighValueCountKey = "scan.highValueCount";
    public const string RegeneratableCountKey = "scan.regeneratableCount";

    public static string PointerPath(string? localApplicationData = null)
    {
        string basePath = localApplicationData ??
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(basePath, "WinOldRecovery", FileName);
    }

    public static string PointerPathFromWorkspace(string workspaceRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workspaceRoot);
        string? sessions = Path.GetDirectoryName(workspaceRoot);
        string? appRoot = Path.GetDirectoryName(sessions);
        if (string.IsNullOrEmpty(appRoot))
        {
            throw new InvalidOperationException("Session workspace is not under WinOldRecovery.");
        }

        return Path.Combine(appRoot, FileName);
    }

    public static void Write(SafeFs safeFs, CompletedScanPointer pointer, string pointerPath)
    {
        ArgumentNullException.ThrowIfNull(safeFs);
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentException.ThrowIfNullOrWhiteSpace(pointerPath);
        string? directory = Path.GetDirectoryName(pointerPath);
        if (!string.IsNullOrEmpty(directory))
        {
            safeFs.CreateDirectory(directory);
        }

        safeFs.WriteAllText(pointerPath, JsonSerializer.Serialize(pointer));
    }

    public static CompletedScanPointer? TryRead(SafeFs safeFs, string pointerPath)
    {
        ArgumentNullException.ThrowIfNull(safeFs);
        ArgumentException.ThrowIfNullOrWhiteSpace(pointerPath);
        if (!safeFs.FileExists(pointerPath))
        {
            return null;
        }

        try
        {
            CompletedScanPointer? pointer = JsonSerializer.Deserialize<CompletedScanPointer>(
                safeFs.ReadAllText(pointerPath));
            if (pointer is null ||
                string.IsNullOrWhiteSpace(pointer.SessionId) ||
                string.IsNullOrWhiteSpace(pointer.WorkspaceRoot) ||
                string.IsNullOrWhiteSpace(pointer.DatabasePath) ||
                string.IsNullOrWhiteSpace(pointer.SourceRoot))
            {
                return null;
            }

            if (!Directory.Exists(PathCanonicalizer.WithoutExtendedPrefix(pointer.SourceRoot)))
            {
                return null;
            }

            if (!File.Exists(pointer.DatabasePath) || !Directory.Exists(pointer.WorkspaceRoot))
            {
                return null;
            }

            return pointer;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException or NotSupportedException)
        {
            return null;
        }
    }
}
