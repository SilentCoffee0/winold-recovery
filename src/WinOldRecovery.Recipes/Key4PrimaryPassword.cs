using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using WinOldRecovery.Core.IO;

namespace WinOldRecovery.Recipes;

public static class Key4PrimaryPassword
{
    public const string Unknown = "unknown (cannot determine)";
    public const string Set = "set";
    public const string NotSet = "not set";
    public const string Missing = "not present";

    private static readonly byte[] PasswordCheck = "password-check\0"u8.ToArray();
    private const string Pbes2Oid = "1.2.840.113549.1.5.13";

    public static string Detect(SafeFs safeFs, string sourcePath, string tempDirectory)
    {
        ArgumentNullException.ThrowIfNull(safeFs);
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(tempDirectory);
        if (!safeFs.FileExists(sourcePath))
        {
            return Missing;
        }

        try
        {
            try
            {
                using SqliteConnection direct = ReadOnlySqlite.OpenReadOnly(sourcePath);
                return DetectFromConnection(direct);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException or SqliteException)
            {
            }

            string copy = ReadOnlySqlite.CopyToTemp(safeFs, sourcePath, tempDirectory, "key4.db");
            return DetectFromCopy(copy);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SqliteException)
        {
            return Unknown;
        }
    }

    public static string DetectFromCopy(string key4Copy)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key4Copy);
        try
        {
            using SqliteConnection connection = ReadOnlySqlite.OpenReadOnly(key4Copy);
            return DetectFromConnection(connection);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or SqliteException or CryptographicException)
        {
            return Unknown;
        }
    }

    private static string DetectFromConnection(SqliteConnection connection)
    {
        try
        {
            if (!TryReadPasswordCheck(connection, out byte[] globalSalt, out byte[] item2))
            {
                return Unknown;
            }

            if (globalSalt.Length == 0 || item2.Length is 0 or > 65_536)
            {
                return Unknown;
            }

            return CheckEmptyPassword(globalSalt, item2);
        }
        catch (Exception exception) when (exception is SqliteException or CryptographicException)
        {
            return Unknown;
        }
    }

    private static bool TryReadPasswordCheck(
        SqliteConnection connection,
        out byte[] globalSalt,
        out byte[] item2)
    {
        globalSalt = [];
        item2 = [];
        foreach (string table in (string[])["metaData", "metadata"])
        {
            try
            {
                using SqliteCommand command = connection.CreateCommand();
                command.CommandText = "SELECT item1, item2 FROM " + table + " WHERE id = 'password' LIMIT 1;";
                using SqliteDataReader reader = command.ExecuteReader();
                if (!reader.Read() || reader.IsDBNull(0) || reader.IsDBNull(1))
                {
                    continue;
                }

                globalSalt = reader.GetFieldValue<byte[]>(0);
                item2 = reader.GetFieldValue<byte[]>(1);
                return globalSalt.Length > 0 && item2.Length > 0;
            }
            catch (SqliteException)
            {
            }
        }

        return false;
    }

    private static string CheckEmptyPassword(byte[] globalSalt, byte[] item2)
    {
        if (!TryParseItem2(item2, out string oid, out byte[] parameters, out byte[] cipher))
        {
            return Unknown;
        }

        if (cipher.Length == 0)
        {
            return Unknown;
        }

        byte[]? plain;
        try
        {
            plain = oid == Pbes2Oid
                ? DecryptPbes2(globalSalt, parameters, cipher)
                : Decrypt3Des(globalSalt, parameters, cipher);
        }
        catch (Exception exception) when (exception is CryptographicException or InvalidOperationException)
        {
            return Set;
        }

        if (plain is null)
        {
            return Unknown;
        }

        return IsPasswordCheck(plain) ? NotSet : Set;
    }

    private static bool TryParseItem2(
        byte[] item2,
        out string oid,
        out byte[] parameters,
        out byte[] cipher)
    {
        oid = string.Empty;
        parameters = [];
        cipher = [];
        try
        {
            int offset = 0;
            DerValue top = Der.Read(item2, ref offset);
            if (top.Tag != 0x30)
            {
                return false;
            }

            List<DerValue> children = Der.Sequence(top.Body);
            if (children.Count < 2 || children[0].Tag != 0x30 || children[1].Tag != 0x04)
            {
                return false;
            }

            List<DerValue> algorithm = Der.Sequence(children[0].Body);
            if (algorithm.Count < 2 || algorithm[0].Tag != 0x06)
            {
                return false;
            }

            oid = Der.Oid(algorithm[0].Body);
            parameters = algorithm[1].Body;
            cipher = children[1].Body;
            return oid.Length > 0 && cipher.Length > 0;
        }
        catch (Exception exception) when (exception is ArgumentOutOfRangeException or InvalidOperationException)
        {
            return false;
        }
    }

    private static byte[]? DecryptPbes2(byte[] globalSalt, byte[] parameters, byte[] cipher)
    {
        List<DerValue> pbes = Der.Sequence(parameters);
        if (pbes.Count < 2 || pbes[0].Tag != 0x30 || pbes[1].Tag != 0x30)
        {
            return null;
        }

        List<DerValue> kdf = Der.Sequence(pbes[0].Body);
        List<DerValue> enc = Der.Sequence(pbes[1].Body);
        if (kdf.Count < 2 || enc.Count < 2 || kdf[1].Tag != 0x30 || enc[1].Tag != 0x04)
        {
            return null;
        }

        List<DerValue> pbkdf = Der.Sequence(kdf[1].Body);
        if (pbkdf.Count < 3 || pbkdf[0].Tag != 0x04 || pbkdf[1].Tag != 0x02 || pbkdf[2].Tag != 0x02)
        {
            return null;
        }

        byte[] entrySalt = pbkdf[0].Body;
        int iterations = Der.Integer(pbkdf[1].Body);
        int keyLength = Der.Integer(pbkdf[2].Body);
        if (iterations is < 1 or > 5_000_000 || keyLength is < 16 or > 64)
        {
            return null;
        }

        byte[] iv = enc[1].Body;
        if (iv.Length == 14)
        {
            iv = [0x04, 0x0E, .. iv];
        }

        if (iv.Length != 16 || cipher.Length % 16 != 0)
        {
            return null;
        }

#pragma warning disable CA5350
        byte[] hp = SHA1.HashData(globalSalt);
#pragma warning restore CA5350
        byte[] key = Rfc2898DeriveBytes.Pbkdf2(hp, entrySalt, iterations, HashAlgorithmName.SHA256, keyLength);
        using Aes aes = Aes.Create();
        aes.Mode = CipherMode.CBC;
        aes.Padding = PaddingMode.None;
        aes.Key = key;
        aes.IV = iv;
        using ICryptoTransform transform = aes.CreateDecryptor();
        byte[] decrypted = transform.TransformFinalBlock(cipher, 0, cipher.Length);
        return Unpad(decrypted, 16) ?? [];
    }

    private static byte[]? Decrypt3Des(byte[] globalSalt, byte[] parameters, byte[] cipher)
    {
        byte[]? entrySalt = FindOctetString(parameters);
        if (entrySalt is null || entrySalt.Length == 0 || cipher.Length % 8 != 0)
        {
            return null;
        }

#pragma warning disable CA5350
        byte[] hp = SHA1.HashData(globalSalt);
        byte[] pes = Pad20(entrySalt);
        byte[] chp = SHA1.HashData([.. hp, .. entrySalt]);
        byte[] k1 = HMACSHA1.HashData((ReadOnlySpan<byte>)chp, (ReadOnlySpan<byte>)[.. pes, .. entrySalt]);
        byte[] tk = HMACSHA1.HashData((ReadOnlySpan<byte>)chp, (ReadOnlySpan<byte>)pes);
        byte[] k2 = HMACSHA1.HashData((ReadOnlySpan<byte>)chp, (ReadOnlySpan<byte>)[.. tk, .. entrySalt]);
#pragma warning restore CA5350
        byte[] material = [.. k1, .. k2];
        byte[] key = material[..24];
        byte[] iv = material[^8..];
#pragma warning disable SYSLIB0021
        using TripleDES des = TripleDES.Create();
#pragma warning restore SYSLIB0021
        des.Mode = CipherMode.CBC;
        des.Padding = PaddingMode.None;
        des.Key = key;
        des.IV = iv;
        using ICryptoTransform transform = des.CreateDecryptor();
        byte[] decrypted = transform.TransformFinalBlock(cipher, 0, cipher.Length);
        return Unpad(decrypted, 8) ?? [];
    }

    private static byte[]? FindOctetString(byte[] parameters)
    {
        if (parameters.Length == 0)
        {
            return null;
        }

        if (parameters[0] == 0x04)
        {
            int offset = 0;
            return Der.Read(parameters, ref offset).Body;
        }

        if (parameters[0] == 0x30)
        {
            int offset = 0;
            DerValue seq = Der.Read(parameters, ref offset);
            foreach (DerValue child in Der.Sequence(seq.Body))
            {
                if (child.Tag == 0x04)
                {
                    return child.Body;
                }

                if (child.Tag == 0x30)
                {
                    foreach (DerValue nested in Der.Sequence(child.Body))
                    {
                        if (nested.Tag == 0x04)
                        {
                            return nested.Body;
                        }
                    }
                }
            }
        }

        return null;
    }

    private static byte[] Pad20(byte[] entrySalt)
    {
        if (entrySalt.Length >= 20)
        {
            return entrySalt;
        }

        byte[] padded = new byte[20];
        entrySalt.CopyTo(padded, 0);
        return padded;
    }

    private static byte[]? Unpad(byte[] data, int blockSize)
    {
        if (data.Length == 0 || data.Length % blockSize != 0)
        {
            return null;
        }

        int pad = data[^1];
        if (pad is < 1 || pad > blockSize)
        {
            return null;
        }

        for (int i = 1; i <= pad; i++)
        {
            if (data[^i] != pad)
            {
                return null;
            }
        }

        return data[..^pad];
    }

    private static bool IsPasswordCheck(byte[] plain)
    {
        if (plain.Length < PasswordCheck.Length - 1)
        {
            return false;
        }

        for (int i = 0; i < PasswordCheck.Length - 1; i++)
        {
            if (plain[i] != PasswordCheck[i])
            {
                return false;
            }
        }

        return plain.Length == PasswordCheck.Length - 1 ||
            (plain.Length >= PasswordCheck.Length && plain[PasswordCheck.Length - 1] == 0);
    }

    private readonly struct DerValue
    {
        public DerValue(byte tag, byte[] body)
        {
            Tag = tag;
            Body = body;
        }

        public byte Tag { get; }
        public byte[] Body { get; }
    }

    private static class Der
    {
        public static DerValue Read(ReadOnlySpan<byte> data, ref int offset)
        {
            if (offset >= data.Length)
            {
                throw new InvalidOperationException("truncated DER");
            }

            byte tag = data[offset++];
            int length = ReadLength(data, ref offset);
            if (offset + length > data.Length)
            {
                throw new InvalidOperationException("truncated DER value");
            }

            byte[] body = data.Slice(offset, length).ToArray();
            offset += length;
            return new DerValue(tag, body);
        }

        public static List<DerValue> Sequence(ReadOnlySpan<byte> body)
        {
            int offset = 0;
            List<DerValue> values = [];
            while (offset < body.Length)
            {
                values.Add(Read(body, ref offset));
            }

            return values;
        }

        public static string Oid(ReadOnlySpan<byte> body)
        {
            if (body.Length == 0)
            {
                return string.Empty;
            }

            int first = body[0];
            string oid = (first / 40).ToString() + "." + (first % 40).ToString();
            int value = 0;
            for (int i = 1; i < body.Length; i++)
            {
                value = (value << 7) | (body[i] & 0x7F);
                if ((body[i] & 0x80) == 0)
                {
                    oid += "." + value.ToString();
                    value = 0;
                }
            }

            return oid;
        }

        public static int Integer(ReadOnlySpan<byte> body)
        {
            if (body.Length is 0 or > 4)
            {
                throw new InvalidOperationException("integer size");
            }

            int value = 0;
            foreach (byte b in body)
            {
                value = (value << 8) | b;
            }

            return value;
        }

        private static int ReadLength(ReadOnlySpan<byte> data, ref int offset)
        {
            if (offset >= data.Length)
            {
                throw new InvalidOperationException("truncated DER length");
            }

            int first = data[offset++];
            if ((first & 0x80) == 0)
            {
                return first;
            }

            int count = first & 0x7F;
            if (count is 0 or > 3 || offset + count > data.Length)
            {
                throw new InvalidOperationException("unsupported DER length");
            }

            int length = 0;
            for (int i = 0; i < count; i++)
            {
                length = (length << 8) | data[offset++];
            }

            return length;
        }
    }
}
