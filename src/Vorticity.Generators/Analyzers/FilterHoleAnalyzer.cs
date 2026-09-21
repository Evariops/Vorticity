using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Vorticity.Generators.Analyzers;

/// <summary>
/// VX1003: a hole of an interpolated <c>Where</c> on the tool scan whose type maps to no dtype. The
/// column a hole is compared to is known only once the file is open, so the check that can be made
/// here is that some column could take the hole at all.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FilterHoleAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Descriptors.HoleHasNoDType];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            KnownSymbols known = new KnownSymbols(start.Compilation);
            if (known.ToolScan is null || known.FilterHandler is null)
            {
                return;
            }

            start.RegisterOperationAction(operation => Analyze(operation, known), OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, KnownSymbols known)
    {
        IInvocationOperation invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol method = invocation.TargetMethod;
        if (method.Name != "Where" || !KnownSymbols.Is(method.ContainingType, known.ToolScan)
            || method.Parameters.Length != 1 || !KnownSymbols.Is(method.Parameters[0].Type, known.FilterHandler)
            || invocation.Arguments.FirstOrDefault()?.Syntax is not ArgumentSyntax argument || invocation.SemanticModel is not SemanticModel model)
        {
            return;
        }

        foreach (InterpolationSyntax hole in argument.Expression.DescendantNodesAndSelf().OfType<InterpolationSyntax>())
        {
            ITypeSymbol? type = model.GetTypeInfo(hole.Expression, context.CancellationToken).Type;
            if (type is null || type.TypeKind == TypeKind.Error)
            {
                continue;
            }

            MappedType mapped = MappedType.Map(type, known);
            if (mapped.Error is not null || mapped.Kind == ValueKind.Record)
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.HoleHasNoDType, hole.GetLocation(), hole.Expression.ToString(), type.ToDisplayString()));
            }
        }
    }
}
