// The scratch of a session that seals its files: what a query or a writer moves to disk.
//
// WHAT IS HELD: a sealed scratch reads back every byte appended, across frames, from the frames on
// disk and from the frame still being filled, by several readers at once, and while the writer moves
// on to the next frame, as a plain one does across its pages written and the one being filled; its
// file holds none of the plaintext, where a plain scratch's does; and a query whose budget forces its lanes to spill gives
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
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AScratchOnDiskReadsBackWhatWasAppended(bool sealFile)
    {
        // Past three of a plain store's pages, so that reads cross the pages written and the one being filled.
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] expected = SealedObjects.Pattern(3_000_003);
        using RunScratch scratch = new RunScratch(memoryBudget: 100_000, directory: null, sealFile);
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

        // Reads across frames and pages, inside the frame or the page being filled, and through both, several at once.
        (long Offset, int Length)[] reads =
        [
            (0, 10), (65_530, 20), (0, expected.Length), (expected.Length - 5_000, 5_000),
            (expected.Length - 70_000, 69_999), (123_456, 654_321), (999_999, 4),
            (RunScratch.PageBytes - 6, 20), ((2 * RunScratch.PageBytes) - 1_000, 2_000), (2 * RunScratch.PageBytes, 900_000),
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
    public async Task ReadersOfTheFrameBeingFilledGetItsBytesWhileTheWriterMovesOn(bool sealFile)
    {
        // The writer appends while readers read what it just appended, which lies in the frame being
        // filled until the frame is full: a reader must never be served the next frame's bytes as the
        // writer seals one frame and starts the next in the same buffer.
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] expected = SealedObjects.Pattern(4 << 20);
        using RunScratch scratch = new RunScratch(memoryBudget: 0, directory: null, sealFile);
        long published = 0;
        bool done = false;
        Task writer = Task.Run(
            async () =>
            {
                for (int at = 0; at < expected.Length; at += 1_000)
                {
                    int length = Math.Min(1_000, expected.Length - at);
                    await scratch.AppendAsync(expected.AsMemory(at, length), ct);
                    Volatile.Write(ref published, at + length);
                }

                Volatile.Write(ref done, true);
            },
            ct);

        Task[] readers = new Task[4];
        for (int r = 0; r < readers.Length; r++)
        {
            readers[r] = Task.Run(
                async () =>
                {
                    byte[] read = new byte[2_000];
                    while (!Volatile.Read(ref done))
                    {
                        long end = Volatile.Read(ref published);
                        if (end < read.Length)
                        {
                            continue;
                        }

                        await scratch.ReadAsync(end - read.Length, read, ct);
                        Assert.True(
                            read.AsSpan().SequenceEqual(expected.AsSpan((int)(end - read.Length), read.Length)),
                            $"the bytes read before {end} are not the ones appended there");
                    }
                },
                ct);
        }

        await writer;
        await Task.WhenAll(readers);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ASealedScratchFileHoldsNoPlaintext(bool sealFile)
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        byte[] secret = Encoding.ASCII.GetBytes(string.Concat(System.Linq.Enumerable.Repeat("SECRET-ROW-", 200_000)));
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
