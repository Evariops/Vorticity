// The Parquet mutations, and why they rewrite Thrift varints in place.
//
// A Parquet file is pages, each behind a Thrift compact header, and a footer of Thrift compact
// metadata: offsets, sizes and counts are varints, whose bytes a random flip turns into a field
// header the parser rejects at once, or a value of another length that shifts everything after it.
// So the mutator walks the footer and the page headers as Thrift, notes where each varint lies and
// how long it is, and rewrites one IN PLACE, at its own length, to a value at an edge: zero, one,
// one off, twice, the most its bytes hold. The structure stays well formed; the value is the one a
// bounds check exists for. That is how a Class I field, an offset, a size, a count, a list length,
// a decompressed size, reaches the code that trusts it.
//
// Beside it: the footer's length, the first bytes of a page's body where an encoding's header
// lies (a bit width, a DELTA block size), a decompression bomb, truncation, a word in the data,
// and the bit flip as the control.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

namespace Vorticity.Fuzz;

/// <summary>Structure-aware mutations over a Parquet file.</summary>
internal static class ParquetMutator
{
    /// <summary>How many distinct mutation kinds there are.</summary>
    internal const int Kinds = 8;

    /// <summary>A varint of the file: where it starts, its bytes, and whether it is zigzag-encoded (a Thrift i16, i32 or i64).</summary>
    private readonly record struct Varint(int At, int Length, bool ZigZag, string Where);

    /// <summary>Applies one mutation to a copy of <paramref name="original"/>.</summary>
    internal static (byte[] Bytes, string What) Apply(byte[] original, Random random, int kind)
    {
        byte[] bytes = (byte[])original.Clone();
        if (bytes.Length < 64)
        {
            return (bytes, "untouched (too small)");
        }

        return kind switch
        {
            0 => FooterVarint(bytes, random),
            1 => FooterLength(bytes, random),
            2 => PageVarint(bytes, random),
            3 => EncodingHeader(bytes, random),
            4 => Bomb(bytes, random),
            5 => Truncate(bytes, random),
            6 => Word(bytes, random),
            _ => BitFlip(bytes, random),
        };
    }

    /// <summary>Rewrites one varint of the footer's metadata at its own length.</summary>
    private static (byte[], string) FooterVarint(byte[] bytes, Random random)
    {
        if (!Footer(bytes, out int start, out int end))
        {
            return BitFlip(bytes, random);
        }

        List<Varint> varints = [];
        Walk(bytes, start, end, varints, "footer");
        return Rewrite(bytes, varints, random);
    }

    /// <summary>Writes an edge into the footer's length, which says where the metadata starts.</summary>
    private static (byte[], string) FooterLength(byte[] bytes, Random random)
    {
        int at = bytes.Length - 8;
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(at));
        ReadOnlySpan<int> edges = [0, 1, length - 1, length + 1, bytes.Length - 8, bytes.Length - 7, int.MaxValue, -1, int.MinValue];
        int value = edges[random.Next(edges.Length)];
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(at), value);
        return (bytes, $"footer length {value}");
    }

    /// <summary>Rewrites one varint of a page header, of a chunk the footer places, at its own length.</summary>
    private static (byte[], string) PageVarint(byte[] bytes, Random random)
    {
        List<Varint> varints = [];
        Pages(bytes, (at, end, _) => Walk(bytes, at, end, varints, $"page at {at}"));
        return varints.Count == 0 ? BitFlip(bytes, random) : Rewrite(bytes, varints, random);
    }

    /// <summary>
    /// Writes an edge into the first bytes of a page's body, where its levels' lengths, an RLE run's
    /// bit width or a DELTA block's sizes lie.
    /// </summary>
    private static (byte[], string) EncodingHeader(byte[] bytes, Random random)
    {
        List<(int Body, int Length)> bodies = [];
        Pages(bytes, (_, _, body) => bodies.Add(body));
        if (bodies.Count == 0)
        {
            return BitFlip(bytes, random);
        }

        (int at, int length) = bodies[random.Next(bodies.Count)];
        if (length <= 0)
        {
            return BitFlip(bytes, random);
        }

        int offset = at + random.Next(Math.Min(length, 8));
        ReadOnlySpan<byte> edges = [0x00, 0x01, 0x07, 0x08, 0x20, 0x21, 0x40, 0x41, 0x7F, 0x80, 0xFF];
        byte value = edges[random.Next(edges.Length)];
        bytes[offset] = value;
        return (bytes, $"encoding byte {value:X2} at {offset}");
    }

    /// <summary>A page claiming a decompressed size past every cap: its header's second field at its own length's most.</summary>
    private static (byte[], string) Bomb(byte[] bytes, Random random)
    {
        List<Varint> sizes = [];
        Pages(bytes, (at, end, _) =>
        {
            List<Varint> fields = [];
            Walk(bytes, at, end, fields, $"page at {at}", depth: 0, onlyTop: true);
            if (fields.Count > 1)
            {
                sizes.Add(fields[1]);
            }
        });

        if (sizes.Count == 0)
        {
            return BitFlip(bytes, random);
        }

        Varint size = sizes[random.Next(sizes.Count)];
        Write(bytes, size, Most(size.Length));
        return (bytes, $"bomb at {size.At}, {size.Where}");
    }

    private static (byte[], string) Truncate(byte[] bytes, Random random)
    {
        int length = random.Next(1, bytes.Length);
        return (bytes[..length], $"truncated to {length}");
    }

    private static (byte[], string) Word(byte[] bytes, Random random)
    {
        int at = random.Next(bytes.Length - 4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(at), (uint)random.Next());
        return (bytes, $"word at {at}");
    }

    private static (byte[], string) BitFlip(byte[] bytes, Random random)
    {
        int at = random.Next(bytes.Length);
        bytes[at] ^= (byte)(1 << random.Next(8));
        return (bytes, $"bit flip at {at}");
    }

    /// <summary>One varint of <paramref name="varints"/> rewritten to an edge its bytes hold.</summary>
    private static (byte[], string) Rewrite(byte[] bytes, List<Varint> varints, Random random)
    {
        if (varints.Count == 0)
        {
            return BitFlip(bytes, random);
        }

        Varint varint = varints[random.Next(varints.Count)];
        long original = Read(bytes, varint);
        long most = Most(varint.Length);
        ReadOnlySpan<long> edges = [0, 1, original - 1, original + 1, original * 2, most, -1, -most, original / 2];
        long value = edges[random.Next(edges.Length)];
        Write(bytes, varint, value);
        return (bytes, $"{varint.Where}: varint at {varint.At} from {original} to {value}");
    }

    /// <summary>The largest magnitude a zigzag varint of <paramref name="length"/> bytes holds.</summary>
    private static long Most(int length) => length >= 9 ? long.MaxValue : ((1L << (7 * length)) - 1) >> 1;

    private static long Read(byte[] bytes, Varint varint)
    {
        ulong value = 0;
        for (int i = 0; i < varint.Length; i++)
        {
            value |= (ulong)(bytes[varint.At + i] & 0x7F) << (7 * i);
        }

        return varint.ZigZag ? (long)(value >> 1) ^ -(long)(value & 1) : (long)value;
    }

    /// <summary>Writes <paramref name="value"/> over <paramref name="varint"/> at its own length, its high bits cut to fit.</summary>
    private static void Write(byte[] bytes, Varint varint, long value)
    {
        ulong raw = varint.ZigZag ? (ulong)((value << 1) ^ (value >> 63)) : (ulong)value;
        for (int i = 0; i < varint.Length; i++)
        {
            byte low = (byte)(raw & 0x7F);
            raw >>= 7;
            bytes[varint.At + i] = i < varint.Length - 1 ? (byte)(low | 0x80) : low;
        }
    }

    /// <summary>Where the footer's metadata lies, by the length before the closing magic.</summary>
    private static bool Footer(byte[] bytes, out int start, out int end)
    {
        end = bytes.Length - 8;
        int length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(end));
        start = end - length;
        return length > 0 && start >= 4;
    }

    /// <summary>
    /// Calls <paramref name="page"/> with each page header's start, the chunk's end, and the page's
    /// body, for every chunk the footer's metadata places by its first page's offset and its bytes.
    /// </summary>
    private static void Pages(byte[] bytes, Action<int, int, (int Body, int Length)> page)
    {
        if (!Footer(bytes, out int start, out int end))
        {
            return;
        }

        // The chunks' offsets and lengths, read by walking the footer and keeping each column
        // chunk's data page offset (field 9 of ColumnMetaData) and compressed size (field 7).
        List<(long Offset, long Length)> chunks = [];
        Chunks(bytes, start, end, chunks);
        int visited = 0;
        foreach ((long offset, long length) in chunks)
        {
            if (offset < 4 || length <= 0 || offset + length > start)
            {
                continue;
            }

            int at = (int)offset;
            int chunkEnd = (int)(offset + length);
            while (at < chunkEnd && visited++ < 4_096)
            {
                List<Varint> fields = [];
                int header = Walk(bytes, at, chunkEnd, fields, "", depth: 0, onlyTop: true);
                if (header <= at || fields.Count < 3)
                {
                    break;
                }

                long stored = Read(bytes, fields[2]);
                if (stored < 0 || header + stored > chunkEnd)
                {
                    break;
                }

                page(at, chunkEnd, (header, (int)stored));
                at = header + (int)stored;
            }
        }
    }

    /// <summary>
    /// Walks a Thrift compact struct from <paramref name="at"/>, noting every varint, and returns where
    /// it ends, or <paramref name="at"/> when it is not one. <paramref name="onlyTop"/> notes the top
    /// struct's fields alone, in their order: a page header's type and two sizes first.
    /// </summary>
    private static int Walk(byte[] bytes, int at, int end, List<Varint> varints, string where, int depth = 0, bool onlyTop = false)
    {
        int position = at;
        short last = 0;
        while (position < end && depth < 16)
        {
            byte header = bytes[position++];
            if (header == 0)
            {
                return position;
            }

            int type = header & 0x0F;
            int delta = header >> 4;
            short id = delta != 0 ? (short)(last + delta) : (short)ReadZigZag(bytes, ref position, end, varints, where, record: false);
            last = id;
            if (!Value(bytes, ref position, end, type, varints, where, depth, record: !onlyTop || depth == 0, onlyTop))
            {
                return at;
            }
        }

        return at;
    }

    /// <summary>Skips one value of compact type <paramref name="type"/>, noting its varints; false where the bytes are no value.</summary>
    private static bool Value(byte[] bytes, ref int position, int end, int type, List<Varint> varints, string where, int depth, bool record, bool onlyTop = false)
    {
        switch (type)
        {
            case 1:
            case 2:
                // A boolean in the field header.
                return true;
            case 3:
                // A byte.
                position++;
                return position <= end;
            case 4:
            case 5:
            case 6:
                ReadZigZag(bytes, ref position, end, varints, where, record);
                return position <= end;
            case 7:
                position += 8;
                return position <= end;
            case 8:
            {
                long length = ReadPlain(bytes, ref position, end, varints, where, record);
                if (length < 0 || position + length > end)
                {
                    return false;
                }

                position += (int)length;
                return true;
            }

            case 9:
            case 10:
            {
                if (position >= end)
                {
                    return false;
                }

                byte header = bytes[position];
                int element = header & 0x0F;
                long count = header >> 4;
                if (count == 15)
                {
                    position++;
                    count = ReadPlain(bytes, ref position, end, varints, where, record);
                }
                else
                {
                    position++;
                }

                for (long i = 0; i < count && position < end; i++)
                {
                    if (!Value(bytes, ref position, end, element == 2 ? 1 : element, varints, where, depth + 1, record, onlyTop))
                    {
                        return false;
                    }
                }

                return true;
            }

            case 11:
            {
                long count = ReadPlain(bytes, ref position, end, varints, where, record);
                if (count > 0 && position < end)
                {
                    byte kinds = bytes[position++];
                    for (long i = 0; i < count && position < end; i++)
                    {
                        if (!Value(bytes, ref position, end, kinds >> 4, varints, where, depth + 1, record, onlyTop)
                            || !Value(bytes, ref position, end, kinds & 0x0F, varints, where, depth + 1, record, onlyTop))
                        {
                            return false;
                        }
                    }
                }

                return true;
            }

            case 12:
            {
                int start = position;
                int stop = Walk(bytes, position, end, varints, where, depth + 1, onlyTop || !record);
                if (stop <= start)
                {
                    return false;
                }

                position = stop;
                return true;
            }

            default:
                return false;
        }
    }

    private static long ReadZigZag(byte[] bytes, ref int position, int end, List<Varint> varints, string where, bool record)
    {
        int start = position;
        ulong raw = ReadRaw(bytes, ref position, end);
        if (record && position > start)
        {
            varints.Add(new Varint(start, position - start, ZigZag: true, where));
        }

        return (long)(raw >> 1) ^ -(long)(raw & 1);
    }

    private static long ReadPlain(byte[] bytes, ref int position, int end, List<Varint> varints, string where, bool record)
    {
        int start = position;
        ulong raw = ReadRaw(bytes, ref position, end);
        if (record && position > start)
        {
            varints.Add(new Varint(start, position - start, ZigZag: false, where));
        }

        return raw > int.MaxValue ? -1 : (long)raw;
    }

    private static ulong ReadRaw(byte[] bytes, ref int position, int end)
    {
        ulong value = 0;
        for (int shift = 0; position < end && shift < 64; shift += 7)
        {
            byte b = bytes[position++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                break;
            }
        }

        return value;
    }

    /// <summary>
    /// The column chunks of the footer from <paramref name="start"/> to <paramref name="end"/>: for each,
    /// the dictionary or data page offset its metadata gives, the first that is there, and its
    /// compressed bytes. Read by a walk that follows FileMetaData's row groups (field 4), their
    /// columns (field 1), their metadata (field 3).
    /// </summary>
    private static void Chunks(byte[] bytes, int start, int end, List<(long Offset, long Length)> chunks)
    {
        // Every ColumnMetaData lies as a struct of fields 7 (total_compressed_size, i64), 9
        // (data_page_offset, i64) and 11 (dictionary_page_offset, i64): found by scanning for
        // structs that hold field 9 after field 7, which the walk below does in the footer's order.
        Scan(bytes, start, end, chunks, depth: 0);
    }

    private static int Scan(byte[] bytes, int position, int end, List<(long Offset, long Length)> chunks, int depth)
    {
        short last = 0;
        long compressed = -1;
        long data = -1;
        long dictionary = -1;
        while (position < end && depth < 16)
        {
            byte header = bytes[position++];
            if (header == 0)
            {
                break;
            }

            int type = header & 0x0F;
            int delta = header >> 4;
            short id = delta != 0 ? (short)(last + delta) : (short)Zag(ReadRaw(bytes, ref position, end));
            last = id;
            if (type == 6 && id is 7 or 9 or 11)
            {
                long value = Zag(ReadRaw(bytes, ref position, end));
                if (id == 7)
                {
                    compressed = value;
                }
                else if (id == 9)
                {
                    data = value;
                }
                else
                {
                    dictionary = value;
                }

                continue;
            }

            if (type == 12)
            {
                position = Scan(bytes, position, end, chunks, depth + 1);
                continue;
            }

            if (type is 9 or 10)
            {
                if (position >= end)
                {
                    break;
                }

                byte list = bytes[position++];
                int element = list & 0x0F;
                long count = list >> 4;
                if (count == 15)
                {
                    count = (long)ReadRaw(bytes, ref position, end);
                }

                for (long i = 0; i < count && position < end; i++)
                {
                    if (element == 12)
                    {
                        position = Scan(bytes, position, end, chunks, depth + 1);
                    }
                    else
                    {
                        List<Varint> ignored = [];
                        if (!Value(bytes, ref position, end, element == 2 ? 1 : element, ignored, "", depth + 1, record: false))
                        {
                            return end;
                        }
                    }
                }

                continue;
            }

            List<Varint> skipped = [];
            if (!Value(bytes, ref position, end, type, skipped, "", depth, record: false))
            {
                return end;
            }
        }

        if (compressed > 0 && data > 0)
        {
            long first = dictionary > 0 && dictionary < data ? dictionary : data;
            chunks.Add((first, compressed));
        }

        return position;
    }

    private static long Zag(ulong raw) => (long)(raw >> 1) ^ -(long)(raw & 1);
}
