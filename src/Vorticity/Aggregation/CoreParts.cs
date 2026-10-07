using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Vorticity.Aggregating;

/// <summary>
/// The parts of a group by's result its core delivers one at a time (PLAN-HIGH-CARDINALITY, H6, H7),
/// each built into its batches and let go before it is handed to the reader: those its workers apply
/// and build as each is done, while the others apply, and those the reader applies and builds itself
/// when no worker's waits; then those it wrote to the scratch, each brought back alone.
/// </summary>
/// <remarks>
/// A result delivered whole holds the parts it kept in memory itself, let go before the first spilled
/// part comes back. A consumer that stops early stops the workers, and waits for them before the
/// query's memory is given back: no worker reserves past it.
/// </remarks>
internal sealed class CoreParts : ResultParts
{
    private readonly GroupCore _core;
    private readonly AggregationPlan _plan;
    private readonly PartBuilder? _builder;
    private ChannelReader<PartResult>? _built;
    private readonly CancellationTokenSource? _stopping;

    // The parts' queue the workers take from, which the reader takes from too, with an applier of its own.
    private readonly int[]? _queue;
    private CoreApplier? _applier;
    private CorePart[]? _spilled;

    // The part of the null groups, delivered once every part is applied.
    private CorePart? _nulls;
    private int _next;
    private bool _held;
    private bool _closed;

    /// <summary>The parts a result delivered whole spilled, after the groups it holds; built by <paramref name="builder"/>, or brought back for the core's emitter alone (H13).</summary>
    internal CoreParts(GroupCore core, CorePart[] spilled, AggregationPlan plan, PartBuilder? builder)
    {
        _core = core;
        _plan = plan;
        _builder = builder;
        _spilled = spilled;
        _held = true;
    }

    /// <summary>
    /// Parts the workers apply, build and publish on <paramref name="built"/>, until they complete it, and
    /// those the reader takes from <paramref name="queue"/> when none waits; then the part of the null
    /// groups, <paramref name="nulls"/>, if any; then the parts spilled.
    /// </summary>
    internal CoreParts(
        GroupCore core, ChannelReader<PartResult> built, int[] queue, CancellationTokenSource stopping, AggregationPlan plan, PartBuilder builder, CorePart? nulls)
    {
        _core = core;
        _plan = plan;
        _builder = builder;
        _built = built;
        _queue = queue;
        _stopping = stopping;
        _nulls = nulls;
    }

    /// <summary>The parts spilled, once known: none before the workers are done.</summary>
    internal override int Count => _spilled?.Length ?? 0;

    /// <summary>The batches of the next part, built, the part let go; null past the last.</summary>
    internal override async ValueTask<PartResult?> NextAsync(CancellationToken cancellationToken)
    {
        if (_built is { } built)
        {
            while (true)
            {
                // A part a worker built; or else the next no worker took, applied and built here rather
                // than wait; or else, every part taken, the wait for those the workers still build. A
                // worker's failure comes out of the wait, the channel completed with it.
                if (built.TryRead(out PartResult? result))
                {
                    return Delivered(result);
                }

                (bool taken, CorePart? own) = await _core.ApplyNextAsync(_queue!, _applier ??= new CoreApplier(_core, lane: null), cancellationToken).ConfigureAwait(false);
                if (own is not null)
                {
                    return Delivered(await _core.BuildAsync(own, Builder, cancellationToken).ConfigureAwait(false));
                }

                if (!taken && !await built.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }
            }

            // Every part applied: the core's counts are final, and what the parts delivered left on the
            // shelf goes before the parts spilled come back.
            _built = null;
            _core.AppliedAll();
            _spilled = _core.SpilledList();
            _held = _spilled.Length > 0;
            if (_plan.LastRun is { } run)
            {
                _plan.LastRun = run with { Core = _core.Run() };
            }
        }

        if (_nulls is { } nulls)
        {
            _nulls = null;
            return Delivered(await _core.BuildAsync(nulls, Builder, cancellationToken).ConfigureAwait(false));
        }

        if (_held)
        {
            _held = false;
            _core.LetHeld();
        }

        if (_spilled is null || _next == _spilled.Length)
        {
            return null;
        }

        CorePart spilled = _spilled[_next++];
        await _core.MaterializeAsync(spilled, cancellationToken).ConfigureAwait(false);
        return Delivered(await _core.BuildAsync(spilled, Builder, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// The parts spilled brought back one after the other, their values told to the core's emitter as
    /// they enter their sets, those written as told coming back first, then let go (PLAN-HIGH-CARDINALITY,
    /// H13): the end of a <c>Distinct</c> that spilled.
    /// </summary>
    internal async ValueTask EmitSpilledAsync(CancellationToken cancellationToken)
    {
        if (_held)
        {
            _held = false;
            _core.LetHeld();
        }

        foreach (CorePart spilled in _spilled ?? [])
        {
            await _core.MaterializeAsync(spilled, cancellationToken).ConfigureAwait(false);
            _core.Let(spilled);
        }

        _spilled = [];
    }

    /// <summary>What builds the parts' batches: a result's reader always has one.</summary>
    private PartBuilder Builder => _builder ?? throw new InvalidOperationException("The parts of a result are built by its reader's builder.");

    /// <summary>The workers stopped and awaited, a consumer leaving early: none touches the core once its query's memory is given back.</summary>
    internal override async ValueTask StopAsync()
    {
        if (_built is not { } built || _stopping is null)
        {
            return;
        }

        // The channel drained until the workers complete it, the batches built given back: the consumer
        // left, and a worker's failure past that point is no one's to see.
        await _stopping.CancelAsync().ConfigureAwait(false);
        try
        {
            while (await built.WaitToReadAsync(CancellationToken.None).ConfigureAwait(false))
            {
                while (built.TryRead(out PartResult? result))
                {
                    Builder.Give(result);
                }
            }
        }
        catch (Exception)
        {
        }

        _built = null;
    }

    /// <summary>Every part done with, delivered or not: the scratch closed, its file gone, the core's shelf handed on.</summary>
    internal override void Close()
    {
        if (_closed)
        {
            return;
        }

        _closed = true;
        _core.CloseSpill();
        _stopping?.Dispose();
    }

    /// <summary>A part's batches, as delivered: the most groups the parts held at once, the plan's peak.</summary>
    private PartResult Delivered(PartResult result)
    {
        _plan.PeakGroups = Math.Max(_plan.PeakGroups, _core.PeakGroups);
        return result;
    }
}
