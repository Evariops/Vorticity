// That the fused pass counts runs exactly as the scan it replaces did - docs/11-write-strategy.md
// §8 stage 2b.
//
// THE VERDICT TURNS ON ONE COMPARISON. `ColumnCompressor` keeps run-end only when a chunk has at
// most `rows / 4` runs, so a count that is off by one changes the plan and therefore the file's
// bytes. `WrittenSizeTests` would catch that only when the error happens to straddle the threshold;
// everywhere else a wrong count is silent. So the count is compared against a naive one, row by row,
// under the compressor's own equality rule: two nulls are equal, a null and a value are not, two
// values are equal byte for byte.
//
// AND UNDER EVERY BATCH CUT, because a run does not stop at a batch boundary. Row 8 192 starting a
// run is a question about row 8 191, which arrived on an arena the scan has since reset; `PreviousRow`
// is the answer and these cuts are what prove it.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class RunBoundaryTests
{
    private const int Rows = 1_997;

    /// <summary>Cuts that make the merge do work: whole, one row, uneven, and a block-sized one.</summary>
    private static readonly int[][] Cuts =
    [
        [Rows],
        [1, Rows - 1],
        [500, 500, 997],
        [1, 1, 1, 1, Rows - 4],
        [1024, 973],
    ];

    /// <summary>How the values repeat, which is what decides the run count.</summary>
    public enum Pattern
    {
        /// <summary>Every row different: one run per row, the case run-end must decline.</summary>
        Distinct,

        /// <summary>Long runs: the case run-end must take.</summary>
        Runs,

        /// <summary>One value for the whole column.</summary>
        Constant,

        /// <summary>Alternating, so every row is a boundary and no run is longer than one.</summary>
        Alternating,
    }

    [Theory]
    [InlineData(PType.I8)]
    [InlineData(PType.I32)]
    [InlineData(PType.I64)]
    [InlineData(PType.U16)]
    [InlineData(PType.F64)]
    public void FixedWidthRunsMatchANaiveCount(PType ptype)
    {
        foreach (Pattern pattern in Enum.GetValues<Pattern>())
        {
            foreach (bool nullable in new[] { false, true })
            {
                foreach (int[] cut in Cuts)
                {
                    CanonicalArena arena = new CanonicalArena();
                    DTypeArena types = new DTypeArena();
                    int node = Fixed(arena, types, ptype, pattern, nullable);
                    Check(arena, node, ptype, pattern, nullable, cut);
                }
            }
        }
    }

    [Theory]
    [InlineData(Pattern.Distinct)]
    [InlineData(Pattern.Runs)]
    [InlineData(Pattern.Constant)]
    [InlineData(Pattern.Alternating)]
    public void BooleanRunsMatchANaiveCount(Pattern pattern)
    {
        foreach (bool nullable in new[] { false, true })
        {
            foreach (int[] cut in Cuts)
            {
                CanonicalArena arena = new CanonicalArena();
                DTypeArena types = new DTypeArena();
                int node = Booleans(arena, types, pattern, nullable);
                Check(arena, node, PType.U8, pattern, nullable, cut);
            }
        }
    }

    [Theory]
    [InlineData(Pattern.Distinct)]
    [InlineData(Pattern.Runs)]
    [InlineData(Pattern.Constant)]
    [InlineData(Pattern.Alternating)]
    public void StringRunsMatchANaiveCount(Pattern pattern)
    {
        foreach (bool nullable in new[] { false, true })
        {
            foreach (int[] cut in Cuts)
            {
                CanonicalArena arena = new CanonicalArena();
                DTypeArena types = new DTypeArena();
                int node = Strings(arena, types, pattern, nullable);
                Check(arena, node, PType.U8, pattern, nullable, cut);
            }
        }
    }

    /// <summary>
    /// The constant canonical form: one element, many rows, and only validity to break the run.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConstantRunsMatchANaiveCount(bool nullable)
    {
        Span<byte> element = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(element, 9);
        foreach (int[] cut in Cuts)
        {
            CanonicalArena arena = new CanonicalArena();
            DTypeArena types = new DTypeArena();
            DType dtype = types.Primitive(
                PType.I32, nullable ? Nullability.Nullable : Nullability.NonNullable);
            Validity validity = nullable
                ? Validity.Bitmap(Bitmap(arena, types, Rows, i => (i / 23 % 2) == 0))
                : Validity.NonNullable;
            int node = arena.AddConstant(dtype, Rows, validity, element);
            Check(arena, node, PType.I32, Pattern.Constant, nullable, cut);
        }
    }

    /// <summary>
    /// A range that does not start the file counts its own leading boundary, and a merge does not.
    /// </summary>
    /// <remarks>
    /// This is the arithmetic <see cref="BlockStats.RunCount"/> exists for and the one place an
    /// off-by-one would hide: the boundary between two blocks belongs to the second, so summing the
    /// blocks of a chunk must not count it twice, and reading one block alone must not count it at
    /// all.
    /// </remarks>
    [Fact]
    public void ABlockBoundaryIsCountedOnceForTheChunkAndNeverForTheBlock()
    {
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();

        // Two blocks of 4, the second holding a different value: two runs over the whole, one run
        // inside each block.
        const int rows = 8;
        VortexBuffer values = arena.Allocate(rows * sizeof(int), sizeof(int), out Span<byte> bytes);
        Span<int> ints = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, int>(bytes);
        for (int i = 0; i < rows; i++)
        {
            ints[i] = i < 4 ? 1 : 2;
        }

        int node = arena.AddPrimitive(
            types.Primitive(PType.I32, Nullability.NonNullable), rows, Validity.NonNullable,
            PType.I32, values);

        ColumnWriter column = new ColumnWriter();
        column.Accumulate(arena, node, 0, 4);
        column.CloseBlock();
        column.Accumulate(arena, node, 4, 4);
        column.CloseBlock();

        Assert.Equal(2, column.Chunk(0, 2).RunCount);
        Assert.Equal(1, column.Chunk(0, 1).RunCount);
        Assert.Equal(1, column.Chunk(1, 1).RunCount);
    }

    // ------------------------------------------------------------------------ the property's oracle

    private static void Check(
        CanonicalArena arena, int node, PType ptype, Pattern pattern, bool nullable, int[] cut)
    {
        ColumnWriter column = new ColumnWriter();
        int offset = 0;
        foreach (int take in cut)
        {
            column.Accumulate(arena, node, offset, take);
            offset += take;
        }

        column.CloseBlock();
        BlockStats stats = column.Chunk(0, 1);

        long naive = NaiveRuns(arena, node);
        Assert.True(stats.HasRunBoundaries, $"{ptype}/{pattern}: the kind should carry boundaries");
        Assert.True(
            naive == stats.RunCount,
            $"{ptype} / {pattern} / nullable={nullable} / [{string.Join(',', cut)}]: " +
            $"the pass counted {stats.RunCount} runs, a naive walk counts {naive}");
    }

    /// <summary>
    /// One run, plus one for every row that differs from the row before it. `RowComparer.Equal`'s
    /// rule, written out.
    /// </summary>
    private static long NaiveRuns(CanonicalArena arena, int node)
    {
        CanonicalNode column = arena.GetNode(node);
        ValidityMask mask = ValidityMask.From(arena, column.Validity);
        int rows = column.Length;
        if (rows == 0)
        {
            return 0;
        }

        long runs = 1;
        for (int i = 1; i < rows; i++)
        {
            bool a = mask.IsValid(i - 1);
            bool b = mask.IsValid(i);
            if (a != b)
            {
                runs++;
                continue;
            }

            if (a && !RowBytes(column, i - 1).SequenceEqual(RowBytes(column, i)))
            {
                runs++;
            }
        }

        return runs;
    }

    private static ReadOnlySpan<byte> RowBytes(CanonicalNode node, int row)
    {
        switch (node.Kind)
        {
            case CanonicalKind.Bool:
                return CanonicalSupport.BitAt(node.Bits.Span, node.BitOffset + row)
                    ? OneByte : ZeroByte;

            case CanonicalKind.Constant:
                return node.ConstantElement;

            case CanonicalKind.VarBinView:
            {
                ReadOnlySpan<byte> view = node.Views.Span.Slice(row * 16, 16);
                int size = BinaryPrimitives.ReadInt32LittleEndian(view);
                if (size <= 12)
                {
                    return view.Slice(4, size);
                }

                int buffer = BinaryPrimitives.ReadInt32LittleEndian(view[8..12]);
                int at = BinaryPrimitives.ReadInt32LittleEndian(view[12..16]);
                return node.GetDataBuffer(buffer).Span.Slice(at, size);
            }

            default:
            {
                int width = node.PType.ByteWidth();
                return node.Values.Span.Slice(row * width, width);
            }
        }
    }

    private static ReadOnlySpan<byte> OneByte => [1];

    private static ReadOnlySpan<byte> ZeroByte => [0];

    // ---------------------------------------------------------------------------------- generators

    private static long Value(Pattern pattern, int row) => pattern switch
    {
        Pattern.Distinct => row,
        Pattern.Runs => row / 61,
        Pattern.Constant => 7,
        _ => row % 2,
    };

    private static int Fixed(
        CanonicalArena arena, DTypeArena types, PType ptype, Pattern pattern, bool nullable)
    {
        int width = ptype.ByteWidth();
        VortexBuffer buffer = arena.Allocate(Rows * width, width, out Span<byte> destination);
        for (int i = 0; i < Rows; i++)
        {
            Span<byte> slot = destination.Slice(i * width, width);
            long value = Value(pattern, i);
            switch (ptype)
            {
                case PType.I8: slot[0] = unchecked((byte)value); break;
                case PType.U16: BinaryPrimitives.WriteUInt16LittleEndian(slot, (ushort)value); break;
                case PType.I32: BinaryPrimitives.WriteInt32LittleEndian(slot, (int)value); break;
                case PType.I64: BinaryPrimitives.WriteInt64LittleEndian(slot, value); break;
                default: BinaryPrimitives.WriteDoubleLittleEndian(slot, value); break;
            }
        }

        return arena.AddPrimitive(
            types.Primitive(ptype, nullable ? Nullability.Nullable : Nullability.NonNullable),
            Rows, Mask(arena, types, nullable), ptype, buffer);
    }

    private static int Booleans(
        CanonicalArena arena, DTypeArena types, Pattern pattern, bool nullable)
    {
        int bytes = Math.Max(CanonicalSupport.BitmapByteCount(Rows), 1);
        VortexBuffer bits = arena.Allocate(bytes, 1, out Span<byte> destination);
        destination.Clear();
        for (int i = 0; i < Rows; i++)
        {
            if ((Value(pattern, i) & 1) != 0)
            {
                CanonicalSupport.SetBit(destination, i);
            }
        }

        return arena.AddBool(
            types.Bool(nullable ? Nullability.Nullable : Nullability.NonNullable),
            Rows, Mask(arena, types, nullable), bits, 0);
    }

    /// <summary>
    /// A varbinview with inline values AND out-of-line ones, plus the same long value written twice
    /// at two different offsets — the case docs/11 §3.2.4 says must not split a run.
    /// </summary>
    private static int Strings(
        CanonicalArena arena, DTypeArena types, Pattern pattern, bool nullable)
    {
        List<byte[]> payloads = [];
        for (int i = 0; i < Rows; i++)
        {
            long value = Value(pattern, i);

            // Values above 12 bytes go out of line, and each occurrence gets its OWN copy in the
            // heap, so two equal values have different views.
            string text = (value % 3) == 0
                ? $"value-{value}-padded-out-of-line"
                : $"v{value}";
            payloads.Add(System.Text.Encoding.UTF8.GetBytes(text));
        }

        int heap = 0;
        foreach (byte[] payload in payloads)
        {
            if (payload.Length > 12)
            {
                heap += payload.Length;
            }
        }

        VortexBuffer data = arena.Allocate(Math.Max(heap, 1), 1, out Span<byte> heapBytes);
        VortexBuffer views = arena.Allocate(Rows * 16, 16, out Span<byte> viewBytes);
        viewBytes.Clear();

        int at = 0;
        for (int i = 0; i < Rows; i++)
        {
            byte[] payload = payloads[i];
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(view, payload.Length);
            if (payload.Length <= 12)
            {
                payload.CopyTo(view[4..]);
                continue;
            }

            payload.CopyTo(heapBytes[at..]);
            BinaryPrimitives.WriteInt32LittleEndian(view[8..12], 0);
            BinaryPrimitives.WriteInt32LittleEndian(view[12..16], at);
            at += payload.Length;
        }

        return arena.AddVarBinView(
            types.Utf8(nullable ? Nullability.Nullable : Nullability.NonNullable),
            Rows, Mask(arena, types, nullable), views, [data]);
    }

    private static Validity Mask(CanonicalArena arena, DTypeArena types, bool nullable) =>
        nullable
            ? Validity.Bitmap(Bitmap(arena, types, Rows, i => (i / 17 % 3) != 0))
            : Validity.NonNullable;

    private static int Bitmap(CanonicalArena arena, DTypeArena types, int rows, Func<int, bool> set)
    {
        int bytes = Math.Max(CanonicalSupport.BitmapByteCount(rows), 1);
        VortexBuffer bits = arena.Allocate(bytes, 1, out Span<byte> destination);
        destination.Clear();
        for (int i = 0; i < rows; i++)
        {
            if (set(i))
            {
                CanonicalSupport.SetBit(destination, i);
            }
        }

        return arena.AddBool(
            types.Bool(Nullability.NonNullable), rows, Validity.NonNullable, bits, 0);
    }
}
