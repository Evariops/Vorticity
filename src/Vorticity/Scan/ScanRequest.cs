using System;

using Vorticity.Layouts;

namespace Vorticity.Scanning;

/// <summary>
/// The immutable description of one batch, threaded through both phases of the executor. Segment
/// registration and execution are handed the same struct by value, so they cannot disagree about
/// what the batch covers.
/// </summary>
/// <remarks>
/// <see cref="Rows"/> is in the root layout's coordinates, which for a Vortex file are the file's
/// own row indices. Layout readers translate them for their children.
/// </remarks>
internal readonly struct ScanRequest
{
    /// <summary>Creates a request.</summary>
    /// <param name="rows">The rows this batch covers, in root coordinates.</param>
    /// <param name="fields">Which fields of the root struct the caller wants.</param>
    /// <param name="maxBatchRows">
    /// The cap the batch was planned under. Carried for diagnostics: the range in
    /// <paramref name="rows"/> already honours it.
    /// </param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="maxBatchRows"/> is not positive.</exception>
    public ScanRequest(RowRange rows, FieldMask fields, int maxBatchRows)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBatchRows);
        Rows = rows;
        Fields = fields;
        MaxBatchRows = maxBatchRows;
    }

    /// <summary>The rows this batch covers, in root-layout coordinates.</summary>
    public RowRange Rows { get; }

    /// <summary>Which fields of the root struct the caller wants.</summary>
    public FieldMask Fields { get; }

    /// <summary>The batch-size cap the split planner applied.</summary>
    public int MaxBatchRows { get; }
}
