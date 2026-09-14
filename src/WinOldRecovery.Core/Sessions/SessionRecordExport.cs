using System.Globalization;
using WinOldRecovery.Core.IO;
using WinOldRecovery.Core.Persistence;

namespace WinOldRecovery.Core.Sessions;

public static class SessionRecordExport
{
    public const string FileName = "session-record.txt";

    public static string Write(SafeFs safeFs, SessionWorkspace workspace, SessionDb sessionDb)
    {
        ArgumentNullException.ThrowIfNull(safeFs);
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(sessionDb);

        string path = Path.Combine(workspace.RootPath, FileName);
        int planCount = sessionDb.ListPlanItems(workspace.SessionId).Count;
        bool journalSettled = sessionDb.RestoreJournalSettled(workspace.SessionId);
        bool verifyOk = sessionDb.LastVerifyReportAllOk(workspace.SessionId);
        string body =
            "sessionId=" + workspace.SessionId + Environment.NewLine +
            "exportedUtc=" + DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture) + Environment.NewLine +
            "planItems=" + planCount.ToString(CultureInfo.InvariantCulture) + Environment.NewLine +
            "journalSettled=" + journalSettled + Environment.NewLine +
            "verifyAllOk=" + verifyOk + Environment.NewLine;
        safeFs.WriteAllText(path, body);
        return path;
    }
}
