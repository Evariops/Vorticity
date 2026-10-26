// The public Variant: built from .NET values, fields, elements and JSON, walked by key and index,
// read as stored with As… and converted with To…, compared under the encoding's equivalence
// classes, and written and read back as a record's member.
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class VariantTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void TheDefaultIsTheVariantNull()
    {
        Variant none = default;
        Assert.True(none.IsNull);
        Assert.Equal(VariantKind.Null, none.Kind);
        Assert.Equal("null", none.ToJson());
        Assert.Equal(Variant.Null, none);
        Assert.Null(none.AsInt64());
        Assert.Equal(0, none.Count);
    }

    [Fact]
    public void AnIntegerTakesTheNarrowestWidthAndReadsAsAnInt64()
    {
        Assert.Equal(VariantKind.Int8, Variant.From(7L).Kind);
        Assert.Equal(VariantKind.Int16, Variant.From(300L).Kind);
        Assert.Equal(VariantKind.Int32, Variant.From(70_000L).Kind);
        Assert.Equal(VariantKind.Int64, Variant.From(5_000_000_000L).Kind);
        foreach (long value in new[] { 0L, -128, 127, 300, -70_000, long.MinValue, long.MaxValue })
        {
            Assert.Equal(value, Variant.From(value).AsInt64());
        }
    }

    [Fact]
    public void AsReadsTheStoredTypeAndToConverts()
    {
        Variant text = "502";
        Variant number = 502L;
        Variant real = 502.0;
        Assert.Null(text.AsInt64());
        Assert.Equal(502, text.ToInt64());
        Assert.Equal(502, number.AsInt64());
        Assert.Null(number.AsString());
        Assert.Null(real.AsInt64());
        Assert.Equal(502, real.ToInt64());
        Assert.Null(Variant.From(502.5).ToInt64());
        Assert.Equal(502.0, number.ToDouble());
        Assert.Equal(502.0, real.AsDouble());
        Assert.Null(text.AsDouble());
        Assert.Equal(true, Variant.From("TRUE").ToBool());
        Assert.Null(Variant.From("yes").ToBool());
        Assert.Equal(12.34m, Variant.From(12.34m).AsDecimal());
        Assert.Equal(12, Variant.From(12.00m).ToInt64());
        Assert.Null(Variant.From(12.5m).ToInt64());
    }

    [Fact]
    public void ThePrimitivesOfEveryTypeReadBack()
    {
        DateTimeOffset instant = new(2026, 10, 18, 12, 34, 56, 789, TimeSpan.Zero);
        DateTime local = new(2026, 10, 18, 12, 34, 56, 789, DateTimeKind.Unspecified);
        Guid id = Guid.Parse("5a5a5a5a-5a5a-4a5a-8a5a-5a5a5a5a5a5a");
        Assert.Equal(instant, Variant.From(instant).AsTimestamp());
        Assert.Equal(local, Variant.From(local).AsLocalTimestamp());
        Assert.Equal(instant.UtcDateTime, Variant.From(instant.UtcDateTime).AsTimestamp()!.Value.UtcDateTime);
        Assert.Equal(new DateOnly(2026, 10, 18), Variant.From(new DateOnly(2026, 10, 18)).AsDate());
        Assert.Equal(new TimeOnly(12, 34, 56), Variant.From(new TimeOnly(12, 34, 56)).AsTime());
        Assert.Equal(id, Variant.From(id).AsUuid());
        Assert.Equal(new byte[] { 1, 2, 3 }, Variant.From([1, 2, 3]).AsBinary()!.Value.ToArray());
        Assert.Equal(1.5f, Variant.From(1.5f).AsDouble());
        Assert.Equal(true, Variant.From(true).AsBool());
        string text = new('x', 200);
        Assert.Equal(text, Variant.From(text).AsString());
        Assert.Equal("\"" + text + "\"", Variant.From(text).ToJson());
    }

    [Fact]
    public void AnObjectWalksByWholeKeysAndAnArrayByIndex()
    {
        Variant body = Variant.Object(
            ("http.route", "/orders/{id}"),
            ("http", Variant.Object(("route", "/other"), ("status", 502L))),
            ("tags", Variant.Array("a", 2L, Variant.Null)));

        Assert.Equal(VariantKind.Object, body.Kind);
        Assert.Equal(3, body.Count);
        Assert.Equal(["http", "http.route", "tags"], body.GetKeys());
        Assert.Equal("/orders/{id}", body["http.route"]!.Value.AsString());
        Assert.Equal("/other", body["http"]!.Value["route"]!.Value.AsString());
        Assert.Equal(502, body["http"]!.Value["status"]!.Value.AsInt64());
        Assert.Null(body["http"]!.Value["missing"]);
        Assert.Null(body["http.route"]!.Value["route"]);
        Assert.True(body.Has("http.route"));
        Assert.False(body.Has("route"));
        Assert.Equal(2, body["tags"]!.Value[1]!.Value.AsInt64());
        Assert.True(body["tags"]!.Value[2]!.Value.IsNull);
        Assert.Null(body["tags"]!.Value[3]);
        Assert.Equal(
            """{"http":{"route":"/other","status":502},"http.route":"/orders/{id}","tags":["a",2,null]}""",
            body.ToJson());
    }

    [Fact]
    public void AnObjectRefusesARepeatedKey()
    {
        Assert.Throws<ArgumentException>(() => Variant.Object(("a", 1L), ("a", 2L)));
        Assert.Throws<ArgumentException>(() => Variant.FromJson("""{"a": 1, "a": 2}"""));
    }

    [Fact]
    public void JsonReadsAndWritesBackTheSameValue()
    {
        string json = """{"b":[1,-300,70000,5000000000,2.5,"x",true,false,null],"a":{"c":{}},"d":[]}""";
        Variant parsed = Variant.FromJson(json);
        Assert.Equal(
            """{"a":{"c":{}},"b":[1,-300,70000,5000000000,2.5,"x",true,false,null],"d":[]}""",
            parsed.ToJson());
        Assert.Equal(parsed, Variant.FromJson(parsed.ToJson()));
        Assert.Equal(VariantKind.Decimal, Variant.FromJson("123456789012345678901234567890").Kind);
        Assert.Throws<ArgumentException>(() => Variant.FromJson("{"));
        Assert.Throws<ArgumentException>(() => Variant.FromJson("1 2"));
    }

    [Fact]
    public void EqualityFollowsTheEquivalenceClasses()
    {
        Variant narrow = Variant.FromBytes(VariantEncoding.EmptyMetadata, VariantEncoding.Primitive(3, [7]));
        Variant wide = Variant.FromBytes(VariantEncoding.EmptyMetadata, VariantEncoding.Primitive(6, BitConverter.GetBytes(7L)));
        Assert.Equal(narrow, wide);
        Assert.Equal(narrow.GetHashCode(), wide.GetHashCode());
        Assert.Equal(Variant.From(7m), narrow);
        Assert.Equal(Variant.From(7.00m), Variant.From(7m));
        Assert.Equal(Variant.From(1.5f), Variant.From(1.5));
        Assert.NotEqual(Variant.From(7.0), narrow);
        Assert.NotEqual(Variant.From("7"), narrow);

        // The same object under two dictionaries: inside a larger object, its keys take other ids.
        Variant one = Variant.Object(("b", 1L), ("a", 2L));
        Variant other = Variant.Object(("a0", 0L), ("inner", Variant.FromJson("""{"a":2,"b":1}""")))["inner"]!.Value;
        Assert.NotEqual(one.Metadata.Length, other.Metadata.Length);
        Assert.Equal(one, other);
        Assert.Equal(one.GetHashCode(), other.GetHashCode());
        Assert.NotEqual(one, Variant.FromJson("""{"a":2,"b":1,"c":null}"""));
    }

    [Fact]
    public void BytesAreCheckedBeforeTheyAreTaken()
    {
        Variant body = Variant.FromJson("""{"k":[1,2]}""");
        Variant copy = Variant.FromBytes(body.Metadata.Span, body.Value.Span);
        Assert.Equal(body, copy);
        Assert.Throws<VortexFormatException>(() => Variant.FromBytes(VariantEncoding.EmptyMetadata, body.Value.Span));
        Assert.Throws<VortexFormatException>(() => Variant.FromBytes(body.Metadata.Span, [.. body.Value.Span, 0]));
        Assert.Throws<VortexFormatException>(() => Variant.FromBytes(body.Metadata.Span, body.Value.Span[..^1]));
    }

    [Fact]
    public async Task ARecordsVariantMembersAreWrittenAndReadBack()
    {
        Event[] rows =
        [
            new(1, Variant.FromJson("""{"http.route":"/a","status":200}"""), Variant.From(1.5)),
            new(2, Variant.Object(("user", Variant.Object(("id", 7L), ("name", "x")))), null),
            new(3, Variant.Null, Variant.Null),
            new(4, Variant.Array(1L, "two", 3.0), Variant.From(new DateOnly(2026, 10, 18))),
        ];

        string path = Path.Combine(Path.GetTempPath(), $"vorticity-variant-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Event>(path))
            {
                await writer.WriteAsync<Event>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            Assert.Equal(VortexTypeKind.Variant, file.Schema[file.Schema.IndexOf(nameof(Event.Attributes))].Type.Kind);
            Assert.True(file.Schema[file.Schema.IndexOf(nameof(Event.Extra))].Type.IsNullable);
            List<Event> read = await file.Scan<Event>().ToRecordsAsync(Ct).ToListAsync(Ct);
            Assert.Equal(rows.Length, read.Count);
            for (int i = 0; i < rows.Length; i++)
            {
                Assert.Equal(rows[i].Id, read[i].Id);
                Assert.Equal(rows[i].Attributes, read[i].Attributes);
                Assert.Equal(rows[i].Extra, read[i].Extra);
            }

            Assert.Null(read[1].Extra);
            Assert.True(read[2].Extra!.Value.IsNull);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [VortexRecord]
    public partial record struct Event(long Id, Variant Attributes, Variant? Extra);
}
