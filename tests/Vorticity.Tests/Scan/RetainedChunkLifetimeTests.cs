using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Layouts;
using Vorticity.Scanning;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Scan;

/// <summary>
/// A chunk larger than a batch is decoded once and borrowed by every batch cut from it, and its
/// values can be views onto the segment they were decoded from: the segment has to live as long as
/// the decoded chunk, not only as long as the batch that first read it.
/// </summary>
/// <remarks>
/// A memory-mapped file hides the fault, because its pages stay mapped while the file is open. A
/// source that copies each segment into memory of its own frees that memory when the batch lets go
/// of it, and what the next batch then reads is whatever reused the memory. The source here
/// overwrites a segment's bytes the moment its last reference goes, so a read through a released
/// segment is wrong every time instead of whenever a collection happens to reuse the memory.
/// </remarks>
public sealed class RetainedChunkLifetimeTests
{
    private const int Rows = 4_000;
    private const int BatchRows = 512;
    private const string NothingReleased =
        "no segment was released during the scan: nothing was tested";

    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly DType Schema = Types.Struct(
        ["value", "name"],
        [Types.Primitive(PType.I64, Nullability.NonNullable), Types.Utf8(Nullability.NonNullable)],
        Nullability.NonNullable);

    [Fact]
    public async Task EveryWindowOfAKeyOrderedReadReadsTheChunksOwnBytes()
    {
        byte[] bytes = await WriteAsync();
        PoisoningSegmentSource source = new PoisoningSegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(source, VortexOpenOptions.Default);
        Assert.Equal(1, FewestChunks(LayoutTree.Parse(file).Root));

        List<(long Value, string Name)> read = [];
        int windows = 0;
        ScanBuilder scan = file.Scan().InKeyOrder("value").WithMaxBatchRows(BatchRows);
        await foreach (RecordBatch batch in scan.ExecuteAsync())
        {
            windows++;
            ReadOnlySpan<long> column = batch.Column(0).AsPrimitive<long>().Values;
            BinaryColumn strings = batch.Column(1).AsBinary();
            for (int row = 0; row < batch.RowCount; row++)
            {
                read.Add((column[row], strings.GetString(row)!));
            }
        }

        // Each window takes its rows out of the one chunk decoded for the first; the segment that
        // chunk was decoded from is released with the first window.
        List<(long Value, string Name)> expected = [];
        for (int row = 0; row < Rows; row++)
        {
            expected.Add((ValueAt(row), NameAt(row)));
        }

        expected.Sort((left, right) => left.Value.CompareTo(right.Value));
        Assert.Equal(Rows / BatchRows + 1, windows);
        Assert.True(source.Freed > 0, NothingReleased);
        Assert.Equal(expected, read);
    }

    [Fact]
    public async Task EveryBatchCutFromOneChunkReadsTheChunksOwnBytes()
    {
        byte[] bytes = await WriteAsync();
        PoisoningSegmentSource source = new PoisoningSegmentSource(bytes);
        await using VortexFile file = await VortexFile.OpenAsync(source, VortexOpenOptions.Default);
        Assert.Equal(1, FewestChunks(LayoutTree.Parse(file).Root));

        List<long> values = [];
        List<string> names = [];
        int batches = 0;
        await foreach (RecordBatch batch in file.Scan().WithMaxBatchRows(BatchRows).ExecuteAsync())
        {
            batches++;
            ReadOnlySpan<long> column = batch.Column(0).AsPrimitive<long>().Values;
            BinaryColumn strings = batch.Column(1).AsBinary();
            for (int row = 0; row < batch.RowCount; row++)
            {
                values.Add(column[row]);
                names.Add(strings.GetString(row)!);
            }
        }

        // Several batches out of the one chunk, and segments released between them: the case the
        // retained decode exists for, under a source that punishes a released read.
        Assert.Equal(Rows / BatchRows + 1, batches);
        Assert.True(source.Freed > 0, NothingReleased);
        Assert.Equal(Rows, values.Count);
        for (int row = 0; row < Rows; row++)
        {
            Assert.Equal(ValueAt(row), values[row]);
            Assert.Equal(NameAt(row), names[row]);
        }
    }

    private static long ValueAt(int row) => (row * 7_919L) ^ 0x5DEECE66DL;

    private static string NameAt(int row) => $"row number {row:D6} of the chunk";

    /// <summary>
    /// One chunk per column, uncompressed, so the primitive and the strings decode as views, with
    /// the sorted runs a key-ordered read walks.
    /// </summary>
    private static async Task<byte[]> WriteAsync()
    {
        CanonicalArena arena = new CanonicalArena();
        try
        {
            VortexBuffer valueBuffer = arena.Allocate(
                Rows * sizeof(long), sizeof(long), out Span<byte> valueBytes);
            Span<long> values = MemoryMarshal.Cast<byte, long>(valueBytes);
            byte[][] encoded = new byte[Rows][];
            int heapBytes = 0;
            for (int row = 0; row < Rows; row++)
            {
                values[row] = ValueAt(row);
                encoded[row] = Encoding.UTF8.GetBytes(NameAt(row));
                heapBytes += encoded[row].Length;
            }

            // Every name is longer than the twelve bytes a view holds inline, so each view points
            // into the data buffer and a released buffer shows in the strings as in the numbers.
            const int viewSize = 16;
            VortexBuffer views = arena.Allocate(
                Rows * viewSize, viewSize, out Span<byte> viewBytes);
            VortexBuffer heap = arena.Allocate(heapBytes, 1, out Span<byte> heapBuffer);
            int offset = 0;
            for (int row = 0; row < Rows; row++)
            {
                byte[] name = encoded[row];
                Span<byte> view = viewBytes.Slice(row * viewSize, viewSize);
                BinaryPrimitives.WriteUInt32LittleEndian(view, (uint)name.Length);
                name.AsSpan(0, 4).CopyTo(view[4..8]);
                BinaryPrimitives.WriteUInt32LittleEndian(view[8..12], 0);
                BinaryPrimitives.WriteUInt32LittleEndian(view[12..16], (uint)offset);
                name.CopyTo(heapBuffer[offset..]);
                offset += name.Length;
            }

            int valueNode = arena.AddPrimitive(
                Schema.GetField(0), Rows, Validity.NonNullable, PType.I64, valueBuffer);
            int nameNode = arena.AddVarBinView(
                Schema.GetField(1), Rows, Validity.NonNullable, views, [heap]);
            int root = arena.AddStruct(Schema, Rows, Validity.NonNullable, [valueNode, nameNode]);

            using MemoryStream stream = new MemoryStream();
            VortexWriteOptions options = new VortexWriteOptions
            {
                Compress = false,
                Indexes = WritePolicy.None.For("value", IndexPolicy.SortedRuns.AsRequired()),
            };
            await using (VortexFileWriter writer = VortexFileWriter.Create(
                new StreamSegmentSink(stream, ownsStream: false), Schema, options))
            {
                using RecordBatch batch = new RecordBatch(arena, root, 0);
                await writer.WriteAsync(batch);
                await writer.CompleteAsync();
            }

            return stream.ToArray();
        }
        finally
        {
            arena.Reset();
        }
    }

    private static int FewestChunks(LayoutNode root)
    {
        int fewest = int.MaxValue;
        for (int i = 0; i < root.ChildCount; i++)
        {
            fewest = Math.Min(fewest, ColumnChunks(root.GetChild(i)));
        }

        return fewest;
    }

    private static int ColumnChunks(LayoutNode node)
    {
        if (node.Encoding == LayoutEncodingId.Chunked)
        {
            return node.ChildCount;
        }

        int most = 1;
        for (int i = 0; i < node.ChildCount; i++)
        {
            most = Math.Max(most, ColumnChunks(node.GetChild(i)));
        }

        return most;
    }

    /// <summary>
    /// Copies each segment into memory of its own, as a source over a buffer or an object store
    /// does, and overwrites it when its last reference goes. The memory itself stays allocated for
    /// the source's lifetime, so a late read sees the overwrite and never a crash.
    /// </summary>
    private sealed class PoisoningSegmentSource(byte[] bytes) : ISegmentSource
    {
        private readonly List<PoisoningSegmentOwner> _owners = [];
        private int _freed;

        /// <summary>How many segments have been released so far.</summary>
        internal int Freed => Volatile.Read(ref _freed);

        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) =>
            new ValueTask<long>(bytes.Length);

        public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken token) =>
            new ValueTask<SegmentOwner>(
                Own(bytes.AsSpan((int)spec.Offset, (int)spec.Length), 1 << spec.AlignmentExponent));

        public async ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken token)
        {
            if (requests.IsPopulated)
            {
                return;
            }

            for (int slot = 0; slot < requests.Count; slot++)
            {
                if (!requests.IsFilled(slot))
                {
                    requests.SetResult(slot, await ReadAsync(requests.GetSpec(slot), token));
                }
            }

            requests.Complete();
        }

        public ValueTask<SegmentOwner> ReadRangeAsync(
            long offset, int length, int alignment, CancellationToken token)
        {
            int available = (int)Math.Min(length, bytes.Length - offset);
            ReadOnlySpan<byte> range = bytes.AsSpan((int)offset, available);
            return new ValueTask<SegmentOwner>(Own(range, alignment));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private PoisoningSegmentOwner Own(ReadOnlySpan<byte> data, int alignment)
        {
            PoisoningSegmentOwner owner = new PoisoningSegmentOwner(
                data, alignment, () => Interlocked.Increment(ref _freed));
            lock (_owners)
            {
                _owners.Add(owner);
            }

            return owner;
        }
    }

    private sealed unsafe class PoisoningSegmentOwner : SegmentOwner
    {
        private readonly byte[] _memory;
        private readonly int _start;
        private readonly int _length;
        private readonly Action _freed;

        internal PoisoningSegmentOwner(ReadOnlySpan<byte> data, int alignment, Action freed)
        {
            _memory = GC.AllocateArray<byte>(data.Length + alignment, pinned: true);
            byte* start = (byte*)Marshal.UnsafeAddrOfPinnedArrayElement(_memory, 0);
            _start = (int)((alignment - ((nint)start % alignment)) % alignment);
            _length = data.Length;
            _freed = freed;
            data.CopyTo(_memory.AsSpan(_start, _length));
            Buffer = VortexBuffer.FromPointer(
                start + _start, _length, System.Numerics.BitOperations.Log2((uint)alignment));
        }

        protected override void FreeCore()
        {
            _memory.AsSpan(_start, _length).Fill(0xDD);
            _freed();
        }
    }
}
