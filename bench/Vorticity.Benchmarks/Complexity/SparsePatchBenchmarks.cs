// What a sparse array read a batch at a time pays for its patch set.
//
// One node of 262,144 rows, its P patches written as the reference writer writes a regular patch
// set: indices and values as `vortex.sequence`, which decode to P values each. The node is read in
// batches of B rows, each a take of every sixteenth row of its window, as a scan with a selection
// reads a flat node larger than its batch: the batch reset, the blob loaded again, a node-check
// scope open around the decode. `Original` is the decoder as it was (`SparseDecoderBefore`), which
// decodes the patch set at every batch; `Library` is the decoder of the library, which decodes it
// once for the node.
using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Compressed;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Serialization.FlatBuffers;
using Vorticity.Serialization.Protobuf;
using Vorticity.Serialization.Schemas;
using Vorticity.Types;
using Vorticity.Types.Serialization;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>Every batch of one sparse node read with a selection, against its patch count.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class SparsePatchBenchmarks
{
    /// <summary>The patches of the node.</summary>
    [Params(1_024, 16_384, 65_536)]
    public int Patches { get; set; }

    /// <summary>The rows of a batch, down from the reader's window.</summary>
    [Params(8_192, 32_768, 131_072)]
    public int BatchRows { get; set; }

    /// <summary>The rows of the node, a chunk as the reference writer cuts one.</summary>
    private const int Rows = 262_144;

    /// <summary>One row in this many is taken.</summary>
    private const int Every = 16;

    /// <summary>The segment the node's checks and retained children are keyed by.</summary>
    private const uint Segment = 7;

    private byte[] _pinned = [];
    private int _offset;
    private int _length;
    private ScanContext _context = null!;
    private DType _dtype;
    private int[] _wanted = [];

    /// <summary>Builds the node's blob and the context that decodes it.</summary>
    [GlobalSetup]
    public unsafe void Setup()
    {
        int stride = Rows / Patches;
        ScalarStore store = new ScalarStore();
        byte[] fill = ScalarProtobuf.SerializeValue(store.Int64(0));
        byte[] sparse = Metadata(PatchesMetadata.Create((ulong)Patches, 0, PType.U32));
        byte[] indices = Sequence(store.UInt64((ulong)(stride / 2)), store.UInt64((ulong)stride));
        byte[] values = Sequence(store.Int64(1), store.Int64(1));

        byte[] flat;
        using (FlatBufferBuilder builder = new FlatBufferBuilder())
        {
            int indicesNode = ArrayWriter.WriteNode(builder, 1, indices, [], [], 0);
            int valuesNode = ArrayWriter.WriteNode(builder, 1, values, [], [], 0);
            int root = ArrayWriter.WriteNode(builder, 0, sparse, [indicesNode, valuesNode], [0], 0);
            int array = ArrayWriter.Write(builder, root, [new BufferSpec(0, 4, 0, (uint)fill.Length)]);
            flat = builder.FinishToArray(array);
        }

        // [fill][flatbuffer][u32 length], started on a 64-byte boundary of a pinned array, as the
        // reader's segments are.
        _length = fill.Length + flat.Length + 4;
        _pinned = GC.AllocateArray<byte>(_length + 64, pinned: true);
        _offset = (int)((64 - ((nuint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_pinned)) & 63)) & 63);
        Span<byte> segment = _pinned.AsSpan(_offset, _length);
        fill.CopyTo(segment);
        flat.CopyTo(segment[fill.Length..]);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(segment[(fill.Length + flat.Length)..], (uint)flat.Length);

        _context = new ScanContext(["vortex.sparse", "vortex.sequence"]);
        _dtype = _context.Types.Primitive(PType.I64, Nullability.NonNullable);
        _wanted = new int[BatchRows / Every];
    }

    /// <summary>Gives the context back.</summary>
    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    /// <summary>The patch set decoded at every batch.</summary>
    [Benchmark(Baseline = true)]
    public long Original() => ReadNode(SparseDecoderBefore.Instance);

    /// <summary>The patch set decoded once for the node.</summary>
    [Benchmark]
    public long Library() => ReadNode(SparseDecoder.Instance);

    /// <summary>Every batch of the node, then a context readied for a node never seen.</summary>
    private long ReadNode(ArrayDecoder decoder)
    {
        long rows = 0;
        for (int start = 0; start < Rows; start += BatchRows)
        {
            _context.ResetBatch();
            ArrayBlobReader.Load(
                _context.Nodes,
                VortexBuffer.FromPinned(_pinned.AsSpan(_offset, _length), VortexLimits.MaxAlignmentExponent),
                _context.ArrayEncodings);
            for (int i = 0; i < _wanted.Length; i++)
            {
                _wanted[i] = start + (i * Every);
            }

            uint? outer = _context.Decode.BeginNodeCheckScope(Segment);
            try
            {
                int node = decoder.DecodeSelected(_context.Decode, _context.Nodes.Root, _dtype, Rows, _wanted);
                rows += _context.Canonical.GetNode(node).Length;
            }
            finally
            {
                _context.Decode.EndNodeCheckScope(outer);
            }
        }

        _context.Recycle();
        return rows;
    }

    private static byte[] Metadata(PatchesMetadata patches)
    {
        SparseMetadata value = new SparseMetadata(in patches);
        ProtoWriter writer = new ProtoWriter();
        try
        {
            SparseMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static byte[] Sequence(ScalarValue start, ScalarValue step)
    {
        SequenceMetadata value = new SequenceMetadata(start, step);
        ProtoWriter writer = new ProtoWriter();
        try
        {
            SequenceMetadata.Write(ref writer, in value);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }
}
