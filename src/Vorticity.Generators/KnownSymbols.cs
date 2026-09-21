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
        Half = compilation.GetTypeByMetadataName("System.Half");
        DateOnly = compilation.GetTypeByMetadataName("System.DateOnly");
        TimeOnly = compilation.GetTypeByMetadataName("System.TimeOnly");
        DateTimeOffset = compilation.GetTypeByMetadataName("System.DateTimeOffset");
        Guid = compilation.GetTypeByMetadataName("System.Guid");
        ReadOnlyMemory = compilation.GetTypeByMetadataName("System.ReadOnlyMemory`1");
        Memory = compilation.GetTypeByMetadataName("System.Memory`1");
    }

    public INamedTypeSymbol? RecordAttribute { get; }

    public INamedTypeSymbol? ColumnAttribute { get; }

    public INamedTypeSymbol? IgnoreAttribute { get; }

    public INamedTypeSymbol? RecordInterface { get; }

    public INamedTypeSymbol? ExtensionInterface { get; }

    public INamedTypeSymbol? VortexDecimal { get; }

    public INamedTypeSymbol? Half { get; }

    public INamedTypeSymbol? DateOnly { get; }

    public INamedTypeSymbol? TimeOnly { get; }

    public INamedTypeSymbol? DateTimeOffset { get; }

    public INamedTypeSymbol? Guid { get; }

    public INamedTypeSymbol? ReadOnlyMemory { get; }

    public INamedTypeSymbol? Memory { get; }

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
