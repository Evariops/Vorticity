using Microsoft.CodeAnalysis;

namespace Vorticity.Generators;

/// <summary>The dtype family a scalar .NET type maps to.</summary>
internal enum ScalarKind : byte
{
    Bool,
    Int8,
    Int16,
    Int32,
    Int64,
    UInt8,
    UInt16,
    UInt32,
    UInt64,
    Float16,
    Float32,
    Float64,
    Utf8,
    Binary,
    Decimal,
    WideDecimal,
    Date,
    Time,
    Timestamp,
    ZonedTimestamp,
    Uuid,

    /// <summary>A <see cref="System.TimeSpan"/>, an i64 of ticks.</summary>
    Duration,

    /// <summary>An <see cref="System.Int128"/>, a decimal of scale 0.</summary>
    Int128,

    /// <summary>A <see cref="System.UInt128"/>, a decimal of scale 0.</summary>
    UInt128,

    /// <summary>A <see cref="System.Numerics.BigInteger"/>, a decimal of scale 0.</summary>
    BigInteger,
}

/// <summary>The .NET container of a list, or of the bytes of a binary value.</summary>
internal enum ListShape : byte
{
    /// <summary><c>ReadOnlyMemory&lt;T&gt;</c>.</summary>
    ReadOnlyMemory,

    /// <summary><c>Memory&lt;T&gt;</c>.</summary>
    Memory,

    /// <summary><c>T[]</c>.</summary>
    Array,

    /// <summary><c>List&lt;T&gt;</c>.</summary>
    List,

    /// <summary><c>ImmutableArray&lt;T&gt;</c>.</summary>
    ImmutableArray,

    /// <summary>An interface an array implements, <c>IReadOnlyList&lt;T&gt;</c> or <c>IEnumerable&lt;T&gt;</c>: read back as an array.</summary>
    Sequence,
}

/// <summary>What a mapped .NET type is.</summary>
internal enum ValueKind : byte
{
    Scalar,
    Enum,
    List,
    Record,
    Extension,

    /// <summary>A dictionary: a map column of its keys and values.</summary>
    Map,
}

/// <summary>A .NET type as the column mapping sees it: its family, its nullability, and for a list its element.</summary>
internal sealed class MappedType
{
    private MappedType(ValueKind kind, ScalarKind scalar, bool nullable, ITypeSymbol core, MappedType? element, string? error,
        ListShape shape = ListShape.ReadOnlyMemory, ITypeSymbol? elementType = null, MappedType? key = null, ITypeSymbol? keyType = null)
    {
        Key = key;
        KeyType = keyType;
        Kind = kind;
        Scalar = scalar;
        IsNullable = nullable;
        Core = core;
        Element = element;
        Error = error;
        Shape = shape;
        ElementType = elementType;
    }

    public ValueKind Kind { get; }

    /// <summary>The scalar family of a scalar, or the underlying integer's of an enum.</summary>
    public ScalarKind Scalar { get; }

    /// <summary>Whether the type is <c>T?</c>: a <see cref="System.Nullable{T}"/> or an annotated reference type.</summary>
    public bool IsNullable { get; }

    /// <summary>The type without its nullability.</summary>
    public ITypeSymbol Core { get; }

    public MappedType? Element { get; }

    /// <summary>The container of a list, or of a binary value's bytes.</summary>
    public ListShape Shape { get; }

    /// <summary>A list's element type, or a map's value type, as declared, with its nullability.</summary>
    public ITypeSymbol? ElementType { get; }

    /// <summary>A map's key.</summary>
    public MappedType? Key { get; }

    /// <summary>A map's key type as declared.</summary>
    public ITypeSymbol? KeyType { get; }

    /// <summary>Why the type has no mapping, or null when it has one.</summary>
    public string? Error { get; }

    public static MappedType Map(ITypeSymbol type, KnownSymbols known)
    {
        bool nullable = false;
        ITypeSymbol core = type;
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } wrapped)
        {
            nullable = true;
            core = wrapped.TypeArguments[0];
        }
        else if (type.IsReferenceType && type.NullableAnnotation == NullableAnnotation.Annotated)
        {
            nullable = true;
            core = type.WithNullableAnnotation(NullableAnnotation.NotAnnotated);
        }

        if (core is INamedTypeSymbol { TypeKind: TypeKind.Enum, EnumUnderlyingType: { } underlying })
        {
            return FromSpecialType(underlying.SpecialType) is ScalarKind integer
                ? new MappedType(ValueKind.Enum, integer, nullable, core, null, null)
                : Fail(core, "the enum's underlying type has no mapping to a dtype");
        }

        if (FromSpecialType(core.SpecialType) is ScalarKind special)
        {
            return new MappedType(ValueKind.Scalar, special, nullable, core, null, null);
        }

        ScalarKind? named =
            KnownSymbols.Is(core, known.Half) ? ScalarKind.Float16
            : KnownSymbols.Is(core, known.DateOnly) ? ScalarKind.Date
            : KnownSymbols.Is(core, known.TimeOnly) ? ScalarKind.Time
            : KnownSymbols.Is(core, known.DateTimeOffset) ? ScalarKind.ZonedTimestamp
            : KnownSymbols.Is(core, known.Guid) ? ScalarKind.Uuid
            : KnownSymbols.Is(core, known.VortexDecimal) ? ScalarKind.WideDecimal
            : KnownSymbols.Is(core, known.TimeSpan) ? ScalarKind.Duration
            : KnownSymbols.Is(core, known.Int128) ? ScalarKind.Int128
            : KnownSymbols.Is(core, known.UInt128) ? ScalarKind.UInt128
            : KnownSymbols.Is(core, known.BigInteger) ? ScalarKind.BigInteger
            : null;
        if (named is ScalarKind scalar)
        {
            return new MappedType(ValueKind.Scalar, scalar, nullable, core, null, null);
        }

        if (core is INamedTypeSymbol { TypeArguments.Length: 2 } dictionary
            && System.Array.Exists(known.Dictionaries, candidate => KnownSymbols.Is(dictionary, candidate)))
        {
            MappedType key = Map(dictionary.TypeArguments[0], known);
            MappedType value = Map(dictionary.TypeArguments[1], known);
            string? problem = key.Error is not null ? $"its key type {key.Core.ToDisplayString()} does not map to a column: {key.Error}"
                : value.Error is not null ? $"its value type {value.Core.ToDisplayString()} does not map to a column: {value.Error}"
                : key.IsNullable ? "a map's keys are never null; declare the key type without '?'"
                : key.Kind is not (ValueKind.Scalar or ValueKind.Enum) || value.Kind is not (ValueKind.Scalar or ValueKind.Enum)
                    ? "a map's keys and values are scalars; hold anything deeper in a record"
                : null;
            return problem is not null
                ? Fail(core, problem)
                : new MappedType(ValueKind.Map, default, nullable, core, value, null, default, dictionary.TypeArguments[1], key, dictionary.TypeArguments[0]);
        }

        if (ListOf(core, known) is (ITypeSymbol elementType, ListShape shape))
        {
            // Bytes in a buffer are a binary value; bytes in a list or an interface are a list of u8.
            if (elementType.SpecialType == SpecialType.System_Byte && shape is ListShape.ReadOnlyMemory or ListShape.Memory or ListShape.Array)
            {
                return new MappedType(ValueKind.Scalar, ScalarKind.Binary, nullable, core, null, null, shape);
            }

            MappedType element = Map(elementType, known);
            if (element.Error is not null)
            {
                return Fail(core, $"its element type {element.Core.ToDisplayString()} does not map to a column: {element.Error}");
            }

            if (element.Kind == ValueKind.Record && element.IsNullable)
            {
                return Fail(core, "a list of records holds records, not nulls; declare its element type without '?'");
            }

            if (element.Kind == ValueKind.List && element.Element!.Kind == ValueKind.Record)
            {
                return Fail(core, "a list of lists of records has no typed column to read it through; hold the inner lists in a record");
            }

            return new MappedType(ValueKind.List, default, nullable, core, element, null, shape, elementType);
        }

        if (core is IArrayTypeSymbol)
        {
            return Fail(core, "an array of more than one dimension is not a column type; declare an array of arrays");
        }

        if (KnownSymbols.ImplementsSelf(core, known.ExtensionInterface))
        {
            return nullable
                ? Fail(core, "a registered extension type is read and written without nulls; declare the member without '?'")
                : new MappedType(ValueKind.Extension, default, false, core, null, null);
        }

        if (core.TypeKind is TypeKind.Class or TypeKind.Struct && known.IsRecord(core))
        {
            return new MappedType(ValueKind.Record, default, nullable, core, null, null);
        }

        return Fail(core, "the type has no mapping to a dtype");
    }

    /// <summary>The element and the container of a type a list is declared as, or null for any other type.</summary>
    private static (ITypeSymbol Element, ListShape Shape)? ListOf(ITypeSymbol core, KnownSymbols known)
    {
        if (core is IArrayTypeSymbol { Rank: 1 } array)
        {
            return (array.ElementType, ListShape.Array);
        }

        if (core is not INamedTypeSymbol { TypeArguments.Length: 1 } generic)
        {
            return null;
        }

        ListShape? shape =
            KnownSymbols.Is(generic, known.ReadOnlyMemory) ? ListShape.ReadOnlyMemory
            : KnownSymbols.Is(generic, known.Memory) ? ListShape.Memory
            : KnownSymbols.Is(generic, known.List) ? ListShape.List
            : KnownSymbols.Is(generic, known.ImmutableArray) ? ListShape.ImmutableArray
            : System.Array.Exists(known.Sequences, sequence => KnownSymbols.Is(generic, sequence)) ? ListShape.Sequence
            : null;
        return shape is ListShape found ? (generic.TypeArguments[0], found) : null;
    }

    private static MappedType Fail(ITypeSymbol core, string error) =>
        new MappedType(ValueKind.Scalar, default, false, core, null, error);

    private static ScalarKind? FromSpecialType(SpecialType type) => type switch
    {
        SpecialType.System_Boolean => ScalarKind.Bool,
        SpecialType.System_SByte => ScalarKind.Int8,
        SpecialType.System_Int16 => ScalarKind.Int16,
        SpecialType.System_Int32 => ScalarKind.Int32,
        SpecialType.System_Int64 => ScalarKind.Int64,
        SpecialType.System_Byte => ScalarKind.UInt8,
        SpecialType.System_UInt16 => ScalarKind.UInt16,
        SpecialType.System_UInt32 => ScalarKind.UInt32,
        SpecialType.System_UInt64 => ScalarKind.UInt64,
        SpecialType.System_Single => ScalarKind.Float32,
        SpecialType.System_Double => ScalarKind.Float64,
        SpecialType.System_String => ScalarKind.Utf8,
        SpecialType.System_Decimal => ScalarKind.Decimal,
        SpecialType.System_DateTime => ScalarKind.Timestamp,

        // A UTF-16 code unit is a u16, and a native integer the 64 bits it is where a record runs.
        SpecialType.System_Char => ScalarKind.UInt16,
        SpecialType.System_IntPtr => ScalarKind.Int64,
        SpecialType.System_UIntPtr => ScalarKind.UInt64,
        _ => null,
    };
}
