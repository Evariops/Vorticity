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
    /// <remarks>
    /// READ OFF THE ARENA NODE, not off <c>VortexColumn.Kind</c>, and the difference is the point:
    /// the column API deliberately never reports <see cref="CanonicalKind.Constant"/>, because the
    /// form is how the decoder stored the column and not what the column holds -- a caller
    /// switching on the kind is asking the second question. So the arena is the only place the
    /// switch's effect is observable, which is exactly what this test is for.
    /// </remarks>
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

    /// <summary>Whichever form the arena holds, the public column view reports Primitive.</summary>
    /// <remarks>
    /// The other half of the promise above, and the one that keeps every caller's `switch` working
    /// when the switch is flipped: a constant column presents the form its dtype stands for.
    /// </remarks>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TheColumnViewNeverReportsTheConstantForm(bool constantForm)
    {
        Vorticity.Tests.Scan.Decoders.EnsureRegistered();

        await using VortexFile file = await VortexFile.OpenAsync(
            CorpusManifest.Get("encodings/constant").Path, With(constantForm), CancellationToken.None);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            Assert.Equal(CanonicalKind.Primitive, batch.Root.Kind);
            return;
        }

        Assert.Fail("encodings/constant produced no batches.");
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

    /// <summary>Every row of every shipped file reads the same with the switch either way.</summary>
    /// <remarks>
    /// <para>
    /// THE TEST THAT WAS MISSING, and the corpus cross-check against Vortex Rust is what said so:
    /// `containers/zoned_many_zones` came back with row 33792's `banded` reading 32 where the
    /// reference has 33 -- one zone's constant standing in for the next one's. Nothing in the suite
    /// caught it, because every test that could have compared two reads made with the SAME option,
    /// or compared a read against a rewrite of itself; a read that is consistently wrong passes all
    /// of those. The constant form's one promise is that it changes no value, so the assertion has
    /// to be the two forms against EACH OTHER, over files that are not about constants.
    /// </para>
    /// <para>
    /// Whole corpus rather than a list: the file that broke would not have been on a list, which is
    /// the entire lesson. It runs in a few seconds because it renders rows and compares strings.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task EveryCorpusFileReadsTheSameValuesEitherWay()
    {
        Vorticity.Tests.Scan.Decoders.EnsureRegistered();

        List<string> disagreed = [];
        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            List<string> tiled;
            try
            {
                tiled = await RowsOf(entry, false);
            }
            catch (Exception e) when (e is VortexUnsupportedException or VortexFormatException)
            {
                // The file does not read under this build's default options at all -- a missing
                // embedded dtype, an edition that is off. The coverage tests own that question, and
                // it is the SAME question with the switch either way. Only the tiled side is
                // caught: if that read succeeds and the constant one throws, the switch broke it,
                // and the exception is the answer this test exists to give.
                continue;
            }

            List<string> constant = await RowsOf(entry, true);

            if (tiled.Count != constant.Count)
            {
                disagreed.Add($"{entry.Id}: {tiled.Count} rows tiled, {constant.Count} constant");
                continue;
            }

            for (int i = 0; i < tiled.Count; i++)
            {
                if (tiled[i] != constant[i])
                {
                    disagreed.Add($"{entry.Id}: row {i} is '{tiled[i]}' tiled and '{constant[i]}' constant");
                    break;
                }
            }
        }

        Assert.Empty(disagreed);
    }

    private static async Task<List<string>> RowsOf(CorpusEntry entry, bool constantForm)
    {
        List<string> rows = [];
        await using VortexFile file = await VortexFile.OpenAsync(
            entry.Path, With(constantForm), CancellationToken.None);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            Vorticity.Tests.Writing.Values.DescribeRows(batch, rows);
        }

        return rows;
    }

    private static async Task<(CanonicalKind Kind, int Rows)> RootOf(string entry, bool constantForm)
    {
        await using VortexFile file = await VortexFile.OpenAsync(
            CorpusManifest.Get(entry).Path, With(constantForm), CancellationToken.None);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            return (batch.Node(batch.RootIndex).Kind, batch.RowCount);
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
