using System;

namespace Vorticity.Dataset;

/// <summary>
/// The synopses a handle read and parsed, the column sheets, counts and sketches of its objects, by object and region,
/// so that the ranges of one question and the questions after it read and parse each once. It has no budget of its own:
/// the registry of kept memory takes back what it holds above <see cref="FloorBytes"/>, the oldest first unless a
/// question asked for them since (<see cref="ClockMemo{TKey, TValue}"/>).
/// </summary>
/// <remarks>
/// An object never changes, so a synopsis never goes stale, and no two regions of an object start at the same offset. The
/// lanes of a question race here; a synopsis two of them read at once is kept once, and a lookup takes no lock.
/// </remarks>
internal sealed class SynopsisMemo
{
    /// <summary>
    /// The bytes the memo keeps whatever the registry asks: with the handle's other floors, what forty handles read was all
    /// kept after two turns of the slow timer (2026-11-16).
    /// </summary>
    internal const long FloorBytes = 4L << 20;

    /// <summary>The largest synopsis the memo keeps, 16 MiB: one past it is parsed again at each asking rather than held.</summary>
    private const long LargestKept = 16L << 20;

    private readonly ClockMemo<(UInt128 Object, ulong Offset), object> _synopses = ClockMemo<(UInt128 Object, ulong Offset), object>.Kept(FloorBytes);

    /// <summary>The bytes the memo holds now.</summary>
    internal long Held => _synopses.Held;

    /// <summary>Lets go of up to <paramref name="bytes"/> above the floor, only what no question read since the timer's turn before when <paramref name="stale"/>.</summary>
    internal long LetGo(long bytes, bool stale) => _synopses.LetGo(bytes, stale);

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
        if (uid == UInt128.Zero || bytes > LargestKept)
        {
            return;
        }

        _ = _synopses.Keep((uid, offset), synopsis, bytes, out _);
    }
}
