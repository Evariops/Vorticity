// Runs of a column of padded views: two views alike hold one value, two whose length or first four bytes differ hold two,
// and two long values that agree on both are told apart by their bytes, as the compressor's comparer tells every pair.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Scanning;
using Vorticity.Types;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class ViewRunsTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const int Rows = 8_192;
    private const int RunRows = 64;

    /// <summary>
    /// Runs of 64 rows of padded views, each row its own copy of its value's bytes: values past their views that share
    /// their length and first four bytes run next to each other, short ones among them. Written as run-end, the column
    /// reads back as it was written.
    /// </summary>
    [Fact]
    public async Task LongValuesThatShareTheirHeadsKeepTheirRuns()
    {
        string[] values = [.. Enumerable.Range(0, Rows).Select(row => Value(row / RunRows))];
        CanonicalArena arena = new CanonicalArena();
        DTypeArena types = new DTypeArena();
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType schema = types.Struct(["name"], [utf8], Nullability.NonNullable);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [PaddedViews(arena, utf8, values)]);
        string path = Path.Combine(Path.GetTempPath(), $"view-runs-{Guid.NewGuid():N}.vortex");
        try
        {
            WriteReport report;
            await using (VortexFileWriter writer = VortexFileWriter.Create(path, schema, new VortexWriteOptions()))
            {
                using (RecordBatch batch = new RecordBatch(arena, root, 0))
                {
                    await writer.WriteAsync(batch, Ct);
                }

                report = await writer.CompleteAsync(Ct);
            }

            Assert.All(Assert.Single(report.Columns).Encodings, scheme => Assert.Equal(nameof(EncodingHint.RunEnd), scheme));
            List<string> read = [];
            await using (VortexFile file = await VortexFile.OpenAsync(path, Ct))
            {
                await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(Ct))
                {
                    using (batch)
                    {
                        BinaryColumn names = batch.Column(0).AsBinary();
                        for (int row = 0; row < batch.RowCount; row++)
                        {
                            read.Add(Encoding.UTF8.GetString(names.GetSpan(row)));
                        }
                    }
                }
            }

            Assert.Equal(values, read);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>Run <paramref name="run"/>'s value: past its view, of one length and one head, but every fifth run's short.</summary>
    private static string Value(int run) => run % 5 == 4 ? $"short-{run % 7}" : $"request/path-{run % 9:D4}";

    /// <summary>The views of <paramref name="values"/>, each one's bytes inside it and zeros past them, or its first four and its own copy of its bytes.</summary>
    private static int PaddedViews(CanonicalArena arena, DType dtype, string[] values)
    {
        byte[][] bytes = [.. values.Select(Encoding.UTF8.GetBytes)];
        int past = bytes.Where(value => value.Length > 12).Sum(value => value.Length);
        VortexBuffer views = arena.Allocate(values.Length * 16, 16, out Span<byte> viewBytes);
        VortexBuffer data = arena.Allocate(Math.Max(past, 1), 1, out Span<byte> heap);
        int at = 0;
        for (int i = 0; i < bytes.Length; i++)
        {
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(view, bytes[i].Length);
            if (bytes[i].Length <= 12)
            {
                bytes[i].CopyTo(view[4..]);
                continue;
            }

            bytes[i].AsSpan(0, 4).CopyTo(view[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], at);
            bytes[i].CopyTo(heap[at..]);
            at += bytes[i].Length;
        }

        return arena.AddVarBinView(dtype, values.Length, Validity.NonNullable, views, past > 0 ? [data] : [], padded: true);
    }
}
