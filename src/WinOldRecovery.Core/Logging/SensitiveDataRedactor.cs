using System.Collections.Concurrent;
using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Core.Logging;

public sealed class SensitiveDataRedactor : ILogRedactor
{
    private readonly ConcurrentDictionary<string, string> replacements =
        new(StringComparer.OrdinalIgnoreCase);

    public void RegisterSensitivePath(long nodeId, string path)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(nodeId);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        string replacement = $"<sensitive:{nodeId}>";
        replacements[path] = replacement;

        string normalized = PathCanonicalizer.NormalizeLexically(path);
        replacements[normalized] = replacement;
        if (normalized.StartsWith(@"\\?\", StringComparison.Ordinal))
        {
            replacements[normalized[4..]] = replacement;
        }
    }

    public void RegisterSecretLiteral(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);
        if (secret.Length < 4)
        {
            throw new ArgumentException(
                "Secret literals shorter than four characters cannot be registered.",
                nameof(secret));
        }

        replacements[secret] = "<redacted>";
    }

    public string Redact(string value)
    {
        if (string.IsNullOrEmpty(value) || replacements.IsEmpty)
        {
            return value;
        }

        string redacted = value;
        foreach ((string sensitive, string replacement) in replacements
                     .OrderByDescending(static pair => pair.Key.Length))
        {
            redacted = redacted.Replace(
                sensitive,
                replacement,
                StringComparison.OrdinalIgnoreCase);
        }

        return redacted;
    }
}
