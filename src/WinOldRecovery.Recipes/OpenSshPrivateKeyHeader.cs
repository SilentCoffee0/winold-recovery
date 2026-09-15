using System.Buffers.Binary;
using System.Text;

namespace WinOldRecovery.Recipes;

internal static class OpenSshPrivateKeyHeader
{
    private const string Begin = "-----BEGIN OPENSSH PRIVATE KEY-----";
    private static readonly byte[] Magic = "openssh-key-v1\0"u8.ToArray();

    public static bool IsUnencrypted(ReadOnlySpan<byte> prefix)
    {
        return TryReadCipherName(prefix, out string cipher) &&
            cipher.Equals("none", StringComparison.Ordinal);
    }

    public static bool TryReadCipherName(ReadOnlySpan<byte> prefix, out string cipher)
    {
        cipher = string.Empty;
        string text = Encoding.ASCII.GetString(prefix);
        int begin = text.IndexOf(Begin, StringComparison.Ordinal);
        if (begin < 0)
        {
            return false;
        }

        int payload = begin + Begin.Length;
        while (payload < text.Length && char.IsWhiteSpace(text[payload]))
        {
            payload++;
        }

        int end = text.IndexOf("-----END", payload, StringComparison.Ordinal);
        string b64 = end < 0 ? text[payload..] : text[payload..end];
        b64 = b64.Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal);
        if (b64.Length < 8)
        {
            return false;
        }

        int take = Math.Min(b64.Length, 80);
        while (take % 4 != 0 && take < b64.Length)
        {
            take++;
        }

        take -= take % 4;
        byte[] decoded;
        try
        {
            decoded = Convert.FromBase64String(b64[..take]);
        }
        catch (FormatException)
        {
            return false;
        }

        if (decoded.Length < Magic.Length + 8 || !decoded.AsSpan(0, Magic.Length).SequenceEqual(Magic))
        {
            return false;
        }

        int offset = Magic.Length;
        if (!TryReadSshString(decoded, ref offset, out cipher))
        {
            cipher = string.Empty;
            return false;
        }

        return true;
    }

    private static bool TryReadSshString(byte[] data, ref int offset, out string value)
    {
        value = string.Empty;
        if (offset + 4 > data.Length)
        {
            return false;
        }

        int length = (int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
        offset += 4;
        if (length < 0 || offset + length > data.Length)
        {
            return false;
        }

        value = Encoding.ASCII.GetString(data, offset, length);
        offset += length;
        return true;
    }
}
