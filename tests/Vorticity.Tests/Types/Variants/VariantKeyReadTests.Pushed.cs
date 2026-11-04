using System;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Types.Variants;
using Xunit;

namespace Vorticity.Tests.Types.Variants;

public sealed partial class VariantKeyReadTests
{
    private static readonly VariantShredding Pushed = VariantShredding.Object(
        ("route", VariantShredding.Primitive(VariantClass.String)),
        ("rare", VariantShredding.Primitive(VariantClass.Integer)));

    /// <summary>
    /// A count under a filter on a shredded key, a route on every row or a rare number on one row in fifty: the filter is
    /// offered to the key's typed column, whose encoding answers, its dictionary or its runs, a row without the key
    /// unknown, and no value is decoded, the count the rows' own; the records it keeps are those the rows keep. Without the
    /// typed column answering, the count decodes every route.
    /// </summary>
    [Fact]
    public async Task AFilterOnAShreddedKeyIsAnsweredByItsTypedColumn()
    {
        Random random = new Random(12);
        Doc[] rows = [.. Enumerable.Range(0, 6_000).Select(i => new Doc(i, Variant.Object(
        [
            ("route", Variant.From($"/r/{random.Next(12)}")),
            .. (System.Collections.Generic.IEnumerable<(string, Variant)>)(i % 50 == 0 ? [("rare", Variant.From((long)random.Next(1_000_000)))] : []),
        ])))];
        await using VortexFile file = await OpenAsync(await WriteAsync(rows, Pushed), out _);

        long routed = rows.Count(static r => r.Attributes["route"]?.AsString() == "/r/5");
        long rare = rows.Count(static r => r.Attributes["rare"]?.AsInt64() >= 900_000);
        Assert.True(routed > 0 && rare > 0);

        // The first count of the file decodes its zone tables; the ones after decode nothing of the filter's key.
        Assert.Equal(routed, await file.Scan<Doc>().Where(r => r.Attributes["route"].AsString() == "/r/5").CountAsync(Ct));
        Scan<Doc> byRoute = file.Scan<Doc>().Where(r => r.Attributes["route"].AsString() == "/r/5");
        Assert.Equal(routed, await byRoute.CountAsync(Ct));
        Assert.Equal(0, byRoute.Counters.ValuesDecoded);

        Scan<Doc> byRare = file.Scan<Doc>().Where(r => r.Attributes["rare"].AsInt64() >= 900_000);
        Assert.Equal(rare, await byRare.CountAsync(Ct));
        Assert.Equal(0, byRare.Counters.ValuesDecoded);

        Doc[] kept = [.. rows.Where(static r => r.Attributes["rare"]?.AsInt64() >= 900_000)];
        Assert.Equal(kept, await file.Scan<Doc>().Where(r => r.Attributes["rare"].AsInt64() >= 900_000).ToRecordsAsync(Ct).ToListAsync(Ct));
    }
}
