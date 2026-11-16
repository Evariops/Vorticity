using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Buffers;
using Vorticity.Serialization.Protobuf;

namespace Vorticity.Writing;

/// <summary>
/// The metadata of the chunks' flat layouts that carry any, by column and chunk, kept from the
/// chunk's write to the layout's build: the array tree of a chunk whose tree declares no buffer,
/// inlined. Entries and bytes live in blocks of the pool, so a writer that keeps some allocates
/// nothing once warm.
/// </summary>
/// <remarks>
/// <para>
/// A mutable struct, held in one field of its writer and only ever used through it: a copy would
/// share the blocks and lose the counts.
/// </para>
/// <para>
/// The blocks are held by their bytes, the counts giving their lengths, so that the writer made for
/// every write is no larger for them.
/// </para>
/// </remarks>
internal struct FlatMetadataTable
{
    private byte[]? _entries;
    private int _count;
    private byte[]? _bytes;
    private int _length;
    private int _cursor;

    /// <summary>The entries kept.</summary>
    private readonly Block<Entry> Entries => Block<Entry>.Over(_entries, _count);

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
        Block<Entry> entries = Entries;
        if (_count == entries.Capacity)
        {
            Blocks.Grow(ref entries, Math.Max(8, _count * 2));
            _entries = entries.Bytes;
        }

        Block<byte> bytes = Block<byte>.Over(_bytes, _length);
        if (_length + metadata.Length > bytes.Capacity)
        {
            Blocks.Grow(ref bytes, Math.Max(Math.Max(256, _length * 2), _length + metadata.Length));
            _bytes = bytes.Bytes;
        }

        metadata.CopyTo(_bytes.AsSpan(_length));
        _count++;
        Entries[_count - 1] = new Entry(field, chunk, _length, metadata.Length);
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
        Entries.AsSpan().Sort();
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

        ref Entry entry = ref Entries[_cursor];
        if (entry.Field != field || entry.Chunk != chunk)
        {
            return default;
        }

        _cursor++;
        return _bytes.AsSpan(entry.Start, entry.Length);
    }

    /// <summary>Gives the blocks back to the pool and empties the table.</summary>
    internal void Release()
    {
        Blocks.ReturnBytes(_entries);
        Blocks.ReturnBytes(_bytes);
        this = default;
    }

    /// <summary>One chunk's metadata: where its bytes are in the table's buffer.</summary>
    private readonly record struct Entry(int Field, int Chunk, int Start, int Length) : IComparable<Entry>
    {
        public int CompareTo(Entry other) =>
            Field != other.Field ? Field.CompareTo(other.Field) : Chunk.CompareTo(other.Chunk);
    }
}
