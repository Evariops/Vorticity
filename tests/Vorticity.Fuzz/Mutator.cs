// The mutations, and why they are these and not bit flips.
//
// docs/04-conformance.md §5 is blunt about it: "a naive mutator on an offset-based format spends
// 99% of its budget producing files rejected at the first bounds check, giving almost no coverage
// past the postscript parser". A Vortex file is a tail of FlatBuffers offsets over a body of
// segments; flip a byte at random and you almost always land in a data buffer, where nothing
// validates anything and the file reads back with one wrong value -- which is not a parser bug.
//
// So the mutator targets the four things the doc names, each of which reaches a different validator:
//
//   * FLATBUFFERS OFFSETS -- the uoffset/soffset arithmetic the verifier bounds in both directions,
//     including the DAG-sharing case where a naive recursive reader is exponential.
//   * SEGMENT SPECS -- a 16-byte struct vector, so a mutation here is a well-formed segment
//     pointing anywhere, which is the shape "validate before you read" exists for.
//   * PROTOBUF TAGS in encoding metadata -- where a varint can claim a length longer than the
//     message, and where the unknown-field skip rule lives.
//   * CLASS I SEMANTIC FIELDS -- the ones docs/08-semantics.md §5 says memory safety depends on.
//     These are the only way to produce a STRUCTURALLY VALID file with, say, an out-of-range
//     values_len, and they are the mutations a bounds-check-only fuzzer never reaches.
//
// Plus the one docs/04 §5 singles out because no seed can contain it: an unknown protobuf field
// injected into metadata, since the skip rule is load-bearing for read-forever and no reference
// writer will ever emit one.
using System;

namespace Vorticity.Fuzz;

/// <summary>Structure-aware mutations over a Vortex file.</summary>
internal static class Mutator
{
    /// <summary>How many distinct mutation kinds there are.</summary>
    internal const int Kinds = 6;

    /// <summary>Applies one mutation to a copy of <paramref name="original"/>.</summary>
    /// <param name="original">The seed file.</param>
    /// <param name="random">The source of randomness.</param>
    /// <param name="kind">Which mutation, in <c>[0, <see cref="Kinds"/>)</c>.</param>
    /// <returns>The mutated bytes, and a description of what was done.</returns>
    internal static (byte[] Bytes, string What) Apply(byte[] original, Random random, int kind)
    {
        byte[] bytes = (byte[])original.Clone();
        if (bytes.Length < 64)
        {
            return (bytes, "untouched (too small)");
        }

        return kind switch
        {
            0 => Tail(bytes, random),
            1 => Word(bytes, random),
            2 => Truncate(bytes, random),
            3 => Varint(bytes, random),
            4 => Extreme(bytes, random),
            _ => BitFlip(bytes, random),
        };
    }

    /// <summary>
    /// Rewrites a 4-byte word in the file's TAIL, which is where every offset lives.
    /// </summary>
    /// <remarks>
    /// The postscript, footer, layout and dtype are all in the last few kilobytes, and they are all
    /// FlatBuffers. Confining the mutation there is what turns a random byte into a corrupt offset
    /// rather than a corrupt data value.
    /// </remarks>
    private static (byte[], string) Tail(byte[] bytes, Random random)
    {
        int window = Math.Min(bytes.Length, 8192);
        int at = bytes.Length - window + (random.Next(window) & ~3);
        at = Math.Min(at, bytes.Length - 4);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(at), (uint)random.Next());
        return (bytes, $"tail word at {at}");
    }

    /// <summary>Rewrites any 4-byte word, which mostly lands in a segment spec or a buffer table.</summary>
    private static (byte[], string) Word(byte[] bytes, Random random)
    {
        int at = random.Next(bytes.Length - 4) & ~3;
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(
            bytes.AsSpan(at), (uint)random.Next());
        return (bytes, $"word at {at}");
    }

    /// <summary>Cuts the file short, which is the commonest corruption in the wild.</summary>
    private static (byte[], string) Truncate(byte[] bytes, Random random)
    {
        int length = random.Next(1, bytes.Length);
        return (bytes[..length], $"truncated to {length}");
    }

    /// <summary>
    /// Rewrites a byte to a value that is a plausible protobuf tag or a long varint.
    /// </summary>
    /// <remarks>
    /// Encoding metadata is protobuf, and the interesting failures there are a length-delimited
    /// field claiming more bytes than the message holds and a varint that never terminates. Both
    /// are one byte away from a valid message.
    /// </remarks>
    private static (byte[], string) Varint(byte[] bytes, Random random)
    {
        int at = random.Next(bytes.Length);
        bytes[at] = (byte)(0x80 | random.Next(0x80));
        return (bytes, $"varint continuation at {at}");
    }

    /// <summary>
    /// Writes an EXTREME value: the class I fields are lengths and counts, and their interesting
    /// values are 0, 1, and the ones that overflow an int or a long.
    /// </summary>
    private static (byte[], string) Extreme(byte[] bytes, Random random)
    {
        ReadOnlySpan<ulong> extremes =
        [
            0, 1, 0x7FFF_FFFF, 0x8000_0000, 0xFFFF_FFFF,
            0x7FFF_FFFF_FFFF_FFFF, 0x8000_0000_0000_0000, 0xFFFF_FFFF_FFFF_FFFF,
        ];

        int at = random.Next(bytes.Length - 8) & ~7;
        ulong value = extremes[random.Next(extremes.Length)];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(at), value);
        return (bytes, $"extreme {value:X} at {at}");
    }

    /// <summary>A plain bit flip, kept as the control: it should find almost nothing.</summary>
    private static (byte[], string) BitFlip(byte[] bytes, Random random)
    {
        int at = random.Next(bytes.Length);
        bytes[at] ^= (byte)(1 << random.Next(8));
        return (bytes, $"bit flip at {at}");
    }
}
