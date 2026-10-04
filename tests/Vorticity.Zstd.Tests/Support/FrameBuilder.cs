using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Linq;

namespace Vorticity.Zstd.Tests.Support;

/// <summary>A sequence as the format states it: the offset is the raw <c>Offset_Value</c> (1 to 3 repeat).</summary>
internal readonly record struct Seq(int LiteralLength, int MatchLength, uint OffsetValue);

/// <summary>
/// Writes zstd frames by hand, field by field, so that a test can state exactly which construct of
/// the format it exercises. Sequences are encoded with the RLE mode of the three tables, which needs
/// no FSE encoder: every sequence of a block then shares its three codes, and only the extra bits vary.
/// </summary>
internal sealed class FrameBuilder
{
    private readonly List<byte> _blocks = [];

    public ulong? ContentSize { get; set; }
    public bool SingleSegment { get; set; }
    public int WindowLog { get; set; } = 20;
    public int WindowMantissa { get; set; }
    public uint DictionaryId { get; set; }

    /// <summary>The size of the Dictionary_ID field: 0, 1, 2 or 4 bytes; null picks the smallest.</summary>
    public int? DictionaryIdSize { get; set; }

    /// <summary>The size of the Frame_Content_Size field: 1 (single segment only), 2, 4 or 8; null picks the smallest.</summary>
    public int? ContentSizeFieldSize { get; set; }

    /// <summary>When set, a content checksum computed over this content is appended.</summary>
    public byte[]? ChecksumOf { get; set; }

    public bool ReservedBit { get; set; }

    public FrameBuilder Raw(ReadOnlySpan<byte> data, bool last = false) => Block(0, data.Length, data, last);

    public FrameBuilder Rle(byte value, int size, bool last = false) => Block(1, size, [value], last);

    public FrameBuilder Compressed(ReadOnlySpan<byte> content, bool last = false) => Block(2, content.Length, content, last);

    /// <summary>A block whose header says what the caller wants, whatever follows it.</summary>
    public FrameBuilder Block(int type, int size, ReadOnlySpan<byte> content, bool last)
    {
        int header = (last ? 1 : 0) | (type << 1) | (size << 3);
        _blocks.Add((byte)header);
        _blocks.Add((byte)(header >> 8));
        _blocks.Add((byte)(header >> 16));
        _blocks.AddRange(content.ToArray());
        return this;
    }

    public byte[] Build()
    {
        var frame = new List<byte>();
        Span<byte> magic = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(magic, 0xFD2FB528);
        frame.AddRange(magic.ToArray());

        int didSize = DictionaryIdSize ?? (DictionaryId == 0 ? 0 : DictionaryId < 256 ? 1 : DictionaryId < 65536 ? 2 : 4);
        int didCode = didSize == 4 ? 3 : didSize;
        int fcsSize = 0;
        if (ContentSize is ulong size)
        {
            fcsSize = ContentSizeFieldSize ?? (SingleSegment && size < 256 ? 1 : size - 256 < 65536 && size >= 256 ? 2 : size <= uint.MaxValue ? 4 : 8);
        }
        else if (SingleSegment)
        {
            throw new InvalidOperationException("a single-segment frame declares its size");
        }

        int fcsCode = fcsSize switch { 0 or 1 => 0, 2 => 1, 4 => 2, _ => 3 };
        int descriptor = (fcsCode << 6) | (SingleSegment ? 1 << 5 : 0) | (ReservedBit ? 1 << 3 : 0) | (ChecksumOf is null ? 0 : 1 << 2) | didCode;
        frame.Add((byte)descriptor);
        if (!SingleSegment)
        {
            frame.Add((byte)(((WindowLog - 10) << 3) | WindowMantissa));
        }

        for (int i = 0; i < didSize; i++)
        {
            frame.Add((byte)(DictionaryId >> (8 * i)));
        }

        ulong field = fcsSize == 2 ? ContentSize!.Value - 256 : ContentSize ?? 0;
        for (int i = 0; i < fcsSize; i++)
        {
            frame.Add((byte)(field >> (8 * i)));
        }

        frame.AddRange(_blocks);
        if (ChecksumOf is not null)
        {
            uint checksum = (uint)XxHash64.HashToUInt64(ChecksumOf);
            for (int i = 0; i < 4; i++)
            {
                frame.Add((byte)(checksum >> (8 * i)));
            }
        }

        return [.. frame];
    }

    // ------------------------------------------------------------------ block contents

    /// <summary>A literals section storing <paramref name="literals"/> as they are.</summary>
    public static byte[] RawLiterals(ReadOnlySpan<byte> literals) => [.. LiteralsHeader(0, literals.Length), .. literals];

    /// <summary>A literals section repeating one byte.</summary>
    public static byte[] RleLiterals(byte value, int count) => [.. LiteralsHeader(1, count), value];

    private static byte[] LiteralsHeader(int type, int size)
    {
        if (size < 32)
        {
            return [(byte)(type | (size << 3))];
        }

        if (size < 4096)
        {
            int header = type | (1 << 2) | (size << 4);
            return [(byte)header, (byte)(header >> 8)];
        }

        int large = type | (3 << 2) | (size << 4);
        return [(byte)large, (byte)(large >> 8), (byte)(large >> 16)];
    }

    /// <summary>A sequences section with no sequence.</summary>
    public static byte[] NoSequences() => [0];

    /// <summary>
    /// A sequences section in which every table is in RLE mode; the sequences must therefore share
    /// their literal length, match length and offset codes.
    /// </summary>
    public static byte[] RleSequences(params Seq[] sequences)
    {
        if (sequences.Length == 0)
        {
            return NoSequences();
        }

        var codes = sequences.Select(s => (Ll: LiteralLengthCode(s.LiteralLength), Ml: MatchLengthCode(s.MatchLength), Of: OffsetCode(s.OffsetValue))).ToArray();
        if (codes.Distinct().Count() != 1)
        {
            throw new ArgumentException("RLE mode: the sequences of a block share their codes", nameof(sequences));
        }

        var section = new List<byte>();
        section.AddRange(SequenceCount(sequences.Length));
        section.Add(0x54); // LL, OF and ML in RLE mode
        section.Add((byte)codes[0].Ll);
        section.Add((byte)codes[0].Of);
        section.Add((byte)codes[0].Ml);

        // Written last sequence first, each as literal length, match length, offset bits: the
        // decoder reads them back in reverse.
        var bits = new BitWriter();
        for (int i = sequences.Length - 1; i >= 0; i--)
        {
            Seq s = sequences[i];
            (int ll, int ml, int of) = codes[i];
            bits.Add((ulong)(s.LiteralLength - LiteralLengthBase[ll]), LiteralLengthBits[ll]);
            bits.Add((ulong)(s.MatchLength - MatchLengthBase[ml]), MatchLengthBits[ml]);
            bits.Add(s.OffsetValue - (1u << of), of);
        }

        section.AddRange(bits.Close());
        return [.. section];
    }

    /// <summary>The Number_of_Sequences field in its shortest form.</summary>
    public static byte[] SequenceCount(int count) => count switch
    {
        < 128 => [(byte)count],
        < 0x7F00 => [(byte)((count >> 8) + 0x80), (byte)count],
        _ => [0xFF, (byte)(count - 0x7F00), (byte)((count - 0x7F00) >> 8)],
    };

    public static readonly int[] LiteralLengthBase =
    [
        0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 18, 20, 22, 24, 28, 32, 40,
        48, 64, 0x80, 0x100, 0x200, 0x400, 0x800, 0x1000, 0x2000, 0x4000, 0x8000, 0x10000,
    ];

    public static readonly int[] LiteralLengthBits =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3,
        4, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
    ];

    public static readonly int[] MatchLengthBase =
    [
        3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16, 17, 18, 19, 20, 21, 22, 23, 24, 25, 26,
        27, 28, 29, 30, 31, 32, 33, 34, 35, 37, 39, 41, 43, 47, 51, 59, 67, 83, 99, 0x83, 0x103,
        0x203, 0x403, 0x803, 0x1003, 0x2003, 0x4003, 0x8003, 0x10003,
    ];

    public static readonly int[] MatchLengthBits =
    [
        0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0,
        0, 0, 0, 0, 0, 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 3, 4, 4, 5, 7, 8, 9, 10, 11, 12, 13, 14, 15, 16,
    ];

    public static int LiteralLengthCode(int value) => CodeOf(value, LiteralLengthBase, LiteralLengthBits);

    public static int MatchLengthCode(int value) => CodeOf(value, MatchLengthBase, MatchLengthBits);

    public static int OffsetCode(uint offsetValue) => offsetValue == 0
        ? throw new ArgumentOutOfRangeException(nameof(offsetValue))
        : 31 - System.Numerics.BitOperations.LeadingZeroCount(offsetValue);

    private static int CodeOf(int value, int[] bases, int[] bits)
    {
        for (int code = bases.Length - 1; code >= 0; code--)
        {
            if (value >= bases[code] && value - bases[code] < (1 << bits[code]))
            {
                return code;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(value), value, "no code");
    }
}
