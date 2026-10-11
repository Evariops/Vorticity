using System;
using System.Buffers;
using System.IO.Hashing;
using System.Security.Cryptography;

namespace Vorticity.ObjectStorage.Http;

/// <summary>The checksums a put carries, over a body wherever it lies: the platform's, segment by segment.</summary>
internal static class Checksums
{
    /// <summary>The CRC-64/NVME of <paramref name="bytes"/>, which S3 and Azure check.</summary>
    internal static ulong Crc64Nvme(ReadOnlySequence<byte> bytes)
    {
        Crc64 crc = new Crc64(Crc64ParameterSet.Nvme);
        Append(crc, bytes);
        return crc.GetCurrentHashAsUInt64();
    }

    /// <summary>Appends <paramref name="bytes"/> to <paramref name="crc"/>: what keeps a whole object's CRC as its parts go, in order.</summary>
    internal static void Append(Crc64 crc, ReadOnlySequence<byte> bytes)
    {
        foreach (ReadOnlyMemory<byte> segment in bytes)
        {
            crc.Append(segment.Span);
        }
    }

    /// <summary>The CRC-32C of <paramref name="bytes"/>, which Cloud Storage checks.</summary>
    internal static uint Crc32C(ReadOnlySequence<byte> bytes)
    {
        Crc32 crc = new Crc32(Crc32ParameterSet.Crc32C);
        foreach (ReadOnlyMemory<byte> segment in bytes)
        {
            crc.Append(segment.Span);
        }

        return crc.GetCurrentHashAsUInt32();
    }

    /// <summary>The MD5 of <paramref name="bytes"/>, for a <c>Content-MD5</c> a service asks for; never for security.</summary>
    internal static byte[] Md5(ReadOnlySequence<byte> bytes)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        foreach (ReadOnlyMemory<byte> segment in bytes)
        {
            md5.AppendData(segment.Span);
        }

        return md5.GetHashAndReset();
    }
}

/// <summary>A segment of a sequence built over chunks a body holds: in memory, or mapped from a spool file.</summary>
internal sealed class SequenceSegment : ReadOnlySequenceSegment<byte>
{
    internal SequenceSegment(ReadOnlyMemory<byte> memory, long runningIndex)
    {
        Memory = memory;
        RunningIndex = runningIndex;
    }

    /// <summary>Chains <paramref name="memory"/> after this segment and returns its segment.</summary>
    internal SequenceSegment Append(ReadOnlyMemory<byte> memory)
    {
        SequenceSegment next = new SequenceSegment(memory, RunningIndex + Memory.Length);
        Next = next;
        return next;
    }
}
