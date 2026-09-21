// The write decomposition probe, which lived outside the repository until now.
//
// WHAT IT IS FOR, and why it is not a BenchmarkDotNet class. The write axis measures one number per
// file -- read, then write, through the public API -- and that number is a sum of three costs that
// move independently: serializing, buffering rows into chunks, and compressing. A single number
// cannot say which of them a change touched. So the same read-and-write is run under five
// configurations of `VortexWriteOptions`, each one removing exactly one of those costs, and the
// differences between the columns are the decomposition:
//
//     scan        no writer at all, so every other column is read + something
//     ser-batch   Compress = false, RowBlockSize = null: serialize a chunk per batch, no transit
//     ser-def     Compress = false, the default shape: the SAME work plus the transit buffer
//     cmp-batch   Compress = true, RowBlockSize = null: the compressor without the transit
//     cmp-def     Compress = true, the default shape: what the write axis measures
//
// MEDIAN OF FIVE AFTER TWO WARM-UPS, on the public surface only (`VortexFile.OpenAsync`,
// `Scan().ExecuteAsync()`, `VortexFileWriter.Create(sink, schema, options)` into a sink that counts
// and throws the bytes away). It is deliberately a console rather than a gate: it explains, it does
// not ratchet: an explanation it suggests is still to be priced by a variant behind a switch.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Writing;

namespace Vorticity.Benchmarks;

/// <summary>The five-configuration write probe.</summary>
internal static class WriteProbe
{
    /// <summary>The environment variable naming the input directory, as the throughput axis takes it.</summary>
    private const string Variable = "VORTICITY_THROUGHPUT_CORPUS";

    private static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ".cache", "vorticity", "throughput-1M");

    /// <summary>Passes thrown away before the measured ones: JIT, statics, the registry.</summary>
    private const int Warmup = 2;

    /// <summary>Measured passes; the median is the answer.</summary>
    private const int Passes = 5;

    /// <summary>One configuration: its name and what it removes.</summary>
    /// <param name="Name">The column header.</param>
    /// <param name="Options">The options, or null for the read-only column.</param>
    private readonly record struct Configuration(string Name, VortexWriteOptions? Options);

    private static readonly Configuration[] Configurations =
    [
        new Configuration("scan", null),
        new Configuration("ser-batch", new VortexWriteOptions { Compress = false, RowBlockSize = null }),
        new Configuration("ser-def", new VortexWriteOptions { Compress = false }),
        new Configuration("cmp-batch", new VortexWriteOptions { Compress = true, RowBlockSize = null }),
        new Configuration("cmp-def", new VortexWriteOptions { Compress = true }),
    ];

    /// <summary>Runs the probe over the named files, or over every file of the corpus.</summary>
    /// <param name="only">Name fragments to keep, or empty for all of them.</param>
    /// <param name="cancellationToken">Cancels the reads and writes.</param>
    /// <returns>The process exit code.</returns>
    internal static async Task<int> RunAsync(string[] only, CancellationToken cancellationToken)
    {
        string? configured = Environment.GetEnvironmentVariable(Variable);
        string root = string.IsNullOrEmpty(configured) ? DefaultRoot : configured;
        if (!Directory.Exists(root))
        {
            Console.Error.WriteLine(
                $"No inputs at {root}.\n" +
                "They are generated, not committed. Produce them with:\n" +
                "  bench/gen-throughput.sh\n" +
                $"or point {Variable} at a directory that already holds them.");
            return 2;
        }

        List<string> files = [];
        foreach (string file in Directory.GetFiles(root, "*.vortex", SearchOption.AllDirectories))
        {
            if (only.Length == 0 || Named(file, only))
            {
                files.Add(file);
            }
        }

        files.Sort(StringComparer.Ordinal);
        if (files.Count == 0)
        {
            Console.Error.WriteLine($"No file of {root} matches {string.Join(", ", only)}.");
            return 2;
        }

        int width = 34;
        foreach (string file in files)
        {
            width = Math.Max(width, Path.GetFileNameWithoutExtension(file).Length + 2);
        }

        Console.Out.Write("file".PadRight(width));
        foreach (Configuration configuration in Configurations)
        {
            Console.Out.Write(configuration.Name.PadLeft(11));
        }

        Console.Out.WriteLine("   (ms, median of " + Passes.ToString(CultureInfo.InvariantCulture) + ")");

        foreach (string file in files)
        {
            Console.Out.Write(Path.GetFileNameWithoutExtension(file).PadRight(width));
            foreach (Configuration configuration in Configurations)
            {
                double median = await MedianAsync(file, configuration.Options, cancellationToken).ConfigureAwait(false);
                Console.Out.Write(median.ToString("F1", CultureInfo.InvariantCulture).PadLeft(11));
            }

            Console.Out.WriteLine();
        }

        return 0;
    }

    private static bool Named(string file, string[] only)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        foreach (string fragment in only)
        {
            if (name.Contains(fragment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The median of <see cref="Passes"/> passes, in milliseconds.</summary>
    /// <param name="path">The input.</param>
    /// <param name="options">The write options, or null to only scan.</param>
    /// <param name="cancellationToken">Cancels the passes.</param>
    private static async Task<double> MedianAsync(
        string path, VortexWriteOptions? options, CancellationToken cancellationToken)
    {
        for (int pass = 0; pass < Warmup; pass++)
        {
            await PassAsync(path, options, cancellationToken).ConfigureAwait(false);
        }

        double[] times = new double[Passes];
        for (int pass = 0; pass < Passes; pass++)
        {
            times[pass] = await PassAsync(path, options, cancellationToken).ConfigureAwait(false);
        }

        Array.Sort(times);
        return times[Passes / 2];
    }

    /// <summary>One pass: read every batch and, unless scanning only, write it back out.</summary>
    /// <param name="path">The input.</param>
    /// <param name="options">The write options, or null to only scan.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>Its milliseconds.</returns>
    private static async Task<double> PassAsync(
        string path, VortexWriteOptions? options, CancellationToken cancellationToken)
    {
        Stopwatch watch = Stopwatch.StartNew();
        await using VortexFile file = await VortexFile.OpenAsync(path, cancellationToken).ConfigureAwait(false);
        if (options is not { } write)
        {
            long rows = 0;
            await foreach (RecordBatch batch in file.Scan().ExecuteAsync().WithCancellation(cancellationToken))
            {
                rows += batch.RowCount;
            }

            GC.KeepAlive(rows);
            return watch.Elapsed.TotalMilliseconds;
        }

        await using VortexFileWriter writer = VortexFileWriter.Create(new CountingSink(), file.Schema, write);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync().WithCancellation(cancellationToken))
        {
            await writer.WriteAsync(batch, cancellationToken).ConfigureAwait(false);
        }

        await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        return watch.Elapsed.TotalMilliseconds;
    }

    /// <summary>A sink that counts and keeps nothing, so the writer is what gets measured.</summary>
    private sealed class CountingSink : ISegmentSink
    {
        public long Position { get; private set; }

        public ValueTask WriteAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
        {
            Position += data.Length;
            return ValueTask.CompletedTask;
        }

        public ValueTask FlushAsync(CancellationToken cancellationToken) => ValueTask.CompletedTask;
    }
}
