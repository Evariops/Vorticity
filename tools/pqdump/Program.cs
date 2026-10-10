// pqdump prints what a Parquet file holds, from the public surface alone: it is also the executable
// CI publishes as Native AOT and runs over Parquet files, which a library cannot do to prove itself
// trim-clean.
//
// Output is plain text on stdout and diagnostics on stderr, so `pqdump f.parquet | diff -` works, and
// every number is formatted with the invariant culture.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Parquet;

namespace Vorticity.Tools.PqDump;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine(
                """
                pqdump - inspect a Parquet file.

                  pqdump <file.parquet> [options]

                  --schema      the leaf columns: types as the standard spells them, levels, field ids (default)
                  --row-groups  each row group's column chunks: codec, encodings, values and bytes
                  --stats       each column chunk's statistics, and the structures beside it
                  --kv          the key-value metadata
                  --all         all of the above
                  --scan        read every batch and report rows and batches
                  --verify      hold what the file says of itself to its rows; exits 1 on a finding

                exits 3 on a file this build does not read, 4 on a malformed one.
                """);
            return args.Length == 0 ? 2 : 0;
        }

        HashSet<string> options = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < args.Length; i++)
        {
            if (args[i] is not ("--schema" or "--row-groups" or "--stats" or "--kv" or "--all" or "--scan" or "--verify"))
            {
                Console.Error.WriteLine($"pqdump: unknown option '{args[i]}'.");
                return 2;
            }

            options.Add(args[i]);
        }

        bool all = options.Contains("--all");
        bool none = options.Count == 0;
        try
        {
            await using ParquetFile file = await ParquetFile.OpenAsync(args[0], CancellationToken.None).ConfigureAwait(false);
            ParquetMetadata metadata = file.Metadata;
            StringBuilder output = new StringBuilder();
            Line(output, $"file        {args[0]}");
            Line(output, $"created by  {metadata.CreatedBy ?? "(none)"}");
            Line(output, $"version     {metadata.Version}");
            Line(output, $"rows        {metadata.RowCount}");
            Line(output, $"row groups  {metadata.RowGroups.Count}");
            if (all || none || options.Contains("--schema"))
            {
                Schema(output, metadata);
            }

            if (all || options.Contains("--row-groups"))
            {
                RowGroups(output, metadata);
            }

            if (all || options.Contains("--stats"))
            {
                Statistics(output, metadata);
            }

            if (all || options.Contains("--kv"))
            {
                KeyValues(output, metadata);
            }

            Console.Out.Write(output.ToString());
            if (options.Contains("--scan"))
            {
                await ScanAsync(file).ConfigureAwait(false);
            }

            if (options.Contains("--verify"))
            {
                return await VerifyAsync(file).ConfigureAwait(false);
            }

            return 0;
        }
        catch (ParquetUnsupportedException e)
        {
            Console.Error.WriteLine($"pqdump: unsupported: {e.Message}");
            return 3;
        }
        catch (ParquetFormatException e)
        {
            Console.Error.WriteLine($"pqdump: malformed: {e.Message}");
            return 4;
        }
        catch (Exception e) when (e is System.IO.IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"pqdump: {e.GetType().Name}: {e.Message}");
            return 1;
        }
    }

    private static void Schema(StringBuilder output, ParquetMetadata metadata)
    {
        Line(output, string.Empty);
        Line(output, "columns");
        foreach (ParquetColumnInfo column in metadata.Columns)
        {
            string physical = column.TypeLength > 0 ? $"{column.PhysicalType}({column.TypeLength})" : column.PhysicalType;
            string field = column.FieldId is { } id ? $" field {id}" : string.Empty;
            Line(output, $"  {column.Path}: {physical} {column.LogicalType ?? "-"} def {column.MaxDefinitionLevel} rep {column.MaxRepetitionLevel} {column.ColumnOrder}{field} as {column.Type}");
        }
    }

    private static void RowGroups(StringBuilder output, ParquetMetadata metadata)
    {
        foreach (ParquetRowGroupInfo group in metadata.RowGroups)
        {
            Line(output, string.Empty);
            Line(output, $"row group {group.Ordinal}: rows {group.FirstRow} to {group.FirstRow + group.RowCount}");
            foreach (ParquetChunkInfo chunk in group.Chunks)
            {
                Line(output, $"  {chunk.Column}: {chunk.Codec} [{string.Join(", ", chunk.Encodings)}] {chunk.Values} values, {chunk.CompressedBytes} bytes of {chunk.UncompressedBytes} at {chunk.Offset}");
            }
        }
    }

    private static void Statistics(StringBuilder output, ParquetMetadata metadata)
    {
        foreach (ParquetRowGroupInfo group in metadata.RowGroups)
        {
            Line(output, string.Empty);
            Line(output, $"row group {group.Ordinal} statistics");
            foreach (ParquetChunkInfo chunk in group.Chunks)
            {
                string beside = string.Join(
                    ' ',
                    chunk.HasColumnIndex ? "column-index" : string.Empty,
                    chunk.HasOffsetIndex ? "offset-index" : string.Empty,
                    chunk.HasBloomFilter ? "bloom" : string.Empty,
                    chunk.AllDataPagesDictionary == true ? "all-codes" : string.Empty).Trim();
                if (chunk.Statistics is not { } statistics)
                {
                    Line(output, $"  {chunk.Column}: none {beside}");
                }
                else
                {
                    string min = statistics.Min is { } low ? (statistics.MinExact ? low : low + " (bound)") : "-";
                    string max = statistics.Max is { } high ? (statistics.MaxExact ? high : high + " (bound)") : "-";
                    string nulls = statistics.NullCount?.ToString(CultureInfo.InvariantCulture) ?? "?";
                    string nans = statistics.NanCount is { } count ? $" nan {count}" : string.Empty;
                    Line(output, $"  {chunk.Column}: min {min} max {max} nulls {nulls}{nans} {beside}");
                }

                if (chunk.Geospatial is { } geospatial)
                {
                    string types = geospatial.Types.Count == 0 ? "unknown" : string.Join(',', geospatial.Types);
                    string box = geospatial.Box is not { } b ? "none"
                        : string.Create(CultureInfo.InvariantCulture, $"x [{b.XMin:R}, {b.XMax:R}] y [{b.YMin:R}, {b.YMax:R}]")
                            + (b.ZMin is { } zmin ? string.Create(CultureInfo.InvariantCulture, $" z [{zmin:R}, {b.ZMax:R}]") : string.Empty)
                            + (b.MMin is { } mmin ? string.Create(CultureInfo.InvariantCulture, $" m [{mmin:R}, {b.MMax:R}]") : string.Empty);
                    Line(output, $"    box {box} types {types}");
                }
            }
        }
    }

    private static void KeyValues(StringBuilder output, ParquetMetadata metadata)
    {
        Line(output, string.Empty);
        Line(output, "key-value metadata");
        foreach (KeyValuePair<string, string?> pair in metadata.KeyValues)
        {
            string value = pair.Value is null ? "(none)" : pair.Value.Length > 120 ? pair.Value[..120] + "..." : pair.Value;
            Line(output, $"  {pair.Key} = {value}");
        }
    }

    private static async Task ScanAsync(ParquetFile file)
    {
        long rows = 0;
        int batches = 0;
        await foreach (RecordBatch batch in file.Scan().ToBatchesAsync(CancellationToken.None).ConfigureAwait(false))
        {
            using (batch)
            {
                rows += batch.RowCount;
                batches++;
            }
        }

        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"scan        {rows} rows in {batches} batches"));
    }

    private static async Task<int> VerifyAsync(ParquetFile file)
    {
        IReadOnlyList<ParquetFinding> findings = await file.VerifyAsync(CancellationToken.None).ConfigureAwait(false);
        foreach (ParquetFinding finding in findings)
        {
            Console.Out.WriteLine($"finding     {finding}");
        }

        Console.Out.WriteLine(string.Create(CultureInfo.InvariantCulture, $"verify      {findings.Count} findings"));
        return findings.Count == 0 ? 0 : 1;
    }

    private static void Line(StringBuilder output, FormattableString line) =>
        output.Append(line.ToString(CultureInfo.InvariantCulture)).Append('\n');

    private static void Line(StringBuilder output, string line) => output.Append(line).Append('\n');
}
