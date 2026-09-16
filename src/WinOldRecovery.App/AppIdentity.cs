using System.Globalization;
using System.Reflection;

namespace WinOldRecovery.App;

public sealed record AppIdentityInfo(string Version, string Commit, string BuildDate, string Trademark);

public static class AppIdentity
{
    public const string Trademark =
        "WinOld Recovery is an independent community project. It is not affiliated with, endorsed by, or supported by Microsoft Corporation. Windows is a trademark of Microsoft Corporation.";

    public static AppIdentityInfo Current { get; } = From(typeof(AppIdentity).Assembly);

    public static AppIdentityInfo From(Assembly assembly)
    {
        ArgumentNullException.ThrowIfNull(assembly);
        string informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?.Trim() ?? "0.1.0";
        string version = informational;
        string commit = string.Empty;
        int plus = informational.IndexOf('+', StringComparison.Ordinal);
        if (plus >= 0)
        {
            version = informational[..plus];
            commit = informational[(plus + 1)..];
        }

        if (string.IsNullOrWhiteSpace(version))
        {
            version = assembly.GetName().Version?.ToString(3) ?? "0.1.0";
        }

        string buildDate = assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(static attribute => attribute.Key == "BuildDate")
            ?.Value
            ?.Trim() ?? string.Empty;
        return new AppIdentityInfo(version.Trim(), commit.Trim(), buildDate, Trademark);
    }

    public static string Format(AppIdentityInfo info)
    {
        ArgumentNullException.ThrowIfNull(info);
        string[] lines =
        [
            "Version " + (string.IsNullOrWhiteSpace(info.Version) ? "0.1.0" : info.Version),
            string.IsNullOrWhiteSpace(info.Commit) ? "Commit untagged" : "Commit " + info.Commit,
            string.IsNullOrWhiteSpace(info.BuildDate)
                ? "Build date unknown"
                : "Built " + info.BuildDate,
            string.Empty,
            info.Trademark,
        ];
        return string.Join(Environment.NewLine, lines);
    }

    public static bool LooksLikeIsoDate(string value)
    {
        return DateTime.TryParseExact(
            value,
            "yyyy-MM-dd",
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out _);
    }
}
