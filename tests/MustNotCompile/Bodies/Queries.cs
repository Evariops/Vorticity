using System.Linq;
using System.Threading.Tasks;
using Vorticity;

namespace MustNotCompile.Cases;

// Where a query over one table ends: what docs/design/16-queries.md §1 says does not compile.
internal static class Queries
{
    // After `into g` the row's probe is out of scope: a result is a key or an aggregate.
    internal static object ColumnNeitherKeyNorAggregated(VortexFile file) =>
        from r in file.Scan<Reading>()
        group r by r.City into g
        select (g.Key, r.Celsius); // expect: CS0103

    // One table: there is no Join.
    internal static object Join(VortexFile file, VortexFile other) =>
        from r in file.Scan<Reading>() // expect: CS1936
        join o in other.Scan<Reading>() on r.City equals o.City
        select r;

    // A builder describes a query and runs nothing: there is nothing to await.
    internal static async Task AwaitABuilder(VortexFile file) =>
        await file.Scan<Reading>().GroupBy(r => r.City); // expect: CS1061

    // A group is not a value: a Select says what of it to deliver.
    internal static async Task EnumerateGroups(VortexFile file)
    {
        await foreach (var group in file.Scan<Reading>().GroupBy(r => r.City)) // expect: CS8411
        {
            _ = group;
        }
    }

    // Several values have no .NET type until a record gives them one: As<TRecord>() reads them.
    internal static async Task EnumerateSeveralValues(VortexFile file)
    {
        await foreach (var group in file.Scan<Reading>().GroupBy(r => r.City).Select(g => (g.Key, g.Count()))) // expect: CS8411
        {
            _ = group;
        }
    }

    // The answers of a whole scan, several of them, go into the record AggAsync<TResult> names.
    internal static async Task<object> SeveralAnswersWithoutARecord(VortexFile file) =>
        await file.Scan<Reading>().AggAsync(a => (a.Count(), a.Max(r => r.Day))); // expect: CS0411

    // Computing with results is C# after the sink, not the plan's.
    internal static object ArithmeticOnAResult(VortexFile file) =>
        file.Scan<Reading>().GroupBy(r => r.City).Select(g => (g.Key, g.Count() * 2)); // expect: CS0619

    // A key component that is not a column: the same for every row, it groups nothing.
    internal static object LiteralInAKey(VortexFile file) =>
        file.Scan<Reading>().GroupBy(r => (r.City, 42)); // expect: VX1009 "Component 2"

    // The query syntax is the same call.
    internal static object LiteralInAQueryKey(VortexFile file) =>
        from r in file.Scan<Reading>()
        group r by (r.Day, "all") into g // expect: VX1009 "Component 2"
        select (g.Key.Day, g.Count());
}
