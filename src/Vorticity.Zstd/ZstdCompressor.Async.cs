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
        using var slots = new SemaphoreSlim(degree);
        var jobs = new Task<(byte[] Buffer, int Size)>[plan.Count];
        int started = 0;
        int written = 0;
        bool fits = true;
        using MemoryHandle pin = source.Pin();
        nint address = AddressOf(pin);
        if (plan.LongDistance)
        {
            BeginJobsMatcher(plan, address);
        }

        try
        {
            for (int flushed = 0; flushed < plan.Count; flushed++)
            {
                // The jobs written out oldest first (zstdmt's flush), up to twice the degree of them
                // started, the degree of them running: one held back by a slow core leaves the
                // others running.
                while (started < plan.Count && started < flushed + (2 * degree))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    int job = started;
                    (RawSequence[]? sequences, int count) = plan.LongDistance
                        ? await Task.Run(() => GenerateJobSequences(plan, address, job), CancellationToken.None).ConfigureAwait(false)
                        : (null, 0);
                    jobs[job] = RunJobAsync(slots, plan, address, job, sequences, count);
                    started++;
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

    /// <summary>
    /// A job on the thread pool once one of the <paramref name="slots"/> is free, in the order asked, its
    /// long-distance sequences (if any) returned after.
    /// </summary>
    private async Task<(byte[] Buffer, int Size)> RunJobAsync(SemaphoreSlim slots, JobPlan plan, nint address, int job, RawSequence[]? sequences, int count)
    {
        try
        {
            await slots.WaitAsync().ConfigureAwait(false);
            try
            {
                return await Task.Run(() => RunJob(plan, address, job, sequences, count)).ConfigureAwait(false);
            }
            finally
            {
                slots.Release();
            }
        }
        finally
        {
            if (sequences is not null)
            {
                ArrayPool<RawSequence>.Shared.Return(sequences);
            }
        }
    }
}
