using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Execution;

/// <summary>
/// Workers of a question that wait as they go, for the store, the scratch or each other, and share their work through a
/// queue of their own: each one past the first holds a lane of the session's for as long as it runs, taken as it starts,
/// and one that finds no lane free does not start, the others taking its share from the queue.
/// </summary>
/// <remarks>
/// A question started such workers as tasks of the pool, as many as its degree, which the session's degree did not count:
/// a question and a job together took more cores than the degree. A worker holds its lane while it waits too, so that the
/// degree is never passed; that a lane may idle so is the price until the work becomes units of the core.
/// </remarks>
internal static class LaneWorkers
{
    /// <summary>
    /// Starts up to <paramref name="wanted"/> workers beside the caller, each on a lane of <paramref name="lanes"/> free now
    /// that it holds until it ends: the tasks started, as many as lanes were free. Each runs
    /// <paramref name="worker"/> with its number from 1; the caller is worker 0, if it works.
    /// </summary>
    internal static Task[] Start(LaneBudget lanes, int wanted, Func<int, Task> worker)
    {
        List<Task>? started = null;
        for (int w = 1; w <= wanted; w++)
        {
            if (!lanes.TryEnter(out long entered))
            {
                break;
            }

            int number = w;
            (started ??= []).Add(Task.Run(
                async () =>
                {
                    try
                    {
                        await worker(number).ConfigureAwait(false);
                    }
                    finally
                    {
                        lanes.Exit(entered);
                    }
                },
                CancellationToken.None));
        }

        return started is null ? [] : [.. started];
    }

    /// <summary>
    /// Takes up to <paramref name="wanted"/> lanes of <paramref name="lanes"/> free now, for workers started once the caller
    /// knows how many it has (<see cref="RunOnAsync"/>): what each lane's entry was, as many as were free.
    /// </summary>
    internal static long[] Take(LaneBudget lanes, int wanted)
    {
        List<long>? taken = null;
        while ((taken?.Count ?? 0) < wanted && lanes.TryEnter(out long entered))
        {
            (taken ??= []).Add(entered);
        }

        return taken is null ? [] : [.. taken];
    }

    /// <summary>Runs <paramref name="worker"/> on the pool, on the lane of <paramref name="lanes"/> <see cref="Take"/> took as <paramref name="entered"/>, which it gives back as it ends.</summary>
    internal static Task RunOnAsync(LaneBudget lanes, long entered, Func<Task> worker) =>
        Task.Run(
            async () =>
            {
                try
                {
                    await worker().ConfigureAwait(false);
                }
                finally
                {
                    lanes.Exit(entered);
                }
            },
            CancellationToken.None);

    /// <summary>
    /// Runs <paramref name="work"/> on the pool once a lane of <paramref name="lanes"/> is free, which it holds until it ends:
    /// for work a reader waits on that must run whatever the lanes, which waits for one holding none, as a job does before
    /// it starts.
    /// </summary>
    internal static Task RunHeldAsync(LaneBudget lanes, Func<Task> work, CancellationToken cancellationToken) =>
        Task.Run(
            async () =>
            {
                long entered = await lanes.EnterAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await work().ConfigureAwait(false);
                }
                finally
                {
                    lanes.Exit(entered);
                }
            },
            CancellationToken.None);

    /// <summary>
    /// Runs <paramref name="workers"/> workers, the first on the caller's flow and each other on a lane of
    /// <paramref name="lanes"/> free now (<see cref="Start"/>), and waits for all; the first failure cancels
    /// <paramref name="failed"/>, which the workers watch, and is thrown once every worker ended.
    /// </summary>
    internal static async Task RunAsync(LaneBudget lanes, int workers, Func<int, Task> worker, CancellationTokenSource failed)
    {
        Task[] others = Start(lanes, workers - 1, w => GuardedAsync(worker, w, failed));
        Exception? first = null;
        try
        {
            await worker(0).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            first = error;
            await failed.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await Task.WhenAll(others).ConfigureAwait(false);
        }
        catch (Exception error) when (first is null)
        {
            first = error;
        }
        catch (Exception)
        {
            // The caller's failure came first.
        }

        if (first is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw(first);
        }
    }

    private static async Task GuardedAsync(Func<int, Task> worker, int number, CancellationTokenSource failed)
    {
        try
        {
            await worker(number).ConfigureAwait(false);
        }
        catch
        {
            await failed.CancelAsync().ConfigureAwait(false);
            throw;
        }
    }
}
