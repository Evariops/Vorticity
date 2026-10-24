using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Dataset;

/// <summary>
/// A window cut on a dataset's periods and asked part by part, newest first: what lets a page show the
/// newest hours of a month while the rest is read.
/// </summary>
internal static class WindowParts
{
    /// <summary>
    /// The most parts asked at once: each one a question that reads its objects as many at once as the session lets
    /// reads be in flight, so that more parts would only share those reads with the newest one, whose answer comes first.
    /// </summary>
    internal const int Width = 16;

    /// <summary>
    /// The parts of the window from <paramref name="from"/> to <paramref name="to"/>, newest first: the
    /// window cut at the starts of the periods it crosses, by the calendar the dataset's commits keep.
    /// </summary>
    internal static List<(DateTimeOffset From, DateTimeOffset To)> Of(PeriodCalendar calendar, DateTimeOffset from, DateTimeOffset to)
    {
        TimeUnit unit = calendar.Unit;
        List<(DateTimeOffset, DateTimeOffset)> parts = [];
        DateTimeOffset end = to;
        while (end > from)
        {
            // The last instant before the part's end, which an instant counted down to its unit may be.
            long last = TimeSlicer.InstantOf(end, unit);
            if (TimeSlicer.TimeOf(last, unit) >= end)
            {
                last--;
            }

            DateTimeOffset start = TimeSlicer.TimeOf(calendar.Floor(last), unit);
            start = start > from ? start : from;
            parts.Add((start, end));
            end = start;
        }

        return parts;
    }

    /// <summary>
    /// Asks <paramref name="question"/> over each part, as many at once as <paramref name="width"/> says, and
    /// delivers the answers in the parts' order, each once it and every part before it are in.
    /// </summary>
    internal static IAsyncEnumerable<WindowPart<TAnswer>> AskAsync<TAnswer>(
        List<(DateTimeOffset From, DateTimeOffset To)> parts, int width,
        Func<DateTimeOffset, DateTimeOffset, CancellationToken, ValueTask<TAnswer>> question,
        CancellationToken cancellationToken) =>
        AskAsync(parts, width, question, static (from, to, answer) => new WindowPart<TAnswer>(from, to, answer), cancellationToken);

    /// <summary>
    /// <see cref="AskAsync{TAnswer}"/> over parts of any bounds, taken from <paramref name="parts"/> as they are asked, each
    /// answer delivered as the part <paramref name="part"/> makes of it.
    /// </summary>
    internal static async IAsyncEnumerable<TPart> AskAsync<TBound, TAnswer, TPart>(
        IEnumerable<(TBound From, TBound To)> parts, int width,
        Func<TBound, TBound, CancellationToken, ValueTask<TAnswer>> question,
        Func<TBound, TBound, TAnswer, TPart> part,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using IEnumerator<(TBound From, TBound To)> next = parts.GetEnumerator();
        Queue<(TBound From, TBound To, Task<TAnswer> Answer)> ahead = new(width);
        try
        {
            while (true)
            {
                while (ahead.Count < width && next.MoveNext())
                {
                    (TBound from, TBound to) = next.Current;
                    ahead.Enqueue((from, to, question(from, to, stopped.Token).AsTask()));
                }

                if (!ahead.TryDequeue(out (TBound From, TBound To, Task<TAnswer> Answer) turn))
                {
                    yield break;
                }

                // The question was asked by this walk, a few parts before.
#pragma warning disable VSTHRD003
                yield return part(turn.From, turn.To, await turn.Answer.ConfigureAwait(false));
#pragma warning restore VSTHRD003
            }
        }
        finally
        {
            // The questions asked ahead of a consumer that stopped, or of a failure, are nobody's.
            await stopped.CancelAsync().ConfigureAwait(false);
            foreach ((_, _, Task<TAnswer> answer) in ahead)
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
