using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

/// <summary>
/// The least and the greatest value of a decimal column, across a dataset's objects and across the datasets of a set: a
/// decimal's unscaled value past 64 bits is read as its bytes, one within them as an integer, and the two are ordered as
/// the numbers they are, never byte by byte nor by which came last.
/// </summary>
public sealed partial class DecimalExtremesTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task ADatasetsExtremesOrderItsObjectsWideValuesAsNumbers()
    {
        // At the default scale of 10, two and three billion are past 2^63 unscaled, five is not; the bytes of the two wide
        // values, least significant first, order three billion below two.
        await using MemoryObjectStore store = new();
        await using VortexDataset dataset = await DatasetAsync(store, [Two], [Three], [5m]);
        Assert.Equal(Three, await dataset.Scan<Payment>().MaxAsync(p => p.Amount, Ct));
        Assert.Equal(5m, await dataset.Scan<Payment>().MinAsync(p => p.Amount, Ct));

        await using MemoryObjectStore other = new();
        await using VortexDataset late = await DatasetAsync(other, [Two], [5m]);
        Assert.Equal(Two, await late.Scan<Payment>().MaxAsync(p => p.Amount, Ct));
    }

    [Fact]
    public async Task ASetsExtremesOrderItsDatasetsWideValuesAsNumbers()
    {
        await using MemoryObjectStore first = new();
        await using MemoryObjectStore second = new();
        await using VortexDataset low = await DatasetAsync(first, [1m, Two]);
        await using VortexDataset high = await DatasetAsync(second, [5m, Three]);
        VortexDatasetSet set = new(low, high);
        Assert.Equal(Three, await set.Scan<Payment>().MaxAsync(p => p.Amount, Ct));
        Assert.Equal(1m, await set.Scan<Payment>().MinAsync(p => p.Amount, Ct));

        VortexDatasetSet reversed = new(high, low);
        Assert.Equal(Three, await reversed.Scan<Payment>().MaxAsync(p => p.Amount, Ct));
        Assert.Equal(1m, await reversed.Scan<Payment>().MinAsync(p => p.Amount, Ct));

        await using MemoryObjectStore third = new();
        await using VortexDataset small = await DatasetAsync(third, [5m]);
        Assert.Equal(Two, await new VortexDatasetSet(low, small).Scan<Payment>().MaxAsync(p => p.Amount, Ct));
    }

    private const decimal Two = 2_000_000_000m;

    private const decimal Three = 3_000_000_000m;

    /// <summary>A dataset of an object per array of amounts.</summary>
    private static async Task<VortexDataset> DatasetAsync(IObjectStore store, params decimal[][] objects)
    {
        VortexDataset dataset = await VortexDataset.CreateAsync(store, Payment.Schema, new DatasetOptions(), Ct);
        long id = 0;
        foreach (decimal[] amounts in objects)
        {
            Payment[] rows = new Payment[amounts.Length];
            for (int i = 0; i < amounts.Length; i++)
            {
                rows[i] = new Payment(id++, amounts[i]);
            }

            await using ObjectDraft draft = dataset.StartObject();
            await draft.Writer.WriteAsync<Payment>(rows, Ct);
            await dataset.AppendAsync(draft, Ct);
        }

        return dataset;
    }

    [VortexRecord]
    public partial record struct Payment(long Id, decimal Amount);
}
