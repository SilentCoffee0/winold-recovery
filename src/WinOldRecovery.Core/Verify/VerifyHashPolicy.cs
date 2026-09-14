using System.Security.Cryptography;
using System.Text;
using WinOldRecovery.Core.Hashing;

namespace WinOldRecovery.Core.Verify;

public static class VerifyHashPolicy
{
    public const int SamplePercent = 2;
    public const int SampleMinimum = 200;

    public static bool IsMandatory(
        string path,
        long byteLength,
        bool sensitive,
        bool strongVerify)
    {
        if (byteLength <= 0)
        {
            return false;
        }

        return strongVerify ||
            sensitive ||
            FileHashingPass.RequiresContentHash(path, byteLength);
    }

    public static int SampleSize(int candidateCount)
    {
        if (candidateCount <= 0)
        {
            return 0;
        }

        int twoPercent = Math.Max(1, candidateCount * SamplePercent / 100);
        return Math.Min(candidateCount, Math.Max(twoPercent, SampleMinimum));
    }

    public static IReadOnlySet<string> SelectSample(
        IReadOnlyList<string> candidatePaths,
        string seed)
    {
        ArgumentNullException.ThrowIfNull(candidatePaths);
        ArgumentException.ThrowIfNullOrWhiteSpace(seed);
        if (candidatePaths.Count == 0)
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }

        int needed = SampleSize(candidatePaths.Count);
        return candidatePaths
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => RankKey(seed, path), StringComparer.Ordinal)
            .ThenBy(path => path, StringComparer.OrdinalIgnoreCase)
            .Take(needed)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private static string RankKey(string seed, string path)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(seed + "\0" + path);
        return Convert.ToHexString(SHA256.HashData(bytes));
    }
}
