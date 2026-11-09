using System;

namespace Vorticity.Dataset;

/// <summary>
/// The synopses a handle read and parsed, the column sheets, counts and sketches of its objects, by object and region,
/// so that the ranges of one question and the questions after it read and parse each once, within <see cref="Bytes"/>:
/// past it the oldest go first, unless a question asked for them since (<see cref="ClockMemo{TKey, TValue}"/>).
/// </summary>
/// <remarks>
/// An object never changes, so a synopsis never goes stale, and no two regions of an object start at the same offset. The
/// lanes of a question race here; a synopsis two of them read at once is kept once, and a lookup takes no lock.
/// </remarks>
internal sealed class SynopsisMemo
{
    /// <summary>The bytes the memo holds at most: 64 MiB, the sheets of thousands of objects.</summary>
    internal const long Bytes = 64L << 20;

    private readonly ClockMemo<(UInt128 Object, ulong Offset), object> _synopses = new ClockMemo<(UInt128 Object, ulong Offset), object>(Bytes);

    /// <summary>Whether the memo holds the region at <paramref name="offset"/> of object <paramref name="uid"/>, whatever it holds it as.</summary>
    internal bool Holds(UInt128 uid, ulong offset) => _synopses.TryGet((uid, offset), out _);

    /// <summary>The synopsis of the region at <paramref name="offset"/> of object <paramref name="uid"/>, when the memo holds it as one.</summary>
    internal bool TryGet<T>(UInt128 uid, ulong offset, out T? synopsis)
        where T : class
    {
        synopsis = _synopses.TryGet((uid, offset), out object? held) ? held as T : null;
        return synopsis is not null;
    }

    /// <summary>
    /// Keeps <paramref name="synopsis"/>, which holds <paramref name="bytes"/> bytes in memory, as the synopsis of the region
    /// at <paramref name="offset"/> of object <paramref name="uid"/>.
    /// </summary>
    internal void Keep(UInt128 uid, ulong offset, object synopsis, long bytes)
    {
        if (uid == UInt128.Zero || bytes > Bytes / 4)
        {
            return;
        }

        _ = _synopses.Keep((uid, offset), synopsis, bytes, out _);
    }
}
