using System;
using System.Collections.Generic;

namespace Vorticity.Dataset;

/// <summary>
/// The synopses a handle read and parsed, the column sheets, counts and sketches of its objects, by object and region,
/// so that the ranges of one question and the questions after it read and parse each once: the oldest go first once
/// they hold more than <see cref="Bytes"/>.
/// </summary>
/// <remarks>
/// An object never changes, so a synopsis never goes stale, and no two regions of an object start at the same offset. The
/// lanes of a question race here; a synopsis two of them read at once is kept once.
/// </remarks>
internal sealed class SynopsisMemo
{
    /// <summary>The bytes the memo holds at most: 64 MiB, the sheets of thousands of objects.</summary>
    internal const long Bytes = 64L << 20;

    private readonly Dictionary<(UInt128 Object, ulong Offset), object> _synopses = [];
    private readonly Queue<(UInt128 Object, ulong Offset, long Bytes)> _order = new();
    private readonly System.Threading.Lock _gate = new();
    private long _held;

    /// <summary>The synopsis of the region at <paramref name="offset"/> of object <paramref name="uid"/>, when the memo holds it.</summary>
    internal bool TryGet<T>(UInt128 uid, ulong offset, out T? synopsis)
        where T : class
    {
        lock (_gate)
        {
            bool found = _synopses.TryGetValue((uid, offset), out object? held);
            synopsis = held as T;
            return found && synopsis is not null;
        }
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

        lock (_gate)
        {
            if (!_synopses.TryAdd((uid, offset), synopsis))
            {
                return;
            }

            _order.Enqueue((uid, offset, bytes));
            _held += bytes;
            while (_held > Bytes && _order.TryDequeue(out (UInt128 Object, ulong Offset, long Bytes) oldest))
            {
                _synopses.Remove((oldest.Object, oldest.Offset));
                _held -= oldest.Bytes;
            }
        }
    }
}
