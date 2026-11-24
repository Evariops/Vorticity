using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// Reads a walk begins ahead of their use, a few at once: on a token of their own, linked to the caller's, and through a
/// gate of <c>most</c> places. Disposed, the reads still on their way are called off and each begun one is awaited, so that
/// none goes on reading past the walk, its failure seen by nobody; a walk that consumed them all awaits nothing more.
/// </summary>
/// <remarks>
/// What <c>StatesAsync</c> and <c>StatisticsPlacedAsync</c> do by hand: a walk that failed, or whose caller stopped early,
/// left its reads running, a task and a request an object of the version, their answers held until a collection.
/// </remarks>
internal sealed class ReadsAhead : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop;
    private readonly SemaphoreSlim _gate;
    private readonly List<Task> _begun = [];

    /// <summary>Reads on a token linked to <paramref name="cancellationToken"/>, <paramref name="most"/> at once.</summary>
    internal ReadsAhead(int most, CancellationToken cancellationToken)
    {
        _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _gate = new SemaphoreSlim(Math.Max(most, 1));
    }

    /// <summary>
    /// <paramref name="read"/> begun once a place in the gate is free, on the reads' token: complete at once when a place is
    /// free and the read completes at once, as a read a cache answers does.
    /// </summary>
    internal ValueTask<T> BeginAsync<T>(Func<CancellationToken, ValueTask<T>> read)
    {
        ValueTask<T> begun = GatedAsync(read);
        if (!begun.IsCompleted)
        {
            Task<T> pending = begun.AsTask();
            _begun.Add(pending);
            return new ValueTask<T>(pending);
        }

        return begun;
    }

    private async ValueTask<T> GatedAsync<T>(Func<CancellationToken, ValueTask<T>> read)
    {
        await _gate.WaitAsync(_stop.Token).ConfigureAwait(false);
        try
        {
            return await read(_stop.Token).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>Calls off the reads still on their way and awaits each begun one, its outcome the walk's no more.</summary>
    public async ValueTask DisposeAsync()
    {
        await _stop.CancelAsync().ConfigureAwait(false);
        foreach (Task read in _begun)
        {
            try
            {
                await read.ConfigureAwait(false);
            }
            catch (Exception) when (read.IsFaulted || read.IsCanceled)
            {
                // A read the walk no longer waits for: its failure was the walk's when it awaited it, or follows the stop.
            }
        }

        _gate.Dispose();
        _stop.Dispose();
    }
}
