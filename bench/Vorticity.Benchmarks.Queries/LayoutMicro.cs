using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Layouts;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// <c>--micro layout</c>: the bytes a row of each column takes in a dataset's objects, the reference requests in the order
/// they arrive against the same rows clustered by emitter (<c>service/version/host</c>, then the instant), each compacted
/// to its last level: what the clustering changes, column by column, against the model of the design (22 §3.4).
/// </summary>
internal static class LayoutMicro
{
    internal static async Task<int> Run(string root, double minutes, double rate)
    {
        DateTimeOffset start = new DateTimeOffset(2026, 10, 25, 0, 0, 0, TimeSpan.Zero);
        RequestGenerator generator = new RequestGenerator(7);
        List<EmitterRequest[]> batches = [.. generator.Appends(start, TimeSpan.FromMinutes(minutes), rate, TimeSpan.FromSeconds(5), writers: 4)
            .Select(batch => batch.Select(EmitterRequest.Of).ToArray())];
        long rows = batches.Sum(batch => (long)batch.Length);
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"layout: {rows:N0} requests, {minutes:0.#} minutes at {rate:N0} a second, in {batches.Count} appends"));

        Dictionary<string, double> arrival = await BytesAsync(Path.Combine(root, "arrival"), batches, new DatasetOptions());
        Dictionary<string, double> emitter = await BytesAsync(
            Path.Combine(root, "emitter"), batches, new DatasetOptions { ClusteringKey = [nameof(EmitterRequest.Emitter), nameof(EmitterRequest.At)] });

        Console.WriteLine($"{"column",-12} {"arrival B/row",14} {"by emitter B/row",17}");
        foreach (string column in arrival.Keys)
        {
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{column,-12} {arrival[column] / rows,14:F3} {emitter[column] / rows,17:F3}"));
        }

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"{"all",-12} {arrival.Values.Sum() / rows,14:F3} {emitter.Values.Sum() / rows,17:F3}"));
        return 0;
    }

    /// <summary>The rows appended to a new dataset under <paramref name="directory"/>, compacted to its last level, and each column's bytes over its objects.</summary>
    private static async Task<Dictionary<string, double>> BytesAsync(string directory, List<EmitterRequest[]> batches, DatasetOptions options)
    {
        if (Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }

        Directory.CreateDirectory(directory);
        List<string> live = [];
        await using (FileObjectStore store = new FileObjectStore(directory))
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, EmitterRequest.Schema, options))
        {
            foreach (EmitterRequest[] batch in batches)
            {
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<EmitterRequest>(batch);
                await dataset.AppendAsync(draft);
            }

            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 1 }) is not null)
            {
            }

            await foreach (DataObject held in dataset.ListObjectsAsync())
            {
                live.Add(held.Key);
            }
        }

        Dictionary<string, double> bytes = [];
        foreach (string key in live)
        {
            await using VortexFile file = await VortexFile.OpenAsync(Path.Combine(directory, key));
            LayoutNode node = file.LayoutTree.Root;
            for (int field = 0; field < node.ChildCount; field++)
            {
                string name = file.Schema[field].Name;
                bytes[name] = bytes.GetValueOrDefault(name) + Bytes(node.GetChild(field), file.SegmentSpecs);
            }
        }

        return bytes;
    }

    /// <summary>The bytes of every segment under <paramref name="node"/>.</summary>
    private static long Bytes(LayoutNode node, ReadOnlySpan<SegmentSpec> segments)
    {
        long bytes = 0;
        foreach (uint segment in node.Segments)
        {
            bytes += segments[(int)segment].Length;
        }

        for (int c = 0; c < node.ChildCount; c++)
        {
            bytes += Bytes(node.GetChild(c), segments);
        }

        return bytes;
    }
}

/// <summary>A reference request led by its emitter, <c>service/version/host</c>, which a dataset clustered by emitter keys on.</summary>
[VortexRecord]
public partial record struct EmitterRequest(
    string Emitter,
    DateTimeOffset At,
    string Service,
    string Endpoint,
    string Method,
    short Status,
    string Region,
    string Version,
    string Host,
    string RequestId,
    long DurationNs,
    double Weight)
{
    internal static EmitterRequest Of(GeneratedRequest request) => new EmitterRequest(
        $"{request.Service}/{request.Version}/{request.Host}", request.At, request.Service, request.Endpoint, request.Method, request.Status,
        request.Region, request.Version, request.Host, request.RequestId, request.DurationNs, request.Weight);
}
