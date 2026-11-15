using System;
using System.Globalization;
using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// Whom a block of the pool is counted for: a query, a piece of work under a budget that is not a query, or the memory the
/// process keeps for later. A block rented under an account takes its bytes there as it is rented (<see cref="Rent"/>), and
/// gives them back when its last holder returns it to the pool.
/// </summary>
/// <remarks>
/// <para>
/// Taking reserves the bytes in the account's budget and measures them, as a table's arrays are: the rest of the process is
/// read as the heap less what is measured, and a block reserved without being measured would count twice.
/// </para>
/// <para>
/// A block that can leave for a caller, a scan's or a result's batches, rents under an account that ends, its query's or its
/// work's: a caller that drops a batch without giving it back leaves its block to the collector, which gives nothing back to
/// any account. An account that ends gives back everything at its end and ignores what comes back after. The process's
/// account never ends, and only the blocks the library gives back itself, in a <c>finally</c> or at its own end, rent under
/// it.
/// </para>
/// </remarks>
internal abstract class MemoryAccount
{
    /// <summary>
    /// The memory the process keeps for later, its caches': reserved nowhere, it counts with the rest of the process once a
    /// full collection has read it, and is given back in the order the caches let go of it.
    /// </summary>
    internal static MemoryAccount Kept { get; } = new Uncounted("kept memory");

    /// <summary>
    /// The rents whose account the refactoring has yet to name (PLAN-MEMORY, step 3): counted nowhere, as every rent was
    /// before it. The residue ratchet holds its uses to zero by the end of the step.
    /// </summary>
    internal static MemoryAccount Unassigned { get; } = new Uncounted("unassigned");

    /// <summary>Takes <paramref name="bytes"/>, reserved and measured; false, and nothing taken, when the account refuses them.</summary>
    internal abstract bool TryTake(long bytes);

    /// <summary>Gives back <paramref name="bytes"/> taken; nothing once the account has given back everything at its end.</summary>
    internal abstract void Give(long bytes);

    /// <summary>The exception a rent of <paramref name="bytes"/> the account refused throws.</summary>
    internal abstract VortexMemoryException Refused(long bytes);

    /// <summary>
    /// A block of the library's pool of at least <paramref name="length"/> bytes aligned to <paramref name="alignment"/>, its
    /// bytes counted here until its last holder gives it back.
    /// </summary>
    /// <exception cref="VortexMemoryException">The account refuses the block's bytes.</exception>
    internal BlockOwner Rent(int length, int alignment) => BlockPool.Shared.Rent(this, length, alignment);

    /// <summary>An account that counts nothing: the memory the process keeps, and the rents not yet given theirs.</summary>
    private sealed class Uncounted(string name) : MemoryAccount
    {
        internal override bool TryTake(long bytes) => true;

        internal override void Give(long bytes)
        {
        }

        internal override VortexMemoryException Refused(long bytes) =>
            new VortexMemoryException(string.Create(CultureInfo.InvariantCulture, $"The {name} account refuses nothing, yet {bytes:N0} bytes were refused."));
    }
}

/// <summary>
/// The account of work under a budget that is not a query: the process's for the blocks the library gives back itself
/// (<see cref="QueryMemoryBudget.Account"/>), and, as a <see cref="WorkAccount"/>, a piece of work that ends.
/// </summary>
internal class BudgetAccount : MemoryAccount
{
    private readonly string _work;

    /// <summary>The account of <paramref name="work"/>, which reserves in <paramref name="budget"/>.</summary>
    internal BudgetAccount(QueryMemoryBudget budget, string work)
    {
        Budget = budget;
        _work = work;
    }

    /// <summary>The budget the account reserves in.</summary>
    internal QueryMemoryBudget Budget { get; }

    internal override bool TryTake(long bytes)
    {
        if (!Budget.TryReserve(bytes))
        {
            return false;
        }

        Budget.Measure(bytes);
        return true;
    }

    internal override void Give(long bytes)
    {
        Budget.Release(bytes);
        Budget.Measure(-bytes);
    }

    internal override VortexMemoryException Refused(long bytes)
    {
        QueryMemoryBudget process = QueryMemoryBudget.Process;
        return new VortexMemoryException(string.Create(
            CultureInfo.InvariantCulture,
            $"The {_work} asks for a block of {bytes:N0} bytes, past its memory budget of {Budget.CeilingBytes:N0} bytes, {Budget.ReservedBytes:N0} of which is held; the process leaves its work {process.CeilingBytes:N0} bytes, the rest of its memory holding {process.RestBytes:N0}."));
    }
}

/// <summary>
/// The account of a piece of work that ends, a scan or a writer outside a query: what its blocks hold is given back at its end
/// (<see cref="Close"/>), whatever its holders still hold, and what comes back after is ignored. Nothing holding it, the
/// collector's finalizer gives back what a piece of work never closed held.
/// </summary>
internal sealed class WorkAccount : BudgetAccount, IDisposable
{
    // What the work's blocks hold now; -1 once the account has given back everything.
    private long _held;

    /// <summary>The account of <paramref name="work"/>, which reserves in <paramref name="budget"/>.</summary>
    internal WorkAccount(QueryMemoryBudget budget, string work)
        : base(budget, work)
    {
    }

    ~WorkAccount() => Close();

    /// <summary>The bytes the work's blocks hold now.</summary>
    internal long Held => Math.Max(0, Volatile.Read(ref _held));

    internal override bool TryTake(long bytes)
    {
        long held = Volatile.Read(ref _held);
        while (true)
        {
            if (held < 0)
            {
                // Ended: a block rented now is the caller's alone, counted nowhere, as a block given back after the end is.
                return true;
            }

            if (!base.TryTake(bytes))
            {
                return false;
            }

            long seen = Interlocked.CompareExchange(ref _held, held + bytes, held);
            if (seen == held)
            {
                return true;
            }

            base.Give(bytes);
            held = seen;
        }
    }

    internal override void Give(long bytes)
    {
        long held = Volatile.Read(ref _held);
        while (held >= 0)
        {
            long seen = Interlocked.CompareExchange(ref _held, held - bytes, held);
            if (seen == held)
            {
                base.Give(bytes);
                return;
            }

            held = seen;
        }
    }

    /// <summary>Gives back everything the work's blocks hold: what comes back after is ignored.</summary>
    internal void Close()
    {
        long held = Interlocked.Exchange(ref _held, -1);
        if (held > 0)
        {
            base.Give(held);
        }
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        Close();
        GC.SuppressFinalize(this);
    }
}
