using System.Buffers;
using System.Runtime.CompilerServices;

namespace Vorticity.Dataset;

/// <summary>Arrays rented from the shared pool by a stream that grows them, where the empty array stands for none rented yet.</summary>
internal static class Pooled
{
    /// <summary>
    /// Makes <paramref name="rented"/> hold at least <paramref name="length"/> items, returning the
    /// one it held for a larger one when it does not; what it held is not carried over.
    /// </summary>
    internal static void Grow<T>(ref T[] rented, int length)
    {
        if (rented.Length < length)
        {
            Return(rented);
            rented = ArrayPool<T>.Shared.Rent(length);
        }
    }

    /// <summary>Returns <paramref name="rented"/> to the pool, unless it is the empty array, which never came from it.</summary>
    internal static void Return<T>(T[] rented)
    {
        if (rented.Length > 0)
        {
            ArrayPool<T>.Shared.Return(rented, RuntimeHelpers.IsReferenceOrContainsReferences<T>());
        }
    }
}
