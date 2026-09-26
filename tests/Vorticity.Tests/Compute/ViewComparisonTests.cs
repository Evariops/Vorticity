// Text comparisons decided from views, against an oracle that compares each row's bytes.
//
// The kernels decide a row from its view when the view holds enough -- its length and first four
// bytes, and the next eight when the value is inline -- and read the value only otherwise. So the
// values here are the ones that make that hard: every length around the four bytes of a view's
// prefix and the twelve of an inline value, values that are prefixes of the literal and literals
// that are prefixes of values, zero bytes that look like the padding past a value's end, 0xFF
// bytes, inline views whose bytes past the length are not zero, and null rows whose views point
// nowhere.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class ViewComparisonTests
{
    private const int ViewSize = 16;

    private static readonly ComparisonOp[] Ops =
    [
        ComparisonOp.Equal, ComparisonOp.NotEqual, ComparisonOp.Less,
        ComparisonOp.LessOrEqual, ComparisonOp.Greater, ComparisonOp.GreaterOrEqual,
    ];

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryRowIsAnsweredAsItsBytesAnswerIt(bool nulls)
    {
        Random random = new Random(nulls ? 11 : 7);
        List<byte[]> values = Values(random);
        int rows = values.Count;
        bool[] valid = new bool[rows];
        for (int i = 0; i < rows; i++)
        {
            valid[i] = !nulls || random.Next(5) != 0;
        }

        CanonicalArena arena = new CanonicalArena();
        int node = Column(arena, values, valid, random);
        byte[] states = new byte[rows];

        List<byte[]> literals = [];
        for (int k = 0; k < 40; k++)
        {
            literals.Add(values[random.Next(rows)]);
        }

        literals.AddRange(Edges());
        foreach (byte[] literal in literals)
        {
            foreach (ComparisonOp op in Ops)
            {
                ComparisonKernels.Compare(arena, node, op, FilterLiteral.From(literal), states);
                for (int i = 0; i < rows; i++)
                {
                    byte expected = !valid[i] ? Trilean.Unknown : Holds(op, values[i].AsSpan().SequenceCompareTo(literal)) ? Trilean.True : Trilean.False;
                    Assert.True(expected == states[i], $"{op} of row {i} ({Convert.ToHexString(values[i])}) against {Convert.ToHexString(literal)}");
                }
            }

            ComparisonKernels.StringMatch(arena, node, StringMatchOp.StartsWith, FilterLiteral.From(literal), 0, states);
            for (int i = 0; i < rows; i++)
            {
                byte expected = !valid[i] ? Trilean.Unknown : values[i].AsSpan().StartsWith(literal) ? Trilean.True : Trilean.False;
                Assert.True(expected == states[i], $"StartsWith of row {i} ({Convert.ToHexString(values[i])}) against {Convert.ToHexString(literal)}");
            }
        }
    }

    private static bool Holds(ComparisonOp op, int order) => op switch
    {
        ComparisonOp.Equal => order == 0,
        ComparisonOp.NotEqual => order != 0,
        ComparisonOp.Less => order < 0,
        ComparisonOp.LessOrEqual => order <= 0,
        ComparisonOp.Greater => order > 0,
        _ => order >= 0,
    };

    /// <summary>
    /// Values over a small alphabet, so rows share prefixes of every length, at every length
    /// from 0 to 45: past the twelve of an inline value, and past the 32 compared at once after a
    /// view's four.
    /// </summary>
    private static List<byte[]> Values(Random random)
    {
        byte[] alphabet = [0x00, 0x01, (byte)'a', (byte)'b', 0x7F, 0x80, 0xFF];
        byte[] stem = new byte[45];
        byte[] motif = [(byte)'a', (byte)'b', 0x00, (byte)'a', (byte)'b', (byte)'a', 0xFF, (byte)'b', (byte)'a', 0x00, (byte)'a', (byte)'b', (byte)'a', (byte)'b', 0x01];
        for (int i = 0; i < stem.Length; i++)
        {
            stem[i] = motif[i % motif.Length];
        }

        List<byte[]> values = [];
        for (int length = 0; length <= stem.Length; length++)
        {
            values.Add(stem[..length]);
            for (int k = 0; k < 12; k++)
            {
                byte[] value = stem[..length];
                if (length > 0)
                {
                    // Mostly the stem, one byte changed, so the rows agree with each other far in.
                    value[random.Next(length)] = alphabet[random.Next(alphabet.Length)];
                }

                values.Add(value);
            }
        }

        return values;
    }

    /// <summary>Literals the column's values extend, end short of, or never reach.</summary>
    private static IEnumerable<byte[]> Edges()
    {
        yield return [];
        yield return [0x00];
        yield return [(byte)'a'];
        yield return [(byte)'a', (byte)'b', 0x00];
        yield return [(byte)'a', (byte)'b', 0x00, 0x00];
        yield return [(byte)'a', (byte)'b', 0x00, (byte)'a', 0x00];
        yield return [0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF];
        yield return [(byte)'a', (byte)'b', 0x00, (byte)'a', (byte)'b', (byte)'a', 0xFF, (byte)'b', (byte)'a', 0x00, (byte)'a', (byte)'b'];
        yield return [(byte)'a', (byte)'b', 0x00, (byte)'a', (byte)'b', (byte)'a', 0xFF, (byte)'b', (byte)'a', 0x00, (byte)'a', (byte)'b', 0x00];
        yield return [(byte)'a', (byte)'b', 0x00, (byte)'a', (byte)'b', (byte)'a', 0xFF, (byte)'b', (byte)'a', 0x00, (byte)'a', (byte)'b', (byte)'a', (byte)'b', 0x01, (byte)'a', (byte)'b', (byte)'a', (byte)'b', (byte)'a', (byte)'z'];
    }

    /// <summary>
    /// A varbinview column of <paramref name="values"/>: an inline view's bytes past its length
    /// are noise, and a null row's view claims a long value in a buffer that is not there.
    /// </summary>
    private static int Column(CanonicalArena arena, List<byte[]> values, bool[] valid, Random random)
    {
        int rows = values.Count;
        DTypeArena types = new DTypeArena();
        int heapBytes = 1;
        foreach (byte[] value in values)
        {
            heapBytes += value.Length > 12 ? value.Length : 0;
        }

        VortexBuffer views = arena.Allocate(rows * ViewSize, ViewSize, out Span<byte> viewBytes);
        VortexBuffer heap = arena.Allocate(heapBytes, 1, out Span<byte> heapSpan);
        VortexBuffer second = arena.Allocate(heapBytes, 1, out Span<byte> secondSpan);
        int[] offsets = [0, 0];
        int longRows = 0;
        for (int i = 0; i < rows; i++)
        {
            byte[] bytes = values[i];
            Span<byte> view = viewBytes.Slice(i * ViewSize, ViewSize);
            random.NextBytes(view);
            if (!valid[i])
            {
                BinaryPrimitives.WriteUInt32LittleEndian(view, 1000);
                BinaryPrimitives.WriteUInt32LittleEndian(view[8..], 7);
                continue;
            }

            BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)bytes.Length);
            if (bytes.Length <= 12)
            {
                bytes.CopyTo(view[4..]);
                continue;
            }

            bytes.AsSpan(0, 4).CopyTo(view[4..8]);
            // One long value in three lives in a second buffer, which the kernels resolve by row.
            int buffer = longRows++ % 3 == 2 ? 1 : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(view[8..12], (uint)buffer);
            BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)offsets[buffer]);
            bytes.CopyTo((buffer == 0 ? heapSpan : secondSpan)[offsets[buffer]..]);
            offsets[buffer] += bytes.Length;
        }

        Validity validity = Validity.NonNullable;
        if (Array.IndexOf(valid, false) >= 0)
        {
            VortexBuffer bits = arena.Allocate((rows + 7) / 8, 1, out Span<byte> bitBytes);
            bitBytes.Clear();
            for (int i = 0; i < rows; i++)
            {
                if (valid[i])
                {
                    bitBytes[i >> 3] |= (byte)(1 << (i & 7));
                }
            }

            validity = Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), rows, Validity.NonNullable, bits, 0));
        }

        return arena.AddVarBinView(types.Binary(Nullability.Nullable), rows, validity, views, [heap, second]);
    }
}
