// The tail a streamed object's sink keeps (ObjectSegmentSink.Tail) lives in a block of the library's pool, which the sink
// gives back once it commits or discards: a segment read from that tail is the bytes copied out of it, and stays whole
// after the array serves another renter.
using System;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed class ObjectSegmentSinkTailTests
{
    [Fact]
    public async Task ASegmentReadFromTheKeptTailOutlivesTheSink()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore store = new MemoryObjectStore();
        BlockPool pool = new BlockPool();
        ObjectSegmentSink sink = new ObjectSegmentSink(store, "data/tail.vortex", streamedFrom: 1 << 10);
        byte[] body = new byte[8 << 10];
        byte[] tail = new byte[1 << 10];
        for (int i = 0; i < body.Length; i++)
        {
            body[i] = (byte)i;
        }

        for (int i = 0; i < tail.Length; i++)
        {
            tail[i] = (byte)(255 - (i % 251));
        }

        await sink.WriteAsync(body, ct);
        Assert.True(sink.IsStreamed);
        sink.KeepTail();
        await sink.WriteAsync(tail, ct);

        ISegmentReader? reader = sink.Tail(pool);
        Assert.NotNull(reader);
        SegmentOwner segment = await reader!.ReadRangeAsync(body.Length + 256, 512, 1, ct);
        try
        {
            sink.Discard();

            // The blocks the pool hands out next, the tail's among them, written over.
            Block<byte>[] rented = new Block<byte>[8];
            for (int i = 0; i < rented.Length; i++)
            {
                rented[i] = Blocks.Rent<byte>(64 << 10);
                rented[i].AsSpan().Fill(0xEE);
            }

            Assert.True(segment.Buffer.Span.SequenceEqual(tail.AsSpan(256, 512)));
            foreach (Block<byte> block in rented)
            {
                Blocks.Return(block);
            }
        }
        finally
        {
            segment.Dispose();
            await reader.DisposeAsync();
            await sink.DisposeAsync();
        }
    }
}
