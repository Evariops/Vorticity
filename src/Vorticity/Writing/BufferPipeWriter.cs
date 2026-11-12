using System;
using System.Buffers;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity.Writing;

/// <summary>
/// A pipe writer over a buffer writer: what a file writer writes lies in the buffer as it was written, nothing flushed
/// anywhere else. A pipe over a memory stream copied each segment into the stream, which copied itself at each doubling,
/// and the stream's array was copied once more to be kept.
/// </summary>
internal sealed class BufferPipeWriter(IBufferWriter<byte> buffer) : PipeWriter
{
    public override void Advance(int bytes) => buffer.Advance(bytes);

    public override Memory<byte> GetMemory(int sizeHint = 0) => buffer.GetMemory(sizeHint);

    public override Span<byte> GetSpan(int sizeHint = 0) => buffer.GetSpan(sizeHint);

    public override void CancelPendingFlush()
    {
    }

    public override void Complete(Exception? exception = null)
    {
    }

    public override ValueTask<FlushResult> FlushAsync(CancellationToken cancellationToken = default) =>
        new ValueTask<FlushResult>(new FlushResult(isCanceled: false, isCompleted: false));
}
