// vxdump - docs/01-scope.md F12: "Dump the layout/encoding tree (equivalent of `display_tree`) --
// indispensable for debugging and cross-testing".
//
// It exists for three jobs, and the third is the one that shapes the project file rather than this
// one: it is what docs/03-architecture.md §4 invariant 5 publishes as Native AOT and runs over the
// corpus. A library cannot prove it is AOT-clean; an executable that actually opens a file and
// walks its layout tree can, because every reflection-shaped mistake shows up as a trim warning at
// publish or a failure at run.
//
// Output is plain text on stdout and diagnostics on stderr, so `vxdump f.vortex | diff -` works.
// Nothing here is culture-sensitive: InvariantGlobalization is set in the project file and every
// number below is formatted with the invariant culture explicitly, because a build that lost the
// property should still produce identical bytes on a French machine.
using System;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Serialization.Schemas;
using Vorticity.Scan;
using Vorticity.Types;

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

                  --layout      the layout tree (default)
                  --schema      the file's dtype, one line per root field
                  --encodings   the array and layout encoding dictionaries
                  --segments    every segment's offset, length and alignment
                  --stats       file-level statistics, when the file carries them
                  --all         all of the above
                  --scan        read every batch and report rows, batches and null counts
                """);
            return args.Length == 0 ? 2 : 0;
        }

        string path = args[0];
        Sections sections = Sections.Parse(args.AsSpan(1));

        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
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
                Layout(output, file);
            }

            if (sections.Segments)
            {
                Segments(output, file);
            }

            if (sections.Stats)
            {
                Statistics(output, file);
            }

            Console.Out.Write(output.ToString());

            if (sections.Scan)
            {
                await Scan(file).ConfigureAwait(false);
            }

            return 0;
        }
        catch (VortexUnsupportedException error)
        {
            // The one message a user acts on: the id AND the kind, which is what upstream's own
            // documentation needs to answer "which edition, which minimum version".
            Console.Error.WriteLine($"unsupported: {error.Message}");
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
    }

    private static void Header(StringBuilder output, string path, VortexFile file)
    {
        output.Append("file      ").Append(path).Append('\n')
            .Append("version   ").Append(Text(file.FormatVersion)).Append('\n')
            .Append("bytes     ").Append(Text(file.FileLength)).Append('\n')
            .Append("rows      ").Append(Text(file.RowCount)).Append('\n')
            .Append("tabular   ").Append(file.IsTabular ? "yes" : "no").Append('\n');
    }

    private static void Schema(StringBuilder output, VortexFile file)
    {
        output.Append("\nschema\n");
        DType schema = file.Schema;
        if (schema.Kind != DTypeKind.Struct)
        {
            output.Append("  ").Append(schema.ToString()).Append('\n');
            return;
        }

        for (int i = 0; i < schema.FieldCount; i++)
        {
            output.Append("  ").Append(schema.GetFieldName(i)).Append(": ")
                .Append(schema.GetField(i).ToString()).Append('\n');
        }
    }

    private static void Encodings(StringBuilder output, VortexFile file)
    {
        output.Append("\narray encodings\n");
        for (int i = 0; i < file.ArrayEncodingCount; i++)
        {
            // An id the build does not implement is printed with a marker rather than omitted:
            // "which component is missing" is the question this tool exists to answer.
            bool known = file.GetArrayEncoding(i) != ArrayEncodingId.Unknown;
            output.Append("  ").Append(Text(i)).Append(' ')
                .Append(file.GetArrayEncodingId(i))
                .Append(known ? string.Empty : "   [not decoded by this build]")
                .Append('\n');
        }

        output.Append("\nlayout encodings\n");
        for (int i = 0; i < file.LayoutEncodingCount; i++)
        {
            bool known = file.GetLayoutEncoding(i) != LayoutEncodingId.Unknown;
            output.Append("  ").Append(Text(i)).Append(' ')
                .Append(file.GetLayoutEncodingId(i))
                .Append(known ? string.Empty : "   [not read by this build]")
                .Append('\n');
        }
    }

    private static void Layout(StringBuilder output, VortexFile file)
    {
        output.Append("\nlayout\n");
        LayoutTree tree = LayoutTree.Parse(file);
        Node(output, tree.Root, depth: 1);
    }

    private static void Node(StringBuilder output, LayoutNode node, int depth)
    {
        output.Append(' ', depth * 2)
            .Append(node.EncodingIdText)
            .Append("  rows=").Append(Text(node.RowCount));

        ReadOnlySpan<uint> segments = node.Segments;
        if (segments.Length != 0)
        {
            output.Append("  segments=[");
            for (int i = 0; i < segments.Length; i++)
            {
                if (i != 0)
                {
                    output.Append(',');
                }

                output.Append(Text(segments[i]));
            }

            output.Append(']');
        }

        if (node.TryGetZoneMap(out ZoneMap zones))
        {
            output.Append("  zones=").Append(Text(zones.ZoneCount))
                .Append('x').Append(Text(zones.ZoneLength))
                .Append(zones.IsPruningAvailable ? string.Empty : " (unusable)");
        }

        if (!node.DType.IsDefault)
        {
            output.Append("  dtype=").Append(node.DType.ToString());
        }

        output.Append('\n');

        for (int i = 0; i < node.ChildCount; i++)
        {
            Node(output, node.GetChild(i), depth + 1);
        }
    }

    private static void Segments(StringBuilder output, VortexFile file)
    {
        output.Append("\nsegments\n");
        ReadOnlySpan<SegmentSpec> specs = file.SegmentSpecs;
        for (int i = 0; i < specs.Length; i++)
        {
            ref readonly SegmentSpec spec = ref specs[i];
            output.Append("  ").Append(Text(i))
                .Append("  offset=").Append(Text(spec.Offset))
                .Append("  length=").Append(Text(spec.Length))
                .Append("  align=1<<").Append(Text(spec.AlignmentExponent))
                .Append('\n');
        }
    }

    private static void Statistics(StringBuilder output, VortexFile file)
    {
        output.Append("\nstatistics\n");
        if (!file.HasFileStatistics)
        {
            output.Append("  (none)\n");
            return;
        }

        FileStatistics stats = file.Statistics;

        for (int i = 0; i < stats.FieldCount; i++)
        {
            FieldStatistics field = stats.GetField(i);
            output.Append("  field ").Append(Text(i))
                .Append(field.TryGetNullCount(out ulong nulls) ? "  nulls=" + Text(nulls) : string.Empty)
                .Append(field.HasMin ? "  min=set" : string.Empty)
                .Append(field.HasMax ? "  max=set" : string.Empty)
                .Append('\n');
        }
    }

    private static async Task Scan(VortexFile file)
    {
        long rows = 0;
        long batches = 0;
        await foreach (Vorticity.Columns.RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            rows += batch.RowCount;
            batches++;
        }

        Console.Out.Write(
            "\nscan\n  batches=" + Text(batches) + "\n  rows=" + Text(rows) + "\n");
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private readonly struct Sections
    {
        private Sections(bool schema, bool encodings, bool layout, bool segments, bool stats, bool scan)
        {
            Schema = schema;
            Encodings = encodings;
            Layout = layout;
            Segments = segments;
            Stats = stats;
            Scan = scan;
        }

        internal bool Schema { get; }

        internal bool Encodings { get; }

        internal bool Layout { get; }

        internal bool Segments { get; }

        internal bool Stats { get; }

        internal bool Scan { get; }

        internal static Sections Parse(ReadOnlySpan<string> args)
        {
            bool schema = false;
            bool encodings = false;
            bool layout = false;
            bool segments = false;
            bool stats = false;
            bool scan = false;
            bool any = false;

            foreach (string arg in args)
            {
                switch (arg)
                {
                    case "--schema": schema = any = true; break;
                    case "--encodings": encodings = any = true; break;
                    case "--layout": layout = any = true; break;
                    case "--segments": segments = any = true; break;
                    case "--stats": stats = any = true; break;
                    case "--scan": scan = any = true; break;
                    case "--all":
                        schema = encodings = layout = segments = stats = any = true;
                        break;
                    default:
                        Console.Error.WriteLine($"vxdump: unknown option '{arg}'");
                        break;
                }
            }

            // No section asked for means the layout tree, which is what F12 names.
            return any
                ? new Sections(schema, encodings, layout, segments, stats, scan)
                : new Sections(false, false, true, false, false, false);
        }
    }
}
