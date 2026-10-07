using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Aggregating;

/// <summary>
/// The parts of a group by's result its core wrote to the scratch (PLAN-HIGH-CARDINALITY, H6), delivered
/// after the groups it held in memory, one at a time: the groups held let go first, then each part
/// brought back alone, delivered, and let go before the next.
/// </summary>
internal sealed class SpilledParts(GroupCore core, CorePart[] parts, AggregationPlan plan)
{
    private int _next;
    private CorePart? _current;
    private bool _heldLet;
    private bool _closed;

    /// <summary>The parts left to deliver, the one delivered now included.</summary>
    internal int Count => parts.Length;

    /// <summary>The next part back in memory, as an outcome of its own, the part delivered before let go; null past the last.</summary>
    internal async ValueTask<AggregationOutcome?> NextAsync(CancellationToken cancellationToken)
    {
        Release();
        if (!_heldLet)
        {
            _heldLet = true;
            core.LetHeld();
        }

        if (_next == parts.Length)
        {
            return null;
        }

        CorePart part = parts[_next++];
        await core.MaterializeAsync(part, cancellationToken).ConfigureAwait(false);
        _current = part;
        (GroupKeys keys, AggregateSlot[] slots) = core.Joined(part);
        return new AggregationOutcome(plan, slots, keys, AggregationEngine.Shuffled(keys.Order(sorted: false)));
    }

    /// <summary>Every part done with, delivered or not: the scratch closed, its file gone, the core's shelf handed on.</summary>
    internal void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        Release();
        core.CloseSpill();
    }

    private void Release()
    {
        if (_current is { } part)
        {
            core.Let(part);
            _current = null;
        }
    }
}
