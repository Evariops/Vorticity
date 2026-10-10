using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using Vorticity.Parquet;
using Vorticity.Parquet.Codecs;
using Xunit;

namespace Vorticity.Parquet.Tests;

/// <summary>
/// GZIP pages, read by this package's own DEFLATE decoder: the base class library's members at every
/// level read back, around the decoder's fast path's limits; members one after the other and the
/// header's optional fields; hand-built blocks of every kind, and of codes zlib takes at their
/// edges; and every way a stream can break RFC 1951 or RFC 1952 refused, mutated members never
/// failing any other way.
/// </summary>
public sealed class GzipTests
{
    private static byte[] Shaped(Random random, int length, int shape)
    {
        byte[] data = new byte[length];
        switch (shape)
        {
            case 0:
                random.NextBytes(data);
                break;
            case 1:
                byte[] words = "the quick brown fox jumps over the lazy dog, Paris 2026 "u8.ToArray();
                for (int i = 0; i < length; i++)
                {
                    data[i] = random.Next(12) == 0 ? (byte)random.Next(256) : words[(i * 7 / 3) % words.Length];
                }

                break;
            case 2:
                // Short periods: matches whose source overlaps what they write, a byte apart among them.
                int period = random.Next(1, 20);
                for (int i = 0; i < length; i++)
                {
                    data[i] = (byte)(i % period * 37);
                }

                break;
            case 3:
                // A block of noise repeated from far back: distances near the window's 32 KiB.
                byte[] block = new byte[random.Next(20_000, 32_700)];
                random.NextBytes(block);
                for (int i = 0; i < length; i++)
                {
                    data[i] = random.Next(64) == 0 ? (byte)random.Next(256) : block[i % block.Length];
                }

                break;
            default:
                // Doubles of few values, as a column's plain pages hold them.
                for (int i = 0; i + 8 <= length; i += 8)
                {
                    BitConverter.TryWriteBytes(data.AsSpan(i), random.Next(500) / 4.0);
                }

                break;
        }

        return data;
    }

    private static byte[] Gzipped(ReadOnlySpan<byte> data, CompressionLevel level)
    {
        using MemoryStream stream = new();
        using (GZipStream gzip = new(stream, level, leaveOpen: true))
        {
            gzip.Write(data);
        }

        return stream.ToArray();
    }

    private static byte[] Decoded(byte[] member, int length)
    {
        byte[] read = new byte[length];
        Gzip.Decompress(member, read);
        return read;
    }

    [Fact]
    public void EveryLevelOfTheBaseLibraryReadsBack()
    {
        Random random = new(11);
        CompressionLevel[] levels = [CompressionLevel.NoCompression, CompressionLevel.Fastest, CompressionLevel.Optimal, CompressionLevel.SmallestSize];
        foreach (int length in new[] { 0, 1, 2, 7, 100, 289, 290, 291, 1_000, 65_535, 65_536, 200_000, 1_100_000 })
        {
            for (int shape = 0; shape < 5; shape++)
            {
                byte[] data = Shaped(random, length, shape);
                foreach (CompressionLevel level in levels)
                {
                    byte[] member = Gzipped(data, level);
                    Assert.True(data.AsSpan().SequenceEqual(Decoded(member, length)), $"length {length}, shape {shape}, {level}");
                }
            }
        }
    }

    [Fact]
    public void MembersDecodeOneAfterTheOther()
    {
        Random random = new(12);
        byte[] first = Shaped(random, 70_000, 1);
        byte[] second = Shaped(random, 5_000, 2);
        byte[] empty = Gzipped([], CompressionLevel.Optimal);
        byte[] page = [.. Gzipped(first, CompressionLevel.Optimal), .. empty, .. Gzipped(second, CompressionLevel.Fastest)];
        Assert.Equal([.. first, .. second], Decoded(page, first.Length + second.Length));
    }

    [Fact]
    public void TheHeadersOptionalFieldsAreSteppedOver()
    {
        byte[] data = "a page of text, read past a header that carries every field it may"u8.ToArray();
        byte[] plain = Gzipped(data, CompressionLevel.Optimal);
        List<byte> header = [.. plain.AsSpan(0, 10)];
        header[3] = 0x1E;
        header.AddRange(new byte[] { 3, 0, 1, 2, 3 });
        header.AddRange("page.bin\0"u8.ToArray());
        header.AddRange("a comment\0"u8.ToArray());
        uint crc = Crc32.HashToUInt32(header.ToArray());
        header.Add((byte)crc);
        header.Add((byte)(crc >> 8));
        byte[] member = [.. header, .. plain.AsSpan(10)];
        Assert.Equal(data, Decoded(member, data.Length));

        // A header whose CRC does not match it.
        member[header.Count - 1] ^= 1;
        Assert.Throws<ParquetFormatException>(() => Decoded(member, data.Length));
    }

    [Fact]
    public void HandBuiltBlocksOfEveryKindDecode()
    {
        // A fixed block: "abc", then a match of nine bytes from three back, which overlaps itself.
        Bits fixedBlock = new();
        fixedBlock.Write(1, 1).Write(1, 2);
        fixedBlock.Code(0x30 + 'a', 8).Code(0x30 + 'b', 8).Code(0x30 + 'c', 8);
        fixedBlock.Code(263 - 256, 7).Code(2, 5).Code(0, 7);
        Assert.Equal("abcabcabcabc"u8.ToArray(), Raw(fixedBlock.Bytes(), 12));

        // A stored block, then a fixed one that copies from it.
        Bits stored = new();
        stored.Write(0, 1).Write(0, 2).Align().Write(3, 16).Write(0xFFFC, 16).Write('x', 8).Write('y', 8).Write('z', 8);
        stored.Write(1, 1).Write(1, 2).Code(258 - 256, 7).Code(2, 5).Code(0, 7);
        Assert.Equal("xyzxyzx"u8.ToArray(), Raw(stored.Bytes(), 7));

        // A dynamic block of literals alone, its one distance codeword absent.
        byte[] litlen = new byte[257];
        litlen['a'] = litlen['b'] = litlen['c'] = litlen[256] = 2;
        Bits literals = Dynamic(litlen, [0]);
        literals.Code(0, 2).Code(1, 2).Code(2, 2).Code(1, 2).Code(3, 2);
        Assert.Equal("abcb"u8.ToArray(), Raw(literals.Bytes(), 4));

        // A dynamic block whose distance code is one codeword of one bit, the incomplete code zlib takes.
        litlen = new byte[258];
        litlen['a'] = 1;
        litlen[256] = litlen[257] = 2;
        Bits single = Dynamic(litlen, [1]);
        single.Code(0, 1).Code(3, 2).Code(0, 1).Code(2, 2);
        Assert.Equal("aaaa"u8.ToArray(), Raw(single.Bytes(), 4));
    }

    [Fact]
    public void BlocksThatBreakRfc1951AreRefused()
    {
        // A reserved block type.
        Refused(new Bits().Write(1, 1).Write(3, 2).Write(0, 16).Bytes(), 4);

        // A stored block whose length does not match its complement, or runs past the data.
        Refused(new Bits().Write(1, 1).Write(0, 2).Align().Write(3, 16).Write(0xFFFB, 16).Write('x', 8).Write('y', 8).Write('z', 8).Bytes(), 3);
        Refused(new Bits().Write(1, 1).Write(0, 2).Align().Write(5, 16).Write(0xFFFA, 16).Write('x', 8).Bytes(), 5);

        // A distance before the output's start, and a match past the output's end.
        Refused(new Bits().Write(1, 1).Write(1, 2).Code(0x30 + 'a', 8).Code(257 - 256, 7).Code(1, 5).Code(0, 7).Bytes(), 4);
        Refused(new Bits().Write(1, 1).Write(1, 2).Code(0x30 + 'a', 8).Code(263 - 256, 7).Code(0, 5).Code(0, 7).Bytes(), 5);

        // More literal and length codes than 286, more distance codes than 30.
        Refused(new Bits().Write(1, 1).Write(2, 2).Write(30, 5).Write(0, 5).Write(15, 4).Bytes(), 1);
        Refused(new Bits().Write(1, 1).Write(2, 2).Write(0, 5).Write(30, 5).Write(15, 4).Bytes(), 1);

        // A literal and length code with no end of block, over-subscribed, or incomplete.
        byte[] litlen = new byte[257];
        litlen['a'] = litlen['b'] = 1;
        Refused(Dynamic(litlen, [1]).Code(0, 1).Bytes(), 1);
        litlen = new byte[257];
        litlen['a'] = litlen['b'] = litlen[256] = 1;
        Refused(Dynamic(litlen, [1]).Code(0, 1).Bytes(), 1);
        litlen = new byte[257];
        litlen['a'] = litlen[256] = 2;
        Refused(Dynamic(litlen, [1]).Code(0, 2).Code(1, 2).Bytes(), 1);

        // A precode that repeats a length before any, or past the codes' count.
        Bits repeatFirst = new Bits().Write(1, 1).Write(2, 2).Write(0, 5).Write(0, 5).Write(15, 4);
        Precode(repeatFirst);
        Length(repeatFirst, 16).Write(0, 2);
        Refused(repeatFirst.Bytes(), 1);
        Bits repeatPast = new Bits().Write(1, 1).Write(2, 2).Write(0, 5).Write(0, 5).Write(15, 4);
        Precode(repeatPast);
        for (int i = 0; i < 3; i++)
        {
            Length(repeatPast, 18).Write(127, 7);
        }

        Refused(repeatPast.Bytes(), 1);

        // Data that ends inside a block.
        byte[] cut = new Bits().Write(1, 1).Write(1, 2).Code(0x30 + 'a', 8).Code(0x30 + 'b', 8).Bytes();
        Refused(cut, 2);
    }

    [Fact]
    public void MembersThatBreakRfc1952AreRefused()
    {
        byte[] data = "a member's trailer must describe what its data decodes to"u8.ToArray();
        byte[] member = Gzipped(data, CompressionLevel.Optimal);
        Assert.Throws<ParquetFormatException>(() => Gzip.Decompress([], new byte[1]));
        Mutated(member, data.Length, m => m[0] = 0x1E);
        Mutated(member, data.Length, m => m[2] = 7);
        Mutated(member, data.Length, m => m[3] = 0x20);
        Mutated(member, data.Length, m => m[^8] ^= 1);
        Mutated(member, data.Length, m => m[^4] ^= 1);
        Assert.Throws<ParquetFormatException>(() => Gzip.Decompress(member.AsSpan(0, member.Length - 1), new byte[data.Length]));
        Assert.Throws<ParquetFormatException>(() => Gzip.Decompress([.. member, 0], new byte[data.Length]));
        Assert.Throws<ParquetFormatException>(() => Gzip.Decompress(member, new byte[data.Length - 1]));
        Assert.Throws<ParquetFormatException>(() => Gzip.Decompress(member, new byte[data.Length + 1]));
    }

    [Fact]
    public void DataPastAnEmptyDestinationIsRefused()
    {
        // An empty destination is a null pointer: the decoder's fast loop must measure the room it
        // has, not hold the output to a limit taken off that pointer, which would wrap past any other.
        Random random = new(14);
        byte[] data = Shaped(random, 5_000, 1);
        byte[] member = Gzipped(data, CompressionLevel.Optimal);
        Assert.Throws<ParquetFormatException>(() => Gzip.Decompress(member, Span<byte>.Empty));
        Assert.Throws<ParquetFormatException>(() => Gzip.Decompress([.. member, .. member], new byte[data.Length]));
        Assert.Throws<ParquetFormatException>(() => Inflate.Decode(member.AsSpan(10), Span<byte>.Empty, new uint[Inflate.TableEntries], out _));
    }

    [Fact]
    public void AMutatedMemberIsReadOrRefusedAndNothingElse()
    {
        Random random = new(13);
        for (int trial = 0; trial < 3_000; trial++)
        {
            byte[] data = Shaped(random, random.Next(0, 40_000), random.Next(5));
            byte[] member = Gzipped(data, random.Next(2) == 0 ? CompressionLevel.Fastest : CompressionLevel.Optimal);
            int edits = random.Next(1, 4);
            for (int e = 0; e < edits; e++)
            {
                int at = random.Next(member.Length);
                member[at] = random.Next(3) == 0 ? (byte)random.Next(256) : (byte)(member[at] ^ (1 << random.Next(8)));
            }

            int length = random.Next(4) == 0 ? random.Next(member.Length) : member.Length;
            byte[] read = new byte[random.Next(3) == 0 ? random.Next(data.Length + 100) : data.Length];
            try
            {
                Gzip.Decompress(member.AsSpan(0, length), read);
            }
            catch (ParquetFormatException)
            {
            }
        }
    }

    private static void Mutated(byte[] member, int length, Action<byte[]> edit)
    {
        byte[] copy = (byte[])member.Clone();
        edit(copy);
        Assert.Throws<ParquetFormatException>(() => Gzip.Decompress(copy, new byte[length]));
    }

    private static byte[] Raw(byte[] deflate, int length)
    {
        byte[] read = new byte[length];
        int consumed = Inflate.Decode(deflate, read, new uint[Inflate.TableEntries], out int produced);
        Assert.Equal(length, produced);
        Assert.Equal(deflate.Length, consumed);
        return read;
    }

    private static void Refused(byte[] deflate, int length) =>
        Assert.Throws<ParquetFormatException>(() => Inflate.Decode(deflate, new byte[length], new uint[Inflate.TableEntries], out _));

    /// <summary>A complete precode: the lengths 0 to 12 four bits each, 13 to 15 and the three repeats five.</summary>
    private static void Precode(Bits bits)
    {
        // In the order the precode's lengths are sent: 16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15.
        int[] order = [16, 17, 18, 0, 8, 7, 9, 6, 10, 5, 11, 4, 12, 3, 13, 2, 14, 1, 15];
        foreach (int symbol in order)
        {
            bits.Write(symbol <= 12 ? 4u : 5u, 3);
        }
    }

    /// <summary>
    /// Symbol <paramref name="symbol"/> of <see cref="Precode"/>'s code: canonically, 0 to 12 the codes
    /// 0 to 12 of four bits, then 13 to 18 the codes 26 to 31 of five.
    /// </summary>
    private static Bits Length(Bits bits, int symbol) => symbol <= 12 ? bits.Code(symbol, 4) : bits.Code(26 + symbol - 13, 5);

    /// <summary>
    /// A final dynamic block's header declaring <paramref name="litlen"/> and <paramref name="distances"/>,
    /// each length sent through <see cref="Precode"/>'s code.
    /// </summary>
    private static Bits Dynamic(byte[] litlen, byte[] distances)
    {
        Bits bits = new Bits().Write(1, 1).Write(2, 2).Write((uint)(litlen.Length - 257), 5).Write((uint)(distances.Length - 1), 5).Write(15, 4);
        Precode(bits);
        foreach (byte length in (byte[])[.. litlen, .. distances])
        {
            Length(bits, length);
        }

        return bits;
    }

    /// <summary>A stream of bits, as DEFLATE packs them: each field from its lowest bit, a codeword from its first.</summary>
    private sealed class Bits
    {
        private readonly List<byte> _bytes = [];
        private ulong _pending;
        private int _count;

        internal Bits Write(uint value, int count)
        {
            _pending |= (ulong)value << _count;
            _count += count;
            Flush();
            return this;
        }

        internal Bits Code(int code, int length)
        {
            for (int i = length - 1; i >= 0; i--)
            {
                Write((uint)(code >> i) & 1, 1);
            }

            return this;
        }

        internal Bits Align()
        {
            _count = (_count + 7) & ~7;
            Flush();
            return this;
        }

        internal byte[] Bytes()
        {
            Align();
            return [.. _bytes];
        }

        private void Flush()
        {
            while (_count >= 8)
            {
                _bytes.Add((byte)_pending);
                _pending >>= 8;
                _count -= 8;
            }
        }
    }
}
