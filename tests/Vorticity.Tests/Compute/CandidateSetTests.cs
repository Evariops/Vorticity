using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;
using Vorticity.Types.Numerics;
using Xunit;

namespace Vorticity.Tests.Compute;

/// <summary>
/// An <c>IN</c> read against its candidates hashed once answers every row as the OR of equalities
/// it stands for, whatever the column's type, and refuses what those equalities refuse.
/// </summary>
public sealed class CandidateSetTests
{
    private const int Rows = 1_000;

    /// <summary>A column type a set is built for.</summary>
    public enum Shape
    {
        /// <summary>Strings, inline in their view and in the heap.</summary>
        Utf8,

        /// <summary>Strings behind a dictionary, answered through its values.</summary>
        DictionaryUtf8,

        /// <summary>Doubles, with both zeros, NaN and the infinities.</summary>
        F64,

        /// <summary>Floats, which a double candidate meets widened.</summary>
        F32,

        /// <summary>Halves.</summary>
        F16,

        /// <summary>Fixed-size lists of sixteen bytes.</summary>
        Uuid,

        /// <summary>Fixed-size lists of five bytes.</summary>
        FixedFive,

        /// <summary>Decimals stored in eight bytes.</summary>
        Decimal64,

        /// <summary>Decimals stored in sixteen bytes.</summary>
        Decimal128,

        /// <summary>Decimals stored in thirty-two bytes.</summary>
        Decimal256,

        /// <summary>Signed integers.</summary>
        I32,

        /// <summary>Unsigned integers, some above the signed maximum.</summary>
        U64,
    }

    public static TheoryData<Shape> Shapes() =>
    [
        Shape.Utf8, Shape.DictionaryUtf8, Shape.F64, Shape.F32, Shape.F16, Shape.Uuid, Shape.FixedFive,
        Shape.Decimal64, Shape.Decimal128, Shape.Decimal256, Shape.I32, Shape.U64,
    ];

    [Theory]
    [MemberData(nameof(Shapes))]
    internal void ASetAnswersEachRowAsTheEqualitiesItStandsFor(Shape shape)
    {
        Random random = new Random(20260926 + (int)shape);
        byte[] expected = new byte[Rows];
        byte[] actual = new byte[Rows];
        byte[] scratch = new byte[Rows];
        int built = 0;
        const int Trials = 40;
        for (int trial = 0; trial < Trials; trial++)
        {
            CanonicalArena arena = new CanonicalArena();
            try
            {
                Column column = Column.Build(arena, new DTypeArena(), shape, random);
                FilterLiteral[] literals = new FilterLiteral[random.Next(1, 40)];
                for (int i = 0; i < literals.Length; i++)
                {
                    // Duplicates and a null candidate as well as values of the column and beside it.
                    literals[i] = i > 0 && random.Next(8) == 0 ? literals[random.Next(i)]
                        : random.Next(16) == 0 ? FilterLiteral.Null
                        : column.Draw(random);
                }

                ComparisonKernels.In(arena, column.Node, literals, expected, scratch, prepared: null);
                CandidateSet? set = CandidateSet.For(literals, ComparisonKernels.CandidatesFor(arena, column.Node));
                built += set is null ? 0 : 1;
                ComparisonKernels.In(arena, column.Node, literals, actual, scratch, set);
                for (int row = 0; row < Rows; row++)
                {
                    if (actual[row] != expected[row])
                    {
                        Assert.Fail(
                            $"{shape}, {literals.Length} candidates: row {row} is {actual[row]}, " +
                            $"its equalities say {expected[row]}");
                    }
                }
            }
            finally
            {
                arena.Reset();
            }
        }

        Assert.True(built >= Trials * 3 / 4, $"{shape}: {built} of {Trials} trials built a set.");
    }

    [Fact]
    internal void AnInOverStringsAnswersAsItsDisjunctionThroughTheEvaluator()
    {
        Random random = new Random(7);
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        Column column = Column.Build(arena, types, Shape.Utf8, random);
        DType schema = types.Struct(["s"], [types.Utf8(Nullability.Nullable)], Nullability.NonNullable);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column.Node]);

        FilterLiteral[] literals = new FilterLiteral[12];
        VortexExpr disjunction = null!;
        for (int i = 0; i < literals.Length; i++)
        {
            literals[i] = column.Draw(random);
            ComparisonExpr equal = Expr.Eq(Expr.Field("s"), Expr.Literal(literals[i]));
            disjunction = i == 0 ? equal : Expr.Or(disjunction, equal);
        }

        byte[] expected = new byte[Rows];
        byte[] actual = new byte[Rows];
        new FilterEvaluator(disjunction).Evaluate(arena, root, Rows, expected);
        FilterEvaluator membership = new FilterEvaluator(Expr.In(Expr.Field("s"), literals));

        // Twice: the second batch reads the set the first one built.
        for (int batch = 0; batch < 2; batch++)
        {
            actual.AsSpan().Clear();
            membership.Evaluate(arena, root, Rows, actual);
            Assert.Equal(expected, actual);
        }

        Assert.Contains(Trilean.True, actual);
        arena.Reset();
    }

    [Fact]
    internal void AnUnsignedCandidateAgainstADecimalIsRefusedHoweverManyCandidatesThereAre()
    {
        Random random = new Random(11);
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        Column column = Column.Build(arena, types, Shape.Decimal64, random);
        DType schema = types.Struct(["d"], [types.Decimal(18, 2, Nullability.Nullable)], Nullability.NonNullable);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column.Node]);
        byte[] states = new byte[Rows];

        FilterLiteral[] few = [FilterLiteral.From(1L), FilterLiteral.From(2UL)];
        FilterLiteral[] many =
        [
            FilterLiteral.From(1L), FilterLiteral.From(2L), FilterLiteral.From(3L), FilterLiteral.From(4L),
            FilterLiteral.From(5L), FilterLiteral.From(6UL),
        ];

        foreach (FilterLiteral[] literals in new[] { few, many })
        {
            FilterEvaluator evaluator = new FilterEvaluator(Expr.In(Expr.Field("d"), literals));
            Assert.Throws<NotSupportedException>(() => evaluator.Evaluate(arena, root, Rows, states));
        }

        arena.Reset();
    }

    /// <summary>A column of one shape, and a way to draw candidates for it.</summary>
    private sealed class Column
    {
        private Column(int node, Func<Random, FilterLiteral> draw)
        {
            Node = node;
            Draw = draw;
        }

        /// <summary>The column's node.</summary>
        internal int Node { get; }

        /// <summary>One candidate: a value of the column's rows most of the time, one beside them otherwise.</summary>
        internal Func<Random, FilterLiteral> Draw { get; }

        internal static Column Build(CanonicalArena arena, DTypeArena types, Shape shape, Random random)
        {
            bool[] valid = new bool[Rows];
            for (int row = 0; row < Rows; row++)
            {
                valid[row] = random.Next(10) != 0;
            }

            return shape switch
            {
                Shape.Utf8 => Strings(arena, types, random, valid, dictionary: false),
                Shape.DictionaryUtf8 => Strings(arena, types, random, valid, dictionary: true),
                Shape.F64 => Doubles(arena, types, random, valid),
                Shape.F32 => Floats(arena, types, random, valid),
                Shape.F16 => Halves(arena, types, random, valid),
                Shape.Uuid => Fixed(arena, types, random, valid, 16),
                Shape.FixedFive => Fixed(arena, types, random, valid, 5),
                Shape.Decimal64 => Decimals(arena, types, random, valid, DecimalStorageType.I64),
                Shape.Decimal128 => Decimals(arena, types, random, valid, DecimalStorageType.I128),
                Shape.Decimal256 => Decimals(arena, types, random, valid, DecimalStorageType.I256),
                Shape.I32 => Ints(arena, types, random, valid),
                _ => Ulongs(arena, types, random, valid),
            };
        }

        private static Column Strings(
            CanonicalArena arena, DTypeArena types, Random random, bool[] valid, bool dictionary)
        {
            // Empty, short, exactly inline, just out of line and long.
            byte[][] domain = new byte[24][];
            for (int i = 0; i < domain.Length; i++)
            {
                domain[i] = Text(random, i % 6 switch { 0 => 0, 1 => 1, 2 => 5, 3 => 12, 4 => 13, _ => 30 });
            }

            DType utf8 = types.Utf8(Nullability.Nullable);
            int node;
            if (dictionary)
            {
                int values = Views(arena, types.Utf8(Nullability.NonNullable), domain, Validity.NonNullable);
                VortexBuffer codes = arena.Allocate(Rows * sizeof(uint), sizeof(uint), out Span<byte> bytes);
                Span<uint> code = MemoryMarshal.Cast<byte, uint>(bytes);
                for (int row = 0; row < Rows; row++)
                {
                    code[row] = (uint)random.Next(domain.Length);
                }

                node = arena.AddDictionary(utf8, Rows, Valid(arena, types, valid), codes, values);
            }
            else
            {
                byte[][] rows = new byte[Rows][];
                for (int row = 0; row < Rows; row++)
                {
                    rows[row] = valid[row] ? domain[random.Next(domain.Length)] : [];
                }

                node = Views(arena, utf8, rows, Valid(arena, types, valid));
            }

            return new Column(node, r => FilterLiteral.From(
                r.Next(5) == 0 ? Text(r, r.Next(0, 20)) : domain[r.Next(domain.Length)]));
        }

        private static byte[] Text(Random random, int length)
        {
            byte[] text = new byte[length];
            for (int i = 0; i < length; i++)
            {
                text[i] = (byte)random.Next('a', 'e');
            }

            return text;
        }

        private static int Views(CanonicalArena arena, DType dtype, byte[][] values, Validity validity)
        {
            int total = 0;
            foreach (byte[] value in values)
            {
                total += value.Length;
            }

            VortexBuffer heap = arena.Allocate(Math.Max(total, 1), 1, out Span<byte> heapBytes);
            VortexBuffer views = arena.Allocate(values.Length * 16, 16, out Span<byte> viewBytes);
            int offset = 0;
            for (int row = 0; row < values.Length; row++)
            {
                byte[] value = values[row];
                Span<byte> view = viewBytes.Slice(row * 16, 16);
                view.Clear();
                BinaryPrimitives.WriteInt32LittleEndian(view, value.Length);
                if (value.Length <= 12)
                {
                    value.CopyTo(view[4..]);
                }
                else
                {
                    value.AsSpan(0, 4).CopyTo(view.Slice(4, 4));
                    BinaryPrimitives.WriteInt32LittleEndian(view[12..], offset);
                }

                value.CopyTo(heapBytes[offset..]);
                offset += value.Length;
            }

            return arena.AddVarBinView(dtype, values.Length, validity, views, [heap]);
        }

        private static readonly double[] DoubleDomain =
        [
            0.0, -0.0, double.NaN, 1.0, 1.5, -2.25, 3.0, 42.0, 1000.25, 0.1, 1e300, -1e-300,
            double.Epsilon, double.PositiveInfinity, double.NegativeInfinity,
        ];

        /// <summary>A number of the doubles' domain, or an integer candidate a float column meets widened.</summary>
        private static FilterLiteral NumberCandidate(Random random, double[] domain) => random.Next(6) switch
        {
            0 => FilterLiteral.From((long)random.Next(-3, 50)),
            1 => FilterLiteral.From(random.Next(2) == 0 ? 3UL : ulong.MaxValue),
            2 => FilterLiteral.From(random.NextDouble() * 100),
            _ => FilterLiteral.From(domain[random.Next(domain.Length)]),
        };

        private static Column Doubles(CanonicalArena arena, DTypeArena types, Random random, bool[] valid)
        {
            VortexBuffer buffer = arena.Allocate(Rows * sizeof(double), sizeof(double), out Span<byte> bytes);
            Span<double> values = MemoryMarshal.Cast<byte, double>(bytes);
            for (int row = 0; row < Rows; row++)
            {
                values[row] = DoubleDomain[random.Next(DoubleDomain.Length)];
            }

            int node = arena.AddPrimitive(
                types.Primitive(PType.F64, Nullability.Nullable), Rows, Valid(arena, types, valid), PType.F64, buffer);
            return new Column(node, r => NumberCandidate(r, DoubleDomain));
        }

        private static Column Floats(CanonicalArena arena, DTypeArena types, Random random, bool[] valid)
        {
            float[] domain = [0f, -0f, float.NaN, 1.5f, 0.1f, 3f, -2.25f, float.MaxValue, float.PositiveInfinity];
            double[] widened = Array.ConvertAll(domain, value => (double)value);
            VortexBuffer buffer = arena.Allocate(Rows * sizeof(float), sizeof(float), out Span<byte> bytes);
            Span<float> values = MemoryMarshal.Cast<byte, float>(bytes);
            for (int row = 0; row < Rows; row++)
            {
                values[row] = domain[random.Next(domain.Length)];
            }

            int node = arena.AddPrimitive(
                types.Primitive(PType.F32, Nullability.Nullable), Rows, Valid(arena, types, valid), PType.F32, buffer);

            // 0.1 as a double is not 0.1f widened, and the equality holds them apart.
            return new Column(node, r => r.Next(8) == 0 ? FilterLiteral.From(0.1) : NumberCandidate(r, widened));
        }

        private static Column Halves(CanonicalArena arena, DTypeArena types, Random random, bool[] valid)
        {
            Half[] domain = [(Half)0, -(Half)0, Half.NaN, (Half)1.5, (Half)3, (Half)0.1, Half.MaxValue];
            double[] widened = Array.ConvertAll(domain, value => (double)value);
            VortexBuffer buffer = arena.Allocate(Rows * 2, 2, out Span<byte> bytes);
            Span<Half> values = MemoryMarshal.Cast<byte, Half>(bytes);
            for (int row = 0; row < Rows; row++)
            {
                values[row] = domain[random.Next(domain.Length)];
            }

            int node = arena.AddPrimitive(
                types.Primitive(PType.F16, Nullability.Nullable), Rows, Valid(arena, types, valid), PType.F16, buffer);
            return new Column(node, r => NumberCandidate(r, widened));
        }

        private static Column Fixed(CanonicalArena arena, DTypeArena types, Random random, bool[] valid, int size)
        {
            byte[][] domain = new byte[12][];
            for (int i = 0; i < domain.Length; i++)
            {
                domain[i] = new byte[size];
                random.NextBytes(domain[i]);
            }

            DType u8 = types.Primitive(PType.U8, Nullability.NonNullable);
            VortexBuffer buffer = arena.Allocate(Rows * size, 16, out Span<byte> bytes);
            for (int row = 0; row < Rows; row++)
            {
                domain[random.Next(domain.Length)].CopyTo(bytes.Slice(row * size, size));
            }

            int elements = arena.AddPrimitive(u8, Rows * size, Validity.NonNullable, PType.U8, buffer);
            int node = arena.AddFixedSizeList(
                types.FixedSizeList(u8, (uint)size, Nullability.Nullable), Rows, Valid(arena, types, valid),
                elements, (uint)size);

            // Values of another length never equal a row, and a set must not take them for one.
            return new Column(node, r => r.Next(6) switch
            {
                0 => FilterLiteral.From(Text(r, size + r.Next(-1, 2))),
                1 => FilterLiteral.From(Text(r, size)),
                _ => FilterLiteral.From(domain[r.Next(domain.Length)]),
            });
        }

        private static Column Decimals(
            CanonicalArena arena, DTypeArena types, Random random, bool[] valid, DecimalStorageType storage)
        {
            int width = storage switch
            {
                DecimalStorageType.I64 => 8,
                DecimalStorageType.I128 => 16,
                _ => Int256.ByteCount,
            };

            // Values of each width's range, the edges of the narrower ones among them.
            Int256[] domain = new Int256[16];
            for (int i = 0; i < domain.Length; i++)
            {
                domain[i] = (i % 4, width) switch
                {
                    (0, _) => new Int256(random.NextInt64(-1000, 1000)),
                    (1, _) => new Int256(i % 8 == 1 ? long.MinValue : long.MaxValue),
                    (2, >= 16) => new Int256(Int128.MaxValue - random.Next(3)),
                    (3, 32) => Int256.FromLimbs((ulong)random.NextInt64(), (ulong)random.NextInt64(), 7UL, 1UL << 62),
                    _ => new Int256(random.NextInt64()),
                };
            }

            VortexBuffer buffer = arena.Allocate(Rows * width, width, out Span<byte> bytes);
            Span<byte> wide = stackalloc byte[Int256.ByteCount];
            for (int row = 0; row < Rows; row++)
            {
                domain[random.Next(domain.Length)].WriteLittleEndianBytes(wide);
                wide[..width].CopyTo(bytes.Slice(row * width, width));
            }

            byte precision = width switch { 8 => 18, 16 => 38, _ => 76 };
            int node = arena.AddDecimal(
                types.Decimal(precision, 2, Nullability.Nullable), Rows, Valid(arena, types, valid), storage,
                precision, 2, buffer);
            return new Column(node, r => DecimalCandidate(r, domain));
        }

        /// <summary>A decimal candidate as a signed integer or as sixteen or thirty-two bytes, a value beyond the column's storage now and then.</summary>
        private static FilterLiteral DecimalCandidate(Random random, Int256[] domain)
        {
            Int256 value = random.Next(8) == 0
                ? Int256.FromLimbs((ulong)random.NextInt64(), 3UL, 5UL, (ulong)random.NextInt64(1, long.MaxValue))
                : domain[random.Next(domain.Length)];
            byte[] bytes = new byte[Int256.ByteCount];
            value.WriteLittleEndianBytes(bytes);
            return random.Next(3) switch
            {
                0 when value.TryToInt64(out long narrow) => FilterLiteral.From(narrow),
                1 when value.TryToInt128(out Int128 middle) => FilterLiteral.From(Sixteen(middle)),
                _ => FilterLiteral.From(bytes),
            };
        }

        private static byte[] Sixteen(Int128 value)
        {
            byte[] bytes = new byte[16];
            BinaryPrimitives.WriteInt128LittleEndian(bytes, value);
            return bytes;
        }

        private static Column Ints(CanonicalArena arena, DTypeArena types, Random random, bool[] valid)
        {
            VortexBuffer buffer = arena.Allocate(Rows * sizeof(int), sizeof(int), out Span<byte> bytes);
            Span<int> values = MemoryMarshal.Cast<byte, int>(bytes);
            for (int row = 0; row < Rows; row++)
            {
                values[row] = random.Next(-40, 40);
            }

            int node = arena.AddPrimitive(
                types.Primitive(PType.I32, Nullability.Nullable), Rows, Valid(arena, types, valid), PType.I32, buffer);
            return new Column(node, r => r.Next(5) switch
            {
                0 => FilterLiteral.From(r.Next(2) == 0 ? 7UL : ulong.MaxValue - 3),
                _ => FilterLiteral.From((long)r.Next(-50, 50)),
            });
        }

        private static Column Ulongs(CanonicalArena arena, DTypeArena types, Random random, bool[] valid)
        {
            ulong[] domain = [0, 1, 7, 40, long.MaxValue, (ulong)long.MaxValue + 1, ulong.MaxValue - 3, ulong.MaxValue];
            VortexBuffer buffer = arena.Allocate(Rows * sizeof(ulong), sizeof(ulong), out Span<byte> bytes);
            Span<ulong> values = MemoryMarshal.Cast<byte, ulong>(bytes);
            for (int row = 0; row < Rows; row++)
            {
                values[row] = domain[random.Next(domain.Length)];
            }

            int node = arena.AddPrimitive(
                types.Primitive(PType.U64, Nullability.Nullable), Rows, Valid(arena, types, valid), PType.U64, buffer);

            // A negative candidate shares its bits with a value above the signed maximum.
            return new Column(node, r => r.Next(4) switch
            {
                0 => FilterLiteral.From(-(long)r.Next(1, 5)),
                1 => FilterLiteral.From((long)r.Next(0, 50)),
                _ => FilterLiteral.From(domain[r.Next(domain.Length)]),
            });
        }

        private static Validity Valid(CanonicalArena arena, DTypeArena types, bool[] valid)
        {
            VortexBuffer buffer = arena.Allocate((Rows + 7) / 8, 1, out Span<byte> bytes);
            bytes.Clear();
            for (int row = 0; row < Rows; row++)
            {
                if (valid[row])
                {
                    bytes[row >> 3] |= (byte)(1 << (row & 7));
                }
            }

            return Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, buffer, 0));
        }
    }
}
