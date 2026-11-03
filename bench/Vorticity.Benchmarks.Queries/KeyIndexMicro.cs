using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Expressions;
using Vorticity.Indexes;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The search of one object's sparse index alone, in memory (<c>--micro keyindex</c>): an index of 3 300 granules, an
/// object of a gibibyte, under the key <c>(Service, Hour(At), Host)</c> of 600 services, searched for a service, a service
/// and an hour, and a host, which bounds no leading component. A pruning a question, searching 64 objects; the
/// time, the boxes judged, the keys compared and the bytes allocated a search, and the bytes the index holds a granule.
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

        // The same index read again for each object a question searches: a pruning searches each copy once, as it does
        // each object of its question, and keeps what it found of each.
        GranuleKeys[] objects = new GranuleKeys[Objects];
        for (int i = 0; i < objects.Length; i++)
        {
            objects[i] = Index(table, pages);
        }

        GranuleKeys index = objects[0];
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"keyindex: {Granules:N0} granules, {index.ResidentBytes:N0} bytes held, {(double)index.ResidentBytes / Granules:0.0} a granule"));
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
                    foreach (GranuleKeys searched in objects)
                    {
                        ranges = pruning.Granules(searched, default).Count;
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
                    foreach (GranuleKeys searched in objects)
                    {
                        pruning.Granules(searched, default);
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

    /// <summary>The sparse indexes of the objects of the dataset at <paramref name="directory"/>, read and weighed.</summary>
    private static async Task<int> HeldAsync(string directory)
    {
        await using FileObjectStore files = new FileObjectStore(directory);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(files, new DatasetOptions { KeyIndexCacheBytes = 0 });
        long objects = 0;
        long granules = 0;
        long onDisk = 0;
        long held = 0;
        long whole = 0;
        await foreach (PositionedObject positioned in dataset.WalkAsync(null, 0, long.MaxValue, null, default))
        {
            if (positioned.Entry.Contents is not { KeyIndex: { } table } contents
                || await dataset.KeyIndexAsync(positioned.Entry, contents, default) is not { } keys)
            {
                continue;
            }

            objects++;
            granules += keys.Granules;
            onDisk += table.Length + (contents.KeyPages?.Length ?? 0);
            held += keys.ResidentBytes;

            // What the keys took decoded whole, as the index held them before: each key and where it ends.
            GranuleKeys.KeyReader reader = default;
            for (int granule = 0; granule < keys.Granules; granule++)
            {
                whole += reader.Key(keys, granule).Length + sizeof(int);
            }

            reader.Dispose();
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"keyindex {directory}: {objects:N0} objects, {granules:N0} granules, {(double)onDisk / Math.Max(granules, 1):0.0} bytes a granule on disk, {(double)held / Math.Max(granules, 1):0.0} held, {(double)whole / Math.Max(granules, 1):0.0} decoded whole"));
        return 0;
    }

    /// <summary>The index of <paramref name="table"/> and <paramref name="pages"/>, every page in hand.</summary>
    private static GranuleKeys Index(byte[] table, byte[] pages)
    {
        GranuleKeys index = GranuleKeys.From(table) ?? throw new InvalidOperationException("The table does not decode.");
        for (int page = 0; page < index.Pages; page++)
        {
            (long offset, int length, ulong _) = index.PageAt(page);
            if (!index.HasPage(page) && !index.TakePage(page, pages.AsSpan((int)offset, length)))
            {
                throw new InvalidOperationException($"Page {page} does not decode.");
            }
        }

        return index;
    }

    private static VortexExpr Eq(string path, FilterLiteral value) => Expr.Eq(Expr.Field(path), Expr.Literal(value));

    private static FilterLiteral Text(string value) => FilterLiteral.From(value);

    private static FilterLiteral Micros(DateTime at) => FilterLiteral.From((at - DateTime.UnixEpoch).Ticks / 10);
}

[VortexRecord]
public partial record struct KeyIndexRow(string Service, DateTime At, string Host);
