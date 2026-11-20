// The bytes a sorted text column lends for each of its keys, in a zone of one key as in a zone of many: what the key reads
// as, which a merge of objects compares keys by.
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Expressions;
using Vorticity.Keys;
using Xunit;

namespace Vorticity.Tests.Keys;

public sealed partial class SortedColumnBytesTests
{
    [VortexRecord]
    public partial record struct Named(string Name);

    /// <summary>
    /// Three blocks of one name, each its own chunk and zone, then names that ascend: every entry lends the bytes its key
    /// reads as. A zone of one key decodes to a constant, of which the source lent no bytes: a merge of objects that
    /// compared its key by them ranked it below every other key, and a walk down took every key of the other objects first.
    /// </summary>
    [Fact]
    public async Task EveryKeyLendsTheBytesItReadsAsInAZoneOfOneKeyToo()
    {
        Named[] rows = [.. Enumerable.Repeat(new Named("k-aaa"), 3 * 1_024), .. Enumerable.Range(0, 3 * 1_024).Select(i => new Named($"k-b{i:D5}"))];
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-sorted-bytes-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Named>(path, new VortexWriteOptions { BlockRows = 1_024, ChunkTargetBytes = 1 }))
            {
                await writer.WriteAsync<Named>(rows, TestContext.Current.CancellationToken);
                await writer.CompleteAsync(TestContext.Current.CancellationToken);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, cancellationToken: TestContext.Current.CancellationToken);
            (SortedColumnSource? source, string? reason) = await SortedColumnSource.OpenAsync(file, nameof(Named.Name), TestContext.Current.CancellationToken);
            Assert.True(source is not null, reason);
            await using (source)
            {
                for (long entry = 0; entry < source.EntryCount; entry++)
                {
                    await source.EnsureEntryAsync(entry, TestContext.Current.CancellationToken);
                    FilterLiteral key = source.LoadedKey(entry);
                    Assert.True(source.BytesAt(entry).SequenceEqual(key.BytesValue), $"entry {entry}: no bytes lent for {System.Text.Encoding.UTF8.GetString(key.BytesValue)}");
                }
            }
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }
}
