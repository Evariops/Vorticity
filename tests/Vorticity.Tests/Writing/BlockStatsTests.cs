// The oracle for the fused pass - docs/11-write-strategy.md §5.2, first row of its table.
//
// `BlockStatsPass` is the one place in the writer where a value is READ rather than moved, and every
// stage after this one hangs its verdicts off what it produces: the zone map today, the chooser's
// exact formulas next, the Bloom filters after that. A wrong min is not a wrong file until something
// prunes with it, at which point it is a row that a scan did not return -- the one failure
// docs/08-semantics.md §1 forbids. So it is tested against a NAIVE per-row implementation, on
// generated data, over every physical type, every validity shape, and -- the part that matters most
// -- every way of cutting the rows into batches.
//
// THE BATCH CUT IS THE POINT. A block is 8 192 rows counted from row 0 of the file and a batch is
// whatever the caller handed over, so the pass sees a block as a sequence of arbitrary ranges that
// it has to fold together exactly. A merge that were merely approximate would pass a test that
// summarized each block in one call, which is why every case here is run under several cuts,
// including cuts of one row.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class BlockStatsTests
{
    /// <summary>Rows per generated column; prime-ish so no cut divides it.</summary>
    private const int Rows = 2_053;

    private static readonly PType[] Integers =
    [
        PType.I8, PType.I16, PType.I32, PType.I64,
        PType.U8, PType.U16, PType.U32, PType.U64,
    ];

    private static readonly PType[] Floats = [PType.F16, PType.F32, PType.F64];

    /// <summary>
    /// The cuts a block is fed in: whole, halves, uneven thirds, and one row at a time.
    /// </summary>
    private static readonly int[][] Cuts =
    [
        [Rows],
        [1, Rows - 1],
        [Rows - 1, 1],
        [1024, 1029],
        [700, 700, 653],
        [1, 1, 1, 1, Rows - 4],
    ];

    /// <summary>The four validity shapes, named by what they make the pass do.</summary>
    private enum Shape
    {
        NonNullable,
        AllValid,
        AllInvalid,
        /// <summary>A bitmap with runs of both, so neither hoisted branch is the only one taken.</summary>
        Mixed,
        /// <summary>A bitmap whose every bit is set: the kind says "maybe", the bits say no.</summary>
        BitmapAllSet,
        /// <summary>A bitmap whose every bit is clear.</summary>
        BitmapNoneSet,
    }

    [Fact]
    public void IntegerBlocksMatchANaivePass()
    {
        foreach (PType ptype in Integers)
        {
            foreach (Shape shape in Enum.GetValues<Shape>())
            {
                foreach (int[] cut in Cuts)
                {
                    Check(ptype, shape, cut, seed: 17);
                }
            }
        }
    }

    [Fact]
    public void FloatBlocksMatchANaivePassWithNaNsAndSignedZeros()
    {
        foreach (PType ptype in Floats)
        {
            foreach (Shape shape in Enum.GetValues<Shape>())
            {
                foreach (int[] cut in Cuts)
                {
                    Check(ptype, shape, cut, seed: 23);
                }
            }
        }
    }

    /// <summary>
    /// A block of only NaNs has no bound, and says so rather than inventing one.
    /// </summary>
    /// <remarks>
    /// The row is not null -- the null count is zero -- and yet there is nothing to bound it with.
    /// A writer that let a NaN reach `max` would poison every comparison against the zone, because
    /// NaN compares false with everything.
    /// </remarks>
    [Fact]
    public void ABlockOfNaNsHasNoBoundAndNoNulls()
    {
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        double[] values = new double[64];
        Array.Fill(values, double.NaN);
        int node = Doubles(arena, types, values, Shape.NonNullable);

        BlockStats stats = default;
        BlockStatsPass.Accumulate(arena, node, 0, values.Length, ref stats);

        Assert.True(stats.IsSummarizable);
        Assert.False(stats.HasBounds);
        Assert.Equal(0, stats.NullCount);
        Assert.Equal(values.Length, stats.Rows);
    }

    /// <summary>
    /// <c>-0.0</c> and <c>+0.0</c> are two values, and the bounds keep them apart.
    /// </summary>
    /// <remarks>
    /// `-0.0 &lt; +0.0` is false, so a raw compare would keep whichever row arrived first and make
    /// the bound depend on the batching. docs/07-dotnet-mapping.md counts them as distinct; Math.Min
    /// and Math.Max are the functions that order them, and the pass uses their rule.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SignedZerosAreOrderedWhicheverWayTheyArrive(bool negativeFirst)
    {
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        double[] values = negativeFirst ? [-0.0, 0.0] : [0.0, -0.0];
        int node = Doubles(arena, types, values, Shape.NonNullable);

        // One row at a time, so the merge across batches is what is being asked.
        BlockStats stats = default;
        BlockStatsPass.Accumulate(arena, node, 0, 1, ref stats);
        BlockStatsPass.Accumulate(arena, node, 1, 1, ref stats);

        Assert.True(stats.HasBounds);
        Assert.True(double.IsNegative(stats.Min.FloatValue));
        Assert.False(double.IsNegative(stats.Max.FloatValue));
    }

    /// <summary>
    /// The constant form is summarized without materializing the column it exists to not build.
    /// </summary>
    [Fact]
    public void AConstantBlockIsBoundedByItsElement()
    {
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        DType dtype = types.Primitive(PType.I32, Nullability.NonNullable);
        Span<byte> element = stackalloc byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(element, -7);
        int node = arena.AddConstant(dtype, 10_000, Validity.NonNullable, element);

        BlockStats stats = default;
        BlockStatsPass.Accumulate(arena, node, 0, 4_000, ref stats);
        BlockStatsPass.Accumulate(arena, node, 4_000, 6_000, ref stats);

        Assert.True(stats.IsSummarizable);
        Assert.True(stats.HasBounds);
        Assert.Equal(-7L, stats.Min.SignedValue);
        Assert.Equal(-7L, stats.Max.SignedValue);
        Assert.Equal(0, stats.NullCount);
        Assert.Equal(10_000, stats.Rows);
    }

    /// <summary>
    /// A kind with no scalar bound contributes its null count and nothing else.
    /// </summary>
    /// <remarks>
    /// Utf8 and binary have a perfectly good lexicographic min/max and no place to put it without a
    /// second varbinview per zone; a struct has no scalar bound at all. Both still get a zone map,
    /// with the null count alone, which is what IS NULL pruning runs on.
    /// </remarks>
    [Fact]
    public void AnUnsummarizableColumnStillCountsItsNulls()
    {
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();

        const int rows = 100;
        int bitmap = Bitmap(arena, types, rows, i => i % 3 != 0);
        VortexBuffer bits = arena.Allocate(CanonicalSupport.BitmapByteCount(rows), 1, out Span<byte> _);
        int node = arena.AddBool(
            types.Bool(Nullability.Nullable), rows, Validity.Bitmap(bitmap), bits, 0);

        BlockStats stats = default;
        BlockStatsPass.Accumulate(arena, node, 0, rows, ref stats);

        Assert.False(stats.IsSummarizable);
        Assert.False(stats.HasBounds);
        Assert.Equal(34, stats.NullCount); // 0, 3, 6 ... 99
        Assert.Equal(rows, stats.Rows);
    }

    // ------------------------------------------------------------------- the property, and its oracle

    private static void Check(PType ptype, Shape shape, int[] cut, int seed)
    {
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();

        int width = ptype.ByteWidth();
        byte[] raw = Generate(ptype, seed);
        VortexBuffer buffer = arena.Allocate(Rows * width, width, out Span<byte> destination);
        raw.AsSpan().CopyTo(destination);

        Validity validity = Build(arena, types, shape);
        DType dtype = types.Primitive(
            ptype, shape == Shape.NonNullable ? Nullability.NonNullable : Nullability.Nullable);
        int node = arena.AddPrimitive(dtype, Rows, validity, ptype, buffer);

        BlockStats stats = default;
        int offset = 0;
        foreach (int take in cut)
        {
            BlockStatsPass.Accumulate(arena, node, offset, take, ref stats);
            offset += take;
        }

        Assert.Equal(Rows, offset);

        string where = $"{ptype} / {shape} / [{string.Join(',', cut)}]";
        BlockStats naive = Naive(arena, node, ptype, shape);

        Assert.Equal(naive.Rows, stats.Rows);
        Assert.True(naive.NullCount == stats.NullCount, $"{where}: nulls");
        Assert.True(naive.IsSummarizable == stats.IsSummarizable, $"{where}: summarizable");
        Assert.True(naive.HasBounds == stats.HasBounds, $"{where}: has bounds");
        if (!naive.HasBounds)
        {
            return;
        }

        Assert.True(Same(naive.Min, stats.Min, ptype), $"{where}: min");
        Assert.True(Same(naive.Max, stats.Max, ptype), $"{where}: max");
    }

    /// <summary>
    /// The oracle: one pass, one row at a time, <c>IsValid</c> per row, no cleverness at all.
    /// </summary>
    private static BlockStats Naive(CanonicalArena arena, int node, PType ptype, Shape shape)
    {
        CanonicalNode column = arena.GetNode(node);
        ValidityMask mask = ValidityMask.From(arena, column.Validity);
        ReadOnlySpan<byte> values = column.Values.Span;

        BlockStats stats = default;
        stats.Rows = Rows;
        stats.IsSummarizable = true;

        bool signed = ptype.IsSignedInteger();
        bool floating = ptype.IsFloat();
        long minSigned = long.MaxValue;
        long maxSigned = long.MinValue;
        ulong minUnsigned = ulong.MaxValue;
        ulong maxUnsigned = ulong.MinValue;
        double minFloat = double.PositiveInfinity;
        double maxFloat = double.NegativeInfinity;
        bool any = false;

        for (int i = 0; i < Rows; i++)
        {
            if (!mask.IsValid(i))
            {
                stats.NullCount++;
                continue;
            }

            if (floating)
            {
                double value = ReadFloat(values, ptype, i);
                if (double.IsNaN(value))
                {
                    continue;
                }

                any = true;
                minFloat = Math.Min(minFloat, value);
                maxFloat = Math.Max(maxFloat, value);
            }
            else if (signed)
            {
                any = true;
                long value = ReadSigned(values, ptype, i);
                minSigned = Math.Min(minSigned, value);
                maxSigned = Math.Max(maxSigned, value);
            }
            else
            {
                any = true;
                ulong value = ReadUnsignedValue(values, ptype, i);
                minUnsigned = Math.Min(minUnsigned, value);
                maxUnsigned = Math.Max(maxUnsigned, value);
            }
        }

        _ = shape;
        if (!any)
        {
            return stats;
        }

        if (floating)
        {
            stats.MergeFloat(minFloat, maxFloat);
        }
        else if (signed)
        {
            stats.MergeSigned(minSigned, maxSigned);
        }
        else
        {
            stats.MergeUnsigned(minUnsigned, maxUnsigned);
        }

        return stats;
    }

    /// <summary>Compares two bounds by their raw bits, so a signed zero is not equal to a zero.</summary>
    private static bool Same(FilterLiteral a, FilterLiteral b, PType ptype)
    {
        if (a.Kind != b.Kind)
        {
            return false;
        }

        return ptype.IsFloat()
            ? BitConverter.DoubleToUInt64Bits(a.FloatValue) == BitConverter.DoubleToUInt64Bits(b.FloatValue)
            : a.UnsignedValue == b.UnsignedValue;
    }

    // ---------------------------------------------------------------------------------- generators

    /// <summary>
    /// Deterministic values with the shapes that break a careless bound.
    /// </summary>
    /// <remarks>
    /// Every integer width gets its own extremes, so a pass that widened through the wrong sign or
    /// truncated to the wrong size is caught rather than merely unlikely; the floats get NaN, both
    /// infinities and both zeros.
    /// </remarks>
    private static byte[] Generate(PType ptype, int seed)
    {
        int width = ptype.ByteWidth();
        byte[] raw = new byte[Rows * width];
        Span<byte> span = raw;
        uint state = (uint)seed;

        for (int i = 0; i < Rows; i++)
        {
            state = unchecked((state * 1_664_525u) + 1_013_904_223u);
            Span<byte> slot = span.Slice(i * width, width);

            switch (ptype)
            {
                case PType.I8: slot[0] = Special8(i, state); break;
                case PType.U8: slot[0] = Special8(i, state); break;
                case PType.I16:
                case PType.U16:
                    BinaryPrimitives.WriteUInt16LittleEndian(slot, Special16(i, state));
                    break;
                case PType.I32:
                case PType.U32:
                    BinaryPrimitives.WriteUInt32LittleEndian(slot, Special32(i, state));
                    break;
                case PType.I64:
                case PType.U64:
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        slot, Special32(i, state) | ((ulong)Special32(i + 1, state * 7u) << 32));
                    break;
                case PType.F16:
                    BinaryPrimitives.WriteHalfLittleEndian(slot, (Half)SpecialFloat(i, state));
                    break;
                case PType.F32:
                    BinaryPrimitives.WriteSingleLittleEndian(slot, (float)SpecialFloat(i, state));
                    break;
                default:
                    BinaryPrimitives.WriteDoubleLittleEndian(slot, SpecialFloat(i, state));
                    break;
            }
        }

        return raw;
    }

    private static byte Special8(int i, uint state) => (i % 257) switch
    {
        0 => 0x80,          // the signed minimum, the unsigned middle
        1 => 0x7F,          // the signed maximum
        2 => 0xFF,          // the unsigned maximum, minus one signed
        3 => 0x00,
        _ => (byte)state,
    };

    private static ushort Special16(int i, uint state) => (i % 131) switch
    {
        0 => 0x8000,
        1 => 0x7FFF,
        2 => 0xFFFF,
        3 => 0x0000,
        _ => (ushort)state,
    };

    private static uint Special32(int i, uint state) => (i % 97) switch
    {
        0 => 0x8000_0000u,
        1 => 0x7FFF_FFFFu,
        2 => 0xFFFF_FFFFu,
        3 => 0u,
        _ => state,
    };

    private static double SpecialFloat(int i, uint state) => (i % 61) switch
    {
        0 => double.NaN,
        1 => -0.0,
        2 => 0.0,
        3 => double.PositiveInfinity,
        4 => double.NegativeInfinity,
        5 => -1.5,
        _ => ((int)(state % 20_001) - 10_000) / 8.0,
    };

    private static Validity Build(CanonicalArena arena, DTypeArena types, Shape shape) => shape switch
    {
        Shape.NonNullable => Validity.NonNullable,
        Shape.AllValid => Validity.AllValid,
        Shape.AllInvalid => Validity.AllInvalid,
        Shape.BitmapAllSet => Validity.Bitmap(Bitmap(arena, types, Rows, _ => true)),
        Shape.BitmapNoneSet => Validity.Bitmap(Bitmap(arena, types, Rows, _ => false)),

        // Runs of both, of lengths that straddle byte and word boundaries, plus a long all-valid
        // stretch so the hoisted branch is exercised inside a mixed column too.
        _ => Validity.Bitmap(Bitmap(arena, types, Rows, i => (i / 37 % 3) != 0 || i is > 900 and < 1500)),
    };

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

        return arena.AddBool(types.Bool(Nullability.NonNullable), rows, Validity.NonNullable, bits, 0);
    }

    private static int Doubles(
        CanonicalArena arena, DTypeArena types, ReadOnlySpan<double> values, Shape shape)
    {
        VortexBuffer buffer = arena.Allocate(
            values.Length * sizeof(double), sizeof(double), out Span<byte> destination);
        for (int i = 0; i < values.Length; i++)
        {
            BinaryPrimitives.WriteDoubleLittleEndian(
                destination.Slice(i * sizeof(double), sizeof(double)), values[i]);
        }

        DType dtype = types.Primitive(
            PType.F64, shape == Shape.NonNullable ? Nullability.NonNullable : Nullability.Nullable);
        return arena.AddPrimitive(dtype, values.Length, Validity.NonNullable, PType.F64, buffer);
    }

    private static double ReadFloat(ReadOnlySpan<byte> values, PType ptype, int i) => ptype switch
    {
        PType.F16 => (double)BinaryPrimitives.ReadHalfLittleEndian(values.Slice(i * 2, 2)),
        PType.F32 => BinaryPrimitives.ReadSingleLittleEndian(values.Slice(i * 4, 4)),
        _ => BinaryPrimitives.ReadDoubleLittleEndian(values.Slice(i * 8, 8)),
    };

    private static long ReadSigned(ReadOnlySpan<byte> values, PType ptype, int i) => ptype switch
    {
        PType.I8 => (sbyte)values[i],
        PType.I16 => BinaryPrimitives.ReadInt16LittleEndian(values.Slice(i * 2, 2)),
        PType.I32 => BinaryPrimitives.ReadInt32LittleEndian(values.Slice(i * 4, 4)),
        _ => BinaryPrimitives.ReadInt64LittleEndian(values.Slice(i * 8, 8)),
    };

    private static ulong ReadUnsignedValue(ReadOnlySpan<byte> values, PType ptype, int i) => ptype switch
    {
        PType.U8 => values[i],
        PType.U16 => BinaryPrimitives.ReadUInt16LittleEndian(values.Slice(i * 2, 2)),
        PType.U32 => BinaryPrimitives.ReadUInt32LittleEndian(values.Slice(i * 4, 4)),
        _ => BinaryPrimitives.ReadUInt64LittleEndian(values.Slice(i * 8, 8)),
    };
}
