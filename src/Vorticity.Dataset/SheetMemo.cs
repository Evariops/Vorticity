using System;
using System.Collections.Generic;
using Vorticity.Indexes;

namespace Vorticity.Dataset;

/// <summary>
/// The column sheets a handle read, by object and column, so that the ranges of one question and the questions after it
/// read each sheet once: the oldest go first once they hold more than <see cref="Bytes"/>.
/// </summary>
/// <remarks>
/// An object never changes, so a sheet never goes stale. The lanes of a question race here; a sheet two of them read
/// at once is kept once.
/// </remarks>
internal sealed class SheetMemo
{
    /// <summary>The bytes the memo holds at most: 64 MiB, the sheets of thousands of objects.</summary>
    internal const long Bytes = 64L << 20;

    private readonly Dictionary<(UInt128 Object, int Field), ColumnSheet> _sheets = [];
    private readonly Queue<(UInt128 Object, int Field, long Bytes)> _order = new();
    private readonly System.Threading.Lock _gate = new();
    private long _held;

    /// <summary>The sheet of column <paramref name="field"/> of object <paramref name="uid"/>, when the memo holds it.</summary>
    internal bool TryGet(UInt128 uid, int field, out ColumnSheet? sheet)
    {
        lock (_gate)
        {
            bool found = _sheets.TryGetValue((uid, field), out ColumnSheet? held);
            sheet = held;
            return found;
        }
    }

    /// <summary>Keeps <paramref name="sheet"/>, of <paramref name="bytes"/> bytes, as column <paramref name="field"/>'s of object <paramref name="uid"/>.</summary>
    internal void Keep(UInt128 uid, int field, ColumnSheet sheet, long bytes)
    {
        if (uid == UInt128.Zero || bytes > Bytes / 4)
        {
            return;
        }

        lock (_gate)
        {
            if (!_sheets.TryAdd((uid, field), sheet))
            {
                return;
            }

            _order.Enqueue((uid, field, bytes));
            _held += bytes;
            while (_held > Bytes && _order.TryDequeue(out (UInt128 Object, int Field, long Bytes) oldest))
            {
                _sheets.Remove((oldest.Object, oldest.Field));
                _held -= oldest.Bytes;
            }
        }
    }
}
