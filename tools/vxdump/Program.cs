// vxdump prints what a Vortex file holds, from the public surface alone: it is also the executable a
// test publishes as Native AOT and runs over the corpus, which a library cannot do to prove itself
// trim-clean.
//
// Output is plain text on stdout and diagnostics on stderr, so `vxdump f.vortex | diff -` works, and
// every number is formatted with the invariant culture.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;

namespace Vorticity.Tools.VxDump;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Console.Error.WriteLine(
                """
                vxdump - inspect a Vortex file.

                  vxdump <file.vortex> [options]

                  --layout      the layout tree, with each flat node's array encoding (default)
                  --schema      the file's columns
                  --encodings   the array and layout encodings the footer declares
                  --segments    every segment's offset, length and alignment
                  --stats       the file's statistics, when it carries them
                  --all         all of the above
                  --scan        read every batch and report rows and batches
                  --indexes     the file's indexes: kind, column, runs, blocks, entries and bytes
                  --explain E   the plan of a scan filtered by E, e.g. "id >= 10 and name = 'x'"
                  --fragment P  add the index fragment in file P to the file's own indexes (repeatable)
                  --verify      check every index region against its checksum, and a fragment's
                                record of the file's hash against the file
                  --repair      truncate a torn append back to the last valid file
                """);
            return args.Length == 0 ? 2 : 0;
        }

        string path = args[0];
        if (args.AsSpan(1).Contains("--repair"))
        {
            return await RepairAsync(path).ConfigureAwait(false);
        }

        if (Sections.Parse(args.AsSpan(1)) is not { } sections)
        {
            return 2;
        }

        try
        {
            ImmutableArray<IndexFragment>.Builder fragments = ImmutableArray.CreateBuilder<IndexFragment>();
            foreach (string fragment in sections.Fragments)
            {
                fragments.Add(new IndexFragment(await System.IO.File.ReadAllBytesAsync(fragment).ConfigureAwait(false), []));
            }

            VortexOpenOptions open = new VortexOpenOptions { IndexFragments = fragments.ToImmutable() };
            VortexFile file = await VortexSession.Default.OpenAsync(path, open).ConfigureAwait(false);
            await using (file.ConfigureAwait(false))
            {
                StringBuilder output = new StringBuilder();
                Header(output, path, file);
                if (sections.Schema)
                {
                    Schema(output, file);
                }

                if (sections.Encodings)
                {
                    Encodings(output, file);
                }

                if (sections.Layout)
                {
                    output.Append("\nlayout\n");
                    Layout(output, await file.GetLayoutAsync().ConfigureAwait(false), depth: 1);
                }

                if (sections.Segments)
                {
                    Segments(output, file);
                }

                if (sections.Stats)
                {
                    Statistics(output, file);
                }

                if (sections.Indexes)
                {
                    await IndexesAsync(output, file).ConfigureAwait(false);
                }

                if (sections.Explain is { } expression)
                {
                    await ExplainAsync(output, file, expression).ConfigureAwait(false);
                }

                bool verified = !sections.Verify || await VerifyAsync(output, file).ConfigureAwait(false);
                Console.Out.Write(output.ToString());
                if (!verified)
                {
                    return 6;
                }

                if (sections.Scan)
                {
                    await ScanAsync(file).ConfigureAwait(false);
                }
            }

            return 0;
        }
        catch (VortexUnsupportedException error)
        {
            // The id and the kind together are what the edition tables answer.
            Console.Error.WriteLine($"unsupported {error.Kind.ToString().ToLowerInvariant()}: {error.Message}");
            return 3;
        }
        catch (VortexFormatException error)
        {
            Console.Error.WriteLine($"malformed: {error.Message}");
            return 4;
        }
        catch (System.IO.IOException error)
        {
            Console.Error.WriteLine($"io: {error.Message}");
            return 5;
        }
        catch (Exception error) when (error is FormatException or ArgumentException or VortexSchemaException)
        {
            Console.Error.WriteLine($"vxdump: {error.Message}");
            return 2;
        }
    }

    private static void Header(StringBuilder output, string path, VortexFile file)
    {
        output.Append("file      ").Append(path).Append('\n')
            .Append("edition   ").Append(VortexEditions.Name(file.Edition)).Append('\n')
            .Append("bytes     ").Append(Text(file.Length)).Append('\n')
            .Append("rows      ").Append(Text(file.RowCount)).Append('\n')
            .Append("tabular   ").Append(IsTabular(file.Schema) ? "yes" : "no").Append('\n')
            .Append("identity  ").Append(file.Identity == Guid.Empty ? "none" : file.Identity.ToString("N")).Append('\n');
        if (file.TornTail is { } torn)
        {
            output.Append("torn      ").Append(Text(torn.FileLength - torn.ValidLength))
                .Append(" bytes after the last whole version, of ").Append(Text(torn.FileLength))
                .Append(" (").Append(torn.Reason).Append("); --repair truncates them\n");
        }
    }

    private static bool IsTabular(VortexSchema schema) => !(schema.Count == 1 && schema[0].Name.Length == 0);

    private static void Schema(StringBuilder output, VortexFile file)
    {
        output.Append("\nschema\n");
        if (!IsTabular(file.Schema))
        {
            output.Append("  ").Append(file.Schema[0].Type.ToString()).Append('\n');
            return;
        }

        foreach (VortexField field in file.Schema)
        {
            output.Append("  ").Append(field.Name).Append(": ").Append(field.Type.ToString()).Append('\n');
        }
    }

    private static void Encodings(StringBuilder output, VortexFile file)
    {
        output.Append("\narray encodings\n");
        Components(output, file.ArrayEncodings, "   [not decoded by this build]");
        output.Append("\nlayout encodings\n");
        Components(output, file.LayoutEncodings, "   [not read by this build]");
    }

    private static void Components(StringBuilder output, ImmutableArray<VortexComponent> components, string unsupported)
    {
        for (int i = 0; i < components.Length; i++)
        {
            output.Append("  ").Append(Text(i)).Append(' ').Append(components[i].Id)
                .Append(components[i].Supported ? string.Empty : unsupported).Append('\n');
        }
    }

    private static void Layout(StringBuilder output, VortexLayout node, int depth)
    {
        output.Append(' ', depth * 2).Append(node.Encoding).Append("  rows=").Append(Text(node.RowCount));
        if (!node.Segments.IsEmpty)
        {
            output.Append("  segments=[");
            for (int i = 0; i < node.Segments.Length; i++)
            {
                output.Append(i == 0 ? string.Empty : ",").Append(Text(node.Segments[i]));
            }

            output.Append(']');
        }

        if (node.ZoneCount > 0)
        {
            output.Append("  zones=").Append(Text(node.ZoneCount)).Append('x').Append(Text(node.ZoneLength))
                .Append(node.ZonesUsable ? string.Empty : " (unusable)");
        }

        if (node.Type is { } type)
        {
            output.Append("  dtype=").Append(type.ToString());
        }

        if (node.ArrayEncoding is { } encoding)
        {
            output.Append("  encoding=").Append(encoding);
        }

        output.Append('\n');
        foreach (VortexLayout child in node.Children)
        {
            Layout(output, child, depth + 1);
        }
    }

    private static void Segments(StringBuilder output, VortexFile file)
    {
        output.Append("\nsegments\n");
        ImmutableArray<VortexSegment> segments = file.SegmentMap;
        for (int i = 0; i < segments.Length; i++)
        {
            output.Append("  ").Append(Text(i))
                .Append("  offset=").Append(Text(segments[i].Offset))
                .Append("  length=").Append(Text(segments[i].Length))
                .Append("  align=").Append(Text(segments[i].Alignment))
                .Append('\n');
        }
    }

    private static void Statistics(StringBuilder output, VortexFile file)
    {
        output.Append("\nstatistics\n");
        VortexFileStatistics statistics = file.Statistics;
        if (statistics.Count == 0)
        {
            output.Append("  (none)\n");
            return;
        }

        for (int i = 0; i < statistics.Count; i++)
        {
            FieldStatistics field = statistics[i];
            output.Append("  ").Append(IsTabular(file.Schema) ? file.Schema[i].Name : "(root)")
                .Append(field.TryGetNullCount(out long nulls) ? "  nulls=" + Text(nulls) : string.Empty)
                .Append(Bounds(field, file.Schema[i].Type))
                .Append(field.TryGetIsSorted(out bool sorted) && sorted ? "  sorted" : string.Empty)
                .Append(field.TryGetIsConstant(out bool constant) && constant ? "  constant" : string.Empty)
                .Append('\n');
        }
    }

    /// <summary>The exact minimum and maximum, read as the .NET type the column maps to.</summary>
    private static string Bounds(FieldStatistics field, VortexType type)
    {
        VortexType t = type.NonNullable;
        return t switch
        {
            _ when t == VortexType.Bool => Pair<bool>(field),
            _ when t == VortexType.Int8 => Pair<sbyte>(field),
            _ when t == VortexType.Int16 => Pair<short>(field),
            _ when t == VortexType.Int32 => Pair<int>(field),
            _ when t == VortexType.Int64 => Pair<long>(field),
            _ when t == VortexType.UInt8 => Pair<byte>(field),
            _ when t == VortexType.UInt16 => Pair<ushort>(field),
            _ when t == VortexType.UInt32 => Pair<uint>(field),
            _ when t == VortexType.UInt64 => Pair<ulong>(field),
            _ when t == VortexType.Float16 => Pair<Half>(field),
            _ when t == VortexType.Float32 => Pair<float>(field),
            _ when t == VortexType.Float64 => Pair<double>(field),
            _ when t == VortexType.Utf8 => Pair<string>(field),
            _ when t.Kind == VortexTypeKind.Decimal => Pair<VortexDecimal>(field),
            _ when t == VortexType.Date => Pair<DateOnly>(field),
            _ when t.Kind == VortexTypeKind.Extension && t.StorageType is { } storage && storage.NonNullable == VortexType.Int64 => Pair<long>(field),
            _ when t.Kind == VortexTypeKind.Extension && t.StorageType is { } storage && storage.NonNullable == VortexType.Int32 => Pair<int>(field),
            _ => string.Empty,
        };
    }

    private static string Pair<T>(FieldStatistics field) =>
        (field.TryGetMin(out T? min) ? "  min=" + Format(min) : string.Empty) +
        (field.TryGetMax(out T? max) ? "  max=" + Format(max) : string.Empty);

    private static string Format<T>(T value) => value switch
    {
        string text => "'" + text + "'",
        bool flag => flag ? "true" : "false",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value?.ToString() ?? "null",
    };

    private static async Task IndexesAsync(StringBuilder output, VortexFile file)
    {
        output.Append("\nindexes\n");
        ImmutableArray<VortexIndexInfo> indexes = await file.GetIndexesAsync().ConfigureAwait(false);
        if (indexes.IsEmpty)
        {
            output.Append("  none\n");
            return;
        }

        foreach (VortexIndexInfo index in indexes)
        {
            output.Append("  ").Append(index.Kind)
                .Append("  column=").Append(index.Column.Length == 0 ? "(root)" : index.Column)
                .Append("  block=").Append(Text(index.BlockLength))
                .Append("  runs=").Append(Text(index.Runs))
                .Append("  blocks=").Append(Text(index.Blocks))
                .Append("  entries=").Append(Text(index.Entries))
                .Append("  listed-bytes=").Append(Text(index.ListedBytes))
                .Append("  layout=").Append(index.Layout.ToString())
                .Append('\n');
        }
    }

    private static async Task ExplainAsync(StringBuilder output, VortexFile file, string expression)
    {
        VortexExpr filter = VortexExpr.Parse(expression);
        ScanPlan plan = await file.Scan().Where(filter).ExplainAsync().ConfigureAwait(false);
        output.Append("\nexplain   ").Append(filter.ToString()).Append('\n')
            .Append("  may match        ").Append(plan.MayMatch ? "yes" : "no").Append('\n')
            .Append("  rows             ").Append(Text(plan.Rows)).Append('\n')
            .Append("  blocks           ").Append(Text(plan.LiveBlocks)).Append(" live of ").Append(Text(plan.Blocks)).Append('\n');
        foreach (PruningStep step in plan.Pruning)
        {
            output.Append("    ").Append(step.Structure).Append(": ").Append(Text(step.BlocksPruned))
                .Append(" pruned, ").Append(Text(step.SegmentsRead)).Append(" segments / ")
                .Append(Text(step.BytesRead)).Append(" bytes read\n");
        }

        output.Append("  to read          ").Append(Text(plan.Segments)).Append(" segments, ")
            .Append(Text(plan.BytesToRead)).Append(" bytes of ").Append(Text(file.Length)).Append('\n');
        CountPlan count = plan.Count;
        output.Append("  count tiers      ")
            .Append(count.Exact ? "exact (" + Text(count.Rows) + "), " : string.Empty)
            .Append(Text(count.Pruned)).Append(" pruned, ")
            .Append(Text(count.Proven)).Append(" proven, ")
            .Append(Text(count.Decoded)).Append(" decoded\n");
        output.Append("  count            ").Append(Text(await file.Scan().Where(filter).CountAsync().ConfigureAwait(false))).Append('\n');
    }

    private static async Task<bool> VerifyAsync(StringBuilder output, VortexFile file)
    {
        output.Append("\nverify\n");
        VortexIndexVerification verified = await file.VerifyIndexesAsync().ConfigureAwait(false);
        foreach (string torn in verified.Torn)
        {
            output.Append("  torn      ").Append(torn).Append('\n');
        }

        output.Append("  regions   ").Append(Text(verified.Held)).Append(" hold their checksums, ")
            .Append(Text(verified.Torn.Count)).Append(" do not, ").Append(Text(verified.Bare)).Append(" carry none")
            .Append(" (the pages and nodes under them are checked as they are read)\n");
        output.Append("  file hash ").Append(verified.FileHashHolds switch
        {
            null => "no fragment records one",
            true => "matches every fragment's record",
            false => "is not a fragment's record: the file changed under its identity",
        }).Append('\n');
        return verified.Holds;
    }

    private static async Task ScanAsync(VortexFile file)
    {
        long rows = 0;
        long batches = 0;
        await foreach (BatchView batch in file.Scan())
        {
            rows += batch.RowCount;
            batches++;
        }

        Console.Out.Write("\nscan\n  batches=" + Text(batches) + "\n  rows=" + Text(rows) + "\n");
    }

    private static async Task<int> RepairAsync(string path)
    {
        try
        {
            VortexRepairResult result = await VortexFileRepair.RepairAsync(path).ConfigureAwait(false);
            Console.Out.WriteLine(result.Truncated
                ? $"repaired  {path}: {Text(result.OriginalLength)} -> {Text(result.Length)} bytes ({Text(result.OriginalLength - result.Length)} torn bytes removed)"
                : $"valid     {path}: {Text(result.Length)} bytes, nothing to repair");
            return 0;
        }
        catch (VortexFormatException error)
        {
            Console.Error.WriteLine($"malformed: {error.Message}");
            return 4;
        }
        catch (System.IO.IOException error)
        {
            Console.Error.WriteLine($"io: {error.Message}");
            return 5;
        }
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private readonly record struct Sections(
        bool Schema, bool Encodings, bool Layout, bool Segments, bool Stats, bool Scan,
        bool Indexes, string? Explain, IReadOnlyList<string> Fragments, bool Verify)
    {
        /// <summary>The sections asked for, or null, the option named on stderr, for an option vxdump does not know.</summary>
        internal static Sections? Parse(ReadOnlySpan<string> args)
        {
            bool schema = false, encodings = false, layout = false, segments = false, stats = false;
            bool scan = false, indexes = false, verify = false, any = false;
            string? explain = null;
            List<string> fragments = [];
            for (int i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--indexes": indexes = any = true; break;
                    case "--fragment" when i + 1 < args.Length: fragments.Add(args[++i]); break;
                    case "--verify": verify = any = true; break;
                    case "--explain" when i + 1 < args.Length: explain = args[++i]; any = true; break;
                    case "--schema": schema = any = true; break;
                    case "--encodings": encodings = any = true; break;
                    case "--layout": layout = any = true; break;
                    case "--segments": segments = any = true; break;
                    case "--stats": stats = any = true; break;
                    case "--scan": scan = any = true; break;
                    case "--all": schema = encodings = layout = segments = stats = any = true; break;
                    default:
                        Console.Error.WriteLine($"vxdump: unknown option '{args[i]}'; --help lists them");
                        return null;
                }
            }

            return any
                ? new Sections(schema, encodings, layout, segments, stats, scan, indexes, explain, fragments, verify)
                : new Sections(false, false, true, false, false, false, false, null, fragments, false);
        }
    }
}
