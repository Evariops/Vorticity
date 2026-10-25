using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// A range cut in parts and asked part by part, several at once, each answer delivered in the parts' order: what lets a
/// page show the newest values of a range while the rest is read (<see cref="VortexDataset.AskByValuesAsync"/>).
/// </summary>
internal static class WindowParts
{
    /// <summary>
    /// The most parts asked at once: each one a question that reads its objects as many at once as the session lets
    /// reads be in flight, so that more parts would only share those reads with the newest one, whose answer comes first.
    /// </summary>
    internal const int Width = 16;

    /// <summary>
    /// Asks <paramref name="question"/> over each of <paramref name="parts"/>, taken as they are asked, as many at once as
    /// <paramref name="width"/> says, and delivers the answers in the parts' order, each once it and every part before it
    /// are in, as the part <paramref name="part"/> makes of it with the share of the range covered.
    /// </summary>
    internal static async IAsyncEnumerable<TPart> AskAsync<TBound, TAnswer, TPart>(
        IEnumerable<(TBound From, TBound To, double Covered)> parts, int width,
        Func<TBound, TBound, CancellationToken, ValueTask<TAnswer>> question,
        Func<TBound, TBound, double, TAnswer, TPart> part,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IEnumerator<(TBound From, TBound To, double Covered)> next = parts.GetEnumerator();
        Queue<(TBound From, TBound To, double Covered, Task<TAnswer> Answer)> ahead = new(width);
        try
        {
            while (true)
            {
                while (ahead.Count < width && next.MoveNext())
                {
                    (TBound from, TBound to, double covered) = next.Current;
                    ahead.Enqueue((from, to, covered, question(from, to, stopped.Token).AsTask()));
                }

                if (!ahead.TryDequeue(out (TBound From, TBound To, double Covered, Task<TAnswer> Answer) turn))
                {
                    yield break;
                }

                // The question was asked by this walk, a few parts before.
#pragma warning disable VSTHRD003
                yield return part(turn.From, turn.To, turn.Covered, await turn.Answer.ConfigureAwait(false));
#pragma warning restore VSTHRD003
            }
        }
        finally
        {
            // The questions asked ahead of a consumer that stopped, or of a failure, are nobody's.
            await stopped.CancelAsync().ConfigureAwait(false);
            foreach ((_, _, _, Task<TAnswer> answer) in ahead)
            {
                try
                {
#pragma warning disable VSTHRD003
                    await answer.ConfigureAwait(false);
#pragma warning restore VSTHRD003
                }
                catch (Exception) when (answer.IsFaulted || answer.IsCanceled)
                {
                }
            }
        }
    }
}
