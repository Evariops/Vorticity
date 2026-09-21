using System;
using System.Globalization;
using System.Threading;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Types;

/// <summary>
/// The scalar model. The load-bearing property is that <see cref="ScalarValueKind.Absent"/> and
/// <see cref="ScalarValueKind.Null"/> never collapse into each other: a statistic with no value
/// licenses nothing, while a null one is an assertion about the data.
/// </summary>
public sealed class ScalarValueTests
{
    [Fact]
    public void AbsentIsNotNull()
    {
        ScalarStore store = new();
        ScalarValue absent = store.Absent;
        ScalarValue nul = store.Null();

        Assert.Equal(ScalarValueKind.Absent, absent.Kind);
        Assert.Equal(ScalarValueKind.Null, nul.Kind);
        Assert.True(absent.IsAbsent);
        Assert.False(absent.IsNull);
        Assert.False(nul.IsAbsent);
        Assert.True(nul.IsNull);
        Assert.NotEqual(absent, nul);
        Assert.NotEqual(nul, absent);
    }

    [Fact]
    public void AbsentIsTheDefaultAndIsStoreless()
    {
        ScalarStore a = new();
        ScalarStore b = new();

        Assert.Equal(default, a.Absent);
        Assert.Equal(a.Absent, b.Absent);
        Assert.Null(a.Absent.Store);
        Assert.Equal(ScalarValueKind.Absent, default(ScalarValue).Kind);
        Assert.Equal(0, default(ScalarValue).GetHashCode());
    }

    [Fact]
    public void NullFromTwoStoresIsEqual()
    {
        ScalarStore a = new();
        ScalarStore b = new();
        Assert.Equal(a.Null(), b.Null());
        Assert.Equal(a.Null().GetHashCode(), b.Null().GetHashCode());
    }

    [Fact]
    public void NodeZeroIsNotMistakenForAbsent()
    {
        // The handle is stored biased by one precisely so that the first value in a store is not
        // indistinguishable from default(ScalarValue).
        ScalarStore store = new();
        ScalarValue first = store.Bool(false);

        Assert.Equal(ScalarValueKind.Bool, first.Kind);
        Assert.False(first.IsAbsent);
        Assert.NotEqual(store.Absent, first);
        Assert.Same(store, first.Store);
    }

    // ------------------------------------------------------------------ payload fidelity

    [Theory]
    [InlineData(long.MinValue)]
    [InlineData(-1L)]
    [InlineData(0L)]
    [InlineData(1L)]
    [InlineData(long.MaxValue)]
    public void Int64RoundTripsAtTheBoundaries(long value)
    {
        ScalarStore store = new();
        Assert.Equal(value, store.Int64(value).AsInt64);
    }

    [Theory]
    [InlineData(0UL)]
    [InlineData(1UL)]
    [InlineData(ulong.MaxValue)]
    public void UInt64RoundTripsAtTheBoundaries(ulong value)
    {
        ScalarStore store = new();
        Assert.Equal(value, store.UInt64(value).AsUInt64);
    }

    [Fact]
    public void Int64AndUInt64AreDistinctKindsEvenForTheSameBits()
    {
        ScalarStore store = new();
        Assert.NotEqual(store.Int64(-1), store.UInt64(ulong.MaxValue));
    }

    [Theory]
    [InlineData((ushort)0x0000)]
    [InlineData((ushort)0x8000)]
    [InlineData((ushort)0x3C00)]
    [InlineData((ushort)0x7C00)]
    [InlineData((ushort)0xFC00)]
    [InlineData((ushort)0x7E01)]
    [InlineData((ushort)0x7FFF)]
    public void F16KeepsItsExactBits(ushort bits)
    {
        // The ScalarValue message's f16_value is a uint64 varint carrying the raw binary16 bits,
        // so a NaN payload must survive: converting through Half and back must not canonicalise.
        ScalarStore store = new();
        ScalarValue v = store.F16FromBits(bits);

        Assert.Equal(bits, v.F16Bits);
        Assert.Equal(bits, BitConverter.HalfToUInt16Bits(v.AsF16));
        Assert.Equal(v, store.F16(BitConverter.UInt16BitsToHalf(bits)));
    }

    [Fact]
    public void FloatsCompareByBitsNotByIeee()
    {
        // Value identity, deliberately: NaN equals itself and +0 does not equal -0. Filter
        // evaluation uses IEEE 754 instead.
        ScalarStore a = new();
        ScalarStore b = new();

        Assert.Equal(a.F64(double.NaN), b.F64(double.NaN));
        Assert.Equal(a.F32(float.NaN), b.F32(float.NaN));
        Assert.NotEqual(a.F64(0.0), b.F64(-0.0));
        Assert.NotEqual(a.F32(0.0f), b.F32(-0.0f));
        Assert.Equal(a.F64(double.PositiveInfinity), b.F64(double.PositiveInfinity));
        Assert.NotEqual(a.F64(double.PositiveInfinity), b.F64(double.NegativeInfinity));

        // A quiet NaN and a signalling NaN have different bits and are different values.
        ScalarValue quiet = a.F64(BitConverter.UInt64BitsToDouble(0x7FF8_0000_0000_0000UL));
        ScalarValue signalling = b.F64(BitConverter.UInt64BitsToDouble(0x7FF0_0000_0000_0001UL));
        Assert.NotEqual(quiet, signalling);
    }

    [Fact]
    public void F32AndF64AreDistinctKinds()
    {
        ScalarStore store = new();
        Assert.NotEqual(store.F32(1.0f), store.F64(1.0));
    }

    [Fact]
    public void StringAndBytesAreDistinctEvenWithIdenticalBytes()
    {
        ScalarStore store = new();
        Assert.NotEqual(store.String("abc"u8), store.Bytes("abc"u8));
    }

    [Fact]
    public void StringsAndBytesCompareByContentAcrossStores()
    {
        ScalarStore a = new();
        ScalarStore b = new();

        Assert.Equal(a.String("héllo"), b.String("héllo"u8));
        Assert.Equal(a.String("héllo").GetHashCode(), b.String("héllo"u8).GetHashCode());
        Assert.NotEqual(a.String("ab"u8), b.String("abc"u8));
        Assert.Equal(a.Bytes(ReadOnlySpan<byte>.Empty), b.Bytes([]));
        Assert.NotEqual(a.String(ReadOnlySpan<byte>.Empty), b.Bytes([]));
    }

    [Fact]
    public void StringBytesAreCopiedNotAliased()
    {
        ScalarStore store = new();
        byte[] source = [1, 2, 3];
        ScalarValue v = store.Bytes(source);
        source[0] = 99;

        bool unchanged = v.AsBytes.SequenceEqual([(byte)1, (byte)2, (byte)3]);
        Assert.True(unchanged);
    }

    [Fact]
    public void ManyBlobsSurviveArrayGrowth()
    {
        // The byte array reallocates as it grows; every previously stored value must still read
        // back its own bytes.
        ScalarStore store = new(4);
        ScalarValue[] values = new ScalarValue[500];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = store.String("value_" + i.ToString(CultureInfo.InvariantCulture));
        }

        for (int i = 0; i < values.Length; i++)
        {
            Assert.Equal(values[i], store.String("value_" + i.ToString(CultureInfo.InvariantCulture)));
        }
    }

    // ------------------------------------------------------------------ lists

    [Fact]
    public void ListsCompareElementwiseAcrossStores()
    {
        ScalarStore a = new();
        ScalarStore b = new();

        ScalarValue x = a.List([a.Int64(1), a.Int64(2), a.String("z"u8)]);
        ScalarValue y = b.List([b.Int64(1), b.Int64(2), b.String("z"u8)]);
        ScalarValue z = b.List([b.Int64(1), b.Int64(2)]);
        ScalarValue w = b.List([b.Int64(2), b.Int64(1), b.String("z"u8)]);

        Assert.Equal(x, y);
        Assert.Equal(x.GetHashCode(), y.GetHashCode());
        Assert.NotEqual(x, z);
        Assert.NotEqual(x, w);
        Assert.Equal(3, x.ListCount);
        Assert.Equal(2L, x.GetListElement(1).AsInt64);
    }

    [Fact]
    public void AnEmptyListIsNotANullAndNotAnAbsent()
    {
        ScalarStore store = new();
        ScalarValue empty = store.List(ReadOnlySpan<ScalarValue>.Empty);

        Assert.Equal(ScalarValueKind.List, empty.Kind);
        Assert.Equal(0, empty.ListCount);
        Assert.NotEqual(store.Null(), empty);
        Assert.NotEqual(store.Absent, empty);
    }

    [Fact]
    public void AnAbsentElementIsNotANullElement()
    {
        ScalarStore store = new();
        ScalarValue withAbsent = store.List([store.Absent]);
        ScalarValue withNull = store.List([store.Null()]);

        Assert.NotEqual(withAbsent, withNull);
        Assert.True(withAbsent.GetListElement(0).IsAbsent);
        Assert.True(withNull.GetListElement(0).IsNull);
    }

    [Fact]
    public void ListsNestUpToTheDepthCap()
    {
        ScalarStore store = new();
        ScalarValue v = store.Int64(1);
        for (int i = 1; i < VortexLimits.MaxDTypeDepth; i++)
        {
            v = store.List([v]);
        }

        ScalarValue atCap = v;
        Assert.Throws<VortexFormatException>(() => { store.List([atCap]); });
    }

    [Fact]
    public void DeeplyNestedListsStillCompareAcrossStores()
    {
        ScalarStore a = new();
        ScalarStore b = new();
        Assert.Equal(Nest(a), Nest(b));
        Assert.Equal(Nest(a).GetHashCode(), Nest(b).GetHashCode());

        static ScalarValue Nest(ScalarStore store)
        {
            ScalarValue v = store.String("leaf"u8);
            for (int i = 1; i < VortexLimits.MaxDTypeDepth; i++)
            {
                v = store.List([v, store.Int64(i)]);
            }

            return v;
        }
    }

    [Fact]
    public void ListRejectsAnElementFromAnotherStore()
    {
        ScalarStore a = new();
        ScalarStore b = new();
        ScalarValue theirs = b.Int64(1);
        Assert.Throws<ArgumentException>(() => { a.List([theirs]); });
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(2)]
    [InlineData(int.MaxValue)]
    [InlineData(int.MinValue)]
    public void ListElementIndexIsRangeChecked(int index)
    {
        ScalarStore store = new();
        ScalarValue list = store.List([store.Int64(1), store.Int64(2)]);
        Assert.Throws<ArgumentOutOfRangeException>(() => { list.GetListElement(index); });
    }

    // ------------------------------------------------------------------ union and variant

    [Fact]
    public void UnionCarriesTypeIdAndValue()
    {
        ScalarStore a = new();
        ScalarStore b = new();

        ScalarValue x = a.Union(3, a.Int64(7));
        ScalarValue y = b.Union(3, b.Int64(7));

        Assert.Equal(x, y);
        Assert.Equal(3u, x.UnionTypeId);
        Assert.Equal(7L, x.UnionValue.AsInt64);
        Assert.NotEqual(x, b.Union(4, b.Int64(7)));
        Assert.NotEqual(x, b.Union(3, b.Int64(8)));
        Assert.NotEqual(x, b.Union(3, b.Absent));
        Assert.True(b.Union(3, b.Absent).UnionValue.IsAbsent);
        Assert.Equal(uint.MaxValue, a.Union(uint.MaxValue, a.Null()).UnionTypeId);
    }

    [Fact]
    public void UnionRejectsAValueFromAnotherStore()
    {
        ScalarStore a = new();
        ScalarStore b = new();
        ScalarValue theirs = b.Int64(1);
        Assert.Throws<ArgumentException>(() => { a.Union(0, theirs); });
    }

    [Fact]
    public void VariantCarriesADTypeAndAValueAcrossArenas()
    {
        DTypeArena da = new();
        DTypeArena db = new();
        ScalarStore sa = new();
        ScalarStore sb = new();

        Scalar x = new(da.Primitive(PType.I32, Nullability.Nullable), sa.Int64(5));
        Scalar y = new(db.Primitive(PType.I32, Nullability.Nullable), sb.Int64(5));

        ScalarValue vx = sa.Variant(x);
        ScalarValue vy = sb.Variant(y);

        Assert.Equal(vx, vy);
        Assert.Equal(vx.GetHashCode(), vy.GetHashCode());
        Assert.Equal(x.DType, vx.AsVariant.DType);
        Assert.Equal(5L, vx.AsVariant.Value.AsInt64);

        // A different nested dtype makes it a different value even with the same payload.
        Scalar other = new(db.Primitive(PType.I64, Nullability.Nullable), sb.Int64(5));
        Assert.NotEqual(vx, sb.Variant(other));
    }

    [Fact]
    public void VariantRejectsAValueFromAnotherStore()
    {
        DTypeArena arena = new();
        ScalarStore a = new();
        ScalarStore b = new();
        Scalar foreign = new(arena.Bool(Nullability.NonNullable), b.Bool(true));
        Assert.Throws<ArgumentException>(() => { a.Variant(foreign); });
    }

    [Fact]
    public void VariantNestingIsDepthCapped()
    {
        DTypeArena arena = new();
        DType dtype = arena.Bool(Nullability.NonNullable);
        ScalarStore store = new();
        ScalarValue v = store.Bool(true);
        for (int i = 1; i < VortexLimits.MaxDTypeDepth; i++)
        {
            v = store.Variant(new Scalar(dtype, v));
        }

        Scalar atCap = new(dtype, v);
        Assert.Throws<VortexFormatException>(() => { store.Variant(atCap); });
    }

    // ------------------------------------------------------------------ accessor discipline

    [Fact]
    public void AccessorsRejectAKindMismatch()
    {
        ScalarStore store = new();
        ScalarValue i = store.Int64(1);
        ScalarValue s = store.String("x"u8);

        Assert.Throws<InvalidOperationException>(() => { _ = i.AsBool; });
        Assert.Throws<InvalidOperationException>(() => { _ = i.AsUInt64; });
        Assert.Throws<InvalidOperationException>(() => { _ = i.AsF16; });
        Assert.Throws<InvalidOperationException>(() => { _ = i.F16Bits; });
        Assert.Throws<InvalidOperationException>(() => { _ = i.AsF32; });
        Assert.Throws<InvalidOperationException>(() => { _ = i.AsF64; });
        Assert.Throws<InvalidOperationException>(() => { _ = i.AsBytes.Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = i.ListCount; });
        Assert.Throws<InvalidOperationException>(() => { i.GetListElement(0); });
        Assert.Throws<InvalidOperationException>(() => { _ = i.AsVariant; });
        Assert.Throws<InvalidOperationException>(() => { _ = i.UnionTypeId; });
        Assert.Throws<InvalidOperationException>(() => { _ = i.UnionValue; });
        Assert.Throws<InvalidOperationException>(() => { _ = s.AsInt64; });
    }

    [Fact]
    public void AbsentHasNoPayloadAtAll()
    {
        ScalarValue absent = default;
        Assert.Throws<InvalidOperationException>(() => { _ = absent.AsBool; });
        Assert.Throws<InvalidOperationException>(() => { _ = absent.AsInt64; });
        Assert.Throws<InvalidOperationException>(() => { _ = absent.AsBytes.Length; });
        Assert.Throws<InvalidOperationException>(() => { _ = absent.ListCount; });
        Assert.Throws<InvalidOperationException>(() => { _ = absent.AsVariant; });
    }

    [Fact]
    public void NullHasNoPayloadEither()
    {
        ScalarStore store = new();
        ScalarValue nul = store.Null();
        Assert.Throws<InvalidOperationException>(() => { _ = nul.AsBool; });
        Assert.Throws<InvalidOperationException>(() => { _ = nul.AsBytes.Length; });
    }

    [Fact]
    public void ClearResetsTheStore()
    {
        ScalarStore store = new();
        store.List([store.Int64(1), store.String("x"u8)]);
        Assert.True(store.NodeCount > 0);

        store.Clear();

        Assert.Equal(0, store.NodeCount);
        Assert.Equal(ScalarValueKind.Bool, store.Bool(true).Kind);
    }

    [Fact]
    public void AHandleHeldAcrossClearIsRefused()
    {
        ScalarStore store = new();
        ScalarValue stale = store.Int64(7);

        store.Clear();

        // Refilled to the same slot with a different kind: without the generation the stale handle
        // would read the string and AsInt64 would hand back its offset and length as an integer.
        ScalarValue fresh = store.String("x"u8);

        Assert.Throws<InvalidOperationException>(() => { _ = stale.Kind; });
        Assert.Throws<InvalidOperationException>(() => { _ = stale.AsInt64; });
        Assert.Throws<InvalidOperationException>(() => { _ = stale.GetHashCode(); });
        Assert.Throws<InvalidOperationException>(() => { _ = stale.Equals(fresh); });
        Assert.Throws<InvalidOperationException>(() => { _ = fresh.Equals(stale); });
        Assert.Throws<InvalidOperationException>(() => stale.ToString());
        Assert.Equal(ScalarValueKind.String, fresh.Kind);
    }

    [Fact]
    public void NegativeCapacityIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => { _ = new ScalarStore(-1); });

    // ------------------------------------------------------------------ rendering

    [Fact]
    public void ToStringIsReadableAndCultureInvariant()
    {
        CultureInfo hostile = (CultureInfo)CultureInfo.InvariantCulture.Clone();
        hostile.NumberFormat.NegativeSign = "!";
        hostile.NumberFormat.NumberDecimalSeparator = ",";

        CultureInfo previous = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = hostile;
            ScalarStore store = new();
            DTypeArena arena = new();

            Assert.Equal("absent", store.Absent.ToString());
            Assert.Equal("null", store.Null().ToString());
            Assert.Equal("true", store.Bool(true).ToString());
            Assert.Equal("false", store.Bool(false).ToString());
            Assert.Equal("-42", store.Int64(-42).ToString());
            Assert.Equal("18446744073709551615", store.UInt64(ulong.MaxValue).ToString());
            Assert.Equal("-1.5", store.F64(-1.5).ToString());
            Assert.Equal("-1.5", store.F32(-1.5f).ToString());

            // F16 is the kind most likely to lose its provider, since AsF16 has to go through
            // BitConverter.UInt16BitsToHalf first. Without CultureInfo.InvariantCulture this
            // renders "!1,5" under the hostile culture above.
            Assert.Equal("-1.5", store.F16((Half)(-1.5f)).ToString());
            Assert.Equal("\"hi\"", store.String("hi"u8).ToString());
            Assert.Equal("0x00ff10", store.Bytes([0x00, 0xFF, 0x10]).ToString());
            Assert.Equal("[1, null]", store.List([store.Int64(1), store.Null()]).ToString());
            Assert.Equal("union(3, 1)", store.Union(3, store.Int64(1)).ToString());

            DType i32 = arena.Primitive(PType.I32, Nullability.Nullable);
            Assert.Equal("variant(i32? = 7)", store.Variant(new Scalar(i32, store.Int64(7))).ToString());
            Assert.Equal("i32? = 7", new Scalar(i32, store.Int64(7)).ToString());
            Assert.Equal("i32? = absent", new Scalar(i32, default).ToString());
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = previous;
        }
    }

    /// <summary>
    /// The same unbounded-rental hazard as a huge field name, reached the way a real file reaches
    /// it: a String scalar's blob comes straight from the file (a min/max statistic, a constant),
    /// and <c>ToString</c> is on the diagnostic path.
    /// </summary>
    [Fact]
    public void AHugeStringScalarIsRenderedWithoutAllocatingItsLength()
    {
        // A different size class from DTypeFormatTests' huge-name case on purpose: both would
        // otherwise share an ArrayPool<char> bucket, and whichever ran first would leave a rented
        // array behind that hides the other's allocation.
        const int Bytes = 1024 * 1024;

        byte[] blob = new byte[Bytes];
        Array.Fill(blob, (byte)'x');

        ScalarStore store = new();
        ScalarValue value = store.String(blob);

        // Warm the render path on a small value only - see the note in DTypeFormatTests.
        ScalarValue warm = store.String("x"u8);
        for (int i = 0; i < 20; i++)
        {
            _ = warm.ToString();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        string text = value.ToString();
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.True(
            text.Length <= DTypeFormatter.MaxRenderedLength + 3,
            $"length was {text.Length.ToString(CultureInfo.InvariantCulture)}");
        Assert.True(
            allocated < 64 * 1024,
            $"rendering a {Bytes.ToString(CultureInfo.InvariantCulture)}-byte string scalar " +
            $"allocated {allocated.ToString(CultureInfo.InvariantCulture)} bytes");
    }

    // ------------------------------------------------------------------ Scalar

    [Fact]
    public void ScalarPairsADTypeWithAValue()
    {
        DTypeArena arena = new();
        ScalarStore store = new();
        DType i64 = arena.Primitive(PType.I64, Nullability.Nullable);

        Scalar typed = new(i64, store.Int64(3));
        Scalar nul = new(i64, store.Null());
        Scalar absent = new(i64, default);

        Assert.Equal(i64, typed.DType);
        Assert.Equal(3L, typed.Value.AsInt64);
        Assert.False(typed.IsNull);
        Assert.True(nul.IsNull);
        // Absence is not nullity: a statistic that is missing asserts nothing.
        Assert.False(absent.IsNull);
        Assert.True(absent.Value.IsAbsent);
    }

    [Fact]
    public void ScalarEqualityComparesBothHalvesAcrossArenas()
    {
        DTypeArena da = new();
        DTypeArena db = new();
        ScalarStore sa = new();
        ScalarStore sb = new();

        Scalar x = new(da.Utf8(Nullability.Nullable), sa.String("v"u8));
        Scalar y = new(db.Utf8(Nullability.Nullable), sb.String("v"u8));
        Scalar differentType = new(db.Utf8(Nullability.NonNullable), sb.String("v"u8));
        Scalar differentValue = new(db.Utf8(Nullability.Nullable), sb.String("w"u8));

        Assert.Equal(x, y);
        Assert.Equal(x.GetHashCode(), y.GetHashCode());
        Assert.NotEqual(x, differentType);
        Assert.NotEqual(x, differentValue);
        Assert.False(x.Equals("not a scalar"));
    }

    /// <summary>
    /// A list that holds the same element twice is a DAG: 40 levels of <c>List([v, v])</c> is a
    /// 41-node value with 2^40 root-to-leaf paths. The store does not deduplicate, so even two
    /// roots built in the SAME store take the full walk — the early-out is
    /// <c>ReferenceEquals(a, b) &amp;&amp; ai == bi</c> and these are different indices. A
    /// comparison that walks per path never returns; the depth cap bounds the stack, not the work.
    /// </summary>
    [Fact]
    public void EqualityOnASharedElementDagTerminates()
    {
        const int Levels = 40;

        ScalarStore store = new();
        ScalarValue x = BuildDag(store, Levels, 1);
        ScalarValue y = BuildDag(store, Levels, 1);
        Assert.True(x.Equals(y));
        Assert.Equal(x.GetHashCode(), y.GetHashCode());

        // Across stores too, and a buried mismatch still comes back false.
        ScalarStore other = new();
        Assert.True(x.Equals(BuildDag(other, Levels, 1)));
        Assert.False(x.Equals(BuildDag(other, Levels, 2)));

        static ScalarValue BuildDag(ScalarStore store, int levels, long leaf)
        {
            ScalarValue v = store.Int64(leaf);
            for (int i = 0; i < levels; i++)
            {
                v = store.List([v, v]);
            }

            return v;
        }
    }

    [Fact]
    public void ScalarAcceptsADefaultDType()
    {
        // A codec reading a malformed Scalar message decides for itself whether a missing dtype is
        // fatal; the model must be able to hold what was read.
        ScalarStore store = new();
        Scalar s = new(default, store.Int64(1));
        Assert.True(s.DType.IsDefault);
        Assert.Equal("<default> = 1", s.ToString());
    }
}
