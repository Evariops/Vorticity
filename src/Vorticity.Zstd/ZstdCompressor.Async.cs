using System;
using System.Buffers;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Zstd.Internal;

namespace Vorticity.Zstd;

/// <summary>Compression in parallel jobs, awaited: the frames of libzstd with workers.</summary>
public sealed partial class ZstdCompressor
{
    /// <summary>
    /// Compresses <paramref name="source"/> into one frame at the start of <paramref name="destination"/>,
    /// as libzstd does with workers (<c>ZSTD_c_nbWorkers</c> of 1 or more): the source cut into jobs of
    /// four windows (1 MiB to 1 GiB), compressed on the thread pool, up to
    /// <paramref name="maxDegreeOfParallelism"/> at a time, by compressors this one keeps for them.
    /// </summary>
    /// <param name="source">The content of the frame.</param>
    /// <param name="destination">Where the frame is written; <see cref="GetMaxCompressedLength"/> bytes always suffice.</param>
    /// <param name="maxDegreeOfParallelism">The most jobs compressed at once; 0 or less for <see cref="Environment.ProcessorCount"/>.</param>
    /// <param name="cancellationToken">Stops the jobs not started yet; the frame is then abandoned.</param>
    /// <returns>The size of the frame.</returns>
    /// <remarks>
    /// The frame is the one libzstd writes whatever the number of its workers, and so whatever the
    /// degree of parallelism; up to 512 KiB, it is the one <see cref="Compress"/> writes (libzstd runs
    /// no workers for it). The source and the destination must not change until the task completes,
    /// and the compressor must not be used meanwhile.
    /// </remarks>
    /// <exception cref="ArgumentException">The frame does not fit in <paramref name="destination"/>.</exception>
    public async ValueTask<int> CompressAsync(
        ReadOnlyMemory<byte> source, Memory<byte> destination, int maxDegreeOfParallelism = -1, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        JobPlan plan = PlanJobs(source.Length);
        if (plan.Count == 0)
        {
            return Compress(source.Span, destination.Span, out _, out int frame) == OperationStatus.Done
                ? frame
                : throw new ArgumentException("The frame does not fit in the destination.", nameof(destination));
        }

        int degree = Math.Min(maxDegreeOfParallelism > 0 ? maxDegreeOfParallelism : Environment.ProcessorCount, plan.Count);
        var jobs = new Task<(byte[] Buffer, int Size)>[plan.Count];
        int started = 0;
        int written = 0;
        bool fits = true;
        using MemoryHandle pin = source.Pin();
        nint address = AddressOf(pin);
        try
        {
            for (int flushed = 0; flushed < plan.Count; flushed++)
            {
                // Up to the degree of jobs at once, the oldest written out first: zstdmt's flush.
                while (started < plan.Count && started < flushed + degree)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int job = started++;
                    jobs[job] = Task.Run(() => RunJob(plan, address, job), CancellationToken.None);
                }

                (byte[] buffer, int size) = await jobs[flushed].ConfigureAwait(false);
                fits = fits && size <= destination.Length - written;
                if (fits)
                {
                    buffer.AsSpan(0, size).CopyTo(destination.Span[written..]);
                    written += size;
                }

                ArrayPool<byte>.Shared.Return(buffer);
                jobs[flushed] = null!;
            }
        }
        finally
        {
            // A job left running would read the source after its pin.
            for (int job = 0; job < started; job++)
            {
                if (jobs[job] is { } running)
                {
                    try
                    {
                        ArrayPool<byte>.Shared.Return((await running.ConfigureAwait(false)).Buffer);
                    }
                    catch (Exception)
                    {
                        // Its failure is not the one reported.
                    }
                }
            }
        }

        if (AppendChecksum)
        {
            fits = fits && FrameFormat.ChecksumSize <= destination.Length - written;
            if (fits)
            {
                WriteChecksum(source.Span, destination.Span.Slice(written, FrameFormat.ChecksumSize));
                written += FrameFormat.ChecksumSize;
            }
        }

        return fits ? written : throw new ArgumentException("The frame does not fit in the destination.", nameof(destination));
    }
}
