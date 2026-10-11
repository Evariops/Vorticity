using System;
using System.Buffers;
using Vorticity.Arrays.Metadata;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Writing;

/// <summary>
/// The metadata of the chunks' flat layouts that carry any, by column and chunk, kept from the
/// chunk's write to the layout's build: the array tree of a chunk whose tree declares no buffer,
/// inlined. Entries and bytes live in arrays from the shared pools, so a writer that keeps some
/// allocates nothing once warm.
/// </summary>
/// <remarks>
/// A mutable struct, held in one field of its writer and only ever used through it: a copy would
/// share the arrays and lose the counts.
/// </remarks>
internal struct FlatMetadataTable
{
    private Entry[]? _entries;
    private int _count;
    private byte[]? _bytes;
    private int _length;
    private int _cursor;

    /// <summary>Keeps <paramref name="tree"/>, inlined, as the metadata of chunk <paramref name="chunk"/> of column <paramref name="field"/>.</summary>
    /// <param name="field">The column.</param>
    /// <param name="chunk">The chunk, in the column's order.</param>
    /// <param name="tree">The chunk's Array FlatBuffer.</param>
    internal void AddTree(int field, int chunk, ReadOnlySpan<byte> tree)
    {
        // A plain local plus try/finally, not `using`: CS1657 forbids passing a `using` variable as
        // a `ref` argument.
        ProtoWriter writer = new ProtoWriter(tree.Length + 8);
        try
        {
            FlatLayoutMetadata.Write(ref writer, new FlatLayoutMetadata(tree));
            Add(field, chunk, writer.WrittenSpan);
        }
        finally
        {
            writer.Dispose();
        }
    }

    /// <summary>Keeps <paramref name="metadata"/> as the metadata of chunk <paramref name="chunk"/> of column <paramref name="field"/>.</summary>
    /// <param name="field">The column.</param>
    /// <param name="chunk">The chunk, in the column's order.</param>
    /// <param name="metadata">The flat layout's metadata, as it goes in the layout.</param>
    internal void Add(int field, int chunk, ReadOnlySpan<byte> metadata)
    {
        if (_entries is null || _count == _entries.Length)
        {
            _entries = Grow(_entries, _count, Math.Max(8, _count * 2));
        }

        if (_bytes is null || _length + metadata.Length > _bytes.Length)
        {
            _bytes = Grow(_bytes, _length, Math.Max(Math.Max(256, _length * 2), _length + metadata.Length));
        }

        metadata.CopyTo(_bytes.AsSpan(_length));
        _entries[_count++] = new Entry(field, chunk, _length, metadata.Length);
        _length += metadata.Length;
    }

    /// <summary>
    /// Orders the entries by column, then chunk, the order a layout is built in, and starts
    /// <see cref="Take"/> over; before each build.
    /// </summary>
    internal void Seal()
    {
        // Chunks are written column after column within a chunk of the file, so the entries come
        // chunk-major, and the layout is built column-major.
        _entries.AsSpan(0, _count).Sort();
        _cursor = 0;
    }

    /// <summary>
    /// The metadata of chunk <paramref name="chunk"/> of column <paramref name="field"/>, or empty
    /// for none; asked of every chunk, in column then chunk order, after <see cref="Seal"/>.
    /// </summary>
    /// <param name="field">The column.</param>
    /// <param name="chunk">The chunk.</param>
    internal ReadOnlySpan<byte> Take(int field, int chunk)
    {
        if (_cursor == _count)
        {
            return default;
        }

        ref Entry entry = ref _entries![_cursor];
        if (entry.Field != field || entry.Chunk != chunk)
        {
            return default;
        }

        _cursor++;
        return _bytes.AsSpan(entry.Start, entry.Length);
    }

    /// <summary>Gives the arrays back to the shared pools and empties the table.</summary>
    internal void Release()
    {
        if (_entries is not null)
        {
            ArrayPool<Entry>.Shared.Return(_entries);
        }

        if (_bytes is not null)
        {
            ArrayPool<byte>.Shared.Return(_bytes);
        }

        this = default;
    }

    private static T[] Grow<T>(T[]? array, int used, int length)
    {
        T[] grown = ArrayPool<T>.Shared.Rent(length);
        if (array is not null)
        {
            array.AsSpan(0, used).CopyTo(grown);
            ArrayPool<T>.Shared.Return(array);
        }

        return grown;
    }

    /// <summary>One chunk's metadata: where its bytes are in the table's buffer.</summary>
    private readonly record struct Entry(int Field, int Chunk, int Start, int Length) : IComparable<Entry>
    {
        public int CompareTo(Entry other) =>
            Field != other.Field ? Field.CompareTo(other.Field) : Chunk.CompareTo(other.Chunk);
    }
}
