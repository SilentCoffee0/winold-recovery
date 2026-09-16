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
                " files verified by SHA-256 (sample + files 64 MB or smaller, VHDX, and sensitive items)   ✔" +
                Environment.NewLine +
                "0 failures. You can now open the restored files and apps and confirm they work." +
                Environment.NewLine +
                "Purge is available once every restore job is verified.";
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
