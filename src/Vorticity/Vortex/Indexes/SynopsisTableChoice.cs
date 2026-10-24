using System;
using System.Collections.Generic;

namespace Vorticity.Indexes;

/// <summary>
/// The writer's choice of the synopsis tables an object keeps, of those it built: what each saves the questions it
/// answers, against the bytes it holds.
/// </summary>
internal static class SynopsisTableChoice
{
    /// <summary>
    /// Which of <paramref name="tables"/> the synopsis keeps, and why, with the numbers: a greedy choice in
    /// the manner of the classic selection of views. The questions are the grains the tables hold, a bucket
    /// and keys each, and their distributions for a table of sketches, weighed alike. Answering one costs its
    /// source's bytes at <paramref name="prices"/>'s price of a byte, and its rows at the price of a row
    /// grouped: the columns it sums, whose bytes <paramref name="columnBytes"/> gives, over the object's
    /// <paramref name="rows"/>, or the cheapest table kept that answers it by merging its rows. Each round
    /// keeps the table that lowers the questions' costs the most per byte it holds, as long as it lowers them
    /// and fits what is left of <paramref name="budget"/>.
    /// </summary>
    /// <remarks>
    /// A table answers a question when its bucket is as fine or finer, a slice's nesting in its hour and an
    /// hour's in the object, its keys hold the question's, and its measures too, sketched when the question's
    /// are. Of two tables that hold the same rows, the one weighed second saves nothing, and is left out.
    /// </remarks>
    internal static (bool[] Kept, string[] Verdicts) Choose(
        IReadOnlyList<(SynopsisTableInfo Table, long Bytes)> tables, Func<int, long> columnBytes, long rows, long budget, SynopsisPrices prices)
    {
        int count = tables.Count;

        // The questions, a grain each, and what each costs now: its columns.
        List<SynopsisTableInfo> questions = [];
        int[] asks = new int[count];
        for (int c = 0; c < count; c++)
        {
            SynopsisTableInfo table = tables[c].Table;
            asks[c] = questions.FindIndex(q =>
                q.Bucket == table.Bucket && (q.Kind == SynopsisTableKind.Sketches) == (table.Kind == SynopsisTableKind.Sketches)
                && q.Keys.Length == table.Keys.Length && Holds(q.Keys, table.Keys));
            if (asks[c] < 0)
            {
                asks[c] = questions.Count;
                questions.Add(table);
            }
        }

        double[] now = new double[questions.Count];
        for (int q = 0; q < now.Length; q++)
        {
            long bytes = 0;
            foreach (int field in questions[q].Keys)
            {
                bytes += columnBytes(field);
            }

            foreach (int field in questions[q].Measures)
            {
                bytes += columnBytes(field);
            }

            now[q] = prices.Cost(bytes, rows);
        }

        bool[] kept = new bool[count];
        string[] verdicts = new string[count];
        long left = budget;
        while (true)
        {
            int pick = -1;
            double pickSaved = 0;
            double pickRatio = 0;
            int pickQuestions = 0;
            for (int c = 0; c < count; c++)
            {
                if (kept[c] || tables[c].Bytes > left)
                {
                    continue;
                }

                (double saved, int answered) = Saved(tables[c], questions, now, prices);
                double ratio = saved / Math.Max(tables[c].Bytes, 1);
                if (saved > 0 && ratio > pickRatio)
                {
                    (pick, pickSaved, pickRatio, pickQuestions) = (c, saved, ratio, answered);
                }
            }

            if (pick < 0)
            {
                break;
            }

            kept[pick] = true;
            left -= tables[pick].Bytes;
            verdicts[pick] = FormattableString.Invariant(
                $"kept: saves {pickSaved * 1e3:G4} ms over {pickQuestions} questions for {tables[pick].Bytes} bytes, {left} bytes of the budget left");
            double own = prices.Cost(tables[pick].Bytes, tables[pick].Table.Rows);
            for (int q = 0; q < questions.Count; q++)
            {
                if (Answers(tables[pick].Table, questions[q]))
                {
                    now[q] = Math.Min(now[q], own);
                }
            }
        }

        for (int c = 0; c < count; c++)
        {
            if (!kept[c])
            {
                (double saved, int _) = Saved(tables[c], questions, now, prices);
                verdicts[c] = saved <= 0
                    ? FormattableString.Invariant($"left out: its {tables[c].Bytes} bytes save nothing the columns and the tables kept do not")
                    : FormattableString.Invariant($"left out: {tables[c].Bytes} bytes past the {left} the budget of {budget} left");
            }
        }

        return (kept, verdicts);
    }

    /// <summary>What answering from <paramref name="table"/> saves over the questions it answers, against what they cost <paramref name="now"/>, and how many it lowers.</summary>
    private static (double Saved, int Answered) Saved(
        (SynopsisTableInfo Table, long Bytes) table, List<SynopsisTableInfo> questions, double[] now, SynopsisPrices prices)
    {
        double own = prices.Cost(table.Bytes, table.Table.Rows);
        double saved = 0;
        int answered = 0;
        for (int q = 0; q < questions.Count; q++)
        {
            if (Answers(table.Table, questions[q]) && now[q] > own)
            {
                saved += now[q] - own;
                answered++;
            }
        }

        return (saved, answered);
    }

    /// <summary>
    /// Whether <paramref name="table"/>'s rows give <paramref name="other"/>'s, merged: a bucket as fine or finer, its keys and
    /// measures, and its sketches, which only a table of sketches holds.
    /// </summary>
    private static bool Answers(SynopsisTableInfo table, SynopsisTableInfo other) =>
        table.Bucket <= other.Bucket && (table.Kind == SynopsisTableKind.Sketches || other.Kind != SynopsisTableKind.Sketches)
        && Holds(table.Keys, other.Keys) && Holds(table.Measures, other.Measures);

    private static bool Holds(int[] fields, int[] others)
    {
        foreach (int field in others)
        {
            if (Array.IndexOf(fields, field) < 0)
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// What the synopsis's choice prices a question's source at: each byte it reads, and each row it groups.
/// </summary>
/// <param name="ByteSeconds">The seconds a byte read takes.</param>
/// <param name="RowSeconds">The seconds grouping a row takes, a row of data or of a table.</param>
internal readonly record struct SynopsisPrices(double ByteSeconds, double RowSeconds)
{
    /// <summary>
    /// A byte at a tenth of a gigabyte a second, an object store's one stream as the encoding advice takes
    /// it, provisional until the bench of questions measures each store's and the local cache's; and a row
    /// grouped in 12 ns, the engine's on one lane over three keys, a count and three sums, reading and
    /// decoding included (`vortex-queries --micro synopsistables`).
    /// </summary>
    internal static SynopsisPrices Default { get; } = new SynopsisPrices(1e-8, 12e-9);

    /// <summary>What reading <paramref name="bytes"/> and grouping <paramref name="rows"/> costs, in seconds.</summary>
    internal double Cost(long bytes, long rows) => (bytes * ByteSeconds) + (rows * RowSeconds);
}
