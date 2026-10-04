using Microsoft.CodeAnalysis;

namespace Vorticity.Generators;

/// <summary>Every diagnostic the package reports.</summary>
internal static class Descriptors
{
    private const string HelpLink = "docs/guide/diagnostics.md#";

    public static readonly DiagnosticDescriptor BorrowedSpanEscapes = new DiagnosticDescriptor(
        "VX1001",
        "A borrowed span outlives its batch",
        "'{0}' is borrowed from a column of the current batch and is valid only inside the body of the enumeration that produced it; '{1}' is declared outside that body",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "A span, a column or a view taken from a delivered batch points into buffers that the next MoveNextAsync reuses. Declare the variable inside the enumeration body, or copy the values with ToArray or ToOwned.",
        HelpLink + "vx1001");

    public static readonly DiagnosticDescriptor BatchNotDisposed = new DiagnosticDescriptor(
        "VX1002",
        "An owned batch is never disposed",
        "The RecordBatch from '{0}' is never disposed; dispose it, or hand it on",
        "Reliability",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "A RecordBatch holds pooled buffers until it is disposed. Dispose it with using or Dispose, or pass it on, return it or store it so that its new owner does.",
        HelpLink + "vx1002");

    public static readonly DiagnosticDescriptor HoleHasNoDType = new DiagnosticDescriptor(
        "VX1003",
        "A filter hole has no column type",
        "The hole '{0}' is of type '{1}', which no column type maps to; the filter throws when it is built",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "Every hole of an interpolated Where is compared to a column, so its type must map to a dtype: a number, a bool, a string, a decimal, a date, a time, a timestamp, a Guid, bytes or a list.",
        HelpLink + "vx1003");

    public static readonly DiagnosticDescriptor RowsByRangeAndIndices = new DiagnosticDescriptor(
        "VX1004",
        "Rows by range and by indices on one scan",
        "This scan already selects rows {0}; a scan selects rows by range or by indices, not both, and throws here",
        "Usage",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        "Rows(RowRange) and Rows(params ReadOnlySpan<long>) exclude each other on one scan. Keep one, or express the range as indices.",
        HelpLink + "vx1004");

    public static readonly DiagnosticDescriptor MemberHasNoDType = new DiagnosticDescriptor(
        "VX1005",
        "A record member has no column type",
        "Member '{0}' of '{1}' is of type '{2}', which does not map to a column: {3}",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "Every member of a [VortexRecord] type becomes a column, so its type must map to a dtype. Change the type, or mark the member [VortexIgnore].",
        HelpLink + "vx1005");

    public static readonly DiagnosticDescriptor RecordNotPartial = new DiagnosticDescriptor(
        "VX1006",
        "A record type is not partial",
        "'{0}' must be declared partial, as must every type that contains it, for the generator to implement IVortexRecord",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "The generator adds the schema and the row copies to the type itself, which it can do only for a partial type nested in partial types.",
        HelpLink + "vx1006");

    public static readonly DiagnosticDescriptor RecordShapeUnsupported = new DiagnosticDescriptor(
        "VX1007",
        "A type cannot be a record",
        "'{0}' cannot be a [VortexRecord]: {1}",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "A record is a concrete, non-generic class or struct whose schema is known when the program is compiled.",
        HelpLink + "vx1007");

    public static readonly DiagnosticDescriptor MemberNotReadable = new DiagnosticDescriptor(
        "VX1008",
        "A record member cannot be filled from a column",
        "Member '{0}' of '{1}' cannot be filled when rows are read: {2}",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "Reading rows builds each record through a constructor whose parameters are members, then sets the other members. Give the member a setter or an init accessor, make it a constructor parameter, or mark it [VortexIgnore].",
        HelpLink + "vx1008");

    public static readonly DiagnosticDescriptor KeyComponentNotASymbol = new DiagnosticDescriptor(
        "VX1009",
        "A component of a group key is not a column",
        "Component {0} of the key, '{1}', is a {2}, not a symbol of the scan's columns; GroupBy throws when it is built",
        "Usage",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true,
        "A group key is a column of the scan, or a tuple of them: r => (r.City, r.Day). A literal or a captured value is the same for every row and groups nothing.",
        HelpLink + "vx1009");
}
