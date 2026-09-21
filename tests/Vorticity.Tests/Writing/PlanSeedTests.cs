// On append, plan memory is seeded from the last chunk's encoding tree.
//
// THE SEED IS A READING OF WHAT THE WRITER WROTE, so its oracle is the writer's own ledger. Every
// in-scope corpus table is written again by this writer, and every chunk of every top-level column
// is read back through the seed: it must name the scheme `WriteReport` says the chunk was written
// with, and no scheme where the report says the chunk stayed canonical (or is a struct, whose
// fields keep the memories). A scheme whose root encoding the mapping misreads fails here on the
// first corpus file that chooses it. What the seed then does to an append is `AppendTests`'.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.Layouts;
using Vorticity.Scan;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

public sealed class PlanSeedTests
{
    [Fact]
    public async Task EveryChunkReadsBackAsTheSchemeTheWriterReported()
    {
        Decoders.EnsureRegistered();
        StringBuilder failures = new StringBuilder();
        Dictionary<string, int> seen = new Dictionary<string, int>(StringComparer.Ordinal);
        int chunks = 0;
        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            if (!entry.HasDTypeSegment)
            {
                continue;
            }

            string path = TempPath();
            try
            {
                await using VortexFile source = await VortexFile.OpenAsync(entry.Path);
                if (source.Schema.Kind != DTypeKind.Struct || source.Schema.FieldCount == 0)
                {
                    continue;
                }

                WriteReport report = await RewriteAsync(source, path);
                chunks += await CompareAsync(entry.Id, path, report, failures, seen);
            }
            finally
            {
                System.IO.File.Delete(path);
            }
        }

        // No corpus table is written with FSST, so a text column it wins on is written here:
        // high-cardinality URLs, two hundred of them.
        string urls = TempPath();
        try
        {
            chunks += await CompareAsync("urls", urls, await WriteUrlsAsync(urls), failures, seen);
        }
        finally
        {
            System.IO.File.Delete(urls);
        }

        Assert.True(failures.Length == 0, failures.ToString());
        // The corpus's tables, one chunk or a few each: the floor sits just under their total.
        Assert.True(chunks >= 400, $"only {chunks} chunks compared");

        // The corpus reaches every scheme the seed maps, so none of them is taken on faith.
        string[] expected =
        [
            nameof(ColumnScheme.Dict), nameof(ColumnScheme.RunEnd), nameof(ColumnScheme.BitPacked),
            nameof(ColumnScheme.Sequence), nameof(ColumnScheme.Alp), nameof(ColumnScheme.Fsst),
            nameof(ColumnScheme.Zstd),
        ];
        Assert.All(expected, scheme => Assert.True(
            seen.ContainsKey(scheme),
            $"{scheme} was never written; seen: {string.Join(", ", seen.Select(p => $"{p.Key} {p.Value}"))}"));
    }

    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), $"vorticity-seed-{Guid.NewGuid():N}.vortex");

    private static async Task<WriteReport> RewriteAsync(VortexFile source, string path)
    {
        await using VortexFileWriter writer = VortexFileWriter.Create(
            path, source.Schema, new VortexWriteOptions { Indexes = WritePolicy.None });
        await foreach (RecordBatch batch in source.Scan().ExecuteAsync())
        {
            await writer.WriteAsync(batch);
        }

        return await writer.CompleteAsync();
    }

    private static async Task<WriteReport> WriteUrlsAsync(string path)
    {
        // Under zstd's sixteen kilobytes, so FSST is the trial that decides.
        const int Rows = 200;
        DTypeArena types = new DTypeArena();
        DType utf8 = types.Utf8(Nullability.NonNullable);
        DType schema = types.Struct(["url"], [utf8], Nullability.NonNullable);
        CanonicalArena arena = new CanonicalArena();
        byte[][] values = new byte[Rows][];
        int heapBytes = 0;
        for (int row = 0; row < Rows; row++)
        {
            values[row] = Encoding.UTF8.GetBytes(string.Create(
                CultureInfo.InvariantCulture,
                $"https://example.com/catalog/item/{(row * 7919L) % 100_003}/detail?ref={row}&lang=fr"));
            heapBytes += values[row].Length;
        }

        // Every URL is longer than twelve bytes, so every view points into the one heap.
        VortexBuffer heap = arena.Allocate(heapBytes, 1, out Span<byte> data);
        VortexBuffer views = arena.Allocate(Rows * 16, 16, out Span<byte> bytes);
        int offset = 0;
        for (int row = 0; row < Rows; row++)
        {
            Span<byte> view = bytes.Slice(row * 16, 16);
            BinaryPrimitives.WriteInt32LittleEndian(view, values[row].Length);
            values[row].AsSpan(0, 4).CopyTo(view[4..]);
            BinaryPrimitives.WriteInt32LittleEndian(view[8..], 0);
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], offset);
            values[row].CopyTo(data[offset..]);
            offset += values[row].Length;
        }

        int column = arena.AddVarBinView(utf8, Rows, Validity.NonNullable, views, [heap]);
        int root = arena.AddStruct(schema, Rows, Validity.NonNullable, [column]);
        await using VortexFileWriter writer = VortexFileWriter.Create(
            path, schema, new VortexWriteOptions { Indexes = WritePolicy.None });
        await writer.WriteAsync(new RecordBatch(arena, root, 0));
        return await writer.CompleteAsync();
    }

    private static async Task<int> CompareAsync(
        string id, string path, WriteReport report, StringBuilder failures, Dictionary<string, int> seen)
    {
        int compared = 0;
        await using VortexFile written = await VortexFile.OpenAsync(path);
        DType schema = written.Schema;
        for (int field = 0; field < schema.FieldCount; field++)
        {
            (List<(LayoutNode Flat, long Start)> flats, _) =
                VortexFileWriter.AppendPlan.ColumnChunks(written.LayoutTree, field);
            IReadOnlyList<string> encodings = report.Columns[field].Encodings;
            if (flats.Count != encodings.Count)
            {
                failures.Append(CultureInfo.InvariantCulture, $"{id} {schema.GetFieldName(field)}: ")
                    .Append(CultureInfo.InvariantCulture, $"{flats.Count} chunks, {encodings.Count} reported\n");
                continue;
            }

            for (int c = 0; c < flats.Count; c++)
            {
                PlanSeed? seed = await VortexFileWriter.AppendPlan.SeedAsync(
                    written, flats[c].Flat, schema.GetField(field), CancellationToken.None);
                string read = seed?.Scheme?.ToString() ?? nameof(ColumnScheme.None);
                string wrote = encodings[c].Length == 0 ? nameof(ColumnScheme.None) : encodings[c];
                if (read != wrote)
                {
                    failures.Append(CultureInfo.InvariantCulture, $"{id} {schema.GetFieldName(field)} chunk {c}: ")
                        .Append(CultureInfo.InvariantCulture, $"wrote {wrote}, read {read}\n");
                }

                seen[wrote] = seen.GetValueOrDefault(wrote) + 1;
                compared++;
            }
        }

        return compared;
    }
}
