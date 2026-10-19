using System;
using System.Collections.Generic;
using System.Linq;
using Vorticity.Dataset;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The keys a chunk of a variant shreds when it shreds only those its rows hold (<c>--micro chunkshapes</c>):
/// a slice of mixed sources, each source its own keys, sorted three ways, its rows then cut into chunks.
/// For each way, the runs the order stores, the chunks, and the keys the chunks shred together, each key
/// a chunk shreds costing its nodes and its entry in the path map.
/// </summary>
internal static class ChunkShapesMicro
{
    internal static int Run(int rows, int chunkRows)
    {
        Random random = new Random(19);

        // Fifty sources, each its eight keys of a hundred and fifty, a severity of six for all.
        int[][] keysOf = [.. Enumerable.Range(0, 50).Select(_ => Enumerable.Range(0, 150).OrderBy(_ => random.Next()).Take(8).ToArray())];
        int[] severity = [.. Enumerable.Range(0, rows).Select(_ => random.Next(6))];
        int[] source = [.. Enumerable.Range(0, rows).Select(_ => (int)Math.Min(49, Math.Abs(Normal(random) * 15)))];
        (int[] Codes, int Distinct)[] columns = [(severity, 6), (source, 50)];

        Console.WriteLine($"{rows} rows a slice, chunks of {chunkRows}, 50 sources of 8 keys among 150");
        Console.WriteLine("way                         order     runs   chunks   keys shredded   keys a chunk");
        int[] greedy = SliceSort.Greedy(columns, rows);
        Report("greedy, chunks of a size", greedy, columns, source, keysOf, rows, chunkRows, cutAtShapes: false);
        Report("shape first", [1, 0], columns, source, keysOf, rows, chunkRows, cutAtShapes: false);
        Report("greedy, cut at shapes", greedy, columns, source, keysOf, rows, chunkRows, cutAtShapes: true);
        return 0;
    }

    private static void Report(
        string name, int[] order, (int[] Codes, int Distinct)[] columns, int[] source, int[][] keysOf, int rows, int chunkRows, bool cutAtShapes)
    {
        int[] permutation = SliceSort.Permutation([.. order.Select(k => columns[k])], rows);
        long runs = SliceSort.Runs([.. order.Select(k => columns[k].Codes)], rows);
        int chunks = 0;
        long shredded = 0;
        HashSet<int> keys = [];
        int held = 0;
        for (int i = 0; i < rows; i++)
        {
            int shape = source[permutation[i]];
            bool cut = held == chunkRows || (cutAtShapes && held > 0 && source[permutation[i - 1]] != shape);
            if (cut)
            {
                shredded += keys.Count;
                chunks++;
                keys.Clear();
                held = 0;
            }

            keys.UnionWith(keysOf[shape]);
            held++;
        }

        shredded += keys.Count;
        chunks++;
        Console.WriteLine($"{name,-26} {string.Join(",", order),-8} {runs,6} {chunks,8} {shredded,15} {(double)shredded / chunks,14:F1}");
    }

    private static double Normal(Random random) => Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
}
