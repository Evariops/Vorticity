using System;
using System.Buffers.Binary;

namespace Vorticity.Sealing;

/// <summary>One epoch of a sealed object: where its frames start, and the plaintext they hold.</summary>
/// <param name="FramesStart">The object offset of its first frame.</param>
/// <param name="PlainStart">The plaintext offset of its first byte, the end of the epoch before it.</param>
/// <param name="PlainLength">Its plaintext bytes.</param>
/// <param name="Salt">The salt its key is derived with.</param>
/// <param name="Commitment">The commitment to that key.</param>
internal readonly record struct SealedEpoch(long FramesStart, long PlainStart, long PlainLength, ReadOnlyMemory<byte> Salt, ReadOnlyMemory<byte> Commitment)
{
    /// <summary>The plaintext offset past its last byte.</summary>
    internal long PlainEnd => PlainStart + PlainLength;
}

/// <summary>
/// The layout a sealed object's trailer describes: its descriptor, its epochs, and the plaintext they
/// hold one after the other.
/// </summary>
/// <remarks>
/// The trailer is the descriptor again, the count of epochs as a 32-bit integer, an entry per epoch
/// (where its frames start and its plaintext length, as 64-bit integers, then for every epoch after
/// the first its salt and its commitment), the trailer's own length as a 32-bit integer, and
/// <c>VXSE</c>. Everything is checked here against the object's length, so that no later offset
/// computed from it can escape the object: a hostile trailer is a format error, never a wild read.
/// </remarks>
internal sealed class SealedLayout
{
    private SealedLayout(SealDescriptor descriptor, SealedEpoch[] epochs, long objectLength, long trailerStart)
    {
        Descriptor = descriptor;
        Epochs = epochs;
        ObjectLength = objectLength;
        TrailerStart = trailerStart;
        PlainLength = epochs[^1].PlainEnd;
    }

    internal SealDescriptor Descriptor { get; }

    internal SealedEpoch[] Epochs { get; }

    /// <summary>The sealed object's length.</summary>
    internal long ObjectLength { get; }

    /// <summary>Where the last trailer starts.</summary>
    internal long TrailerStart { get; }

    /// <summary>The plaintext the object holds.</summary>
    internal long PlainLength { get; }

    internal int FrameSize => Descriptor.FrameSize;

    /// <summary>The header's length, where the first epoch's frames start.</summary>
    internal int HeaderLength => SealedFormat.HeaderPrefixBytes + Descriptor.Length;

    /// <summary>Whether <paramref name="end"/>, the last bytes of an object, ends as a sealed object does.</summary>
    internal static bool EndsSealed(ReadOnlySpan<byte> end) =>
        end.Length >= SealedFormat.TrailerMagic.Length && end[^SealedFormat.TrailerMagic.Length..].SequenceEqual(SealedFormat.TrailerMagic);

    /// <summary>The length of the trailer that <paramref name="end"/>, the object's last bytes, closes.</summary>
    /// <exception cref="VortexFormatException">The bytes do not end a sealed object, or the length cannot be a trailer's.</exception>
    internal static int TrailerLength(ReadOnlySpan<byte> end, long objectLength)
    {
        if (end.Length < SealedFormat.TrailerSuffixBytes || !EndsSealed(end))
        {
            throw new VortexFormatException("Malformed sealed object: it does not end with VXSE.");
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(end[^SealedFormat.TrailerSuffixBytes..]);
        if (length < SealedFormat.TrailerSuffixBytes || length > objectLength - SealedFormat.HeaderPrefixBytes || length > SealedFormat.MaxTrailerBytes)
        {
            throw new VortexFormatException($"Malformed sealed object: a trailer of {length} bytes does not fit an object of {objectLength}.");
        }

        return (int)length;
    }

    /// <summary>Reads the trailer that ends the object.</summary>
    /// <param name="trailer">The trailer, exactly: the bytes from its start to the object's end.</param>
    /// <param name="objectLength">The sealed object's length.</param>
    /// <returns>The layout.</returns>
    /// <exception cref="VortexFormatException">The trailer is not one this object can end with.</exception>
    /// <exception cref="VortexUnsupportedException">The descriptor is of a version this build does not know.</exception>
    internal static SealedLayout Read(ReadOnlySpan<byte> trailer, long objectLength)
    {
        long trailerStart = objectLength - trailer.Length;
        SealDescriptor descriptor = SealDescriptor.Read(trailer, out int at);
        if (at > trailer.Length - SealedFormat.TrailerSuffixBytes)
        {
            throw Malformed("its trailer's descriptor runs into the trailer's length");
        }

        ReadOnlySpan<byte> rest = trailer[at..^SealedFormat.TrailerSuffixBytes];
        if (rest.Length < 4)
        {
            throw Malformed("its trailer holds no epoch count");
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(rest);
        rest = rest[4..];
        if (count is 0 or > SealedFormat.MaxEpochs
            || rest.Length != SealedFormat.EpochEntryBytes + ((long)(count - 1) * SealedFormat.AppendedEpochEntryBytes))
        {
            throw Malformed($"its trailer lists {count} epochs in {rest.Length} bytes");
        }

        int frameSize = descriptor.FrameSize;
        long headerLength = SealedFormat.HeaderPrefixBytes + descriptor.Length;
        SealedEpoch[] epochs = new SealedEpoch[count];
        long plainStart = 0;
        long earliest = headerLength;
        for (int e = 0; e < count; e++)
        {
            long framesStart = BinaryPrimitives.ReadInt64LittleEndian(rest);
            long plainLength = BinaryPrimitives.ReadInt64LittleEndian(rest[8..]);
            ReadOnlyMemory<byte> salt;
            ReadOnlyMemory<byte> commitment;
            if (e == 0)
            {
                rest = rest[SealedFormat.EpochEntryBytes..];
                salt = descriptor.Bytes.AsMemory(descriptor.Length - SealedFormat.CommitmentBytes - SealedFormat.SaltBytes, SealedFormat.SaltBytes);
                commitment = descriptor.Bytes.AsMemory(descriptor.Length - SealedFormat.CommitmentBytes);
                if (framesStart != headerLength)
                {
                    throw Malformed($"its first epoch starts at {framesStart}, where its header ends at {headerLength}");
                }
            }
            else
            {
                salt = rest.Slice(SealedFormat.EpochEntryBytes, SealedFormat.SaltBytes).ToArray();
                commitment = rest.Slice(SealedFormat.EpochEntryBytes + SealedFormat.SaltBytes, SealedFormat.CommitmentBytes).ToArray();
                rest = rest[SealedFormat.AppendedEpochEntryBytes..];
            }

            // Each epoch starts past the one before, its frames end before the next one or the
            // trailer, and it holds no more frames than one key may seal.
            if (plainLength < 0 || framesStart < earliest || framesStart > trailerStart
                || SealedFormat.FrameCount(plainLength, frameSize) > SealedFormat.MaxFramesPerEpoch
                || plainLength > trailerStart - framesStart)
            {
                throw Malformed($"its epoch {e} does not fit the object");
            }

            long framesEnd = framesStart + SealedFormat.CipherLength(plainLength, frameSize);
            if (framesEnd > trailerStart || (e == count - 1 && framesEnd != trailerStart))
            {
                throw Malformed($"the frames of its epoch {e} end at {framesEnd}, and its trailer starts at {trailerStart}");
            }

            epochs[e] = new SealedEpoch(framesStart, plainStart, plainLength, salt, commitment);
            plainStart += plainLength;
            earliest = framesEnd;
        }

        return new SealedLayout(descriptor, epochs, objectLength, trailerStart);
    }

    /// <summary>The epoch whose plaintext holds <paramref name="plainOffset"/>, which lies inside the object's plaintext.</summary>
    internal int EpochOf(long plainOffset)
    {
        SealedEpoch[] epochs = Epochs;
        int low = 0;
        int high = epochs.Length - 1;
        while (low < high)
        {
            int middle = (low + high + 1) >>> 1;
            if (epochs[middle].PlainStart <= plainOffset)
            {
                low = middle;
            }
            else
            {
                high = middle - 1;
            }
        }

        // An empty epoch ahead of the one that holds the offset starts at the same plaintext offset.
        while (low + 1 < epochs.Length && epochs[low].PlainEnd <= plainOffset)
        {
            low++;
        }

        return low;
    }

    private static VortexFormatException Malformed(string why) => new VortexFormatException($"Malformed sealed object: {why}.");
}
