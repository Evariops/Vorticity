// Questions over the shredded keys of a variant column, read through the path map: every key read
// alone from its own ranges, decoded as a column, no variant rebuilt. The same questions over the
// same rows written whole give the same answers, and a question on one key reads none of the bytes
// of the column's other keys.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Indexes;
using Vorticity.Tests.File;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class VariantKeyReadTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly VariantShredding Plan = VariantShredding.Object(
        ("ok", VariantShredding.Primitive(VariantClass.Boolean)),
        ("route", VariantShredding.Primitive(VariantClass.String)),
        ("score", VariantShredding.Primitive(VariantClass.Double)),
        ("status", VariantShredding.Primitive(VariantClass.Integer)));

    /// <summary>
    /// Six chunks of attributes, the status also text in one of them and a float now and then among the
    /// scores: each read of a key, as stored or converted, its presence, and groups over keys, answer on
    /// the shredded file as on the same rows written whole.
    /// </summary>
    [Fact]
    public async Task EveryQuestionOverKeysAnswersAsOverTheWholeColumn()
    {
        Doc[] rows = Rows();
        await using VortexFile shredded = await OpenAsync(await WriteAsync(rows, Plan), out _);
        await using VortexFile whole = await OpenAsync(await WriteAsync(rows, null), out _);

        foreach (VortexFile file in new[] { shredded, whole })
        {
            Assert.Equal(rows.Select(r => r.Attributes.Kind == VariantKind.Object ? r.Attributes["status"]?.AsInt64() : null), await file.Scan<Doc>().Select(r => r.Attributes["status"].AsInt64()).ToListAsync(Ct));
            Assert.Equal(rows.Select(r => r.Attributes.Kind == VariantKind.Object ? r.Attributes["status"]?.ToInt64() : null), await file.Scan<Doc>().Select(r => r.Attributes["status"].ToInt64()).ToListAsync(Ct));
            Assert.Equal(rows.Select(r => r.Attributes.Kind == VariantKind.Object ? r.Attributes["route"]?.AsString() : null), await file.Scan<Doc>().Select(r => r.Attributes["route"].AsString()).ToListAsync(Ct));
            Assert.Equal(rows.Select(r => r.Attributes.Kind == VariantKind.Object ? r.Attributes["score"]?.AsDouble() : null), await file.Scan<Doc>().Select(r => r.Attributes["score"].AsDouble()).ToListAsync(Ct));
            Assert.Equal(rows.Select(r => r.Attributes.Kind == VariantKind.Object ? r.Attributes["ok"]?.AsBool() : null), await file.Scan<Doc>().Select(r => r.Attributes["ok"].AsBool()).ToListAsync(Ct));
            Assert.Equal(rows.Count(r => r.Attributes.Kind == VariantKind.Object && r.Attributes.Has("status")), await file.Scan<Doc>().Where(r => r.Attributes.Has("status")).CountAsync(Ct));
        }

        // A row that is no object has no route: its group is the null one.
        List<RouteSum> Expected() => [.. rows
            .GroupBy(r => r.Attributes.Kind == VariantKind.Object ? r.Attributes["route"]?.AsString() : null)
            .Select(g => new RouteSum(g.Key, g.Count(), g.Sum(r => r.Attributes.Kind == VariantKind.Object ? r.Attributes["status"]?.AsInt64() ?? 0 : 0)))
            .OrderBy(g => g.Route ?? "~", StringComparer.Ordinal)];
        foreach (VortexFile file in new[] { shredded, whole })
        {
            List<RouteSum> groups = await file.Scan<Doc>()
                .GroupBy(r => r.Attributes["route"].AsString())
                .Select(g => (g.Key, g.Count(), g.Sum(x => x.Attributes["status"].AsInt64())))
                .As<RouteSum>().ToRecordsAsync(Ct).ToListAsync(Ct);
            Assert.Equal(
                Expected().Select(g => (g.Route, g.Rows, g.Statuses)),
                groups.OrderBy(g => g.Route ?? "~", StringComparer.Ordinal).Select(g => (g.Route, g.Rows, g.Statuses)));
        }
    }

    /// <summary>
    /// A question on one key reads that key's ranges, and not a byte the column's other keys hold,
    /// where the whole column would read every chunk's segment.
    /// </summary>
    [Fact]
    public async Task AQuestionOnOneKeyReadsNoOtherKeysBytes()
    {
        byte[] bytes = await WriteAsync(Rows(), Plan);
        await using VortexFile file = await OpenAsync(bytes, out TestSegmentSource source);
        int field = file.DType.IndexOfField(nameof(Doc.Attributes));
        VariantPathMap map = (await VariantPathMap.ReadAsync(file, field, Plan.Keys.Select(k => k.Name).ToArray(), Ct))!;
        int before = source.Ranges!.Count;

        Assert.NotEmpty(await file.Scan<Doc>().Select(r => r.Attributes["route"].AsString()).ToListAsync(Ct));

        List<(long Offset, long Length)> read = [.. source.Ranges.Skip(before)];
        PathChunk[] routes = map.Chunks[map.Keys.ToList().IndexOf("route")];
        Assert.Contains(read, range => routes.Any(chunk => (long)chunk.Offset == range.Offset && (long)chunk.Length == range.Length));
        foreach (string other in new[] { "ok", "score", "status" })
        {
            foreach (PathChunk chunk in map.Chunks[map.Keys.ToList().IndexOf(other)])
            {
                // A key's range starts where its widest alignment allows, so two keys' ranges may share
                // the padding between them, and no more.
                Assert.All(read, range => Assert.True(
                    Math.Min(range.Offset + range.Length, (long)chunk.Offset + chunk.Length) - Math.Max(range.Offset, (long)chunk.Offset) < VortexLimits.MaxAlignment,
                    $"{range.Offset}+{range.Length} reads the {other} key's {chunk.Offset}+{chunk.Length}"));
            }
        }
    }

    /// <summary>
    /// A route in the first half of the rows, a status in the second: each chunk shreds only the keys its
    /// rows hold, so the path map lists each key in half the chunks, and a question on both keys still reads
    /// them by key, a key a chunk lacks reading null there, and answers as over the rows written whole.
    /// </summary>
    [Fact]
    public async Task AChunkShredsOnlyTheKeysItsRowsHold()
    {
        Doc[] rows = [.. Enumerable.Range(0, 6_000).Select(i => new Doc(i, i < 3_000
            ? Variant.Object(("route", Variant.From($"/r/{i % 12}")), ("other", Variant.From(i)))
            : Variant.Object(("status", Variant.From(200L + (i % 4))), ("other", Variant.From(i)))))];
        byte[] bytes = await WriteAsync(rows, Plan);
        await using VortexFile shredded = await OpenAsync(bytes, out TestSegmentSource source);
        await using VortexFile whole = await OpenAsync(await WriteAsync(rows, null), out _);

        int field = shredded.DType.IndexOfField(nameof(Doc.Attributes));
        VariantPathMap map = (await VariantPathMap.ReadAsync(shredded, field, Plan.Keys.Select(k => k.Name).ToArray(), Ct))!;
        PathChunk[] routes = map.Chunks[map.Keys.ToList().IndexOf("route")];
        PathChunk[] statuses = map.Chunks[map.Keys.ToList().IndexOf("status")];
        Assert.NotEmpty(routes);
        Assert.NotEmpty(statuses);
        Assert.All(routes, chunk => Assert.True(chunk.FirstRow < 3_000));
        Assert.All(statuses, chunk => Assert.True(chunk.EndRow > 3_000));
        Assert.Empty(map.Chunks[map.Keys.ToList().IndexOf("ok")]);

        foreach (VortexFile file in new[] { shredded, whole })
        {
            Assert.Equal(rows.Select(r => r.Attributes["route"]?.AsString()), await file.Scan<Doc>().Select(r => r.Attributes["route"].AsString()).ToListAsync(Ct));
            Assert.Equal(rows.Select(r => r.Attributes["status"]?.AsInt64()), await file.Scan<Doc>().Select(r => r.Attributes["status"].AsInt64()).ToListAsync(Ct));
        }

        // The question reads each key's ranges, never a chunk's segment whole.
        int before = source.Ranges!.Count;
        Assert.Equal(1_500, await shredded.Scan<Doc>().Where(r => r.Attributes["status"].AsInt64() >= 202).CountAsync(Ct));
        long read = source.Ranges.Skip(before).Sum(range => range.Length);
        Assert.True(read < bytes.Length / 2, $"{read} bytes read of a file of {bytes.Length}");
    }

    private static Doc[] Rows()
    {
        Random random = new Random(8);
        return [.. Enumerable.Range(0, 6_000).Select(i => new Doc(i, i % 997 == 0 ? Variant.From("no object") : Variant.Object(
        [
            ("route", Variant.From($"/r/{random.Next(12)}")),
            ("score", i % 50 == 0 ? Variant.From((float)random.NextDouble()) : Variant.From(random.NextDouble())),
            .. (IEnumerable<(string, Variant)>)(i % 5 == 0 ? [] : [("status", i is > 2_000 and < 3_000 && i % 7 == 0 ? Variant.From("502") : Variant.From(200 + random.Next(4)))]),
            ("ok", Variant.From(i % 3 == 0)),
            ("other", Variant.From(i)),
        ])))];
    }

    private static async Task<byte[]> WriteAsync(Doc[] rows, VariantShredding? plan)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-keyreads-{Guid.NewGuid():N}.vortex");
        try
        {
            VortexWriteOptions options = new VortexWriteOptions
            {
                BlockRows = 1_000,
                ChunkTargetBytes = 1_024,
                ShreddedVariants = plan is null ? null : new Dictionary<string, VariantShredding> { [nameof(Doc.Attributes)] = plan },
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

    private static ValueTask<VortexFile> OpenAsync(byte[] bytes, out TestSegmentSource source)
    {
        source = new TestSegmentSource(bytes) { Ranges = [] };
        return VortexFile.OpenAsync(source, VortexOpenOptions.Default with { InitialReadBytes = 64 }, Ct);
    }

    [VortexRecord]
    public partial record struct Doc(long Id, Variant Attributes);

    [VortexRecord]
    public partial record struct RouteSum(string? Route, long Rows, long Statuses);
}
