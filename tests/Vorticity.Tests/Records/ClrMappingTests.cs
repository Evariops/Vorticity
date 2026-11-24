using System;
using System.IO;
using System.Threading.Tasks;
using Xunit;

namespace Vorticity.Tests.Records;

/// <summary>How a .NET member is checked against a column, and what the checks keep.</summary>
public sealed partial class ClrMappingTests
{
    [VortexRecord]
    public partial record struct NullableLongs(ReadOnlyMemory<long?> Values);

    [VortexRecord]
    public partial record struct PlainLongs(ReadOnlyMemory<long> Values);

    [VortexRecord]
    public partial record struct DayValue(int Day, long Value);

    [VortexRecord]
    public partial record struct Priced(int Id, ReadOnlyMemory<decimal> Prices);

    /// <summary>
    /// A decimal read from a column of 28 digits whose file holds a value past the 96 bits a decimal holds is a format
    /// error: read as its low 96 bits, it was a wrong decimal without a word.
    /// </summary>
    [Fact]
    public void ADecimalPastNinetySixBitsIsAFormatError()
    {
        VortexType column = VortexType.Decimal(28, 2);
        Assert.Equal(1.5m, LiteralValues.Decimal(150, column));
        Assert.Equal(-1.5m, LiteralValues.Decimal(-150, column));
        Assert.Throws<VortexFormatException>(() => LiteralValues.Decimal(Int128.One << 96, column));
        Assert.Throws<VortexFormatException>(() => LiteralValues.Decimal(Int128.MinValue, column));
    }

    /// <summary>
    /// A list of decimals is filtered by Contains at its elements' scale, as a comparison is: 1.5 finds the rows that hold
    /// it, a value its scale holds that no row holds finds none, and one finer than its scale finds none without a word.
    /// It threw that a list is filtered by Contains.
    /// </summary>
    [Fact]
    public async Task AListOfDecimalsIsFilteredByContains()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "clr-mapping");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"prices-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Priced>(path))
        {
            await writer.WriteAsync<Priced>(
                [new Priced(1, new[] { 1.5m, 2.25m }), new Priced(2, new[] { 3m }), new Priced(3, new[] { 1.5m })],
                TestContext.Current.CancellationToken);
            await writer.CompleteAsync(TestContext.Current.CancellationToken);
        }

        await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
        async Task<int[]> Ids(Func<Probe<Priced>, Predicate> filter)
        {
            System.Collections.Generic.List<int> ids = [];
            await foreach (Priced row in file.Scan<Priced>().Where(filter).ToRecordsAsync(TestContext.Current.CancellationToken))
            {
                ids.Add(row.Id);
            }

            return [.. ids];
        }

        int[] found = await Ids(r => r.Prices.Contains(1.5m));
        Assert.Equal(new[] { 1, 3 }, found);
        Assert.Empty(await Ids(r => r.Prices.Contains(1.555m)));
        Assert.Empty(await Ids(r => r.Prices.Contains(1.50000000001m)));
    }

    /// <summary>
    /// A list of nullable elements is refused to a member whose elements are not, as a nullable column is to a member
    /// that is not: the elements of 1, null, 3 read as 1, 0, 3, without a word. A member of nullable elements reads them.
    /// </summary>
    [Fact]
    public async Task AListOfNullableElementsIsRefusedToAMemberOfPlainOnes()
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "clr-mapping");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"lists-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<NullableLongs>(path))
        {
            await writer.WriteAsync<NullableLongs>([new NullableLongs(new long?[] { 1, null, 3 })], TestContext.Current.CancellationToken);
            await writer.CompleteAsync(TestContext.Current.CancellationToken);
        }

        await using VortexFile file = await VortexFile.OpenAsync(path, TestContext.Current.CancellationToken);
        VortexSchemaException refused = await Assert.ThrowsAsync<VortexSchemaException>(async () =>
        {
            await foreach (PlainLongs row in file.Scan<PlainLongs>().ToRecordsAsync(TestContext.Current.CancellationToken))
            {
                _ = row;
            }
        });
        Assert.Contains("declare the elements nullable", refused.Message);

        await foreach (NullableLongs row in file.Scan<NullableLongs>().ToRecordsAsync(TestContext.Current.CancellationToken))
        {
            Assert.Equal(new long?[] { 1, null, 3 }, row.Values.ToArray());
        }
    }

    /// <summary>
    /// Two schemas of the same fields share the binding of a record, made once, as the files of a directory opened one
    /// after the other do; under two registries of extensions, each keeps its own, which the other does not replace.
    /// </summary>
    [Fact]
    public void TwoSchemasOfTheSameFieldsShareABinding()
    {
        VortexSchema first = VortexSchema.Create([new VortexField("Day", VortexType.Int32), new VortexField("Value", VortexType.Int64)]);
        VortexSchema second = VortexSchema.Create([new VortexField("Day", VortexType.Int32), new VortexField("Value", VortexType.Int64)]);
        Assert.NotSame(first, second);
        if (!ReferenceEquals(first.Root, second.Root))
        {
            // The types kept for reuse are full in this process: equal roots are two types, and the binding is the schema's.
            Assert.True(VortexType.InternedBytes > (8L << 20) - 4096, "equal roots were not kept as one type");
            return;
        }

        Assert.Same(RecordBinding.For<DayValue>(first, null), RecordBinding.For<DayValue>(second, null));

        VortexExtensionRegistry one = new VortexExtensionRegistry();
        VortexExtensionRegistry other = new VortexExtensionRegistry();
        RecordBinding underOne = RecordBinding.For<DayValue>(first, one);
        RecordBinding underOther = RecordBinding.For<DayValue>(first, other);
        Assert.NotSame(underOne, underOther);
        Assert.Same(underOne, RecordBinding.For<DayValue>(second, one));
        Assert.Same(underOther, RecordBinding.For<DayValue>(second, other));
        Assert.Same(underOne, RecordBinding.ForType<DayValue>(first.Root, one));
    }

    /// <summary>
    /// The zones a file names are kept with their answers within a budget: ids of a thousand characters the host does
    /// not know, past what the budget holds, are answered and not kept, and a zone the host knows still resolves.
    /// </summary>
    [Fact]
    public void TheZonesKeptStayWithinTheirBudget()
    {
        for (int i = 0; i < 300; i++)
        {
            Assert.False(TimeZones.TryResolve($"Nowhere/{i:D4}/" + new string('x', 1_000), out _));
        }

        Assert.True(TimeZones.KeptBytes <= 256 * 1024, $"{TimeZones.KeptBytes} bytes of zones kept");
        Assert.True(TimeZones.TryResolve("UTC", out TimeZoneInfo utc));
        Assert.Same(TimeZoneInfo.Utc, utc);
    }

    /// <summary>
    /// A timestamp column is checked against a <see cref="DateTime"/> or a <see cref="DateTimeOffset"/> member at every
    /// batch: the check reads the zone the type resolved once, and allocates nothing.
    /// </summary>
    [Fact]
    public void CheckingATimestampColumnAtEveryBatchAllocatesNothing()
    {
        VortexType zoned = VortexType.Timestamp(TimeUnit.Microseconds, "UTC");
        VortexType naive = VortexType.Timestamp(TimeUnit.Microseconds);
        ClrFit.RequireColumn<DateTimeOffset>(zoned, "at", null);
        ClrFit.RequireColumn<DateTime>(naive, "at", null);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int batch = 0; batch < 100; batch++)
        {
            ClrFit.RequireColumn<DateTimeOffset>(zoned, "at", null);
            ClrFit.RequireColumn<DateTime>(zoned, "at", null);
            ClrFit.RequireColumn<DateTime>(naive, "at", null);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>A zone the host does not know is refused to a <see cref="DateTimeOffset"/> member, and a zoned column to a <see cref="DateTime"/> one.</summary>
    [Fact]
    public void AZoneTheHostDoesNotKnowIsRefusedToADateTimeOffset()
    {
        VortexType unknown = VortexType.Timestamp(TimeUnit.Microseconds, "Nowhere/Atlantis");
        Assert.Equal(ZoneState.Unresolved, unknown.Zone);
        Assert.Throws<VortexSchemaException>(() => ClrFit.RequireColumn<DateTimeOffset>(unknown, "at", null));
        Assert.Throws<VortexSchemaException>(() => ClrFit.RequireColumn<DateTime>(VortexType.Timestamp(TimeUnit.Microseconds, "Europe/Paris"), "at", null));
        Assert.Equal(ZoneState.Resolved, VortexType.Timestamp(TimeUnit.Microseconds, "Europe/Paris").Zone);
    }
}
