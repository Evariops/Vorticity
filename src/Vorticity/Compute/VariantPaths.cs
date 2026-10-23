using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Vorticity.Arrays;
using Vorticity.Arrays.Decoders.Canonical;
using Vorticity.Buffers;
using Vorticity.Expressions;
using Vorticity.Types;
using Vorticity.Types.Variants;

namespace Vorticity.Compute;

/// <summary>One step of a path into a variant: a whole key of an object, or an index of an array.</summary>
internal readonly struct VariantStep
{
    private VariantStep(string? key, byte[]? keyUtf8, int index)
    {
        Key = key;
        KeyUtf8 = keyUtf8;
        Index = index;
    }

    /// <summary>The key, dots included; null for an index.</summary>
    internal string? Key { get; }

    /// <summary>The key's UTF-8 bytes, encoded once.</summary>
    internal byte[]? KeyUtf8 { get; }

    /// <summary>The index of an array's element; meaningless for a key.</summary>
    internal int Index { get; }

    internal bool IsKey => Key is not null;

    internal static VariantStep Of(string key) => new(key ?? throw new ArgumentNullException(nameof(key)), Encoding.UTF8.GetBytes(key), -1);

    internal static VariantStep Of(int index) =>
        index >= 0 ? new(null, null, index) : throw new ArgumentOutOfRangeException(nameof(index), index, "An array's index is not negative.");

    /// <summary>The step as a path's text writes it: the key quoted whole, the index as a number.</summary>
    public override string ToString() => IsKey ? "[" + Quoted(Key!) + "]" : "[" + Index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "]";

    /// <summary>A key between single quotes, a quote in it doubled, as the filter grammar quotes text.</summary>
    internal static string Quoted(string key) => "'" + key.Replace("'", "''", StringComparison.Ordinal) + "'";
}

/// <summary>What a path's value is read as.</summary>
internal enum VariantRead : byte
{
    /// <summary>An integer of any width, as stored.</summary>
    AsInt64,

    /// <summary>A float or a double, as stored.</summary>
    AsDouble,

    /// <summary>A string, as stored.</summary>
    AsString,

    /// <summary>A boolean, as stored.</summary>
    AsBool,

    /// <summary>A timestamp in UTC, as stored, in microseconds.</summary>
    AsTimestamp,

    /// <summary>Any number, a boolean or text converted to an integer.</summary>
    ToInt64,

    /// <summary>Any number, a boolean or text converted to a double.</summary>
    ToDouble,

    /// <summary>Whether the path is present: true or false, never null.</summary>
    Has,
}

/// <summary>
/// A path into a variant column read as a typed column: its keys and indices from the column's
/// value, and the type class it is read as. Its values are null where the path is absent, the
/// column's row is null, or the value is of another class.
/// </summary>
/// <remarks>
/// <para>
/// A field of the scan whose segments are the variant column's, so that the scan reads the column,
/// and whose values <see cref="FilterEvaluator.Resolve"/> extracts from it, a batch at a time. The
/// filters over it are fenced (<see cref="VariantFilterExpr"/>): no statistic of the variant column
/// describes a path's values, so nothing prunes on them.
/// </para>
/// <para>
/// The extraction parses no row it does not walk: the keys are looked up once per run of rows of
/// the same metadata, an object's field is found by halves when its dictionary is sorted, and a
/// string is a view of the variant's own bytes. A batch allocates its output and nothing else.
/// </para>
/// </remarks>
internal sealed class VariantFieldExpr : FieldExpr
{
    /// <summary>The arena of the extracted columns' dtypes, built once.</summary>
    private static readonly DTypeArena Types = new();

    private static readonly DType Int64Type = Types.Primitive(PType.I64, Nullability.Nullable);
    private static readonly DType DoubleType = Types.Primitive(PType.F64, Nullability.Nullable);
    private static readonly DType StringType = Types.Utf8(Nullability.Nullable);
    private static readonly DType BoolType = Types.Bool(Nullability.Nullable);
    private static readonly DType PresentType = Types.Bool(Nullability.NonNullable);
    private static readonly DType BitmapType = Types.Bool(Nullability.NonNullable);
    private static readonly DType TimestampType = VortexTypes.ToDType(Timestamps, Types);

    internal VariantFieldExpr(FieldExpr column, VariantStep[] steps, VariantRead read)
        : base(column.Segments ?? column.Path.Split('.'))
    {
        Column = column;
        Steps = steps;
        Read = read;
        Text = $"{column.Path}{string.Concat(steps)}.{read}()";
        TextUtf8 = Encoding.UTF8.GetBytes(Text);
    }

    /// <summary>The type a timestamp read is, a nullable instant in UTC microseconds.</summary>
    internal static VortexType Timestamps => VortexType.Timestamp(TimeUnit.Microseconds, "UTC").Nullable;

    /// <summary>The variant column.</summary>
    internal FieldExpr Column { get; }

    internal VariantStep[] Steps { get; }

    internal VariantRead Read { get; }

    /// <summary>
    /// The path as a plan writes it: its column, its keys quoted whole, and what it is read as. A batch of states
    /// carries the path's values under this name (see <see cref="FilterEvaluator"/>).
    /// </summary>
    internal string Text { get; }

    /// <summary>The UTF-8 bytes of <see cref="Text"/>, encoded once.</summary>
    internal byte[] TextUtf8 { get; }

    /// <summary>
    /// The class a synopsis counts the values this path reads in, when it reads one top-level key of a top-level
    /// column, strictly; null otherwise. <c>AsDouble</c> reads floats too, which the double class leaves out, so a
    /// path of doubles answers it only in an object that holds no float there (<c>SynopsisPath.Floats</c>).
    /// </summary>
    internal VariantClass? CountedClass =>
        Steps is [{ IsKey: true }] && Column is ColumnFieldExpr && !Column.Path.Contains('.', StringComparison.Ordinal)
            ? Read switch
            {
                VariantRead.AsInt64 => VariantClass.Integer,
                VariantRead.AsDouble => VariantClass.Double,
                VariantRead.AsString => VariantClass.String,
                VariantRead.AsBool => VariantClass.Boolean,
                _ => null,
            }
            : null;

    internal override string Key => $"{Column.Key}{string.Concat(Steps)}.{Read}()";

    /// <summary>The type the values are read as.</summary>
    internal static VortexType TypeOf(VariantRead read) => read switch
    {
        VariantRead.AsInt64 or VariantRead.ToInt64 => VortexType.Int64.Nullable,
        VariantRead.AsDouble or VariantRead.ToDouble => VortexType.Float64.Nullable,
        VariantRead.AsString => VortexType.Utf8.Nullable,
        VariantRead.AsBool => VortexType.Bool.Nullable,
        VariantRead.AsTimestamp => Timestamps,
        _ => VortexType.Bool,
    };

    /// <summary>The same path over another reference to the column: its name in an object of a dataset.</summary>
    internal VariantFieldExpr Over(FieldExpr column) => new(column, Steps, Read);

    /// <summary>The path's values over the variant column's node, a node of their type in the same arena.</summary>
    /// <exception cref="VortexFormatException">The node is not a variant's, or a row is malformed.</exception>
    internal int Apply(CanonicalArena arena, int node)
    {
        CanonicalNode variant = arena.GetNode(node);
        if (variant.Kind == CanonicalKind.Struct && variant.FieldCount == 3)
        {
            return ApplyKeyed(arena, variant);
        }

        if (variant.Kind != CanonicalKind.Struct || variant.FieldCount != 2)
        {
            throw new VortexFormatException($"'{Column.Path}' decoded as {variant.Kind}, not as a variant's struct of its metadata and its value.");
        }

        int rows = variant.Length;
        int metadataIndex = EncodedForms.Canonical(arena, variant.GetFieldIndex(0));
        int valueIndex = EncodedForms.Canonical(arena, variant.GetFieldIndex(1));
        CanonicalNode metadata = arena.GetNode(metadataIndex);
        CanonicalNode values = arena.GetNode(valueIndex);
        ReadOnlySpan<ulong> valid = ArenaWords.Validity(arena, node);
        ReadOnlySpan<ulong> valueValid = ArenaWords.Validity(arena, valueIndex);
        ReadOnlySpan<byte> metadataViews = metadata.Views.Span;
        ReadOnlySpan<byte> valueViews = values.Views.Span;

        Output output = new(arena, Read, rows);
        Span<int> ids = Steps.Length <= 16 ? stackalloc int[Steps.Length] : new int[Steps.Length];
        ReadOnlySpan<byte> lastMetadata = default;
        bool resolved = false;
        bool sorted = false;
        // A row the path does not reach is left as the output starts it: null, or false for a presence.
        for (int row = 0; row < rows; row++)
        {
            if (!Valid(valid, row) || !Valid(valueValid, row))
            {
                continue;
            }

            ReadOnlySpan<byte> rowMetadata = Bytes(in metadata, metadataViews, row);
            if (!resolved || !Same(rowMetadata, lastMetadata))
            {
                sorted = Resolve(rowMetadata, ids);
                lastMetadata = rowMetadata;
                resolved = true;
            }

            ReadOnlySpan<byte> rowValue = Bytes(in values, valueViews, row);
            if (!Walk(rowValue, ids, sorted, out ReadOnlySpan<byte> found))
            {
                continue;
            }

            output.Write(row, found, valueViews, rowValue);
        }

        return output.Finish(arena, values);
    }

    /// <summary>
    /// The path's values over a variant read through its shredded keys alone (<see cref="Indexes.VariantKeyReads"/>):
    /// a struct of a group per key, each the key's values of other classes and its typed column. The
    /// class a typed column holds lies nowhere else, so reading that class is the typed column itself,
    /// its buffers shared; any other read takes the values of other classes, and the typed column's
    /// rows where a conversion or a presence asks for them. No variant is rebuilt.
    /// </summary>
    private int ApplyKeyed(CanonicalArena arena, CanonicalNode variant)
    {
        CanonicalNode tree = arena.GetNode(variant.GetFieldIndex(2));
        int field = Steps is [{ IsKey: true } step] ? tree.DType.IndexOfField(step.Key!) : -1;
        if (field < 0)
        {
            throw new VortexFormatException($"'{Column.Path}' was read through keys that do not hold {Text}.");
        }

        CanonicalNode group = arena.GetNode(tree.GetFieldIndex(field));
        int rows = group.Length;
        int typedIndex = EncodedForms.Canonical(arena, group.GetFieldIndex(2));
        int valueIndex = EncodedForms.Canonical(arena, group.GetFieldIndex(1));
        CanonicalNode typed = arena.GetNode(typedIndex);
        CanonicalNode values = arena.GetNode(valueIndex);
        if (Shared(arena, typed) is int shared)
        {
            return shared;
        }

        ReadOnlySpan<ulong> valueValid = ArenaWords.Validity(arena, valueIndex);
        ReadOnlySpan<ulong> typedValid = ArenaWords.Validity(arena, typedIndex);
        bool hasValues = values.Validity.Kind != ValidityKind.AllInvalid;
        ReadOnlySpan<byte> valueViews = hasValues ? values.Views.Span : default;
        Output output = new(arena, Read, rows);
        Span<byte> scratch = stackalloc byte[ShortString + 1];
        bool typedAny = typed.Validity.Kind != ValidityKind.AllInvalid;
        for (int row = 0; row < rows; row++)
        {
            if (hasValues && Valid(valueValid, row))
            {
                ReadOnlySpan<byte> value = Bytes(in values, valueViews, row);
                output.Write(row, value, valueViews, value);
            }
            else if (typedAny && Valid(typedValid, row))
            {
                // A presence reads no value; a conversion reads the typed row as the value it stands for.
                if (Read == VariantRead.Has)
                {
                    output.Write(row, default, default, default);
                }
                else if (Typed(arena, typedIndex, row, scratch, out ReadOnlySpan<byte> encoded))
                {
                    output.Write(row, encoded, default, default);
                }
            }
        }

        return output.Finish(arena, values);
    }

    /// <summary>
    /// The typed column itself, read as it is, when the read is of the class it holds and every value
    /// of that class lies in it: its buffers shared under the read's dtype. Null otherwise.
    /// </summary>
    private int? Shared(CanonicalArena arena, in CanonicalNode typed)
    {
        bool integers = typed.Kind == CanonicalKind.Primitive && typed.PType == PType.I64;
        switch (Read)
        {
            case VariantRead.AsInt64 when integers:
                return arena.AddPrimitive(Int64Type, typed.Length, typed.Validity, PType.I64, typed.Values);
            case VariantRead.AsString when typed.Kind == CanonicalKind.VarBinView && typed.DType.Kind == DTypeKind.Utf8:
            {
                int count = typed.DataBufferCount;
                Span<VortexBuffer> stack = stackalloc VortexBuffer[16];
                using Scratch<VortexBuffer> buffers = new(count, stack);
                for (int i = 0; i < count; i++)
                {
                    buffers.Span[i] = typed.GetDataBuffer(i);
                }

                return arena.AddVarBinView(StringType, typed.Length, typed.Validity, typed.Views, buffers.Span);
            }

            case VariantRead.AsBool when typed.Kind == CanonicalKind.Bool:
                return arena.AddBool(BoolType, typed.Length, typed.Validity, typed.Bits, typed.BitOffset);
            default:
                return null;
        }
    }

    /// <summary>The longest text the encoding writes as a short string, its length in its header.</summary>
    private const int ShortString = 63;

    /// <summary>
    /// Row <paramref name="row"/> of the typed column as the variant value it stands for, in
    /// <paramref name="scratch"/>: what a conversion reads of it. False for a text longer than a short
    /// string, which no conversion to a number takes.
    /// </summary>
    private static bool Typed(CanonicalArena arena, int typed, int row, Span<byte> scratch, out ReadOnlySpan<byte> encoded)
    {
        encoded = default;
        if (!LiteralReader.TryRead(arena, typed, row, out FilterLiteral literal))
        {
            return false;
        }

        switch (literal.Kind)
        {
            case FilterLiteralKind.Signed:
                scratch[0] = ParquetVariant.Primitive(6);
                BinaryPrimitives.WriteInt64LittleEndian(scratch[1..], literal.SignedValue);
                encoded = scratch[..9];
                return true;
            case FilterLiteralKind.Float:
                scratch[0] = ParquetVariant.Primitive(7);
                BinaryPrimitives.WriteDoubleLittleEndian(scratch[1..], literal.FloatValue);
                encoded = scratch[..9];
                return true;
            case FilterLiteralKind.Bool:
                scratch[0] = ParquetVariant.Primitive(literal.BoolValue ? 1 : 2);
                encoded = scratch[..1];
                return true;
            case FilterLiteralKind.Bytes when literal.BytesValue.Length <= ShortString:
                scratch[0] = (byte)((literal.BytesValue.Length << 2) | ParquetVariant.BasicShortString);
                literal.BytesValue.CopyTo(scratch[1..]);
                encoded = scratch[..(1 + literal.BytesValue.Length)];
                return true;
            default:
                return false;
        }
    }

    /// <summary>The ids of the path's keys in <paramref name="metadata"/>'s dictionary, -1 for a key it lacks; whether the dictionary is sorted.</summary>
    private bool Resolve(ReadOnlySpan<byte> metadata, Span<int> ids)
    {
        VariantDictionary dictionary = VariantDictionary.Read(metadata);
        for (int i = 0; i < Steps.Length; i++)
        {
            ids[i] = Steps[i].IsKey ? dictionary.Find(Steps[i].KeyUtf8) : -1;
        }

        return dictionary.Sorted;
    }

    /// <summary>The value at the end of the path in <paramref name="value"/>, or false where a step finds nothing.</summary>
    private bool Walk(ReadOnlySpan<byte> value, ReadOnlySpan<int> ids, bool sorted, out ReadOnlySpan<byte> found)
    {
        found = value;
        for (int i = 0; i < Steps.Length; i++)
        {
            if (found.IsEmpty)
            {
                return false;
            }

            int basic = found[0] & 0x03;
            if (Steps[i].IsKey)
            {
                if (ids[i] < 0 || basic != ParquetVariant.BasicObject || !Field(VariantNested.Read(found), ids[i], sorted, out found))
                {
                    return false;
                }
            }
            else
            {
                if (basic != ParquetVariant.BasicArray)
                {
                    return false;
                }

                VariantNested array = VariantNested.Read(found);
                if (Steps[i].Index >= array.Count)
                {
                    return false;
                }

                found = array.ValueAt(Steps[i].Index);
            }
        }

        return !found.IsEmpty;
    }

    /// <summary>
    /// The field of id <paramref name="id"/>: by halves under a sorted dictionary, whose ids follow
    /// the keys' order an object's fields are written in, one by one otherwise.
    /// </summary>
    private static bool Field(VariantNested nested, int id, bool sorted, out ReadOnlySpan<byte> field)
    {
        if (!sorted || nested.Count <= 8)
        {
            return VariantEncoding.TryField(nested, id, out field);
        }

        int low = 0;
        int high = nested.Count - 1;
        while (low <= high)
        {
            int middle = (int)((uint)(low + high) >> 1);
            int at = nested.IdAt(middle);
            if (at == id)
            {
                field = nested.ValueAt(middle);
                return true;
            }

            if (at < id)
            {
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        field = default;
        return false;
    }

    /// <summary>Whether two metadata spans are the same bytes: the same place first, which rows of a dictionary are.</summary>
    private static bool Same(ReadOnlySpan<byte> a, ReadOnlySpan<byte> b) =>
        a.Length == b.Length && (Unsafe.AreSame(ref MemoryMarshal.GetReference(a), ref MemoryMarshal.GetReference(b)) || a.SequenceEqual(b));

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static bool Valid(ReadOnlySpan<ulong> words, int row) => words.IsEmpty || ((words[row >> 6] >> (row & 63)) & 1UL) != 0;

    /// <summary>The bytes of row <paramref name="row"/> of a canonical binary node.</summary>
    private static ReadOnlySpan<byte> Bytes(scoped in CanonicalNode record, ReadOnlySpan<byte> views, int row)
    {
        ReadOnlySpan<byte> view = views.Slice(row * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
        uint size = BinaryPrimitives.ReadUInt32LittleEndian(view);
        if (size <= CanonicalSupport.MaxInlineViewLength)
        {
            return view.Slice(4, (int)size);
        }

        int buffer = (int)BinaryPrimitives.ReadUInt32LittleEndian(view[8..12]);
        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(view[12..16]);
        VortexBuffer data = record.GetDataBuffer(buffer);
        if ((ulong)offset + size > (ulong)(uint)data.Length)
        {
            throw new VortexFormatException($"A variant's row {row} spans [{offset}, {(ulong)offset + size}) of a data buffer of {data.Length} bytes.");
        }

        return data.Span.Slice((int)offset, (int)size);
    }

    /// <summary>The column a path's values are written into: values, a validity bit per row, and for text views of the variant's bytes.</summary>
    private ref struct Output
    {
        private readonly VariantRead _read;
        private readonly int _rows;
        private readonly VortexBuffer _values;
        private readonly Span<byte> _into;
        private readonly VortexBuffer _validity;
        private readonly Span<byte> _validBits;
        private int _present;

        internal Output(CanonicalArena arena, VariantRead read, int rows)
        {
            _read = read;
            _rows = rows;
            int width = read switch
            {
                VariantRead.AsString => CanonicalSupport.ViewSize,
                VariantRead.AsBool or VariantRead.Has => 0,
                _ => sizeof(long),
            };

            if (width == 0)
            {
                _values = arena.Allocate(Math.Max(CanonicalSupport.BitmapByteCount(rows), 1), 1, out _into);
            }
            else
            {
                _values = arena.Allocate(Math.Max(rows * width, width), 64, out _into);
            }

            _validity = arena.Allocate(Math.Max(CanonicalSupport.BitmapByteCount(rows), 1), 1, out _validBits);
            _into.Clear();
            _validBits.Clear();
        }

        /// <summary>Writes the value <paramref name="found"/> at <paramref name="row"/>, when it is of the class read.</summary>
        internal void Write(int row, scoped ReadOnlySpan<byte> found, scoped ReadOnlySpan<byte> valueViews, scoped ReadOnlySpan<byte> rowValue)
        {
            if (_read == VariantRead.Has)
            {
                Set(_into, row);
                Present(row);
                return;
            }

            VariantScalar scalar = VariantScalar.Read(found);
            switch (_read)
            {
                case VariantRead.AsInt64 when scalar.IsInteger:
                    BinaryPrimitives.WriteInt64LittleEndian(_into[(row * 8)..], (long)scalar.Integer);
                    break;
                case VariantRead.AsDouble when scalar.Kind is VariantKind.Float or VariantKind.Double:
                    BinaryPrimitives.WriteDoubleLittleEndian(_into[(row * 8)..], scalar.Number);
                    break;
                case VariantRead.AsTimestamp when scalar.Kind == VariantKind.Timestamp:
                    BinaryPrimitives.WriteInt64LittleEndian(_into[(row * 8)..], scalar.Microseconds);
                    break;
                case VariantRead.AsBool when scalar.Kind == VariantKind.Bool:
                    if (scalar.Integer != 0)
                    {
                        Set(_into, row);
                    }

                    break;
                case VariantRead.ToInt64 when scalar.TryToInt64(out long integer):
                    BinaryPrimitives.WriteInt64LittleEndian(_into[(row * 8)..], integer);
                    break;
                case VariantRead.ToDouble when scalar.TryToDouble(out double number):
                    BinaryPrimitives.WriteDoubleLittleEndian(_into[(row * 8)..], number);
                    break;
                case VariantRead.AsString when scalar.Kind == VariantKind.String:
                    View(row, scalar.Bytes, valueViews, rowValue);
                    break;
                default:
                    return;
            }

            Present(row);
        }

        /// <summary>The view of a string: inline when it fits, otherwise into the data buffer the variant's value lies in.</summary>
        private readonly void View(int row, scoped ReadOnlySpan<byte> text, scoped ReadOnlySpan<byte> valueViews, scoped ReadOnlySpan<byte> rowValue)
        {
            Span<byte> view = _into.Slice(row * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
            if (text.Length <= CanonicalSupport.MaxInlineViewLength)
            {
                CanonicalSupport.WriteInlineView(view, text);
                return;
            }

            // Longer than twelve bytes, so its value is too, and lies in a data buffer at an offset.
            ReadOnlySpan<byte> source = valueViews.Slice(row * CanonicalSupport.ViewSize, CanonicalSupport.ViewSize);
            int buffer = (int)BinaryPrimitives.ReadUInt32LittleEndian(source[8..12]);
            int offset = (int)BinaryPrimitives.ReadUInt32LittleEndian(source[12..16]);
            int within = (int)Unsafe.ByteOffset(ref MemoryMarshal.GetReference(rowValue), ref MemoryMarshal.GetReference(text));
            CanonicalSupport.WriteReferenceView(view, text.Length, text, buffer, offset + within);
        }

        private void Present(int row)
        {
            Set(_validBits, row);
            _present++;
        }

        private static void Set(Span<byte> bits, int row) => bits[row >> 3] |= (byte)(1 << (row & 7));

        /// <summary>The node of the column written.</summary>
        internal readonly int Finish(CanonicalArena arena, in CanonicalNode values)
        {
            if (_read == VariantRead.Has)
            {
                return arena.AddBool(PresentType, _rows, Validity.NonNullable, _values, 0);
            }

            Validity validity = _present == _rows ? Validity.AllValid
                : _present == 0 ? Validity.AllInvalid
                : Validity.Bitmap(arena.AddBool(BitmapType, _rows, Validity.NonNullable, _validity, 0));
            switch (_read)
            {
                case VariantRead.AsBool:
                    return arena.AddBool(BoolType, _rows, validity, _values, 0);
                case VariantRead.AsString:
                {
                    // The views name the value column's data buffers by index, so they are its own.
                    int count = values.DataBufferCount;
                    Span<VortexBuffer> stack = stackalloc VortexBuffer[16];
                    using Scratch<VortexBuffer> buffers = new(count, stack);
                    for (int i = 0; i < count; i++)
                    {
                        buffers.Span[i] = values.GetDataBuffer(i);
                    }

                    return arena.AddVarBinView(StringType, _rows, validity, _values, buffers.Span);
                }

                case VariantRead.AsDouble:
                case VariantRead.ToDouble:
                    return arena.AddPrimitive(DoubleType, _rows, validity, PType.F64, _values);
                case VariantRead.AsTimestamp:
                    return arena.AddExtension(TimestampType, _rows, arena.AddPrimitive(TimestampType.StorageType, _rows, validity, PType.I64, _values));
                default:
                    return arena.AddPrimitive(Int64Type, _rows, validity, PType.I64, _values);
            }
        }
    }
}

/// <summary>
/// A filter over paths of variant columns: the predicate itself, behind a node no pruner knows, so
/// that each answers it as one it cannot rule on and keeps every row for the evaluator.
/// </summary>
/// <remarks>
/// The variant column's statistics describe the column, not a path in it: its null count says
/// nothing of whether a path is absent, and a zone map has no bound of a path's values. A walk that
/// reads the filter's columns or evaluates it looks through the fence; one that prunes does not.
/// </remarks>
internal sealed class VariantFilterExpr : VortexExpr
{
    private VariantFilterExpr(VortexExpr inner) => Inner = inner;

    /// <summary>The predicate over the paths.</summary>
    internal VortexExpr Inner { get; }

    /// <inheritdoc/>
    internal override ExprKind Kind => ExprKind.VariantPath;

    /// <inheritdoc/>
    internal override int Height => Inner.Height + 1;

    /// <inheritdoc/>
    internal override void CollectFields(ICollection<string> paths) => Inner.CollectFields(paths);

    /// <summary><paramref name="predicate"/> behind a fence when one of its columns is a variant's path; itself otherwise.</summary>
    internal static VortexExpr Of(VortexExpr predicate) => predicate switch
    {
        ComparisonExpr { Field: VariantFieldExpr } => new VariantFilterExpr(predicate),
        NullCheckExpr { Field: VariantFieldExpr } => new VariantFilterExpr(predicate),
        InExpr { Field: VariantFieldExpr } => new VariantFilterExpr(predicate),
        TypedInExpr { Field: VariantFieldExpr } => new VariantFilterExpr(predicate),
        StringMatchExpr { Field: VariantFieldExpr } => new VariantFilterExpr(predicate),
        ColumnComparisonExpr columns when columns.Left is VariantFieldExpr || columns.Right is VariantFieldExpr => new VariantFilterExpr(predicate),
        _ => predicate,
    };

    /// <summary>The same filter with its inner predicate rewritten.</summary>
    internal VariantFilterExpr With(VortexExpr inner) =>
        ReferenceEquals(inner, Inner) ? this : inner as VariantFilterExpr ?? new VariantFilterExpr(inner);
}
