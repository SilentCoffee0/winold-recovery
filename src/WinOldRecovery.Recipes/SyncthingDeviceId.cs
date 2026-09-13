using System.Security.Cryptography;
using System.Text;

namespace WinOldRecovery.Recipes;

public static class SyncthingDeviceId
{
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string FromCertificatePem(string pem)
    {
        byte[] der = PemToDer(pem, "CERTIFICATE");
        return FromCertificateDer(der);
    }

    public static string FromCertificateDer(byte[] der)
    {
        byte[] hash = SHA256.HashData(der);
        string encoded = ToBase32(hash);
        string withCheck = Luhnify(encoded);
        return Chunkify(withCheck);
    }

    internal static byte[] PemToDer(string pem, string label)
    {
        string begin = "-----BEGIN " + label + "-----";
        string end = "-----END " + label + "-----";
        int start = pem.IndexOf(begin, StringComparison.Ordinal);
        int stop = pem.IndexOf(end, StringComparison.Ordinal);
        if (start < 0 || stop < 0 || stop <= start)
        {
            return Encoding.UTF8.GetBytes(pem);
        }

        string body = pem[(start + begin.Length)..stop].Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal).Trim();
        return Convert.FromBase64String(body);
    }

    private static string ToBase32(byte[] data)
    {
        StringBuilder builder = new((data.Length * 8 + 4) / 5);
        int buffer = 0;
        int bits = 0;
        foreach (byte value in data)
        {
            buffer = (buffer << 8) | value;
            bits += 8;
            while (bits >= 5)
            {
                bits -= 5;
                builder.Append(Alphabet[(buffer >> bits) & 31]);
            }
        }

        if (bits > 0)
        {
            builder.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        }

        return builder.ToString();
    }

    private static string Luhnify(string encoded)
    {
        if (encoded.Length != 52)
        {
            return encoded;
        }

        StringBuilder builder = new(56);
        for (int group = 0; group < 4; group++)
        {
            string chunk = encoded.Substring(group * 13, 13);
            builder.Append(chunk);
            builder.Append(Luhn32(chunk));
        }

        return builder.ToString();
    }

    private static char Luhn32(string chunk)
    {
        int factor = 1;
        int sum = 0;
        for (int i = chunk.Length - 1; i >= 0; i--)
        {
            int codePoint = Alphabet.IndexOf(chunk[i]);
            if (codePoint < 0)
            {
                return 'A';
            }

            int addend = factor * codePoint;
            factor = 3 - factor;
            addend = (addend / 32) + (addend % 32);
            sum += addend;
        }

        int check = (32 - (sum % 32)) % 32;
        return Alphabet[check];
    }

    private static string Chunkify(string value)
    {
        if (value.Length != 56)
        {
            return value;
        }

        StringBuilder builder = new(63);
        for (int i = 0; i < 8; i++)
        {
            if (i > 0)
            {
                builder.Append('-');
            }

            builder.Append(value.AsSpan(i * 7, 7));
        }

        return builder.ToString();
    }
}
