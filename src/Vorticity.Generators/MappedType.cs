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
}

/// <summary>What a mapped .NET type is.</summary>
internal enum ValueKind : byte
{
    Scalar,
    Enum,
    List,
    Record,
    Extension,
}

/// <summary>A .NET type as the column mapping sees it: its family, its nullability, and for a list its element.</summary>
internal sealed class MappedType
{
    private MappedType(ValueKind kind, ScalarKind scalar, bool nullable, ITypeSymbol core, MappedType? element, string? error)
    {
        Kind = kind;
        Scalar = scalar;
        IsNullable = nullable;
        Core = core;
        Element = element;
        Error = error;
    }

    public ValueKind Kind { get; }

    /// <summary>The scalar family of a scalar, or the underlying integer's of an enum.</summary>
    public ScalarKind Scalar { get; }

    /// <summary>Whether the type is <c>T?</c>: a <see cref="System.Nullable{T}"/> or an annotated reference type.</summary>
    public bool IsNullable { get; }

    /// <summary>The type without its nullability.</summary>
    public ITypeSymbol Core { get; }

    public MappedType? Element { get; }

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
            : null;
        if (named is ScalarKind scalar)
        {
            return new MappedType(ValueKind.Scalar, scalar, nullable, core, null, null);
        }

        if (core.SpecialType == SpecialType.System_Char)
        {
            return Fail(core, "a char is not a dtype; use string");
        }

        if (core is INamedTypeSymbol generic && KnownSymbols.Is(generic, known.ReadOnlyMemory))
        {
            ITypeSymbol elementType = generic.TypeArguments[0];
            if (elementType.SpecialType == SpecialType.System_Byte)
            {
                return new MappedType(ValueKind.Scalar, ScalarKind.Binary, nullable, core, null, null);
            }

            MappedType element = Map(elementType, known);
            if (element.Error is not null)
            {
                return Fail(core, $"its element type {element.Core.ToDisplayString()} does not map to a column: {element.Error}");
            }

            if (element.Kind == ValueKind.Record)
            {
                return Fail(core, "a list of records has no typed column to read it through; declare a list of a scalar type");
            }

            return new MappedType(ValueKind.List, default, nullable, core, element, null);
        }

        if (core is IArrayTypeSymbol array)
        {
            return Fail(core, $"an array is not a column type; declare the member ReadOnlyMemory<{array.ElementType.ToDisplayString()}>");
        }

        if (core is INamedTypeSymbol memory && KnownSymbols.Is(memory, known.Memory))
        {
            return Fail(core, $"declare the member ReadOnlyMemory<{memory.TypeArguments[0].ToDisplayString()}>");
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
        _ => null,
    };
}
