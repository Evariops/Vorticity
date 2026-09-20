// The constant form: a column stored as one element and a row count.
//
// There used to be two forms behind a switch, and these tests read a file both ways and compared
// them to each other. With one form left that question cannot be asked, so the assertions are
// against things outside the form instead: the corpus files' own values, the values the same rows
// have after being written back and read again, and the promise that no caller can see the form at
// all.
//
// The corpus-wide value oracle is not here. `CorpusValueTests` in the conformance project checks
// every shipped file against its sidecar, which is an independent record of what the file holds;
// duplicating that here would assert our reader against itself.
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
    /// <summary>The canonicalizer produces the form, and it stands for every row.</summary>
    /// <remarks>
    /// READ OFF THE ARENA NODE, not off <c>VortexColumn.Kind</c>, and the difference is the point:
    /// the column API deliberately never reports <see cref="CanonicalKind.Constant"/>, because the
    /// form is how the decoder stored the column and not what the column holds -- a caller
    /// switching on the kind is asking the second question. The arena is the only place the form is
    /// observable at all.
    /// </remarks>
    [Fact]
    public async Task TheCanonicalizerProducesTheConstantForm()
    {
        Vorticity.Tests.Scan.Decoders.EnsureRegistered();

        (CanonicalKind kind, int rows) = await RootOf("encodings/constant");

        Assert.Equal(CanonicalKind.Constant, kind);
        Assert.True(rows > 0, "the corpus entry produced no rows");
    }

    /// <summary>Whatever the arena holds, the public column view reports the dtype's own form.</summary>
    /// <remarks>
    /// The other half of the promise above, and what keeps every caller's `switch` working: a
    /// constant column presents the form its dtype stands for.
    /// </remarks>
    [Fact]
    public async Task TheColumnViewNeverReportsTheConstantForm()
    {
        Vorticity.Tests.Scan.Decoders.EnsureRegistered();

        await using VortexFile file = await VortexFile.OpenAsync(
            CorpusManifest.Get("encodings/constant").Path, CancellationToken.None);
        int batches = 0;
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            Assert.Equal(CanonicalKind.Primitive, batch.Root.Kind);
            batches++;
        }

        Assert.True(batches > 0, "the corpus entry produced no batches");
    }

    /// <summary>A constant column holds the same values after a round trip through the writer.</summary>
    /// <remarks>
    /// The form is a decode-side representation and the writer expands it, so the rows a rewrite
    /// holds have travelled through both halves: read as one element, written as a column, read
    /// back as one element again. Every shape the corpus carries, including the empty file and the
    /// single row, because those are where a form that stands for a count goes wrong.
    /// </remarks>
    [Theory]
    [InlineData("encodings/constant")]
    [InlineData("encodings/constant_r0")]
    [InlineData("encodings/constant_r1")]
    [InlineData("encodings/constant_r1023")]
    [InlineData("encodings/constant_r1025")]
    public async Task AConstantColumnSurvivesARoundTrip(string entry)
    {
        Vorticity.Tests.Scan.Decoders.EnsureRegistered();

        string source = CorpusManifest.Get(entry).Path;
        List<string> before = await ValuesOf(source);

        string written = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-constant-{Guid.NewGuid():N}.vortex");
        try
        {
            await Rewrite(source, written);
            Assert.Equal(before, await ValuesOf(written));
        }
        finally
        {
            if (System.IO.File.Exists(written))
            {
                System.IO.File.Delete(written);
            }
        }
    }

    /// <summary>Writing a constant column back twice produces the same bytes.</summary>
    /// <remarks>
    /// The writer expands the element rather than emitting `vortex.constant` on the wire, so the
    /// file a rewrite produces does not depend on how the read stored the column. Emitting a
    /// constant on the wire would shrink the file, which is a different gain and a different point;
    /// folding it in here would move `WrittenSizeTests` under cover of a memory refactor.
    /// </remarks>
    [Fact]
    public async Task WritingBackIsDeterministic()
    {
        Vorticity.Tests.Scan.Decoders.EnsureRegistered();
        Assert.Equal(await RewriteLength(), await RewriteLength());
    }

    private static async Task<(CanonicalKind Kind, int Rows)> RootOf(string entry)
    {
        await using VortexFile file = await VortexFile.OpenAsync(
            CorpusManifest.Get(entry).Path, CancellationToken.None);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            return (batch.Node(batch.RootIndex).Kind, batch.RowCount);
        }

        throw new InvalidOperationException($"{entry} produced no batches.");
    }

    private static async Task<List<string>> ValuesOf(string path)
    {
        List<string> values = [];
        await using VortexFile file = await VortexFile.OpenAsync(path, CancellationToken.None);
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

    private static async Task Rewrite(string source, string destination)
    {
        await using VortexFile file = await VortexFile.OpenAsync(source, CancellationToken.None);
        await using Vorticity.Writing.VortexFileWriter writer =
            Vorticity.Writing.VortexFileWriter.Create(destination, file.Schema);
        await foreach (RecordBatch batch in file.Scan().ExecuteAsync()
            .WithCancellation(CancellationToken.None))
        {
            await writer.WriteAsync(batch, CancellationToken.None);
        }

        await writer.CompleteAsync(CancellationToken.None);
    }

    private static async Task<long> RewriteLength()
    {
        string written = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"vorticity-constant-{Guid.NewGuid():N}.vortex");
        try
        {
            await Rewrite(CorpusManifest.Get("encodings/constant").Path, written);
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
