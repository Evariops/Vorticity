using System;
using System.Collections.Generic;

namespace Vorticity.Zstd.Tests.Fuzz;

/// <summary>
/// Breaks valid frames in the ways that matter: bit flips and truncations anywhere, and, knowing
/// where things are, sizes that lie (content size, block sizes, literals and sequence counts),
/// corrupted table descriptions and corrupted bitstreams.
/// </summary>
internal sealed class FrameMutator
{
    private readonly Random _random;
    private readonly IReadOnlyList<byte[]> _donors;

    public FrameMutator(Random random, IReadOnlyList<byte[]> donors)
    {
        _random = random;
        _donors = donors;
    }

    public byte[] Mutate(byte[] frame)
    {
        byte[] result = frame;
        int rounds = 1 + (_random.Next(4) == 0 ? _random.Next(3) : 0);
        for (int i = 0; i < rounds; i++)
        {
            result = MutateOnce(result);
        }

        return result;
    }

    private byte[] MutateOnce(byte[] frame)
    {
        if (frame.Length == 0)
        {
            return [(byte)_random.Next(256)];
        }

        List<Block> blocks = Blocks(frame, out int headerSize);
        switch (_random.Next(10))
        {
            case 0:
            case 1:
                return FlipBits(frame, 0, frame.Length);
            case 2:
                return SetBytes(frame, 0, frame.Length);
            case 3:
                return frame.AsSpan(0, _random.Next(frame.Length)).ToArray();
            case 4:
                return InsertOrDelete(frame);
            case 5:
                return LieInHeader(frame, headerSize);
            case 6:
                return blocks.Count > 0 ? LieInBlockHeader(frame, blocks[_random.Next(blocks.Count)]) : FlipBits(frame, 0, frame.Length);
            case 7:
            {
                // The start of a compressed block: literals header, Huffman tree, sequences header.
                Block? block = PickCompressed(blocks);
                return block is { } b ? (_random.Next(2) == 0 ? SetBytes(frame, b.Content, Math.Min(b.End, b.Content + 24)) : FlipBits(frame, b.Content, Math.Min(b.End, b.Content + 24))) : SetBytes(frame, 0, frame.Length);
            }

            case 8:
            {
                // The end of a compressed block: the sequences' bitstream and its end marker.
                Block? block = PickCompressed(blocks);
                return block is { } b ? FlipBits(frame, Math.Max(b.Content, b.End - 16), b.End) : FlipBits(frame, 0, frame.Length);
            }

            default:
                return Splice(frame);
        }
    }

    private byte[] FlipBits(byte[] frame, int start, int end)
    {
        if (end <= start)
        {
            return frame;
        }

        byte[] copy = (byte[])frame.Clone();
        int flips = 1 + _random.Next(_random.Next(4) == 0 ? 8 : 2);
        for (int i = 0; i < flips; i++)
        {
            copy[start + _random.Next(end - start)] ^= (byte)(1 << _random.Next(8));
        }

        return copy;
    }

    private static readonly byte[] Interesting = [0x00, 0x01, 0x7F, 0x80, 0xFF, 0xFE, 0x10, 0x3F, 0xC0];

    private byte[] SetBytes(byte[] frame, int start, int end)
    {
        if (end <= start)
        {
            return frame;
        }

        byte[] copy = (byte[])frame.Clone();
        int count = 1 + _random.Next(3);
        for (int i = 0; i < count; i++)
        {
            copy[start + _random.Next(end - start)] = _random.Next(2) == 0 ? Interesting[_random.Next(Interesting.Length)] : (byte)_random.Next(256);
        }

        return copy;
    }

    private byte[] InsertOrDelete(byte[] frame)
    {
        int at = _random.Next(frame.Length);
        int count = 1 + _random.Next(_random.Next(4) == 0 ? 64 : 4);
        if (_random.Next(2) == 0)
        {
            byte[] inserted = new byte[count];
            _random.NextBytes(inserted);
            return [.. frame.AsSpan(0, at), .. inserted, .. frame.AsSpan(at)];
        }

        count = Math.Min(count, frame.Length - at);
        return [.. frame.AsSpan(0, at), .. frame.AsSpan(at + count)];
    }

    /// <summary>Rewrites the frame header: descriptor flags, window, content size.</summary>
    private byte[] LieInHeader(byte[] frame, int headerSize)
    {
        int end = Math.Min(headerSize, frame.Length);
        if (end <= 4)
        {
            return FlipBits(frame, 0, frame.Length);
        }

        byte[] copy = (byte[])frame.Clone();
        int position = 4 + _random.Next(end - 4);
        copy[position] = _random.Next(3) switch
        {
            0 => (byte)(copy[position] + (_random.Next(2) == 0 ? 1 : -1)),
            1 => Interesting[_random.Next(Interesting.Length)],
            _ => (byte)(copy[position] ^ (1 << _random.Next(8))),
        };
        return copy;
    }

    /// <summary>Rewrites a block header: its size by a little or a lot, its type, its last flag.</summary>
    private byte[] LieInBlockHeader(byte[] frame, Block block)
    {
        if (block.Header + 3 > frame.Length)
        {
            return frame;
        }

        byte[] copy = (byte[])frame.Clone();
        int header = copy[block.Header] | (copy[block.Header + 1] << 8) | (copy[block.Header + 2] << 16);
        int size = header >> 3;
        switch (_random.Next(4))
        {
            case 0:
                size += _random.Next(2) == 0 ? 1 : -1;
                break;
            case 1:
                size = _random.Next(1 << 21);
                break;
            case 2:
                header ^= 2 << _random.Next(2); // block type
                break;
            default:
                header ^= 1; // last block
                break;
        }

        header = (header & 7) | ((size & 0x1FFFFF) << 3);
        copy[block.Header] = (byte)header;
        copy[block.Header + 1] = (byte)(header >> 8);
        copy[block.Header + 2] = (byte)(header >> 16);
        return copy;
    }

    private byte[] Splice(byte[] frame)
    {
        byte[] donor = _donors[_random.Next(_donors.Count)];
        if (donor.Length == 0)
        {
            return frame;
        }

        int at = _random.Next(frame.Length);
        int from = _random.Next(donor.Length);
        int count = Math.Min(1 + _random.Next(64), Math.Min(donor.Length - from, frame.Length - at));
        byte[] copy = (byte[])frame.Clone();
        donor.AsSpan(from, count).CopyTo(copy.AsSpan(at));
        return copy;
    }

    private Block? PickCompressed(List<Block> blocks)
    {
        List<Block> compressed = blocks.FindAll(b => b.Type == 2 && b.End > b.Content);
        return compressed.Count == 0 ? null : compressed[_random.Next(compressed.Count)];
    }

    private readonly record struct Block(int Header, int Type, int Content, int End);

    /// <summary>Walks the blocks of the frame as far as its headers allow.</summary>
    private static List<Block> Blocks(byte[] frame, out int headerSize)
    {
        var blocks = new List<Block>();
        headerSize = 0;
        if (frame.Length < 6 || frame[0] != 0x28 || frame[1] != 0xB5 || frame[2] != 0x2F || frame[3] != 0xFD)
        {
            return blocks;
        }

        byte descriptor = frame[4];
        bool single = (descriptor & 0x20) != 0;
        int did = (descriptor & 3) switch { 0 => 0, 1 => 1, 2 => 2, _ => 4 };
        int fcs = (descriptor >> 6) switch { 0 => single ? 1 : 0, 1 => 2, 2 => 4, _ => 8 };
        headerSize = 5 + (single ? 0 : 1) + did + fcs;
        int position = headerSize;
        while (position + 3 <= frame.Length)
        {
            int header = frame[position] | (frame[position + 1] << 8) | (frame[position + 2] << 16);
            int type = (header >> 1) & 3;
            int size = type == 1 ? 1 : header >> 3;
            int end = Math.Min(frame.Length, position + 3 + size);
            blocks.Add(new Block(position, type, position + 3, end));
            position = end;
            if ((header & 1) != 0 || type == 3)
            {
                break;
            }
        }

        return blocks;
    }
}
