using System;
using System.Runtime.CompilerServices;
using Vorticity.Arrays;

namespace Vorticity;

/// <summary>
/// One column of a delivered batch, typed by the .NET type its dtype maps to. Borrowed: valid inside
/// the body of the enumeration that produced it, which the compiler enforces.
/// </summary>
/// <typeparam name="T">
/// The column's .NET type. <c>Column&lt;double?&gt;</c> is a nullable column and
/// <c>Column&lt;double&gt;</c> is not; the accessors come from <see cref="ColumnExtensions"/>.
/// </typeparam>
public readonly ref struct Column<T>
{
    internal Column(CanonicalArena arena, int node, VortexType type, VortexExtensionRegistry? extensions)
    {
        Arena = arena;
        Type = type;
        Extensions = extensions;

        // A date, a time, a timestamp or a registered extension is its storage plus a label; every
        // accessor reads the storage, and the label is in Type.
        while (arena.RecordRef(node).Kind == CanonicalKind.Extension)
        {
            node = arena.GetNode(node).StorageIndex;
        }

        Node = node;
    }

    internal CanonicalArena Arena { get; }

    /// <summary>The storage node the accessors read.</summary>
    internal int Node { get; }

    /// <summary>The column's type, as the file declares it.</summary>
    internal VortexType Type { get; }

    internal VortexExtensionRegistry? Extensions { get; }

    /// <summary>The number of rows.</summary>
    public int Length => Arena.RecordRef(Node).Length;

    /// <summary>The number of null rows, counted from the validity words.</summary>
    public int NullCount => ArenaWords.NullCount(Arena, Node);

    /// <summary>Whether no row is null, so that <see cref="ValidityWords"/> is empty and no check is needed.</summary>
    public bool IsAllValid => Arena.RecordRef(Node).Validity.IsAllValid;

    /// <summary>
    /// The validity as 64-bit words: bit <c>i % 64</c> of word <c>i / 64</c> is row <c>i</c>, least
    /// significant first, and the bits past <see cref="Length"/> are zero. Empty when <see cref="IsAllValid"/>.
    /// </summary>
    public ReadOnlySpan<ulong> ValidityWords => ArenaWords.Validity(Arena, Node);

    /// <summary>Whether row <paramref name="index"/> holds a value.</summary>
    /// <param name="index">A row of the batch.</param>
    /// <returns>False for a null.</returns>
    public bool IsValid(int index) => ArenaWords.IsValid(Arena, Node, index);

    /// <summary>How the column holds its values before anything decodes them.</summary>
    public ColumnEncoding Encoding => EncodedForms.EncodingOf(Arena, Node);

    /// <summary>The codes and the distinct values of a dictionary column, without decoding it.</summary>
    /// <returns>The view.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Encoding"/> is not <see cref="ColumnEncoding.Dictionary"/>.</exception>
    public DictionaryView<T> AsDictionary()
    {
        RequireEncoding(ColumnEncoding.Dictionary);
        int values = EncodedForms.Dictionary(Arena, Node, out ReadOnlySpan<uint> codes);
        return new DictionaryView<T>(codes, new Column<T>(Arena, values, Type, Extensions));
    }

    /// <summary>The run ends and the value of each run of a run-end column, without decoding it.</summary>
    /// <returns>The view.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Encoding"/> is not <see cref="ColumnEncoding.RunEnd"/>.</exception>
    public RunEndView<T> AsRunEnd()
    {
        RequireEncoding(ColumnEncoding.RunEnd);
        int values = EncodedForms.RunEnd(Arena, Node, out ReadOnlySpan<uint> ends);
        return new RunEndView<T>(ends, new Column<T>(Arena, values, Type, Extensions));
    }

    /// <summary>The one value of a constant column.</summary>
    /// <returns>The value; the default of <typeparamref name="T"/> for a column of nulls.</returns>
    /// <exception cref="InvalidOperationException"><see cref="Encoding"/> is not <see cref="ColumnEncoding.Constant"/>.</exception>
    public T AsConstant()
    {
        RequireEncoding(ColumnEncoding.Constant);
        if (Length == 0)
        {
            return default!;
        }

        // One row of the constant, made canonical, rather than the whole column tiled to read its
        // first value: the value is the element, whatever the length.
        int one = Layouts.CanonicalSlice.SliceAcross(Arena, Arena, Node, 0, 1);
        return ColumnReader.Read(new Column<T>(Arena, one, Type, Extensions).AsCanonical(), 0);
    }

    /// <summary>The column in canonical form: the values decoded, once, into contiguous memory.</summary>
    /// <returns>The canonical column; this one when it already is.</returns>
    public Column<T> AsCanonical()
    {
        int canonical = EncodedForms.Canonical(Arena, Node);
        return canonical == Node ? this : new Column<T>(Arena, canonical, Type, Extensions);
    }

    /// <summary>The node that holds the values contiguously: the node itself, or its decoded twin.</summary>
    internal int ValuesNode
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get
        {
            CanonicalKind kind = Arena.RecordRef(Node).Kind;
            return kind is CanonicalKind.Constant or CanonicalKind.Dictionary or CanonicalKind.RunEnd
                ? EncodedForms.Canonical(Arena, Node)
                : Node;
        }
    }

    private void RequireEncoding(ColumnEncoding wanted)
    {
        if (Encoding != wanted)
        {
            throw new InvalidOperationException($"The column is {Encoding}, not {wanted}; read Encoding before asking for a view.");
        }
    }
}

/// <summary>The rows of one delivered batch, as the columns of <typeparamref name="TRecord"/>. Borrowed, like every column.</summary>
/// <typeparam name="TRecord">The record the scan is typed by.</typeparam>
public readonly ref struct Columns<TRecord>
{
    private readonly ReadOnlySpan<ulong> _selection;
    private readonly int _selected;
    private readonly bool _projected;
    private readonly RecordScratch? _scratch;
    private readonly int _depth;

    /// <param name="batch">The batch the rows are of.</param>
    /// <param name="arena">The arena its nodes lie in.</param>
    /// <param name="node">The struct node the record's members are fields of.</param>
    /// <param name="binding">The record's members bound to the struct's fields.</param>
    /// <param name="startRow">The file row of the batch's first row.</param>
    /// <param name="selection">The rows the filter kept as words; empty when every row is.</param>
    /// <param name="selected">The rows the filter kept.</param>
    /// <param name="projected">Whether the struct holds the record's fields only, in its order.</param>
    /// <param name="scratch">
    /// What holds the arrays generated code fills before it builds the records (<see cref="Scratch{TMember}"/>), from one
    /// batch to the next: a typed scan's enumeration; null where nothing reads more than one batch, which then allocates.
    /// </param>
    /// <param name="depth">How deep the record lies in the one read: 0 for the batch's, one more for each record within.</param>
    internal Columns(
        RecordBatch batch, CanonicalArena arena, int node, RecordBinding binding, long startRow, ReadOnlySpan<ulong> selection, int selected, bool projected,
        RecordScratch? scratch = null, int depth = 0)
    {
        Batch = batch;
        Arena = arena;
        Node = node;
        Binding = binding;
        StartRow = startRow;
        _selection = selection;
        _selected = selected;
        _projected = projected;
        _scratch = scratch;
        _depth = depth;
    }

    /// <summary>
    /// Room for <paramref name="count"/> values of a member, which the code the generator emits fills before it builds the
    /// records: an array held by what reads the records, from one batch to the next, and the same for the same
    /// <paramref name="slot"/> at the same depth. The values are the caller's to clear once read, when they hold references.
    /// </summary>
    /// <typeparam name="TMember">The member's type.</typeparam>
    /// <param name="slot">Which of the record's uses: four a member, the generated code's own numbering.</param>
    /// <param name="count">The values the room holds.</param>
    /// <returns>The room, valid until the next batch is read.</returns>
    /// <remarks>A view no enumeration made, one a caller took of a batch, holds no room from one batch to the next: its room is made for the call.</remarks>
    [System.ComponentModel.EditorBrowsable(System.ComponentModel.EditorBrowsableState.Never)]
    public Span<TMember> Scratch<TMember>(int slot, int count) =>
        _scratch is { } scratch ? scratch.Take<TMember>(_depth, slot, count).AsSpan(0, count) : new TMember[count];

    /// <summary>The scratch the records within share, a level deeper.</summary>
    internal RecordScratch? NestedScratch => _scratch;

    /// <summary>The depth of the records within.</summary>
    internal int NestedDepth => _depth + 1;

    internal RecordBatch Batch { get; }

    internal CanonicalArena Arena { get; }

    /// <summary>The struct node the record's members are fields of.</summary>
    internal int Node { get; }

    internal RecordBinding Binding { get; }

    /// <summary>The number of rows in the batch.</summary>
    public int RowCount => Arena.RecordRef(Node).Length;

    /// <summary>
    /// The file row of the block's first row: row <c>i</c> is file row <c>StartRow + i</c> unless a
    /// filter compacted the batch, which keeps the kept rows only; with
    /// <c>ScanOptions.Compact = false</c> the positions hold and <see cref="Selection"/> says which passed.
    /// </summary>
    public long StartRow { get; }

    /// <summary>Which rows passed the filter: all of them unless <c>ScanOptions.Compact</c> is false, or the batch is a take.</summary>
    public Selection Selection => _selection.IsEmpty ? new Selection(RowCount) : new Selection(_selection, RowCount, _selected);

    /// <summary>Whether every row holds a record: always for a batch, and for a nested record whose column has no null.</summary>
    public bool IsAllValid => Arena.RecordRef(StructNode()).Validity.IsAllValid;

    /// <summary>
    /// For a nullable nested record, which rows hold one, as the 64-bit words of
    /// <see cref="Column{T}.ValidityWords"/>; empty when <see cref="IsAllValid"/>.
    /// </summary>
    public ReadOnlySpan<ulong> ValidityWords => ArenaWords.Validity(Arena, StructNode());

    /// <summary>Whether row <paramref name="index"/> holds a record, rather than a null nested record.</summary>
    /// <param name="index">A row of the batch.</param>
    /// <returns>False for a null.</returns>
    public bool IsValid(int index) => ArenaWords.IsValid(Arena, StructNode(), index);

    /// <summary>The rows the filter kept as words, or empty when every row is selected.</summary>
    internal ReadOnlySpan<ulong> SelectionWords => _selection;

    /// <summary>The column of member <paramref name="index"/>.</summary>
    /// <typeparam name="T">The member's .NET type.</typeparam>
    /// <param name="index">The member's position in the record.</param>
    /// <returns>The column.</returns>
    /// <exception cref="VortexSchemaException"><typeparamref name="T"/> does not map to the column.</exception>
    public Column<T> Column<T>(int index)
    {
        Binding.Require<T>(index);
        int child = Arena.GetNode(StructNode()).GetFieldIndex(Slot(index));
        return new Column<T>(Arena, child, Binding.TypeOf(index), Binding.Extensions);
    }

    /// <summary>The nested record held by member <paramref name="index"/>.</summary>
    /// <typeparam name="TNested">The nested record type.</typeparam>
    /// <param name="index">The member's position in the record.</param>
    /// <returns>The nested record's columns, over the same rows.</returns>
    public Columns<TNested> Struct<TNested>(int index)
        where TNested : IVortexRecord<TNested>
    {
        RecordBinding nested = Binding.NestedFor<TNested>(index);
        int child = Arena.GetNode(StructNode()).GetFieldIndex(Slot(index));
        return new Columns<TNested>(Batch, Arena, child, nested, StartRow, _selection, _selected, _projected, NestedScratch, NestedDepth);
    }

    /// <summary>The lists of records held by member <paramref name="index"/>.</summary>
    /// <typeparam name="TNested">The record the lists hold.</typeparam>
    /// <param name="index">The member's position in the record.</param>
    /// <returns>The lists: each row's range, and the elements as the columns of <typeparamref name="TNested"/>.</returns>
    /// <exception cref="VortexSchemaException">The member is not a list of <typeparamref name="TNested"/>.</exception>
    public ListColumns<TNested> ListOf<TNested>(int index)
        where TNested : IVortexRecord<TNested>
    {
        RecordBinding elements = Binding.ElementsFor<TNested>(index);
        return new ListColumns<TNested>(Batch, Arena, Arena.GetNode(StructNode()).GetFieldIndex(Slot(index)), elements, NestedScratch, NestedDepth);
    }

    /// <summary>The maps held by member <paramref name="index"/>.</summary>
    /// <typeparam name="TKey">The keys' .NET type.</typeparam>
    /// <typeparam name="TValue">The values' .NET type.</typeparam>
    /// <param name="index">The member's position in the record.</param>
    /// <returns>The maps: each row's range, and the keys and values as two columns.</returns>
    /// <exception cref="VortexSchemaException">The member is not a map, or its keys or values do not map to the types asked for.</exception>
    public MapColumns<TKey, TValue> MapOf<TKey, TValue>(int index)
    {
        VortexType type = Binding.TypeOf(index);
        if (type.Kind != VortexTypeKind.Map)
        {
            throw new VortexSchemaException($"Member '{Binding.Record[index].Name}' of {Binding.RecordType.Name} is a column of {type}, not a map.");
        }

        return new MapColumns<TKey, TValue>(Arena, Arena.GetNode(StructNode()).GetFieldIndex(Slot(index)), type, Binding.Extensions);
    }

    /// <summary>
    /// Where member <paramref name="index"/> sits in the batch's struct: its rank among the read
    /// columns in a batch the scan projected, its file index in one delivered whole.
    /// </summary>
    private int Slot(int index) => _projected ? Binding.BatchIndex[index] : Binding.FileIndex[index];

    /// <summary>The number of the record's members.</summary>
    internal int MemberCount => Binding.Record.Count;

    /// <summary>The arena node of member <paramref name="index"/>'s column, extension or not.</summary>
    internal int ColumnNode(int index) => Arena.GetNode(StructNode()).GetFieldIndex(Slot(index));

    /// <summary>Copies the batch once into buffers the caller owns, to keep it past the enumeration.</summary>
    /// <returns>The owned batch; the caller disposes it.</returns>
    public RecordBatch ToOwned() => RecordBatch.Own(Arena, StructNode(), StartRow, Batch?.Schema, Batch?.Session, _selection, _selected);

    private int StructNode()
    {
        int node = Node;
        while (Arena.RecordRef(node).Kind == CanonicalKind.Extension)
        {
            node = Arena.GetNode(node).StorageIndex;
        }

        return node;
    }
}
