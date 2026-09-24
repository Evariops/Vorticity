using System;
using System.Threading;

namespace Vorticity.Arrays;

/// <summary>
/// The contexts scans decode their batches in, kept from one scan to the next: a context is a
/// dozen arrays -- its node and canonical arenas, its scalars, its segment set -- that a scan built
/// per lane and dropped, where the next scan, of the same file or of another, needed the same.
/// </summary>
/// <remarks>
/// A context comes back recycled, holding nothing of the scan or the file it served, and is bound
/// to the next scan's file when it is taken. The bound is what a take on a lane per processor
/// holds, three contexts a lane, and never less than a few scans at once hold, a lane each; a
/// context past it is disposed.
/// </remarks>
internal static class ScanContexts
{
    private static readonly int Capacity = Math.Max(16, 3 * Environment.ProcessorCount);

    private static readonly ScanContext?[] Contexts = new ScanContext?[Capacity];
    private static readonly Lock Gate = new Lock();
    private static int _count;

    /// <summary>A context an earlier scan gave back, bound to <paramref name="file"/>, or a new one.</summary>
    /// <param name="file">The file the scan reads.</param>
    internal static ScanContext Rent(VortexFile file)
    {
        ScanContext? context = null;
        lock (Gate)
        {
            if (_count > 0)
            {
                context = Contexts[--_count];
                Contexts[_count] = null;
            }
        }

        if (context is null)
        {
            return new ScanContext(file) { KeepsBlobs = true };
        }

        context.Rebind(file);
        return context;
    }

    /// <summary>Recycles <paramref name="context"/> and keeps it for a later scan, or disposes it past the bound.</summary>
    /// <param name="context">A context <see cref="Rent"/> gave, which nothing reads any more.</param>
    internal static void Return(ScanContext context)
    {
        context.Recycle();
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
