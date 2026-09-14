using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Verify;

public static class VerifyReportFormat
{
    public static string Counts(VerifyReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.AllOk)
        {
            return QuantityFormat.Count(report.SizeTimeOk) +
                " files verified by size and timestamp   ✔" +
                Environment.NewLine +
                QuantityFormat.Count(report.HashOk) +
                " files verified by SHA-256 (sample + all files under 64 MB)   ✔";
        }

        return QuantityFormat.Count(report.SizeTimeOk) +
            " of " +
            QuantityFormat.Count(report.SizeTimeFiles) +
            " files matched size and timestamp; " +
            QuantityFormat.Count(report.HashOk) +
            " of " +
            QuantityFormat.Count(report.HashFiles) +
            " SHA-256 checks passed.";
    }
}
