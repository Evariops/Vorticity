using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

/// <summary>
/// A filter's states over a batch are those of its three-valued logic row by row, whichever rows a
/// logical node's right side is evaluated over.
/// </summary>
public sealed class FilterEvaluationTests
{
    private const int Rows = 2_048;

    // Values spread over a range wide enough that a bound near either end keeps a few rows, which
    // is what sends a right side to the rows its left side left open.
    private const long Range = 10_000;

    private static readonly string[] Words = ["alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta"];

    [Fact]
    public void EveryFilterAnswersEachRowAsItsLogicDoes()
    {
        Random random = new Random(20260926);
        Batch batch = Batch.Build(random);
        byte[] states = new byte[Rows];
        for (int trial = 0; trial < 600; trial++)
        {
            (VortexExpr filter, Func<int, byte> expected) = RandomFilter(random, batch, depth: 0);
            new FilterEvaluator(filter).Evaluate(batch.Arena, batch.Root, Rows, states);
            for (int row = 0; row < Rows; row++)
            {
                if (states[row] != expected(row))
                {
                    Assert.Fail($"{ExprText.Format(filter)}: row {row} is {states[row]}, its logic says {expected(row)}");
                }
            }
        }
    }

    /// <summary>A filter and the state its logic gives each row.</summary>
    private static (VortexExpr Filter, Func<int, byte> Expected) RandomFilter(Random random, Batch batch, int depth)
    {
        int shape = depth >= 4 ? random.Next(4) : random.Next(8);
        switch (shape)
        {
            case 0:
            case 1:
            {
                // A comparison, whose bound sits near the ends of the range as often as inside it.
                string field = random.Next(2) == 0 ? "a" : "b";
                long?[] values = field == "a" ? batch.A : batch.B;
                long bound = random.Next(3) switch
                {
                    0 => random.NextInt64(0, Range / 100),
                    1 => random.NextInt64(Range - (Range / 100), Range),
                    _ => random.NextInt64(0, Range),
                };
                int op = random.Next(6);
                VortexExpr left = Expr.Field(field);
                VortexExpr right = Expr.Literal(FilterLiteral.From(bound));
                VortexExpr filter = op switch
                {
                    0 => Expr.Lt(left, right),
                    1 => Expr.Le(left, right),
                    2 => Expr.Gt(left, right),
                    3 => Expr.Ge(left, right),
                    4 => Expr.Eq(left, right),
                    _ => Expr.Ne(left, right),
                };
                return (filter, row => values[row] is long value
                    ? Truth(op switch
                    {
                        0 => value < bound,
                        1 => value <= bound,
                        2 => value > bound,
                        3 => value >= bound,
                        4 => value == bound,
                        _ => value != bound,
                    })
                    : Trilean.Unknown);
            }

            case 2:
            {
                string field = random.Next(3) switch { 0 => "a", 1 => "b", _ => "s" };
                bool isNull = random.Next(2) == 0;
                Func<int, bool> nulls = field switch
                {
                    "a" => row => batch.A[row] is null,
                    "b" => row => batch.B[row] is null,
                    _ => row => batch.S[row] is null,
                };
                return (isNull ? Expr.IsNull(Expr.Field(field)) : Expr.IsNotNull(Expr.Field(field)),
                    row => Truth(nulls(row) == isNull));
            }

            case 3:
            {
                // A pattern: a word anywhere, at the start or at the end.
                string word = Words[random.Next(Words.Length)];
                string needle = word.Substring(0, 1 + random.Next(word.Length - 1));
                int kind = random.Next(3);
                string pattern = kind switch { 0 => "%" + needle + "%", 1 => needle + "%", _ => "%" + needle };
                VortexExpr filter = Expr.Like(Expr.Field("s"), FilterLiteral.From(Encoding.UTF8.GetBytes(pattern)));
                return (filter, row => batch.S[row] is string value
                    ? Truth(kind switch
                    {
                        0 => value.Contains(needle, StringComparison.Ordinal),
                        1 => value.StartsWith(needle, StringComparison.Ordinal),
                        _ => value.EndsWith(needle, StringComparison.Ordinal),
                    })
                    : Trilean.Unknown);
            }

            case 4:
            {
                (VortexExpr operand, Func<int, byte> inner) = RandomFilter(random, batch, depth + 1);
                return (Expr.Not(operand), row => inner(row) switch
                {
                    Trilean.True => Trilean.False,
                    Trilean.False => Trilean.True,
                    _ => Trilean.Unknown,
                });
            }

            default:
            {
                (VortexExpr left, Func<int, byte> l) = RandomFilter(random, batch, depth + 1);
                (VortexExpr right, Func<int, byte> r) = RandomFilter(random, batch, depth + 1);
                if (random.Next(2) == 0)
                {
                    return (Expr.And(left, right), row =>
                    {
                        byte a = l(row);
                        byte b = r(row);
                        return a == Trilean.False || b == Trilean.False ? Trilean.False
                            : a == Trilean.Unknown || b == Trilean.Unknown ? Trilean.Unknown : Trilean.True;
                    });
                }

                return (Expr.Or(left, right), row =>
                {
                    byte a = l(row);
                    byte b = r(row);
                    return a == Trilean.True || b == Trilean.True ? Trilean.True
                        : a == Trilean.Unknown || b == Trilean.Unknown ? Trilean.Unknown : Trilean.False;
                });
            }
        }
    }

    private static byte Truth(bool value) => value ? Trilean.True : Trilean.False;

    /// <summary>A struct of two nullable integer columns and a nullable string column, and their values.</summary>
    private sealed class Batch
    {
        internal CanonicalArena Arena { get; } = new CanonicalArena();

        internal int Root { get; private set; }

        internal long?[] A { get; } = new long?[Rows];

        internal long?[] B { get; } = new long?[Rows];

        internal string?[] S { get; } = new string?[Rows];

        internal static Batch Build(Random random)
        {
            Batch batch = new Batch();
            DTypeArena types = new DTypeArena();
            DType i64 = types.Primitive(PType.I64, Nullability.Nullable);
            DType utf8 = types.Utf8(Nullability.Nullable);
            DType bits = types.Bool(Nullability.NonNullable);
            DType schema = types.Struct(["a", "b", "s"], [i64, i64, utf8], Nullability.NonNullable);
            for (int row = 0; row < Rows; row++)
            {
                batch.A[row] = random.Next(10) == 0 ? null : random.NextInt64(0, Range);
                batch.B[row] = random.Next(4) == 0 ? null : random.NextInt64(0, Range);
                batch.S[row] = random.Next(8) == 0 ? null
                    : Words[random.Next(Words.Length)] + "-" + Words[random.Next(Words.Length)] + random.Next(100);
            }

            CanonicalArena arena = batch.Arena;
            int a = Integers(arena, i64, bits, batch.A);
            int b = Integers(arena, i64, bits, batch.B);
            int s = Strings(arena, utf8, bits, batch.S);
            batch.Root = arena.AddStruct(schema, Rows, Validity.NonNullable, [a, b, s]);
            return batch;
        }

        private static int Integers(CanonicalArena arena, DType dtype, DType bits, long?[] values)
        {
            VortexBuffer buffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> longs = MemoryMarshal.Cast<byte, long>(bytes);
            for (int row = 0; row < Rows; row++)
            {
                longs[row] = values[row] ?? 0;
            }

            return arena.AddPrimitive(dtype, Rows, Valid(arena, bits, row => values[row] is not null), PType.I64, buffer);
        }

        private static int Strings(CanonicalArena arena, DType dtype, DType bits, string?[] values)
        {
            byte[][] encoded = new byte[Rows][];
            int total = 0;
            for (int row = 0; row < Rows; row++)
            {
                encoded[row] = Encoding.UTF8.GetBytes(values[row] ?? string.Empty);
                total += encoded[row].Length;
            }

            VortexBuffer heap = arena.Allocate(Math.Max(total, 1), 1, out Span<byte> heapBytes);
            VortexBuffer views = arena.Allocate(Rows * 16, 16, out Span<byte> viewBytes);
            int offset = 0;
            for (int row = 0; row < Rows; row++)
            {
                byte[] value = encoded[row];
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
                    BinaryPrimitives.WriteInt32LittleEndian(view.Slice(8), 0);
                    BinaryPrimitives.WriteInt32LittleEndian(view.Slice(12), offset);
                }

                value.CopyTo(heapBytes[offset..]);
                offset += value.Length;
            }

            return arena.AddVarBinView(dtype, Rows, Valid(arena, bits, row => values[row] is not null), views, [heap]);
        }

        private static Validity Valid(CanonicalArena arena, DType bits, Func<int, bool> valid)
        {
            VortexBuffer buffer = arena.Allocate((Rows + 7) / 8, 1, out Span<byte> bytes);
            bytes.Clear();
            for (int row = 0; row < Rows; row++)
            {
                if (valid(row))
                {
                    bytes[row >> 3] |= (byte)(1 << (row & 7));
                }
            }

            return Validity.Bitmap(arena.AddBool(bits, Rows, Validity.NonNullable, buffer, 0));
        }
    }
}
