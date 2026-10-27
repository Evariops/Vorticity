using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Vorticity.Dataset;

namespace Vorticity.Cluster.Tcp;

/// <summary>
/// The frames of the reference transport (spec F, 6): a length of 4 bytes, little-endian, then the message. A request is
/// its kind, then its fields: a part's question, arguments, dataset location, version and rows, or a warming's location,
/// version and nodes. A reply is a status, then the partial's bytes, or the type and the message of the node's error. A
/// text is its length in 4 bytes then its UTF-8; bytes their length then themselves.
/// </summary>
internal static class TcpFrames
{
    /// <summary>A part to run.</summary>
    internal const byte Run = 1;

    /// <summary>The owned objects of a version to warm.</summary>
    internal const byte Warm = 2;

    /// <summary>The request done: a part's partial follows, a warming's nothing.</summary>
    internal const byte Done = 0;

    /// <summary>The node's error: its type and its message follow.</summary>
    internal const byte Failed = 1;

    /// <summary>The bytes of a frame's head: its length, then a reply's status.</summary>
    internal const int Head = 5;

    /// <summary>The longest message a side reads: a longer length is not this protocol's.</summary>
    internal static int Longest => Array.MaxLength;

    /// <summary>The frame asking for <paramref name="task"/>, in a rented buffer of which <paramref name="length"/> bytes are the frame.</summary>
    internal static byte[] RunRequest(ClusterTask task, out int length)
    {
        length = 4 + 1 + TextBytes(task.Question) + 4 + task.Arguments.Length + TextBytes(task.Location) + (3 * 8);
        byte[] frame = ArrayPool<byte>.Shared.Rent(length);
        FrameWriter writer = new FrameWriter(frame.AsSpan(0, length));
        writer.Int32(length - 4);
        writer.Byte(Run);
        writer.Text(task.Question);
        writer.Bytes(task.Arguments.Span);
        writer.Text(task.Location);
        writer.UInt64(task.Version);
        writer.Int64(task.Rows.Start);
        writer.Int64(task.Rows.End);
        return frame;
    }

    /// <summary>The frame asking a node to warm what it owns among <paramref name="nodes"/>, in a rented buffer of which <paramref name="length"/> bytes are the frame.</summary>
    internal static byte[] WarmRequest(string location, ulong version, IReadOnlyList<string> nodes, out int length)
    {
        length = 4 + 1 + TextBytes(location) + 8 + 4;
        foreach (string node in nodes)
        {
            length += TextBytes(node);
        }

        byte[] frame = ArrayPool<byte>.Shared.Rent(length);
        FrameWriter writer = new FrameWriter(frame.AsSpan(0, length));
        writer.Int32(length - 4);
        writer.Byte(Warm);
        writer.Text(location);
        writer.UInt64(version);
        writer.Int32(nodes.Count);
        foreach (string node in nodes)
        {
            writer.Text(node);
        }

        return frame;
    }

    /// <summary>The part a request of kind <see cref="Run"/> asks for, its arguments a slice of the request's bytes.</summary>
    /// <exception cref="InvalidDataException">The request is not this protocol's.</exception>
    internal static ClusterTask ReadRun(ref FrameReader request)
    {
        string question = request.Text();
        ReadOnlyMemory<byte> arguments = request.Bytes();
        string location = request.Text();
        ulong version = request.UInt64();
        long start = request.Int64();
        long end = request.Int64();
        if (start < 0 || end < start)
        {
            throw new InvalidDataException($"A part of rows [{start}, {end}), which no dataset holds.");
        }

        return new ClusterTask(question, arguments, location, version, new RowRange(start, end));
    }

    /// <summary>The location, version and nodes a request of kind <see cref="Warm"/> names.</summary>
    /// <exception cref="InvalidDataException">The request is not this protocol's.</exception>
    internal static (string Location, ulong Version, string[] Nodes) ReadWarm(ref FrameReader request)
    {
        string location = request.Text();
        ulong version = request.UInt64();
        int count = request.Int32();
        if (count < 1 || count > request.Left / 4)
        {
            throw new InvalidDataException($"A warming of {count} nodes, which its frame does not hold.");
        }

        string[] nodes = new string[count];
        for (int i = 0; i < nodes.Length; i++)
        {
            nodes[i] = request.Text();
        }

        return (location, version, nodes);
    }

    /// <summary>Writes the head of a reply into <paramref name="head"/>: its length, then <paramref name="status"/>, before <paramref name="payload"/> bytes.</summary>
    internal static void WriteHead(Span<byte> head, byte status, int payload)
    {
        BinaryPrimitives.WriteInt32LittleEndian(head, 1 + payload);
        head[4] = status;
    }

    /// <summary>The payload of a reply of status <see cref="Failed"/>: the type of <paramref name="failure"/>, then its message.</summary>
    internal static byte[] FailedPayload(Exception failure)
    {
        string type = failure.GetType().FullName ?? failure.GetType().Name;
        byte[] payload = new byte[TextBytes(type) + TextBytes(failure.Message)];
        FrameWriter writer = new FrameWriter(payload);
        writer.Text(type);
        writer.Text(failure.Message);
        return payload;
    }

    /// <summary>The type and the message of a node's error, from a reply's payload.</summary>
    /// <exception cref="InvalidDataException">The payload is not this protocol's.</exception>
    internal static (string Type, string Message) ReadFailed(byte[] payload)
    {
        FrameReader reader = new FrameReader(payload);
        return (reader.Text(), reader.Text());
    }

    private static int TextBytes(string text) => 4 + Encoding.UTF8.GetByteCount(text);
}

/// <summary>Writes a frame's fields one after the other into its bytes, sized beforehand.</summary>
internal ref struct FrameWriter
{
    private readonly Span<byte> _frame;
    private int _at;

    internal FrameWriter(Span<byte> frame) => _frame = frame;

    internal void Byte(byte value) => _frame[_at++] = value;

    internal void Int32(int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(_frame[_at..], value);
        _at += 4;
    }

    internal void UInt64(ulong value)
    {
        BinaryPrimitives.WriteUInt64LittleEndian(_frame[_at..], value);
        _at += 8;
    }

    internal void Int64(long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(_frame[_at..], value);
        _at += 8;
    }

    internal void Text(string value)
    {
        int written = Encoding.UTF8.GetBytes(value, _frame[(_at + 4)..]);
        Int32(written);
        _at += written;
    }

    internal void Bytes(ReadOnlySpan<byte> value)
    {
        Int32(value.Length);
        value.CopyTo(_frame[_at..]);
        _at += value.Length;
    }
}

/// <summary>Reads a frame's fields one after the other, a field past its end refused.</summary>
internal struct FrameReader(ReadOnlyMemory<byte> frame)
{
    private int _at;

    /// <summary>The bytes not read yet.</summary>
    internal readonly int Left => frame.Length - _at;

    internal byte Byte() => Take(1)[0];

    internal int Int32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));

    internal ulong UInt64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));

    internal long Int64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));

    internal string Text() => Encoding.UTF8.GetString(Take(Length()));

    /// <summary>A field of bytes, a slice of the frame's.</summary>
    internal ReadOnlyMemory<byte> Bytes()
    {
        int length = Length();
        ReadOnlyMemory<byte> bytes = frame.Slice(_at, length);
        _at += length;
        return bytes;
    }

    private int Length()
    {
        int length = Int32();
        return length >= 0 && length <= Left ? length : throw new InvalidDataException($"A field of {length} bytes, past the frame's end.");
    }

    private ReadOnlySpan<byte> Take(int count)
    {
        if (count > Left)
        {
            throw new InvalidDataException("A field past the frame's end.");
        }

        ReadOnlySpan<byte> taken = frame.Span.Slice(_at, count);
        _at += count;
        return taken;
    }
}
