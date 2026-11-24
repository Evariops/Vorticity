// A put's spool, held while a request still sends it.
//
// The handler can hand a response back, an early one or a failure, while its write of the body goes on, and the put then
// ends and disposes its spool. The mapping the write reads must outlive it: unmapped under the write, the next slice read
// a page that is gone, a fault that ends the process. This one cannot be shown failing without its fix for that reason.
using System;
using System.Buffers;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.ObjectStorage.Http;
using Xunit;

namespace Vorticity.Tests.ObjectStorage;

public sealed class SpoolFileTests
{
    /// <summary>
    /// A spool disposed while a request still writes its body keeps its mapping and its file until the write ends, which
    /// then reads the content whole; the file goes with it. A body sent once the spool is gone is refused, not read.
    /// </summary>
    [Fact]
    public async Task ASpoolOutlivesItsPutWhileARequestStillSendsIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string directory = TestScratch.Create("vorticity-spool-");
        try
        {
            byte[] content = new byte[3 << 20];
            new Random(11).NextBytes(content);
            SpoolFile spool = await SpoolFile.FillAsync(directory, PipeReader.Create(new ReadOnlySequence<byte>(content)), content.Length, "key", ct);
            ReadOnlySequence<byte> bytes = spool.Bytes;

            using GatedStream sink = new GatedStream();
            using SequenceContent body = new SequenceContent(bytes);
            Task sending = body.CopyToAsync(sink, ct);
            await sink.FirstWrite.WaitAsync(TimeSpan.FromSeconds(30), ct);

            // The put ends: its spool is disposed while the write waits.
            spool.Dispose();
            Assert.Single(Directory.EnumerateFiles(directory));

            sink.Open();
            await sending.WaitAsync(TimeSpan.FromSeconds(30), ct);
            Assert.Equal(content, sink.ToArray());
            Assert.Empty(Directory.EnumerateFiles(directory));

            using SequenceContent late = new SequenceContent(bytes);
            System.Net.Http.HttpRequestException refused = await Assert.ThrowsAsync<System.Net.Http.HttpRequestException>(() => late.CopyToAsync(new MemoryStream(), ct));
            Assert.IsType<ObjectDisposedException>(refused.InnerException);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>A stream that takes its first write, then holds every write until it is opened.</summary>
    private sealed class GatedStream : MemoryStream
    {
        private readonly TaskCompletionSource _first = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Task FirstWrite => _first.Task;

        internal void Open() => _gate.TrySetResult();

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Write(buffer.Span);
            _first.TrySetResult();
            await _gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
