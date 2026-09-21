// The fuzzer: the parser meets input nobody wrote on purpose.
//
// THE INVARIANT, quoted: "only VortexFormatException or VortexUnsupportedException may escape. Any
// AccessViolation, IndexOutOfRange, OutOfMemory, hang, or silently wrong value is a bug."
//
// Two additions that a naive fuzzer skips, and that are the reason this is a program
// rather than a loop over File.ReadAllBytes:
//
//   * RESOURCE CAPS ARE FUZZING INVARIANTS, not just constants. "A file declaring 2^255 alignment
//     or a 100 GiB decompressed segment must fail CLEANLY AND FAST"; the failure AND the time bound
//     are both asserted, because a parser that eventually throws after allocating 100 GiB has
//     satisfied the type of the exception and nothing else.
//   * A SCAN, not just an open. Opening validates the tail; the encodings, the layouts and every
//     bounds check inside a decoder are only reached by reading the data.
//
// AND IT REPORTS ITS OWN COVERAGE, because "0 findings" is the same output from a fuzzer that
// exercised every decoder and from one that was rejected at byte 0 on every iteration. The campaign
// prints how many mutations were read SUCCESSFULLY and how many reached a decoder at all; a run
// whose mutations all die in the postscript has proved nothing and says so.
//
// Deterministic by seed, so a finding is reproducible from its line of output alone:
//
//     dotnet run --project tests/Vorticity.Fuzz -c Release -- <corpus-dir> [iterations] [seed]
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Vorticity;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Scanning;

namespace Vorticity.Fuzz;

internal static class Program
{
    /// <summary>
    /// How long one mutated file may take before it counts as a hang.
    /// </summary>
    /// <remarks>
    /// The largest corpus file scans in single-digit milliseconds, so five seconds is not a
    /// tolerance, it is a diagnosis: anything near it is a cap that is not being enforced.
    /// </remarks>
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(5);

    private static async Task<int> Main(string[] args)
    {
        string corpus = args.Length > 0
            ? args[0]
            : Path.Combine("tests", "Vorticity.Conformance", "corpus");
        int iterations = args.Length > 1
            ? int.Parse(args[1], CultureInfo.InvariantCulture)
            : 20_000;
        int seed = args.Length > 2
            ? int.Parse(args[2], CultureInfo.InvariantCulture)
            : Environment.TickCount;

        if (!Directory.Exists(corpus))
        {
            Console.Error.WriteLine($"no corpus at {corpus}");
            return 2;
        }

        string[] seeds = Directory.GetFiles(corpus, "*.vortex", SearchOption.AllDirectories);
        Array.Sort(seeds, StringComparer.Ordinal);
        if (seeds.Length == 0)
        {
            Console.Error.WriteLine($"no .vortex files under {corpus}");
            return 2;
        }

        Console.Out.WriteLine(
            $"fuzzing {iterations} mutations of {seeds.Length} seed files, seed {seed}");

        Random random = new Random(seed);
        Outcome totals = default;

        for (int i = 0; i < iterations; i++)
        {
            string path = seeds[random.Next(seeds.Length)];
            byte[] original = System.IO.File.ReadAllBytes(path);
            int kind = random.Next(Mutator.Kinds);
            (byte[] mutated, string what) = Mutator.Apply(original, random, kind);

            (Finding? finding, Reach reach) = await Run(mutated).ConfigureAwait(false);
            switch (reach)
            {
                case Reach.RejectedAtOpen: totals.RejectedAtOpen++; break;
                case Reach.RejectedWhileDecoding: totals.RejectedWhileDecoding++; break;
                default: totals.Read++; break;
            }

            if (finding is not null)
            {
                Console.Error.WriteLine(
                    $"FINDING seed={seed} iteration={i} file={Path.GetFileName(path)} " +
                    $"mutation=\"{what}\": {finding.Value.Kind} - {finding.Value.Detail}");
                Save(mutated, seed, i);
                totals.Findings++;
                if (totals.Findings >= 20)
                {
                    Console.Error.WriteLine("stopping after 20 findings");
                    break;
                }
            }
            else
            {
                totals.Clean++;
            }
        }

        Console.Out.WriteLine(
            $"{totals.Clean} mutations failed cleanly or read successfully, " +
            $"{totals.Findings} findings");
        Console.Out.WriteLine(
            $"  reach: {totals.RejectedAtOpen} refused at open, " +
            $"{totals.RejectedWhileDecoding} refused while decoding, {totals.Read} read to the end");

        // A campaign whose mutations all died in the postscript exercised the tail parser and
        // nothing else, and reporting "0 findings" for it would be misleading rather than merely
        // uninformative.
        int deep = totals.RejectedWhileDecoding + totals.Read;
        if (deep * 10 < iterations)
        {
            Console.Error.WriteLine(
                $"WARNING: only {deep} of {iterations} mutations got past the open path; this " +
                "campaign says little about the decoders.");
        }

        return totals.Findings == 0 ? 0 : 1;
    }

    /// <summary>Opens and fully scans <paramref name="bytes"/>, classifying whatever happens.</summary>
    private static async Task<(Finding?, Reach)> Run(byte[] bytes)
    {
        Stopwatch clock = Stopwatch.StartNew();
        Reach reach = Reach.RejectedAtOpen;
        try
        {
            await using MemorySegmentSource source = new MemorySegmentSource(bytes);
            await using VortexFile file = await VortexFile.OpenAsync(
                source, new VortexOpenOptions { LeaveSourceOpen = true }, CancellationToken.None);

            reach = Reach.RejectedWhileDecoding;
            await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
                .WithCancellation(CancellationToken.None))
            {
                // Touch the rows: a decoder that produced a buffer too short for its declared
                // length fails here and nowhere earlier.
                for (int field = 0; field < batch.FieldCount; field++)
                {
                    VortexColumn column = batch.Column(field);
                    for (int row = 0; row < batch.RowCount; row++)
                    {
                        _ = column.IsValid(row);
                    }
                }
            }

            reach = Reach.Read;
        }
        catch (VortexFormatException)
        {
            // The expected answer for a mutated file.
        }
        catch (VortexUnsupportedException)
        {
            // Also expected: a mutation can rename an encoding id.
        }
        catch (ArgumentException)
        {
            // A caller-error exception from a FILE-driven path is a finding, because the file is
            // not the caller. Reported rather than swallowed.
            return (
                new Finding("ArgumentException", "a file-driven path reported a caller error"), reach);
        }
        catch (Exception error)
        {
            return (new Finding(error.GetType().Name, error.Message), reach);
        }
        finally
        {
            clock.Stop();
        }

        return (
            clock.Elapsed > Budget
                ? new Finding("Timeout", $"took {clock.Elapsed.TotalSeconds:F1}s, budget {Budget.TotalSeconds}s")
                : null,
            reach);
    }

    private static void Save(byte[] bytes, int seed, int iteration)
    {
        // Minimized by hand afterwards and checked in as a regression test.
        string directory = Path.Combine("fuzz", "artifacts");
        Directory.CreateDirectory(directory);
        System.IO.File.WriteAllBytes(
            Path.Combine(directory, $"crash-{seed}-{iteration}.vortex"), bytes);
    }

    private readonly record struct Finding(string Kind, string Detail);

    /// <summary>How far one mutated file got.</summary>
    private enum Reach : byte
    {
        /// <summary>Refused before a single row was decoded.</summary>
        RejectedAtOpen = 0,

        /// <summary>Opened, then refused while decoding: a decoder's own bounds check fired.</summary>
        RejectedWhileDecoding = 1,

        /// <summary>Read to the end. The mutation landed somewhere that changes no structure.</summary>
        Read = 2,
    }

    private struct Outcome
    {
        internal int Clean;
        internal int Findings;
        internal int RejectedAtOpen;
        internal int RejectedWhileDecoding;
        internal int Read;
    }
}
