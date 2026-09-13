using System.IO;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Sessions;

namespace WinOldRecovery.App.Help;

public sealed class FirstRunState
{
    public const string MarkerFileName = "first-run.dismissed";

    private readonly SafeFs safeFs;
    private readonly string markerPath;

    public FirstRunState(SafeFs safeFs, string markerPath)
    {
        this.safeFs = safeFs ?? throw new ArgumentNullException(nameof(safeFs));
        ArgumentException.ThrowIfNullOrWhiteSpace(markerPath);
        this.markerPath = markerPath;
    }

    public static FirstRunState FromWorkspace(SafeFs safeFs, SessionWorkspace workspace)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        string appRoot = Path.GetFullPath(Path.Combine(workspace.RootPath, "..", ".."));
        return new FirstRunState(safeFs, Path.Combine(appRoot, MarkerFileName));
    }

    public bool IsDismissed()
    {
        return safeFs.FileExists(markerPath);
    }

    public void Dismiss()
    {
        string? parent = Path.GetDirectoryName(markerPath);
        if (!string.IsNullOrWhiteSpace(parent))
        {
            safeFs.CreateDirectory(parent);
        }

        using FileStream stream = safeFs.OpenWrite(markerPath, FileMode.Create);
        stream.Write("dismissed"u8);
    }
}
