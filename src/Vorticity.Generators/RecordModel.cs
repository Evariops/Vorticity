using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Vorticity.Generators;

/// <summary>How <c>ReadRows</c> gives a member its value.</summary>
internal enum MemberFill : byte
{
    /// <summary>As an argument of the constructor.</summary>
    Constructor,

    /// <summary>In the object initializer, for an <c>init</c> or <c>required</c> member.</summary>
    Initializer,

    /// <summary>By assignment, once the row exists.</summary>
    Assignment,
}

/// <summary>A mapped .NET type, with the C# the emitter writes for it.</summary>
/// <param name="Kind">The family.</param>
/// <param name="Scalar">The scalar family, or an enum's underlying integer's.</param>
/// <param name="IsNullable">Whether the type is <c>T?</c>.</param>
/// <param name="MemberType">The type as declared, fully qualified, with its nullability.</param>
/// <param name="CoreType">The type without its nullability.</param>
/// <param name="UnderlyingType">An enum's underlying integer; the core type otherwise.</param>
/// <param name="Schema">The <c>VortexType</c> expression of the column.</param>
/// <param name="Element">A list's element.</param>
internal sealed record ValueModel(
    ValueKind Kind,
    ScalarKind Scalar,
    bool IsNullable,
    string MemberType,
    string CoreType,
    string UnderlyingType,
    string Schema,
    ValueModel? Element)
{
    /// <summary>Whether the values are an <c>IBinaryNumber</c> the columns expose as a span.</summary>
    public bool IsNumber => Kind is ValueKind.Scalar or ValueKind.Enum && Scalar is >= ScalarKind.Int8 and <= ScalarKind.Float64;

    /// <summary>The <c>T</c> of <c>Column&lt;T&gt;</c> and <c>ColumnBuilder&lt;T&gt;</c>: an enum as its integer, a list without its nullability.</summary>
    public string ColumnType => Kind switch
    {
        ValueKind.Enum => UnderlyingType + (IsNullable ? "?" : string.Empty),
        ValueKind.List => "global::System.ReadOnlyMemory<" + Element!.ColumnType + ">",
        _ => MemberType,
    };

    /// <summary>
    /// The <c>T</c> of the builders <c>WriteRows</c> appends to: <see cref="ColumnType"/> with text
    /// unannotated, because the text appends extend <c>ColumnBuilder&lt;string&gt;</c> and a
    /// <c>ColumnBuilder&lt;string?&gt;</c> receiver is a nullability mismatch; a list keeps its
    /// elements' annotations, which its generic appends infer from the span they take.
    /// </summary>
    public string WriteColumnType => Kind == ValueKind.Scalar && Scalar == ScalarKind.Utf8 ? CoreType : ColumnType;

    /// <summary>The <c>T</c> of <c>Sym&lt;T&gt;</c>: the member's own type, a list without its nullability.</summary>
    public string SymbolType => Kind == ValueKind.List ? "global::System.ReadOnlyMemory<" + Element!.MemberType + ">" : MemberType;

    /// <summary>Whether a list's elements are written as they are, with no conversion per element.</summary>
    public bool ElementsAsDeclared => Element is not null && Element.ColumnType == Element.MemberType;
}

/// <summary>A member of a record: a column.</summary>
/// <param name="Name">The member's C# name.</param>
/// <param name="ColumnName">The column's name.</param>
/// <param name="Fill">How reading gives the member its value.</param>
/// <param name="Value">The member's type.</param>
/// <param name="DeclaringType">The type that declares the member, a base type for an inherited one.</param>
internal sealed record MemberModel(string Name, string ColumnName, MemberFill Fill, ValueModel Value, string DeclaringType);

/// <summary>A type that contains a record, redeclared partial around it.</summary>
/// <param name="Keyword">The declaration keyword: <c>class</c>, <c>struct</c>, <c>record</c>, <c>record struct</c> or <c>interface</c>.</param>
/// <param name="Name">The type's name.</param>
internal sealed record ContainerModel(string Keyword, string Name);

/// <summary>Everything the emitter needs for one record type.</summary>
internal sealed record RecordModel(
    string? Namespace,
    EquatableArray<ContainerModel> Containers,
    string Keyword,
    string Name,
    string FullName,
    EquatableArray<MemberModel> Members,
    bool HasConstructorArguments,
    EquatableArray<int> ConstructorArguments,
    bool NeedsSchemaHelper,
    bool NeedsExtensionHelper,
    GeneratedNames Names,
    string? ExtensionsName,
    string ExtensionsAccessibility,
    string HintName);

/// <summary>Which of the generated members clash with the type's own, or hide a base type's.</summary>
internal sealed record GeneratedNames(
    bool ExplicitSchema,
    bool ExplicitReadRows,
    bool ExplicitWriteRows,
    bool EmitColumnNames,
    bool HidesSchema,
    bool HidesReadRows,
    bool HidesWriteRows,
    bool HidesColumnNames,
    string ProbeName,
    string ColumnsName,
    string BuilderName,
    EquatableArray<string> DeconstructNames);

/// <summary>A source location that compares by value.</summary>
internal sealed record LocationModel(string Path, TextSpan Span, LinePositionSpan LineSpan)
{
    public static LocationModel? From(Location? location) =>
        location is { IsInSource: true } ? new LocationModel(location.SourceTree!.FilePath, location.SourceSpan, location.GetLineSpan().Span) : null;

    public Location ToLocation() => Location.Create(Path, Span, LineSpan);
}

/// <summary>A diagnostic that compares by value, reported when the source is produced.</summary>
internal sealed record DiagnosticModel(string Id, LocationModel? Location, EquatableArray<string> Arguments);

/// <summary>What the analysis of one record type yields: the model to emit, unless a diagnostic stops it.</summary>
internal sealed record RecordResult(RecordModel? Model, EquatableArray<DiagnosticModel> Diagnostics);
