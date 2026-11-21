using System;
using System.Threading;
using System.Threading.Tasks;

namespace Vorticity;

/// <summary>
/// The room one part of a job reserved in its share of the job's budget before it takes it: an input of a merge before it
/// reads a chunk's segments, decodes what a read of the chunk decodes whole, or decodes a batch. The bytes are counted
/// under the budget from the moment they are reserved, and the blocks the part rents then take them first
/// (<see cref="Take"/>), so that what it was told it may hold is there when it takes it. A job's writer reserves the same
/// way, on the job's flow, a block's rows before it copies them and a chunk's before it lays them out.
/// </summary>
/// <remarks>
/// <para>
/// A size is reserved where it is read, never where it is guessed: a segment's bytes in the file's footer, a child's decoded
/// bytes in its chunk's header. A step whose size no file says, a batch's decode, reserves what the heaviest before it
/// held, takes more when the budget holds it, and when a block is refused stops, reserves what it lacked and runs again.
/// </para>
/// <para>
/// Work that can wait asks with <see cref="TryRaise"/> and is not started when the room is not there: a window decoded
/// ahead, a slice merged ahead, a block in flight past the one that must go. Work the job cannot go on without asks with
/// <see cref="RaiseAsync"/>, on the flow that drives the job: the job then gives back what it started ahead, a level at a
/// time (<see cref="MakeRoom"/>), and is told no only once nothing of that is left, which ends its merge at the key it
/// reached. No block is refused a part that reserved it, and none is waited for inside a decode.
/// </para>
/// <para>
/// What the steps under way reserved and have not closed is kept apart (<see cref="Close"/>): a step that ends gives back
/// what it reserved and no block took, the reservations of the steps beside it left whole.
/// </para>
/// </remarks>
internal sealed class MemoryReserve
{
    private readonly QueryMemoryBudget _budget;

    // Reserved in the budget and taken by no block yet.
    private long _spare;

    // What the steps under way reserved, closed by none yet: the most the spare is kept at.
    private long _pending;

    private long _raised;
    private long _heldBack;
    private long _madeRoom;

    /// <summary>A reserve in <paramref name="budget"/>, the budget the blocks that take from it are reserved in.</summary>
    internal MemoryReserve(QueryMemoryBudget budget) => _budget = budget;

    /// <summary>
    /// Has the job give back what it started ahead, level after level from 0, on the flow that drives it; false once no
    /// such level is left. Null for a reserve whose work never waits.
    /// </summary>
    internal Func<int, ValueTask<bool>>? MakeRoom { get; set; }

    /// <summary>
    /// Whether the work the part belongs to admits the bytes it is given more of the part's, the budget holding them or
    /// not: what a job asks of its inputs, so that they leave its writer what it has yet to take. A reservation it does
    /// not admit is not granted, as one the budget does not hold. Null for a part its budget alone bounds.
    /// </summary>
    internal Func<long, bool>? Admits { get; set; }

    /// <summary>The bytes reserved and taken by no block yet.</summary>
    internal long Spare => Math.Max(0, Volatile.Read(ref _spare));

    /// <summary>The bytes reserved since the reserve was made, for a job's measures and the tests.</summary>
    internal long Raised => Interlocked.Read(ref _raised);

    /// <summary>The reservations not granted at once: work not started ahead, or a step that had the job make room.</summary>
    internal long HeldBack => Interlocked.Read(ref _heldBack);

    /// <summary>The times a reservation had the job give back what it started ahead.</summary>
    internal long MadeRoom => Interlocked.Read(ref _madeRoom);

    /// <summary>
    /// Reserves <paramref name="bytes"/> more when the budget holds them now; false, and nothing reserved, otherwise. What
    /// work that can wait asks: looked at first without the governor's last resorts, a collection among them, which a
    /// window decoded ahead is not worth.
    /// </summary>
    internal bool TryRaise(long bytes)
    {
        if (bytes <= 0)
        {
            return true;
        }

        if (Admits?.Invoke(bytes) == false || !_budget.CanReserve(bytes) || !_budget.TryReserve(bytes))
        {
            Interlocked.Increment(ref _heldBack);
            return false;
        }

        Reserved(bytes);
        return true;
    }

    /// <summary>Reserves <paramref name="bytes"/> when the work admits them (<see cref="Admits"/>) and the budget holds them.</summary>
    private bool TryGrant(long bytes) => Admits?.Invoke(bytes) != false && _budget.TryReserve(bytes);

    /// <summary>
    /// Reserves <paramref name="bytes"/> more, having the job give back what it started ahead while the budget does not
    /// hold them (<see cref="MakeRoom"/>); false, and nothing reserved, once nothing of that is left. On the flow that
    /// drives the job alone.
    /// </summary>
    internal async ValueTask<bool> RaiseAsync(long bytes)
    {
        if (bytes <= 0)
        {
            return true;
        }

        if (TryGrant(bytes))
        {
            Reserved(bytes);
            return true;
        }

        Interlocked.Increment(ref _heldBack);
        if (MakeRoom is not { } makeRoom)
        {
            return false;
        }

        for (int level = 0; ; level++)
        {
            if (!await makeRoom(level).ConfigureAwait(false))
            {
                return false;
            }

            Interlocked.Increment(ref _madeRoom);
            if (TryGrant(bytes))
            {
                Reserved(bytes);
                return true;
            }
        }
    }

    private void Reserved(long bytes)
    {
        Interlocked.Add(ref _pending, bytes);
        Interlocked.Add(ref _spare, bytes);
        Interlocked.Add(ref _raised, bytes);
        long most = Volatile.Read(ref _mostAtOnce);
        while (bytes > most)
        {
            long seen = Interlocked.CompareExchange(ref _mostAtOnce, bytes, most);
            if (seen == most)
            {
                break;
            }

            most = seen;
        }
    }

    private long _mostAtOnce;

    /// <summary>The most bytes one reservation asked for and was granted: what the heaviest thing the part held whole was read to weigh.</summary>
    internal long MostAtOnce => Interlocked.Read(ref _mostAtOnce);

    /// <summary>
    /// A step that reserved <paramref name="raised"/> in all ended: what no block took of it goes back to the budget, the
    /// reservations of the steps still under way kept.
    /// </summary>
    internal void Close(long raised)
    {
        if (raised <= 0)
        {
            return;
        }

        long pending = Math.Max(0, Interlocked.Add(ref _pending, -raised));
        long spare = Volatile.Read(ref _spare);
        while (spare > pending)
        {
            long seen = Interlocked.CompareExchange(ref _spare, pending, spare);
            if (seen == spare)
            {
                _budget.Release(spare - pending);
                return;
            }

            spare = seen;
        }
    }

    /// <summary>
    /// Takes up to <paramref name="bytes"/> of the spare for a block being rented, and returns what it took: bytes the
    /// budget counts already, which the block's account does not reserve again
    /// (<see cref="Buffers.MemoryAccount.TryTake(long, long)"/>).
    /// </summary>
    internal long Take(long bytes)
    {
        long spare = Volatile.Read(ref _spare);
        while (true)
        {
            long take = Math.Min(bytes, spare);
            if (take <= 0)
            {
                return 0;
            }

            long seen = Interlocked.CompareExchange(ref _spare, spare - take, spare);
            if (seen == spare)
            {
                return take;
            }

            spare = seen;
        }
    }

    /// <summary>
    /// Whether the budget holds <paramref name="bytes"/> more now, nothing reserved: what a block takes past what its part
    /// reserved for it, a size no file said, when the room is there.
    /// </summary>
    internal bool Holds(long bytes) => Admits?.Invoke(bytes) != false && _budget.CanReserve(bytes);

    /// <summary>Puts back <paramref name="bytes"/> taken for a block its account then refused.</summary>
    internal void PutBack(long bytes)
    {
        if (bytes > 0)
        {
            Interlocked.Add(ref _spare, bytes);
        }
    }

    /// <summary>Gives back everything reserved and not taken: the part is done.</summary>
    internal void End()
    {
        Interlocked.Exchange(ref _pending, 0);
        long spare = Interlocked.Exchange(ref _spare, 0);
        if (spare > 0)
        {
            _budget.Release(spare);
        }
    }
}
