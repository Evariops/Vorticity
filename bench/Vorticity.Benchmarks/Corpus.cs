// Locating the benchmark inputs.
//
// The data is the conformance corpus, deliberately. Upstream's own datasets would make numbers
// comparable with published Vortex figures, and those are gigabytes that do not belong in a
// repository -- but the corpus is here, it was written by the Rust writer with real distributions,
// and "read identical bytes with both implementations" is the property that makes a comparison
// honest whatever the bytes are. When the TPC-H and ClickBench inputs are wired up, they come
// through the same seam: a path, resolved once.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Types;

namespace Vorticity.Benchmarks;

/// <summary>Resolves benchmark input files.</summary>
internal static class Corpus
{
    private static readonly Lazy<string> RootLazy = new Lazy<string>(Locate);

    /// <summary>The corpus directory.</summary>
    internal static string Root => RootLazy.Value;

    /// <summary>The path of one corpus entry.</summary>
    /// <param name="id">e.g. <c>distributions/high_cardinality_i64_r8193</c>.</param>
    internal static string Path(string id) =>
        System.IO.Path.Combine(Root, id.Replace('/', System.IO.Path.DirectorySeparatorChar) + ".vortex");

    /// <summary>
    /// An override for a real dataset, when one is present.
    /// </summary>
    /// <param name="variable">The environment variable naming it.</param>
    /// <param name="fallback">The corpus entry to use when the variable is unset.</param>
    internal static string Dataset(string variable, string fallback)
    {
        string? path = Environment.GetEnvironmentVariable(variable);
        return string.IsNullOrEmpty(path) ? Path(fallback) : path;
    }

    /// <summary>The shape a benchmark needs from its input, read from the file itself.</summary>
    /// <param name="Rows">Rows in the file, from the footer.</param>
    /// <param name="First">The column's first value.</param>
    /// <param name="Step">The difference between its first two values, at least 1.</param>
    internal readonly record struct ColumnShape(long Rows, long First, long Step);

    /// <summary>
    /// Reads the shape of one integer column, refusing a file that does not have it.
    /// </summary>
    /// <param name="path">The file, which may have come from an environment variable.</param>
    /// <param name="field">The column the benchmark reads.</param>
    /// <param name="variable">The variable that could have redirected it, for the message.</param>
    /// <returns>Its row count and the first two values of <paramref name="field"/>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The column is missing, is not an integer, or the file has fewer than two rows.
    /// </exception>
    /// <remarks>
    /// Seven classes honour `VORTICITY_BENCH_DATA` while hard-wiring `Rows = 65_536`, a base
    /// of 1 000 000 and a step of 3 -- all of them properties of ONE file. Pointing the variable
    /// elsewhere measured an empty band or asked for rows past the end, and said nothing: the
    /// trap of a filter band, `[1 000, 1 100)`, that matched no row at all. What can be derived
    /// is derived here, from the file in front of us; what cannot is refused by name.
    /// </remarks>
    internal static ColumnShape RequireIntegerColumn(string path, string field, string variable)
    {
        return RequireAsync(path, field, variable).GetAwaiter().GetResult();
    }

    private static async Task<ColumnShape> RequireAsync(string path, string field, string variable)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        long rows = file.RowCount;
        if (rows < 2)
        {
            throw new InvalidOperationException(
                $"{path} has {rows} row(s); this benchmark needs at least two to read a step. " +
                $"Unset {variable} to use the corpus file it was written for.");
        }

        // A FILE WITHOUT THAT COLUMN FAILS HERE, BY NAME. `Project` throws its own
        // `ArgumentException` -- "a projection on a non-struct root may only be Projection.All" --
        // which is true and tells the reader nothing about which variable put them there.
        IAsyncEnumerable<RecordBatch> batches;
        try
        {
            batches = file.Scan().Project(field).ExecuteAsync();
        }
        catch (ArgumentException e)
        {
            throw new InvalidOperationException(
                $"{path} has no column '{field}' to read ({e.Message.Split('.')[0]}). Unset " +
                $"{variable} to use the corpus file this benchmark was written for.", e);
        }

        await foreach (RecordBatch batch in batches.WithCancellation(CancellationToken.None))
        {
            VortexColumn column = batch.IsTabular ? batch.Column(0) : batch.Root;
            if (column.Kind != CanonicalKind.Primitive || !column.DType.PType.IsInteger())
            {
                throw new InvalidOperationException(
                    $"{path}: column '{field}' is {column.Kind}/{column.DType.PType}, and this " +
                    $"benchmark needs an integer column. Unset {variable} to use the corpus file " +
                    "it was written for.");
            }

            if (batch.RowCount < 2)
            {
                continue;
            }

            long first = Read(column, 0);
            long second = Read(column, 1);
            return new ColumnShape(rows, first, Math.Max(1, second - first));
        }

        throw new InvalidOperationException(
            $"{path}: column '{field}' produced no batch with two rows. Unset {variable} to use " +
            "the corpus file this benchmark was written for.");
    }

    /// <summary>One integer value, whatever width the column stores it at.</summary>
    private static long Read(VortexColumn column, int row) => column.DType.PType switch
    {
        PType.U8 => column.AsPrimitive<byte>()[row],
        PType.I8 => column.AsPrimitive<sbyte>()[row],
        PType.U16 => column.AsPrimitive<ushort>()[row],
        PType.I16 => column.AsPrimitive<short>()[row],
        PType.U32 => column.AsPrimitive<uint>()[row],
        PType.I32 => column.AsPrimitive<int>()[row],
        PType.U64 => (long)column.AsPrimitive<ulong>()[row],
        _ => column.AsPrimitive<long>()[row],
    };

    private static string Locate()
    {
        // Walk up from the assembly rather than the working directory: BenchmarkDotNet runs the
        // generated harness from a directory of its own choosing.
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = System.IO.Path.Combine(
                directory.FullName, "tests", "Vorticity.Conformance", "corpus");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate tests/Vorticity.Conformance/corpus above " + AppContext.BaseDirectory);
    }
}
