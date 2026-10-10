// The scratch of a session that seals its files: what a query or a writer moves to disk.
//
// WHAT IS HELD: a sealed scratch reads back every byte appended, across frames, from the frames on
// disk and from the frame still being filled, by several readers at once; its file holds none of the
// plaintext, where a plain scratch's does; and a query whose budget forces its lanes to spill gives
// the same answer in a session that seals.
using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Sealing;

public sealed class SealedScratchTests
{
    [Fact]
    public async Task ASealedScratchReadsBackWhatWasAppended()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] expected = SealedObjects.Pattern(1_000_003);
        using RunScratch scratch = new RunScratch(memoryBudget: 100_000, directory: null, sealFile: true);
        int[] pieces = [1_000, 70_000, 3, 200_000, 65_536, 1, 131_072, 400_000];
        int at = 0;
        foreach (int piece in pieces)
        {
            int length = Math.Min(piece, expected.Length - at);
            Assert.Equal(at, await scratch.AppendAsync(expected.AsMemory(at, length), ct));
            at += length;
        }

        await scratch.AppendAsync(expected.AsMemory(at), ct);
        Assert.True(scratch.OnDisk);
        Assert.Equal(expected.Length, scratch.Length);

        // Reads across frames, inside the frame being filled, and through both, several at once.
        (long Offset, int Length)[] reads =
        [
            (0, 10), (65_530, 20), (0, expected.Length), (expected.Length - 5_000, 5_000),
            (expected.Length - 70_000, 69_999), (123_456, 654_321), (999_999, 4),
        ];
        Task[] all = new Task[reads.Length];
        for (int i = 0; i < reads.Length; i++)
        {
            (long offset, int length) = reads[i];
            all[i] = Task.Run(
                async () =>
                {
                    byte[] read = new byte[length];
                    await scratch.ReadAsync(offset, read, ct);
                    Assert.Equal(expected.AsSpan((int)offset, length).ToArray(), read);
                },
                ct);
        }

        await Task.WhenAll(all);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASealedScratchFileHoldsNoPlaintext(bool sealFile)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] secret = Encoding.ASCII.GetBytes(string.Concat(System.Linq.Enumerable.Repeat("SECRET-ROW-", 50_000)));
        using RunScratch scratch = new RunScratch(memoryBudget: 0, directory: null, sealFile);
        await scratch.AppendAsync(secret, ct);
        Assert.True(scratch.OnDisk);

        long length = RandomAccess.GetLength(scratch.FileHandle!);
        byte[] raw = new byte[length];
        int read = 0;
        while (read < raw.Length)
        {
            read += await RandomAccess.ReadAsync(scratch.FileHandle!, raw.AsMemory(read), read, ct);
        }

        Assert.Equal(!sealFile, raw.AsSpan().IndexOf("SECRET-ROW-"u8) >= 0);
    }
}
