using System;
using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace Vorticity.Aggregating;

/// <summary>
/// The working arrays the writer rents by the megabyte, a blob assembled, the keys of a part sorted, a compressor's trials:
/// below <see cref="SharedBytes"/>, the shared array pool's, which keeps them by thread and by core; past it, the process's
/// shelf's (<see cref="ArrayShelf.Retained"/>), at the power of two above the length asked, which a full collection does not
/// trim and which finds an array given back on another thread again.
/// </summary>
/// <remarks>
/// A build of the client's log records rented such arrays on one lane and gave them back on another, and the shared pool,
/// trimmed at every full collection, made 1.05 GB of them again over 18 M rows, 58 bytes a row (M7e). An array is given back
/// by the rule it was rented by; one rented from the shared pool below the line and given back at a power of two past it
/// goes to the shelf, which takes any array.
/// </remarks>
internal static class WorkArrays
{
    /// <summary>The bytes from which an array is the shelf's rather than the shared pool's.</summary>
    internal const int SharedBytes = 1 << 20;

    /// <summary>An array of <paramref name="length"/> elements at least, its contents whatever the last renter left.</summary>
    internal static T[] Rent<T>(int length)
    {
        // Judged at the power of two the array has, as its return is: the shared pool's are of such lengths too.
        if (length > 1 << 30 || (long)BitOperations.RoundUpToPowerOf2((uint)Math.Max(length, 1)) * Unsafe.SizeOf<T>() < SharedBytes)
        {
            return ArrayPool<T>.Shared.Rent(length);
        }

        return ArrayShelf.Retained.Take<T>((int)BitOperations.RoundUpToPowerOf2((uint)length), zeroed: false);
    }

    /// <summary>Gives <paramref name="array"/> back, cleared first when <paramref name="clearArray"/>; once only.</summary>
    internal static void Return<T>(T[] array, bool clearArray = false)
    {
        if ((long)array.Length * Unsafe.SizeOf<T>() < SharedBytes || array.Length > 1 << 30)
        {
            ArrayPool<T>.Shared.Return(array, clearArray);
            return;
        }

        // The shelf clears an array of references itself.
        if (clearArray && !RuntimeHelpers.IsReferenceOrContainsReferences<T>())
        {
            Array.Clear(array);
        }

        ArrayShelf.Retained.Give(array);
    }
}
