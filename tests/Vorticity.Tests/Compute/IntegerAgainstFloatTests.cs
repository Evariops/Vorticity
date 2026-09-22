// An integer column compared against a float literal, at the place a comparison made in double
// would stop being exact.
//
// `x < 3.5` on an i64 column is an ordinary predicate and is answered rather than refused, in the
// column's domain: the literal is placed on the integers -- the rows below it are the rows below
// its ceiling, the rows above it the rows above its floor, and none equals a float that is not
// integral -- and the rows are compared as integers. Widening each value to double instead would be
// exact below 2^53 and off by one above it.
//
// 2^53 + 1 is the first integer a double cannot hold. It sits exactly halfway between 2^53 and
// 2^53 + 2, so it rounds to even and becomes the first of them: a comparison made in double would
// keep both under an equality on 2^53, and this one keeps the one integer the double is.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Compute;

public sealed class IntegerAgainstFloatTests
{
    private const string Field = "n";

    /// <summary>2^53, the last integer whose successor a double cannot hold.</summary>
    private const long Boundary = 9_007_199_254_740_992L;

    private static readonly long[] Values =
    [
        0L,
        3L,
        4L,
        Boundary - 1,
        Boundary,
        Boundary + 1,
        Boundary + 2,
    ];

    /// <summary>Below the boundary the answer is the arithmetic one, row for row.</summary>
    [Fact]
    public async Task BelowTheBoundaryAFloatLiteralIsExact()
    {
        long[] kept = await Matching(Less(3.5));
        Assert.Equal([0L, 3L], kept);
    }

    /// <summary>
    /// At the boundary two integers share one double, and an equality on that double keeps the one
    /// integer the double is, not the one that rounds to it.
    /// </summary>
    [Fact]
    public async Task AtTheBoundaryAnEqualityKeepsTheOneIntegerTheDoubleIs()
    {
        long[] kept = await Matching(Equal(Boundary));
        Assert.Equal([Boundary], kept);

        // And the order relations tell the two apart, which a comparison in double could not.
        long[] atOrBelow = await Matching(LessOrEqual(Boundary));
        long[] above = await Matching(Greater(Boundary));
        Assert.Equal([0L, 3L, 4L, Boundary - 1, Boundary], atOrBelow);
        Assert.Equal([Boundary + 1, Boundary + 2], above);
    }

    /// <summary>
    /// And the integer past the pair is a double of its own, so it is not swept in with them.
    /// </summary>
    [Fact]
    public async Task PastTheBoundaryTheNextRepresentableIntegerStandsAlone()
    {
        long[] kept = await Matching(Equal(Boundary + 2));
        Assert.Equal([Boundary + 2], kept);
    }

    /// <summary>No integer equals a float that is not integral, and every integer differs from it.</summary>
    [Fact]
    public async Task NoIntegerEqualsAFractionalLiteral()
    {
        long[] equal = await Matching(Expr.Eq(Expr.Field(Field), Expr.Literal(FilterLiteral.From(3.5))));
        long[] unequal = await Matching(Expr.Ne(Expr.Field(Field), Expr.Literal(FilterLiteral.From(3.5))));
        long[] below = await Matching(Expr.Le(Expr.Field(Field), Expr.Literal(FilterLiteral.From(3.5))));
        long[] above = await Matching(Expr.Ge(Expr.Field(Field), Expr.Literal(FilterLiteral.From(3.5))));
        Assert.Empty(equal);
        Assert.Equal(Values, unequal);
        Assert.Equal([0L, 3L], below);
        Assert.Equal([4L, Boundary - 1, Boundary, Boundary + 1, Boundary + 2], above);
    }

    /// <summary>A literal beyond every i64 decides every row alike, and a NaN relates to none.</summary>
    [Fact]
    public async Task ALiteralBeyondTheColumnDecidesEveryRow()
    {
        long[] belowHuge = await Matching(Less(1e30));
        long[] belowTiny = await Matching(Less(-1e30));
        long[] aboveTiny = await Matching(Greater(-1e30));
        long[] belowNaN = await Matching(Less(double.NaN));
        long[] unequalNaN = await Matching(Expr.Ne(Expr.Field(Field), Expr.Literal(FilterLiteral.From(double.NaN))));
        Assert.Equal(Values, belowHuge);
        Assert.Empty(belowTiny);
        Assert.Equal(Values, aboveTiny);
        Assert.Empty(belowNaN);
        Assert.Equal(Values, unequalNaN);
    }

    /// <summary><c>n &lt; wanted</c>, the literal a double.</summary>
    private static VortexExpr Less(double wanted) =>
        Expr.Lt(Expr.Field(Field), Expr.Literal(FilterLiteral.From(wanted)));

    /// <summary><c>n &lt;= wanted</c>, the literal the double <paramref name="wanted"/> widens to.</summary>
    private static VortexExpr LessOrEqual(long wanted) =>
        Expr.Le(Expr.Field(Field), Expr.Literal(FilterLiteral.From((double)wanted)));

    /// <summary><c>n &gt; wanted</c>, the literal a double.</summary>
    private static VortexExpr Greater(double wanted) =>
        Expr.Gt(Expr.Field(Field), Expr.Literal(FilterLiteral.From(wanted)));

    /// <summary><c>n = wanted</c>, the literal the double <paramref name="wanted"/> widens to.</summary>
    private static VortexExpr Equal(long wanted) =>
        Expr.Eq(Expr.Field(Field), Expr.Literal(FilterLiteral.From((double)wanted)));

    /// <summary>The values a scan keeps under <paramref name="filter"/>, in file order.</summary>
    private static async Task<long[]> Matching(VortexExpr filter)
    {
        string path = Write();
        try
        {
            await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
            List<long> kept = [];
            await foreach (RecordBatch batch in file.ScanBuilder().Where(filter).ExecuteAsync()
                .WithCancellation(CancellationToken.None))
            {
                PrimitiveColumn<long> column = batch.Column(0).AsPrimitive<long>();
                for (int row = 0; row < batch.RowCount; row++)
                {
                    kept.Add(column[row]);
                }
            }

            return [.. kept];
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>A one-column i64 file holding <see cref="Values"/>.</summary>
    private static string Write()
    {
        DTypeArena types = new DTypeArena();
        DType i64 = types.Primitive(PType.I64, Nullability.NonNullable);
        DType schema = types.Struct([Field], [i64], Nullability.NonNullable);

        CanonicalArena arena = new CanonicalArena();
        VortexBuffer buffer = arena.Allocate(Values.Length * sizeof(long), 8, out Span<byte> bytes);
        System.Runtime.InteropServices.MemoryMarshal.Cast<long, byte>(Values).CopyTo(bytes);

        int column = arena.AddPrimitive(
            i64, Values.Length, Validity.NonNullable, PType.I64, buffer);
        int root = arena.AddStruct(schema, Values.Length, Validity.NonNullable, [column]);

        string path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-intfloat-{Guid.NewGuid():N}.vortex");
        WriteAsync(path, schema, arena, root).GetAwaiter().GetResult();
        return path;
    }

    private static async Task WriteAsync(string path, DType schema, CanonicalArena arena, int root)
    {
        // No index: an exact index answers an equality before the kernel is reached, and the
        // kernel is what these three assert.
        VortexWriteOptions options = new VortexWriteOptions
        {
            WritePolicy = Vorticity.Indexes.WritePolicy.None,
        };

        await using VortexFileWriter writer = VortexFileWriter.Create(path, schema, options);
        using RecordBatch batch = new RecordBatch(arena, root, 0);
        await writer.WriteAsync(batch, CancellationToken.None);
        await writer.CompleteAsync(CancellationToken.None);
    }
}
