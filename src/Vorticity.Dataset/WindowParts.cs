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
    /// delivers the answers newest first, each once it and every newer part are in.
    /// </summary>
    internal static async IAsyncEnumerable<WindowPart<TAnswer>> AskAsync<TAnswer>(
        List<(DateTimeOffset From, DateTimeOffset To)> parts, int width,
        Func<DateTimeOffset, DateTimeOffset, CancellationToken, ValueTask<TAnswer>> question,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using CancellationTokenSource stopped = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        Queue<(DateTimeOffset From, DateTimeOffset To, Task<TAnswer> Answer)> ahead = new(Math.Min(width, parts.Count));
        int next = 0;
        try
        {
            while (true)
            {
                while (ahead.Count < width && next < parts.Count)
                {
                    (DateTimeOffset from, DateTimeOffset to) = parts[next++];
                    ahead.Enqueue((from, to, question(from, to, stopped.Token).AsTask()));
                }

                if (!ahead.TryDequeue(out (DateTimeOffset From, DateTimeOffset To, Task<TAnswer> Answer) turn))
                {
                    yield break;
                }

                // The question was asked by this walk, a few parts before.
#pragma warning disable VSTHRD003
                yield return new WindowPart<TAnswer>(turn.From, turn.To, await turn.Answer.ConfigureAwait(false));
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
