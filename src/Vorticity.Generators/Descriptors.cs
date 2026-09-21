using Microsoft.CodeAnalysis;

namespace Vorticity.Generators;

/// <summary>Every diagnostic the package reports.</summary>
internal static class Descriptors
{
    private const string HelpLink = "docs/guide/diagnostics.md#";

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
}
