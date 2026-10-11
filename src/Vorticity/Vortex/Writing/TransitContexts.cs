using System.Threading;
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
/// a context past it is disposed.
/// </remarks>
internal static class TransitContexts
{
    private const int Capacity = 8;

    private static readonly ScanContext?[] Contexts = new ScanContext?[Capacity];
    private static readonly Lock Gate = new Lock();
    private static int _count;

    /// <summary>A context an earlier writer gave back, or a new one when none waits.</summary>
    internal static ScanContext Rent()
    {
        lock (Gate)
        {
            if (_count > 0)
            {
                ScanContext context = Contexts[--_count]!;
                Contexts[_count] = null;
                return context;
            }
        }

        return new ScanContext([]);
    }

    /// <summary>Resets <paramref name="context"/> and keeps it for a later writer, or disposes it past the bound.</summary>
    internal static void Return(ScanContext context)
    {
        context.ResetBatch();
        context.Types.Clear();
        lock (Gate)
        {
            if (_count < Capacity)
            {
                Contexts[_count++] = context;
                return;
            }
        }

        context.Dispose();
    }
}
