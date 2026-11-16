// A store that holds reads and heads in rounds: what a test of round trips counts by, rather than by a latency, which a busy
// machine moves.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <summary>
/// A store that holds each read and head, while <see cref="Holding"/>, until the test lets the round go: requests sent without
/// waiting for one another are held together however late a busy thread sends the last, and a request sent once an answer is
/// in waits for the next round.
/// </summary>
internal sealed class RoundStore(IObjectStore inner) : ForwardingStore(inner)
{
    /// <summary>How long a round waits for its requests, which only a caller that waits for an answer before it sends the next reaches.</summary>
    internal static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    private readonly object _gate = new object();
    private readonly SemaphoreSlim _arrived = new SemaphoreSlim(0);
    private readonly List<ObjectOperation> _held = [];
    private TaskCompletionSource _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Whether reads and heads are held; set it with nothing in flight.</summary>
    internal bool Holding { get; set; }

    /// <summary>Waits until <paramref name="count"/> requests are held, lets the round go, and returns their kinds, in the enumeration's order.</summary>
    internal async Task<ObjectOperation[]> ReleaseAsync(int count, CancellationToken cancellationToken)
    {
        for (int i = 0; i < count; i++)
        {
            if (!await _arrived.WaitAsync(Patience, cancellationToken))
            {
                Assert.Fail($"{i} of the round's {count} requests went out before an answer came back.");
            }
        }

        return Release();
    }

    /// <summary>
    /// Runs <paramref name="work"/> holding its requests in rounds of <paramref name="sizes"/>, one after another: fails when a
    /// round lacks a request, or when one more comes once they are all let go.
    /// </summary>
    internal async Task<T> RoundsAsync<T>(Func<Task<T>> work, int[] sizes, CancellationToken cancellationToken)
    {
        Holding = true;
        try
        {
            Task<T> running = work();
            foreach (int size in sizes)
            {
                await ReleaseAsync(size, cancellationToken);
            }

            if (await ArrivesBeforeAsync(running, cancellationToken))
            {
                Release();
                Assert.Fail($"A request went out past the {sizes.Length} rounds of {string.Join(", ", sizes)}.");
            }

            return await running;
        }
        finally
        {
            Holding = false;
        }
    }

    public override async ValueTask<ObjectRead> ReadAsync(
        string key, long offset, Memory<byte> destination, string? ifToken, CancellationToken cancellationToken)
    {
        await HoldAsync(ObjectOperation.Read, cancellationToken);
        return await Inner.ReadAsync(key, offset, destination, ifToken, cancellationToken);
    }

    public override async ValueTask<ObjectRead> ReadTailAsync(string key, Memory<byte> destination, CancellationToken cancellationToken)
    {
        await HoldAsync(ObjectOperation.Read, cancellationToken);
        return await Inner.ReadTailAsync(key, destination, cancellationToken);
    }

    public override async ValueTask<ObjectHead?> HeadAsync(string key, CancellationToken cancellationToken)
    {
        await HoldAsync(ObjectOperation.Head, cancellationToken);
        return await Inner.HeadAsync(key, cancellationToken);
    }

    public override ValueTask DisposeAsync()
    {
        _arrived.Dispose();
        return ValueTask.CompletedTask;
    }

    /// <summary>Whether a request is held before <paramref name="running"/> ends, the wait for it cancelled when it does not come.</summary>
    private async Task<bool> ArrivesBeforeAsync(Task running, CancellationToken cancellationToken)
    {
        using CancellationTokenSource waiting = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Task<bool> arrived = _arrived.WaitAsync(Patience, waiting.Token);
        if (await Task.WhenAny(arrived, running) == arrived)
        {
            return await arrived;
        }

        await waiting.CancelAsync();
        try
        {
            return await arrived;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private ObjectOperation[] Release()
    {
        lock (_gate)
        {
            ObjectOperation[] round = [.. _held.Order()];
            _held.Clear();
            TaskCompletionSource release = _release;
            _release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            release.SetResult();
            return round;
        }
    }

    private Task HoldAsync(ObjectOperation operation, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!Holding)
            {
                return Task.CompletedTask;
            }

            _held.Add(operation);
            _arrived.Release();
            return _release.Task.WaitAsync(cancellationToken);
        }
    }
}
