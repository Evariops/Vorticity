// Hand-crafted FlatBuffers byte vectors. Every buffer here is written out literally, byte by byte,
// with its offsets in the comments: the reader under test must be checked against the layout the
// FORMAT defines, not against whatever our own builder happens to emit.
//
// FlatBuffers layout reminders:
//   buffer   := [u32 root uoffset] ... objects ...
//   table    := [i32 soffset to vtable][inline field data]
//   vtable   := [u16 vtable_size][u16 table_size][u16 slot per field id]
//   vector   := [u32 count][elements]
//   string   := [u32 length][utf8 bytes][NUL]   (the NUL is not counted by length)
// uoffsets are unsigned and point forward; the vtable soffset is signed and may point either way.
using System;
using System.Runtime.InteropServices;

namespace Vorticity.Tests.Serialization.FlatBuffers;

internal static class TestFlatBuffers
{
    /// <summary>
    /// A table carrying every scalar type. Field 11 has a zero slot (present in the
    /// vtable, absent from the table) and the vtable stops there, so field 12 and
    /// beyond are absent by vtable length.
    /// </summary>
    internal static readonly byte[] Scalars =
    [
        0x20, 0x00, 0x00, 0x00,                             // [  0] root uoffset -> 32 (the table)
        0x1C, 0x00,                                         // [  4] vtable_size = 28 (4-byte header + 12 slots)
        0x30, 0x00,                                         // [  6] table_size = 48
        0x2C, 0x00,                                         // [  8] slot  0 (i8)   -> table+44
        0x2D, 0x00,                                         // [ 10] slot  1 (u8)   -> table+45
        0x28, 0x00,                                         // [ 12] slot  2 (i16)  -> table+40
        0x2A, 0x00,                                         // [ 14] slot  3 (u16)  -> table+42
        0x04, 0x00,                                         // [ 16] slot  4 (i32)  -> table+4
        0x08, 0x00,                                         // [ 18] slot  5 (u32)  -> table+8
        0x0C, 0x00,                                         // [ 20] slot  6 (i64)  -> table+12
        0x14, 0x00,                                         // [ 22] slot  7 (u64)  -> table+20
        0x1C, 0x00,                                         // [ 24] slot  8 (f32)  -> table+28
        0x20, 0x00,                                         // [ 26] slot  9 (f64)  -> table+32
        0x2E, 0x00,                                         // [ 28] slot 10 (bool) -> table+46
        0x00, 0x00,                                         // [ 30] slot 11        -> 0: in the vtable, but ABSENT
        0x1C, 0x00, 0x00, 0x00,                             // [ 32] table: soffset = 28 -> vtable at 32-28 = 4
        0xC0, 0x1D, 0xFE, 0xFF,                             // [ 36] field 4  i32 = -123456
        0x00, 0x28, 0x6B, 0xEE,                             // [ 40] field 5  u32 = 4000000000
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x80,     // [ 44] field 6  i64 = long.MinValue
        0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF,     // [ 52] field 7  u64 = ulong.MaxValue
        0x00, 0x00, 0xC0, 0x3F,                             // [ 60] field 8  f32 = 1.5
        0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x02, 0xC0,     // [ 64] field 9  f64 = -2.25
        0xD0, 0x8A,                                         // [ 72] field 2  i16 = -30000
        0xFF, 0xFF,                                         // [ 74] field 3  u16 = 65535
        0x80,                                               // [ 76] field 0  i8  = -128
        0xFF,                                               // [ 77] field 1  u8  = 255
        0x01,                                               // [ 78] field 10 bool = true
        0x00,                                               // [ 79] padding up to table_size
    ];   // 80 bytes

    /// <summary>
    /// A table whose vtable follows it, i.e. a negative soffset. Legal: soffsets are
    /// signed and must be bounded in BOTH directions.
    /// </summary>
    internal static readonly byte[] VtableAfterTable =
    [
        0x04, 0x00, 0x00, 0x00,                             // [  0] root uoffset -> 4 (the table)
        0xF8, 0xFF, 0xFF, 0xFF,                             // [  4] table: soffset = -8 -> vtable at 4-(-8) = 12: the vtable FOLLOWS the table
        0x2A, 0x00, 0x00, 0x00,                             // [  8] field 0 i32 = 42
        0x08, 0x00,                                         // [ 12] vtable_size = 8
        0x08, 0x00,                                         // [ 14] table_size = 8
        0x04, 0x00,                                         // [ 16] slot 0 -> table+4
        0x00, 0x00,                                         // [ 18] slot 1 -> 0: absent
    ];   // 20 bytes

    /// <summary>
    /// Two sub-tables sharing one vtable - the routine builder optimization a naive
    /// "never revisit a position" verifier would reject.
    /// </summary>
    internal static readonly byte[] SharedVtable =
    [
        0x0C, 0x00, 0x00, 0x00,                             // [  0] root uoffset -> 12
        0x08, 0x00,                                         // [  4] root vtable_size = 8
        0x0C, 0x00,                                         // [  6] root table_size = 12
        0x04, 0x00,                                         // [  8] slot 0 (child a) -> table+4
        0x08, 0x00,                                         // [ 10] slot 1 (child b) -> table+8
        0x08, 0x00, 0x00, 0x00,                             // [ 12] root table: soffset = 8 -> vtable at 4
        0x0E, 0x00, 0x00, 0x00,                             // [ 16] field 0 uoffset = 14 -> 16+14 = 30 (child a)
        0x12, 0x00, 0x00, 0x00,                             // [ 20] field 1 uoffset = 18 -> 20+18 = 38 (child b)
        0x06, 0x00,                                         // [ 24] the ONE shared child vtable: vtable_size = 6
        0x08, 0x00,                                         // [ 26] table_size = 8
        0x04, 0x00,                                         // [ 28] slot 0 -> table+4
        0x06, 0x00, 0x00, 0x00,                             // [ 30] child a: soffset = 6 -> shared vtable at 30-6 = 24
        0x6F, 0x00, 0x00, 0x00,                             // [ 34] child a field 0 = 111
        0x0E, 0x00, 0x00, 0x00,                             // [ 38] child b: soffset = 14 -> THE SAME vtable at 38-14 = 24
        0xDE, 0x00, 0x00, 0x00,                             // [ 42] child b field 0 = 222
    ];   // 46 bytes

    /// <summary>
    /// A string, a [ubyte], an empty vector, a vector of tables, a vector of strings,
    /// and one absent sub-table field.
    /// </summary>
    internal static readonly byte[] StringsAndVectors =
    [
        0x14, 0x00, 0x00, 0x00,                             // [  0] root uoffset -> 20
        0x10, 0x00,                                         // [  4] vtable_size = 16 (6 slots)
        0x18, 0x00,                                         // [  6] table_size = 24
        0x04, 0x00,                                         // [  8] slot 0 (string)         -> table+4
        0x08, 0x00,                                         // [ 10] slot 1 ([ubyte])        -> table+8
        0x0C, 0x00,                                         // [ 12] slot 2 (vector of tables)  -> table+12
        0x10, 0x00,                                         // [ 14] slot 3 (vector of strings) -> table+16
        0x14, 0x00,                                         // [ 16] slot 4 (empty vector)   -> table+20
        0x00, 0x00,                                         // [ 18] slot 5 (sub-table)      -> 0: ABSENT
        0x10, 0x00, 0x00, 0x00,                             // [ 20] root table: soffset = 16 -> vtable at 4
        0x14, 0x00, 0x00, 0x00,                             // [ 24] field 0 uoffset -> 24+20 = 44 (the string)
        0x1A, 0x00, 0x00, 0x00,                             // [ 28] field 1 uoffset -> 28+26 = 54 (the byte vector)
        0x21, 0x00, 0x00, 0x00,                             // [ 32] field 2 uoffset -> 32+33 = 65 (the vector of tables)
        0x3F, 0x00, 0x00, 0x00,                             // [ 36] field 3 uoffset -> 36+63 = 99 (the vector of strings)
        0x15, 0x00, 0x00, 0x00,                             // [ 40] field 4 uoffset -> 40+21 = 61 (the empty vector)
        0x05, 0x00, 0x00, 0x00,                             // [ 44] string length = 5; the trailing NUL is NOT counted
        0x68, 0x65, 0x6C, 0x6C, 0x6F,                       // [ 48] "hello"
        0x00,                                               // [ 53] string terminator
        0x03, 0x00, 0x00, 0x00,                             // [ 54] byte vector count = 3
        0x01, 0x02, 0x03,                                   // [ 58] bytes 1, 2, 3
        0x00, 0x00, 0x00, 0x00,                             // [ 61] the empty vector: count = 0, no elements
        0x02, 0x00, 0x00, 0x00,                             // [ 65] table vector count = 2
        0x0E, 0x00, 0x00, 0x00,                             // [ 69] element 0 uoffset -> 69+14 = 83
        0x12, 0x00, 0x00, 0x00,                             // [ 73] element 1 uoffset -> 73+18 = 91
        0x06, 0x00,                                         // [ 77] element vtable (shared by both elements): vtable_size = 6
        0x08, 0x00,                                         // [ 79] table_size = 8
        0x04, 0x00,                                         // [ 81] slot 0 -> table+4
        0x06, 0x00, 0x00, 0x00,                             // [ 83] element 0: soffset = 6 -> vtable at 77
        0x07, 0x00, 0x00, 0x00,                             // [ 87] element 0 field 0 = 7
        0x0E, 0x00, 0x00, 0x00,                             // [ 91] element 1: soffset = 14 -> the same vtable at 77
        0x09, 0x00, 0x00, 0x00,                             // [ 95] element 1 field 0 = 9
        0x02, 0x00, 0x00, 0x00,                             // [ 99] string vector count = 2
        0x08, 0x00, 0x00, 0x00,                             // [103] element 0 uoffset -> 103+8 = 111
        0x0B, 0x00, 0x00, 0x00,                             // [107] element 1 uoffset -> 107+11 = 118
        0x02, 0x00, 0x00, 0x00,                             // [111] string length = 2
        0x61, 0x62,                                         // [115] "ab"
        0x00,                                               // [117] terminator
        0x03, 0x00, 0x00, 0x00,                             // [118] string length = 3
        0x78, 0x79, 0x7A,                                   // [122] "xyz"
        0x00,                                               // [125] terminator
    ];   // 126 bytes

    /// <summary>
    /// Vectors reinterpreted in place with no traversal: a 16-byte SegmentSpec vector,
    /// a [uint16] and a [uint32].
    /// </summary>
    internal static readonly byte[] StructVectors =
    [
        0x10, 0x00, 0x00, 0x00,                             // [  0] root uoffset -> 16
        0x0A, 0x00,                                         // [  4] vtable_size = 10 (3 slots)
        0x10, 0x00,                                         // [  6] table_size = 16
        0x04, 0x00,                                         // [  8] slot 0 ([SegmentSpec], 16 B each) -> table+4
        0x08, 0x00,                                         // [ 10] slot 1 ([uint16])                 -> table+8
        0x0C, 0x00,                                         // [ 12] slot 2 ([uint32])                 -> table+12
        0x00, 0x00,                                         // [ 14] padding
        0x0C, 0x00, 0x00, 0x00,                             // [ 16] root table: soffset = 12 -> vtable at 4
        0x10, 0x00, 0x00, 0x00,                             // [ 20] field 0 uoffset -> 20+16 = 36
        0x30, 0x00, 0x00, 0x00,                             // [ 24] field 1 uoffset -> 24+48 = 72
        0x38, 0x00, 0x00, 0x00,                             // [ 28] field 2 uoffset -> 28+56 = 84
        0x00, 0x00, 0x00, 0x00,                             // [ 32] padding so the 16-byte structs start 8-byte aligned
        0x02, 0x00, 0x00, 0x00,                             // [ 36] SegmentSpec vector count = 2; elements start at 40 (8-byte aligned)
        0x00, 0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,     // [ 40] segment 0: offset = 4096
        0x00, 0x01, 0x00, 0x00,                             // [ 48] segment 0: length = 256
        0x06,                                               // [ 52] segment 0: alignment_exponent = 6
        0x00,                                               // [ 53] segment 0: _compression
        0x00, 0x00,                                         // [ 54] segment 0: _encryption
        0x00, 0x20, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,     // [ 56] segment 1: offset = 8192
        0x00, 0x02, 0x00, 0x00,                             // [ 64] segment 1: length = 512
        0x03,                                               // [ 68] segment 1: alignment_exponent = 3
        0x01,                                               // [ 69] segment 1: _compression = 1
        0x02, 0x00,                                         // [ 70] segment 1: _encryption = 2
        0x03, 0x00, 0x00, 0x00,                             // [ 72] uint16 vector count = 3; elements at 76 (2-byte aligned)
        0x0A, 0x00,                                         // [ 76] [0] = 10
        0x14, 0x00,                                         // [ 78] [1] = 20
        0x1E, 0x00,                                         // [ 80] [2] = 30
        0x00, 0x00,                                         // [ 82] padding so the uint32 elements start 4-byte aligned
        0x02, 0x00, 0x00, 0x00,                             // [ 84] uint32 vector count = 2; elements at 88 (4-byte aligned)
        0x70, 0x11, 0x01, 0x00,                             // [ 88] [0] = 70000
        0x80, 0x38, 0x01, 0x00,                             // [ 92] [1] = 80000
    ];   // 96 bytes

    /// <summary>
    /// A 16-byte struct vector whose elements start 4 mod 8. Reinterpreting it in
    /// place would be an unaligned read, so it must be refused, not copied.
    /// </summary>
    internal static readonly byte[] MisalignedStructVector =
    [
        0x0C, 0x00, 0x00, 0x00,                             // [  0] root uoffset -> 12
        0x06, 0x00,                                         // [  4] vtable_size = 6 (1 slot)
        0x08, 0x00,                                         // [  6] table_size = 8
        0x04, 0x00,                                         // [  8] slot 0 ([SegmentSpec]) -> table+4
        0x00, 0x00,                                         // [ 10] padding
        0x08, 0x00, 0x00, 0x00,                             // [ 12] root table: soffset = 8 -> vtable at 4
        0x08, 0x00, 0x00, 0x00,                             // [ 16] field 0 uoffset -> 16+8 = 24
        0x00, 0x00, 0x00, 0x00,                             // [ 20] padding chosen so the elements land at 4 mod 8
        0x01, 0x00, 0x00, 0x00,                             // [ 24] count = 1; elements start at 28, which is 4 mod 8: MISALIGNED
        0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,     // [ 28] segment 0: offset = 1
        0x02, 0x00, 0x00, 0x00,                             // [ 36] segment 0: length = 2
        0x00,                                               // [ 40] segment 0: alignment_exponent
        0x00,                                               // [ 41] segment 0: _compression
        0x00, 0x00,                                         // [ 42] segment 0: _encryption
    ];   // 44 bytes

    /// <summary>
    /// A FlatBuffers struct stored inline in the table body, read by value with an
    /// unaligned load (so no alignment rule applies to it).
    /// </summary>
    internal static readonly byte[] InlineStruct =
    [
        0x0C, 0x00, 0x00, 0x00,                             // [  0] root uoffset -> 12
        0x06, 0x00,                                         // [  4] vtable_size = 6 (1 slot)
        0x14, 0x00,                                         // [  6] table_size = 20 (4-byte soffset + a 16-byte inline struct)
        0x04, 0x00,                                         // [  8] slot 0 (inline SegmentSpec) -> table+4
        0x00, 0x00,                                         // [ 10] padding
        0x08, 0x00, 0x00, 0x00,                             // [ 12] root table: soffset = 8 -> vtable at 4
        0xAD, 0xDE, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,     // [ 16] inline struct: offset = 57005
        0x4D, 0x00, 0x00, 0x00,                             // [ 24] inline struct: length = 77
        0x04,                                               // [ 28] inline struct: alignment_exponent = 4
        0x02,                                               // [ 29] inline struct: _compression = 2
        0x09, 0x00,                                         // [ 30] inline struct: _encryption = 9
    ];   // 32 bytes

    /// <summary>
    /// A table with no fields at all: vtable_size == 4 (header only), table_size == 4 (the soffset
    /// only). This is exactly what the dtype schema's `table Null {}` and the footer schema's
    /// `table EncryptionSpec {}` serialize to, so it must be accepted.
    /// </summary>
    internal static readonly byte[] EmptyTable =
    [
        0x08, 0x00, 0x00, 0x00,                             // [  0] root uoffset -> 8
        0x04, 0x00,                                         // [  4] vtable_size = 4: header only
        0x04, 0x00,                                         // [  6] table_size = 4: the soffset only
        0x04, 0x00, 0x00, 0x00,                             // [  8] table: soffset = 4 -> vtable at 4
    ];   // 12 bytes

    /// <summary>
    /// Mirrors <c>struct SegmentSpec</c> from the footer schema: 16 bytes, 8-byte
    /// alignment, read as a reinterpreted span.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct SegmentSpecLike
    {
        internal ulong Offset;
        internal uint Length;
        internal byte AlignmentExponent;
        internal byte Compression;
        internal ushort Encryption;
    }

    /// <summary>
    /// Mirrors <c>struct Buffer</c> from the array schema: 8 bytes, but only 4-byte
    /// alignment. Requiring <c>sizeof(T)</c> instead of <c>alignof(T)</c> would reject a perfectly
    /// legal file whose Buffer vector sits at a 4 mod 8 address.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct BufferLike
    {
        internal ushort Padding;
        internal byte AlignmentExponent;
        internal byte Compression;
        internal uint Length;
    }

    /// <summary>
    /// A chain of <paramref name="count"/> nested tables, each one's field 0 pointing forward to
    /// the next, to exercise the <see cref="Vorticity.VortexLimits.MaxFlatBufferDepth"/> cap.
    /// Too long to write out by hand, so it is assembled here with the same explicit offsets.
    /// </summary>
    internal static byte[] TableChain(int count)
    {
        // [0]  u32 root uoffset -> 14
        // [4]  chain vtable:      vtable_size = 6, table_size = 8, slot 0 -> table+4
        // [10] terminator vtable: vtable_size = 4, table_size = 4, no slots at all
        // [14] table i at 14 + 8*i, for i in [0, count-1); the last table is 4 bytes.
        int last = 14 + (8 * (count - 1));
        byte[] bytes = new byte[last + 4];
        WriteUInt32(bytes, 0, 14);
        WriteUInt16(bytes, 4, 6);
        WriteUInt16(bytes, 6, 8);
        WriteUInt16(bytes, 8, 4);
        WriteUInt16(bytes, 10, 4);
        WriteUInt16(bytes, 12, 4);

        for (int i = 0; i < count - 1; i++)
        {
            int table = 14 + (8 * i);
            WriteInt32(bytes, table, table - 4);          // soffset -> the chain vtable at 4
            WriteUInt32(bytes, table + 4, 4);             // field 0 uoffset -> the next table
        }

        WriteInt32(bytes, last, last - 10);               // soffset -> the terminator vtable at 10
        return bytes;
    }

    /// <summary>Returns a copy of <paramref name="source"/> with <paramref name="replacement"/> spliced in at <paramref name="offset"/>.</summary>
    internal static byte[] With(byte[] source, int offset, params byte[] replacement)
    {
        byte[] copy = (byte[])source.Clone();
        replacement.CopyTo(copy.AsSpan(offset));
        return copy;
    }

    /// <summary>Returns the first <paramref name="length"/> bytes of <paramref name="source"/>.</summary>
    internal static byte[] Truncate(byte[] source, int length) => source.AsSpan(0, length).ToArray();

    internal static void WriteUInt16(byte[] target, int offset, ushort value)
    {
        target[offset] = (byte)value;
        target[offset + 1] = (byte)(value >> 8);
    }

    internal static void WriteUInt32(byte[] target, int offset, uint value)
    {
        target[offset] = (byte)value;
        target[offset + 1] = (byte)(value >> 8);
        target[offset + 2] = (byte)(value >> 16);
        target[offset + 3] = (byte)(value >> 24);
    }

    internal static void WriteInt32(byte[] target, int offset, int value) =>
        WriteUInt32(target, offset, (uint)value);
}
