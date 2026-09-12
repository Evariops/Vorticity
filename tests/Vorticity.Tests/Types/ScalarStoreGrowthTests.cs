using System;
using System.Globalization;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Types;

/// <summary>
/// The store hands out spans into its own byte array through <see cref="ScalarValue.AsBytes"/> and
/// accepts them back through <see cref="ScalarStore.Bytes"/>. Every such call can trigger a resize
/// of the array being read from.
/// </summary>
public sealed class ScalarStoreGrowthTests
{
    [Fact]
    public void StoringAValuesOwnBytesBackIntoTheStoreIsSafe()
    {
        ScalarStore store = new(4);
        byte[] payload = new byte[64];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(255 - i);
        }

        ScalarValue first = store.Bytes(payload);
        for (int round = 0; round < 20; round++)
        {
            ScalarValue copy = store.Bytes(first.AsBytes);
            bool same = copy.AsBytes.SequenceEqual(payload);
            Assert.True(same, $"corrupted at round {round}");
            Assert.Equal(first, copy);
            first = copy;
        }
    }

    [Fact]
    public void SubSlicesOfStoredBytesRoundTrip()
    {
        ScalarStore store = new(4);
        byte[] payload = [10, 20, 30, 40, 50, 60, 70, 80];
        ScalarValue whole = store.Bytes(payload);

        ScalarValue half = store.Bytes(whole.AsBytes[..4]);

        bool sliced = half.AsBytes.SequenceEqual([(byte)10, (byte)20, (byte)30, (byte)40]);
        Assert.True(sliced);
        bool wholeIntact = whole.AsBytes.SequenceEqual(payload);
        Assert.True(wholeIntact);
    }

    [Fact]
    public void ManyValuesSurviveNodeArrayGrowth()
    {
        const int Count = 3000;
        ScalarStore store = new(4);
        ScalarValue[] values = new ScalarValue[Count];
        for (int i = 0; i < Count; i++)
        {
            values[i] = (i % 4) switch
            {
                0 => store.Int64(i),
                1 => store.String("s" + i.ToString(CultureInfo.InvariantCulture)),
                2 => store.F64(i * 0.5),
                _ => store.List([store.Int64(i), store.Bool(i % 8 == 0)]),
            };
        }

        Assert.Equal(Count, store.NodeCount - (Count / 4 * 2));
        for (int i = 0; i < Count; i++)
        {
            ScalarValue expected = (i % 4) switch
            {
                0 => store.Int64(i),
                1 => store.String("s" + i.ToString(CultureInfo.InvariantCulture)),
                2 => store.F64(i * 0.5),
                _ => store.List([store.Int64(i), store.Bool(i % 8 == 0)]),
            };
            Assert.Equal(expected, values[i]);
        }
    }

    [Fact]
    public void ListChildArrayGrowthPreservesElementOrder()
    {
        const int Count = 1000;
        ScalarStore store = new(4);
        ScalarValue[] elements = new ScalarValue[Count];
        for (int i = 0; i < Count; i++)
        {
            elements[i] = store.Int64(i);
        }

        ScalarValue list = store.List(elements);

        Assert.Equal(Count, list.ListCount);
        for (int i = 0; i < Count; i++)
        {
            Assert.Equal(i, list.GetListElement(i).AsInt64);
        }
    }

    [Fact]
    public void VariantTypeArrayGrowthKeepsEachDTypeWithItsValue()
    {
        DTypeArena arena = new(4);
        ScalarStore store = new(4);
        const int Count = 200;
        ScalarValue[] variants = new ScalarValue[Count];
        DType[] dtypes = new DType[Count];

        for (int i = 0; i < Count; i++)
        {
            dtypes[i] = arena.Primitive((PType)(i % 11), i % 2 == 0 ? Nullability.Nullable : Nullability.NonNullable);
            variants[i] = store.Variant(new Scalar(dtypes[i], store.Int64(i)));
        }

        for (int i = 0; i < Count; i++)
        {
            Scalar s = variants[i].AsVariant;
            Assert.Equal(dtypes[i], s.DType);
            Assert.Equal(i, s.Value.AsInt64);
        }
    }

    [Fact]
    public void ClearDropsDTypeReferencesHeldByVariants()
    {
        // Variants pin a DTypeArena through the store; Clear must release them so a long-lived
        // store does not keep every schema it ever saw alive.
        DTypeArena arena = new();
        ScalarStore store = new();
        store.Variant(new Scalar(arena.Bool(Nullability.NonNullable), store.Bool(true)));

        store.Clear();

        Assert.Equal(0, store.NodeCount);
        ScalarValue reused = store.Variant(new Scalar(arena.Utf8(Nullability.Nullable), store.String("x"u8)));
        Assert.Equal(DTypeKind.Utf8, reused.AsVariant.DType.Kind);
    }
}
