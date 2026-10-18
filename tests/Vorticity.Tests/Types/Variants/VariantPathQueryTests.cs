// Paths of a variant column in typed queries: a key or an index walks into each row's value, As…
// reads the type stored and To… converts, a path absent or of another type is null under the
// three-valued logic, Has is never unknown, and a path filters, groups and selects like a column,
// with the same answers whether the variant is shredded or not.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class VariantPathQueryTests
{
    private static readonly DateTimeOffset At = new(2026, 10, 18, 12, 0, 0, TimeSpan.Zero);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Event[] Rows() =>
    [
        new(1, Variant.FromJson("""{"status": 200, "route": "/a", "http.route": "/x"}"""), Variant.FromJson("""{"k": 1}""")),
        new(2, Variant.FromJson("""{"status": 502, "route": "/b", "http": {"route": "/y"}}"""), null),
        new(3, Variant.FromJson("""{"status": "502", "route": "/a"}"""), Variant.FromJson("""{"k": 2}""")),
        new(4, Variant.FromJson("""{"route": "/b"}"""), null),
        new(5, Variant.FromJson("""{"status": 70000, "route": "/a", "tags": ["t1", "t2"]}"""), Variant.Null),
        new(6, Variant.From(42L), Variant.FromJson("""{"j": 1}""")),
        new(7, Variant.Null, null),
        new(8, Variant.Object(("status", 503.0), ("ok", true), ("at", Variant.From(At))), Variant.FromJson("""{"k": 3}""")),
    ];

    [Fact]
    public async Task AsReadsTheStoredTypeAndToConverts()
    {
        await WithFileAsync(async file =>
        {
            Assert.Equal([2, 5], await IdsAsync(file, r => r.Attributes["status"].AsInt64() >= 500));
            Assert.Equal([3], await IdsAsync(file, r => r.Attributes["status"].AsString() == "502"));
            Assert.Equal([2, 3, 5, 8], await IdsAsync(file, r => r.Attributes["status"].ToInt64() >= 500));
            Assert.Equal([8], await IdsAsync(file, r => r.Attributes["status"].AsDouble() > 500));
            Assert.Equal([1, 2, 3, 5, 8], await IdsAsync(file, r => r.Attributes["status"].ToDouble() > 100));
            Assert.Equal([8], await IdsAsync(file, r => r.Attributes["ok"].AsBool() == true));
            Assert.Equal([8], await IdsAsync(file, r => r.Attributes["at"].AsTimestamp() >= At));
            Assert.Empty(await IdsAsync(file, r => r.Attributes["at"].AsTimestamp() > At));
        });
    }

    [Fact]
    public async Task APathAbsentOrOfAnotherTypeIsNullAndHasIsNeverUnknown()
    {
        await WithFileAsync(async file =>
        {
            Assert.Equal([3, 4, 6, 7, 8], await IdsAsync(file, r => r.Attributes["status"].AsInt64().IsNull));
            Assert.Equal([1, 2, 5], await IdsAsync(file, r => r.Attributes["status"].AsInt64().IsNotNull));

            // Unknown stays unknown under a negation: only the integers below 500 are kept.
            Assert.Equal([1], await IdsAsync(file, r => !(r.Attributes["status"].AsInt64() >= 500)));

            Assert.Equal([1, 2, 3, 5, 8], await IdsAsync(file, r => r.Attributes.Has("status")));
            Assert.Equal([4, 6, 7], await IdsAsync(file, r => !r.Attributes.Has("status")));
            Assert.Equal([1, 2, 3, 5, 8], await IdsAsync(file, r => r.Attributes["status"].Exists));

            // A null row reaches no path, so its Has is false and never null.
            Assert.Equal([1, 3, 8], await IdsAsync(file, r => r.Extra.Has("k")));
            Assert.Equal([2, 4, 5, 6, 7], await IdsAsync(file, r => !r.Extra.Has("k")));
            Assert.Equal([3, 8], await IdsAsync(file, r => r.Extra["k"].AsInt64() >= 2));
        });
    }

    [Fact]
    public async Task AKeyIsWholeAndAPathWalksObjectsAndArrays()
    {
        await WithFileAsync(async file =>
        {
            Assert.Equal([1], await IdsAsync(file, r => r.Attributes["http.route"].AsString() == "/x"));
            Assert.Equal([2], await IdsAsync(file, r => r.Attributes["http"]["route"].AsString() == "/y"));
            Assert.Empty(await IdsAsync(file, r => r.Attributes["http"]["route"].AsString() == "/x"));
            Assert.Equal([5], await IdsAsync(file, r => r.Attributes["tags"][1].AsString() == "t2"));
            Assert.Empty(await IdsAsync(file, r => r.Attributes["tags"][2].Exists));
            Assert.Equal([1, 3, 5], await IdsAsync(file, r => r.Attributes["route"].AsString().In("/a")));
            Assert.Equal([3, 5], await IdsAsync(file, r => r.Id > 2 & r.Attributes["route"].AsString() == "/a"));
            Assert.Equal([1, 3, 4, 5, 6, 7, 8], await IdsAsync(file, r => r.Id != 2 | r.Attributes["route"].AsString() == "/z"));
        });
    }

    [Fact]
    public async Task APathGroupsAndSelectsLikeAColumn()
    {
        await WithFileAsync(async file =>
        {
            List<long?> statuses = await file.Scan<Event>().Select(r => r.Attributes["status"].AsInt64()).ToListAsync(Ct);
            Assert.Equal([200, 502, null, null, 70000, null, null, null], statuses);

            List<RouteCount> routes = await file.Scan<Event>()
                .GroupBy(r => r.Attributes["route"].AsString())
                .Select(g => (g.Key, g.Count()))
                .As<RouteCount>().ToRecordsAsync(Ct).ToListAsync(Ct);
            Assert.Equal(
                [("/a", 3L), ("/b", 2L), (null, 3L)],
                routes.Select(r => (r.Route, r.Count)).OrderBy(r => r.Route ?? "~", StringComparer.Ordinal));

            // A path is an aggregate's input: the string "502" and the double 503.0 are not integers.
            List<RouteSum> sums = await file.Scan<Event>()
                .GroupBy(r => r.Attributes["route"].AsString())
                .Select(g => (g.Key, g.Sum(x => x.Attributes["status"].AsInt64())))
                .As<RouteSum>().ToRecordsAsync(Ct).ToListAsync(Ct);
            Assert.Equal(200 + 70000, sums.Single(s => s.Route == "/a").Sum);
            Assert.Equal(502, sums.Single(s => s.Route == "/b").Sum);
        });
    }

    [Fact]
    public async Task APathIsComparedThroughWhatItIsReadAs()
    {
        await WithFileAsync(async file =>
        {
            InvalidOperationException refused = await Assert.ThrowsAsync<InvalidOperationException>(async () =>
                await IdsAsync(file, r => r.Attributes["status"] == Variant.From(1L)));
            Assert.Contains("AsInt64()", refused.Message, StringComparison.Ordinal);
        });
    }

    /// <summary>The same questions over the reference's shredded files and their unshredded twins give the same rows.</summary>
    [Theory]
    [InlineData("object")]
    [InlineData("nested")]
    [InlineData("chunks")]
    public async Task APathAnswersTheSameShreddedOrNot(string name)
    {
        string directory = Path.GetFullPath(Path.Combine(DataDirectory(), name + ".plain.vortex"));
        List<string> plain = await AnswersAsync(directory);
        Assert.Equal(plain, await AnswersAsync(directory.Replace(".plain.", ".shredded.", StringComparison.Ordinal)));
        Assert.Equal(plain, await AnswersAsync(directory.Replace(".plain.", ".parquet.", StringComparison.Ordinal)));
    }

    private static async Task<List<string>> AnswersAsync(string path)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
        List<string> answers = [];
        answers.Add(string.Join(",", await file.Scan<Doc>().Select(r => r.V["status"].AsInt64()).ToListAsync(Ct)));
        answers.Add(string.Join(",", await file.Scan<Doc>().Select(r => r.V["route"].AsString()).ToListAsync(Ct)));
        answers.Add(string.Join(",", await file.Scan<Doc>().Select(r => r.V["http"]["method"].AsString()).ToListAsync(Ct)));
        answers.Add(string.Join(",", await file.Scan<Doc>().Select(r => r.V["service"].AsString()).ToListAsync(Ct)));
        answers.Add((await file.Scan<Doc>().Where(r => r.V.Has("status")).Select(r => r.V["user"].AsString()).ToListAsync(Ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        answers.Add((await file.Scan<Doc>().Where(r => r.V["status"].ToInt64() >= 300).Select(r => r.V["route"].AsString()).ToListAsync(Ct)).Count.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return answers;
    }

    private static string DataDirectory([System.Runtime.CompilerServices.CallerFilePath] string source = "") =>
        Path.Combine(Path.GetDirectoryName(source)!, "..", "..", "Data", "shredded-variants");

    private static async Task<List<long>> IdsAsync(VortexFile file, Func<Probe<Event>, Predicate> filter) =>
        await file.Scan<Event>().Where(filter).Select(r => r.Id).ToListAsync(Ct);

    private static async Task WithFileAsync(Func<VortexFile, Task> body)
    {
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-variant-paths-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Event>(path))
            {
                await writer.WriteAsync<Event>(Rows(), Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            await body(file);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [VortexRecord]
    public partial record struct Event(long Id, Variant Attributes, Variant? Extra);

    [VortexRecord]
    public partial record struct Doc(Variant V);

    [VortexRecord]
    public partial record struct RouteCount(string? Route, long Count);

    [VortexRecord]
    public partial record struct RouteSum(string? Route, long? Sum);
}
