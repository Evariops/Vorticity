using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

/// <summary>
/// A variant value read from a file is held to what its bytes can hold before anything is sized from
/// it or walked: a field id to its metadata's keys, a value to a header byte at least, and its nodes
/// to its bytes, so that fields that name the same bytes over and over are refused rather than
/// unfolded into exponentially many nodes.
/// </summary>
public sealed class VariantBoundsTests
{
    private static readonly byte[] FourKeys = VariantEncoding.Metadata([[(byte)'a'], [(byte)'b'], [(byte)'c'], [(byte)'d']]);

    [Fact]
    public void AFieldIdPastTheKeysIsRefusedBeforeTheCensusSizesAnythingFromIt()
    {
        // An object of one field, its id 500 000 000 in four bytes, its value a null.
        byte[] value = new byte[1 + 1 + 4 + 2 + 1];
        value[0] = (3 << 4) | 2;
        value[1] = 1;
        BinaryPrimitives.WriteInt32LittleEndian(value.AsSpan(2), 500_000_000);
        value[6] = 0;
        value[7] = 1;
        value[8] = 0;
        PathCensus census = new();
        VortexFormatException refused = Assert.Throws<VortexFormatException>(() => census.Add(FourKeys, value));
        Assert.Contains("field id 500000000", refused.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyValueIsAFormatError()
    {
        PathCensus census = new();
        Assert.Throws<VortexFormatException>(() => census.Add(FourKeys, []));
    }

    [Fact]
    public void AValueWhoseFieldsShareTheirBytesIsRefusedInLinearTime()
    {
        // 31 objects deep, each of four fields that all name the one object below: 500 bytes that unfold into 4^31 nodes.
        byte[] overlapping = Overlapping(depth: 31);
        Assert.True(overlapping.Length < 600);
        Assert.Throws<VortexFormatException>(() => Variant.FromBytes(FourKeys, overlapping));
        Assert.Throws<VortexFormatException>(() => new PathCensus().Add(FourKeys, overlapping));
        Assert.Throws<VortexFormatException>(() => VariantEncoding.Json(FourKeys, overlapping));
        Assert.Throws<VortexFormatException>(() => VariantEncoding.ValueHash(FourKeys, overlapping));
        Assert.Throws<VortexFormatException>(() => VariantEncoding.ValueEquals(FourKeys, overlapping, FourKeys, overlapping));
        Assert.Throws<VortexFormatException>(() => VariantEncoding.CollectKeys(FourKeys, overlapping, new SortedSet<byte[]>(VariantEncoding.KeyOrder)));
        Assert.Throws<VortexFormatException>(() => VariantEncoding.Rewrite(FourKeys, overlapping, [[(byte)'a'], [(byte)'b'], [(byte)'c'], [(byte)'d']]));

        // The same shape with its fields apart reads.
        Assert.Equal(VariantKind.Object, Variant.FromBytes(FourKeys, Apart(depth: 3)).Kind);
    }

    /// <summary>
    /// Values of the right type past what .NET's types hold read as null, as <c>As…</c> promises for a value it cannot give,
    /// and print as JSON in the form they are stored; a decimal is given exactly or not at all.
    /// </summary>
    [Fact]
    public void ALegalValuePastDotNetsTypesReadsAsNullAndPrints()
    {
        byte[] none = VariantEncoding.Metadata([]);
        Variant late = Variant.FromBytes(none, VariantEncoding.Timestamp(long.MaxValue, utc: true));
        Assert.Null(late.AsTimestamp());
        Assert.Equal(long.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), late.ToJson());
        Assert.Null(Variant.FromBytes(none, VariantEncoding.Timestamp(long.MinValue, utc: false)).AsLocalTimestamp());

        Variant far = Variant.FromBytes(none, VariantEncoding.Date(int.MaxValue));
        Assert.Null(far.AsDate());
        Assert.Equal(int.MaxValue.ToString(System.Globalization.CultureInfo.InvariantCulture), far.ToJson());
        Assert.Null(Variant.FromBytes(none, VariantEncoding.Time(-1)).AsTime());

        Variant least = Variant.FromBytes(none, VariantEncoding.Decimal(Int128.MinValue, 0));
        Assert.Null(least.AsDecimal());
        Assert.Equal("-170141183460469231731687303715884105728", least.ToJson());

        // A scale past 28 that trailing zeros make up is exact at 28; one that holds a digit there is not, and is no rounded value.
        Assert.Equal(1.5m, Variant.FromBytes(none, VariantEncoding.Decimal(15 * Int128.Parse("100000000000000000000000000000", System.Globalization.CultureInfo.InvariantCulture), 30)).AsDecimal());
        Assert.Null(Variant.FromBytes(none, VariantEncoding.Decimal(1, 30)).AsDecimal());
    }

    [Fact]
    public void ACensusSpansAreCountedByItsAccountUntilReleased()
    {
        // A key every other row, each row a span of its own past a gap of one row: the spans grow with the rows.
        TestAccount account = new();
        PathCensus census = new(stringBoundBytes: 0, groupRows: 1) { Account = account };
        byte[] metadata = VariantEncoding.Metadata([[(byte)'a'], [(byte)'b']]);
        byte[] a = VariantEncoding.Object([(0, VariantEncoding.Integer(1))]);
        byte[] b = VariantEncoding.Object([(1, VariantEncoding.Integer(2))]);
        for (int row = 0; row < 20_000; row++)
        {
            census.Add(metadata, row % 2 == 0 ? a : b);
        }

        Assert.True(account.Held >= 10_000 * 2 * sizeof(long), $"The census's account holds {account.Held} bytes for 10 000 spans.");
        census.Release();
        Assert.Equal(0, account.Held);
    }

    [Fact]
    public void AnInstantBefore1970RoundsDownToItsMicrosecond()
    {
        // Half a microsecond past 23:59:59 on the last day of 1969: down to 23:59:59.000000, as the temporal columns round.
        DateTime instant = new DateTime(1969, 12, 31, 23, 59, 59, DateTimeKind.Utc).AddTicks(5);
        Assert.Equal(new DateTime(1969, 12, 31, 23, 59, 59, DateTimeKind.Utc), Variant.From(instant).AsTimestamp()!.Value.UtcDateTime);
        Assert.Equal(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(10), Variant.From(new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(15)).AsTimestamp()!.Value.UtcDateTime);
    }

    /// <summary>Objects <paramref name="depth"/> deep, each of four fields at offset 0, all of them its child.</summary>
    private static byte[] Overlapping(int depth)
    {
        byte[] child = [0];
        for (int level = 0; level < depth; level++)
        {
            // Two-byte offsets, one-byte ids: the value header's offset size is 2.
            byte[] parent = new byte[1 + 1 + 4 + (5 * 2) + child.Length];
            parent[0] = (1 << 2) | 2;
            parent[1] = 4;
            for (int f = 0; f < 4; f++)
            {
                parent[2 + f] = (byte)f;
            }

            BinaryPrimitives.WriteUInt16LittleEndian(parent.AsSpan(6 + (4 * 2)), (ushort)child.Length);
            child.CopyTo(parent, 1 + 1 + 4 + (5 * 2));
            child = parent;
        }

        return child;
    }

    /// <summary>Objects <paramref name="depth"/> deep, each of four fields, each of them a copy of its child laid out apart.</summary>
    private static byte[] Apart(int depth)
    {
        byte[] child = [0];
        for (int level = 0; level < depth; level++)
        {
            List<(int Id, byte[] Value)> fields = [(0, child), (1, child), (2, child), (3, child)];
            child = VariantEncoding.Object(fields);
        }

        return child;
    }
}
