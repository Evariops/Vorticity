// The internal switch of PERF-AUDIT-v2.md Z1b, exercised on both sides.
//
// The point of a switch is that the two forms coexist in ONE process, so what has to be tested is
// not "the new form works" but "the two forms agree". Both assertions below read the same corpus
// file twice, once each way.
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Writing;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Arrays;

public sealed class ConstantFormTests
{
    private const string Entry = "encodings/constant";

    private static VortexOpenOptions With(bool constantForm) =>
        new VortexOpenOptions { Read = new VortexReadOptions { ConstantForm = constantForm } };

    /// <summary>The switch, and nothing else, decides which kind the canonicalizer produces.</summary>
    [Fact]
    public async Task TheSwitchDecidesWhichKindTheCanonicalizerProduces()
    {
        Vorticity.Tests.Scan.Decoders.EnsureRegistered();

        (CanonicalKind tiled, int tiledRows) = await RootOf(constantForm: false);
        (CanonicalKind constant, int constantRows) = await RootOf(constantForm: true);

        Assert.Equal(CanonicalKind.Primitive, tiled);
        Assert.Equal(CanonicalKind.Constant, constant);

        // The row count is the half of the claim that must NOT change: the form holds one element,
        // it still stands for every row.
        Assert.Equal(tiledRows, constantRows);
        Assert.True(tiledRows > 0, "the corpus entry produced no rows");
    }

    /// <summary>
    /// Writing a constant column back produces the SAME BYTES either way.
    /// </summary>
    /// <remarks>
    /// The writer materializes the element rather than emitting `vortex.constant` on the wire, so
    /// this is byte-exact by construction -- and this test is what makes "by construction" a fact
    /// rather than a claim. Emitting a constant on the wire would shrink the file, which is a
    /// different gain and a different point; mixing it in here would move `WrittenSizeTests` under
    /// cover of a memory refactor.
    /// </remarks>
    [Fact]
    public async Task WritingBackIsByteIdenticalEitherWay()
    {
        Vorticity.Tests.Scan.Decoders.EnsureRegistered();

        Assert.Equal(await RewriteOf(constantForm: false), await RewriteOf(constantForm: true));
    }

    private static async Task<(CanonicalKind Kind, int Rows)> RootOf(bool constantForm)
    {
        await using VortexFile file = await VortexFile.OpenAsync(
            CorpusManifest.Get(Entry).Path, With(constantForm), CancellationToken.None);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            return (batch.Root.Kind, batch.RowCount);
        }

        throw new InvalidOperationException($"{Entry} produced no batches.");
    }

    private static async Task<long> RewriteOf(bool constantForm)
    {
        string written = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-constant-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFile source = await VortexFile.OpenAsync(
                CorpusManifest.Get(Entry).Path, With(constantForm), CancellationToken.None))
            await using (VortexFileWriter writer = VortexFileWriter.Create(written, source.Schema))
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

    /// <summary>
    /// Both forms hand the caller the same values, row for row, on every constant entry.
    /// </summary>
    /// <remarks>
    /// THE ASSERTION THAT WOULD CATCH A WRONG WINDOW, and nothing else would: a constant resolved to
    /// the wrong element, or to a stale one, differs here while the kind, the row count, the dtype
    /// and the validity all still agree. The six entries cover the row counts the corpus varies --
    /// 0, 1, 1023, 1025 and 4096 -- donc le cas vide, le cas a une ligne et les tailles qui ne
    /// tombent pas sur un octet sont tous lus.
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

        List<string> tiled = await ValuesOf(entry, constantForm: false);
        List<string> constant = await ValuesOf(entry, constantForm: true);

        Assert.Equal(tiled, constant);
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
                    ? column.AsPrimitive<long>()[i].ToString(System.Globalization.CultureInfo.InvariantCulture)
                    : "null");
            }
        }

        return values;
    }
}
