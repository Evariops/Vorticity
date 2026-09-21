// Structural inspection of a buffer the builder produced, done with raw little-endian reads rather
// than through FlatBufferTable. The vtable-dedup requirement is a statement about the BYTES -
// "both tables soffset to the same vtable" - and asserting it through the reader would only prove
// that both tables read the same values, which is exactly what a non-deduplicating builder also
// achieves.
//
// FlatBuffers layout reminders:
//   buffer := [u32 root uoffset] ... objects ...
//   table  := [i32 soffset to vtable][inline field data]
//   vtable := [u16 vtable_size][u16 table_size][u16 slot per field id]
//   vector := [u32 count][elements]
using System;
using System.Buffers.Binary;

namespace Vorticity.Tests.Serialization.FlatBuffers;

internal static class BuiltFlatBuffer
{
    /// <summary>Position of the root table, from the uoffset at position 0.</summary>
    internal static int RootTable(ReadOnlySpan<byte> buffer) => (int)ReadUInt32(buffer, 0);

    /// <summary>
    /// Position of a table's vtable. The soffset is SIGNED, so the vtable may sit either side of
    /// the table; the builder produces both directions, a fresh vtable before its table and a
    /// deduplicated one after it.
    /// </summary>
    internal static int VTableOf(ReadOnlySpan<byte> buffer, int tablePos) =>
        tablePos - ReadInt32(buffer, tablePos);

    internal static int VTableSize(ReadOnlySpan<byte> buffer, int vtablePos) =>
        ReadUInt16(buffer, vtablePos);

    internal static int TableSize(ReadOnlySpan<byte> buffer, int vtablePos) =>
        ReadUInt16(buffer, vtablePos + 2);

    internal static int SlotCount(ReadOnlySpan<byte> buffer, int vtablePos) =>
        (VTableSize(buffer, vtablePos) - 4) / 2;

    /// <summary>The raw vtable slot of a field: its byte offset inside the table, or 0 when absent.</summary>
    internal static int Slot(ReadOnlySpan<byte> buffer, int tablePos, int fieldId)
    {
        int vtablePos = VTableOf(buffer, tablePos);
        if (fieldId >= SlotCount(buffer, vtablePos))
        {
            return 0;
        }

        return ReadUInt16(buffer, vtablePos + 4 + (fieldId * 2));
    }

    /// <summary>Absolute position of a field's inline value.</summary>
    internal static int FieldPos(ReadOnlySpan<byte> buffer, int tablePos, int fieldId) =>
        tablePos + Slot(buffer, tablePos, fieldId);

    /// <summary>Follows a forward uoffset stored at <paramref name="pos"/>.</summary>
    internal static int Follow(ReadOnlySpan<byte> buffer, int pos) => pos + (int)ReadUInt32(buffer, pos);

    /// <summary>Position of the object (table, vector or string) a reference field points at.</summary>
    internal static int Referenced(ReadOnlySpan<byte> buffer, int tablePos, int fieldId) =>
        Follow(buffer, FieldPos(buffer, tablePos, fieldId));

    /// <summary>Element count of the vector or string a reference field points at.</summary>
    internal static int CountOf(ReadOnlySpan<byte> buffer, int tablePos, int fieldId) =>
        (int)ReadUInt32(buffer, Referenced(buffer, tablePos, fieldId));

    /// <summary>Position of the first element of the vector a reference field points at.</summary>
    internal static int ElementsOf(ReadOnlySpan<byte> buffer, int tablePos, int fieldId) =>
        Referenced(buffer, tablePos, fieldId) + 4;

    /// <summary>The distinct vtable positions used by the given tables, in first-seen order.</summary>
    internal static int[] DistinctVTables(byte[] buffer, params int[] tablePositions)
    {
        int[] found = new int[tablePositions.Length];
        int count = 0;
        for (int i = 0; i < tablePositions.Length; i++)
        {
            int vtable = VTableOf(buffer, tablePositions[i]);
            bool seen = false;
            for (int j = 0; j < count; j++)
            {
                if (found[j] == vtable)
                {
                    seen = true;
                    break;
                }
            }

            if (!seen)
            {
                found[count++] = vtable;
            }
        }

        return found.AsSpan(0, count).ToArray();
    }

    internal static ushort ReadUInt16(ReadOnlySpan<byte> buffer, int pos) =>
        BinaryPrimitives.ReadUInt16LittleEndian(buffer.Slice(pos, 2));

    internal static uint ReadUInt32(ReadOnlySpan<byte> buffer, int pos) =>
        BinaryPrimitives.ReadUInt32LittleEndian(buffer.Slice(pos, 4));

    internal static int ReadInt32(ReadOnlySpan<byte> buffer, int pos) =>
        BinaryPrimitives.ReadInt32LittleEndian(buffer.Slice(pos, 4));
}
