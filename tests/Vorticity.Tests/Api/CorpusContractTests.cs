using System;
using System.Collections.Generic;
using System.Globalization;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Vorticity.Tests.Scan;
using Xunit;

namespace Vorticity.Tests.Api;

/// <summary>
/// The promises a scan makes that hold for any file, checked over every file of
/// the conformance corpus this build reads, through the tool path a caller without a record uses.
/// </summary>
[Trait("Category", "ApiContract")]
public sealed class CorpusContractTests
{
    private const int Alignment = 64;

    [Fact]
    public async Task EveryValuesSpanOfTheCorpusIsContiguousAndAligned()
    {
        List<string> failures = [];
        int spans = 0;
        int files = 0;
        foreach (CorpusEntry entry in Readable())
        {
            files++;
            await using VortexFile file = await VortexFile.OpenAsync(entry.Path, TestContext.Current.CancellationToken);
            int batchIndex = 0;
            await foreach (BatchView batch in file.Scan().WithCancellation(TestContext.Current.CancellationToken))
            {
                for (int field = 0; field < batch.Schema.Count; field++)
                {
                    VortexType type = batch.Schema[field].Type;
                    bool list = type.Kind is VortexTypeKind.List or VortexTypeKind.FixedSizeList;
                    string? wrong = Check(batch, field, list ? type.ElementType! : type, list, ref spans);
                    if (wrong is not null)
                    {
                        failures.Add($"{entry.Id}, batch {batchIndex}, column {field} ({type}): {wrong}");
                    }
                }

                batchIndex++;
            }
        }

        Assert.True(files > 100, $"only {files} corpus files were read");
        Assert.True(spans > 1000, $"only {spans} Values spans were checked");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public async Task EveryCorpusFileReadsWhatItsPlanSaid()
    {
        List<string> failures = [];
        int files = 0;
        foreach (CorpusEntry entry in Readable())
        {
            await using VortexFile file = await VortexFile.OpenAsync(entry.Path, TestContext.Current.CancellationToken);
            await Compare(entry.Id, file.Scan(), failures);
            if (file.Schema.Count > 1)
            {
                await Compare(entry.Id + " (first column)", file.Scan(file.Schema[0].Name), failures);
            }

            files++;
        }

        Assert.True(files > 100, $"only {files} corpus files were read");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static async Task Compare(string id, Vorticity.Scan scan, List<string> failures)
    {
        ScanPlan plan = await scan.ExplainAsync(TestContext.Current.CancellationToken);
        await foreach (BatchView batch in scan.WithCancellation(TestContext.Current.CancellationToken))
        {
            GC.KeepAlive(batch.RowCount);
        }

        ScanStatistics statistics = scan.Statistics;
        if (plan.Segments != statistics.Requests || plan.BytesToRead != statistics.BytesRequested || plan.LiveBlocks != statistics.BlocksDecoded)
        {
            failures.Add(string.Create(
                CultureInfo.InvariantCulture,
                $"{id}: planned {plan.Segments} segments, {plan.BytesToRead} B, {plan.LiveBlocks} live blocks; ran {statistics.Requests} requests, {statistics.BytesRequested} B, {statistics.BlocksDecoded} blocks decoded"));
        }
    }

    /// <summary>Every corpus file this build reads, with its schema in the file.</summary>
    private static IEnumerable<CorpusEntry> Readable()
    {
        foreach (CorpusEntry entry in CorpusManifest.InScope())
        {
            if (entry.HasDTypeSegment)
            {
                yield return entry;
            }
        }
    }

    /// <summary>
    /// Checks the <c>Values</c> of column <paramref name="field"/>, or of its lists' elements, when
    /// its type has them: a number, or a date or time read as the number it is stored as.
    /// </summary>
    private static string? Check(BatchView batch, int field, VortexType type, bool list, ref int spans)
    {
        VortexType t = type.NonNullable;
        if (t.Kind == VortexTypeKind.Extension && t.ExtensionId != VortexType.Uuid.ExtensionId
            && t.StorageType is { Kind: VortexTypeKind.Primitive } storage)
        {
            t = storage.NonNullable;
        }

        return t switch
        {
            _ when t == VortexType.Int8 => Values<sbyte>(batch, field, list, ref spans),
            _ when t == VortexType.Int16 => Values<short>(batch, field, list, ref spans),
            _ when t == VortexType.Int32 => Values<int>(batch, field, list, ref spans),
            _ when t == VortexType.Int64 => Values<long>(batch, field, list, ref spans),
            _ when t == VortexType.UInt8 => Values<byte>(batch, field, list, ref spans),
            _ when t == VortexType.UInt16 => Values<ushort>(batch, field, list, ref spans),
            _ when t == VortexType.UInt32 => Values<uint>(batch, field, list, ref spans),
            _ when t == VortexType.UInt64 => Values<ulong>(batch, field, list, ref spans),
            _ when t == VortexType.Float16 => Values<Half>(batch, field, list, ref spans),
            _ when t == VortexType.Float32 => Values<float>(batch, field, list, ref spans),
            _ when t == VortexType.Float64 => Values<double>(batch, field, list, ref spans),
            _ => null,
        };
    }

    private static string? Values<T>(BatchView batch, int field, bool list, ref int spans)
        where T : unmanaged, IBinaryNumber<T>
    {
        Column<T?> column = list ? batch.Column<ReadOnlyMemory<T?>>(field).Elements : batch.Column<T?>(field);
        ReadOnlySpan<T> values = column.Values;
        spans++;
        if (values.Length != column.Length)
        {
            return $"Values holds {values.Length} of {column.Length} rows";
        }

        return Address(values) % Alignment == 0 ? null : $"Values starts at {Address(values) % Alignment} bytes past a 64-byte boundary";
    }

    private static unsafe nuint Address<T>(ReadOnlySpan<T> values)
        where T : unmanaged =>
        (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(values));
}
