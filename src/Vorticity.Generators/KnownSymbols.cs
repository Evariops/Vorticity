using Microsoft.CodeAnalysis;

namespace Vorticity.Generators;

/// <summary>The types of the core and of the base library the package recognises, resolved once per compilation.</summary>
internal sealed class KnownSymbols
{
    public KnownSymbols(Compilation compilation)
    {
        RecordAttribute = compilation.GetTypeByMetadataName("Vorticity.VortexRecordAttribute");
        ColumnAttribute = compilation.GetTypeByMetadataName("Vorticity.VortexColumnAttribute");
        IgnoreAttribute = compilation.GetTypeByMetadataName("Vorticity.VortexIgnoreAttribute");
        RecordInterface = compilation.GetTypeByMetadataName("Vorticity.IVortexRecord`1");
        ExtensionInterface = compilation.GetTypeByMetadataName("Vorticity.IVortexExtension`1");
        VortexDecimal = compilation.GetTypeByMetadataName("Vorticity.VortexDecimal");
        Column = compilation.GetTypeByMetadataName("Vorticity.Column`1");
        Columns = compilation.GetTypeByMetadataName("Vorticity.Columns`1");
        BatchView = compilation.GetTypeByMetadataName("Vorticity.BatchView");
        Selection = compilation.GetTypeByMetadataName("Vorticity.Selection");
        DictionaryView = compilation.GetTypeByMetadataName("Vorticity.DictionaryView`1");
        RunEndView = compilation.GetTypeByMetadataName("Vorticity.RunEndView`1");
        RecordBatch = compilation.GetTypeByMetadataName("Vorticity.RecordBatch");
        TypedScan = compilation.GetTypeByMetadataName("Vorticity.Scan`1");
        Sym = compilation.GetTypeByMetadataName("Vorticity.Sym`1");
        ToolScan = compilation.GetTypeByMetadataName("Vorticity.Scan");
        FilterHandler = compilation.GetTypeByMetadataName("Vorticity.FilterHandler");
        RowRange = compilation.GetTypeByMetadataName("Vorticity.RowRange");
        Half = compilation.GetTypeByMetadataName("System.Half");
        DateOnly = compilation.GetTypeByMetadataName("System.DateOnly");
        TimeOnly = compilation.GetTypeByMetadataName("System.TimeOnly");
        DateTimeOffset = compilation.GetTypeByMetadataName("System.DateTimeOffset");
        Guid = compilation.GetTypeByMetadataName("System.Guid");
        TimeSpan = compilation.GetTypeByMetadataName("System.TimeSpan");
        Int128 = compilation.GetTypeByMetadataName("System.Int128");
        UInt128 = compilation.GetTypeByMetadataName("System.UInt128");
        BigInteger = compilation.GetTypeByMetadataName("System.Numerics.BigInteger");
        ReadOnlyMemory = compilation.GetTypeByMetadataName("System.ReadOnlyMemory`1");
        Memory = compilation.GetTypeByMetadataName("System.Memory`1");
        List = compilation.GetTypeByMetadataName("System.Collections.Generic.List`1");
        Dictionaries =
        [
            compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2"),
            compilation.GetTypeByMetadataName("System.Collections.Generic.IDictionary`2"),
            compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyDictionary`2"),
        ];
        ImmutableArray = compilation.GetTypeByMetadataName("System.Collections.Immutable.ImmutableArray`1");
        Sequences =
        [
            compilation.GetTypeByMetadataName("System.Collections.Generic.IEnumerable`1"),
            compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyList`1"),
            compilation.GetTypeByMetadataName("System.Collections.Generic.IReadOnlyCollection`1"),
            compilation.GetTypeByMetadataName("System.Collections.Generic.IList`1"),
            compilation.GetTypeByMetadataName("System.Collections.Generic.ICollection`1"),
        ];
        Span = compilation.GetTypeByMetadataName("System.Span`1");
        ReadOnlySpan = compilation.GetTypeByMetadataName("System.ReadOnlySpan`1");
    }

    public INamedTypeSymbol? RecordAttribute { get; }

    public INamedTypeSymbol? ColumnAttribute { get; }

    public INamedTypeSymbol? IgnoreAttribute { get; }

    public INamedTypeSymbol? RecordInterface { get; }

    public INamedTypeSymbol? ExtensionInterface { get; }

    public INamedTypeSymbol? VortexDecimal { get; }

    public INamedTypeSymbol? Column { get; }

    public INamedTypeSymbol? Columns { get; }

    public INamedTypeSymbol? BatchView { get; }

    public INamedTypeSymbol? Selection { get; }

    public INamedTypeSymbol? DictionaryView { get; }

    public INamedTypeSymbol? RunEndView { get; }

    /// <summary>The symbol of a column, an aggregate or a key in a scan's lambdas.</summary>
    public INamedTypeSymbol? Sym { get; }

    public INamedTypeSymbol? RecordBatch { get; }

    public INamedTypeSymbol? TypedScan { get; }

    public INamedTypeSymbol? ToolScan { get; }

    public INamedTypeSymbol? FilterHandler { get; }

    public INamedTypeSymbol? RowRange { get; }

    public INamedTypeSymbol? Half { get; }

    public INamedTypeSymbol? DateOnly { get; }

    public INamedTypeSymbol? TimeOnly { get; }

    public INamedTypeSymbol? DateTimeOffset { get; }

    public INamedTypeSymbol? Guid { get; }

    public INamedTypeSymbol? TimeSpan { get; }

    public INamedTypeSymbol? Int128 { get; }

    public INamedTypeSymbol? UInt128 { get; }

    public INamedTypeSymbol? BigInteger { get; }

    public INamedTypeSymbol? ReadOnlyMemory { get; }

    public INamedTypeSymbol? Memory { get; }

    public INamedTypeSymbol? List { get; }

    /// <summary>The dictionary types a map member may be declared as, read back as a <c>Dictionary&lt;TKey, TValue&gt;</c>.</summary>
    public INamedTypeSymbol?[] Dictionaries { get; }

    public INamedTypeSymbol? ImmutableArray { get; }

    /// <summary>The interfaces an array implements, which a member may be declared as and read back into an array through.</summary>
    public INamedTypeSymbol?[] Sequences { get; }

    public INamedTypeSymbol? Span { get; }

    public INamedTypeSymbol? ReadOnlySpan { get; }

    public static bool Is(ITypeSymbol? type, INamedTypeSymbol? known) =>
        type is not null && known is not null && SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, known);

    /// <summary>Whether <paramref name="type"/> implements <paramref name="selfInterface"/> closed over itself, as <c>IVortexRecord&lt;T&gt;</c> is.</summary>
    public static bool ImplementsSelf(ITypeSymbol type, INamedTypeSymbol? selfInterface)
    {
        if (selfInterface is null)
        {
            return false;
        }

        foreach (INamedTypeSymbol implemented in type.AllInterfaces)
        {
            if (SymbolEqualityComparer.Default.Equals(implemented.OriginalDefinition, selfInterface)
                && SymbolEqualityComparer.Default.Equals(implemented.TypeArguments[0], type))
            {
                return true;
            }
        }

        return false;
    }

    public bool IsRecord(ITypeSymbol type) =>
        ImplementsSelf(type, RecordInterface) || (RecordAttribute is not null && HasAttribute(type, RecordAttribute));

    public static bool HasAttribute(ISymbol symbol, INamedTypeSymbol attribute)
    {
        foreach (AttributeData data in symbol.GetAttributes())
        {
            if (SymbolEqualityComparer.Default.Equals(data.AttributeClass, attribute))
            {
                return true;
            }
        }

        return false;
    }
}
