using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// The bytes every spill of the process wrote to its scratch, the core's and the lanes': past a tenth
/// of the free space their directories leave, a spill fails rather than fill the disk.
/// </summary>
internal static class SpillSpace
{
    private static long s_spilled;

    /// <summary>
    /// Counts <paramref name="bytes"/> more written to <paramref name="directory"/>; false, nothing
    /// counted, when the free space left would fall under a tenth of what it and the spills make together.
    /// </summary>
    internal static bool TryClaim(string directory, long bytes) => TryClaim(GroupCore.Free(directory), bytes);

    /// <summary>As <see cref="TryClaim(string, long)"/>, the directory's free space read already, or null when the system does not tell.</summary>
    internal static bool TryClaim(long? free, long bytes)
    {
        long spilled = Interlocked.Add(ref s_spilled, bytes);
        if (free is long left && left - bytes < (left + spilled) / 10)
        {
            Interlocked.Add(ref s_spilled, -bytes);
            return false;
        }

        return true;
    }

    /// <summary>Gives back <paramref name="bytes"/> a spill counted: its file is gone.</summary>
    internal static void Release(long bytes) => Interlocked.Add(ref s_spilled, -bytes);
}

/// <summary>
/// What a query's lanes wrote to its scratch when its budget held their tables no more and the core could
/// not take them: a file a lane or a slot, the runs written to them, and what they cost. Disposed with the
/// query, its files gone and the scratch it held given back.
/// </summary>
internal sealed class SpillScope : IDisposable
{
    private readonly VortexSessionOptions _options;
    private readonly Lock _gate = new Lock();
    private readonly List<SpillFile> _files = [];
    private string? _directory;
    private long _written;
    private long _read;
    private int _runs;
    private long _firstHeld = -1;

    internal SpillScope(VortexSessionOptions options, QueryMemory memory)
    {
        _options = options;
        Memory = memory;
    }

    /// <summary>The query's memory, which the spill's buffers are counted in.</summary>
    internal QueryMemory Memory { get; }

    /// <summary>The runs written.</summary>
    internal int Runs => Volatile.Read(ref _runs);

    /// <summary>The bytes the runs took in the scratch.</summary>
    internal long WrittenBytes => Interlocked.Read(ref _written);

    /// <summary>The bytes read back from them.</summary>
    internal long ReadBytes => Interlocked.Read(ref _read);

    /// <summary>
    /// What the query held of its budget when its first run was written, a share of the ceiling; -1 before.
    /// The lanes ask for a doubling of every table that still grows before each batch, and a run comes
    /// when that no longer fits: what the tables held then is what the budget was used to.
    /// </summary>
    internal double FirstShare => _firstHeld < 0 ? -1 : (double)_firstHeld / Math.Max(1, Memory.Ceiling);

    /// <summary>A file of its own in the session's scratch directory, never one in memory.</summary>
    /// <exception cref="VortexMemoryException">The directory lies on a file system in memory.</exception>
    internal SpillFile NewFile()
    {
        lock (_gate)
        {
            if (_directory is null)
            {
                string directory = _options.ScratchDirectory ?? Path.GetTempPath();
                if (GroupCore.InMemory(directory))
                {
                    throw new VortexMemoryException(
                        $"The group by needs to spill, and its scratch directory {directory} lies on a file system in memory, which would take the memory spilling gives back. Give its session a ScratchDirectory on a disk.");
                }

                _directory = directory;
            }

            SpillFile file = new SpillFile(this, _directory, _options.ScratchBudget);
            _files.Add(file);
            return file;
        }
    }

    /// <summary>A run of <paramref name="bytes"/> written: the first notes what the query held then.</summary>
    internal void Ran(long bytes)
    {
        Interlocked.Add(ref _written, bytes);
        if (Interlocked.Increment(ref _runs) == 1)
        {
            _firstHeld = Memory.Held;
        }
    }

    /// <summary><paramref name="bytes"/> read back.</summary>
    internal void Read(long bytes) => Interlocked.Add(ref _read, bytes);

    /// <summary>Every file closed and gone, the scratch they held given back.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            foreach (SpillFile file in _files)
            {
                file.Dispose();
            }

            _files.Clear();
        }
    }
}

/// <summary>
/// A lane's file in the scratch, which only that lane appends to: its runs one after the other, read back
/// at the end by whoever merges a part. Each byte written counts against a tenth of the directory's free
/// space and the host's scratch budget, given back when the file goes.
/// </summary>
internal sealed class SpillFile : IDisposable
{
    private readonly SpillScope _scope;
    private readonly string _directory;
    private readonly ScratchBudget? _budget;
    private readonly RunScratch _scratch;
    private long _claimed;

    // The directory's free space when last read, and what the file had written then: read again once
    // the file has written a 64th of it since, 64 MiB at least. A read is a walk of the system's mounts,
    // which a page of a few kilobytes written at a time would pay over and over.
    private long? _free;
    private long _freeAt = -1;

    internal SpillFile(SpillScope scope, string directory, ScratchBudget? budget)
    {
        _scope = scope;
        _directory = directory;
        _budget = budget;
        _scratch = new RunScratch(memoryBudget: 0, directory);
    }

    /// <summary>The bytes written.</summary>
    internal long Length => _scratch.Length;

    /// <summary>Appends <paramref name="bytes"/>.</summary>
    /// <returns>Where they start.</returns>
    /// <exception cref="VortexMemoryException">The directory's free space or the host's scratch budget does not take them.</exception>
    internal ValueTask<long> AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        long length = bytes.Length;
        if (_freeAt < 0 || _claimed - _freeAt > Math.Max(64L << 20, (_free ?? 0) / 64))
        {
            _free = GroupCore.Free(_directory);
            _freeAt = _claimed;
        }

        if (!SpillSpace.TryClaim(_free - (_claimed - _freeAt), length))
        {
            throw _scope.Memory.Exceeded("spill of a group by", -1, length);
        }

        if (_budget is { } budget && !budget.TryReserve(length))
        {
            SpillSpace.Release(length);
            throw GroupCore.ScratchExceeded(budget, "spill of a group by", length);
        }

        _claimed += length;
        return _scratch.AppendAsync(bytes, cancellationToken);
    }

    /// <summary>Reads <paramref name="destination"/>'s length of bytes from <paramref name="offset"/>; several readers at once.</summary>
    internal ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        _scope.Read(destination.Length);
        return _scratch.ReadAsync(offset, destination, cancellationToken);
    }

    public void Dispose()
    {
        SpillSpace.Release(_claimed);
        _budget?.Release(_claimed);
        _claimed = 0;
        _scratch.Dispose();
    }
}

/// <summary>
/// One run: what a lane's table, or a distinct count's set, held when it went to the scratch, in
/// <see cref="Sections"/> sections one after the other in its file, a section the groups or values whose
/// hash has its top byte, the cut a merge in parts nests in. A part of 2^b of them is a stretch of
/// sections, one read.
/// </summary>
internal sealed class SpillRun(SpillFile file)
{
    /// <summary>The sections of a run: the top byte of a hash.</summary>
    internal const int Sections = 256;

    /// <summary>The bits a run's sections take of a hash.</summary>
    internal const int SectionBits = 8;

    internal SpillFile File { get; } = file;

    /// <summary>Where each section starts in the file, then where the last ends.</summary>
    internal long[] Starts { get; } = new long[Sections + 1];

    /// <summary>The entries of each section.</summary>
    internal int[] Counts { get; } = new int[Sections];

    /// <summary>Whether a distinct count's set held the value of zero bits, which no section holds.</summary>
    internal bool Zero { get; set; }

    /// <summary>The bytes of the run.</summary>
    internal long Bytes => Starts[Sections] - Starts[0];

    /// <summary>The sections of part <paramref name="part"/> of 2^<paramref name="bits"/>: [first, past).</summary>
    internal static (int First, int Past) Of(int part, int bits) => (part << (SectionBits - bits), (part + 1) << (SectionBits - bits));

    /// <summary>The entries of sections [<paramref name="first"/>, <paramref name="past"/>).</summary>
    internal long Entries(int first, int past)
    {
        long entries = 0;
        for (int section = first; section < past; section++)
        {
            entries += Counts[section];
        }

        return entries;
    }

    /// <summary>The bytes of sections [<paramref name="first"/>, <paramref name="past"/>) read, into a buffer of the caller's, grown as it must.</summary>
    internal async ValueTask<int> ReadAsync(int first, int past, SpillBuffer buffer, CancellationToken cancellationToken)
    {
        long length = Starts[past] - Starts[first];
        if (length == 0)
        {
            return 0;
        }

        Memory<byte> into = buffer.Ensure(checked((int)length));
        await File.ReadAsync(Starts[first], into, cancellationToken).ConfigureAwait(false);
        return (int)length;
    }
}

/// <summary>
/// The bytes of a spill on their way to its file, or read back from it: a page, appended to the file once
/// full, grown past it only for a section that must be written whole, and counted in the query's memory,
/// past its budget if it must, given back when it is let go.
/// </summary>
internal sealed class SpillBuffer
{
    private readonly QueryMemory? _memory;
    private byte[] _bytes = [];
    private int _length;

    /// <summary>A buffer of a page of <see cref="PageOf"/>'s bytes, taken now.</summary>
    internal SpillBuffer(QueryMemory? memory, int lanes = 1)
    {
        _memory = memory;
        Page = PageOf(memory, lanes);
        Grow(Page);
    }

    /// <summary>
    /// The bytes a lane writes or reads at a time: a 64th of the budget's ceiling shared by the lanes, a
    /// power of two from 4 KiB to a megabyte. Taken past the budget while the lane gives back what it
    /// spills, every lane's at once is a sixty-fourth of the ceiling at most.
    /// </summary>
    internal static int PageOf(QueryMemory? memory, int lanes)
    {
        long share = (memory?.Ceiling ?? (1L << 26)) / 64 / Math.Max(1, lanes);
        return (int)BitOperations.RoundUpToPowerOf2((ulong)Math.Clamp(share, 4096, 1 << 20));
    }

    /// <summary>The bytes of its page.</summary>
    internal int Page { get; }

    internal int Length => _length;

    /// <summary>Whether its page is full: what it holds goes to the file before more is written.</summary>
    internal bool Full => _length >= Page;

    /// <summary>What was written, the first byte first.</summary>
    internal ReadOnlyMemory<byte> Written => _bytes.AsMemory(0, _length);

    /// <summary>The room left in its page, written next, then <see cref="Advance"/>.</summary>
    internal Span<byte> Room => _bytes.AsSpan(_length, Math.Max(0, Page - _length));

    /// <summary><paramref name="bytes"/> of <see cref="Room"/> written.</summary>
    internal void Advance(int bytes) => _length += bytes;

    /// <summary>The bytes read back into it.</summary>
    internal ReadOnlySpan<byte> Span(int length) => _bytes.AsSpan(0, length);

    internal void Clear() => _length = 0;

    /// <summary>Room for <paramref name="length"/> bytes, from the first, what it held before lost.</summary>
    internal Memory<byte> Ensure(int length)
    {
        Grow(length);
        return _bytes.AsMemory(0, length);
    }

    /// <summary><paramref name="bytes"/> more, written next.</summary>
    internal Span<byte> Take(int bytes)
    {
        Grow(_length + bytes);
        Span<byte> taken = _bytes.AsSpan(_length, bytes);
        _length += bytes;
        return taken;
    }

    internal void Write<T>(T value)
        where T : unmanaged => MemoryMarshal.Write(Take(Unsafe.SizeOf<T>()), in value);

    internal void Write<T>(ReadOnlySpan<T> values)
        where T : unmanaged => MemoryMarshal.AsBytes(values).CopyTo(Take(values.Length * Unsafe.SizeOf<T>()));

    /// <summary>Bytes after their length.</summary>
    internal void WriteBytes(ReadOnlySpan<byte> value)
    {
        Write(value.Length);
        value.CopyTo(Take(value.Length));
    }

    /// <summary>The buffer let go, its bytes given back.</summary>
    internal void Release()
    {
        _memory?.LetGo(_bytes.Length);
        _bytes = [];
        _length = 0;
    }

    private void Grow(int length)
    {
        if (length <= _bytes.Length)
        {
            return;
        }

        int grown = Scratch.Capacity(length, _bytes.Length);

        // Taken past the budget when it must: a spill is there to give memory back.
        _memory?.Force(grown - _bytes.Length);
        _memory?.Measure(grown - _bytes.Length);
        Array.Resize(ref _bytes, grown);
    }
}

/// <summary>The bytes of a section read back, a value after the other, in the order they were written.</summary>
internal ref struct SpillReader(ReadOnlySpan<byte> bytes)
{
    private readonly ReadOnlySpan<byte> _bytes = bytes;
    private int _at;

    /// <summary>Whether every byte was read.</summary>
    internal readonly bool End => _at == _bytes.Length;

    internal T Read<T>()
        where T : unmanaged
    {
        T value = MemoryMarshal.Read<T>(_bytes[_at..]);
        _at += Unsafe.SizeOf<T>();
        return value;
    }

    /// <summary>The next <paramref name="length"/> bytes as they lie.</summary>
    internal ReadOnlySpan<byte> Bytes(int length)
    {
        ReadOnlySpan<byte> bytes = _bytes.Slice(_at, length);
        _at += length;
        return bytes;
    }

    /// <summary>Bytes written after their length (<see cref="SpillBuffer.WriteBytes"/>).</summary>
    internal ReadOnlySpan<byte> Bytes() => Bytes(Read<int>());

    /// <summary>The next values, copied into <paramref name="into"/>.</summary>
    internal void Read<T>(Span<T> into)
        where T : unmanaged => Bytes(into.Length * Unsafe.SizeOf<T>()).CopyTo(MemoryMarshal.AsBytes(into));
}
