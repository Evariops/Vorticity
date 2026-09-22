using System;
using System.Buffers;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Tests.Api;

/// <summary>
/// A day of the sensor table the samples read, written by hand the way the generator would: the
/// test project does not run the generator, and the contract is about what a record gets, not how.
/// </summary>
internal readonly record struct Reading(int Day, double? Celsius, string City) : IVortexRecord<Reading>
{
    public static VortexSchema Schema { get; } =
    [
        ("Day", VortexType.Int32),
        ("Celsius", VortexType.Float64.Nullable),
        ("City", VortexType.Utf8),
    ];

    public static void ReadRows(Columns<Reading> columns, Span<Reading> rows)
    {
        ReadOnlySpan<int> days = columns.Column<int>(0).Values;
        Column<double?> celsius = columns.Column<double?>(1);
        Column<string> city = columns.Column<string>(2);
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new Reading(days[i], celsius[i], city.GetString(i)!);
        }
    }

    public static void WriteRows(ColumnsBuilder<Reading> builder, ReadOnlySpan<Reading> rows)
    {
        ColumnBuilder<int> days = builder.Column<int>(0);
        Span<int> span = days.GetSpan(rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            span[i] = rows[i].Day;
        }

        days.Advance(rows.Length);

        ColumnBuilder<double?> celsius = builder.Column<double?>(1);
        ColumnBuilder<string> city = builder.Column<string>(2);
        for (int i = 0; i < rows.Length; i++)
        {
            celsius.Append(rows[i].Celsius);
            city.Append(rows[i].City.AsSpan());
        }
    }
}

/// <summary>The members of <see cref="Reading"/> as the symbols of a scan's lambdas.</summary>
internal static class ReadingSymbols
{
    extension(Probe<Reading> r)
    {
        public Sym<int> Day => r.Column<int>(0);

        public Sym<double?> Celsius => r.Column<double?>(1);

        public Sym<string> City => r.Column<string>(2);
    }
}

/// <summary>
/// The file the contract tests read, written once per run through the public writer.
/// </summary>
/// <remarks>
/// Its shape is the demo's, chosen for what each gate needs: <c>Day</c> sorted, in runs of a
/// thousand rows, so that it is written run-end and its zone maps prune; <c>Celsius</c> nullable,
/// every fiftieth row null, over a range every block spans, so that a filter on it prunes nothing;
/// <c>City</c> one of eight names in no run, so that it is written as a dictionary. The writer cuts
/// the rows into chunks larger than a batch and chunks of one batch, so a scan walks both a chunk
/// it decodes once and slices, and a chunk it decodes whole into the batch.
/// </remarks>
internal static class ContractFile
{
    internal const int Rows = 250_000;

    internal static readonly string[] Cities =
        ["Paris", "Lyon", "Marseille", "Toulouse", "Nice", "Nantes", "Strasbourg", "Lille"];

    private static readonly Lazy<Task<string>> Written = new Lazy<Task<string>>(() => WriteAsync("readings", null));

    // The columns compress to a few bytes a row, which a run listing every key does not, so the
    // budget is set far above the default rather than the index being dropped for its size.
    private static readonly Lazy<Task<string>> Indexed = new Lazy<Task<string>>(() => WriteAsync(
        "readings-indexed",
        new VortexWriteOptions { Indexes = IndexPolicy.None.SortedRuns("City").WithBudgetPerMille(20_000) }));

    /// <summary>The path of the file, written on first use.</summary>
    internal static Task<string> PathAsync() => Written.Value;

    /// <summary>The same rows with sorted runs over <c>City</c>, an index that both locates blocks and proves rows.</summary>
    internal static Task<string> IndexedPathAsync() => Indexed.Value;

    /// <summary>Row <paramref name="row"/> of the table.</summary>
    internal static Reading Row(int row) => new Reading(
        row / 1_000,
        row % 50 == 0 ? null : 10.0 + (row % 400 / 10.0),
        Cities[(int)(((uint)row * 2_654_435_761u) >> 29)]);

    private static async Task<string> WriteAsync(string name, VortexWriteOptions? options)
    {
        // One per run, named by the process so that two runs side by side never share it, and
        // gone when the run ends.
        string directory = System.IO.Path.Combine(AppContext.BaseDirectory, "contract");
        Directory.CreateDirectory(directory);
        string path = System.IO.Path.Combine(directory, $"{name}-{Environment.ProcessId}.vortex");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => System.IO.File.Delete(path);

        await using VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(path, options);
        Reading[] block = ArrayPool<Reading>.Shared.Rent(writer.BlockRows);
        try
        {
            for (int start = 0; start < Rows; start += writer.BlockRows)
            {
                int count = Math.Min(writer.BlockRows, Rows - start);
                for (int i = 0; i < count; i++)
                {
                    block[i] = Row(start + i);
                }

                await writer.WriteAsync<Reading>(block.AsSpan(0, count), CancellationToken.None);
            }
        }
        finally
        {
            ArrayPool<Reading>.Shared.Return(block, RuntimeHelpers.IsReferenceOrContainsReferences<Reading>());
        }

        await writer.CompleteAsync(CancellationToken.None);
        return path;
    }
}
