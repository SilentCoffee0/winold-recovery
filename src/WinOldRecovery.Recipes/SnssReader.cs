using System.Buffers.Binary;
using System.Text;

namespace WinOldRecovery.Recipes;

public static class SnssReader
{
    public const byte UpdateTabNavigation = 6;

    public static byte[] CreateSessionFile(int version, IReadOnlyList<(byte CommandId, byte[] Payload)> commands)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream, Encoding.ASCII, leaveOpen: true);
        writer.Write("SNSS"u8);
        writer.Write(version);
        foreach ((byte commandId, byte[] payload) in commands)
        {
            int size = 1 + payload.Length;
            if (size > ushort.MaxValue)
            {
                throw new InvalidOperationException("SNSS command exceeds 64 KiB.");
            }

            writer.Write((ushort)size);
            writer.Write(commandId);
            writer.Write(payload);
        }

        return stream.ToArray();
    }

    public static IReadOnlyList<string> ReadTabUrls(ReadOnlySpan<byte> data)
    {
        if (data.Length < 8 ||
            data[0] != (byte)'S' ||
            data[1] != (byte)'N' ||
            data[2] != (byte)'S' ||
            data[3] != (byte)'S')
        {
            return [];
        }

        int offset = 8;
        List<string> urls = [];
        HashSet<string> seen = new(StringComparer.Ordinal);
        while (offset + 3 <= data.Length)
        {
            ushort size = BinaryPrimitives.ReadUInt16LittleEndian(data[offset..]);
            offset += 2;
            if (size == 0 || offset + size > data.Length)
            {
                break;
            }

            byte commandId = data[offset];
            ReadOnlySpan<byte> payload = data.Slice(offset + 1, size - 1);
            offset += size;
            if (commandId != UpdateTabNavigation)
            {
                continue;
            }

            foreach (string url in ExtractUrls(payload))
            {
                if (seen.Add(url))
                {
                    urls.Add(url);
                }
            }
        }

        return urls;
    }

    private static IEnumerable<string> ExtractUrls(ReadOnlySpan<byte> payload)
    {
        List<string> urls = [];
        CollectAscii(payload, urls);
        if (payload.Length >= 2)
        {
            string utf16 = Encoding.Unicode.GetString(payload);
            CollectFromText(utf16, urls);
        }

        return urls;
    }

    private static void CollectAscii(ReadOnlySpan<byte> payload, List<string> urls)
    {
        CollectFromText(Encoding.ASCII.GetString(payload), urls);
        CollectFromText(Encoding.UTF8.GetString(payload), urls);
    }

    private static void CollectFromText(string text, List<string> urls)
    {
        int index = 0;
        while (index < text.Length)
        {
            int http = text.IndexOf("http://", index, StringComparison.OrdinalIgnoreCase);
            int https = text.IndexOf("https://", index, StringComparison.OrdinalIgnoreCase);
            int start = MinPositive(http, https);
            if (start < 0)
            {
                break;
            }

            int end = start;
            while (end < text.Length && text[end] > 32 && text[end] < 127 && text[end] is not ('"' or '\'' or '<' or '>' or ')'))
            {
                end++;
            }

            string url = text[start..end].TrimEnd('.', ',', ';');
            if (url.Length > 10)
            {
                urls.Add(url);
            }

            index = start + 1;
        }
    }

    private static int MinPositive(int left, int right)
    {
        if (left < 0)
        {
            return right;
        }

        if (right < 0)
        {
            return left;
        }

        return Math.Min(left, right);
    }
}
