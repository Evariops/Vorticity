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
/// What a table's arrays would take more if more entries came, a group or a value each: what a lane
/// whose table spills asks the budget for before a batch, the batch's rows at most, rather than a doubling
/// of every array it holds, which a table at its size for good never makes.
/// </summary>
internal static class TableGrowth
{
    /// <summary>
    /// The bytes of the array that replaces one of <paramref name="length"/> elements of
    /// <paramref name="elementBytes"/> each, holding <paramref name="count"/> and taking
    /// <paramref name="more"/>, when they pass the <paramref name="usable"/> elements it fills before it
    /// doubles; 0 when they do not.
    /// </summary>
    internal static long Of(long count, long more, long length, long usable, int elementBytes)
    {
        if (count + more <= usable)
        {
            return 0;
        }

        long grown = Math.Max(1, length);
        long fill = Math.Max(1, usable);
        while (count + more > fill)
        {
            grown *= 2;
            fill *= 2;
        }

        return grown * elementBytes;
    }
}

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

    /// <summary>The lanes' tables written one at a time: what writing one takes past the budget is given back with it before the next.</summary>
    internal SemaphoreSlim Writing { get; } = new SemaphoreSlim(1, 1);

    /// <summary>The lanes that retire under pressure, rather than write their tables, while others still run.</summary>
    internal LaneRetirement Retirement { get; } = new LaneRetirement();

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
/// A sketch of the distinct keys of a table, HyperLogLog's: 4 096 registers of a byte, each the most
/// leading zeros its keys' hashes show past the twelve bits that pick it, plus one. Two sketches merge by
/// their registers' maxima, the union's; an estimate errs by about 1.6 %. What tells, before a lane's
/// table is merged into another, how many of its keys that one lacks.
/// </summary>
internal sealed class KeySketch
{
    private const int Bits = 12;
    private readonly byte[] _registers = new byte[1 << Bits];

    /// <summary>Adds the keys of <paramref name="hashes"/>, their hashes under the merge's seed; a 0, a null group's, left out.</summary>
    internal void Add(ReadOnlySpan<ulong> hashes)
    {
        foreach (ulong hash in hashes)
        {
            if (hash == 0)
            {
                continue;
            }

            int register = (int)(hash >> (64 - Bits));
            byte rank = (byte)(BitOperations.LeadingZeroCount((hash << Bits) | (1UL << (Bits - 1))) + 1);
            if (rank > _registers[register])
            {
                _registers[register] = rank;
            }
        }
    }

    /// <summary>The keys of <paramref name="other"/> added: the union's sketch.</summary>
    internal void Merge(KeySketch other)
    {
        for (int i = 0; i < _registers.Length; i++)
        {
            _registers[i] = Math.Max(_registers[i], other._registers[i]);
        }
    }

    /// <summary>The distinct keys estimated, of this sketch's and <paramref name="with"/>'s union when given.</summary>
    internal double Estimate(KeySketch? with = null)
    {
        int m = _registers.Length;
        double sum = 0;
        int zeros = 0;
        for (int i = 0; i < m; i++)
        {
            int rank = with is null ? _registers[i] : Math.Max(_registers[i], with._registers[i]);
            sum += Math.ScaleB(1.0, -rank);
            zeros += rank == 0 ? 1 : 0;
        }

        double estimate = 0.7213 / (1 + (1.079 / m)) * m * m / sum;
        return estimate <= 2.5 * m && zeros > 0 ? m * Math.Log((double)m / zeros) : estimate;
    }

    /// <summary>The sketch of <paramref name="keys"/>, their hashes taken past the budget a moment, as the table they come from is about to be let go.</summary>
    internal static KeySketch Of(GroupKeys keys, QueryMemory? memory)
    {
        KeySketch sketch = new KeySketch();
        long bytes = (long)keys.Count * sizeof(ulong);
        memory?.Force(bytes);
        memory?.Measure(bytes);
        try
        {
            ulong[] hashes = new ulong[keys.Count];
            keys.Hashes(hashes);
            sketch.Add(hashes);
        }
        finally
        {
            memory?.LetGo(bytes);
        }

        return sketch;
    }
}

/// <summary>
/// The lanes that retire under pressure: a lane whose table the budget holds no more, while others still
/// run, merges it into the table the retired lanes share and goes on with an empty one, taking no more
/// ranges past its own. A key every lane meets is then held once, not once a lane, and nothing goes to the
/// scratch while that table grows by less than the lanes give back. Once the merges show the lanes' keys
/// apart, half of what they merge or more new to the shared table, merging frees nothing and costs the
/// lanes that stop: the lanes spill instead.
/// </summary>
internal sealed class LaneRetirement
{
    /// <summary>The groups merged into the shared table past which what they brought new to it decides whether retiring pays.</summary>
    private const long Judged = 65_536;

    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private long _merged;
    private long _grew;

    // The lanes that still take ranges: the last of them never retires, so that every range is read. And
    // the sketch of the shared table's keys.
    private int _takers;
    private KeySketch? _sketch;

    /// <summary>The table the retired lanes share, a partition no lane folds into; null before the first retires.</summary>
    internal AggregationPartition? Retired { get; private set; }

    /// <summary>The lanes that retired.</summary>
    internal int Lanes { get; private set; }

    /// <summary>The pass starts on <paramref name="lanes"/> lanes, each taking ranges.</summary>
    internal void Begin(int lanes) => _takers = lanes;

    /// <summary>
    /// <paramref name="lane"/>'s table merged into the shared one, the lane's emptied; the first lane's
    /// handed over whole. Whether it was, and whether the lane retires: one already
    /// <paramref name="retiring"/>, or another that takes ranges with it; the last one goes on, its table
    /// empty. A lane alone starts no shared table. When the shared table cannot take the lane's, the larger
    /// of the two goes to the scratch: the lane's, nothing merged; or the shared one, the lane's taking its
    /// place. Once merging no longer pays, the shared table goes to the scratch, and nothing is merged.
    /// </summary>
    internal async ValueTask<(bool Merged, bool Retires)> TakeAsync(AggregationPartition lane, bool retiring, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int count = lane.Keys!.Count;
            if (_merged >= Judged && 2 * _grew >= _merged)
            {
                // The memory the shared table holds is given back once, its groups to the scratch.
                if (Retired is { Keys.Count: > 0 } stale)
                {
                    await stale.EvictAsync(cancellationToken).ConfigureAwait(false);
                }

                return (false, false);
            }

            bool retires = retiring || _takers > 1;
            if (Retired is null)
            {
                if (!retires)
                {
                    return (false, false);
                }

                _sketch = KeySketch.Of(lane.Keys, lane.Memory);
                Retired = lane.Sibling();
                lane.HandTo(Retired);
            }
            else if (count > 0)
            {
                // The keys the shared table lacks, as the sketches tell, a tenth more for their error: what
                // it grows by. All of the lane's would ask for a doubling where the lanes meet the same keys.
                AggregationPartition retired = Retired;
                KeySketch sketch = KeySketch.Of(lane.Keys, lane.Memory);
                double lacks = Math.Max(0, sketch.Estimate(_sketch) - _sketch!.Estimate());
                int grows = (int)Math.Min(count, (1.1 * lacks) + 1_024);

                // A shared table the budget cannot double fills tighter first: it holds every key past its
                // growth point, where a spill would write it whole.
                if (lane.Memory is { } memory && !memory.CanGrow(retired.GrowthFor(lane, grows))
                    && !(retired.Keys!.Squeeze(grows) && memory.CanGrow(retired.GrowthFor(lane, grows))))
                {
                    if (retired.Keys!.Count <= count)
                    {
                        return (false, false);
                    }

                    await retired.EvictAsync(cancellationToken).ConfigureAwait(false);
                    lane.HandTo(retired);
                    _sketch = sketch;
                }
                else
                {
                    int before = retired.Keys!.Count;
                    int[]? numbers = null;
                    retired.MergeFrom(lane, ref numbers);
                    retired.Recount();
                    _sketch.Merge(sketch);
                    _merged += count;
                    _grew += retired.Keys.Count - before;
                    lane.Emptied();
                }
            }

            if (!retiring && retires)
            {
                Lanes++;
                _takers--;
            }

            return (true, retires);
        }
        finally
        {
            _gate.Release();
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

    /// <summary><paramref name="bytes"/> more, written next: the room checked here, grown out of line.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Span<byte> Take(int bytes)
    {
        int at = _length;
        if (at + bytes > _bytes.Length)
        {
            Grow(at + bytes);
        }

        _length = at + bytes;
        return _bytes.AsSpan(at, bytes);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void Write<T>(T value)
        where T : unmanaged => MemoryMarshal.Write(Take(Unsafe.SizeOf<T>()), in value);

    internal void Write<T>(ReadOnlySpan<T> values)
        where T : unmanaged => MemoryMarshal.AsBytes(values).CopyTo(Take(values.Length * Unsafe.SizeOf<T>()));

    /// <summary>Bytes after their length, one room taken for both.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal void WriteBytes(ReadOnlySpan<byte> value)
    {
        Span<byte> taken = Take(sizeof(int) + value.Length);
        MemoryMarshal.Write(taken, value.Length);
        value.CopyTo(taken[sizeof(int)..]);
    }

    /// <summary>Writes <paramref name="value"/> again at <paramref name="at"/>, written before: a count known once what it counts is written.</summary>
    internal void Patch<T>(int at, T value)
        where T : unmanaged => MemoryMarshal.Write(_bytes.AsSpan(at), in value);

    /// <summary>The buffer let go, its bytes given back.</summary>
    internal void Release()
    {
        _memory?.LetGo(_bytes.Length);
        _bytes = [];
        _length = 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
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

/// <summary>
/// The parts of a group by whose lanes wrote their tables to the scratch, read back from the runs: a part
/// is a stretch of every run's sections, each section's groups made into slots of their own and merged
/// into the part's table as a lane's table merges into a part's. The table grows from a shelf of its own
/// under the query's memory, each array reserved before it comes: runs that share keys count more
/// entries than the part holds groups. The files go once the last part is merged.
/// </summary>
internal sealed class RunMerge(
    SpillRun[] runs, GroupKeys kind, AggregationPlan plan, AggregateSlot?[] settled, int[] inputs, ScanSource source, QueryMemory memory, SpillScope spill,
    int bits, int readBytes) : PartSource
{
    internal override int Parts => 1 << bits;

    internal override (GroupKeys Keys, AggregateSlot[] Slots) Empty() => (kind.ForSpill(null), AggregationPartition.NewSlots(plan, settled, source));

    internal override async ValueTask<(GroupKeys Keys, AggregateSlot[] Slots, long Reserved, long Measured)> MergeAsync(int part, CancellationToken cancellationToken)
    {
        (int first, int past) = SpillRun.Of(part, bits);

        // Each array reserved alone, nothing ahead: a quarter of a megabyte ahead a part outweighs the
        // small parts of a small budget.
        ArrayShelf shelf = new ArrayShelf(memory) { Exact = true };
        GroupKeys keys = kind.ForSpill(shelf);
        AggregateSlot[] slots = AggregationPartition.NewSlots(plan, settled, source, out _, shelf);
        SpillBuffer? buffer = null;
        long room = 0;
        try
        {
            buffer = new SpillBuffer(memory);
            foreach (SpillRun run in runs)
            {
                // As many of the run's sections at a time as a read of readBytes takes, one at least.
                int section = first;
                while (section < past)
                {
                    int end = section + 1;
                    while (end < past && run.Starts[end + 1] - run.Starts[section] <= readBytes)
                    {
                        end++;
                    }

                    int length = checked((int)(run.Starts[end] - run.Starts[section]));
                    if (length > 0)
                    {
                        await run.File.ReadAsync(run.Starts[section], buffer.Ensure(length), cancellationToken).ConfigureAwait(false);
                        MergeSections(run, section, end, buffer.Span(length), keys, slots);
                    }

                    section = end;
                }
            }

            buffer.Release();
            buffer = null;

            // The table as its shelf counts it, and as much again for the batches its builder makes of it.
            room = shelf.Out;
            if (!memory.TryGrow(room))
            {
                room = 0;
                throw memory.Exceeded("merge of a spilled group by", keys.Count, shelf.Out);
            }

            return (keys, slots, shelf.Reserved + room, shelf.Out);
        }
        catch
        {
            buffer?.Release();
            shelf.LetGo();
            memory.Shrink(room);
            throw;
        }
    }

    /// <summary>The groups of sections [<paramref name="first"/>, <paramref name="past"/>) of a run, read back into <paramref name="bytes"/>, merged into the part's table.</summary>
    private void MergeSections(SpillRun run, int first, int past, ReadOnlySpan<byte> bytes, GroupKeys keys, AggregateSlot[] slots)
    {
        SpillReader reader = new SpillReader(bytes);
        int[] map = [];
        for (int section = first; section < past; section++)
        {
            int count = run.Counts[section];
            if (count == 0)
            {
                continue;
            }

            // The section's keys, each to its group in the part; then its states into slots of their own,
            // groups 0 on, records first, then what each slot keeps apart; then merged.
            Scratch.Grow(ref map, count);
            Span<int> into = map.AsSpan(0, count);
            keys.ReadKeys(ref reader, into);
            AggregateSlot[] read = AggregationPartition.NewSlots(plan, settled, source, out GroupRecords? records);
            records?.Read(ref reader, count);
            for (int i = 0; i < read.Length; i++)
            {
                if (inputs[i] != AggregationPartition.Settled && read[i].StateBytes == 0)
                {
                    read[i].ReadStates(ref reader, count);
                }
            }

            ReadOnlySpan<int> all = Numbers.Upto(count);
            for (int i = 0; i < slots.Length; i++)
            {
                slots[i].EnsureGroups(keys.Count);
                if (inputs[i] != AggregationPartition.Settled)
                {
                    slots[i].MergeFrom(read[i], all, into);
                }
            }
        }
    }

    /// <summary>The runs' files closed and gone.</summary>
    internal override void Release() => spill.Dispose();

    internal override AggregationRun Finished(AggregationRun run) => run.Spilled(spill);
}
