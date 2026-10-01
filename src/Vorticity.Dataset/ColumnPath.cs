using System;
using System.Text;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Types;

namespace Vorticity.Dataset;

/// <summary>
/// The <c>.</c>-separated paths the dataset names its columns by, walked a segment at a time: each
/// segment is encoded on the stack and looked up by its UTF-8 bytes, so a walk allocates nothing
/// where splitting the path would allocate an array and a string per segment.
/// </summary>
internal static class ColumnPath
{
    /// <summary>The index of the field of <paramref name="type"/> named <paramref name="name"/>, or -1; -1 too when the type has no fields.</summary>
    internal static int IndexOf(DType type, ReadOnlySpan<char> name)
    {
        if (type.Kind != DTypeKind.Struct)
        {
            return -1;
        }

        Span<byte> stack = stackalloc byte[256];
        using Scratch<byte> buffer = new Scratch<byte>(Encoding.UTF8.GetByteCount(name), stack);
        int written = Encoding.UTF8.GetBytes(name, buffer.Span);
        return type.IndexOfField(buffer.Span[..written]);
    }

    /// <summary>The column of <paramref name="schema"/> that <paramref name="path"/> starts from, or -1.</summary>
    internal static int TopOf(DType schema, string path)
    {
        int dot = path.IndexOf('.', StringComparison.Ordinal);
        return IndexOf(schema, dot < 0 ? path : path.AsSpan(0, dot));
    }

    /// <summary>Whether <paramref name="path"/> may hold a null in <paramref name="schema"/>: a field on the way, or the column, is nullable, or the path names none.</summary>
    internal static bool MayBeNull(DType schema, string path)
    {
        DType current = schema;
        ReadOnlySpan<char> rest = path;
        foreach (Range segment in rest.Split('.'))
        {
            int field = IndexOf(current, rest[segment]);
            if (field < 0)
            {
                return true;
            }

            current = current.GetField(field);
            if (current.IsNullable)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>The node of <paramref name="path"/> in a batch, by the names of its structs, or -1 when it names none.</summary>
    internal static int NodeOf(RecordBatch batch, string path)
    {
        DType at = batch.DType;
        int node = batch.RootIndex;
        ReadOnlySpan<char> rest = path;
        foreach (Range segment in rest.Split('.'))
        {
            int field = IndexOf(at, rest[segment]);
            if (field < 0)
            {
                return -1;
            }

            node = batch.Arena.GetNode(node).GetFieldIndex(field);
            at = at.GetField(field);
        }

        return node;
    }
}
