using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Vorticity.Buffers;

/// <summary>
/// What keeps memory from one operation to the next, so that the next goes faster: the pool's blocks kept for the next
/// rent, a cache of segments, a dataset handle's caches. It says what it is and what it holds now.
/// </summary>
internal interface IHeldMemory
{
    /// <summary>What it is, as the gauges tag it: <c>pool</c>, <c>segments</c>, <c>datasets</c>.</summary>
    string Holder { get; }

    /// <summary>The bytes it holds now.</summary>
    long HeldBytes { get; }
}

/// <summary>
/// Every <see cref="IHeldMemory"/> of the process, held weakly: a holder nothing else references goes with its memory, and
/// leaves the list with it. What the gauges and <see cref="VortexDiagnostics.GetMemoryReport"/> read.
/// </summary>
internal static class HeldMemory
{
    private static readonly ConditionalWeakTable<IHeldMemory, object?> Holders = new ConditionalWeakTable<IHeldMemory, object?>();

    /// <summary>Lists <paramref name="holder"/> until it is let go, or <see cref="Unregister"/> takes it out.</summary>
    internal static void Register(IHeldMemory holder) => Holders.AddOrUpdate(holder, null);

    /// <summary>Takes <paramref name="holder"/> out of the list, once it holds nothing more.</summary>
    internal static void Unregister(IHeldMemory holder) => Holders.Remove(holder);

    /// <summary>The bytes each kind of holder holds now, and how many holders of each kind there are.</summary>
    internal static Dictionary<string, (long Bytes, int Holders)> ByHolder()
    {
        Dictionary<string, (long Bytes, int Holders)> held = new Dictionary<string, (long Bytes, int Holders)>(System.StringComparer.Ordinal);
        foreach (KeyValuePair<IHeldMemory, object?> pair in Holders)
        {
            IHeldMemory holder = pair.Key;
            (long bytes, int count) = held.GetValueOrDefault(holder.Holder);
            held[holder.Holder] = (bytes + holder.HeldBytes, count + 1);
        }

        return held;
    }

    /// <summary>The bytes the holders of type <typeparamref name="T"/> hold now.</summary>
    internal static long BytesOf<T>()
        where T : IHeldMemory
    {
        long bytes = 0;
        foreach (KeyValuePair<IHeldMemory, object?> pair in Holders)
        {
            if (pair.Key is T holder)
            {
                bytes += holder.HeldBytes;
            }
        }

        return bytes;
    }
}
