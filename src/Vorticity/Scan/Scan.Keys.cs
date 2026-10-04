using System;

namespace Vorticity;

public sealed partial class Scan<TRecord>
{
    /// <summary>
    /// A cursor over the keys of <paramref name="column"/>, in key order: a file's sorted column or
    /// index, or a result's values, read whole when the cursor opens and sorted in memory unless
    /// they arrive in order.
    /// </summary>
    /// <typeparam name="TKey">The key column's type, inferred from the member.</typeparam>
    /// <param name="column">The key column.</param>
    /// <returns>The cursor's builder.</returns>
    /// <exception cref="InvalidOperationException">The scan has a filter or selects rows: a cursor walks the whole column.</exception>
    public KeyCursorBuilder<TKey> Keys<TKey>(Func<Probe<TRecord>, Sym<TKey>> column)
    {
        ArgumentNullException.ThrowIfNull(column);
        if (!Filter.IsAll || _rows is not null || _take is not null)
        {
            throw new InvalidOperationException("A key cursor walks the whole column; build it on a scan without Where or Rows.");
        }

        ColumnSym key = column(new Probe<TRecord>(Binding)).Column;
        return new KeyCursorBuilder<TKey>(Source, key, _options.UseIndexes);
    }
}
