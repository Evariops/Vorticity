// What a group by allocates for the lists and the variants of its chosen rows, once its process is warm, on every
// thread: the rows are kept as rows and gathered into the result, so a list thirty-two times longer costs the same
// objects, whatever bytes its elements take.
using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Aggregation;

[Collection(nameof(AllocationCollection))]
[Trait("Category", "RunsAlone")]
public sealed partial class ChosenRowAllocationTests
{
    private const int Rows = 100_000;

    private const int Groups = 1_000;

    private const int WarmUp = 3;

    private const int Runs = 5;

    /// <summary>
    /// Lists of two elements and of sixty-four, a group's first and last rows chosen, at one lane: the warm floor of the
    /// longer lists is within 16 KiB of the shorter's, where 2 000 lists of 62 more texts each made as values would be
    /// 124 000 strings, some 4 MB.
    /// </summary>
    [Fact]
    public async Task AChosenListAllocatesTheSameWhateverItsLength()
    {
        ReleaseOnlyCeilings.Require();
        string few = await WriteAsync(elements: 2);
        string many = await WriteAsync(elements: 64);
        try
        {
            long short_ = await FloorAsync(few);
            long long_ = await FloorAsync(many);
            Assert.True(
                long_ - short_ < 16 * 1024,
                string.Create(CultureInfo.InvariantCulture, $"lists of 64 elements allocated {long_:N0} B, of 2 elements {short_:N0} B"));
        }
        finally
        {
            System.IO.File.Delete(few);
            System.IO.File.Delete(many);
        }
    }

    /// <summary>The least a warm query allocates on every thread, of <see cref="Runs"/> after <see cref="WarmUp"/>.</summary>
    private static async Task<long> FloorAsync(string path)
    {
        await using VortexSession session = VortexSession.Create(options => options.MaxDegreeOfParallelism = 1);
        await using VortexFile file = await session.OpenAsync(path, cancellationToken: Ct);
        for (int i = 0; i < WarmUp; i++)
        {
            Assert.Equal(Groups, await QueryAsync(file));
        }

        long floor = long.MaxValue;
        for (int i = 0; i < Runs; i++)
        {
            long before = GC.GetTotalAllocatedBytes(precise: true);
            int groups = await QueryAsync(file);
            floor = Math.Min(floor, GC.GetTotalAllocatedBytes(precise: true) - before);
            Assert.Equal(Groups, groups);
        }

        return floor;
    }

    /// <summary>Each group's first and last lists and its last attributes, read as columns: the groups read.</summary>
    private static async Task<int> QueryAsync(VortexFile file)
    {
        int groups = 0;
        await foreach (Columns<Ends> batch in file.Scan<Item>()
            .GroupBy(r => r.Group)
            .Select(g => (g.Key, g.First().Names, g.Last().Names, g.Last().Attributes))
            .As<Ends>()
            .WithCancellation(Ct))
        {
            groups += batch.RowCount;
        }

        return groups;
    }

    private static async Task<string> WriteAsync(int elements)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "chosen-allocations");
        Directory.CreateDirectory(directory);
        string path = Path.Combine(directory, $"items-{elements}-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        string[] names = [.. Enumerable.Range(0, 97).Select(i => string.Create(CultureInfo.InvariantCulture, $"name-{i:D4}"))];
        Item[] rows = new Item[Rows];
        for (int row = 0; row < Rows; row++)
        {
            string[] list = new string[elements];
            for (int e = 0; e < elements; e++)
            {
                list[e] = names[(row + e) % names.Length];
            }

            rows[row] = new Item(row % Groups, list, Variant.Object(("n", Variant.From(row)), ("name", Variant.From(names[row % names.Length]))));
        }

        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Item>(path))
        {
            await writer.WriteAsync<Item>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        return path;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Item(int Group, ReadOnlyMemory<string> Names, Variant Attributes);

    [VortexRecord]
    public partial record struct Ends(int Group, ReadOnlyMemory<string> First, ReadOnlyMemory<string> Last, Variant Attributes);
}
