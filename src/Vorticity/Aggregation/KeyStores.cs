using System;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Writing;

namespace Vorticity.Aggregating;

/// <summary>
/// A key's values written to the store of a result column of the key column's own type, as the
/// index holds them: no conversion through a .NET type, a decimal narrowed back to its column's
/// width, a uuid back to its bytes.
/// </summary>
internal static class KeyStores
{
    /// <summary>Appends one value of a fixed-width key, as <see cref="FixedReader"/> read it, to <paramref name="store"/>.</summary>
    internal static void AppendFixed<TValue>(ColumnStore store, TValue value, StorageKind kind)
        where TValue : unmanaged
    {
        ColumnStore leaf = store.Leaf;
        if (kind == StorageKind.Uuid)
        {
            FixedListStore list = (FixedListStore)leaf;
            BinaryPrimitives.WriteUInt128BigEndian(((FixedStore)list.Elements).Reserve(16), Unsafe.As<TValue, UInt128>(ref value));
            list.Count++;
            return;
        }

        FixedStore fixedStore = (FixedStore)leaf;
        if (fixedStore.Width == Unsafe.SizeOf<TValue>())
        {
            fixedStore.Append(value);
            return;
        }

        // A decimal the index widened: its low bytes are the value at the column's width, which holds it.
        fixedStore.AppendBytes(MemoryMarshal.AsBytes(new ReadOnlySpan<TValue>(in value))[..fixedStore.Width]);
    }

    /// <summary>Appends one part of a composite key, its bytes as the key encodes them after the tag, to <paramref name="store"/>.</summary>
    internal static void AppendPart(ColumnStore store, ReadOnlySpan<byte> part, ColumnShape shape)
    {
        ColumnStore leaf = store.Leaf;
        switch (shape.Kind)
        {
            case StorageKind.Uuid:
            {
                FixedListStore list = (FixedListStore)leaf;
                ((FixedStore)list.Elements).AppendBytes(part[..16]);
                list.Count++;
                return;
            }

            case StorageKind.Bytes:
                ((VarBinStore)leaf).Append(part.Slice(4, BinaryPrimitives.ReadInt32LittleEndian(part)));
                return;
            default:
            {
                FixedStore fixedStore = (FixedStore)leaf;
                fixedStore.AppendBytes(part[..fixedStore.Width]);
                return;
            }
        }
    }

    /// <summary>Appends a null row, or the default of a column that holds none: a key's null group read into a column that is not nullable.</summary>
    internal static void AppendNull(ColumnStore store)
    {
        if (store.IsNullable)
        {
            store.AppendNull();
        }
        else
        {
            store.AppendDefault();
        }
    }
}
