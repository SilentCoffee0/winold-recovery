using System.Buffers.Binary;
using K4os.Compression.LZ4;

namespace WinOldRecovery.Recipes;

public static class MozLz4
{
    public const int MaxUncompressedBytes = 16 * 1024 * 1024;

    private static ReadOnlySpan<byte> Magic => "mozLz40\0"u8;

    public static bool TryDecode(ReadOnlySpan<byte> data, out byte[] utf8)
    {
        utf8 = [];
        if (data.Length < 12 || !data[..8].SequenceEqual(Magic))
        {
            return false;
        }

        int size = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        if (size is < 1 or > MaxUncompressedBytes)
        {
            return false;
        }

        try
        {
            byte[] output = new byte[size];
            int decoded = LZ4Codec.Decode(data[12..], output);
            if (decoded < 0 || decoded != size)
            {
                return false;
            }

            utf8 = output;
            return true;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidDataException)
        {
            return false;
        }
    }

    public static byte[] Encode(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length is 0 or > MaxUncompressedBytes)
        {
            throw new ArgumentOutOfRangeException(nameof(utf8));
        }

        int max = LZ4Codec.MaximumOutputSize(utf8.Length);
        byte[] buffer = new byte[max];
        int encoded = LZ4Codec.Encode(utf8, buffer);
        byte[] result = new byte[12 + encoded];
        Magic.CopyTo(result.AsSpan());
        BinaryPrimitives.WriteInt32LittleEndian(result.AsSpan(8), utf8.Length);
        buffer.AsSpan(0, encoded).CopyTo(result.AsSpan(12));
        return result;
    }
}
