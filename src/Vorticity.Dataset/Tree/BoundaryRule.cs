// Where a page ends - docs/13-dataset.md §4.1, "the boundary rule", and §13.J, which decided the
// prolly tree "with the rule behind a seam so that the B+tree fill rule is measured on the same
// bench, and reversing the decision costs a rebuild and nothing else".
//
// THIS FILE IS THAT SEAM, and it is four lines of interface for a reason: §4.1 says the two
// candidate trees "are one algorithm ... Only the rule that places page boundaries differs, a fill
// factor for the B+tree, a hash of the content for the prolly tree, and from that rule alone follow
// history independence, deduplication across writers and the test oracle." Everything else in the
// tree is written once and measured under both rules.
//
// THE PROLLY RULE, term by term from §4.1:
//   * "from the XXH3-64 of the entry's key alone, seeded with a 16-byte random seed the dataset
//     draws once" -- the key, never the value, so an indexer that updates an object's descriptor
//     rewrites exactly `depth` pages and moves no boundary;
//   * "no boundary before 64 KiB of entries since the previous one" -- the floor;
//   * "a probability that rises with the bytes accumulated so that the mean page is about 128 KiB"
//     -- the hazard rises as the cap approaches, and the constant that makes the MEAN land on the
//     target is derived rather than tuned (below);
//   * "a forced boundary at 256 KiB, the page cap" -- the ceiling, which makes §9.2's byte bound
//     deterministic.
//
// THE CURVE, AND WHY IT IS THIS ONE. A first attempt made the per-entry probability rise linearly
// from the floor to the cap; it cut far too early -- a page holds hundreds of entries, so a
// probability that is "small" per entry is a near-certainty per kilobyte, and the bench measured a
// mean of 71 KiB against a target of 128. The family that puts the mean where it is asked is a
// hazard proportional to the room left: `p = k · entryBytes / (max − bytes)`, whose survival is
// `((max − b) / (max − min))^k` and whose mean extent past the floor is `(max − min) / (k + 1)`.
// Setting that to `target − min` gives `k = (max − min) / (target − min) − 1`, which is 2 at the
// spec's 64 / 128 / 256 KiB. The mean is then the target BY CONSTRUCTION, and the bench checks it.
//
// What matters more than the curve: the rule is a PURE FUNCTION of the entries since the last
// boundary, because that is what makes the tree a pure function of the key set (§4.1) and the
// oracles of §14 possible at all.
using System;
using System.IO.Hashing;

namespace Vorticity.Dataset;

/// <summary>Decides where a page ends.</summary>
/// <remarks>
/// A rule is a state machine over one page: <see cref="Reset"/> at every boundary, then
/// <see cref="IsBoundary"/> once per entry, in key order. It must be a pure function of the entries
/// it has been shown since the last reset — no clock, no counter of its own lifetime, no
/// randomness that is not seeded from the dataset — or the tree stops being a function of its
/// content and the oracles of §14 stop meaning anything.
/// </remarks>
public interface IBoundaryRule
{
    /// <summary>Starts a new page.</summary>
    void Reset();

    /// <summary>Takes one entry and says whether the page ends after it.</summary>
    /// <param name="key">The entry's key, the only thing a prolly boundary looks at.</param>
    /// <param name="entryBytes">What the entry adds to the page.</param>
    /// <returns>Whether this entry is the page's last.</returns>
    bool IsBoundary(ReadOnlySpan<byte> key, int entryBytes);

    /// <summary>A rule of the same kind and parameters, for a second pass over the same level.</summary>
    /// <returns>A rule in its starting state.</returns>
    IBoundaryRule Fresh();
}

/// <summary>The prolly rule of §4.1: a hash of the key, normalised by the bytes accumulated.</summary>
public sealed class ProllyBoundaryRule : IBoundaryRule
{
    /// <summary>No boundary before this many bytes of entries (§4.1).</summary>
    public const int DefaultMinBytes = 64 << 10;

    /// <summary>The mean page size the rule aims for (§4.1).</summary>
    public const int DefaultTargetBytes = 128 << 10;

    /// <summary>The page cap, which forces a boundary (§4.1, §9.2).</summary>
    public const int DefaultMaxBytes = 256 << 10;

    private readonly ulong _seed;
    private readonly int _min;
    private readonly int _target;
    private readonly int _max;
    private readonly double _hazard;
    private long _bytes;

    /// <summary>A rule with the dataset's seed and the default sizes.</summary>
    /// <param name="seed">The dataset's seed, recorded in every commit header (§4.1).</param>
    public ProllyBoundaryRule(ulong seed)
        : this(seed, DefaultMinBytes, DefaultTargetBytes, DefaultMaxBytes)
    {
    }

    /// <summary>A rule with the dataset's seed and its own sizes.</summary>
    /// <param name="seed">The dataset's seed.</param>
    /// <param name="minBytes">No boundary before this many bytes.</param>
    /// <param name="maxBytes">A boundary at this many, whatever the hash says.</param>
    /// <exception cref="ArgumentOutOfRangeException">The sizes are not a usable range.</exception>
    /// <remarks>The target is placed halfway, as the default 64 / 128 / 256 KiB has it.</remarks>
    public ProllyBoundaryRule(ulong seed, int minBytes, int maxBytes)
        : this(seed, minBytes, minBytes + ((maxBytes - minBytes) / 3), maxBytes)
    {
    }

    /// <summary>A rule with the dataset's seed and all three sizes.</summary>
    /// <param name="seed">The dataset's seed.</param>
    /// <param name="minBytes">No boundary before this many bytes.</param>
    /// <param name="targetBytes">The mean page size the hazard is solved for.</param>
    /// <param name="maxBytes">A boundary at this many, whatever the hash says.</param>
    /// <exception cref="ArgumentOutOfRangeException">The sizes are not a usable range.</exception>
    public ProllyBoundaryRule(ulong seed, int minBytes, int targetBytes, int maxBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minBytes);
        ArgumentOutOfRangeException.ThrowIfLessThan(targetBytes, minBytes + 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(maxBytes, targetBytes + 1);
        _seed = seed;
        _min = minBytes;
        _target = targetBytes;
        _max = maxBytes;

        // k such that the mean extent past the floor is `target − min`; see the file's comment.
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

/// <summary>The B+tree fill rule of §13.J, behind the same seam so the bench can measure both.</summary>
/// <remarks>
/// A page ends when it is full. Deterministic, no hash, no seed — and therefore no history
/// independence: the same key set reached by two orders of operations gives two shapes, which is
/// exactly what §13.J's table says and what the oracle of §14 asserts only for the prolly rule.
/// </remarks>
public sealed class FillBoundaryRule : IBoundaryRule
{
    private readonly int _fill;
    private long _bytes;

    /// <summary>A rule that fills to <paramref name="fillBytes"/>.</summary>
    /// <param name="fillBytes">The page size to fill to.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="fillBytes"/> is not positive.</exception>
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
