using System;
using System.Collections.Generic;
using System.Numerics;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Indexes;

/// <summary>
/// Where each value of a column lies, granule by granule (<see cref="IndexKinds.ColumnLocate"/>, spec D, 3): the column's
/// values in the order of their bytes, each with the granules it lies in, or none for a value that lies in so many of them
/// that a filter on it would spare nothing, <em>everywhere</em>. A filter on a value, a set of them or a prefix reads the
/// granules of its values alone, whatever column it is on and wherever the key puts them.
/// </summary>
/// <remarks>
/// Encoded as its version, the file's rows, the rows of a granule and its count of values, then each value: the bytes it
/// shares with the value before it, a count, and the rest, its length first; then its granules, their count shifted past
/// two bits that say how they are given: everywhere, a list of their gaps from the one before, or a map of a bit a granule.
/// A granule is the writer's block, the zone of the zone maps and the place of the sparse index.
/// </remarks>
internal sealed class ColumnLocate : IValueGranules
{
    private const byte Version = 1;

    // How a value's granules are given, in the two low bits of its count.
    private const int Everywhere = 0;
    private const int Listed = 1;
    private const int Mapped = 2;

    private readonly byte[] _bytes;
    private readonly byte[][] _values;
    private readonly int[] _kinds;
    private readonly int[] _counts;
    private readonly int[] _offsets;

    private ColumnLocate(byte[] bytes, long rows, int granuleRows, byte[][] values, int[] kinds, int[] counts, int[] offsets)
    {
        _bytes = bytes;
        Rows = rows;
        GranuleRows = granuleRows;
        _values = values;
        _kinds = kinds;
        _counts = counts;
        _offsets = offsets;
    }

    /// <summary>The file's rows.</summary>
    internal long Rows { get; }

    /// <summary>The rows of a granule.</summary>
    internal int GranuleRows { get; }

    /// <summary>The file's granules.</summary>
    internal int Granules => (int)((Rows + GranuleRows - 1) / GranuleRows);

    /// <summary>The values, in the order of their bytes.</summary>
    internal IReadOnlyList<byte[]> Values => _values;

    /// <summary>The bytes the index takes, which the memo of synopses counts.</summary>
    internal int Bytes => _bytes.Length;

    /// <summary>The place of <paramref name="value"/> among <see cref="Values"/>; -1 when the column holds no such value.</summary>
    internal int Find(ReadOnlySpan<byte> value)
    {
        int low = 0;
        int high = _values.Length - 1;
        while (low <= high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            int order = _values[middle].AsSpan().SequenceCompareTo(value);
            if (order == 0)
            {
                return middle;
            }

            if (order < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        return -1;
    }

    /// <summary>The places of the values that start with <paramref name="prefix"/>: a range of <see cref="Values"/>, ascending.</summary>
    internal (int First, int End) Prefixed(ReadOnlySpan<byte> prefix)
    {
        int first = LowerBound(prefix);
        int end = first;
        while (end < _values.Length && _values[end].AsSpan().StartsWith(prefix))
        {
            end++;
        }

        return (first, end);
    }

    /// <summary>Whether the value at <paramref name="place"/> lies everywhere: no granule spared by it.</summary>
    internal bool IsEverywhere(int place) => _kinds[place] == Everywhere;

    /// <summary>The granules the value at <paramref name="place"/> lies in.</summary>
    internal int GranulesOf(int place) => _counts[place];

    /// <summary>
    /// Sets in <paramref name="granules"/>, a bit a granule, those the value at <paramref name="place"/> lies in: every one
    /// for a value that lies everywhere.
    /// </summary>
    /// <exception cref="VortexFormatException">The value's granules do not decode.</exception>
    internal void Or(int place, Span<ulong> granules)
    {
        int count = Granules;
        switch (_kinds[place])
        {
            case Everywhere:
                Arrays.Decoders.Canonical.BitmapKernels.FillRange(System.Runtime.InteropServices.MemoryMarshal.AsBytes(granules), 0, count, true);
                return;

            case Listed:
            {
                ReadOnlySpan<byte> bytes = _bytes;
                int at = _offsets[place];
                long granule = -1;
                for (int i = 0; i < _counts[place]; i++)
                {
                    granule += (long)Varint(bytes, ref at) + 1;
                    if ((ulong)granule >= (ulong)count)
                    {
                        throw new VortexFormatException($"A value's granule {granule} lies past the {count} of the file.");
                    }

                    granules[(int)(granule >> 6)] |= 1UL << (int)(granule & 63);
                }

                return;
            }

            default:
            {
                ReadOnlySpan<byte> map = _bytes.AsSpan(_offsets[place], (count + 7) / 8);
                Span<byte> into = System.Runtime.InteropServices.MemoryMarshal.AsBytes(granules);
                for (int i = 0; i < map.Length; i++)
                {
                    into[i] |= map[i];
                }

                return;
            }
        }
    }

    /// <inheritdoc/>
    int IValueGranules.Granules => Granules;

    /// <inheritdoc/>
    void IValueGranules.OrValue(ReadOnlySpan<byte> value, Span<ulong> granules)
    {
        if (Find(value) is int place and >= 0)
        {
            Or(place, granules);
        }
    }

    /// <inheritdoc/>
    bool IValueGranules.TryOrPrefix(ReadOnlySpan<byte> prefix, Span<ulong> granules)
    {
        (int first, int end) = Prefixed(prefix);
        for (int place = first; place < end; place++)
        {
            Or(place, granules);
        }

        return true;
    }

    /// <summary>The first place whose value does not come before <paramref name="value"/>.</summary>
    private int LowerBound(ReadOnlySpan<byte> value)
    {
        int low = 0;
        int high = _values.Length;
        while (low < high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            if (_values[middle].AsSpan().SequenceCompareTo(value) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        return low;
    }

    /// <summary>The index <paramref name="bytes"/> hold; null when they are no index this version reads.</summary>
    internal static ColumnLocate? From(byte[] bytes)
    {
        try
        {
            if (bytes.Length == 0 || bytes[0] != Version)
            {
                return null;
            }

            ReadOnlySpan<byte> span = bytes;
            int at = 1;
            long rows = checked((long)Varint(span, ref at));
            int granuleRows = checked((int)Varint(span, ref at));
            int count = checked((int)Varint(span, ref at));
            if (granuleRows <= 0 || rows < 0 || count < 0 || count > bytes.Length)
            {
                return null;
            }

            int granules = (int)((rows + granuleRows - 1) / granuleRows);
            byte[][] values = new byte[count][];
            int[] kinds = new int[count];
            int[] counts = new int[count];
            int[] offsets = new int[count];
            byte[] previous = [];
            for (int v = 0; v < count; v++)
            {
                int shared = checked((int)Varint(span, ref at));
                int length = checked((int)Varint(span, ref at));
                if (shared > previous.Length || length > span.Length - at)
                {
                    return null;
                }

                ReadOnlySpan<byte> rest = span.Slice(at, length);
                at += length;

                byte[] value = new byte[shared + rest.Length];
                previous.AsSpan(0, shared).CopyTo(value);
                rest.CopyTo(value.AsSpan(shared));
                if (v > 0 && previous.AsSpan().SequenceCompareTo(value) >= 0)
                {
                    return null;
                }

                ulong set = Varint(span, ref at);
                int kind = (int)(set & 3);
                int met = checked((int)(set >> 2));
                if (kind > Mapped || met > granules)
                {
                    return null;
                }

                (values[v], kinds[v], counts[v], offsets[v]) = (value, kind, met, at);
                if (kind == Listed)
                {
                    for (int i = 0; i < met; i++)
                    {
                        Varint(span, ref at);
                    }
                }
                else if (kind == Mapped)
                {
                    at = checked(at + ((granules + 7) / 8));
                }

                previous = value;
            }

            return at == span.Length ? new ColumnLocate(bytes, rows, granuleRows, values, kinds, counts, offsets) : null;
        }
        catch (Exception e) when (e is VortexFormatException or OverflowException)
        {
            return null;
        }
    }

    /// <summary>
    /// The bytes of the index of a file of <paramref name="rows"/> rows, granules of <paramref name="granuleRows"/>: the
    /// values <paramref name="values"/>, ascending by their bytes, each with its granules, ascending, from
    /// <paramref name="granules"/> at <paramref name="starts"/>; a value in more than <paramref name="most"/> of them lies
    /// everywhere. Each value's granules are given as the shorter of a list and a map.
    /// </summary>
    internal static byte[] ToBytes(
        long rows, int granuleRows, IReadOnlyList<byte[]> values, ReadOnlySpan<int> starts, ReadOnlySpan<int> granules, int most)
    {
        int count = (int)((rows + granuleRows - 1) / granuleRows);
        int mapBytes = (count + 7) / 8;
        ProtoWriter writer = new ProtoWriter(64 + (values.Count * 8) + (granules.Length * 2));
        try
        {
            writer.WriteRawBytes([Version]);
            writer.WriteVarint((ulong)rows);
            writer.WriteVarint((ulong)granuleRows);
            writer.WriteVarint((ulong)values.Count);
            byte[] previous = [];
            byte[] map = new byte[mapBytes];
            for (int v = 0; v < values.Count; v++)
            {
                byte[] value = values[v];
                int shared = value.AsSpan().CommonPrefixLength(previous);
                writer.WriteVarint((ulong)shared);
                writer.WriteVarint((ulong)(value.Length - shared));
                writer.WriteRawBytes(value.AsSpan(shared));
                previous = value;

                ReadOnlySpan<int> own = granules[starts[v]..starts[v + 1]];
                if (own.Length > most)
                {
                    writer.WriteVarint(((ulong)own.Length << 2) | Everywhere);
                    continue;
                }

                int listBytes = 0;
                for (int i = 0, last = -1; i < own.Length; last = own[i++])
                {
                    listBytes += VarintBytes((uint)(own[i] - last - 1));
                }

                if (listBytes <= mapBytes)
                {
                    writer.WriteVarint(((ulong)own.Length << 2) | Listed);
                    for (int i = 0, last = -1; i < own.Length; last = own[i++])
                    {
                        writer.WriteVarint((ulong)(own[i] - last - 1));
                    }

                    continue;
                }

                writer.WriteVarint(((ulong)own.Length << 2) | Mapped);
                Array.Clear(map);
                foreach (int granule in own)
                {
                    map[granule >> 3] |= (byte)(1 << (granule & 7));
                }

                writer.WriteRawBytes(map);
            }

            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static int VarintBytes(uint value) => Math.Max(1, (38 - BitOperations.LeadingZeroCount(value)) / 7);

    /// <summary>The varint at <paramref name="at"/>, which moves past it.</summary>
    /// <exception cref="VortexFormatException">The bytes end inside it, or it runs past ten bytes.</exception>
    private static ulong Varint(ReadOnlySpan<byte> bytes, ref int at)
    {
        ulong value = 0;
        for (int shift = 0; shift < 70; shift += 7)
        {
            if ((uint)at >= (uint)bytes.Length)
            {
                throw new VortexFormatException("An index of where values lie ends inside a varint.");
            }

            byte next = bytes[at++];
            value |= (ulong)(next & 0x7F) << shift;
            if (next < 0x80)
            {
                return value;
            }
        }

        throw new VortexFormatException("An index of where values lie holds a varint past ten bytes.");
    }
}
