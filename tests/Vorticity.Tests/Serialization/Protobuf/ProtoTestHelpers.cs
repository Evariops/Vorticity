using System;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Tests.Serialization.Protobuf;

/// <summary>
/// Shared plumbing for the Protobuf runtime tests.
/// </summary>
/// <remarks>
/// <see cref="ProtoWriter"/> is a mutable struct, so every helper takes it by <c>ref</c>: a
/// by-value copy would share the pooled array while keeping its own length counter, which is
/// exactly the bug these tests must not paper over.
/// </remarks>
internal static class ProtoTestHelpers
{
    internal delegate void WriteAction(ref ProtoWriter writer);

    /// <summary>Runs <paramref name="action"/> against a fresh writer and snapshots the bytes.</summary>
    internal static byte[] Write(WriteAction action, int initialCapacity = 64)
    {
        ProtoWriter writer = new ProtoWriter(initialCapacity);
        try
        {
            action(ref writer);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Encodes one bare varint (no tag).</summary>
    internal static byte[] Varint(ulong value) =>
        Write((ref ProtoWriter w) => w.WriteVarint(value));

    /// <summary>Encodes a bare tag varint, bypassing <see cref="ProtoWriter.WriteTag"/>'s validation.</summary>
    /// <remarks>
    /// The writer refuses to emit a group or a zero field number, so the malformed-tag tests have
    /// to build the tag by hand — that is the point of them.
    /// </remarks>
    internal static byte[] RawTag(int fieldNumber, int wireType) =>
        Varint(((ulong)(uint)fieldNumber << 3) | (uint)wireType);

    /// <summary>Concatenates byte runs.</summary>
    internal static byte[] Concat(params byte[][] parts)
    {
        int total = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            total += parts[i].Length;
        }

        byte[] result = new byte[total];
        int offset = 0;
        for (int i = 0; i < parts.Length; i++)
        {
            parts[i].CopyTo(result, offset);
            offset += parts[i].Length;
        }

        return result;
    }

    /// <summary>A deterministic filler payload; the value depends only on the index.</summary>
    internal static byte[] Payload(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31) + 7);
        }

        return bytes;
    }

    /// <summary>An independent varint length computation, for checking the writer.</summary>
    internal static int ExpectedVarintSize(ulong value)
    {
        int size = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            size++;
        }

        return size;
    }
}
