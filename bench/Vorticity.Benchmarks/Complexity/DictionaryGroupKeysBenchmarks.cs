// What grouping a chunk by a dictionary column pays for the dictionary's size, batch after batch.
//
// One chunk of 1,048,576 rows whose key column is a dictionary of V values, decoded once and cut
// into batches as a scan cuts a retained chunk: 8,192 rows, a zone, when the scan filters, and a
// window of 131,072 otherwise. Each batch is assigned to groups as an aggregation assigns it, every
// value already having its group from an earlier chunk. The table from code to group is the
// dictionary's size. `Retained` publishes the chunk as the scan publishes what it retains, so that
// every batch can tell it views the same values as the batch before; without it the table starts
// over at every batch, which is what a chunk the scan does not retain pays.
using System;
using System.Buffers.Binary;
using System.Runtime.InteropServices;

using BenchmarkDotNet.Attributes;

using Vorticity.Aggregating;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Layouts;
using Vorticity.Types;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One chunk assigned to groups by a dictionary key, batch by batch, against the dictionary's size.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class DictionaryGroupKeysBenchmarks
{
    /// <summary>The dictionary's values.</summary>
    [Params(1_024, 65_536, 262_144)]
    public int Values { get; set; }

    /// <summary>The rows of a batch.</summary>
    [Params(8_192, 131_072)]
    public int BatchRows { get; set; }

    /// <summary>The key's type.</summary>
    [Params(KeyType.Int64, KeyType.Utf8)]
    public KeyType Key { get; set; }

    /// <summary>Whether the chunk is published, so its batches know their values again.</summary>
    [Params(false, true)]
    public bool Retained { get; set; }

    /// <summary>A key column's type.</summary>
    public enum KeyType
    {
        /// <summary>64-bit integers.</summary>
        Int64,

        /// <summary>Strings of about ten bytes.</summary>
        Utf8,
    }

    private const int ChunkRows = 1 << 20;

    private RetainingArena _chunk = null!;
    private RetainingArena _batch = null!;
    private int _dictionary;
    private ulong[] _selection = [];
    private int[] _rowGroups = [];
    private GroupRanges _ranges = null!;
    private GroupKeys _keys = null!;

    /// <summary>Builds the chunk and assigns it once, so every value has its group.</summary>
    [GlobalSetup]
    public void Setup()
    {
        DTypeArena types = new DTypeArena();
        _chunk = new RetainingArena();
        _batch = new RetainingArena();
        bool text = Key == KeyType.Utf8;
        DType dtype = text ? types.Utf8(Nullability.NonNullable) : types.Primitive(PType.I64, Nullability.NonNullable);
        int values = text ? Strings(dtype) : Integers(dtype);

        VortexBuffer codes = _chunk.Allocate(ChunkRows * sizeof(uint), sizeof(uint), out Span<byte> codeBytes);
        Span<uint> code = MemoryMarshal.Cast<byte, uint>(codeBytes);
        Random random = new Random(16);
        for (int row = 0; row < ChunkRows; row++)
        {
            code[row] = (uint)random.Next(Values);
        }

        _dictionary = _chunk.AddDictionary(dtype, ChunkRows, Validity.NonNullable, codes, values);

        _selection = new ulong[BatchRows / 64];
        _selection.AsSpan().Fill(ulong.MaxValue);
        _rowGroups = new int[BatchRows];
        _ranges = new GroupRanges();
        VortexType type = text ? VortexType.Utf8 : VortexType.Int64;
        ColumnShape shape = new ColumnShape(new ColumnSym(Expr.Field("k"), type, null, null, -1, []));
        _keys = text ? new BytesKeys(shape, sorted: false) : new FixedKeys<long>(shape, sorted: false);
        Chunk();
    }

    /// <summary>Gives the arenas' blocks back.</summary>
    [GlobalCleanup]
    public void Cleanup()
    {
        _batch.Reset();
        _chunk.Reset();
    }

    /// <summary>The chunk's batches, each cut from it and assigned to groups by the library's key table.</summary>
    [Benchmark]
    public int Chunk()
    {
        if (Retained)
        {
            // A new publication of the same values, as when a chunk is read again: its first batch
            // finds the table of another chunk.
            _chunk.Seal();
        }

        for (int start = 0; start < ChunkRows; start += BatchRows)
        {
            _batch.ResetKeepingBlocks();
            int node = CanonicalSlice.SliceAcross(_chunk, _batch, _dictionary, start, BatchRows);
            _ranges.Clear();
            _keys.Assign(_batch, [node], BatchRows, _selection, _rowGroups, _ranges);
        }

        return _rowGroups[BatchRows - 1];
    }

    private int Integers(DType dtype)
    {
        VortexBuffer buffer = _chunk.Allocate(Values * sizeof(long), sizeof(long), out Span<byte> bytes);
        Span<long> value = MemoryMarshal.Cast<byte, long>(bytes);
        for (int i = 0; i < Values; i++)
        {
            value[i] = i * 7L;
        }

        return _chunk.AddPrimitive(dtype, Values, Validity.NonNullable, PType.I64, buffer);
    }

    private int Strings(DType dtype)
    {
        VortexBuffer heap = _chunk.Allocate(Values * 16, 1, out Span<byte> heapBytes);
        VortexBuffer views = _chunk.Allocate(Values * 16, 16, out Span<byte> viewBytes);
        int used = 0;
        for (int i = 0; i < Values; i++)
        {
            Span<byte> value = heapBytes.Slice(used, 16);
            "value-"u8.CopyTo(value);
            i.TryFormat(value[6..], out int digits);
            int length = 6 + digits;
            Span<byte> view = viewBytes.Slice(i * 16, 16);
            view.Clear();
            BinaryPrimitives.WriteInt32LittleEndian(view, length);
            if (length <= 12)
            {
                value[..length].CopyTo(view[4..]);
                continue;
            }

            value[..4].CopyTo(view.Slice(4, 4));
            BinaryPrimitives.WriteInt32LittleEndian(view[12..], used);
            used += length;
        }

        return _chunk.AddVarBinView(dtype, Values, Validity.NonNullable, views, [heap]);
    }
}
