using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The search of one object's sparse index alone, in memory (<c>--micro keyindex</c>): an index of 3 300 granules, an
/// object of a gibibyte, under the key <c>(Service, Hour(At), Host)</c> of 600 services, searched for a service, a service
/// and an hour, and a host, which bounds no leading component. A pruning a question, searching 64 objects, each index
/// taken over its bytes as a read hands them, searched and given back, as a question does; the time, the boxes judged, the
/// keys compared and the bytes allocated an object, and the bytes an index holds besides those read of it.
/// </summary>
internal static class KeyIndexMicro
{
    private const int Granules = 3_300;

    private const int Services = 600;

    private const int Searches = 2_048;

    /// <summary>The objects a question searches, each a copy of the index.</summary>
    private const int Objects = 64;

    private static readonly DateTime Start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);

    /// <summary>
    /// With <paramref name="directory"/>, the sparse indexes of every object of the dataset there instead: their granules, the
    /// bytes their pages take on disk, and those they hold once read.
    /// </summary>
    internal static async Task<int> Run(int rounds, string? directory = null)
    {
        if (directory is not null)
        {
            return await HeldAsync(directory);
        }

        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, KeyIndexRow.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout
            {
                SortKey = [LayoutValue.Column(nameof(KeyIndexRow.Service)), LayoutValue.Column(nameof(KeyIndexRow.At)).Truncate(CalendarUnit.Hour), LayoutValue.Column(nameof(KeyIndexRow.Host))],
            },
        });
        DatasetSchema schema = dataset.Snapshot.Schema;
        Vorticity.Dataset.SortKey key = Vorticity.Dataset.SortKey.Of(schema.Layout!, partition: false, 3);
        List<byte[]> keys = [];
        for (int granule = 0; granule < Granules; granule++)
        {
            int service = (int)((long)granule * Services / Granules);
            int within = granule - (int)(((long)service * Granules + Services - 1) / Services);
            keys.Add(key.Encode([Text($"svc-{service:D4}"), Micros(Start.AddHours(within * 4)), Text($"host-{(within * 7) % 100:D4}")]));
        }

        (byte[] table, byte[] pages) = GranuleKeys.Build(8_192, (long)Granules * 8_192, 3, keys, keys[^1]);
        DateTime eight = Start.AddHours(8);
        (string Name, VortexExpr Filter)[] filters =
        [
            ("service", Eq(nameof(KeyIndexRow.Service), Text("svc-0300"))),
            ("service and an hour", Expr.And(Eq(nameof(KeyIndexRow.Service), Text("svc-0300")),
                Expr.And(Expr.Ge(Expr.Field(nameof(KeyIndexRow.At)), Expr.Literal(Micros(eight))), Expr.Lt(Expr.Field(nameof(KeyIndexRow.At)), Expr.Literal(Micros(eight.AddHours(1))))))),
            ("host", Eq(nameof(KeyIndexRow.Host), Text("host-0042"))),
        ];

        // The same bytes read again for each object a question searches: a pruning searches each object once, its index taken
        // over its bytes and given back.
        long held;
        using (GranuleKeys whole = Index(table, pages))
        {
            whole.DecodeAll();
            held = whole.HeldBytes;
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"keyindex: {Granules:N0} granules, {table.Length + pages.Length:N0} bytes read, {(double)(table.Length + pages.Length) / Granules:0.0} a granule; {held:N0} bytes held besides, every page reached"));

        // What an index costs past its search: taken over its bytes and given back, then with one page reached.
        byte[] middle = keys[Granules / 2];
        double taken = double.MaxValue;
        double reached = double.MaxValue;
        for (int round = 0; round < Math.Max(1, rounds); round++)
        {
            long started = Stopwatch.GetTimestamp();
            for (int i = 0; i < Searches; i++)
            {
                Index(table, pages).Dispose();
            }

            taken = Math.Min(taken, Stopwatch.GetElapsedTime(started).TotalMicroseconds / Searches);
            started = Stopwatch.GetTimestamp();
            for (int i = 0; i < Searches; i++)
            {
                using GranuleKeys index = Index(table, pages);
                index.FirstAtOrPast(middle);
            }

            reached = Math.Min(reached, Stopwatch.GetElapsedTime(started).TotalMicroseconds / Searches);
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"an index taken and given back: {taken:0.00} µs; with a bound found in it, one page reached: {reached:0.00} µs"));
        Console.WriteLine($"{"filter",-22} {"µs/search",10} {"judged",8} {"compared",9} {"bytes",8} {"ranges",7}");
        foreach ((string name, VortexExpr filter) in filters)
        {
            double best = double.MaxValue;
            long bytes = 0;
            int judged = 0;
            int compared = 0;
            int ranges = 0;
            for (int round = 0; round < Math.Max(1, rounds); round++)
            {
                KeyPruning[] prunings = new KeyPruning[Searches / Objects];
                for (int i = 0; i < prunings.Length; i++)
                {
                    prunings[i] = KeyPruning.Of(schema, filter)!;
                }

                long before = GC.GetAllocatedBytesForCurrentThread();
                long started = Stopwatch.GetTimestamp();
                foreach (KeyPruning pruning in prunings)
                {
                    for (int i = 0; i < Objects; i++)
                    {
                        using GranuleKeys searched = Index(table, pages);
                        ranges = pruning.Granules((UInt128)i, searched, default).Count;
                    }
                }

                double us = Stopwatch.GetElapsedTime(started).TotalMicroseconds / Searches;
                long allocated = (GC.GetAllocatedBytesForCurrentThread() - before) / Searches;
                if (us < best)
                {
                    best = us;
                    bytes = allocated;
                    judged = prunings[0].Judgments / Objects;
                    compared = prunings[0].Comparisons / Objects;
                }
            }

            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{name,-22} {best,10:0.00} {judged,8} {compared,9} {bytes,8} {ranges,7}"));
            if (bytes > 4 << 10)
            {
                // What the searches that allocate most allocate, by type, as the runtime's ticks sample them.
                using AllocationSampler sampler = new AllocationSampler();
                for (int i = 0; i < Searches / Objects; i++)
                {
                    KeyPruning pruning = KeyPruning.Of(schema, filter)!;
                    for (int o = 0; o < Objects; o++)
                    {
                        using GranuleKeys searched = Index(table, pages);
                        pruning.Granules((UInt128)o, searched, default);
                    }
                }

                foreach (string line in sampler.Lines(Searches, 6))
                {
                    Console.WriteLine(line);
                }
            }
        }

        return 0;
    }

    /// <summary>
    /// The sparse indexes of the objects of the dataset at <paramref name="directory"/>, read and weighed: the bytes read of
    /// them, those an index holds besides once every page is reached, and those its keys take decoded whole. Then what its
    /// records would take with each text unpadded: the blocks of 32 bytes the row encoding pads a text to, with the byte
    /// after each, as an escaping code that keeps the order writes it instead, a zero byte doubled and two bytes to end it;
    /// the keys coded forward again, page by page.
    /// </summary>
    private static async Task<int> HeldAsync(string directory)
    {
        await using FileObjectStore files = new FileObjectStore(directory);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(files, new DatasetOptions());
        long objects = 0;
        long granules = 0;
        long onDisk = 0;
        long held = 0;
        long whole = 0;
        long records = 0;
        long escapedRecords = 0;
        long keyBytes = 0;
        long escapedKeyBytes = 0;
        await foreach (PositionedObject positioned in dataset.WalkAsync(null, 0, long.MaxValue, null, default))
        {
            if (positioned.Entry.Contents is not { KeyIndex: { } table } contents
                || await dataset.KeyIndexAsync(positioned.Entry, contents, default) is not { } keys)
            {
                continue;
            }

            using GranuleKeys read = keys;
            objects++;
            granules += keys.Granules;
            onDisk += table.Length + (contents.KeyPages?.Length ?? 0);
            Vorticity.Dataset.SortKey shape = Vorticity.Dataset.SortKey.Of(dataset.Snapshot.Schema.Layout!, partition: false, keys.Components);

            // What the keys take decoded whole: each key and where it ends; and each record, as written and unpadded.
            GranuleKeys.KeyReader reader = default;
            byte[] previous = [];
            byte[] previousEscaped = [];
            for (int granule = 0; granule < keys.Granules; granule++)
            {
                byte[] key = reader.Key(keys, granule).ToArray();
                byte[] escaped = Unpadded(shape, key);
                whole += key.Length + sizeof(int);
                keyBytes += key.Length;
                escapedKeyBytes += escaped.Length;
                bool first = granule % keys.PageEntries == 0;
                records += RecordBytes(first ? key : previous, key);
                escapedRecords += RecordBytes(first ? escaped : previousEscaped, escaped);
                previous = key;
                previousEscaped = escaped;
            }

            reader.Dispose();
            held += keys.HeldBytes;
        }

        double per = Math.Max(granules, 1);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"keyindex {directory}: {objects:N0} objects, {granules:N0} granules, {onDisk / per:0.0} bytes a granule on disk, {held / per:0.0} held, {whole / per:0.0} decoded whole"));
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  texts unpadded: records {records / per:0.0} -> {escapedRecords / per:0.0} bytes a granule ({100.0 * (records - escapedRecords) / Math.Max(records, 1):0.0} % less); keys {keyBytes / per:0.0} -> {escapedKeyBytes / per:0.0} bytes ({100.0 * (keyBytes - escapedKeyBytes) / Math.Max(keyBytes, 1):0.0} % less)"));
        return 0;
    }

    /// <summary>The bytes of <paramref name="key"/>'s record coded forward against <paramref name="before"/>: the bytes they share, the rest's length, the rest.</summary>
    private static int RecordBytes(byte[] before, byte[] key)
    {
        int shared = key.AsSpan().CommonPrefixLength(before);
        return VarintBytes(shared) + VarintBytes(key.Length - shared) + (key.Length - shared);
    }

    private static int VarintBytes(int value) => value < 0x80 ? 1 : value < 0x4000 ? 2 : value < 0x200000 ? 3 : 5;

    /// <summary>
    /// <paramref name="key"/> with each text component's blocks written unpadded: its leading byte, its bytes with a zero
    /// doubled as zero then 0xFF, then two zero bytes, which orders as the blocks do. Other components as they are.
    /// </summary>
    private static byte[] Unpadded(Vorticity.Dataset.SortKey shape, byte[] key)
    {
        const int Block = 32;
        List<byte> into = new List<byte>(key.Length);
        int start = 0;
        for (int component = 0; component < shape.Count && start < key.Length; component++)
        {
            int end = shape.PrefixLength(key, component + 1);
            DType type = shape.TypeAt(component);
            DType storage = type.Kind == DTypeKind.Extension ? type.StorageType : type;
            if (storage.Kind is not (DTypeKind.Utf8 or DTypeKind.Binary) || key[start] != 0x02)
            {
                for (int i = start; i < end; i++)
                {
                    into.Add(key[i]);
                }

                start = end;
                continue;
            }

            into.Add(key[start]);
            for (int at = start + 1; at < end; at += Block + 1)
            {
                byte marker = key[at + Block];
                int length = marker == 0xFF ? Block : marker;
                for (int i = at; i < at + length; i++)
                {
                    into.Add(key[i]);
                    if (key[i] == 0)
                    {
                        into.Add(0xFF);
                    }
                }
            }

            into.Add(0);
            into.Add(0);
            start = end;
        }

        return [.. into];
    }

    /// <summary>The index of <paramref name="table"/> and <paramref name="pages"/>, as a read hands them.</summary>
    private static GranuleKeys Index(byte[] table, byte[] pages)
    {
        GranuleKeys index = GranuleKeys.From(table) ?? throw new InvalidOperationException("The table does not decode.");
        if (index.WantsPages && !index.TakePages(pages))
        {
            throw new InvalidOperationException("The pages do not hold the table's.");
        }

        return index;
    }

    private static VortexExpr Eq(string path, FilterLiteral value) => Expr.Eq(Expr.Field(path), Expr.Literal(value));

    private static FilterLiteral Text(string value) => FilterLiteral.From(value);

    private static FilterLiteral Micros(DateTime at) => FilterLiteral.From((at - DateTime.UnixEpoch).Ticks / 10);
}

[VortexRecord]
public partial record struct KeyIndexRow(string Service, DateTime At, string Host);
