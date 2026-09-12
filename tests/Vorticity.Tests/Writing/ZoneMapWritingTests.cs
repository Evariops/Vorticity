// F11, closing the loop: a file Vorticity wrote is a file Vorticity can prune.
//
// The round-trip and cross-check suites both pass whether or not a zone map is written, because a
// zone map changes no value -- which is exactly why it needs its own tests. Three things have to be
// true and none of them is visible in a value comparison: the map is THERE, it is USABLE, and it
// actually saves reads.
//
// The negative case matters as much: a ragged chunking has no single zone length to declare, and the
// writer must emit a plain chunked layout rather than a zone map whose bounds are attached to the
// wrong rows.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class ZoneMapWritingTests
{
    /// <summary>65536 rows in 64 uniform batches of 1024: exactly the shape a zone map needs.</summary>
    private const string Uniform = "containers/zoned_many_zones_nulls";

    [Fact]
    public async Task AWrittenFileCarriesAUsableZoneMapPerColumn()
    {
        await using Written written = await Written.Copy(Uniform);
        await using VortexFile file = await VortexFile.OpenAsync(written.Path, CancellationToken.None);

        LayoutTree tree = LayoutTree.Parse(file);
        LayoutNode root = tree.Root;
        Assert.Equal(LayoutEncodingId.Struct, root.Encoding);

        int zoned = 0;
        for (int i = 0; i < root.ChildCount; i++)
        {
            LayoutNode child = root.GetChild(i);
            if (child.Encoding != LayoutEncodingId.Zoned)
            {
                continue;
            }

            Assert.True(child.TryGetZoneMap(out ZoneMap map));

            // Usable, not merely present: an unresolvable aggregate would parse and prune nothing.
            Assert.True(map.IsPruningAvailable);
            Assert.Equal(1024, map.ZoneLength);
            Assert.Equal(64, map.ZoneCount);
            zoned++;
        }

        Assert.Equal(root.ChildCount, zoned);
    }

    [Fact]
    public async Task PruningAWrittenFileSkipsSegmentsAndKeepsEveryRow()
    {
        await using Written written = await Written.Copy(Uniform);

        // The same narrow band the corpus-file pruning test uses, so the two are comparable.
        VortexExpr narrow = Expr.And(
            Expr.Ge(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_003_000L))),
            Expr.Lt(Expr.Field("monotone"), Expr.Literal(FilterLiteral.From(1_003_300L))));

        List<long> unpruned = await Read(written.Path, narrow, prune: false);
        List<long> pruned = await Read(written.Path, narrow, prune: true);
        Assert.Equal(unpruned, pruned);
        Assert.NotEmpty(pruned);

        int withPruning = await CountSegments(written.Path, narrow, prune: true);
        int withoutPruning = await CountSegments(written.Path, narrow, prune: false);
        Assert.True(
            withPruning * 4 < withoutPruning,
            $"pruning a file we wrote read {withPruning} segments of {withoutPruning}");
    }

    [Fact]
    public async Task ANullCountOnlyZoneMapStillPrunesIsNull()
    {
        // `strs` is utf8: no scalar bounds, so its zone map carries the null count alone. That is
        // still a zone map and IS NULL still prunes with it.
        await using Written written = await Written.Copy(Uniform);

        List<long> unpruned = await Read(
            written.Path, Expr.IsNull(Expr.Field("nulls")), prune: false);
        List<long> pruned = await Read(
            written.Path, Expr.IsNull(Expr.Field("nulls")), prune: true);

        Assert.Equal(unpruned, pruned);
    }

    [Fact]
    public async Task ARaggedChunkingGetsNoZoneMapRatherThanAWrongOne()
    {
        // A zone map declares ONE zone length. Feeding batches of different sizes leaves no length
        // to declare, and the writer must fall back to a plain chunked layout: a declared length
        // would attach every bound to the wrong rows.
        Decoders.EnsureRegistered();
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFile source = await VortexFile.OpenAsync(
                Corpus.Path(Uniform), CancellationToken.None))
            {
                await using VortexFileWriter writer = VortexFileWriter.Create(path, source.Schema);

                // 700 then 1024: the first batch is not the file's natural split, so the chunk
                // sizes differ in the middle rather than only at the end.
                int batchIndex = 0;
                await foreach (RecordBatch batch in source.Scan()
                    .WithMaxBatchRows(batchIndex++ == 0 ? 700 : 1024)
                    .ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    await writer.WriteAsync(batch, CancellationToken.None);
                    break;
                }

                await foreach (RecordBatch batch in source.Scan()
                    .Rows(RowRange.FromLength(0, 2048))
                    .WithMaxBatchRows(1024)
                    .ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
            }

            await using VortexFile target = await VortexFile.OpenAsync(path, CancellationToken.None);
            LayoutTree tree = LayoutTree.Parse(target);
            for (int i = 0; i < tree.Root.ChildCount; i++)
            {
                Assert.NotEqual(LayoutEncodingId.Zoned, tree.Root.GetChild(i).Encoding);
            }
        }
        finally
        {
            if (System.IO.File.Exists(path))
            {
                System.IO.File.Delete(path);
            }
        }
    }

    private static async Task<List<long>> Read(string path, VortexExpr filter, bool prune)
    {
        byte[] name = System.Text.Encoding.UTF8.GetBytes("monotone");
        List<long> values = [];

        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        await foreach (RecordBatch batch in file.Scan()
            .Project("monotone")
            .Where(filter)
            .WithPruning(prune)
            .ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            PrimitiveColumn<long> column = batch.Column(name).AsPrimitive<long>();
            for (int row = 0; row < batch.RowCount; row++)
            {
                values.Add(column.Values[row]);
            }
        }

        return values;
    }

    private static async Task<int> CountSegments(string path, VortexExpr filter, bool prune)
    {
        await using MemoryMappedSegmentSource inner = MemoryMappedSegmentSource.Open(path);
        CountingSegmentSource counting = new CountingSegmentSource(inner);

        await using VortexFile file = await VortexFile.OpenAsync(
            counting, new VortexOpenOptions { LeaveSourceOpen = true }, CancellationToken.None);

        counting.ResetCounters();
        await foreach (RecordBatch batch in file.Scan()
            .Project("monotone")
            .Where(filter)
            .WithPruning(prune)
            .ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            Assert.True(batch.RowCount > 0);
        }

        return counting.Requested.Count;
    }

    /// <summary>A corpus file written out by Vorticity, deleted on disposal.</summary>
    private sealed class Written : IAsyncDisposable
    {
        private Written(string path) => Path = path;

        internal string Path { get; }

        internal static async Task<Written> Copy(string id)
        {
            Decoders.EnsureRegistered();
            string path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), $"vorticity-{Guid.NewGuid():N}.vortex");

            await using VortexFile source = await VortexFile.OpenAsync(
                Corpus.Path(id), CancellationToken.None);
            await using VortexFileWriter writer = VortexFileWriter.Create(path, source.Schema);
            await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
                .WithCancellation(CancellationToken.None))
            {
                await writer.WriteAsync(batch, CancellationToken.None);
            }

            await writer.CompleteAsync(CancellationToken.None);
            return new Written(path);
        }

        public ValueTask DisposeAsync()
        {
            if (System.IO.File.Exists(Path))
            {
                System.IO.File.Delete(Path);
            }

            return default;
        }
    }
}
