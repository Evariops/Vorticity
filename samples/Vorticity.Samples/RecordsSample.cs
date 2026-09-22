using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace Vorticity.Samples;

public enum OrderStatus : byte
{
    Placed,
    Shipped,
    Delivered,
}

[VortexRecord]
public partial record struct Order(
    [VortexColumn("order_id")] long Id,
    [VortexColumn(Precision = 12, Scale = 2)] decimal Amount,
    [VortexColumn(Unit = TimeUnit.Milliseconds)] DateTime PlacedAt,
    [VortexColumn(TimeZone = "Europe/Paris")] DateTimeOffset DeliverBy,
    OrderStatus Status,
    string? Note,
    ReadOnlyMemory<int> Items,
    Address ShipTo)
{
    [VortexIgnore]
    public readonly decimal AmountWithTax => Amount * 1.2m;
}

[VortexRecord]
public partial record class Customer(long Id, string Name, DateOnly? Since);

[VortexRecord]
public partial struct Point
{
    public double X;
    public double Y;
}

[VortexRecord]
public partial class Shipment
{
    public long Id { get; set; }

    public TimeOnly Slot { get; init; }

    public string Carrier { get; set; } = string.Empty;
}

[VortexRecord]
public partial record struct OrderAmount(long Order_Id, decimal amount);

[VortexRecord]
public partial record struct OrderById(long Id, decimal Amount);

/// <summary>A record written by hand: the interface alone, with no generator and no sugar.</summary>
public readonly struct Pair : IVortexRecord<Pair>
{
    public Pair(int key, double value)
    {
        Key = key;
        Value = value;
    }

    public int Key { get; }

    public double Value { get; }

    public static VortexSchema Schema { get; } = [("key", VortexType.Int32), ("value", VortexType.Float64)];

    public static void ReadRows(Columns<Pair> columns, Span<Pair> rows)
    {
        ReadOnlySpan<int> keys = columns.Column<int>(0).Values;
        ReadOnlySpan<double> values = columns.Column<double>(1).Values;
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new Pair(keys[i], values[i]);
        }
    }

    public static void WriteRows(ColumnsBuilder<Pair> builder, ReadOnlySpan<Pair> rows)
    {
        Span<int> keys = builder.Column<int>(0).GetSpan(rows.Length);
        Span<double> values = builder.Column<double>(1).GetSpan(rows.Length);
        for (int i = 0; i < rows.Length; i++)
        {
            keys[i] = rows[i].Key;
            values[i] = rows[i].Value;
        }

        builder.Column<int>(0).Advance(rows.Length);
        builder.Column<double>(1).Advance(rows.Length);
    }
}

/// <summary>An amount in cents, stored as an <c>i64</c> under the extension id <c>acme.money</c>.</summary>
public readonly record struct Money(long Cents) : IVortexExtension<Money>
{
    public static string Id => "acme.money";

    public static VortexType StorageType => VortexType.Int64;

    public static Money FromStorage(ReadOnlySpan<byte> storage, ReadOnlySpan<byte> metadata) =>
        new Money(BinaryPrimitives.ReadInt64LittleEndian(storage));

    public static void ToStorage(in Money value, Span<byte> storage, ReadOnlySpan<byte> metadata) =>
        BinaryPrimitives.WriteInt64LittleEndian(storage, value.Cents);
}

[VortexRecord]
public partial record struct Invoice(long Number, Money Total);

internal static class Records
{
    internal static async Task RunAsync()
    {
        Console.WriteLine($"Order    {Order.Schema}");
        Console.WriteLine($"Customer {Customer.Schema}");
        Console.WriteLine($"Point    {Point.Schema}");
        Console.WriteLine($"Shipment {Shipment.Schema}");
        Console.WriteLine($"Pair     {Pair.Schema}");
        Console.WriteLine($"Invoice  {Invoice.Schema}");
        Console.WriteLine($"Order.ColumnNames.Id = \"{Order.ColumnNames.Id}\"");

        string path = Demo.Path("orders.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Order>(path))
        {
            await writer.WriteAsync<Order>(Orders(10_000));
            await writer.CompleteAsync();
        }

        await using VortexFile file = await VortexFile.OpenAsync(path);
        Order first = await file.Scan<Order>().ToRecordsAsync().FirstAsync();
        Console.WriteLine($"first row: {first.Id}, {first.Amount}, {first.PlacedAt:O}, {first.DeliverBy:O}, " +
            $"{first.Status}, {first.Note ?? "null"}, [{string.Join(", ", first.Items.ToArray())}], " +
            $"{first.ShipTo.Country}/{first.ShipTo.City ?? "null"}");

        long shipped = await file.Scan<Order>()
            .Where(o => o.Status == OrderStatus.Shipped && o.Amount > 100m)
            .CountAsync();
        Console.WriteLine($"shipped and above 100: {shipped} of {file.RowCount}");

        decimal total = 0;
        long notes = 0;
        await foreach (var (_, amount, _, _, status, note, _, shipTo) in file.Scan<Order>())
        {
            ReadOnlySpan<byte> statuses = status.Values;
            for (int i = 0; i < amount.Length; i++)
            {
                if (statuses[i] == (byte)OrderStatus.Delivered)
                {
                    total += amount[i];
                }
            }

            notes += note.Length - note.NullCount;
            _ = shipTo.Country;
        }

        Console.WriteLine($"delivered total {total}, {notes} notes");

        OrderAmount byCase = await file.Scan<OrderAmount>().ToRecordsAsync().FirstAsync();
        Console.WriteLine($"bound case-insensitively: Order_Id {byCase.Order_Id}, amount {byCase.amount}");

        try
        {
            await file.Scan<OrderById>().CountAsync();
        }
        catch (VortexSchemaException e)
        {
            Console.WriteLine($"a member with no column: {e.Message}");
        }

        string pairs = Demo.Path("pairs.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Pair>(pairs))
        {
            await writer.WriteAsync<Pair>(Enumerable.Range(0, 1_000).Select(i => new Pair(i, i / 4.0)).ToArray());
            await writer.CompleteAsync();
        }

        await using (VortexFile pairFile = await VortexFile.OpenAsync(pairs))
        {
            long above = await pairFile.Scan<Pair>().Where(p => p.Column<int>("key") >= 990).CountAsync();
            Pair last = await pairFile.Scan<Pair>().Rows(999).ToRecordsAsync().FirstAsync();
            Console.WriteLine($"hand-written record: {above} keys at or above 990, last ({last.Key}, {last.Value})");
        }

        await ExtensionAsync();
    }

    private static async Task ExtensionAsync()
    {
        string path = Demo.Path("invoices.vortex");
        try
        {
            await using VortexFileWriter refused = VortexSession.Default.CreateWriter<Invoice>(path);
        }
        catch (Exception e)
        {
            Console.WriteLine($"writing Money without registering it: {e.GetType().Name}: {e.Message}");
        }

        await using VortexSession session = VortexSession.Create(o => o.Extensions.Register<Money>());
        await using (VortexFileWriter writer = session.CreateWriter<Invoice>(path))
        {
            await writer.WriteAsync<Invoice>([new Invoice(1, new Money(1_999)), new Invoice(2, new Money(250_000))]);
            await writer.CompleteAsync();
        }

        await using (VortexFile file = await session.OpenAsync(path))
        {
            List<Invoice> invoices = await file.Scan<Invoice>().ToRecordsAsync().ToListAsync();
            long large = await file.Scan<Invoice>().Where(i => i.Total > new Money(100_000)).CountAsync();
            Console.WriteLine($"with the registration: {file.Schema}; {string.Join(", ", invoices)}; {large} above 1000.00");
        }

        await using VortexFile plain = await VortexFile.OpenAsync(path);
        Console.WriteLine($"without it, the schema reads {plain.Schema}");
        try
        {
            List<Invoice> rows = await plain.Scan<Invoice>().ToRecordsAsync().ToListAsync();
            Console.WriteLine($"and its rows read: {string.Join(", ", rows)}");
            long large = await plain.Scan<Invoice>().Where(i => i.Total > new Money(100_000)).CountAsync();
            Console.WriteLine($"and a filter on Money counts {large}");
        }
        catch (Exception e)
        {
            Console.WriteLine($"and reading Invoice throws {e.GetType().Name}: {e.Message}");
        }
    }

    private static Order[] Orders(int count)
    {
        DateTime start = new DateTime(2026, 9, 1, 8, 0, 0, DateTimeKind.Utc);
        Order[] orders = new Order[count];
        for (int i = 0; i < count; i++)
        {
            orders[i] = new Order(
                1_000 + i,
                decimal.Round(5m + (i % 400) * 0.75m, 2),
                start.AddMinutes(i),
                new DateTimeOffset(start.AddDays(3 + i % 4)),
                (OrderStatus)(i % 3),
                i % 5 == 0 ? "leave at the door" : null,
                Enumerable.Range(i % 7, 1 + i % 3).ToArray(),
                new Address(i % 2 == 0 ? "FR" : "BE", i % 9 == 0 ? null : Demo.Cities[i % Demo.Cities.Length]));
        }

        return orders;
    }
}
