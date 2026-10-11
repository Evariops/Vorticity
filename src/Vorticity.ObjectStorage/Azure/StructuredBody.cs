using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.ObjectStorage.Hashing;
using Vorticity.ObjectStorage.Http;

namespace Vorticity.ObjectStorage.Azure;

/// <summary>
/// Azure's structured body, version 1: a header (its version, the message's length, its flags and its
/// segment count), then each segment (its number, its length, its bytes and, with the CRC flag, their
/// CRC-64), then the CRC-64 of the whole content. The segments' bytes are read straight into the
/// caller's memory and checked as they arrive, so verifying a read costs a CRC pass and no copy.
/// </summary>
internal static class StructuredBody
{
    /// <summary>The header a read sends to ask for it, and the service sends back with it.</summary>
    internal const string Header = "XSM/1.0; properties=crc64";

    private const int MessageHeaderBytes = 13;
    private const int SegmentHeaderBytes = 10;
    private const int CrcBytes = 8;
    private const ushort CrcFlag = 1;

    /// <summary>Reads a structured body's content into <paramref name="destination"/>, checking each segment and the whole.</summary>
    /// <returns>The content's bytes.</returns>
    /// <exception cref="StoreRetryException">The body was cut short, or a CRC does not match: the bytes are asked for again.</exception>
    internal static async ValueTask<int> ReadAsync(HttpTransport transport, HttpResponseMessage response, Memory<byte> destination, CancellationToken cancellationToken)
    {
        byte[] frame = ArrayPool<byte>.Shared.Rent(MessageHeaderBytes);
        try
        {
            await ExactAsync(transport, response, frame.AsMemory(0, MessageHeaderBytes), cancellationToken).ConfigureAwait(false);
            if (frame[0] != 1)
            {
                throw new ObjectStoreException($"The service answered a structured body of version {frame[0]}, which this store does not read.");
            }

            ulong flags = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(9, 2));
            int segments = BinaryPrimitives.ReadUInt16LittleEndian(frame.AsSpan(11, 2));
            bool checksummed = (flags & CrcFlag) != 0;
            ulong whole = ulong.MaxValue;
            int written = 0;
            for (int number = 1; number <= segments; number++)
            {
                await ExactAsync(transport, response, frame.AsMemory(0, SegmentHeaderBytes), cancellationToken).ConfigureAwait(false);
                ushort found = BinaryPrimitives.ReadUInt16LittleEndian(frame);
                ulong length = BinaryPrimitives.ReadUInt64LittleEndian(frame.AsSpan(2, 8));
                if (found != number || length > (ulong)(destination.Length - written))
                {
                    throw new ObjectStoreException($"The structured body's segment {found} of {length} bytes does not fit where segment {number} was due.");
                }

                Memory<byte> data = destination.Slice(written, (int)length);
                await ExactAsync(transport, response, data, cancellationToken).ConfigureAwait(false);
                written += (int)length;
                if (checksummed)
                {
                    ulong crc = ~Crc64Nvme.Update(ulong.MaxValue, data.Span);
                    whole = Crc64Nvme.Update(whole, data.Span);
                    await ExactAsync(transport, response, frame.AsMemory(0, CrcBytes), cancellationToken).ConfigureAwait(false);
                    if (BinaryPrimitives.ReadUInt64LittleEndian(frame) != crc)
                    {
                        throw new StoreRetryException($"segment {number} of a structured body arrived damaged: its CRC-64 does not match", RetryBudget.RetryCost);
                    }
                }
            }

            if (checksummed)
            {
                await ExactAsync(transport, response, frame.AsMemory(0, CrcBytes), cancellationToken).ConfigureAwait(false);
                if (BinaryPrimitives.ReadUInt64LittleEndian(frame) != ~whole)
                {
                    throw new StoreRetryException("a structured body arrived damaged: its whole CRC-64 does not match", RetryBudget.RetryCost);
                }
            }

            return written;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(frame);
        }
    }

    /// <summary>Encodes <paramref name="content"/> as a structured body in segments of <paramref name="segmentBytes"/>: what the service sends, for tests.</summary>
    internal static byte[] Encode(ReadOnlySpan<byte> content, int segmentBytes)
    {
        int segments = Math.Max(1, (content.Length + segmentBytes - 1) / segmentBytes);
        int length = MessageHeaderBytes + (segments * (SegmentHeaderBytes + CrcBytes)) + content.Length + CrcBytes;
        byte[] message = new byte[length];
        Span<byte> at = message;
        at[0] = 1;
        BinaryPrimitives.WriteUInt64LittleEndian(at[1..], (ulong)length);
        BinaryPrimitives.WriteUInt16LittleEndian(at[9..], CrcFlag);
        BinaryPrimitives.WriteUInt16LittleEndian(at[11..], (ushort)segments);
        at = at[MessageHeaderBytes..];
        for (int number = 1; number <= segments; number++)
        {
            ReadOnlySpan<byte> data = content.Slice((number - 1) * segmentBytes, Math.Min(segmentBytes, content.Length - ((number - 1) * segmentBytes)));
            BinaryPrimitives.WriteUInt16LittleEndian(at, (ushort)number);
            BinaryPrimitives.WriteUInt64LittleEndian(at[2..], (ulong)data.Length);
            data.CopyTo(at[SegmentHeaderBytes..]);
            BinaryPrimitives.WriteUInt64LittleEndian(at[(SegmentHeaderBytes + data.Length)..], Crc64Nvme.Hash(data));
            at = at[(SegmentHeaderBytes + data.Length + CrcBytes)..];
        }

        BinaryPrimitives.WriteUInt64LittleEndian(at, Crc64Nvme.Hash(content));
        return message;
    }

    private static async ValueTask ExactAsync(HttpTransport transport, HttpResponseMessage response, Memory<byte> into, CancellationToken cancellationToken)
    {
        int read = await transport.ReadBodyAsync(response, into, cancellationToken).ConfigureAwait(false);
        if (read != into.Length)
        {
            throw new StoreRetryException($"a structured body ended {into.Length - read} bytes short of a frame", RetryBudget.RetryCost);
        }
    }
}
