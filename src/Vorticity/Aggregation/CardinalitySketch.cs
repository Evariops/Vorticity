using System;
using System.Numerics;

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
/// on its content alone: the exact hashes while they number at most <see cref="ExactLimit"/>, sorted
/// register entries while at most <see cref="SparseLimit"/> registers are set, all the registers past
/// that. The relative standard error is about 0.81 %, <c>1.04 / √(2^14)</c>.
/// </para>
/// <para>
/// O. Ertl, <i>New cardinality estimation algorithms for HyperLogLog sketches</i> (2017), algorithm 6.
/// </para>
/// </remarks>
internal sealed class CardinalitySketch
{
    /// <summary>The register index's bits.</summary>
    internal const int Precision = 14;

    /// <summary>The hashes kept exactly, before registers take over.</summary>
    internal const int ExactLimit = 64;

    /// <summary>The registers kept as sorted entries, before all of them are.</summary>
    internal const int SparseLimit = 1 << (Precision - 3);

    private const int Registers = 1 << Precision;
    private const int MaxRank = 64 - Precision + 1;

    // Exactly: the distinct hashes, sorted; null once registers hold the sketch.
    private ulong[]? _hashes = [];
    private int _exact;

    // Sparsely: the set registers' indices and ranks, sorted by index; null when dense or exact.
    private int[]? _indices;
    private byte[]? _ranks;
    private int _set;

    // Densely: every register's rank.
    private byte[]? _dense;

    /// <summary>The bytes the sketch holds.</summary>
    internal long Bytes =>
        ((long)(_hashes?.Length ?? 0) * sizeof(ulong)) + ((long)(_indices?.Length ?? 0) * (sizeof(int) + 1)) + (_dense?.Length ?? 0);

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
    internal void Merge(CardinalitySketch other)
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

        for (int i = 0; i < other._set; i++)
        {
            Raise(other._indices![i], other._ranks![i]);
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
            for (int i = 0; i < _set; i++)
            {
                counts[_ranks![i]]++;
            }
        }

        return (long)Math.Round(Ertl(counts));
    }

    /// <summary>The sketch's words for a spill or a state: its form, then its hashes or its set registers.</summary>
    internal void Write(Action<long> word)
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
        if (_dense is { } dense)
        {
            int set = 0;
            foreach (byte rank in dense)
            {
                set += rank != 0 ? 1 : 0;
            }

            word(set);
            for (int i = 0; i < dense.Length; i++)
            {
                if (dense[i] != 0)
                {
                    word(((long)i << 8) | dense[i]);
                }
            }

            return;
        }

        word(_set);
        for (int i = 0; i < _set; i++)
        {
            word(((long)_indices![i] << 8) | _ranks![i]);
        }
    }

    /// <summary>A sketch read back from <see cref="Write"/>'s words.</summary>
    internal static CardinalitySketch Read(Func<long> word)
    {
        CardinalitySketch sketch = new CardinalitySketch();
        long form = word();
        long entries = word();
        if (form == 0)
        {
            for (long i = 0; i < entries; i++)
            {
                sketch.Add(unchecked((ulong)word()));
            }

            return sketch;
        }

        sketch.Registered();
        for (long i = 0; i < entries; i++)
        {
            long entry = word();
            sketch.Raise(checked((int)(entry >> 8)), (byte)entry);
        }

        return sketch;
    }

    /// <summary>Adds a register entry read back, the form already chosen by <see cref="BeginRegisters"/>.</summary>
    internal void RaiseEntry(long entry) => Raise(checked((int)(entry >> 8)), (byte)entry);

    /// <summary>Turns a sketch read back into registers, before its entries.</summary>
    internal void BeginRegisters() => Registered();

    /// <summary>Moves the exact hashes into registers, once.</summary>
    private void Registered()
    {
        if (_hashes is not { } hashes)
        {
            return;
        }

        int exact = _exact;
        _hashes = null;
        _exact = 0;
        _indices = new int[16];
        _ranks = new byte[16];
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

        int found = Array.BinarySearch(_indices!, 0, _set, index);
        if (found >= 0)
        {
            if (rank > _ranks![found])
            {
                _ranks[found] = rank;
            }

            return;
        }

        if (_set == SparseLimit)
        {
            Densify();
            Raise(index, rank);
            return;
        }

        if (_set == _indices!.Length)
        {
            Array.Resize(ref _indices, _indices.Length * 2);
            Array.Resize(ref _ranks, _ranks!.Length * 2);
        }

        int insert = ~found;
        Array.Copy(_indices, insert, _indices, insert + 1, _set - insert);
        Array.Copy(_ranks!, insert, _ranks!, insert + 1, _set - insert);
        _indices[insert] = index;
        _ranks![insert] = rank;
        _set++;
    }

    private void Densify()
    {
        byte[] dense = new byte[Registers];
        for (int i = 0; i < _set; i++)
        {
            dense[_indices![i]] = _ranks![i];
        }

        _dense = dense;
        _indices = null;
        _ranks = null;
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
