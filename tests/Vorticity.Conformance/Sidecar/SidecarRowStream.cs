// One expected value per row, pulled from the sidecar's `rows` lines in file order.
//
// The scan hands out batches in row order and the sidecar stores values in row order, so the two
// are consumed in lockstep and neither is ever materialized whole. 108 MB of sidecar across the
// corpus is the reason that matters.
using System;

namespace Vorticity.Conformance.Sidecar;

/// <summary>A row-by-row view over a <see cref="SidecarReader"/>'s value stream.</summary>
internal sealed class SidecarRowStream
{
    private readonly SidecarReader _reader;
    private JsonValue[] _values = [];
    private long _lineStart;
    private int _at;

    internal SidecarRowStream(SidecarReader reader) => _reader = reader;

    /// <summary>How many values have been handed out.</summary>
    internal long Produced { get; private set; }

    /// <summary>The next expected value, with the absolute file row it belongs to.</summary>
    /// <param name="row">The absolute row index.</param>
    /// <param name="value">The sidecar value.</param>
    /// <returns><see langword="false"/> when the value stream is exhausted.</returns>
    internal bool TryNext(out long row, out JsonValue value)
    {
        while (_at >= _values.Length)
        {
            if (!_reader.TryReadRows(out long from, out JsonValue[] values))
            {
                row = -1;
                value = JsonValue.Null;
                return false;
            }

            _values = values;
            _lineStart = from;
            _at = 0;
        }

        row = _lineStart + _at;
        value = _values[_at];
        _at++;
        Produced++;
        return true;
    }
}
