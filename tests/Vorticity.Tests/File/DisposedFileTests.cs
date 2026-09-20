using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.File;

/// <summary>
/// What a disposed file does, member by member. The rule is not "everything throws": a member
/// throws when it reads the retained tail, whose buffer has gone back to the pool, and answers
/// when it answers from state captured at the open. Pinning it here is what stops a new
/// tail-reading member from being added without a guard.
/// </summary>
public sealed class DisposedFileTests
{
    [Fact]
    public async Task AMemberThatReadsTheTailThrowsOnceTheFileIsDisposed()
    {
        VortexFile file = await VortexFile.OpenAsync(Corpus.Path("containers/zoned_many_zones_nulls"));
        await file.DisposeAsync();

        Assert.Throws<ObjectDisposedException>(() => file.Identity);
        Assert.Throws<ObjectDisposedException>(() => file.Indexes);
        Assert.Throws<ObjectDisposedException>(() => _ = file.SegmentSpecs.Length);
        Assert.Throws<ObjectDisposedException>(() => file.GetArrayEncodingId(0));
        Assert.Throws<ObjectDisposedException>(() => file.GetLayoutEncodingId(0));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await file.ReadMetadataAsync(0, CancellationToken.None));
        await Assert.ThrowsAsync<ObjectDisposedException>(
            async () => await file.ReadIndexDirectoryAsync());
    }

    [Fact]
    public async Task AMemberThatAnswersFromTheOpenGoesOnAnswering()
    {
        VortexFile file = await VortexFile.OpenAsync(Corpus.Path("containers/zoned_many_zones_nulls"));
        DType schema = file.Schema;
        long rows = file.RowCount;
        long bytes = file.FileLength;
        int metadata = file.MetadataCount;
        int arrays = file.ArrayEncodingCount;
        await file.DisposeAsync();

        // The answer is still true: none of these reads a buffer that was released.
        Assert.Equal(schema, file.Schema);
        Assert.Equal(rows, file.RowCount);
        Assert.Equal(bytes, file.FileLength);
        Assert.Equal(metadata, file.MetadataCount);
        Assert.Equal(arrays, file.ArrayEncodingCount);
        Assert.True(file.IsTabular);
        Assert.Equal(1, file.FormatVersion);

        if (metadata > 0)
        {
            Assert.NotNull(file.GetMetadataKey(0));
            SegmentSpec spec = file.GetMetadataSegment(0);
            Assert.True(spec.Length >= 0);
        }
    }

    [Fact]
    public async Task AScanBuiltFromADisposedFileFailsAtItsFirstRead()
    {
        VortexFile file = await VortexFile.OpenAsync(Corpus.Path("containers/zoned_many_zones_nulls"));
        await file.DisposeAsync();

        // Building it is allowed: the builder reads nothing. Enumerating it reaches the source,
        // which the file disposed with itself.
        ScanBuilder builder = file.Scan();
        await Assert.ThrowsAnyAsync<ObjectDisposedException>(async () =>
        {
            await foreach (RecordBatch batch in builder.ExecuteAsync())
            {
                batch.Dispose();
            }
        });
    }
}
