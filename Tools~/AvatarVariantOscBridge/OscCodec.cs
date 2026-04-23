using System.Buffers.Binary;
using System.Text;

namespace AvatarVariantOscBridge;

internal sealed class OscMessage
{
    public string Address { get; }
    public List<object> Arguments { get; }

    public OscMessage(string address, List<object> arguments)
    {
        Address = address;
        Arguments = arguments;
    }
}

internal static class OscCodec
{
    public static bool TryReadMessage(byte[] buffer, out OscMessage? message)
    {
        message = null;
        try
        {
            var offset = 0;
            var address = ReadPaddedString(buffer, ref offset);
            if (string.IsNullOrWhiteSpace(address)) return false;

            var typeTag = ReadPaddedString(buffer, ref offset);
            if (string.IsNullOrWhiteSpace(typeTag) || typeTag[0] != ',') return false;

            var args = new List<object>();
            for (var i = 1; i < typeTag.Length; i++)
            {
                switch (typeTag[i])
                {
                    case 'i': args.Add(ReadInt(buffer, ref offset)); break;
                    case 'f': args.Add(ReadFloat(buffer, ref offset)); break;
                    case 's': args.Add(ReadPaddedString(buffer, ref offset)); break;
                    case 'T': args.Add(true); break;
                    case 'F': args.Add(false); break;
                    default: return false;
                }
            }

            message = new OscMessage(address, args);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static byte[] WriteMessage(string address, string value)
    {
        using var stream = new MemoryStream();
        WritePaddedString(stream, address);
        WritePaddedString(stream, ",s");
        WritePaddedString(stream, value);
        return stream.ToArray();
    }

    private static string ReadPaddedString(byte[] buffer, ref int offset)
    {
        var start = offset;
        while (offset < buffer.Length && buffer[offset] != 0) offset++;
        if (offset > buffer.Length) throw new InvalidOperationException("Invalid OSC string.");

        var value = Encoding.UTF8.GetString(buffer, start, offset - start);

        // Advance past the terminator and any additional null bytes up to the next 4-byte boundary.
        while (offset < buffer.Length && buffer[offset] == 0)
        {
            offset++;
            if (offset % 4 == 0) break;
        }

        return value;
    }

    private static int ReadInt(byte[] buffer, ref int offset)
    {
        var value = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset, 4));
        offset += 4;
        return value;
    }

    private static float ReadFloat(byte[] buffer, ref int offset)
    {
        var bits = BinaryPrimitives.ReadInt32BigEndian(buffer.AsSpan(offset, 4));
        offset += 4;
        return BitConverter.Int32BitsToSingle(bits);
    }

    private static void WritePaddedString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        stream.Write(bytes, 0, bytes.Length);
        stream.WriteByte(0);
        while (stream.Length % 4 != 0) stream.WriteByte(0);
    }
}
