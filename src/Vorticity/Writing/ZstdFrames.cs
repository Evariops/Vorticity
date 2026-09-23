using System;
using System.Buffers;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace Vorticity.Writing;

/// <summary>
/// Compresses the frames of one zstd column on several threads, each frame into a room of its own
/// in the destination, for the caller to close up behind them: a frame is the same bytes whichever
/// thread and encoder write it, so the column is the one a single thread writes.
/// </summary>
/// <remarks>
/// <para>
/// Rented by a workspace, used by one writer at a time, and given back for the next writer. The
/// helpers are made once and queued again for every column, so a column costs no allocation, nor a
/// file once one has run; the calling thread takes frames as well, and the helpers join only a
/// compression that is open. The caller waits for the last frame, closes the compression, then
/// waits for every helper that joined to leave, so that a helper the pool runs late finds it closed
/// and touches nothing, or finds the next one open, whichever writer's it is, and works for it.
/// </para>
/// <para>
/// The input and the destination are pointers the caller keeps pinned until
/// <see cref="Compress"/> returns, which is after every helper has left.
/// </para>
/// </remarks>
internal sealed unsafe class ZstdFrames
{
    /// <summary>
    /// Numbers kept per frame: its first input byte, its end, its room's offset, the room, what it
    /// produced, and its values, which only the caller reads.
    /// </summary>
    internal const int Fields = 6;

    /// <summary>The fan-outs kept for later writers: what a few writers at once hold, one each.</summary>
    private const int Kept = 8;

    private static readonly ZstdFrames?[] Pool = new ZstdFrames?[Kept];
    private static readonly Lock Gate = new Lock();
    private static int _pooled;

    private readonly ManualResetEventSlim _finished = new ManualResetEventSlim(false);
    private Helper[] _helpers = [];
    private int _lanes;
    private int[] _plan = [];
    private byte* _input;
    private byte* _output;
    private int _count;
    private int _next;
    private int _remaining;
    private int _active;
    private int _failed;
    private volatile bool _open;
    private Exception? _error;

    private ZstdFrames()
    {
    }

    /// <summary>The columns compressed across threads since the rental, whether their frames were kept or not.</summary>
    internal int Columns { get; private set; }

    /// <summary>A fan-out an earlier writer gave back, or a new one, with helpers for <paramref name="lanes"/> threads.</summary>
    /// <param name="lanes">The threads a column's frames may use, the calling one included.</param>
    internal static ZstdFrames Rent(int lanes)
    {
        ZstdFrames? frames = null;
        lock (Gate)
        {
            if (_pooled > 0)
            {
                frames = Pool[--_pooled];
                Pool[_pooled] = null;
            }
        }

        frames ??= new ZstdFrames();
        frames.Columns = 0;
        frames._lanes = lanes;
        int had = frames._helpers.Length;
        if (had < lanes - 1)
        {
            Array.Resize(ref frames._helpers, lanes - 1);
            for (int i = had; i < lanes - 1; i++)
            {
                frames._helpers[i] = new Helper(frames);
            }
        }

        return frames;
    }

    /// <summary>Keeps <paramref name="frames"/> for a later writer, or lets it go past the bound.</summary>
    internal static void Return(ZstdFrames frames)
    {
        lock (Gate)
        {
            if (_pooled < Kept)
            {
                Pool[_pooled++] = frames;
                return;
            }
        }

        frames._finished.Dispose();
    }

    /// <summary>
    /// The frame table of the next compression, <see cref="Fields"/> numbers per frame, for the
    /// caller to fill before <see cref="Compress"/>: kept from one column to the next.
    /// </summary>
    /// <param name="frames">The frames the column may have.</param>
    internal Span<int> Plan(int frames)
    {
        if (_plan.Length < frames * Fields)
        {
            _plan = new int[frames * Fields];
        }

        return _plan.AsSpan(0, frames * Fields);
    }

    /// <summary>
    /// Compresses the <paramref name="count"/> frames <see cref="Plan"/> describes, each from its
    /// input range into its room, recording what each produced in the plan.
    /// </summary>
    /// <param name="input">The values, pinned.</param>
    /// <param name="output">The destination, pinned, as long as every room.</param>
    /// <param name="count">The frames.</param>
    /// <param name="encoder">The calling thread's encoder: the workspace's.</param>
    /// <returns>Whether every frame compressed.</returns>
    internal bool Compress(byte* input, byte* output, int count, ZstandardEncoder encoder)
    {
        _input = input;
        _output = output;
        _count = count;
        _next = 0;
        _remaining = count;
        _failed = 0;
        _error = null;
        _finished.Reset();
        _open = true;

        int helpers = Math.Min(_lanes - 1, count - 1);
        for (int i = 0; i < helpers; i++)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_helpers[i], preferLocal: false);
        }

        Work(encoder);
        if (Volatile.Read(ref _remaining) > 0)
        {
            _finished.Wait();
        }

        _open = false;
        SpinWait spin = default;
        while (Volatile.Read(ref _active) != 0)
        {
            spin.SpinOnce();
        }

        if (_error is { } error)
        {
            ExceptionDispatchInfo.Throw(error);
        }

        Columns++;
        return _failed == 0;
    }

    private void Join()
    {
        Interlocked.Increment(ref _active);
        try
        {
            if (!_open)
            {
                return;
            }

            ZstandardEncoder encoder = ZstdEncoders.Rent();
            try
            {
                Work(encoder);
            }
            finally
            {
                ZstdEncoders.Return(encoder);
            }
        }
        finally
        {
            Interlocked.Decrement(ref _active);
        }
    }

    private void Work(ZstandardEncoder encoder)
    {
        int frame;
        while ((frame = Interlocked.Increment(ref _next) - 1) < _count)
        {
            try
            {
                int at = frame * Fields;
                int from = _plan[at];
                int to = _plan[at + 1];
                ReadOnlySpan<byte> source = new ReadOnlySpan<byte>(_input + from, to - from);
                Span<byte> room = new Span<byte>(_output + _plan[at + 2], _plan[at + 3]);
                encoder.Reset();
                OperationStatus status = encoder.Compress(source, room, out int consumed, out int written, isFinalBlock: true);
                if (status == OperationStatus.Done && consumed == source.Length && written > 0)
                {
                    _plan[at + 4] = written;
                }
                else
                {
                    Interlocked.Exchange(ref _failed, 1);
                }
            }
            catch (Exception error)
            {
                Interlocked.CompareExchange(ref _error, error, null);
            }
            finally
            {
                if (Interlocked.Decrement(ref _remaining) == 0)
                {
                    _finished.Set();
                }
            }
        }
    }

    private sealed class Helper(ZstdFrames owner) : IThreadPoolWorkItem
    {
        public void Execute() => owner.Join();
    }
}
