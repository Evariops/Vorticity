using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Numerics;
using Vorticity.Serialization;

namespace Vorticity.Aggregating;

/// <summary>
/// An approximate count of distinct values: a HyperLogLog of <c>2^14</c> registers over a fixed-seed
/// XXH3-64 of each value, an exact set of hashes while it holds few, and its count estimated by Ertl's
/// improved estimator, which needs no table of bias corrections and is the same bits on every machine.
/// </summary>
/// <remarks>
/// <para>
/// A register keeps the most leading zeros plus one that a hash of its index showed, and a merge keeps
/// each register's maximum, so a sketch merged in any order holds the same registers. Its form depends
/// on its content alone: the exact hashes while they number at most <see cref="ExactLimit"/>, the set
/// registers in a small open-addressing table while at most <see cref="SparseLimit"/> are set, all the
/// registers past that, 16 KiB. The relative standard error is about 0.81 %, <c>1.04 / √(2^14)</c>.
/// </para>
/// <para>
/// O. Ertl, <i>New cardinality estimation algorithms for HyperLogLog sketches</i> (2017), algorithm 6.
/// </para>
/// </remarks>
internal sealed class CardinalitySketch : IGroupSketch<CardinalitySketch>
{
    /// <summary>The register index's bits.</summary>
    internal const int Precision = 14;

    /// <summary>The hashes kept exactly, before registers take over.</summary>
    internal const int ExactLimit = 64;

    /// <summary>The registers kept in the table, at half its load at most, before all of them are: 8 KiB of table at most.</summary>
    internal const int SparseLimit = 1 << (Precision - 4);

    private const int Registers = 1 << Precision;
    private const int MaxRank = 64 - Precision + 1;
    private const int FirstTable = 128;

    // Exactly: the distinct hashes, sorted; null once registers hold the sketch.
    private ulong[]? _hashes = [];
    private int _exact;

    // Sparsely: the set registers, each ((index + 1) << 8) | rank, in an open-addressing table; 0 is empty.
    private int[]? _table;
    private int _set;

    // Densely: every register's rank.
    private byte[]? _dense;

    /// <summary>The bytes the sketch holds.</summary>
    public long Bytes =>
        ((long)(_hashes?.Length ?? 0) * sizeof(ulong)) + ((long)(_table?.Length ?? 0) * sizeof(int)) + (_dense?.Length ?? 0);

    /// <summary>Whether the sketch still counts exactly.</summary>
    internal bool IsExact => _hashes is not null;

    /// <summary>Adds the value whose hash is <paramref name="hash"/>.</summary>
    internal void Add(ulong hash)
    {
        if (_hashes is { } hashes)
        {
            int found = Array.BinarySearch(hashes, 0, _exact, hash);
            if (found >= 0)
            {
                return;
            }

            if (_exact == ExactLimit)
            {
                Registered();
                Set(hash);
                return;
            }

            if (_exact == hashes.Length)
            {
                Array.Resize(ref _hashes, Math.Max(4, hashes.Length * 2));
                hashes = _hashes;
            }

            int insert = ~found;
            Array.Copy(hashes, insert, hashes, insert + 1, _exact - insert);
            hashes[insert] = hash;
            _exact++;
            return;
        }

        Set(hash);
    }

    /// <summary>Merges <paramref name="other"/>: the union of their hashes, or each register's maximum.</summary>
    public void Merge(CardinalitySketch other)
    {
        if (other._hashes is { } theirs)
        {
            for (int i = 0; i < other._exact; i++)
            {
                Add(theirs[i]);
            }

            return;
        }

        Registered();
        if (other._dense is { } dense)
        {
            for (int i = 0; i < dense.Length; i++)
            {
                if (dense[i] != 0)
                {
                    Raise(i, dense[i]);
                }
            }

            return;
        }

        foreach (int entry in other._table!)
        {
            if (entry != 0)
            {
                Raise((entry >> 8) - 1, (byte)entry);
            }
        }
    }

    /// <summary>The estimate, rounded; the exact count while the hashes are kept.</summary>
    internal long Estimate()
    {
        if (_hashes is not null)
        {
            return _exact;
        }

        // The registers by rank, then the estimator.
        Span<int> counts = stackalloc int[MaxRank + 1];
        if (_dense is { } dense)
        {
            foreach (byte rank in dense)
            {
                counts[rank]++;
            }
        }
        else
        {
            counts[0] = Registers - _set;
            foreach (int entry in _table!)
            {
                if (entry != 0)
                {
                    counts[(byte)entry]++;
                }
            }
        }

        return (long)Math.Round(Ertl(counts));
    }

    /// <summary>
    /// The sketch's words for a spill or a state: its form, then its hashes ascending, or its set
    /// registers ascending by index, each <c>(index &lt;&lt; 8) | rank</c>: the same words for the same
    /// content, whichever form held it.
    /// </summary>
    public void Write(Action<long> word)
    {
        if (_hashes is { } hashes)
        {
            word(0);
            word(_exact);
            for (int i = 0; i < _exact; i++)
            {
                word(unchecked((long)hashes[i]));
            }

            return;
        }

        word(1);
        List<long> entries = [];
        if (_dense is { } dense)
        {
            for (int i = 0; i < dense.Length; i++)
            {
                if (dense[i] != 0)
                {
                    entries.Add(((long)i << 8) | dense[i]);
                }
            }
        }
        else
        {
            foreach (int entry in _table!)
            {
                if (entry != 0)
                {
                    entries.Add(((long)((entry >> 8) - 1) << 8) | (byte)entry);
                }
            }

            entries.Sort();
        }

        word(entries.Count);
        foreach (long entry in entries)
        {
            word(entry);
        }
    }

    /// <summary>A sketch read back from <see cref="Write"/>'s words, a spill's or a test's.</summary>
    internal static CardinalitySketch Read<TWords>(ref TWords words)
        where TWords : IWordSource, allows ref struct
    {
        CardinalitySketch sketch = new CardinalitySketch();
        long form = words.NextWord();
        long entries = words.NextWord();
        if (form == 0)
        {
            for (long i = 0; i < entries; i++)
            {
                sketch.Add(unchecked((ulong)words.NextWord()));
            }

            return sketch;
        }

        sketch.Registered();
        for (long i = 0; i < entries; i++)
        {
            sketch.RaiseEntry(words.NextWord());
        }

        return sketch;
    }

    /// <summary>A sketch read back from the words <paramref name="word"/> hands out one at a time.</summary>
    internal static CardinalitySketch Read(Func<long> word)
    {
        WordsOf words = new WordsOf(word);
        return Read(ref words);
    }

    /// <summary>
    /// The sketch as a table of sketches holds it, the same bytes for the same content whichever form held it: a byte of
    /// its form, then its hashes ascending, 8 bytes each, behind their count; or its set registers ascending by index,
    /// each the gap from the one before and its rank in a byte, behind their count, two bytes a register where they lie
    /// close; or, past half of them set, every register's rank, a byte each.
    /// </summary>
    internal void Encode(IBufferWriter<byte> into)
    {
        if (_hashes is { } hashes)
        {
            Span<byte> exact = into.GetSpan(1 + Varint.MaxLength64 + (_exact * sizeof(ulong)));
            exact[0] = ExactForm;
            int at = 1 + Varint.Write(exact[1..], (ulong)_exact);
            for (int i = 0; i < _exact; i++, at += sizeof(ulong))
            {
                BinaryPrimitives.WriteUInt64LittleEndian(exact[at..], hashes[i]);
            }

            into.Advance(at);
            return;
        }

        int set = _dense is { } counted ? Registers - counted.AsSpan().Count((byte)0) : _set;
        if (set > Registers / 2)
        {
            Span<byte> all = into.GetSpan(1 + Registers);
            all[0] = DenseForm;
            Ranks(all.Slice(1, Registers));
            into.Advance(1 + Registers);
            return;
        }

        // The set registers in the order of their indexes, through a rented array of every rank.
        byte[] rented = ArrayPool<byte>.Shared.Rent(Registers);
        try
        {
            Span<byte> ranks = rented.AsSpan(0, Registers);
            Ranks(ranks);
            Span<byte> sparse = into.GetSpan(1 + Varint.MaxLength64 + (set * (Varint.MaxLength32 + 1)));
            sparse[0] = SparseForm;
            int at = 1 + Varint.Write(sparse[1..], (ulong)set);
            int previous = 0;
            for (int index = 0; index < Registers; index++)
            {
                if (ranks[index] != 0)
                {
                    at += Varint.Write(sparse[at..], (ulong)(index - previous));
                    sparse[at++] = ranks[index];
                    previous = index;
                }
            }

            into.Advance(at);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// Merges the sketch <see cref="Encode"/> wrote, as <see cref="Merge"/> merges a sketch: the union of their hashes,
    /// or each register's maximum, so that the sketch holds what adding the values of both would have made it hold.
    /// </summary>
    /// <exception cref="VortexFormatException">The bytes do not hold a sketch.</exception>
    public void MergeEncoded(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
        {
            throw new VortexFormatException("A distinct sketch of no bytes.");
        }

        int at = 1;
        switch (bytes[0])
        {
            case ExactForm:
            {
                ulong count = Varint.Read64<Errors>(bytes, ref at);
                if (count > ExactLimit || (ulong)(bytes.Length - at) != count * sizeof(ulong))
                {
                    throw new VortexFormatException($"A distinct sketch says {count} hashes in {bytes.Length - at} bytes.");
                }

                for (; at < bytes.Length; at += sizeof(ulong))
                {
                    Add(BinaryPrimitives.ReadUInt64LittleEndian(bytes[at..]));
                }

                return;
            }

            case SparseForm:
            {
                ulong count = Varint.Read64<Errors>(bytes, ref at);
                if (count > Registers)
                {
                    throw new VortexFormatException($"A distinct sketch says {count} registers set of {Registers}.");
                }

                Registered();
                long index = 0;
                for (ulong e = 0; e < count; e++)
                {
                    index += (long)Varint.Read64<Errors>(bytes, ref at);
                    if (index >= Registers || at >= bytes.Length || bytes[at] is 0 or > MaxRank)
                    {
                        throw new VortexFormatException($"A distinct sketch's register {index} is out of the sketch or of its ranks.");
                    }

                    Raise((int)index, bytes[at++]);
                }

                if (at != bytes.Length)
                {
                    throw new VortexFormatException($"A distinct sketch holds {bytes.Length - at} bytes past its registers.");
                }

                return;
            }

            case DenseForm:
            {
                if (bytes.Length != 1 + Registers)
                {
                    throw new VortexFormatException($"A dense distinct sketch of {bytes.Length - 1} bytes, not {Registers}.");
                }

                Registered();
                for (int index = 0; index < Registers; index++)
                {
                    byte rank = bytes[1 + index];
                    if (rank > MaxRank)
                    {
                        throw new VortexFormatException($"A distinct sketch's register {index} has rank {rank}, past {MaxRank}.");
                    }

                    if (rank != 0)
                    {
                        Raise(index, rank);
                    }
                }

                return;
            }

            default:
                throw new VortexFormatException($"A distinct sketch of form {bytes[0]}.");
        }
    }

    /// <summary>Takes the sketch back to no value, its array of hashes kept for the next.</summary>
    internal void Clear()
    {
        _hashes ??= [];
        _exact = 0;
        _table = null;
        _set = 0;
        _dense = null;
    }

    /// <summary>Every register's rank into <paramref name="ranks"/>, of <see cref="Registers"/> bytes, the empty ones zero.</summary>
    private void Ranks(Span<byte> ranks)
    {
        if (_dense is { } dense)
        {
            dense.CopyTo(ranks);
            return;
        }

        ranks.Clear();
        foreach (int entry in _table!)
        {
            if (entry != 0)
            {
                ranks[(entry >> 8) - 1] = (byte)entry;
            }
        }
    }

    private const byte ExactForm = 0;
    private const byte SparseForm = 1;
    private const byte DenseForm = 2;

    /// <summary>A varint of an encoded sketch cut short or overlong.</summary>
    private readonly struct Errors : IVarintErrors
    {
        public static ulong Truncated(int position) => throw new VortexFormatException($"A distinct sketch ends inside a varint at {position}.");

        public static ulong Malformed(int position, byte value) => throw new VortexFormatException($"A distinct sketch holds a varint too wide at {position}.");
    }

    /// <summary>Adds a register entry read back, <c>(index &lt;&lt; 8) | rank</c>, after <see cref="Registered"/>.</summary>
    internal void RaiseEntry(long entry) => Raise(checked((int)(entry >> 8)), (byte)entry);

    /// <summary>Moves the exact hashes into registers, once: a sketch read back in registers, before its entries.</summary>
    private void Registered()
    {
        if (_hashes is not { } hashes)
        {
            return;
        }

        int exact = _exact;
        _hashes = null;
        _exact = 0;
        _table = new int[FirstTable];
        for (int i = 0; i < exact; i++)
        {
            Set(hashes[i]);
        }
    }

    private void Set(ulong hash)
    {
        int index = (int)(hash >> (64 - Precision));
        ulong rest = hash << Precision;
        int rank = Math.Min(BitOperations.LeadingZeroCount(rest) + 1, MaxRank);
        Raise(index, (byte)rank);
    }

    private void Raise(int index, byte rank)
    {
        if (_dense is { } dense)
        {
            if (rank > dense[index])
            {
                dense[index] = rank;
            }

            return;
        }

        int[] table = _table!;
        int mask = table.Length - 1;
        int key = index + 1;
        for (int slot = Slot(index, table.Length); ; slot = (slot + 1) & mask)
        {
            int entry = table[slot];
            if (entry == 0)
            {
                if (_set == SparseLimit)
                {
                    Densify();
                    Raise(index, rank);
                    return;
                }

                table[slot] = (key << 8) | rank;
                _set++;
                if (_set * 2 > table.Length)
                {
                    Grow();
                }

                return;
            }

            if (entry >> 8 == key)
            {
                if (rank > (byte)entry)
                {
                    table[slot] = (key << 8) | rank;
                }

                return;
            }
        }
    }

    /// <summary>The table slot an index starts its probe at: a multiplicative hash of the index's bits.</summary>
    private static int Slot(int index, int length) => (int)(((uint)index * 0x9E3779B1u) >> (32 - BitOperations.Log2((uint)length)));

    private void Grow()
    {
        int[] old = _table!;
        int[] table = new int[old.Length * 2];
        int mask = table.Length - 1;
        foreach (int entry in old)
        {
            if (entry == 0)
            {
                continue;
            }

            int slot = Slot((entry >> 8) - 1, table.Length);
            while (table[slot] != 0)
            {
                slot = (slot + 1) & mask;
            }

            table[slot] = entry;
        }

        _table = table;
    }

    private void Densify()
    {
        byte[] dense = new byte[Registers];
        foreach (int entry in _table!)
        {
            if (entry != 0)
            {
                dense[(entry >> 8) - 1] = (byte)entry;
            }
        }

        _dense = dense;
        _table = null;
        _set = 0;
    }

    /// <summary>Ertl's improved estimator over the registers counted by rank, <paramref name="counts"/>[0] the empty ones.</summary>
    private static double Ertl(ReadOnlySpan<int> counts)
    {
        const double m = Registers;
        const int q = 64 - Precision;
        double z = m * Tau(1 - (counts[q + 1] / m));
        for (int k = q; k >= 1; k--)
        {
            z = 0.5 * (z + counts[k]);
        }

        z += m * Sigma(counts[0] / m);
        return AlphaInfinity * m * m / z;
    }

    /// <summary>1 / (2 ln 2), written out: a platform's logarithm need not round alike, a literal does.</summary>
    private const double AlphaInfinity = 0.7213475204444817;

    private static double Sigma(double x)
    {
        if (x == 1)
        {
            return double.PositiveInfinity;
        }

        double y = 1;
        double z = x;
        while (true)
        {
            x *= x;
            double previous = z;
            z += x * y;
            y += y;
            if (z == previous)
            {
                return z;
            }
        }
    }

    private static double Tau(double x)
    {
        if (x == 0 || x == 1)
        {
            return 0;
        }

        double y = 1;
        double z = 1 - x;
        while (true)
        {
            x = Math.Sqrt(x);
            double previous = z;
            y *= 0.5;
            z -= (1 - x) * (1 - x) * y;
            if (z == previous)
            {
                return z / 3;
            }
        }
    }
}
