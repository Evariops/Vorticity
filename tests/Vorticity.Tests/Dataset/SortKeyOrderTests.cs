// The sort key's encoding (spec A, 2.3; A2): a layout's components worked out of a row (a column as it is, a truncated
// instant, a bucket of an integer) and encoded so that the bytes of two keys order as their components do, nulls last.
//
// THE ORACLE IS THE COMPONENTS: each worked out here from the row's values, compared component by component, a null after
// every value, a double by the order of its bits (-NaN first, NaN last, -0.0 before 0.0).
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.RowEncoding;
using Vorticity.Scanning;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class SortKeyOrderTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// 20 000 rows of random instants, services (the empty one and a long one among them), integers and prices, nulls and
    /// NaNs among them, keyed by the hour, the service, a bucket of ten of the integer and the price: for 200 000 pairs
    /// drawn at random, the encoded keys order as the components do.
    /// </summary>
    [Fact]
    public async Task EncodedKeysOrderAsTheirComponentsNullsLast()
    {
        const int Rows = 20_000;
        Random random = new Random(9);
        DateTime epoch = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        string[] services = ["", "a", "ab", "b", "api", "api-gateway-of-a-long-name", "z"];
        Row[] rows = new Row[Rows];
        for (int i = 0; i < Rows; i++)
        {
            rows[i] = new Row(
                epoch.AddSeconds(random.Next(-100_000, 100_000)),
                random.Next(10) == 0 ? null : services[random.Next(services.Length)],
                random.Next(8) == 0 ? null : random.Next(-500, 500),
                random.Next(20) == 0 ? double.NaN : Math.Round((random.NextDouble() - 0.5) * 1000, 1));
        }

        string path = Path.Combine(Path.GetTempPath(), $"vorticity-sortkey-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path))
            {
                await writer.WriteAsync<Row>(rows, Ct);
                await writer.CompleteAsync(Ct);
            }

            DatasetLayout layout = new DatasetLayout
            {
                SortKey =
                [
                    LayoutValue.Column(nameof(Row.At)).Truncate(CalendarUnit.Hour),
                    LayoutValue.Column(nameof(Row.Service)),
                    LayoutValue.Column(nameof(Row.Small)).Bucket(10L),
                    LayoutValue.Column(nameof(Row.Price)),
                ],
            };

            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            LayoutPlan plan = LayoutPlan.Of(layout, file.DType)!;
            SortKey key = SortKey.Of(plan, partition: false, components: plan.Components.Count);
            List<byte[]> keys = [];
            await foreach (RecordBatch batch in file.ScanBuilder().ExecuteAsync().WithCancellation(Ct))
            {
                using RowKeys encoded = key.Encode(batch);
                for (int i = 0; i < batch.RowCount; i++)
                {
                    keys.Add(encoded.Row(i).ToArray());
                }
            }

            Assert.Equal(Rows, keys.Count);
            List<string> failures = [];
            for (int probe = 0; probe < 200_000; probe++)
            {
                int a = random.Next(Rows);
                int b = random.Next(Rows);
                int encoded = Math.Sign(keys[a].AsSpan().SequenceCompareTo(keys[b]));
                int computed = Math.Sign(Compare(rows[a], rows[b]));
                if (encoded != computed)
                {
                    failures.Add($"{rows[a]} against {rows[b]}: the keys say {encoded}, the components {computed}");
                }
            }

            Assert.True(failures.Count == 0, $"{failures.Count} pairs disagree:\n{string.Join('\n', failures.GetRange(0, Math.Min(5, failures.Count)))}");
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    private static int Compare(Row left, Row right)
    {
        int order = Hour(left.At).CompareTo(Hour(right.At));
        if (order != 0)
        {
            return order;
        }

        order = Nullable(left.Service, right.Service, static (x, y) => string.CompareOrdinal(x, y));
        if (order != 0)
        {
            return order;
        }

        order = Nullable(Bucket(left.Small), Bucket(right.Small), static (x, y) => x!.Value.CompareTo(y!.Value));
        if (order != 0)
        {
            return order;
        }

        // A double's order as its bits sort: -NaN first, NaN last, -0.0 before 0.0.
        return Total(left.Price).CompareTo(Total(right.Price));
    }

    private static long Hour(DateTime at) => (long)Math.Floor((at - DateTime.UnixEpoch).TotalHours);

    private static int? Bucket(int? value) => value is { } v ? (int)(Math.Floor(v / 10.0) * 10) : null;

    private static long Total(double value)
    {
        long bits = BitConverter.DoubleToInt64Bits(value);
        return bits ^ ((bits >> 63) & long.MaxValue);
    }

    private static int Nullable<T>(T? left, T? right, Func<T?, T?, int> compare)
    {
        if (left is null || right is null)
        {
            return left is null ? (right is null ? 0 : 1) : -1;
        }

        return compare(left, right);
    }

    [VortexRecord]
    public partial record struct Row(DateTime At, string? Service, int? Small, double Price);
}
