using System;
using System.Runtime.InteropServices;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class OverlappingListTests
{
    private static readonly DTypeArena Types = new DTypeArena();

    // Rows sliding over one window name it many times over: each is answered from a count of the
    // window's matches, and must say what a search of its own elements would.
    [Fact]
    public void RowsThatOverlapAnswerAsASearchOfTheirElements()
    {
        const int Rows = 300;
        const int Width = 200;
        long[] elements = new long[Rows + Width];
        for (int i = 0; i < elements.Length; i++)
        {
            elements[i] = i % 97 == 50 ? 7 : i;
        }

        long[] offsets = new long[Rows];
        long[] sizes = new long[Rows];
        for (int row = 0; row < Rows; row++)
        {
            offsets[row] = row;
            sizes[row] = row % 11 == 0 ? 0 : Width - (row % 150);
        }

        byte[] answered = Contains(elements, offsets, sizes, validRows: null, sought: 7);

        for (int row = 0; row < Rows; row++)
        {
            Assert.Equal(Expected(elements, offsets[row], sizes[row], 7), answered[row]);
        }
    }

    // A match at the first or the last element a row names counts; one just past either does not.
    [Fact]
    public void AMatchAtAnEdgeOfARowCountsAndOneJustPastItDoesNot()
    {
        long[] elements = new long[64];
        elements[10] = 7;
        elements[40] = 7;
        long[] offsets = new long[64];
        long[] sizes = new long[64];
        for (int row = 0; row < 64; row++)
        {
            offsets[row] = 0;
            sizes[row] = 64;
        }

        offsets[0] = 10;
        sizes[0] = 1;
        offsets[1] = 11;
        sizes[1] = 29;
        offsets[2] = 11;
        sizes[2] = 30;
        offsets[3] = 0;
        sizes[3] = 10;

        byte[] answered = Contains(elements, offsets, sizes, validRows: null, sought: 7);

        Assert.Equal(Trilean.True, answered[0]);
        Assert.Equal(Trilean.False, answered[1]);
        Assert.Equal(Trilean.True, answered[2]);
        Assert.Equal(Trilean.False, answered[3]);
    }

    [Fact]
    public void ANullRowThatOverlapsIsUnknown()
    {
        long[] elements = new long[40];
        elements[5] = 7;
        long[] offsets = new long[64];
        long[] sizes = new long[64];
        sizes.AsSpan().Fill(40);
        bool[] valid = new bool[64];
        valid.AsSpan().Fill(true);
        valid[3] = false;

        byte[] answered = Contains(elements, offsets, sizes, valid, sought: 7);

        Assert.Equal(Trilean.Unknown, answered[3]);
        Assert.Equal(Trilean.True, answered[4]);
    }

    private static byte Expected(long[] elements, long offset, long size, long sought)
    {
        for (long i = offset; i < offset + size; i++)
        {
            if (elements[i] == sought)
            {
                return Trilean.True;
            }
        }

        return Trilean.False;
    }

    private static byte[] Contains(long[] elements, long[] offsets, long[] sizes, bool[]? validRows, long sought)
    {
        DType i64 = Types.Primitive(PType.I64, Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        VortexBuffer elementBuffer = arena.Allocate(elements.Length * sizeof(long), sizeof(long), out Span<byte> elementBytes);
        elements.AsSpan().CopyTo(MemoryMarshal.Cast<byte, long>(elementBytes));
        int child = arena.AddPrimitive(i64, elements.Length, Validity.NonNullable, PType.I64, elementBuffer);
        VortexBuffer offsetBuffer = arena.Allocate(offsets.Length * sizeof(long), sizeof(long), out Span<byte> offsetBytes);
        offsets.AsSpan().CopyTo(MemoryMarshal.Cast<byte, long>(offsetBytes));
        VortexBuffer sizeBuffer = arena.Allocate(sizes.Length * sizeof(long), sizeof(long), out Span<byte> sizeBytes);
        sizes.AsSpan().CopyTo(MemoryMarshal.Cast<byte, long>(sizeBytes));

        Validity validity = Validity.NonNullable;
        DType list = Types.List(i64, Nullability.NonNullable);
        if (validRows is not null)
        {
            DType flags = Types.Bool(Nullability.NonNullable);
            VortexBuffer bits = arena.Allocate((validRows.Length + 7) / 8, 1, out Span<byte> bitBytes);
            bitBytes.Clear();
            for (int row = 0; row < validRows.Length; row++)
            {
                if (validRows[row])
                {
                    bitBytes[row >> 3] |= (byte)(1 << (row & 7));
                }
            }

            validity = Validity.Bitmap(arena.AddBool(flags, validRows.Length, Validity.NonNullable, bits, 0));
            list = Types.List(i64, Nullability.Nullable);
        }

        int node = arena.AddListView(list, offsets.Length, validity, child, offsetBuffer, PType.I64, sizeBuffer, PType.I64);
        byte[] answered = new byte[offsets.Length];
        ListKernels.Contains(arena, node, FilterLiteral.From(sought), answered);
        return answered;
    }
}
