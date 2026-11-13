// What a column's counts hold while an object is written, against what they keep once it ends: registers of the wide
// columns' distinct values, kept by an entry when its column's values hold AttachRows rows each on average, and sketches
// of the measures, kept when they hold SketchRows. An object whose rows are bounded knows early that its values cannot
// hold their share, and lets both go rather than build what its end would drop.
//
// THE ORACLE IS THE SAME COUNTS WITHOUT A BOUND: the counts and their sketches are the same bytes whether the rows are
// bounded or not, and only what the column holds on the way changes.
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class CountsRegistersTests
{
    private const int Part = 8_192;
    private const int Rows = 2 * Part;

    /// <summary>
    /// A column of 600 services over 16 384 rows, with a host column of 2 000 values and an amount: no service can hold
    /// the 2 048 rows that registers ask for, nor the 256 that sketches do. With the rows bounded, the column builds
    /// neither, where it held 1.2 MB of registers and 600 sketches until its end dropped them. Its counts are the same.
    /// </summary>
    [Fact]
    public void ValuesThatCannotHoldTheirShareBuildNeitherRegistersNorSketches()
    {
        Built bounded = Build(services: 600, rowBound: Rows);
        Built unbounded = Build(services: 600, rowBound: 0);

        Assert.Equal(0, bounded.MostRegisterBytes);
        Assert.False(bounded.SketchedPastTheFirstPart);
        Assert.True(unbounded.MostRegisterBytes >= 600L * ColumnSheet.Registers, $"{unbounded.MostRegisterBytes} bytes of registers");
        Assert.True(unbounded.SketchedPastTheFirstPart);
        Assert.Equal(unbounded.Counts, bounded.Counts);
        Assert.Null(bounded.Sketches);
        Assert.Null(unbounded.Sketches);
    }

    /// <summary>
    /// A column of 4 services over the same rows holds 4 096 rows a value: its registers and its sketches are built and
    /// kept whether the rows are bounded or not, the same bytes.
    /// </summary>
    [Fact]
    public void ValuesThatHoldTheirShareKeepTheirRegistersAndSketches()
    {
        Built bounded = Build(services: 4, rowBound: Rows);
        Built unbounded = Build(services: 4, rowBound: 0);

        Assert.True(bounded.MostRegisterBytes > 0);
        Assert.True(bounded.SketchedPastTheFirstPart);
        Assert.NotNull(bounded.Sketches);
        Assert.Equal(unbounded.Counts, bounded.Counts);
        Assert.Equal(unbounded.Sketches, bounded.Sketches);
    }

    /// <summary>
    /// An object bounded below the rows registers ask for of one value builds none from its first batch: a value of all
    /// its rows holds fewer than 2 048.
    /// </summary>
    [Fact]
    public void AnObjectOfFewerRowsThanOneValueAsksForBuildsNoRegisters()
    {
        const int Few = ColumnCountsBuilder.AttachRows - 1;
        Built bounded = Build(services: 1, rowBound: Few, rows: Few);
        Built unbounded = Build(services: 1, rowBound: 0, rows: Few);

        Assert.Equal(0, bounded.MostRegisterBytes);
        Assert.True(unbounded.MostRegisterBytes > 0);
        Assert.Equal(unbounded.Counts, bounded.Counts);
        Assert.Equal(unbounded.Sketches, bounded.Sketches);
    }

    private sealed record Built(byte[] Counts, byte[]? Sketches, long MostRegisterBytes, bool SketchedPastTheFirstPart);

    /// <summary>
    /// The counts of column <c>Service</c> of <paramref name="rows"/> rows, under a schema of the service, a host of 2 000
    /// values and an amount, fed in parts of 8 192 as a writer feeds them, the plan bounded at <paramref name="rowBound"/>:
    /// the counts, their sketches, the most bytes of registers the column held after a part, and whether it still built
    /// sketches after its last part, or its only one.
    /// </summary>
    private static Built Build(int services, long rowBound, int rows = Rows)
    {
        VortexSchema schema = VortexSchema.Create([
            new VortexField("Service", VortexType.Utf8),
            new VortexField("Host", VortexType.Utf8),
            new VortexField("Amount", VortexType.Int64)]);
        DTypeArena types = new DTypeArena();
        CountsPlan plan = CountsPlan.For(VortexTypes.ToDType(schema, types))!;
        plan.RowBound = rowBound;
        ColumnSheetBuilder service = new ColumnSheetBuilder();
        ColumnSheetBuilder host = new ColumnSheetBuilder();
        service.HashRows();
        host.HashRows();
        plan.Wide.Add(new WideColumn(0, service));
        plan.Wide.Add(new WideColumn(1, host));
        ColumnCountsBuilder counts = new ColumnCountsBuilder(service, plan, field: 0);
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        long most = 0;
        bool sketched = false;
        try
        {
            for (int start = 0; start < rows; start += Part)
            {
                int count = Math.Min(Part, rows - start);
                CanonicalArena arena = new CanonicalArena();
                int[] nodes =
                [
                    Strings(arena, utf8, start, count, row => $"svc-{row % services}"),
                    Strings(arena, utf8, start, count, row => $"host-{row % 2_000}"),
                    Integers(arena, i64, start, count),
                ];
                plan.Prepare(arena, nodes, count);
                service.Accumulate(arena, nodes[0], 0, count);
                host.Accumulate(arena, nodes[1], 0, count);
                counts.Accumulate(arena, nodes[0], 0, count);
                counts.Attach(count);
                most = Math.Max(most, counts.RegisterBytes);
                sketched = counts.Sketches;
            }

            counts.Settle();
            (byte[] bytes, byte[]? sketches, _, _) = counts.Finish()!.Value;
            return new Built(bytes, sketches, most, sketched);
        }
        finally
        {
            counts.Dispose();
            service.Dispose();
            host.Dispose();
        }
    }

    /// <summary>A text column of <paramref name="count"/> rows from <paramref name="start"/>, each value short enough to lie in its view.</summary>
    private static int Strings(CanonicalArena arena, DType dtype, int start, int count, Func<int, string> value)
    {
        VortexBuffer views = arena.Allocate(count * 16, 16, out Span<byte> viewBytes);
        for (int i = 0; i < count; i++)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(value(start + i));
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(view, bytes.Length);
            bytes.CopyTo(view[4..]);
        }

        return arena.AddVarBinView(dtype, count, Validity.NonNullable, views, default);
    }

    /// <summary>An integer column of <paramref name="count"/> rows from <paramref name="start"/>, every value its own.</summary>
    private static int Integers(CanonicalArena arena, DType dtype, int start, int count)
    {
        VortexBuffer buffer = arena.Allocate(count * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < count; i++)
        {
            values[i] = (start + i) * 7L;
        }

        return arena.AddPrimitive(dtype, count, Validity.NonNullable, PType.I64, buffer);
    }
}
