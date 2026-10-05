using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Vorticity.Generators.Analyzers;

/// <summary>
/// VX1009: a component of a tuple a typed scan's <c>GroupBy</c> groups by that is not a symbol of the
/// scan's columns, a literal or a captured value, which <c>GroupBy</c> refuses when it runs. Query
/// syntax, <c>group r by (r.City, 42)</c>, is the same call.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GroupKeyAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Descriptors.KeyComponentNotASymbol];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            KnownSymbols known = new KnownSymbols(start.Compilation);
            if (known.TypedScan is null || known.Sym is null)
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
        if (method.Name != "GroupBy" || method.TypeArguments.Length != 1 || !KnownSymbols.Is(method.ContainingType, known.TypedScan)
            || invocation.Arguments.Length != 1 || Lambda(invocation.Arguments[0].Value) is not IAnonymousFunctionOperation lambda)
        {
            return;
        }

        foreach (IReturnOperation returned in lambda.Body.Descendants().OfType<IReturnOperation>())
        {
            if (Unwrapped(returned.ReturnedValue) is not ITupleOperation tuple)
            {
                continue;
            }

            for (int i = 0; i < tuple.Elements.Length; i++)
            {
                IOperation element = Unwrapped(tuple.Elements[i])!;
                if (element.Type is ITypeSymbol type && !KnownSymbols.Is(type.OriginalDefinition, known.Sym))
                {
                    context.ReportDiagnostic(Diagnostic.Create(
                        Descriptors.KeyComponentNotASymbol,
                        element.Syntax.GetLocation(),
                        i + 1,
                        element.Syntax.ToString(),
                        type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
                }
            }
        }
    }

    private static IOperation? Lambda(IOperation? value)
    {
        IOperation? current = value;
        while (current is IDelegateCreationOperation creation)
        {
            current = creation.Target;
        }

        return Unwrapped(current);
    }

    private static IOperation? Unwrapped(IOperation? value)
    {
        IOperation? current = value;
        while (current is IConversionOperation { IsImplicit: true } conversion)
        {
            current = conversion.Operand;
        }

        return current;
    }
}
