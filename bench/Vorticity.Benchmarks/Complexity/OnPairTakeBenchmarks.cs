// What a take from an OnPair array pays per batch for its dictionary.
//
// One node of 262,144 rows of one to five codes each, over a dictionary of T tokens of one to
// sixteen bytes. It is read in batches of 8,192 rows, each a take of one row in N of its window, as
// a scan with a selection reads a flat node larger than its batch: the batch reset, the blob
// loaded again, a node-check scope open around the decode. `Original` is the decoder as it was
// (`OnPairDecoderBefore`), which checks the whole dictionary and builds a table of every token at
// every batch; `Library` is the decoder of the library, which copies a few rows' tokens straight
// from the offsets and checks the dictionary once for the node.
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
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

namespace Vorticity.Benchmarks.Complexity;

/// <summary>Every batch of one OnPair node read with a selection, against its dictionary's size.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class OnPairTakeBenchmarks
{
    /// <summary>The dictionary's tokens.</summary>
    [Params(1_024, 16_384, 65_536)]
    public int Tokens { get; set; }

    /// <summary>One row in this many is taken.</summary>
    [Params(256, 16)]
    public int Every { get; set; }

    /// <summary>The rows of the node.</summary>
    private const int Rows = 262_144;

    /// <summary>The rows of a batch.</summary>
    private const int BatchRows = 8_192;

    /// <summary>The segment the node's checks are keyed by.</summary>
    private const uint Segment = 11;

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
        Random random = new Random(20260927);
        byte[] dictionary = new byte[Tokens * 16];
        uint[] dictOffsets = new uint[Tokens + 1];
        for (int t = 0; t < Tokens; t++)
        {
            int size = 1 + (t % 16);
            for (int b = 0; b < size; b++)
            {
                dictionary[dictOffsets[t] + b] = (byte)('a' + random.Next(26));
            }

            dictOffsets[t + 1] = dictOffsets[t] + (uint)size;
        }

        List<ushort> codes = [];
        uint[] codesOffsets = new uint[Rows + 1];
        uint[] lengths = new uint[Rows];
        for (int row = 0; row < Rows; row++)
        {
            int count = 1 + random.Next(5);
            for (int c = 0; c < count; c++)
            {
                int token = random.Next(Tokens);
                codes.Add((ushort)token);
                lengths[row] += dictOffsets[token + 1] - dictOffsets[token];
            }

            codesOffsets[row + 1] = (uint)codes.Count;
        }

        byte[][] buffers =
        [
            dictionary.AsSpan(0, (int)dictOffsets[Tokens]).ToArray(),
            MemoryMarshal.AsBytes(dictOffsets.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(CollectionsMarshal.AsSpan(codes)).ToArray(),
            MemoryMarshal.AsBytes(codesOffsets.AsSpan()).ToArray(),
            MemoryMarshal.AsBytes(lengths.AsSpan()).ToArray(),
        ];

        OnPairMetadata metadata = new OnPairMetadata(
            PType.U32, (uint)Tokens, (ulong)codes.Count, PType.U32, PType.U16, PType.U32);
        ProtoWriter writer = new ProtoWriter();
        byte[] metadataBytes;
        try
        {
            OnPairMetadata.Write(ref writer, in metadata);
            metadataBytes = writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }

        // [pad][buffer 0][pad][buffer 1]...[flatbuffer][u32 length], each buffer on 16 bytes.
        BufferSpec[] specs = new BufferSpec[buffers.Length];
        int[] starts = new int[buffers.Length];
        int region = 0;
        for (int i = 0; i < buffers.Length; i++)
        {
            int padding = (16 - (region % 16)) % 16;
            region += padding;
            starts[i] = region;
            region += buffers[i].Length;
            specs[i] = new BufferSpec((ushort)padding, 4, 0, (uint)buffers[i].Length);
        }

        byte[] flat;
        using (FlatBufferBuilder builder = new FlatBufferBuilder())
        {
            int[] children = new int[4];
            for (int i = 0; i < 4; i++)
            {
                children[i] = ArrayWriter.WriteNode(builder, 1, [], [], [(ushort)(i + 1)], 0);
            }

            int root = ArrayWriter.WriteNode(builder, 0, metadataBytes, children, [0], 0);
            flat = builder.FinishToArray(ArrayWriter.Write(builder, root, specs));
        }

        _length = region + flat.Length + 4;
        _pinned = GC.AllocateArray<byte>(_length + 64, pinned: true);
        _offset = (int)((64 - ((nuint)Unsafe.AsPointer(ref MemoryMarshal.GetArrayDataReference(_pinned)) & 63)) & 63);
        Span<byte> segment = _pinned.AsSpan(_offset, _length);
        for (int i = 0; i < buffers.Length; i++)
        {
            buffers[i].CopyTo(segment[starts[i]..]);
        }

        flat.CopyTo(segment[region..]);
        BinaryPrimitives.WriteUInt32LittleEndian(segment[(region + flat.Length)..], (uint)flat.Length);

        _context = new ScanContext(["vortex.onpair", "vortex.primitive"]);
        _dtype = _context.Types.Utf8(Nullability.NonNullable);
        _wanted = new int[BatchRows / Every];
    }

    /// <summary>Gives the context back.</summary>
    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    /// <summary>The whole dictionary checked and tabled at every batch.</summary>
    [Benchmark(Baseline = true)]
    public long Original() => ReadNode(OnPairDecoderBefore.Instance);

    /// <summary>The decoder of the library.</summary>
    [Benchmark]
    public long Library() => ReadNode(OnPairDecoder.Instance);

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
}
