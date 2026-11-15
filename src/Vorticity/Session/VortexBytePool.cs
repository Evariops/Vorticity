using System;
using System.Buffers;
using Vorticity.Buffers;

namespace Vorticity;

/// <summary>
/// The library's pool of blocks of bytes, lent as the arrays an <see cref="ArrayPool{T}"/> lends: for a store, a sink or a
/// transport written against the public surface, whose buffers then come from the pool every block the library rents comes
/// from, kept and let go as the library's are.
/// </summary>
/// <remarks>
/// <para>
/// An array lent holds at least the bytes asked for, a power of two of them from 16 bytes to a gigabyte, and whatever its
/// last renter left. Past a gigabyte, it is allocated at its length and let go when given back.
/// </para>
/// <para>
/// The pool keeps what is given back for the next rent and lets go of a size class no rent has asked for over a minute,
/// after a collection of the oldest generation, and of every class when the machine's memory load is high. An array given
/// back may not be read or written after: it is the next renter's.
/// </para>
/// </remarks>
public sealed class VortexBytePool : ArrayPool<byte>
{
    private VortexBytePool()
    {
    }

    /// <summary>The pool, the library's own.</summary>
    public static VortexBytePool Instance { get; } = new VortexBytePool();

    /// <summary>An array of <paramref name="minimumLength"/> bytes at least; the empty array for none.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="minimumLength"/> is negative, or past the longest array.</exception>
    public override byte[] Rent(int minimumLength) => Blocks.Rent<byte>(minimumLength).Array();

    /// <summary>Gives <paramref name="array"/> back, cleared first when <paramref name="clearArray"/> says so.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="array"/> is null.</exception>
    public override void Return(byte[] array, bool clearArray = false)
    {
        ArgumentNullException.ThrowIfNull(array);
        if (clearArray)
        {
            Array.Clear(array);
        }

        Blocks.ReturnBytes(array);
    }
}
