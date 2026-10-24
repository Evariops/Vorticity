// The path map of a shredded variant column: for each shredded key, chunk by chunk, the range of its
// buffers and its tree over them. A key read through it fetches that range and nothing else of the
// chunk, and decodes the key's values: its typed column, or where the key kept values of other
// classes, a variant of them.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Compute;
using Vorticity.Indexes;
using Vorticity.Tests.File;
using Vorticity.Types;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class VariantPathMapTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VariantShredding Plan = VariantShredding.Object(
        ("route", VariantShredding.Primitive(VariantClass.String)),
        ("score", VariantShredding.Primitive(VariantClass.Double)),
        ("status", VariantShredding.Primitive(VariantClass.Integer)),
        ("user", VariantShredding.Object(("id", VariantShredding.Primitive(VariantClass.Integer)))));

    /// <summary>
    /// Six chunks of a thousand rows: every key reads back from its own range, its typed column or, in
    /// the chunks where the status was also text, a variant of its values; a missing key and a row that
    /// is no object are null; and reading a key fetched the map's region and its ranges alone.
    /// </summary>
    [Fact]
    public async Task EachKeyReadsBackFromItsOwnBuffers()
    {
        Random random = new Random(4);
        Doc[] rows = [.. Enumerable.Range(0, 6_000).Select(i => new Doc(i, i % 997 == 0 ? Variant.From("no object") : Variant.Object(
            [
                ("route", Variant.From($"/r/{random.Next(30)}")),
                ("score", Variant.From(random.NextDouble())),
                .. (IEnumerable<(string, Variant)>)(i % 5 == 0 ? [] : [("status", i is > 2_000 and < 3_000 && i % 7 == 0 ? Variant.From("502") : Variant.From(200 + random.Next(4)))]),
                ("user", Variant.Object(("id", i), ("name", "n"))),
                ("other", Variant.From(i)),
            ])))];
        byte[] bytes = await WriteAsync(rows);
        TestSegmentSource source = new TestSegmentSource(bytes) { Ranges = [] };
        // An open that reads no more of the tail than it needs, so that no chunk is served by it.
        await using VortexFile file = await VortexFile.OpenAsync(source, VortexOpenOptions.Default with { InitialReadBytes = 64 }, Ct);
        int field = file.DType.IndexOfField(nameof(Doc.Attributes));

        foreach (string key in new[] { "route", "score", "status", "user" })
        {
            int before = source.Ranges.Count;
            VariantPathMap map = (await VariantPathMap.ReadAsync(file, field, [key], Ct))!;
            int k = map.Keys.ToList().IndexOf(key);
            PathChunk[] chunks = map.Chunks[k];
            Assert.Equal(6, chunks.Length);
            Assert.Equal(rows.Length, chunks.Sum(chunk => chunk.Rows));
            int afterMap = source.Ranges.Count;
            foreach (PathChunk chunk in chunks)
            {
                using ScanContext context = new ScanContext(file);
                (int node, DType dtype) = await VariantPathReader.ReadAsync(context, file, chunk, Ct);
                Check(context.Canonical, node, dtype, key, rows.AsSpan((int)chunk.FirstRow, (int)chunk.Rows));
            }

            // The map's region at most once, read with the index directory when it lies beside it,
            // then each chunk's range of the key, and nothing else.
            Assert.InRange(afterMap - before, 0, 1);
            (long, long)[] own = [.. chunks.Select(chunk => ((long)chunk.Offset, (long)chunk.Length))];
            Assert.NotEmpty(source.Ranges.Skip(afterMap));
            Assert.All(source.Ranges.Skip(afterMap), range => Assert.Contains(range, own));
        }

        // What a key costs the map a chunk, its tree kept whole and its region one zstd frame: a
        // ratchet on the bytes of its region. Measured on 2026-10-19: 39, 32, 66 and 82 bytes a chunk,
        // where the trees take 219 to 929 bytes a chunk uncompressed.
        VariantPathMap all = (await VariantPathMap.ReadAsync(file, field, ["route", "score", "status", "user"], Ct))!;
        int[] perChunk = [.. Enumerable.Range(0, all.Keys.Count).Select(k => all.KeyBytes(k).Length / all.Chunks[k].Length)];
        Assert.True(perChunk.Max() <= 90, $"bytes a chunk by key: {string.Join(", ", all.Keys.Zip(perChunk, (key, bytes) => $"{key} {bytes}"))}");

        // The status chunk that held text is a variant of its values; the others are integers.
        VariantPathMap statuses = (await VariantPathMap.ReadAsync(file, field, ["status"], Ct))!;
        DTypeArena types = new DTypeArena();
        Assert.Equal(
            [DTypeKind.Primitive, DTypeKind.Primitive, DTypeKind.Variant, DTypeKind.Primitive, DTypeKind.Primitive, DTypeKind.Primitive],
            statuses.Chunks[statuses.Keys.ToList().IndexOf("status")].Select(chunk => Vorticity.Types.Serialization.DTypeProtobuf.Read(chunk.DType, types).Kind));
    }

    /// <summary>A key's rows as the documents say: its value of the key's class, a variant of any value where the chunk kept other classes, null where it is missing.</summary>
    private static void Check(CanonicalArena arena, int node, DType dtype, string key, ReadOnlySpan<Doc> rows)
    {
        CanonicalNode column = arena.GetNode(node);
        for (int row = 0; row < rows.Length; row++)
        {
            Variant document = rows[row].Attributes;
            Variant? value = document.Kind == VariantKind.Object ? document[key] : null;
            bool valid = ArenaWords.IsValid(arena, node, row);
            switch (dtype.Kind)
            {
                case DTypeKind.Variant:
                {
                    Assert.Equal(value is not null, valid);
                    if (valid)
                    {
                        Variant read = Variant.FromBytes(
                            LiteralReader.ViewAt(arena.GetNode(column.GetFieldIndex(0)), row),
                            LiteralReader.ViewAt(arena.GetNode(column.GetFieldIndex(1)), row));
                        Assert.Equal(value!.Value, read);
                    }

                    break;
                }

                case DTypeKind.Struct:
                {
                    Assert.Equal(value is { Kind: VariantKind.Object }, valid);
                    if (valid)
                    {
                        int id = column.GetFieldIndex(dtype.IndexOfField("id"));
                        Assert.Equal(value!.Value["id"]!.Value.AsInt64(), Long(arena, id, row));
                    }

                    break;
                }

                case DTypeKind.Utf8:
                    Assert.Equal(value?.AsString(), valid ? System.Text.Encoding.UTF8.GetString(LiteralReader.ViewAt(column, row)) : null);
                    break;
                case DTypeKind.Primitive when dtype.PType == PType.F64:
                    Assert.Equal(value?.AsDouble(), valid ? System.Runtime.InteropServices.MemoryMarshal.Cast<byte, double>(column.Values.Span)[row] : null);
                    break;
                default:
                    Assert.Equal(value?.AsInt64(), valid ? Long(arena, node, row) : null);
                    break;
            }
        }
    }

    private static long? Long(CanonicalArena arena, int node, int row) =>
        ArenaWords.IsValid(arena, node, row) ? System.Runtime.InteropServices.MemoryMarshal.Cast<byte, long>(arena.GetNode(node).Values.Span)[row] : null;

    private static async Task<byte[]> WriteAsync(Doc[] rows)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-pathmap-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                BlockRows = 1_000,
                ChunkTargetBytes = 1_024,
                ShreddedVariants = new Dictionary<string, VariantShredding> { [nameof(Doc.Attributes)] = Plan },
            };
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Doc>(path, options))
            {
                await writer.WriteAsync<Doc>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            return await System.IO.File.ReadAllBytesAsync(path, Ct);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [VortexRecord]
    public partial record struct Doc(long Id, Variant Attributes);
}
