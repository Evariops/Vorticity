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
//
// and over Parquet files, whose only clean failures are ParquetFormatException and
// ParquetUnsupportedException, each mutation read mapped and by positional reads and verified:
//
//     dotnet run --project tests/Vorticity.Fuzz -c Release -- --parquet <corpus-dir> [iterations] [seed]
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.IO;
using Vorticity.Parquet;

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
        if (args.Length > 0 && args[0] == "--parquet")
        {
            return await ParquetCampaign(args[1..]).ConfigureAwait(false);
        }

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

    /// <summary>
    /// The Parquet campaign: mutations of the corpus's Parquet files, structure-aware over their
    /// Thrift, each read mapped and by positional reads, which cut and read ahead otherwise, and
    /// verified against itself.
    /// </summary>
    private static async Task<int> ParquetCampaign(string[] args)
    {
        string corpus = args.Length > 0 ? args[0] : Path.Combine("E:", "parquet-data", "parquet-testing", "data");
        int iterations = args.Length > 1 ? int.Parse(args[1], CultureInfo.InvariantCulture) : 20_000;
        int seed = args.Length > 2 ? int.Parse(args[2], CultureInfo.InvariantCulture) : Environment.TickCount;
        if (!Directory.Exists(corpus))
        {
            Console.Error.WriteLine($"no corpus at {corpus}");
            return 2;
        }

        // Small seeds: a mutation read twice and verified costs a few of their reads.
        string[] seeds = Array.FindAll(
            Directory.GetFiles(corpus, "*.parquet", SearchOption.AllDirectories),
            path => new FileInfo(path).Length is > 64 and < (2 << 20));
        Array.Sort(seeds, StringComparer.Ordinal);
        if (seeds.Length == 0)
        {
            Console.Error.WriteLine($"no .parquet files under {corpus}");
            return 2;
        }

        Console.Out.WriteLine($"fuzzing {iterations} Parquet mutations of {seeds.Length} seed files, seed {seed}");
        string directory = Path.Combine(Path.GetTempPath(), $"vorticity-fuzz-{Environment.ProcessId}");
        Directory.CreateDirectory(directory);
        Random random = new Random(seed);
        Outcome totals = default;
        await using VortexSession positional = VortexSession.Create(options => options.MapFiles = false);
        try
        {
            for (int i = 0; i < iterations; i++)
            {
                string source = seeds[random.Next(seeds.Length)];
                byte[] original = System.IO.File.ReadAllBytes(source);
                (byte[] mutated, string what) = ParquetMutator.Apply(original, random, random.Next(ParquetMutator.Kinds));
                string path = Path.Combine(directory, $"m{i}.parquet");
                System.IO.File.WriteAllBytes(path, mutated);
                (Finding? finding, Reach reach) = await RunParquet(path, positional).ConfigureAwait(false);
                TryDelete(path);
                switch (reach)
                {
                    case Reach.RejectedAtOpen: totals.RejectedAtOpen++; break;
                    case Reach.RejectedWhileDecoding: totals.RejectedWhileDecoding++; break;
                    default: totals.Read++; break;
                }

                if (finding is not null)
                {
                    Console.Error.WriteLine(
                        $"FINDING seed={seed} iteration={i} file={Path.GetFileName(source)} " +
                        $"mutation=\"{what}\": {finding.Value.Kind} - {finding.Value.Detail}");
                    Directory.CreateDirectory(Path.Combine("fuzz", "artifacts"));
                    System.IO.File.WriteAllBytes(Path.Combine("fuzz", "artifacts", $"crash-{seed}-{i}.parquet"), mutated);
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
        }
        finally
        {
            TryDelete(directory, recursive: true);
        }

        Console.Out.WriteLine($"{totals.Clean} mutations failed cleanly or read successfully, {totals.Findings} findings");
        Console.Out.WriteLine(
            $"  reach: {totals.RejectedAtOpen} refused at open, {totals.RejectedWhileDecoding} refused while decoding, {totals.Read} read to the end");
        int deep = totals.RejectedWhileDecoding + totals.Read;
        if (deep * 10 < iterations)
        {
            Console.Error.WriteLine($"WARNING: only {deep} of {iterations} mutations got past the open path; this campaign says little about the decoders.");
        }

        return totals.Findings == 0 ? 0 : 1;
    }

    /// <summary>
    /// Opens, scans and verifies the Parquet file at <paramref name="path"/>, mapped in a session of
    /// its own, which lets the file go when it is disposed, then by positional reads.
    /// </summary>
    private static async Task<(Finding?, Reach)> RunParquet(string path, VortexSession positional)
    {
        Stopwatch clock = Stopwatch.StartNew();
        Reach reach = Reach.RejectedAtOpen;
        try
        {
            await using (VortexSession mapped = VortexSession.Create(options => options.MapFiles = true))
            {
                await using ParquetFile file = await mapped.OpenParquetAsync(path, null, CancellationToken.None).ConfigureAwait(false);
                reach = Reach.RejectedWhileDecoding;
                await foreach (BatchView batch in file.Scan())
                {
                    if (Touch(batch) is { } wrong)
                    {
                        return (new Finding("WrongValue", wrong), reach);
                    }
                }
            }

            await using (ParquetFile file = await positional.OpenParquetAsync(path, null, CancellationToken.None).ConfigureAwait(false))
            {
                await foreach (BatchView batch in file.Scan())
                {
                    if (Touch(batch) is { } wrong)
                    {
                        return (new Finding("WrongValue", wrong), reach);
                    }
                }

                _ = await file.VerifyAsync(CancellationToken.None).ConfigureAwait(false);
            }

            reach = Reach.Read;
        }
        catch (ParquetFormatException)
        {
            // The expected answer for a mutated file.
        }
        catch (ParquetUnsupportedException)
        {
            // Also expected: a mutation can name a codec or an encoding the reader does not take.
        }
        catch (ArgumentException error)
        {
            return (new Finding("ArgumentException", $"a file-driven path reported a caller error: {error.Message}"), reach);
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

    private static void TryDelete(string path, bool recursive = false)
    {
        try
        {
            if (recursive)
            {
                Directory.Delete(path, recursive: true);
            }
            else
            {
                System.IO.File.Delete(path);
            }
        }
        catch (IOException)
        {
            // A file a mapping still holds goes with the directory, or with the system's next clean-up.
        }
        catch (UnauthorizedAccessException)
        {
            // As above.
        }
    }

    /// <summary>Opens and fully scans <paramref name="bytes"/>, classifying whatever happens.</summary>
    private static async Task<(Finding?, Reach)> Run(byte[] bytes)
    {
        Stopwatch clock = Stopwatch.StartNew();
        Reach reach = Reach.RejectedAtOpen;
        try
        {
            // The file owns the source, and disposes it as well when the open fails.
            await using VortexFile file = await VortexSession.Default.OpenAsync(new MemorySegmentSource(bytes));

            reach = Reach.RejectedWhileDecoding;
            await foreach (BatchView batch in file.Scan())
            {
                if (Touch(batch) is { } wrong)
                {
                    return (new Finding("WrongValue", wrong), reach);
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

    /// <summary>
    /// Reads the validity and every value of each column, which forces its decode: a decoder that
    /// produced a buffer too short for its declared length fails here and nowhere earlier.
    /// </summary>
    /// <returns>What is wrong with a column that read without an exception, or null.</returns>
    private static string? Touch(BatchView batch)
    {
        for (int field = 0; field < batch.Schema.Count; field++)
        {
            VortexType type = batch.Schema[field].Type;
            string? wrong = type.Kind is VortexTypeKind.List or VortexTypeKind.FixedSizeList
                ? Read(batch, field, type.ElementType!, list: true)
                : Read(batch, field, type, list: false);
            if (wrong is not null)
            {
                return $"column {Format(field)}, {type}: {wrong}";
            }
        }

        return null;
    }

    /// <summary>
    /// Reads column <paramref name="field"/>, or the elements of its lists, as the .NET type that
    /// <paramref name="type"/> maps to. A struct, a null, a map, a union, a variant, a list of lists
    /// and an extension over anything but a number have no such type on the tool path: the scan's
    /// own decode is all they get.
    /// </summary>
    private static string? Read(BatchView batch, int field, VortexType type, bool list)
    {
        VortexType t = type.NonNullable;
        if (t.Kind == VortexTypeKind.Extension && t.ExtensionId != VortexType.Uuid.ExtensionId
            && t.StorageType is { Kind: VortexTypeKind.Primitive } storage)
        {
            // A date, a time, a timestamp or another extension over a number is read as its
            // storage, which is what the decoders produce; the conversion to a .NET date is not theirs.
            t = storage.NonNullable;
        }

        return t switch
        {
            _ when t == VortexType.Bool => ReadAs<bool?>(batch, field, list, Bools),
            _ when t == VortexType.Int8 => ReadAs<sbyte?>(batch, field, list, Numbers<sbyte>),
            _ when t == VortexType.Int16 => ReadAs<short?>(batch, field, list, Numbers<short>),
            _ when t == VortexType.Int32 => ReadAs<int?>(batch, field, list, Numbers<int>),
            _ when t == VortexType.Int64 => ReadAs<long?>(batch, field, list, Numbers<long>),
            _ when t == VortexType.UInt8 => ReadAs<byte?>(batch, field, list, Numbers<byte>),
            _ when t == VortexType.UInt16 => ReadAs<ushort?>(batch, field, list, Numbers<ushort>),
            _ when t == VortexType.UInt32 => ReadAs<uint?>(batch, field, list, Numbers<uint>),
            _ when t == VortexType.UInt64 => ReadAs<ulong?>(batch, field, list, Numbers<ulong>),
            _ when t == VortexType.Float16 => ReadAs<Half?>(batch, field, list, Numbers<Half>),
            _ when t == VortexType.Float32 => ReadAs<float?>(batch, field, list, Numbers<float>),
            _ when t == VortexType.Float64 => ReadAs<double?>(batch, field, list, Numbers<double>),
            _ when t == VortexType.Utf8 => ReadAs<string?>(batch, field, list, Text),
            _ when t == VortexType.Binary => ReadAs<ReadOnlyMemory<byte>?>(batch, field, list, Bytes),
            _ when t.Kind == VortexTypeKind.Decimal => ReadAs<VortexDecimal?>(batch, field, list, Decimals),
            _ when t.ExtensionId == VortexType.Uuid.ExtensionId => ReadAs<Guid?>(batch, field, list, Guids),
            _ => null,
        };
    }

    /// <summary>
    /// Reads column <paramref name="field"/> with <paramref name="read"/>; for a list column, every
    /// row's range first, then the elements. The nullable form reads a column of either nullability.
    /// </summary>
    private static string? ReadAs<T>(BatchView batch, int field, bool list, Reader<T> read)
    {
        if (!list)
        {
            Column<T> column = batch.Column<T>(field);
            if (column.Length != batch.RowCount)
            {
                return $"{Format(column.Length)} rows in a batch of {Format(batch.RowCount)}";
            }

            read(column);
            return null;
        }

        Column<ReadOnlyMemory<T>> lists = batch.Column<ReadOnlyMemory<T>>(field);
        if (lists.Length != batch.RowCount)
        {
            return $"{Format(lists.Length)} rows in a batch of {Format(batch.RowCount)}";
        }

        Validity(lists);
        Column<T> elements = lists.Elements;
        for (int row = 0; row < lists.Length; row++)
        {
            // A range outside the elements is one a caller slicing them would fail on.
            Range range = lists[row];
            if (lists.IsValid(row) && !Within(range, elements.Length))
            {
                return $"row {Format(row)} spans {range} of {Format(elements.Length)} elements";
            }
        }

        read(elements);
        return null;
    }

    private static bool Within(Range range, int length) =>
        !range.Start.IsFromEnd && !range.End.IsFromEnd
        && range.Start.Value <= range.End.Value && range.End.Value <= length;

    /// <summary>Reads what every column says of its nulls, before its values.</summary>
    private static void Validity<T>(Column<T> column)
    {
        _ = column.NullCount;
        _ = column.ValidityWords;
        for (int row = 0; row < column.Length; row++)
        {
            _ = column.IsValid(row);
        }
    }

    /// <summary>Reads every slot of the values, a null's included: the buffer covers every row whatever the validity.</summary>
    private static void Numbers<T>(Column<T?> column)
        where T : unmanaged, IBinaryNumber<T>
    {
        Validity(column);
        ReadOnlySpan<T> values = column.Values;
        for (int row = 0; row < column.Length; row++)
        {
            _ = values[row];
        }
    }

    private static void Bools(Column<bool?> column)
    {
        Validity(column);
        for (int row = 0; row < column.Length; row++)
        {
            _ = column[row];
        }
    }

    private static void Text(Column<string?> column)
    {
        Validity(column);
        for (int row = 0; row < column.Length; row++)
        {
            _ = column[row];
        }
    }

    private static void Bytes(Column<ReadOnlyMemory<byte>?> column)
    {
        Validity(column);
        for (int row = 0; row < column.Length; row++)
        {
            _ = column[row];
        }
    }

    private static void Decimals(Column<VortexDecimal?> column)
    {
        Validity(column);
        for (int row = 0; row < column.Length; row++)
        {
            _ = column[row];
        }
    }

    private static void Guids(Column<Guid?> column)
    {
        Validity(column);
        for (int row = 0; row < column.Length; row++)
        {
            _ = column[row];
        }
    }

    private static string Format(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static void Save(byte[] bytes, int seed, int iteration)
    {
        // Minimized by hand afterwards and checked in as a regression test.
        string directory = Path.Combine("fuzz", "artifacts");
        Directory.CreateDirectory(directory);
        System.IO.File.WriteAllBytes(
            Path.Combine(directory, $"crash-{seed}-{iteration}.vortex"), bytes);
    }

    /// <summary>Reads one column of a type known here, not by the scan.</summary>
    private delegate void Reader<T>(Column<T> column);

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
