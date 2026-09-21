using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;

namespace Vorticity.Arrays;

/// <summary>
/// Bitmaps of the arena as 64-bit words: bit <c>i % 64</c> of word <c>i / 64</c> is row <c>i</c>,
/// least significant first, and the bits past the length are zero.
/// </summary>
/// <remarks>
/// A decoded bitmap is used as it is when it already has that shape: no bit offset, a word-aligned
/// address and clear trailing bits. Otherwise it is rebuilt once into the arena and the words are
/// kept on the node until the arena is reset.
/// </remarks>
internal static class ArenaWords
{
    /// <summary>The validity words of <paramref name="node"/>; empty when every row is valid.</summary>
    internal static ReadOnlySpan<ulong> Validity(CanonicalArena arena, int node)
    {
        ref readonly CanonicalRecord record = ref arena.RecordRef(node);
        Validity validity = record.Validity;
        switch (validity.Kind)
        {
            case ValidityKind.NonNullable:
            case ValidityKind.AllValid:
                return default;
            case ValidityKind.AllInvalid:
                return !record.Words.IsEmpty
                    ? record.Words.Cast<ulong>()
                    : Cached(arena, node, record.Length, bits: default, bitOffset: 0, fill: false);
            default:
                if (!record.Words.IsEmpty)
                {
                    return record.Words.Cast<ulong>();
                }

                int length = record.Length;
                ref readonly CanonicalRecord bits = ref arena.RecordRef(validity.CanonicalNodeIndex);
                return Cached(arena, node, length, bits.BufferA.Span, bits.BitOffset, fill: true);
        }
    }

    /// <summary>The values of the bool node <paramref name="node"/> as words.</summary>
    internal static ReadOnlySpan<ulong> Bits(CanonicalArena arena, int node)
    {
        ref readonly CanonicalRecord record = ref arena.RecordRef(node);
        if (record.Kind == CanonicalKind.Constant)
        {
            bool value = record.BufferA.Span[0] != 0;
            if (!record.Words.IsEmpty)
            {
                return record.Words.Cast<ulong>();
            }

            return Cached(arena, node, record.Length, bits: default, bitOffset: 0, fill: value);
        }

        if (!record.Words.IsEmpty)
        {
            return record.Words.Cast<ulong>();
        }

        return Cached(arena, node, record.Length, record.BufferA.Span, record.BitOffset, fill: true);
    }

    /// <summary>The number of null rows of <paramref name="node"/>, counted by population count.</summary>
    internal static int NullCount(CanonicalArena arena, int node)
    {
        ref readonly CanonicalRecord record = ref arena.RecordRef(node);
        switch (record.Validity.Kind)
        {
            case ValidityKind.NonNullable:
            case ValidityKind.AllValid:
                return 0;
            case ValidityKind.AllInvalid:
                return record.Length;
            default:
                ref readonly CanonicalRecord bits = ref arena.RecordRef(record.Validity.CanonicalNodeIndex);
                return record.Length - BitmapKernels.CountSet(bits.BufferA.Span, bits.BitOffset, record.Length);
        }
    }

    /// <summary>Whether row <paramref name="row"/> of <paramref name="node"/> is valid.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static bool IsValid(CanonicalArena arena, int node, int row)
    {
        ref readonly CanonicalRecord record = ref arena.RecordRef(node);
        if ((uint)row >= (uint)record.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(row), row, $"The column has {record.Length} rows.");
        }

        Validity validity = record.Validity;
        if (validity.IsAllValid)
        {
            return true;
        }

        if (validity.Kind == ValidityKind.AllInvalid)
        {
            return false;
        }

        ref readonly CanonicalRecord bits = ref arena.RecordRef(validity.CanonicalNodeIndex);
        int bit = bits.BitOffset + row;
        return ((bits.BufferA.Span[bit >> 3] >> (bit & 7)) & 1) != 0;
    }

    private static ReadOnlySpan<ulong> Cached(CanonicalArena arena, int node, int length, ReadOnlySpan<byte> bits, int bitOffset, bool fill)
    {
        int words = (length + 63) >> 6;
        if (words == 0)
        {
            return default;
        }

        if (bitOffset == 0 && !bits.IsEmpty && bits.Length >= words * 8 && IsWordAligned(bits))
        {
            ReadOnlySpan<ulong> direct = MemoryMarshal.Cast<byte, ulong>(bits[..(words * 8)]);
            int tail = length & 63;
            if (tail == 0 || (direct[^1] >> tail) == 0)
            {
                return direct;
            }
        }

        VortexBuffer buffer = arena.AllocateUninitialized(words * 8, 64, out Span<byte> raw);
        Span<ulong> destination = MemoryMarshal.Cast<byte, ulong>(raw);
        if (bits.IsEmpty)
        {
            destination.Fill(fill ? ulong.MaxValue : 0);
        }
        else
        {
            Shift(bits, bitOffset, length, destination);
        }

        int remainder = length & 63;
        if (remainder != 0)
        {
            destination[^1] &= (1UL << remainder) - 1;
        }

        arena.RecordRefMutable(node).Words = buffer;
        return destination;
    }

    private static void Shift(ReadOnlySpan<byte> bits, int bitOffset, int length, Span<ulong> destination)
    {
        int bytes = (bitOffset + length + 7) >> 3;
        for (int w = 0; w < destination.Length; w++)
        {
            int startBit = bitOffset + (w << 6);
            int startByte = startBit >> 3;
            int shift = startBit & 7;
            ulong word = 0;
            int available = Math.Min(shift == 0 ? 8 : 9, bytes - startByte);
            for (int b = 0; b < available; b++)
            {
                ulong value = bits[startByte + b];
                word |= b == 0 ? value >> shift : value << ((b << 3) - shift);
            }

            destination[w] = word;
        }
    }

    private static unsafe bool IsWordAligned(ReadOnlySpan<byte> bits) =>
        ((nuint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(bits)) & 7) == 0;
}
