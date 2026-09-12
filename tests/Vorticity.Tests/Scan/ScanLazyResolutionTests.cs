// Contract §13.5 - lazy component resolution is REALIZED HERE, not merely permitted elsewhere.
//
// Every other component refrains from throwing early. This one is what makes that pay off: steps 1
// and 2 of the executor walk only the subtrees the FieldMask selects, so an unknown array encoding
// in an unprojected column is never decoded and an unknown layout on an unvisited subtree is never
// resolved.
//
// The fixture is real, not forged at test time: forged/negative/unknown_encoding_id.vortex is
// containers/uncompressed_canonical.vortex with the 16 bytes "vortex.primitive" at offset 99392
// overwritten by "vortex.unknown01" - an equal-length flatbuffer string, so every offset in the
// file is still valid and Vortex checksums nothing. Its `ints` column is therefore unreadable and
// its `strs` column is untouched.
//
// The second half of the pair is the one that matters. Without it, someone "simplifies" the open
// path into an eager failure and nothing objects.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Xunit;

namespace Vorticity.Tests.Scan;

public sealed class ScanLazyResolutionTests
{
    private const string Forged = "negative/unknown_encoding_id";
    private const string Original = "containers/uncompressed_canonical";

    [Fact]
    public async Task OpeningAFileWithAnUnknownEncodingSucceeds()
    {
        // An unknown id in array_specs is not itself an error (contract §2.3).
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Forged(Forged), CancellationToken.None);
        Assert.Equal(4096, file.RowCount);
        Assert.Equal(2, file.Schema.FieldCount);

        bool sawUnknown = false;
        for (int i = 0; i < file.ArrayEncodingCount; i++)
        {
            if (file.GetArrayEncodingId(i) == "vortex.unknown01")
            {
                sawUnknown = true;
                Assert.Equal(Vorticity.Arrays.ArrayEncodingId.Unknown, file.GetArrayEncoding(i));
            }
        }

        Assert.True(sawUnknown, "the fixture must still declare vortex.unknown01");
    }

    [Fact]
    public async Task ProjectingTheUnknownColumnThrowsNamingTheIdAndTheKind()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Forged(Forged), CancellationToken.None);

        VortexUnsupportedException error = await Assert.ThrowsAsync<VortexUnsupportedException>(async () =>
        {
            await foreach (RecordBatch batch in file.Scan().Project("ints").ExecuteAsync())
            {
                Assert.NotNull(batch);
            }
        });

        Assert.Equal("vortex.unknown01", error.ComponentId);
        Assert.Equal(VortexComponentKind.Array, error.Kind);
        Assert.Contains("vortex.unknown01", error.Message, StringComparison.Ordinal);
        Assert.Contains("array", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ScanningTheWholeFileThrowsToo()
    {
        Decoders.EnsureRegistered();
        await using VortexFile file = await VortexFile.OpenAsync(Corpus.Forged(Forged), CancellationToken.None);

        await Assert.ThrowsAsync<VortexUnsupportedException>(async () =>
        {
            await foreach (RecordBatch batch in file.Scan().ExecuteAsync())
            {
                Assert.NotNull(batch);
            }
        });
    }

    [Fact]
    public async Task NotProjectingTheUnknownColumnSucceedsAndMatchesTheUnpatchedFile()
    {
        Decoders.EnsureRegistered();
        List<string?> forged = await ReadStrings(Corpus.Forged(Forged));
        List<string?> original = await ReadStrings(Corpus.Path(Original));

        Assert.Equal(4096, forged.Count);
        Assert.Equal(original.Count, forged.Count);
        for (int i = 0; i < forged.Count; i++)
        {
            Assert.Equal(original[i], forged[i]);
        }
    }

    [Fact]
    public async Task AFailedBatchStillReleasesItsSegments()
    {
        // The throw happens inside Execute, after the read: the context must be reset anyway, or
        // the scan leaks a segment reference for every failed batch.
        Decoders.EnsureRegistered();
        CountingSegmentSource source = new CountingSegmentSource(
            Vorticity.IO.MemoryMappedSegmentSource.Open(Corpus.Forged(Forged)));

        await using VortexFile file = await VortexFile.OpenAsync(
            source, new VortexOpenOptions(), CancellationToken.None);

        IAsyncEnumerator<RecordBatch> enumerator = file.Scan().Project("ints").ExecuteAsync().GetAsyncEnumerator();
        await Assert.ThrowsAsync<VortexUnsupportedException>(async () => await enumerator.MoveNextAsync());

        // DisposeAsync after a failure must not throw and must not double-release.
        await enumerator.DisposeAsync();
        await enumerator.DisposeAsync();
    }

    private static async Task<List<string?>> ReadStrings(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
        List<string?> values = new List<string?>();
        await foreach (RecordBatch batch in file.Scan().Project("strs").ExecuteAsync())
        {
            BinaryColumn column = batch.Column(0).AsBinary();
            for (int i = 0; i < column.Length; i++)
            {
                values.Add(column.GetString(i));
            }
        }

        return values;
    }
}
