// The constant form of PERF-AUDIT-v2.md Z1b, behind its internal switch.
//
// The point of a switch is that the two forms coexist in ONE process, so what has to be tested is
// not "the new form works" but "the two forms agree". Every assertion below reads the same corpus
// file twice, once each way, and compares what a caller sees -- the only thing a change of
// representation is allowed to leave alone.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class ConstantFormTests
{
    private static VortexOpenOptions With(bool constantForm) =>
        new VortexOpenOptions { Read = new VortexReadOptions { ConstantForm = constantForm } };

    /// <summary>The switch, and nothing else, decides which kind the canonicalizer produces.</summary>
    [Fact]
    public async Task TheSwitchDecidesWhichKindTheCanonicalizerProduces()
    {
        Vorticity.Tests.Scan.Decoders.EnsureRegistered();

        (CanonicalKind tiled, int tiledRows) = await RootOf("encodings/constant", false);
        (CanonicalKind constant, int constantRows) = await RootOf("encodings/constant", true);

        Assert.Equal(CanonicalKind.Primitive, tiled);
        Assert.Equal(CanonicalKind.Constant, constant);

        // The row count is the half that must NOT change: the form holds one element, it still
        // stands for every row.
        Assert.Equal(tiledRows, constantRows);
        Assert.True(tiledRows > 0, "the corpus entry produced no rows");
    }

    /// <summary>Both forms hand the caller the same values, row for row.</summary>
    /// <remarks>
    /// THE ASSERTION THAT WOULD CATCH A WRONG WINDOW, and nothing else would: a constant resolved to
    /// the wrong element, or to a stale one, differs here while the kind, the row count, the dtype
    /// and the validity all still agree. The entries cover the row counts the corpus varies -- 0, 1,
    /// 1023, 1025 and 4096 -- so the empty case, the single row and the sizes that do not fall on a
    /// byte are all read.
    /// </remarks>
    [Theory]
    [InlineData("encodings/constant")]
    [InlineData("encodings/constant_r0")]
    [InlineData("encodings/constant_r1")]
    [InlineData("encodings/constant_r1023")]
    [InlineData("encodings/constant_r1025")]
    public async Task BothFormsReadTheSameValues(string entry)
    {
        Vorticity.Tests.Scan.Decoders.EnsureRegistered();
        Assert.Equal(await ValuesOf(entry, false), await ValuesOf(entry, true));
    }

    /// <summary>Writing a constant column back produces the same bytes either way.</summary>
    /// <remarks>
    /// The writer expands the element rather than emitting `vortex.constant` on the wire, so this is
    /// byte-exact by construction -- and this test is what makes "by construction" a measured fact.
    /// Emitting a constant on the wire would shrink the file, which is a different gain and a
    /// different point; folding it in here would move `WrittenSizeTests` under cover of a memory
    /// refactor.
    /// </remarks>
    [Fact]
    public async Task WritingBackIsByteIdenticalEitherWay()
    {
        Vorticity.Tests.Scan.Decoders.EnsureRegistered();
        Assert.Equal(await RewriteOf(false), await RewriteOf(true));
    }

    private static async Task<(CanonicalKind Kind, int Rows)> RootOf(string entry, bool constantForm)
    {
        await using VortexFile file = await VortexFile.OpenAsync(
            CorpusManifest.Get(entry).Path, With(constantForm), CancellationToken.None);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            return (batch.Root.Kind, batch.RowCount);
        }

        throw new InvalidOperationException($"{entry} produced no batches.");
    }

    private static async Task<List<string>> ValuesOf(string entry, bool constantForm)
    {
        List<string> values = [];
        await using VortexFile file = await VortexFile.OpenAsync(
            CorpusManifest.Get(entry).Path, With(constantForm), CancellationToken.None);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            VortexColumn column = batch.Root;
            for (int i = 0; i < batch.RowCount; i++)
            {
                values.Add(column.IsValid(i)
                    ? column.AsPrimitive<long>()[i].ToString(CultureInfo.InvariantCulture)
                    : "null");
            }
        }

        return values;
    }

    private static async Task<long> RewriteOf(bool constantForm)
    {
        string written = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-constant-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFile source = await VortexFile.OpenAsync(
                CorpusManifest.Get("encodings/constant").Path, With(constantForm), CancellationToken.None))
            await using (Vorticity.Writing.VortexFileWriter writer =
                Vorticity.Writing.VortexFileWriter.Create(written, source.Schema))
            {
                await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
            }

            return new System.IO.FileInfo(written).Length;
        }
        finally
        {
            if (System.IO.File.Exists(written))
            {
                System.IO.File.Delete(written);
            }
        }
    }
}
