// A conformant writer targeting an explicit edition.
//
// An edition is a frozen set of component ids with a read-forever guarantee, so naming one is the
// only way to say "every Vortex from version N onward can read this". That makes the claim
// CHECKABLE, and these tests check it the only way that means anything: by reading the written
// file's own encoding dictionaries back and asserting every id in them belongs to the target.
//
// The interesting case is the zone map: `vortex.zoned` arrived in `core2026.08.0`, and a writer
// that emitted it wherever the chunking allowed one would hand a Vortex 0.36.0 reader, the
// version the floor exists for, an unknown layout id. Only a check on the written file sees that.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.Editions;
using Vorticity.File;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Tests.Scan;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class EditionTargetTests
{
    /// <summary>
    /// Every id in a file written at the read-forever floor belongs to that floor.
    /// </summary>
    /// <remarks>
    /// Read out of the written file rather than asserted at the call sites, because the claim is
    /// about the FILE. A writer that checked itself and then emitted something else would pass any
    /// test written the other way round.
    /// </remarks>
    [Theory]
    [InlineData("distributions/high_cardinality_i64_r8193")]
    [InlineData("types/i64_nonnull_r8192")]
    // An arithmetic progression: `vortex.sequence` is the obvious scheme for it and arrived in
    // core2025.06.0, so the floor has to reach a different one.
    [InlineData("types/date_ms_nonnull_r8193")]
    [InlineData("types/utf8_nullable_r1025")]
    [InlineData("containers/uncompressed_canonical")]
    public async Task EveryComponentOfAFloorTargetedFileBelongsToTheFloor(string id)
    {
        string path = await Write(id, VortexEdition.Core20250500);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);

            foreach (string array in ArrayIds(file))
            {
                Assert.True(
                    EditionRegistry.Contains(VortexEdition.Core20250500, ComponentKind.Array, array),
                    $"{id}: array '{array}' is not in core2025.05.0");
            }

            foreach (string layout in LayoutIds(file))
            {
                Assert.True(
                    EditionRegistry.Contains(VortexEdition.Core20250500, ComponentKind.Layout, layout),
                    $"{id}: layout '{layout}' is not in core2025.05.0");
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>
    /// The floor costs the zone map, and that is the trade rather than a bug.
    /// </summary>
    /// <remarks>
    /// `vortex.zoned` arrived in core2026.08.0. Below it the writer omits the zone map instead of
    /// approximating it with the legacy `vortex.stats`, which no release of Vortex has ever
    /// written - 0.86.1 cannot emit one - and which would therefore be an untestable format path.
    /// Pruning is an optimization, so omitting it costs correctness nothing.
    /// </remarks>
    [Fact]
    public async Task TheDefaultTargetCarriesAZoneMapAndTheFloorDoesNot()
    {
        Assert.True(await HasZonedLayout("distributions/high_cardinality_i64_r8193", EditionRegistry.Newest));
        Assert.False(await HasZonedLayout("distributions/high_cardinality_i64_r8193", VortexEdition.Core20250500));
    }

    /// <summary>
    /// A schema the target cannot express fails the write, naming what would fix it.
    /// </summary>
    /// <remarks>
    /// `vortex.uuid` is a core2026.08.3 dtype, and a List column's canonical form here is
    /// `vortex.listview`, which arrived in core2025.10.0. Both are cases where producing a file
    /// the target's readers cannot open is the only alternative, so the write stops instead.
    /// </remarks>
    [Theory]
    [InlineData("types/uuid_nonnull_r1024", "vortex.uuid")]
    [InlineData("types/list_i32_nonnull_r1024", "vortex.listview")]
    public async Task AComponentOutsideTheTargetFailsTheWrite(string id, string component)
    {
        VortexUnsupportedException error = await Assert.ThrowsAsync<VortexUnsupportedException>(
            () => Write(id, VortexEdition.Core20250500));

        Assert.Equal(component, error.ComponentId);

        // The message's job: turn "this does not work" into "raise your target to this".
        Assert.Contains("core2025.05.0", error.Message, StringComparison.Ordinal);
        Assert.Contains(
            EditionRegistry.Name(EditionRegistry.IntroducedIn(
                component == "vortex.uuid" ? ComponentKind.DType : ComponentKind.Array,
                component)!.Value),
            error.Message,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// Lowering the target changes no value, which is the property that makes it a safe knob.
    /// </summary>
    [Fact]
    public async Task LoweringTheTargetChangesNoValue()
    {
        List<string> newest = await Values("containers/uncompressed_canonical", EditionRegistry.Newest);
        List<string> floor = await Values("containers/uncompressed_canonical", VortexEdition.Core20250500);
        Assert.Equal(newest, floor);
        Assert.NotEmpty(newest);
    }

    /// <summary>
    /// The tables are cumulative, which is the property <see cref="EditionRegistry.Contains"/>
    /// relies on to be one integer comparison.
    /// </summary>
    [Fact]
    public void EditionsAreCumulative()
    {
        ReadOnlySpan<VortexEdition> order =
        [
            VortexEdition.Core20250500, VortexEdition.Core20250600, VortexEdition.Core20251000,
            VortexEdition.Core20260800, VortexEdition.Core20260801, VortexEdition.Core20260802,
            VortexEdition.Core20260803,
        ];

        ReadOnlySpan<string> arrays =
        [
            "vortex.primitive", "fastlanes.bitpacked", "vortex.zigzag", "vortex.sequence",
            "fastlanes.rle", "vortex.onpair", "vortex.variant",
        ];

        foreach (string id in arrays)
        {
            bool seen = false;
            foreach (VortexEdition edition in order)
            {
                bool contained = EditionRegistry.Contains(edition, ComponentKind.Array, id);
                Assert.True(!seen || contained, $"{id} left edition {EditionRegistry.Name(edition)}");
                seen |= contained;
            }

            Assert.True(seen, $"{id} is in no edition at all");
        }

        // The namespaces are separate: `vortex.chunked` and `vortex.dict` are both an array id and
        // a layout id, and a single table would answer the wrong question for them.
        Assert.True(EditionRegistry.Contains(
            VortexEdition.Core20250500, ComponentKind.Layout, "vortex.zoned") is false);
        Assert.True(EditionRegistry.Contains(
            EditionRegistry.Newest, ComponentKind.Layout, "vortex.zoned"));
        Assert.Null(EditionRegistry.IntroducedIn(ComponentKind.Layout, "vortex.primitive"));
        Assert.Null(EditionRegistry.IntroducedIn(ComponentKind.Array, "fastlanes.delta"));
    }

    private static async Task<bool> HasZonedLayout(string id, VortexEdition target)
    {
        string path = await Write(id, target);
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            foreach (string layout in LayoutIds(file))
            {
                if (layout == "vortex.zoned")
                {
                    return true;
                }
            }

            return false;
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static List<string> ArrayIds(VortexFile file)
    {
        List<string> ids = [];
        for (int i = 0; i < file.ArrayEncodingCount; i++)
        {
            ids.Add(file.GetArrayEncodingId(i));
        }

        return ids;
    }

    private static List<string> LayoutIds(VortexFile file)
    {
        List<string> ids = [];
        for (int i = 0; i < file.LayoutEncodingCount; i++)
        {
            ids.Add(file.GetLayoutEncodingId(i));
        }

        return ids;
    }

    private static async Task<List<string>> Values(string id, VortexEdition target)
    {
        string path = await Write(id, target);
        try
        {
            List<string> values = [];
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync()
                .WithCancellation(CancellationToken.None))
            {
                Vorticity.Tests.Writing.Values.Describe(batch, values);
            }

            return values;
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static async Task<string> Write(string id, VortexEdition target)
    {
        Decoders.EnsureRegistered();
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-{Guid.NewGuid():N}.vortex");

        await using VortexFile source = await VortexFile.OpenAsync(
            Corpus.Path(id), CancellationToken.None);
        await using VortexFileWriter writer = VortexFileWriter.Create(
            path, source.DType, new VortexWriteOptions { TargetEdition = target });

        await foreach (RecordBatch batch in source.ScanBuilder().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
        return path;
    }
}
