// An IN of texts over a column of views: a row is in the set exactly when its bytes are a candidate's, whether every
// candidate is held inline (found by the view's words) or one is not; a view's bytes past its value, which a writer may
// leave as they were, count for nothing; a null row is unknown.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Expressions;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class BytesInSetViewTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARowIsInTheSetByItsBytesAlone(bool oneLong)
    {
        Random random = new Random(173);
        string[] names = ["", "a", "ab", "abcd", "abcde", "service-xy", "service-xyz1", "service-xyz2", "a-longer-value-out-of-line", "é"];
        List<string> candidates = ["", "abcd", "service-xyz1", "é"];
        if (oneLong)
        {
            candidates.Add("a-longer-value-out-of-line");
        }

        const int Rows = 1_000;
        CanonicalArena arena = new CanonicalArena(64);
        VortexBuffer views = arena.Allocate(Rows * 16, 16, out Span<byte> view);
        VortexBuffer heap = arena.Allocate(Rows * 32, 16, out Span<byte> bytes);
        VortexBuffer bits = arena.Allocate((Rows + 7) / 8, 8, out Span<byte> valid);
        byte[] expected = new byte[Rows];
        int at = 0;
        for (int row = 0; row < Rows; row++)
        {
            string name = names[random.Next(names.Length)];
            byte[] value = Encoding.UTF8.GetBytes(name);
            Span<byte> v = view.Slice(row * 16, 16);
            random.NextBytes(v);
            BinaryPrimitives.WriteInt32LittleEndian(v, value.Length);
            if (value.Length <= 12)
            {
                value.CopyTo(v[4..]);
            }
            else
            {
                value.AsSpan(0, 4).CopyTo(v[4..]);
                BinaryPrimitives.WriteInt32LittleEndian(v[8..], 0);
                BinaryPrimitives.WriteInt32LittleEndian(v[12..], at);
                value.CopyTo(bytes[at..]);
                at += value.Length;
            }

            bool isValid = row % 7 != 2;
            if (isValid)
            {
                valid[row >> 3] |= (byte)(1 << (row & 7));
            }

            expected[row] = !isValid ? Trilean.Unknown : candidates.Contains(name) ? Trilean.True : Trilean.False;
        }

        DTypeArena types = new DTypeArena();
        Validity validity = Validity.Bitmap(arena.AddBool(types.Bool(Nullability.NonNullable), Rows, Validity.NonNullable, bits, 0));
        int node = arena.AddVarBinView(types.Utf8(Nullability.Nullable), Rows, validity, views, [heap]);
        BytesInSet set = BytesInSet.TryBuild(candidates.ConvertAll(FilterLiteral.From).ToArray(), CandidateKind.Bytes)!;
        Assert.NotNull(set);
        byte[] states = new byte[Rows];
        set.Apply(arena.GetNode(node), ValidityMask.From(arena, validity), states);
        Assert.Equal(expected, states);
    }
}
