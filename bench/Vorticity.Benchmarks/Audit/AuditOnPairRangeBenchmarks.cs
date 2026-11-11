// What a scan of an OnPair node larger than its batch pays per batch for its dictionary (DE-03).
//
// One node of 262,144 rows of one to five codes each, over a dictionary of `Tokens` tokens of one to
// sixteen bytes, read in windows of 8,192 rows as a scan reads a flat node larger than its batch:
// the batch reset, the blob loaded again, a node-check scope open around each range decode. The
// dictionary is checked, its token table built and its bytes swept for ASCII at every window; this
// measures what that costs against the rows the windows decode.
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

namespace Vorticity.Benchmarks.Audit;

/// <summary>Every window of one OnPair node decoded as a range, against its dictionary's size.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class AuditOnPairRangeBenchmarks
{
    /// <summary>The dictionary's tokens: the writer's most, and the most a code addresses.</summary>
    [Params(4_096, 65_536)]
    public int Tokens { get; set; }

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

    /// <summary>Builds the node's blob and the context that decodes it.</summary>
    [GlobalSetup]
    public unsafe void Setup()
    {
        Random random = new Random(20261111);
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
    }

    /// <summary>Gives the context back.</summary>
    [GlobalCleanup]
    public void Cleanup() => _context.Dispose();

    /// <summary>Every window of the node, decoded as a range, then a context readied for a node never seen.</summary>
    [Benchmark]
    public long Ranges()
    {
        long bytes = 0;
        for (int start = 0; start < Rows; start += BatchRows)
        {
            _context.ResetBatch();
            ArrayBlobReader.Load(
                _context.Nodes,
                VortexBuffer.FromPinned(_pinned.AsSpan(_offset, _length), VortexLimits.MaxAlignmentExponent),
                _context.ArrayEncodings);
            uint? outer = _context.Decode.BeginNodeCheckScope(Segment);
            try
            {
                int node = OnPairDecoder.Instance.DecodeRange(_context.Decode, _context.Nodes.Root, _dtype, Rows, start, BatchRows);
                bytes += _context.Canonical.GetNode(node).Length;
            }
            finally
            {
                _context.Decode.EndNodeCheckScope(outer);
            }
        }

        _context.Recycle();
        return bytes;
    }
}
