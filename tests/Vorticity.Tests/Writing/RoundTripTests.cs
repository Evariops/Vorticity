// The writer's acceptance test: read a real file, write it back, read what was written, and assert
// the values are the same.
//
// This is worth saying plainly, because it is the first trap of any conformance suite. A round
// trip through our own writer and reader is SELF-CONSISTENT AND CAN BE UNIFORMLY WRONG: if the
// writer and the reader agree on a mistake -- a swapped child order, a validity convention
// misunderstood in the same direction twice -- this test passes and the file is unreadable by every
// other implementation.
//
// What makes it worth having anyway is the SOURCE of the data. The input is the golden corpus,
// which the conformance suite has already verified value for value against the Rust reference. So
// the left-hand side of every comparison is anchored, and the test asks a real question: does what
// we wrote decode back to what Rust says those bytes mean?
//
// It is still not the same as Rust reading our output. That is the Rust cross-check, and it needs
// the Rust toolchain; this test is a step toward it, not a substitute for it.
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class RoundTripTests
{
    [Theory]
    [InlineData("containers/uncompressed_canonical")]
    [InlineData("containers/zoned_many_zones_nulls")]
    [InlineData("distributions/high_cardinality_i64_r8193")]
    [InlineData("distributions/all_null_i64_r1024")]
    [InlineData("distributions/constant_i64_r1024")]
    [InlineData("distributions/float_specials_f32_r8193")]
    [InlineData("types/utf8_nullable_r1025")]
    [InlineData("types/binary_nonnull_r1023")]
    [InlineData("types/bool_nullable_r8193")]
    public async Task AFileWrittenFromACorpusFileReadsBackWithTheSameValues(string id)
    {
        Decoders.EnsureRegistered();

        string written = Path.Combine(Path.GetTempPath(), $"vorticity-{Guid.NewGuid():N}.vortex");
        try
        {
            DType schema;
            List<string> original;

            await using (VortexFile source = await VortexFile.OpenAsync(
                Corpus.Path(id), CancellationToken.None))
            {
                schema = source.Schema;
                await using VortexFileWriter writer = VortexFileWriter.Create(written, schema);

                original = [];
                await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    Describe(batch, original);
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
            }

            List<string> readBack = [];
            await using (VortexFile target = await VortexFile.OpenAsync(written, CancellationToken.None))
            {
                Assert.Equal(schema.ToString(), target.Schema.ToString());

                await foreach (RecordBatch batch in target.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    Describe(batch, readBack);
                }
            }

            Assert.Equal(original, readBack);
            Assert.NotEmpty(original);
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
    /// The same round trip, fed several batches, so the writer CONCATENATES before it emits.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A DIFFERENT CODE PATH, AND NOTHING REACHED IT. `VortexFileWriter` holds batches until it has
    /// a whole row block AND a whole byte block, so a corpus file handed over in one batch goes out
    /// as one chunk and `CanonicalConcat` never runs. Every test above, the conformance sweep and
    /// the Rust cross-check are in exactly that position - so `vortex.variant` can turn
    /// unwritable from more than one batch without a single test noticing, and only a large
    /// benchmark file shows it; a benchmark is not a guard.
    /// </para>
    /// <para>
    /// The encodings here are the ones whose CANONICAL form has children the SCHEMA does not name:
    /// a variant is `Struct{metadata, value}` under a dtype whose own field count is 0, and a map is
    /// a ListView under a Map dtype. Those are the shapes a walk driven by the schema loses. The
    /// last two are controls - an ordinary struct of primitives, and a string column - so a failure
    /// here says which half is broken.
    /// </para>
    /// </remarks>
    /// <param name="id">The corpus entry to rewrite.</param>
    [Theory]
    [InlineData("encodings/variant")]
    [InlineData("encodings/parquet_variant")]
    [InlineData("encodings/map")]
    [InlineData("containers/uncompressed_canonical")]
    [InlineData("types/utf8_nullable_r1025")]
    public async Task AFileWrittenFromSeveralBatchesReadsBackWithTheSameValues(string id)
    {
        Decoders.EnsureRegistered();

        // Small enough that every entry above yields more than one batch, and far below the
        // writer's 8192-row block, so every one of them is still pending at CompleteAsync.
        const int BatchRows = 4;

        string written = Path.Combine(Path.GetTempPath(), $"vorticity-concat-{Guid.NewGuid():N}.vortex");
        try
        {
            DType schema;
            List<string> original = [];
            int batches = 0;

            await using (VortexFile source = await VortexFile.OpenAsync(
                Corpus.Path(id), CancellationToken.None))
            {
                schema = source.Schema;
                await using VortexFileWriter writer = VortexFileWriter.Create(written, schema);

                await foreach (RecordBatch batch in source.Scan().WithMaxBatchRows(BatchRows)
                    .ExecuteAsync().WithCancellation(CancellationToken.None))
                {
                    batches++;
                    Describe(batch, original);
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
            }

            Assert.True(batches > 1, $"{id} must be written from more than one batch to mean anything");

            List<string> readBack = [];
            await using (VortexFile target = await VortexFile.OpenAsync(written, CancellationToken.None))
            {
                Assert.Equal(schema.ToString(), target.Schema.ToString());

                await foreach (RecordBatch batch in target.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    Describe(batch, readBack);
                }
            }

            Assert.Equal(original, readBack);
            Assert.NotEmpty(original);
        }
        finally
        {
            if (System.IO.File.Exists(written))
            {
                System.IO.File.Delete(written);
            }
        }
    }

    [Fact]
    public async Task TheWrittenFileReportsTheRowCountItWasGiven()
    {
        Decoders.EnsureRegistered();
        const string Source = "containers/zoned_many_zones_nulls";

        string written = Path.Combine(Path.GetTempPath(), $"vorticity-{Guid.NewGuid():N}.vortex");
        try
        {
            long expected;
            await using (VortexFile source = await VortexFile.OpenAsync(
                Corpus.Path(Source), CancellationToken.None))
            {
                expected = source.RowCount;
                await using VortexFileWriter writer = VortexFileWriter.Create(written, source.Schema);
                await foreach (RecordBatch batch in source.Scan().ExecuteAsync()
                    .WithCancellation(CancellationToken.None))
                {
                    await writer.WriteAsync(batch, CancellationToken.None);
                }

                await writer.CompleteAsync(CancellationToken.None);
                Assert.Equal(expected, writer.RowCount);
            }

            await using VortexFile target = await VortexFile.OpenAsync(written, CancellationToken.None);
            Assert.Equal(expected, target.RowCount);
            Assert.Equal(VortexFileFormat.Version, target.FormatVersion);
        }
        finally
        {
            if (System.IO.File.Exists(written))
            {
                System.IO.File.Delete(written);
            }
        }
    }

    [Fact]
    public async Task AFileWithNoBatchesIsStillAValidFile()
    {
        // Zero rows, zero segments, and a layout tree that still has to say so.
        Decoders.EnsureRegistered();
        string written = Path.Combine(Path.GetTempPath(), $"vorticity-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFile source = await VortexFile.OpenAsync(
                Corpus.Path("containers/uncompressed_canonical"), CancellationToken.None))
            {
                await using VortexFileWriter writer = VortexFileWriter.Create(written, source.Schema);
                await writer.CompleteAsync(CancellationToken.None);
            }

            await using VortexFile target = await VortexFile.OpenAsync(written, CancellationToken.None);
            Assert.Equal(0, target.RowCount);
        }
        finally
        {
            if (System.IO.File.Exists(written))
            {
                System.IO.File.Delete(written);
            }
        }
    }

    [Fact]
    public void AnUnsetSchemaIsRefused()
    {
        // A non-struct root is legal and common -- half the corpus has one -- so what is refused is
        // only a schema that was never read.
        using MemoryStream stream = new MemoryStream();
        StreamSegmentSink sink = new StreamSegmentSink(stream);
        Assert.Throws<ArgumentException>(() => VortexFileWriter.Create(sink, default));
    }

    /// <summary>
    /// Renders every value of every column, so the comparison is over VALUES rather than over the
    /// bytes we happened to write.
    /// </summary>
    /// <summary>Flattens a batch to one string per value, ROW-major.</summary>
    /// <remarks>
    /// ROW-MAJOR, AND THAT IS THE WHOLE POINT. Field-major within a batch makes the flattened
    /// sequence depend on where the batch boundaries fall, so the same rows split differently
    /// compare unequal -- and the writer is allowed to choose its own chunking, which is what
    /// `VortexWriteOptions.RowBlockSize` does. Row-major is invariant under re-batching, which is
    /// what this test is actually asserting.
    /// </remarks>
    private static void Describe(RecordBatch batch, List<string> into)
    {
        for (int row = 0; row < batch.RowCount; row++)
        {
            for (int field = 0; field < batch.FieldCount; field++)
            {
                into.Add(Render(batch.Column(field), row));
            }
        }
    }

    private static string Render(VortexColumn column, int row)
    {
        if (!column.IsValid(row))
        {
            return "null";
        }

        return column.Kind switch
        {
            CanonicalKind.Bool => column.AsBool()[row].ToString(),
            CanonicalKind.Primitive => RenderPrimitive(column, row),
            CanonicalKind.VarBinView => Convert.ToBase64String(column.AsBinary().GetSpan(row)),
            CanonicalKind.Decimal => column.AsDecimal()[row].Unscaled.ToString(),
            _ => column.Kind.ToString(),
        };
    }

    private static string RenderPrimitive(VortexColumn column, int row) => column.DType.PType switch
    {
        // Floats by their raw bits: -0.0 and a NaN payload must not compare equal to their
        // neighbours just because they render the same (the sidecar rule, applied here too).
        PType.F32 => BitConverter.SingleToUInt32Bits(column.AsPrimitive<float>().Values[row])
            .ToString("X8", System.Globalization.CultureInfo.InvariantCulture),
        PType.F64 => BitConverter.DoubleToUInt64Bits(column.AsPrimitive<double>().Values[row])
            .ToString("X16", System.Globalization.CultureInfo.InvariantCulture),
        PType.I8 => column.AsPrimitive<sbyte>().Values[row].ToString(System.Globalization.CultureInfo.InvariantCulture),
        PType.I16 => column.AsPrimitive<short>().Values[row].ToString(System.Globalization.CultureInfo.InvariantCulture),
        PType.I32 => column.AsPrimitive<int>().Values[row].ToString(System.Globalization.CultureInfo.InvariantCulture),
        PType.I64 => column.AsPrimitive<long>().Values[row].ToString(System.Globalization.CultureInfo.InvariantCulture),
        PType.U8 => column.AsPrimitive<byte>().Values[row].ToString(System.Globalization.CultureInfo.InvariantCulture),
        PType.U16 => column.AsPrimitive<ushort>().Values[row].ToString(System.Globalization.CultureInfo.InvariantCulture),
        PType.U32 => column.AsPrimitive<uint>().Values[row].ToString(System.Globalization.CultureInfo.InvariantCulture),
        _ => column.AsPrimitive<ulong>().Values[row].ToString(System.Globalization.CultureInfo.InvariantCulture),
    };
}
