using System;
using Vorticity.Buffers;

namespace Vorticity.Dataset;

/// <summary>
/// How a job's input shapes each batch its scan decodes, on the lane that decoded it: the rows its deletion vector names
/// taken out, then its columns reshaped as the version's schema holds them, when it was written under another. What the
/// wrappers of an input's batches did on the merge's thread (<see cref="LiveRows.WithoutDeletedAsync"/>,
/// <see cref="ObjectColumns.ReshapeAsync"/>), done in the decode's unit.
/// </summary>
internal sealed class InputShaping(DeletionVector? deletions, ObjectShape? shape)
{
    /// <summary>
    /// <paramref name="batch"/> shaped, a view of it in its arena bound from <paramref name="state"/>'s views; null when no row
    /// of it is left.
    /// </summary>
    internal RecordBatch? Shape(RecordBatch batch, InputShapeState state)
    {
        RecordBatch? view = batch;
        if (deletions is not null)
        {
            state.Live?.Dispose();
            view = state.Live = LiveRows.Without(batch, deletions, ref state.Indices, ref state.Mask, state.Live);
            if (view is null)
            {
                return null;
            }
        }

        if (shape is not null)
        {
            int root = shape.Build(view);
            state.Shaped?.Dispose();
            view = state.Shaped = view.Reshaped(root, view.StartRow, state.Shaped);
        }

        return view;
    }
}

/// <summary>
/// What a lane of an input's scan keeps from one batch to the next to shape them (<see cref="InputShaping"/>): its views,
/// and room for the rows a deletion vector keeps.
/// </summary>
internal class InputShapeState : IDisposable
{
    /// <summary>The view of the batch without its deleted rows.</summary>
    internal RecordBatch? Live;

    /// <summary>The view of the batch reshaped.</summary>
    internal RecordBatch? Shaped;

    /// <summary>Room for the rows a deletion vector keeps.</summary>
    internal Block<int> Indices;

    /// <summary>Room for their mask.</summary>
    internal Block<ulong> Mask;

    public virtual void Dispose()
    {
        Live?.Dispose();
        Live = null;
        Shaped?.Dispose();
        Shaped = null;
        Blocks.Return(Indices);
        Indices = default;
        Blocks.Return(Mask);
        Mask = default;
    }
}
