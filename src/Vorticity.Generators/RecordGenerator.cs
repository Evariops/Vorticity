using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Vorticity.Generators;

/// <summary>
/// Implements <c>IVortexRecord&lt;T&gt;</c> for every partial type marked <c>[VortexRecord]</c>, adds its
/// <c>ColumnNames</c>, and the extension members that name its columns on a probe, a batch and a builder.
/// </summary>
[Generator(LanguageNames.CSharp)]
public sealed class RecordGenerator : IIncrementalGenerator
{
    private static readonly DiagnosticDescriptor[] Reported =
    [
        Descriptors.MemberHasNoDType,
        Descriptors.RecordNotPartial,
        Descriptors.RecordShapeUnsupported,
        Descriptors.MemberNotReadable,
    ];

    /// <inheritdoc/>
    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        IncrementalValuesProvider<RecordResult> records = context.SyntaxProvider.ForAttributeWithMetadataName(
            "Vorticity.VortexRecordAttribute",
            static (node, _) => node is TypeDeclarationSyntax,
            static (attributed, cancellationToken) => RecordAnalysis.Analyze(attributed, cancellationToken));

        context.RegisterSourceOutput(records, static (production, result) =>
        {
            foreach (DiagnosticModel diagnostic in result.Diagnostics)
            {
                DiagnosticDescriptor descriptor = Reported.First(d => d.Id == diagnostic.Id);
                production.ReportDiagnostic(Diagnostic.Create(
                    descriptor, diagnostic.Location?.ToLocation() ?? Location.None, diagnostic.Arguments.Cast<object>().ToArray()));
            }

            if (result.Model is RecordModel model)
            {
                production.AddSource(model.HintName, SourceText.From(RecordEmitter.Emit(model), System.Text.Encoding.UTF8));
            }
        });
    }
}
