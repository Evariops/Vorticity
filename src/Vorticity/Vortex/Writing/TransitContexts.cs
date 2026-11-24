using Vorticity.Arrays;

namespace Vorticity.Writing;

/// <summary>
/// The detached contexts writers hold their pending rows in, kept from one writer to the next: a
/// context's arenas keep their record tables through a reset, and a writer that holds many small
/// batches grows those tables once, where every new writer would grow them again.
/// </summary>
/// <remarks>
/// A context comes back reset, its canonical arena's storage returned to the memory pool, and its
/// dtypes cleared: the next writer may write another schema, and a context kept across schemas would
/// otherwise hold every dtype it ever copied. The bound is what a few writers at once hold -- two
/// for the transit, one for the small batches, one per column with a chunk target of its own -- and
/// a context past it is disposed. The registry of kept memory disposes those no writer took since its
/// last turn, and every one when a reservation needs the room.
/// </remarks>
internal static class TransitContexts
{
    private const int Capacity = 8;

    // Their record tables are not read for the registry's report: they count for nothing there, as the arenas kept do.
    private static readonly Buffers.KeptObjects<ScanContext> Contexts =
        new Buffers.KeptObjects<ScanContext>("transit contexts", Capacity, bytesEach: 0, static context => context.Dispose());

    /// <summary>A context an earlier writer gave back, or a new one when none waits, its blocks counted for <paramref name="account"/>, the writer's.</summary>
    internal static ScanContext Rent(Buffers.MemoryAccount account)
    {
        ScanContext context = Contexts.Take() ?? new ScanContext([]);
        context.Account = account;
        return context;
    }

    /// <summary>Resets <paramref name="context"/> and keeps it for a later writer, or disposes it past the bound.</summary>
    internal static void Return(ScanContext context)
    {
        context.ResetBatch();
        context.Types.Clear();
        context.Account = Buffers.MemoryAccount.Kept;
        if (!Contexts.Give(context))
        {
            context.Dispose();
        }
    }
}
