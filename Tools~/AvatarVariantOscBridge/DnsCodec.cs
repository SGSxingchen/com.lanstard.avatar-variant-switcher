using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace AvatarVariantOscBridge;

/// <summary>
/// Subset of DNS wire-format codec needed for mDNS service discovery:
/// PTR / SRV / TXT / A records, plus label compression on the read path.
///
/// Not a full DNS implementation — we only parse what VRChat / Bonjour emit
/// and serialize what we need to announce / query.
///
/// References: RFC 1035 (DNS), RFC 6762 (mDNS).
/// </summary>
internal static class DnsCodec
{
    public const ushort TypeA = 1;
    public const ushort TypePtr = 12;
    public const ushort TypeTxt = 16;
    public const ushort TypeSrv = 33;
    public const ushort TypeAny = 255;

    public const ushort ClassIn = 0x0001;
    public const ushort ClassInCacheFlush = 0x8001; // mDNS "unique" bit on responses
    public const ushort ClassMask = 0x7FFF;
}

internal sealed class DnsQuestion
{
    public string Name { get; set; } = string.Empty;
    public ushort Type { get; set; }
    public ushort Class { get; set; } = DnsCodec.ClassIn;
}

internal sealed class DnsSrvData
{
    public ushort Priority;
    public ushort Weight;
    public ushort Port;
    public string Target = string.Empty;
}

internal sealed class DnsRecord
{
    public string Name { get; set; } = string.Empty;
    public ushort Type { get; set; }
    public ushort Class { get; set; } = DnsCodec.ClassIn;
    public uint Ttl { get; set; } = 120;

    // Typed RData — exactly one is populated per Type.
    public string? PtrName;
    public DnsSrvData? Srv;
    public List<string>? TxtItems;
    public IPAddress? Address;
    public byte[]? RawRData;
}

internal sealed class DnsPacket
{
    public ushort TransactionId;
    public bool IsResponse;
    public bool AuthoritativeAnswer;
    public List<DnsQuestion> Questions { get; } = new();
    public List<DnsRecord> Answers { get; } = new();
    public List<DnsRecord> Authorities { get; } = new();
    public List<DnsRecord> Additionals { get; } = new();

    // -------- Parse --------

    public static bool TryParse(byte[] buffer, out DnsPacket? packet)
    {
        packet = null;
        try
        {
            if (buffer.Length < 12) return false;

            var p = new DnsPacket
            {
                TransactionId = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(0, 2)),
            };
            var flags = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(2, 2));
            p.IsResponse = (flags & 0x8000) != 0;
            p.AuthoritativeAnswer = (flags & 0x0400) != 0;

            var qd = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(4, 2));
            var an = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(6, 2));
            var ns = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(8, 2));
            var ar = BinaryPrimitives.ReadUInt16BigEndian(buffer.AsSpan(10, 2));

            var offset = 12;
            for (var i = 0; i < qd; i++)
                p.Questions.Add(ReadQuestion(buffer, ref offset));
            for (var i = 0; i < an; i++)
                p.Answers.Add(ReadRecord(buffer, ref offset));
            for (var i = 0; i < ns; i++)
                p.Authorities.Add(ReadRecord(buffer, ref offset));
            for (var i = 0; i < ar; i++)
                p.Additionals.Add(ReadRecord(buffer, ref offset));

            packet = p;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static DnsQuestion ReadQuestion(byte[] buf, ref int offset)
    {
        var name = ReadName(buf, ref offset);
        var type = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(offset, 2)); offset += 2;
        var cls = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(offset, 2)); offset += 2;
        return new DnsQuestion { Name = name, Type = type, Class = cls };
    }

    private static DnsRecord ReadRecord(byte[] buf, ref int offset)
    {
        var name = ReadName(buf, ref offset);
        var type = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(offset, 2)); offset += 2;
        var cls = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(offset, 2)); offset += 2;
        var ttl = BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(offset, 4)); offset += 4;
        var rdlen = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(offset, 2)); offset += 2;

        var rdStart = offset;
        var rdEnd = offset + rdlen;
        var rec = new DnsRecord { Name = name, Type = type, Class = cls, Ttl = ttl };

        switch (type)
        {
            case DnsCodec.TypeA:
                if (rdlen == 4)
                {
                    rec.Address = new IPAddress(buf.AsSpan(rdStart, 4).ToArray());
                }
                break;
            case DnsCodec.TypePtr:
            {
                var cursor = rdStart;
                rec.PtrName = ReadName(buf, ref cursor);
                break;
            }
            case DnsCodec.TypeSrv:
            {
                var cursor = rdStart;
                var priority = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(cursor, 2)); cursor += 2;
                var weight = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(cursor, 2)); cursor += 2;
                var port = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(cursor, 2)); cursor += 2;
                var target = ReadName(buf, ref cursor);
                rec.Srv = new DnsSrvData
                {
                    Priority = priority, Weight = weight, Port = port, Target = target,
                };
                break;
            }
            case DnsCodec.TypeTxt:
            {
                var items = new List<string>();
                var cursor = rdStart;
                while (cursor < rdEnd)
                {
                    int len = buf[cursor++];
                    if (len == 0 || cursor + len > rdEnd) break;
                    items.Add(Encoding.UTF8.GetString(buf, cursor, len));
                    cursor += len;
                }
                rec.TxtItems = items;
                break;
            }
            default:
                rec.RawRData = buf.AsSpan(rdStart, rdlen).ToArray();
                break;
        }

        offset = rdEnd;
        return rec;
    }

    // Read a DNS name starting at `offset`. Advances `offset` past the top-level
    // name encoding (including a terminating 0 byte OR a 2-byte pointer).
    // Pointers jump to earlier positions; we follow them but do NOT advance the
    // outer cursor past the pointer's target data.
    private static string ReadName(byte[] buf, ref int offset)
    {
        var parts = new List<string>();
        var cursor = offset;
        int? endOfThisName = null;
        var hops = 0;

        while (true)
        {
            if (cursor >= buf.Length) throw new InvalidDataException("DNS name ran past end of buffer.");

            var len = buf[cursor];
            if (len == 0)
            {
                cursor++;
                if (endOfThisName == null) endOfThisName = cursor;
                break;
            }
            if ((len & 0xC0) == 0xC0)
            {
                // Compression pointer — 2 bytes total, upper 2 bits are 11,
                // remaining 14 bits are offset into buf.
                if (cursor + 1 >= buf.Length) throw new InvalidDataException("Truncated DNS pointer.");
                var ptr = ((len & 0x3F) << 8) | buf[cursor + 1];
                cursor += 2;
                if (endOfThisName == null) endOfThisName = cursor;
                cursor = ptr;
                if (++hops > 32) throw new InvalidDataException("Too many DNS name pointer hops.");
                continue;
            }
            if ((len & 0xC0) != 0) throw new InvalidDataException("Invalid DNS name label.");

            cursor++;
            if (cursor + len > buf.Length) throw new InvalidDataException("DNS label runs past end of buffer.");
            parts.Add(Encoding.UTF8.GetString(buf, cursor, len));
            cursor += len;
        }

        offset = endOfThisName!.Value;
        return string.Join('.', parts) + ".";
    }

    // -------- Serialize --------

    public byte[] Serialize()
    {
        using var ms = new MemoryStream();
        Span<byte> hdr = stackalloc byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(hdr, TransactionId);
        ushort flags = 0;
        if (IsResponse) flags |= 0x8000;
        if (AuthoritativeAnswer) flags |= 0x0400;
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(2, 2), flags);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(4, 2), (ushort)Questions.Count);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(6, 2), (ushort)Answers.Count);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(8, 2), (ushort)Authorities.Count);
        BinaryPrimitives.WriteUInt16BigEndian(hdr.Slice(10, 2), (ushort)Additionals.Count);
        ms.Write(hdr);

        foreach (var q in Questions)
        {
            WriteName(ms, q.Name);
            WriteUInt16(ms, q.Type);
            WriteUInt16(ms, q.Class);
        }
        foreach (var r in Answers) WriteRecord(ms, r);
        foreach (var r in Authorities) WriteRecord(ms, r);
        foreach (var r in Additionals) WriteRecord(ms, r);

        return ms.ToArray();
    }

    private static void WriteRecord(MemoryStream ms, DnsRecord r)
    {
        WriteName(ms, r.Name);
        WriteUInt16(ms, r.Type);
        WriteUInt16(ms, r.Class);
        WriteUInt32(ms, r.Ttl);

        // Defer RDLENGTH: reserve 2 bytes, write data, patch length.
        var lenPos = ms.Position;
        WriteUInt16(ms, 0);
        var dataStart = ms.Position;

        switch (r.Type)
        {
            case DnsCodec.TypeA:
                if (r.Address == null) throw new InvalidOperationException("A record missing address.");
                var addr = r.Address.GetAddressBytes();
                if (addr.Length != 4) throw new InvalidOperationException("A record needs IPv4.");
                ms.Write(addr, 0, 4);
                break;
            case DnsCodec.TypePtr:
                if (r.PtrName == null) throw new InvalidOperationException("PTR missing target.");
                WriteName(ms, r.PtrName);
                break;
            case DnsCodec.TypeSrv:
                if (r.Srv == null) throw new InvalidOperationException("SRV missing data.");
                WriteUInt16(ms, r.Srv.Priority);
                WriteUInt16(ms, r.Srv.Weight);
                WriteUInt16(ms, r.Srv.Port);
                WriteName(ms, r.Srv.Target);
                break;
            case DnsCodec.TypeTxt:
                if (r.TxtItems == null || r.TxtItems.Count == 0)
                {
                    // RFC 6763: empty TXT must still have a single zero-length item.
                    ms.WriteByte(0);
                }
                else
                {
                    foreach (var item in r.TxtItems)
                    {
                        var bytes = Encoding.UTF8.GetBytes(item);
                        if (bytes.Length > 255) throw new InvalidOperationException("TXT item >255 bytes.");
                        ms.WriteByte((byte)bytes.Length);
                        ms.Write(bytes, 0, bytes.Length);
                    }
                }
                break;
            default:
                if (r.RawRData == null) throw new InvalidOperationException($"Unsupported record type {r.Type} without RawRData.");
                ms.Write(r.RawRData, 0, r.RawRData.Length);
                break;
        }

        var dataEnd = ms.Position;
        var rdlen = checked((ushort)(dataEnd - dataStart));
        ms.Position = lenPos;
        WriteUInt16(ms, rdlen);
        ms.Position = dataEnd;
    }

    // Write names uncompressed. Any name ending with "." is treated as FQDN;
    // the trailing dot maps to the root label (zero byte).
    private static void WriteName(MemoryStream ms, string name)
    {
        if (string.IsNullOrEmpty(name))
        {
            ms.WriteByte(0);
            return;
        }
        // Trim trailing dot so Split doesn't emit an empty label at the end.
        var trimmed = name.EndsWith('.') ? name.Substring(0, name.Length - 1) : name;
        if (trimmed.Length > 0)
        {
            foreach (var label in trimmed.Split('.'))
            {
                var bytes = Encoding.UTF8.GetBytes(label);
                if (bytes.Length == 0 || bytes.Length > 63)
                    throw new InvalidOperationException($"Invalid DNS label length: {bytes.Length}");
                ms.WriteByte((byte)bytes.Length);
                ms.Write(bytes, 0, bytes.Length);
            }
        }
        ms.WriteByte(0);
    }

    private static void WriteUInt16(MemoryStream ms, ushort value)
    {
        Span<byte> tmp = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(tmp, value);
        ms.Write(tmp);
    }

    private static void WriteUInt32(MemoryStream ms, uint value)
    {
        Span<byte> tmp = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(tmp, value);
        ms.Write(tmp);
    }
}
