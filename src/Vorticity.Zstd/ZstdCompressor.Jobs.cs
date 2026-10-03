using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO.Hashing;
using System.Runtime.CompilerServices;
using Vorticity.Zstd.Internal;

namespace Vorticity.Zstd;

/// <summary>
/// libzstd's compression with workers (zstdmt: <c>ZSTD_c_nbWorkers</c> of 1 or more): the frame cut
/// into jobs, compressed independently of one another, then put end to end. The frames do not depend
/// on how many workers there are, nor on the order the jobs run in.
/// </summary>
/// <remarks>
/// A job is a section of the source, compressed from a context of its own. The first starts the frame
/// as libzstd does without workers (header, dictionary); each other one loads the end of the section
/// before it (its prefix, the overlap) as raw content, its repeat offsets cleared, its entropy tables
/// fresh. Every job compresses its section by calls of four blocks, its last block ending the frame
/// when it is the last.
/// </remarks>
public sealed unsafe partial class ZstdCompressor
{
    /// <summary>
    /// libzstd's <c>ZSTDMT_JOBSIZE_MIN</c>: sources up to this size are compressed without workers
    /// (<c>ZSTD_CCtx_init_compressStream2</c> turns them off), the frames <see cref="Compress"/> writes.
    /// </summary>
    internal const int MinJobsSourceSize = 512 << 10;

    /// <summary>The size of libzstd's calls to <c>ZSTD_compressContinue</c> in a job (<c>ZSTDMT_compressionJob</c>).</summary>
    private const int JobChunkSize = 4 * FrameFormat.MaxBlockSize;

    /// <summary>libzstd's <c>ZSTDMT_JOBLOG_MAX</c> on 64-bit targets: jobs of 1 GiB at most.</summary>
    private const int JobLogMax = 30;

    /// <summary>libzstd's <c>ZSTD_c_jobSize</c>, 0 for its default (<see cref="JobSize"/>): for the tests.</summary>
    internal int JobSizeOverride { get; set; }

    /// <summary>libzstd's <c>ZSTD_c_overlapLog</c>, 0 for the strategy's (<see cref="OverlapSize"/>): for the tests.</summary>
    internal int OverlapLogOverride { get; set; }

    /// <summary>The compressors that run the jobs of <see cref="CompressAsync"/>, kept from one call to the next.</summary>
    private readonly Stack<ZstdCompressor> _idleWorkers = new();

    /// <summary>zstdmt's serial long-distance matcher: the jobs' sequences, made in their order.</summary>
    private LongDistanceMatcher? _jobsMatcher;

    /// <summary>The long-distance sequences of the job being compressed (libzstd's <c>externSeqStore</c>).</summary>
    private RawSequence* _jobSequences;

    /// <summary>How many there are; 0 outside a job with the matcher on.</summary>
    private nuint _jobSequenceCount;

    /// <summary>A compressor for jobs, with the settings of <paramref name="owner"/> and its prepared dictionary.</summary>
    private ZstdCompressor(ZstdCompressor owner)
        : this(owner.Level)
    {
        _dictionary = owner._dictionary;
        JobSizeOverride = owner.JobSizeOverride;
        OverlapLogOverride = owner.OverlapLogOverride;
    }

    /// <summary>
    /// The jobs of a frame of <see cref="FrameSize"/> bytes: every one <see cref="JobSize"/> bytes but
    /// the last, each but the first preceded by <see cref="Overlap"/> bytes of the one before; none up
    /// to <see cref="MinJobsSourceSize"/>.
    /// </summary>
    private readonly record struct JobPlan(CompressionParameters Parameters, int FrameSize, int JobSize, int Overlap, bool LongDistance)
    {
        public int Count => FrameSize <= MinJobsSourceSize ? 0 : (int)(((long)FrameSize + JobSize - 1) / JobSize);

        public int Start(int job) => job * JobSize;

        public int Size(int job) => Math.Min(JobSize, FrameSize - (job * JobSize));

        public int PrefixSize(int job) => job == 0 ? 0 : Math.Min(Overlap, JobSize);
    }

    /// <summary>
    /// The jobs libzstd cuts a frame of <paramref name="sourceSize"/> bytes into, from the parameters it
    /// chooses for the whole frame (with the dictionary's size, as <c>ZSTD_CCtx_init_compressStream2</c>).
    /// </summary>
    private JobPlan PlanJobs(int sourceSize)
    {
        CompressionParameters parameters = _dictionary is null
            ? CompressionParameters.ForFrame(Level, sourceSize, LongDistanceMatching)
            : CompressionParameters.ForFrame(_dictionary.Level, sourceSize, _dictionary.Size, attached: false, LongDistanceMatching);
        bool longDistance = LongDistanceMatching || LongDistanceMatcher.EnabledFor(parameters);
        if (longDistance && parameters.Strategy < Strategy.BinaryTreeOptimal)
        {
            throw new NotSupportedException("Long-distance matching is implemented for the optimal parsers only.");
        }

        return new JobPlan(parameters, sourceSize, JobSize(parameters, longDistance), OverlapSize(parameters, longDistance), longDistance);
    }

    /// <summary>
    /// The start of zstdmt's serial long-distance matcher for a frame (<c>ZSTDMT_serialState_reset</c>):
    /// the frame's parameters, its window at the source. A dictionary is never loaded into it.
    /// </summary>
    private void BeginJobsMatcher(in JobPlan plan, byte* source)
    {
        _jobsMatcher ??= new LongDistanceMatcher();
        _jobsMatcher.BeginFrame(plan.Parameters, source);
    }

    /// <summary>
    /// A job's long-distance sequences (<c>ZSTDMT_serialState_genSequences</c>), the jobs before it done:
    /// into a rented buffer, returned by the caller.
    /// </summary>
    private (RawSequence[] Sequences, int Count) GenerateJobSequences(in JobPlan plan, byte* source, int job)
    {
        nuint size = (nuint)plan.Size(job);
        RawSequence[] sequences = ArrayPool<RawSequence>.Shared.Rent((int)_jobsMatcher!.MaxJobSequences(size));
        fixed (RawSequence* buffer = sequences)
        {
            return (sequences, (int)_jobsMatcher.GenerateJob(source + plan.Start(job), size, buffer));
        }
    }

    private ZstdCompressor RentWorker()
    {
        lock (_idleWorkers)
        {
            if (_idleWorkers.TryPop(out ZstdCompressor? worker))
            {
                return worker;
            }
        }

        return new ZstdCompressor(this);
    }

    private void ReturnWorker(ZstdCompressor worker)
    {
        lock (_idleWorkers)
        {
            _idleWorkers.Push(worker);
        }
    }

    /// <summary>
    /// A job of <paramref name="plan"/>, run on a worker into a buffer of its own (rented, returned by
    /// the caller once copied out): libzstd's job with its <c>dstBuff</c>.
    /// </summary>
    private (byte[] Buffer, int Size) RunJob(in JobPlan plan, nint source, int job, RawSequence[]? sequences, int sequenceCount)
    {
        int size = plan.Size(job);
        int capacity = GetMaxCompressedLength(size) + FrameHeaderSizeMax;
        byte[] buffer = ArrayPool<byte>.Shared.Rent(capacity);
        ZstdCompressor worker = RentWorker();
        try
        {
            int written;
            fixed (byte* destination = buffer)
            fixed (RawSequence* jobSequences = sequences)
            {
                written = worker.CompressJob(
                    plan.Parameters, (byte*)source, plan.FrameSize, plan.Start(job), size, plan.PrefixSize(job), destination, capacity, AppendChecksum,
                    jobSequences, (nuint)sequenceCount);
            }

            if (written < 0)
            {
                throw new InvalidOperationException("A job's frame exceeded its bound.");
            }

            return (buffer, written);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(buffer);
            throw;
        }
        finally
        {
            ReturnWorker(worker);
        }
    }

    private void BeginJobsMatcher(in JobPlan plan, nint source) => BeginJobsMatcher(plan, (byte*)source);

    private (RawSequence[] Sequences, int Count) GenerateJobSequences(in JobPlan plan, nint source, int job) =>
        GenerateJobSequences(plan, (byte*)source, job);

    /// <summary>The address of pinned memory, for the jobs (which may not take pointers across their awaits).</summary>
    private static nint AddressOf(in System.Buffers.MemoryHandle pin) => (nint)pin.Pointer;

    /// <summary>The checksum of a frame's content, written after its last job: zstdmt's serial state sums it.</summary>
    private static void WriteChecksum(ReadOnlySpan<byte> content, Span<byte> destination) =>
        BinaryPrimitives.WriteUInt32LittleEndian(destination, (uint)XxHash64.HashToUInt64(content));

    /// <summary>
    /// libzstd's <c>targetSectionSize</c> (<c>ZSTDMT_initCStream_internal</c>): the size of every job but
    /// the last, never under the overlap.
    /// </summary>
    internal int JobSize(in CompressionParameters parameters, bool longDistance) =>
        Math.Max(JobSizeOverride != 0 ? Math.Max(JobSizeOverride, MinJobsSourceSize) : 1 << JobLog(parameters, longDistance), OverlapSize(parameters, longDistance));

    /// <summary>
    /// libzstd's <c>ZSTDMT_computeTargetJobLog</c>: four windows, 1 MiB at least; with the long-distance
    /// matcher, whose window is oversized, eight cycles of the match finder's table, 2 MiB at least.
    /// </summary>
    private static int JobLog(in CompressionParameters parameters, bool longDistance)
    {
        int log = longDistance
            ? Math.Max(21, parameters.ChainLog - (parameters.Strategy >= Strategy.BinaryTreeLazy2 ? 1 : 0) + 3)
            : Math.Max(20, parameters.WindowLog + 2);
        return Math.Min(log, JobLogMax);
    }

    /// <summary>
    /// libzstd's <c>ZSTDMT_computeOverlapSize</c>: the bytes a job loads from before its section, a
    /// fraction of the window by the strategy (<c>ZSTDMT_overlapLog_default</c>), none at overlapLog 1;
    /// with the long-distance matcher, a fraction of a quarter job at most, even at overlapLog 1.
    /// </summary>
    internal int OverlapSize(in CompressionParameters parameters, bool longDistance)
    {
        int overlapLog = OverlapLogOverride != 0 ? OverlapLogOverride : parameters.Strategy switch
        {
            Strategy.BinaryTreeUltra2 => 9,
            Strategy.BinaryTreeUltra or Strategy.BinaryTreeOptimal => 8,
            Strategy.BinaryTreeLazy2 or Strategy.Lazy2 => 7,
            _ => 6,
        };
        int reductionLog = 9 - overlapLog;
        int log = longDistance
            ? Math.Min(parameters.WindowLog, JobLog(parameters, longDistance) - 2) - reductionLog
            : reductionLog >= 8 ? 0 : parameters.WindowLog - reductionLog;
        return log == 0 ? 0 : 1 << log;
    }

    /// <summary>
    /// The frame libzstd writes with workers, its jobs compressed one after the other on this
    /// compressor: the frame of the parallel compression, made sequentially.
    /// </summary>
    internal OperationStatus CompressInJobs(ReadOnlySpan<byte> source, Span<byte> destination, out int bytesWritten)
    {
        bytesWritten = 0;
        int written;
        fixed (byte* src = source)
        fixed (byte* dst = destination)
        {
            written = CompressFrameInJobs(src, source.Length, dst, destination.Length);
        }

        if (written < 0)
        {
            return OperationStatus.DestinationTooSmall;
        }

        bytesWritten = written;
        return OperationStatus.Done;
    }

    /// <returns>The size of the frame, or -1 when it does not fit.</returns>
    private int CompressFrameInJobs(byte* source, int sourceSize, byte* destination, int capacity)
    {
        JobPlan plan = PlanJobs(sourceSize);
        if (plan.Count == 0)
        {
            return CompressFrame(source, sourceSize, destination, capacity);
        }

        if (plan.LongDistance)
        {
            BeginJobsMatcher(plan, source);
        }

        int op = 0;
        for (int job = 0; job < plan.Count; job++)
        {
            (RawSequence[]? sequences, int count) = plan.LongDistance ? GenerateJobSequences(plan, source, job) : (null, 0);
            int written;
            fixed (RawSequence* jobSequences = sequences)
            {
                written = CompressJob(
                    plan.Parameters, source, sourceSize, plan.Start(job), plan.Size(job), plan.PrefixSize(job), destination + op, capacity - op, AppendChecksum,
                    jobSequences, (nuint)count);
            }

            if (sequences is not null)
            {
                ArrayPool<RawSequence>.Shared.Return(sequences);
            }

            if (written < 0)
            {
                return -1;
            }

            op += written;
        }

        if (AppendChecksum)
        {
            if (capacity - op < FrameFormat.ChecksumSize)
            {
                return -1;
            }

            WriteChecksum(new ReadOnlySpan<byte>(source, sourceSize), new Span<byte>(destination + op, FrameFormat.ChecksumSize));
            op += FrameFormat.ChecksumSize;
        }

        return op;
    }

    /// <summary>
    /// libzstd's <c>ZSTDMT_compressionJob</c>: the job whose section of the frame's
    /// <paramref name="frameSize"/> bytes starts at <paramref name="start"/>, its prefix the
    /// <paramref name="prefixSize"/> bytes before it. The first writes the frame's header (declaring a
    /// checksum when <paramref name="checksum"/>), starting from the dictionary when there is one; the
    /// last flags its last block. The checksum itself is the frame's to write. The job's long-distance
    /// sequences, made by the frame's matcher, are offered to its blocks in turn.
    /// </summary>
    /// <returns>The size of the job's output, or -1 when it does not fit.</returns>
    private int CompressJob(
        in CompressionParameters parameters, byte* source, int frameSize, int start, int size, int prefixSize, byte* destination, int capacity, bool checksum,
        RawSequence* sequences, nuint sequenceCount)
    {
        byte* section = source + start;
        bool last = start + size == frameSize;
        int op = 0;
        int blockSizeMax;
        if (start == 0)
        {
            // ZSTD_compressBegin_internal for the frame's whole content (and the dictionary, by its
            // size), its header written by the first chunk.
            uint dictionaryId = 0;
            if (_dictionary is null)
            {
                BeginFrame(parameters, source, size, longDistance: false);
            }
            else
            {
                BeginDictionaryFrame(source, frameSize, inJob: true);
                dictionaryId = _dictionary.Id;
            }

            byte* header = stackalloc byte[FrameHeaderSizeMax];
            int headerSize = WriteFrameHeader(header, parameters.WindowLog, (ulong)frameSize, checksum, dictionaryId);
            if (headerSize > capacity)
            {
                return -1;
            }

            Unsafe.CopyBlockUnaligned(destination, header, (uint)headerSize);
            op = headerSize;
            blockSizeMax = parameters.BlockSizeMax(frameSize);
        }
        else
        {
            BeginJob(parameters, section - prefixSize, prefixSize, size);
            blockSizeMax = parameters.BlockSizeMax(size);
        }

        // ZSTD_referenceExternalSequences: the job's sequences, from the first.
        _jobSequences = sequences;
        _jobSequenceCount = sequenceCount;

        // ZSTD_compressContinue by four blocks: each call cuts its own blocks, the savings carried
        // from call to call as libzstd's consumed and produced sizes, the first job's header with them.
        long consumed = 0;
        long produced = 0;
        for (int chunkStart = 0; chunkStart < size; chunkStart += JobChunkSize)
        {
            int chunk = Math.Min(JobChunkSize, size - chunkStart);
            int written = CompressBlocks(
                section + chunkStart, chunk, destination + op, capacity - op, blockSizeMax, last && chunkStart + chunk == size, consumed - produced);
            if (written < 0)
            {
                return -1;
            }

            op += written;
            consumed += chunk;
            produced = op;
        }

        _jobSequenceCount = 0;
        return op;
    }

    /// <summary>
    /// The start of a job after the first (<c>ZSTD_compressBegin_advanced_internal</c> with
    /// <c>ZSTD_c_forceMaxWindow</c>): its prefix loaded as raw content (<c>ZSTD_dtlm_fast</c>) in the
    /// window, just before its section, no dictionary ending there; the repeat offsets cleared
    /// (<c>ZSTD_invalidateRepCodes</c>), the entropy tables fresh, the first block the frame's first.
    /// </summary>
    private void BeginJob(in CompressionParameters parameters, byte* prefix, int prefixSize, int size)
    {
        if (prefixSize < MatchFinder.HashReadSize)
        {
            // ZSTD_compress_insertDictionary: content under 8 bytes is not loaded.
            BeginFrame(parameters, prefix + prefixSize, size, longDistance: false);
        }
        else
        {
            BeginFrame(parameters, prefix, prefixSize + size, longDistance: false);
            CompressionDictionary.LoadContent(ref _matchState, prefix, (nuint)prefixSize, preparedDictionary: false);
            _matchState.LoadedDictEnd = 0;
        }

        uint* rep = _previous.Rep;
        rep[0] = 0;
        rep[1] = 0;
        rep[2] = 0;
    }
}
