using System;
using System.Buffers;
using System.IO.Compression;
using System.Threading;

namespace Vorticity.Writing;

/// <summary>
/// The frames of one zstd column as the items of a <see cref="WorkFan"/>, each compressed into a
/// room of its own in the destination, for the caller to close up behind them: a frame is the same
/// bytes whichever thread and encoder write it, so the column is the one a single thread writes.
/// </summary>
/// <remarks>
/// Rented by a workspace and given back with it, so that its frame table serves every column of
/// every file. A frame takes an encoder from the process's and gives it back, a lock and a reset
/// against the tens of microseconds the frame costs. The input and the destination are pointers the
/// caller keeps pinned until <see cref="Compress"/> returns, which is after every frame has run.
/// </remarks>
internal sealed unsafe class ZstdFrames : IFanWork
{
    /// <summary>
    /// Numbers kept per frame: its first input byte, its end, its room's offset, the room, what it
    /// produced, and its values, which only the caller reads.
    /// </summary>
    internal const int Fields = 6;

    /// <summary>The frame tables kept for later workspaces: what a few writers at once hold, one each.</summary>
    private const int Kept = 8;

    private static readonly ZstdFrames?[] Pool = new ZstdFrames?[Kept];
    private static readonly Lock Gate = new Lock();
    private static int _pooled;

    private int[] _plan = [];
    private byte* _input;
    private byte* _output;
    private int _failed;

    private ZstdFrames()
    {
    }

    /// <summary>The columns compressed across threads since the rental, whether their frames were kept or not.</summary>
    internal int Columns { get; private set; }

    /// <summary>A frame table an earlier workspace gave back, or a new one.</summary>
    internal static ZstdFrames Rent()
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
        return frames;
    }

    /// <summary>Keeps <paramref name="frames"/> for a later workspace, or lets it go past the bound.</summary>
    internal static void Return(ZstdFrames frames)
    {
        lock (Gate)
        {
            if (_pooled < Kept)
            {
                Pool[_pooled++] = frames;
            }
        }
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
    /// Compresses the <paramref name="count"/> frames <see cref="Plan"/> describes on
    /// <paramref name="fan"/>'s threads, each from its input range into its room, recording what
    /// each produced in the plan.
    /// </summary>
    /// <param name="fan">The threads.</param>
    /// <param name="input">The values, pinned.</param>
    /// <param name="output">The destination, pinned, as long as every room.</param>
    /// <param name="count">The frames.</param>
    /// <returns>Whether every frame compressed.</returns>
    internal bool Compress(WorkFan fan, byte* input, byte* output, int count)
    {
        _input = input;
        _output = output;
        _failed = 0;
        fan.Run(this, count);
        _input = null;
        _output = null;
        Columns++;
        return _failed == 0;
    }

    /// <summary>Compresses frame <paramref name="item"/>.</summary>
    void IFanWork.Run(WorkFan fan, int item)
    {
        int at = item * Fields;
        int from = _plan[at];
        int to = _plan[at + 1];
        ReadOnlySpan<byte> source = new ReadOnlySpan<byte>(_input + from, to - from);
        Span<byte> room = new Span<byte>(_output + _plan[at + 2], _plan[at + 3]);
        ZstandardEncoder encoder = ZstdEncoders.Rent();
        try
        {
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
        finally
        {
            ZstdEncoders.Return(encoder);
        }
    }
}
