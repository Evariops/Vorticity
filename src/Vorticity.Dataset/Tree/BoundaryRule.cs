using System;
using System.IO.Hashing;

namespace Vorticity.Dataset;

/// <summary>Decides where a page ends.</summary>
/// <remarks>
/// A rule is a state machine over one page: <see cref="Reset"/> at every boundary, then
/// <see cref="IsBoundary"/> once per entry, in key order. It must be a pure function of the entries
/// it has been shown since the last reset — no clock, no randomness that is not seeded from the
/// dataset — or the tree stops being a function of its content.
/// </remarks>
internal interface IBoundaryRule
{
    /// <summary>Starts a new page.</summary>
    void Reset();

    /// <summary>
    /// Says whether the page ends after this entry. A prolly boundary looks at the key alone, never
    /// the value, so updating an entry in place moves no boundary.
    /// </summary>
    bool IsBoundary(ReadOnlySpan<byte> key, int entryBytes);

    /// <summary>A rule of the same kind and parameters, in its starting state.</summary>
    IBoundaryRule Fresh();
}

/// <summary>The prolly rule: a hash of the key, normalised by the bytes accumulated.</summary>
internal sealed class ProllyBoundaryRule : IBoundaryRule
{
    /// <summary>No boundary before this many bytes of entries.</summary>
    public const int DefaultMinBytes = 64 << 10;

    /// <summary>The mean page size the rule aims for.</summary>
    public const int DefaultTargetBytes = 128 << 10;

    /// <summary>The page cap, which forces a boundary.</summary>
    public const int DefaultMaxBytes = 256 << 10;

    private readonly ulong _seed;
    private readonly int _min;
    private readonly int _target;
    private readonly int _max;
    private readonly double _hazard;
    private long _bytes;

    /// <summary>A rule with the dataset's seed, recorded in every commit header, and the default sizes.</summary>
    public ProllyBoundaryRule(ulong seed)
        : this(seed, DefaultMinBytes, DefaultTargetBytes, DefaultMaxBytes)
    {
    }

    /// <summary>A rule with its own floor and cap; the target is placed between them.</summary>
    public ProllyBoundaryRule(ulong seed, int minBytes, int maxBytes)
        : this(seed, minBytes, minBytes + ((maxBytes - minBytes) / 3), maxBytes)
    {
    }

    /// <summary>A rule with all three sizes: the floor, the mean aimed for, and the cap.</summary>
    public ProllyBoundaryRule(ulong seed, int minBytes, int targetBytes, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetBytes, minBytes + 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, targetBytes + 1);
        _seed = seed;
        _min = minBytes;
        _target = targetBytes;
        _max = maxBytes;

        // For a hazard proportional to the room left, the mean extent past the floor is
        // `(max − min) / (k + 1)`; this is the k that puts that mean on the target.
        _hazard = ((double)(maxBytes - minBytes) / (targetBytes - minBytes)) - 1.0;
    }

    /// <summary>The seed every boundary of this tree is decided under.</summary>
    public ulong Seed => _seed;

    /// <summary>The floor.</summary>
    public int MinBytes => _min;

    /// <summary>The mean page size the hazard is solved for.</summary>
    public int TargetBytes => _target;

    /// <summary>The cap.</summary>
    public int MaxBytes => _max;

    /// <inheritdoc/>
    public void Reset() => _bytes = 0;

    /// <inheritdoc/>
    public bool IsBoundary(ReadOnlySpan<byte> key, int entryBytes)
    {
        _bytes += entryBytes;
        if (_bytes >= _max)
        {
            return true;
        }

        if (_bytes < _min)
        {
            return false;
        }

        // The hazard rises as the room left shrinks; the constant puts the mean on the target.
        double probability = _hazard * entryBytes / (_max - _bytes);
        if (probability >= 1.0)
        {
            return true;
        }

        ulong hash = XxHash3.HashToUInt64(key, (long)_seed);
        return hash < (ulong)(probability * ulong.MaxValue);
    }

    /// <inheritdoc/>
    public IBoundaryRule Fresh() => new ProllyBoundaryRule(_seed, _min, _target, _max);
}

/// <summary>The B+tree fill rule, behind the same seam so both rules can be compared.</summary>
/// <remarks>
/// A page ends when it is full. Deterministic, no hash, no seed — and therefore no history
/// independence: the same key set reached by two orders of operations gives two shapes.
/// </remarks>
internal sealed class FillBoundaryRule : IBoundaryRule
{
    private readonly int _fill;
    private long _bytes;

    /// <summary>A rule that fills each page to the given size.</summary>
    public FillBoundaryRule(int fillBytes = ProllyBoundaryRule.DefaultTargetBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(fillBytes);
        _fill = fillBytes;
    }

    /// <summary>The size a page is filled to.</summary>
    public int FillBytes => _fill;

    /// <inheritdoc/>
    public void Reset() => _bytes = 0;

    /// <inheritdoc/>
    public bool IsBoundary(ReadOnlySpan<byte> key, int entryBytes)
    {
        _bytes += entryBytes;
        return _bytes >= _fill;
    }

    /// <inheritdoc/>
    public IBoundaryRule Fresh() => new FillBoundaryRule(_fill);
}
