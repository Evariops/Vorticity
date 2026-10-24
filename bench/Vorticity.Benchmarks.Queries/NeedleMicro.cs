using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Compute;
using Vorticity.Indexes;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// What a needle's index weighs and what one probe of it costs (<c>--micro needles</c>): a column of
/// identifiers, four million rows by default (<c>--rows</c>), in blocks of 8 192 rows, written three
/// times, with a sorted run on it, with postings (value to blocks), and with hashed postings (value to
/// blocks through its hash). Each index's bytes are the write report's. A probe decodes one segment of
/// the index and searches it: the run's keys and rows, or the hashed postings' gaps, summed to the
/// probe's cut hash, and blocks. Half the probes look for an identifier the column holds, half for one it
/// does not.
/// </summary>
/// <remarks>Run it with <c>DOTNET_TieredCompilation=0</c>.</remarks>
internal static class NeedleMicro
{
    private const int BlockRows = 8_192;
    private const int Window = 524_288;
    private const int Probes = 2_000;

    /// <summary>The block number's width in an object of a day of the time-window design: 2³² rows in blocks of 8 192.</summary>
    private const int DayBlockBits = 19;

    internal static async Task<int> Run(int rows, int rounds)
    {
        string scratch = Path.Combine(Path.GetTempPath(), $"vortex-needles-{Guid.NewGuid():N}");
        Directory.CreateDirectory(scratch);
        try
        {
            Console.WriteLine($"{rows} rows in blocks of {BlockRows}, {Probes} probes, best of {rounds}");
            foreach (string shape in new[] { "request", "span", "trace" })
            {
                await RunShapeAsync(scratch, shape, rows, rounds);
            }
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }

        return 0;
    }

    private static async Task RunShapeAsync(string scratch, string shape, int rows, int rounds)
    {
        (string Name, string Kind, IndexSpec Spec)[] indexes =
        [
            ("sorted run", IndexKinds.SortedRuns, IndexSpec.SortedRuns),
            ("postings", IndexKinds.PostingsBlocks, IndexSpec.Postings),
            ("hashed", IndexKinds.PostingsHashed, IndexSpec.HashedPostings),
        ];
        string[] paths = [.. indexes.Select(i => Path.Combine(scratch, $"{shape}-{i.Kind}.vortex"))];
        VortexFileWriter[] writers = [.. indexes.Select((i, n) => VortexSession.Default.CreateWriter<NeedleRow>(paths[n], Options(i.Spec)))];
        List<string> present = [];
        long[] written = new long[indexes.Length];
        long[] writeTicks = new long[indexes.Length];
        Random random = new Random(shape.Length);
        for (int start = 0; start < rows; start += Window)
        {
            NeedleRow[] window = Ids(shape, Math.Min(Window, rows - start), random);
            for (int n = 0; n < writers.Length; n++)
            {
                long begin = Stopwatch.GetTimestamp();
                await writers[n].WriteAsync<NeedleRow>(window);
                writeTicks[n] += Stopwatch.GetTimestamp() - begin;
            }

            for (int i = 0; i < window.Length; i += Math.Max(1, rows / Probes))
            {
                present.Add(window[i].Id);
            }
        }

        for (int n = 0; n < writers.Length; n++)
        {
            long begin = Stopwatch.GetTimestamp();
            WriteReport report = await writers[n].CompleteAsync();
            writeTicks[n] += Stopwatch.GetTimestamp() - begin;
            written[n] = report.Index(nameof(NeedleRow.Id), indexes[n].Kind)!.Bytes;
            await writers[n].DisposeAsync();
        }

        string[] probes = [.. present.Take(Probes / 2), .. Absent(shape, Probes / 2)];
        Console.WriteLine($"{shape}: {rows} rows");
        for (int n = 0; n < indexes.Length; n++)
        {
            Probed probed = await ProbeAsync(paths[n], indexes[n].Kind, probes, rounds);
            double ns = writeTicks[n] * 1e9 / Stopwatch.Frequency / rows;
            Console.WriteLine(
                $"  {indexes[n].Name,-11} {(double)written[n] / rows,7:F2} B/row  {(double)written[n] / written[0],6:F3}x the run   {probed.Segments,6} segments, {probed.SegmentBytes / 1024.0,8:F1} KiB a probe, {probed.Microseconds,8:F1} µs a probe   written in {ns,6:F0} ns/row");
            if (indexes[n].Kind == IndexKinds.PostingsHashed)
            {
                double gapBits = probed.FirstBytes * 8.0 / probed.Entries;
                double blockBits = probed.SecondBytes * 8.0 / probed.Entries;
                Console.WriteLine(
                    $"              {probed.Entries} pairs: gaps {gapBits:F2} bits, blocks {blockBits:F2} bits; at {DayBlockBits}-bit blocks {(gapBits + DayBlockBits) / 8:F2} B a pair; found {probed.Found} of {Probes / 2}, {probed.AbsentHits} of {Probes - (Probes / 2)} absent probes answered a block");
            }
        }
    }

    private static VortexWriteOptions Options(IndexSpec spec) => new VortexWriteOptions
    {
        RowBlockSize = BlockRows,
        IndexBudgetPerMille = 1_000_000,
        WritePolicy = WritePolicy.None.For(nameof(NeedleRow.Id), spec.AsRequired()),
    };

    /// <summary>
    /// One window of identifiers: a request's 32 hexadecimal digits, unique to its row; a span's 16,
    /// unique to its row; or a trace's 32, ten rows each, scattered over the window as a slice sorted
    /// by other columns scatters them.
    /// </summary>
    private static NeedleRow[] Ids(string shape, int count, Random random)
    {
        NeedleRow[] window = new NeedleRow[count];
        switch (shape)
        {
            case "request":
                for (int i = 0; i < count; i++)
                {
                    window[i] = new NeedleRow($"{random.NextInt64():x16}{random.NextInt64():x16}");
                }

                break;
            case "span":
                for (int i = 0; i < count; i++)
                {
                    window[i] = new NeedleRow($"{random.NextInt64():x16}");
                }

                break;
            default:
                for (int i = 0; i < count; i += 10)
                {
                    string trace = $"{random.NextInt64():x16}{random.NextInt64():x16}";
                    for (int j = i; j < Math.Min(i + 10, count); j++)
                    {
                        window[j] = new NeedleRow(trace);
                    }
                }

                random.Shuffle(window);
                break;
        }

        return window;
    }

    /// <summary>Identifiers of the shape no window holds: drawn from a generator of their own.</summary>
    private static IEnumerable<string> Absent(string shape, int count)
    {
        Random random = new Random(1_000 + shape.Length);
        for (int i = 0; i < count; i++)
        {
            yield return shape == "span" ? $"{random.NextInt64():x16}" : $"{random.NextInt64():x16}{random.NextInt64():x16}";
        }
    }

    /// <summary>
    /// What probing an index costs: its segments and pairs, the bytes of its segments' first and second
    /// arrays in all, a probe's bytes, its time, and for hashed postings what the probes found.
    /// </summary>
    private sealed record Probed(long Segments, long Entries, long FirstBytes, long SecondBytes, long SegmentBytes, double Microseconds, int Found, int AbsentHits);

    /// <summary>
    /// The index of <paramref name="kind"/> in the file: every segment's regions through its table,
    /// then each probe once per round, decoding the segment its hash or key falls in and searching it,
    /// best of <paramref name="rounds"/>.
    /// </summary>
    private static async Task<Probed> ProbeAsync(string path, string kind, string[] probes, int rounds)
    {
        await using VortexFile file = await VortexFile.OpenAsync(path);
        IndexDirectory directory = (await file.ReadIndexDirectoryAsync(default))!;
        IndexEntry entry = directory.Entries.Single(e => e.Kind == kind);
        IndexRun run = entry.Runs[0];
        int stride = KeyRunOptions.StrideOf(kind);
        bool hashed = kind == IndexKinds.PostingsHashed;
        KeyLayout layout = hashed ? KeyRunOptions.HashedLayout : new KeyLayout(KeyShape.Bytes, 0, default);
        if (!FenceTable.TryOpen(run, stride, layout, out FenceTable? table, out string? reason))
        {
            throw new InvalidOperationException(reason);
        }

        List<Fence> fences = [];
        for (long s = 0; s < table!.SegmentCount; s++)
        {
            fences.Add(await table.GetAsync(file.IndexSourceOf(run), s, default));
        }

        long first = fences.Sum(f => (long)f.Regions[0].Length);
        long second = fences.Sum(f => (long)f.Regions[1].Length);
        long segmentBytes = fences.Sum(f => f.Regions.Sum(r => (long)r.Length)) / fences.Count;
        if (kind == IndexKinds.PostingsBlocks)
        {
            return new Probed(fences.Count, (long)run.EntryCount, first, second, segmentBytes, 0, 0, 0);
        }

        using SafeFileHandle handle = System.IO.File.OpenHandle(path);
        Dictionary<long, (NativeBlob First, NativeBlob Second)> blobs = [];
        try
        {
            // Each probe's segment, by its key or its hash, read once.
            byte[][] literals = [.. probes.Select(Encoding.UTF8.GetBytes)];
            long[] segments = new long[literals.Length];
            ulong[] hashes = new ulong[literals.Length];
            for (int p = 0; p < literals.Length; p++)
            {
                hashes[p] = XxHash3.HashToUInt64(literals[p]);
                byte[] key = hashed ? BitConverter.GetBytes(hashes[p]) : literals[p];
                segments[p] = Math.Min(fences.FindIndex(f => layout.Compare(f.Bounds.Max, key) >= 0) is int at and >= 0 ? at : fences.Count - 1, fences.Count - 1);
                if (!blobs.ContainsKey(segments[p]))
                {
                    Fence fence = fences[(int)segments[p]];
                    blobs[segments[p]] = (NativeBlob.Read(handle, fence.Regions[0]), NativeBlob.Read(handle, fence.Regions[1]));
                }
            }

            DTypeArena types = new DTypeArena();
            DType firstType = hashed ? types.Primitive(PType.U32, Nullability.NonNullable) : types.Utf8(Nullability.NonNullable);
            DType u32 = types.Primitive(PType.U32, Nullability.NonNullable);
            int shift = KeyRunOptions.HashShift(run.EntryCount);
            using ScanContext context = file.CreateIndexContext(run);
            int[] answered = new int[literals.Length];
            double best = double.MaxValue;
            for (int round = 0; round < rounds; round++)
            {
                long begin = Stopwatch.GetTimestamp();
                for (int p = 0; p < literals.Length; p++)
                {
                    Fence fence = fences[(int)segments[p]];
                    int entries = (int)fence.Bounds.Entries;
                    (NativeBlob keys, NativeBlob places) = blobs[segments[p]];
                    context.ResetBatch();
                    context.Decode.LoadBlob(keys.Buffer);
                    CanonicalNode keyNode = context.Canonical.GetNode(context.Decode.DecodeRoot(context.Nodes.Root, firstType, entries));
                    answered[p] = hashed
                        ? Walk(context, keyNode, places, u32, entries, BitConverter.ToUInt64(fence.Bounds.Min) >> shift, hashes[p] >> shift)
                        : Search(context, keyNode, places, u32, entries, literals[p]);
                }

                best = Math.Min(best, Stopwatch.GetElapsedTime(begin).TotalMicroseconds / literals.Length);
            }

            int present = Probes / 2;
            return new Probed(
                fences.Count, (long)run.EntryCount, first, second, segmentBytes, best,
                answered.Take(present).Count(a => a > 0), answered.Skip(present).Count(a => a > 0));
        }
        finally
        {
            foreach ((NativeBlob a, NativeBlob b) in blobs.Values)
            {
                a.Dispose();
                b.Dispose();
            }
        }
    }

    /// <summary>A run's segment: its rows decoded, its keys searched; the entries equal to the literal.</summary>
    private static int Search(ScanContext context, CanonicalNode keys, NativeBlob rows, DType u32, int entries, byte[] literal)
    {
        context.Decode.LoadBlob(rows.Buffer);
        context.Decode.DecodeRoot(context.Nodes.Root, u32, entries);
        int low = 0;
        int high = entries;
        while (low < high)
        {
            int middle = (low + high) >>> 1;
            if (LiteralReader.ViewAt(keys, middle).SequenceCompareTo(literal) < 0)
            {
                low = middle + 1;
            }
            else
            {
                high = middle;
            }
        }

        int found = 0;
        while (low + found < entries && LiteralReader.ViewAt(keys, low + found).SequenceEqual(literal))
        {
            found++;
        }

        return found;
    }

    /// <summary>A hashed segment: its gaps summed to the cut hash wanted, then the blocks of the pairs that hold it.</summary>
    private static int Walk(ScanContext context, CanonicalNode gapNode, NativeBlob blockBlob, DType u32, int entries, ulong cut, ulong wanted)
    {
        ReadOnlySpan<uint> gaps = MemoryMarshal.Cast<byte, uint>(gapNode.Values.Span);
        int at = 0;
        while (at < entries && (cut += gaps[at]) < wanted)
        {
            at++;
        }

        if (at >= entries || cut != wanted)
        {
            return 0;
        }

        context.Decode.LoadBlob(blockBlob.Buffer);
        ReadOnlySpan<uint> blocks = MemoryMarshal.Cast<byte, uint>(
            context.Canonical.GetNode(context.Decode.DecodeRoot(context.Nodes.Root, u32, entries)).Values.Span);
        int found = 0;
        uint last = uint.MaxValue;
        do
        {
            found += blocks[at] != last ? 1 : 0;
            last = blocks[at];
            at++;
        }
        while (at < entries && gaps[at] == 0);
        return found;
    }

    /// <summary>A blob in native memory aligned as a segment is, so that its buffers decode in place.</summary>
    private sealed unsafe class NativeBlob : IDisposable
    {
        private byte* _bytes;

        private NativeBlob(int length)
        {
            _bytes = (byte*)NativeMemory.AlignedAlloc((nuint)Math.Max(length, 1), 1 << VortexLimits.MaxAlignmentExponent);
            Length = length;
        }

        internal int Length { get; }

        internal VortexBuffer Buffer => VortexBuffer.FromPointer(_bytes, Length, VortexLimits.MaxAlignmentExponent);

        internal static NativeBlob Read(SafeFileHandle handle, IndexSegment region)
        {
            NativeBlob blob = new NativeBlob((int)region.Length);
            RandomAccess.Read(handle, new Span<byte>(blob._bytes, blob.Length), (long)region.Offset);
            return blob;
        }

        public void Dispose()
        {
            NativeMemory.AlignedFree(_bytes);
            _bytes = null;
        }
    }
}

[VortexRecord]
public partial record struct NeedleRow(string Id);
