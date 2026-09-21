// vxdump - dumps the layout and encoding tree, the equivalent of `display_tree`: indispensable for
// debugging and cross-testing.
//
// It exists for three jobs, and the third is the one that shapes the project file rather than this
// one: it is the executable a test publishes as Native AOT and runs over the
// corpus. A library cannot prove it is AOT-clean; an executable that actually opens a file and
// walks its layout tree can, because every reflection-shaped mistake shows up as a trim warning at
// publish or a failure at run.
//
// Output is plain text on stdout and diagnostics on stderr, so `vxdump f.vortex | diff -` works.
// Nothing here is culture-sensitive: InvariantGlobalization is set in the project file and every
// number below is formatted with the invariant culture explicitly, because a build that lost the
// property should still produce identical bytes on a French machine.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.RowEncoding;
using Vorticity.Serialization.Schemas;
using Vorticity.Scanning;
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

                  --layout      the layout tree, with each node's array encoding (default)
                  --schema      the file's dtype, one line per root field
                  --encodings   the array and layout encoding dictionaries
                  --segments    every segment's offset, length and alignment
                  --stats       file-level statistics, when the file carries them
                  --all         all of the above
                  --scan        read every batch and report rows, batches and null counts
                  --row-keys    row-encode every batch and report the keys (experimental format)
                  --indexes     the index directory: policy, entries, runs and their bytes
                  --explain E   the plan of a scan filtered by E, e.g. "id >= 10 and name = 'x'"
                  --fragment P  add the index fragment in file P to the file's own indexes
                                (repeatable; a fragment of a dataset, docs/design/13-dataset.md)
                  --verify      check every listed index region against its checksum, and a
                                fragment's record of the file's XXH3-128 against the file
                  --repair      truncate a torn append back to the last valid file
                """);
            return args.Length == 0 ? 2 : 0;
        }

        string path = args[0];
        if (args.AsSpan(1).Contains("--repair"))
        {
            return await Repair(path).ConfigureAwait(false);
        }

        Sections sections = Sections.Parse(args.AsSpan(1));

        try
        {
            List<ReadOnlyMemory<byte>> fragments = [];
            foreach (string fragment in sections.Fragments)
            {
                fragments.Add(await System.IO.File.ReadAllBytesAsync(fragment).ConfigureAwait(false));
            }

            VortexOpenOptions open = fragments.Count > 0
                ? new VortexOpenOptions { Read = new VortexReadOptions { IndexFragments = fragments } }
                : VortexOpenOptions.Default;
            await using VortexFile file = await VortexFile.OpenAsync(path, open, CancellationToken.None);
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
                await Layout(output, file).ConfigureAwait(false);
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
                await Indexes(output, file).ConfigureAwait(false);
            }

            if (sections.Explain is { } expression)
            {
                await Explain(output, file, expression).ConfigureAwait(false);
            }

            bool verified = !sections.Verify || await Verify(output, file).ConfigureAwait(false);
            Console.Out.Write(output.ToString());
            if (!verified)
            {
                return 6;
            }

            if (sections.Scan)
            {
                await Scan(file).ConfigureAwait(false);
            }

            if (sections.RowKeys)
            {
                await RowKeysSection(file).ConfigureAwait(false);
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
        catch (Exception error) when (error is FormatException or ArgumentException)
        {
            Console.Error.WriteLine($"vxdump: {error.Message}");
            return 2;
        }
    }

    private static void Header(StringBuilder output, string path, VortexFile file)
    {
        output.Append("file      ").Append(path).Append('\n')
            .Append("version   ").Append(Text(file.FormatVersion)).Append('\n')
            .Append("bytes     ").Append(Text(file.FileLength)).Append('\n')
            .Append("rows      ").Append(Text(file.RowCount)).Append('\n')
            .Append("tabular   ").Append(file.IsTabular ? "yes" : "no").Append('\n')
            .Append("identity  ").Append(file.Identity is { } identity ? identity.ToString("N") : "none").Append('\n');
        if (file.TornTail is { } torn)
        {
            // The version read is the last whole one: say how much follows it, and why it did not open.
            output.Append("torn      ").Append(Text(torn.FileLength - torn.ValidLength))
                .Append(" bytes after the last whole version, of ").Append(Text(torn.FileLength))
                .Append(" (").Append(torn.Reason).Append("); --repair truncates them\n");
        }
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

    /// <summary>
    /// The layout tree, with the ARRAY encoding of every terminal node beside it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// `--encodings` prints the file's two dictionaries, and a dictionary
    /// attributes nothing: our writer interns only what it actually posts (nine entries on
    /// `zoned_many_zones_nulls`) while the reference interns its whole registry (thirty-four), so
    /// "who encoded this column how" could not be answered by comparing two files -- a cost
    /// attribution had to go around through a target edition instead. The layout tree names the
    /// LAYOUT encodings (`vortex.flat`, `vortex.chunked`), which is the shape of the file, not the
    /// shape of the data. What the data is encoded as lives one level down, in the array blob each
    /// flat node holds, and this build already parses it to decode anything at all.
    /// </para>
    /// <para>
    /// IT COSTS THE SEGMENT READS, and that is the honest price of the answer: a flat node's blob
    /// cannot be walked without the bytes its buffer specs point into. Nothing is decompressed --
    /// the walk resolves buffer spans and reads ids -- so the cost is I/O and not CPU, and on a
    /// file with a thousand chunks it is a thousand reads. `--schema`, `--encodings` and
    /// `--segments` stay metadata-only for when that matters.
    /// </para>
    /// <para>
    /// A NODE THAT WILL NOT PARSE PRINTS WHY AND THE WALK CONTINUES. A dump that refused the whole
    /// file because one blob uses a buffer compression this build lacks would be useless for
    /// exactly the file one is dumping to find that out -- and CI runs `--all` over the corpus,
    /// where such files are the point.
    /// </para>
    /// </remarks>
    /// <param name="output">The report.</param>
    /// <param name="file">The open file.</param>
    /// <returns>The completed walk.</returns>
    private static async Task Layout(StringBuilder output, VortexFile file)
    {
        output.Append("\nlayout\n");
        LayoutTree tree = LayoutTree.Parse(file);

        ArrayEncodingId[] encodings = new ArrayEncodingId[file.ArrayEncodingCount];
        for (int i = 0; i < encodings.Length; i++)
        {
            encodings[i] = file.GetArrayEncoding(i);
        }

        // ONE ARENA FOR THE WHOLE WALK: `ArrayBlobReader.Load` resets it, and a tree of ten
        // thousand flat nodes should not allocate ten thousand arenas to read one id each.
        ArrayNodeArena arena = new ArrayNodeArena();
        await Node(output, file, tree.Root, depth: 1, arena, encodings).ConfigureAwait(false);
    }

    private static async Task Node(
        StringBuilder output,
        VortexFile file,
        LayoutNode node,
        int depth,
        ArrayNodeArena arena,
        ArrayEncodingId[] encodings)
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

        if (node.Encoding == LayoutEncodingId.Flat)
        {
            output.Append("  encoding=")
                .Append(await ArrayEncodingOf(file, node, arena, encodings).ConfigureAwait(false));
        }

        output.Append('\n');

        for (int i = 0; i < node.ChildCount; i++)
        {
            await Node(output, file, node.GetChild(i), depth + 1, arena, encodings)
                .ConfigureAwait(false);
        }
    }

    /// <summary>The array encoding tree inside one <c>vortex.flat</c> node, as one expression.</summary>
    /// <param name="file">The open file, for its segments and its encoding dictionary.</param>
    /// <param name="node">The flat layout node.</param>
    /// <param name="arena">The arena the blob is parsed into; it is reset by the load.</param>
    /// <param name="encodings">Spec index to resolved id, as <c>ArrayBlobReader</c> wants it.</param>
    /// <returns>
    /// Something like <c>vortex.dict(vortex.primitive,fastlanes.bitpacked)</c>, or a parenthesised
    /// reason the node could not be read.
    /// </returns>
    private static async Task<string> ArrayEncodingOf(
        VortexFile file, LayoutNode node, ArrayNodeArena arena, ArrayEncodingId[] encodings)
    {
        ReadOnlySpan<uint> segments = node.Segments;
        if (segments.Length != 1)
        {
            // The layout says flat and carries no single segment: the file is malformed in a way
            // `--layout` is precisely the tool for looking at, so it is reported and not thrown.
            return $"(flat with {segments.Length} segments)";
        }

        uint index = segments[0];
        if (index >= (uint)file.SegmentSpecs.Length)
        {
            return $"(segment {Text(index)} is out of range)";
        }

        SegmentSpec spec = file.SegmentSpecs[(int)index];

        // THE METADATA IS COPIED OUT BEFORE THE AWAIT, because `node.Metadata` is a span over the
        // layout buffer and a span cannot cross one. The inlined-tree variant is the reason it is
        // needed at all: `vortex.flat` may carry the Array FlatBuffer in its own metadata, and then
        // the segment is read from offset 0 as pure buffer region.
        byte[]? inlined = null;
        FlatLayoutMetadata metadata = FlatLayoutMetadata.Read(node.Metadata);
        if (metadata.HasArrayEncodingTree)
        {
            inlined = metadata.ArrayEncodingTree.ToArray();
        }

        try
        {
            using SegmentOwner owner =
                await file.Segments.ReadAsync(spec, CancellationToken.None).ConfigureAwait(false);
            if (inlined is null)
            {
                ArrayBlobReader.Load(arena, owner.Buffer, encodings);
            }
            else
            {
                ArrayBlobReader.Load(arena, inlined, owner.Buffer, encodings);
            }

            StringBuilder tree = new StringBuilder();
            ArrayEncoding(tree, file, arena.Root);
            return tree.ToString();
        }
        catch (VortexUnsupportedException error)
        {
            return $"(unsupported: {error.Message})";
        }
        catch (VortexFormatException error)
        {
            return $"(malformed: {error.Message})";
        }
    }

    /// <summary>Writes one array node and its children as <c>id(child,child)</c>.</summary>
    /// <remarks>
    /// THE TEXT ID, not the resolved enum, so that an encoding this build does not implement still
    /// names itself -- which is the whole question the column is here to answer, and `Unknown` is
    /// not an answer. It is marked with a `?` so that a reader does not take a name this build
    /// cannot decode for one it can.
    /// </remarks>
    /// <param name="output">The expression being built.</param>
    /// <param name="file">The file, for the encoding dictionary the spec index points into.</param>
    /// <param name="node">The array node.</param>
    private static void ArrayEncoding(StringBuilder output, VortexFile file, ArrayNode node)
    {
        output.Append(file.GetArrayEncodingId(node.EncodingSpecIndex))
            .Append(node.Encoding == ArrayEncodingId.Unknown ? "?" : string.Empty);
        if (node.ChildCount == 0)
        {
            return;
        }

        output.Append('(');
        for (int i = 0; i < node.ChildCount; i++)
        {
            if (i != 0)
            {
                output.Append(',');
            }

            ArrayEncoding(output, file, node.GetChild(i));
        }

        output.Append(')');
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

    /// <summary>
    /// Row-encodes every batch. Two reasons this lives in an AOT-published executable rather than
    /// only in the unit tests: the encoder's kernels are generic over eleven value types and
    /// dispatch through static abstract interface members, which is precisely the shape that can
    /// compile cleanly and then fail to find an instantiation at run time under Native AOT; and
    /// the row format supports a strict subset of the dtypes, so running it over the whole corpus
    /// is the only cheap way to exercise both the accepted and the refused half.
    /// </summary>
    /// <remarks>
    /// An unsupported dtype is REPORTED, not fatal: most of the corpus contains extensions, lists
    /// or maps, for which the format defines no ordering at all. Exiting non-zero on those would
    /// say "vxdump failed" about a file that is perfectly fine.
    /// </remarks>
    private static async Task RowKeysSection(VortexFile file)
    {
        long rows = 0;
        long bytes = 0;
        long batches = 0;
        string? refusal = null;

        await foreach (Vorticity.Columns.RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            using (batch)
            {
                if (batch.RowCount == 0)
                {
                    continue;
                }

                int columnCount = batch.IsTabular ? batch.FieldCount : 1;
                if (columnCount == 0)
                {
                    continue;
                }

                int[] columns = new int[columnCount];
                RowSortField[] fields = new RowSortField[columnCount];
                CanonicalNode root = batch.Arena.GetNode(batch.RootIndex);
                for (int i = 0; i < columnCount; i++)
                {
                    columns[i] = batch.IsTabular ? root.GetFieldIndex(i) : batch.RootIndex;
                    fields[i] = new RowSortField(descending: (i & 1) == 1, nullsFirst: (i & 2) == 0);
                }

                try
                {
                    using RowKeys keys = RowEncoder.Encode(batch.Arena, columns, fields);
                    for (int i = 0; i < keys.RowCount; i++)
                    {
                        // Touch every key, so a size the encoder got wrong is a failure here and
                        // not a wrong answer somewhere downstream.
                        if (keys.Row(i).Length != keys.Sizes[i])
                        {
                            throw new VortexFormatException($"Row {i} is not the length it declared.");
                        }
                    }

                    rows += keys.RowCount;
                    bytes += keys.TotalBytes;
                    batches++;
                }
                catch (VortexUnsupportedException error)
                {
                    refusal ??= error.Message;
                }
            }
        }

        Console.Out.Write(
            "\nrow-keys\n  batches=" + Text(batches) + "\n  rows=" + Text(rows) +
            "\n  bytes=" + Text(bytes) +
            (refusal is null ? string.Empty : "\n  unsupported=" + refusal) + "\n");
    }

    private static string Text(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Text(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>`--repair`: truncates a torn append back to the last valid file.</summary>
    private static async Task<int> Repair(string path)
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

    /// <summary>
    /// `--verify`: every index region the directory lists against its checksum, wherever it is read
    /// -- the file or a fragment -- and the file's bytes against the XXH3-128 a fragment recorded:
    /// the hash no reader computes, computed here, offline.
    /// </summary>
    /// <returns>Whether everything checked holds.</returns>
    private static async Task<bool> Verify(StringBuilder output, VortexFile file)
    {
        output.Append("\nverify\n");
        Vorticity.Indexes.IndexDirectory? directory = await file.ReadIndexDirectoryAsync().ConfigureAwait(false);
        foreach (string? fragment in file.IndexFragmentRefusals)
        {
            if (fragment is not null)
            {
                output.Append("  fragment  ").Append(fragment).Append('\n');
            }
        }

        if (directory is null)
        {
            output.Append("  no index directory");
            if (file.IndexDirectoryRefusal is { } refusal)
            {
                output.Append(": refused, ").Append(refusal);
            }

            output.Append('\n');
            return file.IndexDirectoryRefusal is null;
        }

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

    /// <summary>`--indexes`: the file's index directory, as the reader kept it.</summary>
    private static async Task Indexes(StringBuilder output, VortexFile file)
    {
        output.Append("\nindexes\n");
        if (!file.HasIndexDirectory)
        {
            output.Append("  none\n");
            return;
        }

        Vorticity.Indexes.IndexDirectory? directory = await file.ReadIndexDirectoryAsync().ConfigureAwait(false);
        if (directory is null)
        {
            output.Append("  refused: ").Append(file.IndexDirectoryRefusal).Append('\n');
            return;
        }

        Vorticity.Indexes.WritePolicy policy = directory.Policy;
        output.Append("  rows          ").Append(Text(directory.RowCount)).Append('\n')
            .Append("  previous eof  ").Append(Text(directory.PreviousEof)).Append('\n')
            .Append("  budget        ").Append(Text(directory.BudgetPerMille)).Append("‰\n")
            .Append("  policy        default ").Append(policy.Default.ToString());
        foreach (var column in policy.Columns)
        {
            output.Append(", ").Append(column.Key).Append(' ').Append(column.Value.ToString());
        }

        foreach (Vorticity.Indexes.CompositeKeyPolicy key in policy.Keys)
        {
            output.Append(", (").Append(string.Join(", ", key.Paths)).Append(") ").Append(key.Policy.ToString());
        }

        output.Append('\n');

        // What the file carries, as the library describes it: the listed bytes are the
        // directory's regions, and a paged or tree layout holds more below them.
        IReadOnlyList<VortexIndexInfo> infos = await file.ReadIndexesAsync().ConfigureAwait(false);
        for (int i = 0; i < infos.Count; i++)
        {
            VortexIndexInfo info = infos[i];
            IReadOnlyList<Vorticity.Indexes.IndexRun> runs = directory.Entries[i].Runs;
            output.Append("  ").Append(info.Kind)
                .Append("  column=").Append(info.Column.Length == 0 ? "(root)" : info.Column)
                .Append("  block=").Append(Text(info.BlockLength))
                .Append("  runs=").Append(Text(info.Runs))
                .Append("  blocks=").Append(runs.Count == 0 ? "-" : Text(runs[0].FirstBlock) + ".." + Text(runs[^1].EndBlock))
                .Append("  entries=").Append(Text(info.Entries))
                .Append("  listed-bytes=").Append(Text(info.ListedBytes))
                .Append("  layout=").Append(info.Layout.ToString())
                .Append('\n');
        }
    }

    /// <summary>`--explain`: the plan of a filtered scan.</summary>
    private static async Task Explain(StringBuilder output, VortexFile file, string expression)
    {
        Vorticity.Expressions.VortexExpr filter = FilterText.Parse(expression);
        ScanPlan plan = await file.Scan().Where(filter).ExplainAsync().ConfigureAwait(false);
        output.Append("\nexplain   ").Append(expression).Append('\n')
            .Append("  file may match   ").Append(plan.FileMayMatch ? "yes" : "no").Append('\n')
            .Append("  rows             ").Append(Text(plan.RowCount)).Append('\n')
            .Append("  blocks           ").Append(Text(plan.LiveBlocks)).Append(" live of ").Append(Text(plan.Blocks))
            .Append(" (").Append(Text(plan.BlockRows)).Append(" rows each)\n");
        foreach (PruningStep step in plan.Pruning)
        {
            output.Append("    ").Append(step.Structure).Append(": ").Append(Text(step.BlocksPruned))
                .Append(" pruned, ").Append(Text(step.SegmentsRead)).Append(" segments / ")
                .Append(Text(step.BytesRead)).Append(" bytes read\n");
        }

        output.Append("  splits           ").Append(Text(plan.LiveSplits)).Append(" live of ").Append(Text(plan.Splits)).Append('\n')
            .Append("  to read          ").Append(Text(plan.SegmentsToRead)).Append(" segments, ")
            .Append(Text(plan.BytesToRead)).Append(" bytes of ").Append(Text(plan.FileBytes)).Append('\n');
        if (plan.RowsSelectedByIndex > 0)
        {
            output.Append("  index selects    ").Append(Text(plan.RowsSelectedByIndex)).Append(" rows outright\n");
        }

        if (plan.Count is { } tiers)
        {
            output.Append("  count tiers      ")
                .Append(tiers.ExactCover ? "exact cover (" + Text(tiers.ExactCount) + "), " : string.Empty)
                .Append(Text(tiers.SplitsPruned)).Append(" pruned, ")
                .Append(Text(tiers.SplitsProven)).Append(" proven, ")
                .Append(Text(tiers.SplitsDecoded)).Append(" decoded\n");
        }

        long count = await file.Scan().Where(filter).CountAsync().ConfigureAwait(false);
        output.Append("  count            ").Append(Text(count)).Append('\n');
    }

    private readonly struct Sections
    {
        private Sections(
            bool schema, bool encodings, bool layout, bool segments, bool stats, bool scan, bool rowKeys,
            bool indexes, string? explain, IReadOnlyList<string> fragments, bool verify)
        {
            Schema = schema;
            Encodings = encodings;
            Layout = layout;
            Segments = segments;
            Stats = stats;
            Scan = scan;
            RowKeys = rowKeys;
            Indexes = indexes;
            Explain = explain;
            Fragments = fragments;
            Verify = verify;
        }

        internal bool Indexes { get; }

        /// <summary>Files holding index fragments to add to the file's own indexes.</summary>
        internal IReadOnlyList<string> Fragments { get; }

        /// <summary>Check the index regions against their checksums, and a fragment's file hash against the file.</summary>
        internal bool Verify { get; }

        internal string? Explain { get; }

        internal bool Schema { get; }

        internal bool Encodings { get; }

        internal bool Layout { get; }

        internal bool Segments { get; }

        internal bool Stats { get; }

        internal bool Scan { get; }

        internal bool RowKeys { get; }

        internal static Sections Parse(ReadOnlySpan<string> args)
        {
            bool schema = false;
            bool encodings = false;
            bool layout = false;
            bool segments = false;
            bool stats = false;
            bool scan = false;
            bool rowKeys = false;
            bool indexes = false;
            string? explain = null;
            List<string> fragments = [];
            bool verify = false;
            bool any = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];
                switch (arg)
                {
                    case "--indexes": indexes = any = true; break;
                    case "--fragment" when i + 1 < args.Length:
                        fragments.Add(args[++i]);
                        break;
                    case "--verify": verify = any = true; break;
                    case "--explain" when i + 1 < args.Length:
                        explain = args[++i];
                        any = true;
                        break;
                    case "--schema": schema = any = true; break;
                    case "--encodings": encodings = any = true; break;
                    case "--layout": layout = any = true; break;
                    case "--segments": segments = any = true; break;
                    case "--stats": stats = any = true; break;
                    case "--scan": scan = any = true; break;

                    // Deliberately NOT part of --all: the row format supports a strict subset of
                    // the dtypes, so folding it in would make --all report "unsupported" for most
                    // of a corpus that is entirely well-formed.
                    case "--row-keys": rowKeys = any = true; break;
                    case "--all":
                        schema = encodings = layout = segments = stats = any = true;
                        break;
                    default:
                        Console.Error.WriteLine($"vxdump: unknown option '{arg}'");
                        break;
                }
            }

            // No section asked for means the layout tree.
            return any
                ? new Sections(schema, encodings, layout, segments, stats, scan, rowKeys, indexes, explain, fragments, verify)
                : new Sections(false, false, true, false, false, false, false, false, null, fragments, false);
        }
    }
}
