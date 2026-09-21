using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Vorticity.Generators.Analyzers;

/// <summary>
/// VX1002: a <c>RecordBatch</c> from <c>ToBatchesAsync</c> or <c>ToOwned</c> that nothing disposes:
/// no <c>using</c>, no <c>Dispose</c>, and it is not returned, passed on, or stored where a new owner
/// could dispose it.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class UndisposedBatchAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Descriptors.BatchNotDisposed];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            KnownSymbols known = new KnownSymbols(start.Compilation);
            if (known.RecordBatch is null)
            {
                return;
            }

            start.RegisterOperationAction(operation => AnalyzeToOwned(operation, known), OperationKind.Invocation);
            start.RegisterOperationAction(operation => AnalyzeLoop(operation, known), OperationKind.Loop);
        });
    }

    private static void AnalyzeToOwned(OperationAnalysisContext context, KnownSymbols known)
    {
        IInvocationOperation invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol method = invocation.TargetMethod;
        if (method.Name != "ToOwned" || !KnownSymbols.Is(method.ReturnType, known.RecordBatch)
            || !(KnownSymbols.Is(method.ContainingType, known.Columns) || KnownSymbols.Is(method.ContainingType, known.BatchView)))
        {
            return;
        }

        if (!Handled(invocation, context))
        {
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.BatchNotDisposed, invocation.Syntax.GetLocation(), "ToOwned"));
        }
    }

    private static void AnalyzeLoop(OperationAnalysisContext context, KnownSymbols known)
    {
        if (context.Operation is not IForEachLoopOperation { LoopControlVariable: IVariableDeclaratorOperation variable } loop
            || !KnownSymbols.Is(variable.Symbol.Type, known.RecordBatch) || !FromToBatches(loop.Collection, known))
        {
            return;
        }

        if (!LocalHandled(variable.Symbol, loop.Body, context))
        {
            Location location = loop.Syntax is ForEachStatementSyntax statement ? statement.Identifier.GetLocation() : variable.Syntax.GetLocation();
            context.ReportDiagnostic(Diagnostic.Create(Descriptors.BatchNotDisposed, location, "ToBatchesAsync"));
        }
    }

    /// <summary>Whether the collection is <c>ToBatchesAsync</c> of a scan, possibly through <c>WithCancellation</c> or <c>ConfigureAwait</c>.</summary>
    private static bool FromToBatches(IOperation collection, KnownSymbols known)
    {
        IOperation? current = OperationPaths.Inner(collection);
        while (current is IInvocationOperation invocation)
        {
            IMethodSymbol method = invocation.TargetMethod;
            if (method.Name == "ToBatchesAsync")
            {
                return KnownSymbols.Is(method.ContainingType, known.TypedScan) || KnownSymbols.Is(method.ContainingType, known.ToolScan);
            }

            if (method.Name is not ("WithCancellation" or "ConfigureAwait"))
            {
                return false;
            }

            current = OperationPaths.Inner(invocation.Instance ?? invocation.Arguments.FirstOrDefault()?.Value);
        }

        return false;
    }

    /// <summary>Whether the batch <paramref name="produced"/> yields is disposed or handed to another owner.</summary>
    private static bool Handled(IOperation produced, OperationAnalysisContext context)
    {
        IOperation value = OperationPaths.Outer(produced);
        switch (value.Parent)
        {
            case IVariableInitializerOperation { Parent: IVariableDeclaratorOperation declarator }:
                return IsUsing(declarator) || LocalHandled(declarator.Symbol, OperationPaths.Body(declarator), context);
            case ISimpleAssignmentOperation assignment when assignment.Value == value:
                return assignment.Target switch
                {
                    IDiscardOperation => false,
                    ILocalReferenceOperation local => LocalHandled(local.Local, OperationPaths.Body(assignment), context),
                    _ => true,
                };
            case IExpressionStatementOperation:
                return false;
            case IInvocationOperation call when call.Instance == value:
                return call.TargetMethod.Name == "Dispose";
            case IPropertyReferenceOperation property when property.Instance == value:
                return false;
            default:
                return true;
        }
    }

    private static bool IsUsing(IVariableDeclaratorOperation declarator) =>
        declarator.Parent is IVariableDeclarationOperation { Parent: IVariableDeclarationGroupOperation { Parent: IUsingDeclarationOperation or IUsingOperation } }
        || declarator.Parent is IVariableDeclarationOperation { Parent: IUsingOperation };

    /// <summary>Whether some use of <paramref name="local"/> within <paramref name="scope"/> disposes it or hands it on.</summary>
    private static bool LocalHandled(ILocalSymbol local, IOperation scope, OperationAnalysisContext context)
    {
        foreach (ILocalReferenceOperation reference in scope.DescendantsAndSelf().OfType<ILocalReferenceOperation>())
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            if (!SymbolEqualityComparer.Default.Equals(reference.Local, local))
            {
                continue;
            }

            if (OperationPaths.IsCaptured(reference, scope))
            {
                return true;
            }

            IOperation value = OperationPaths.Outer(reference);
            switch (value.Parent)
            {
                case IInvocationOperation call when call.Instance == value && call.TargetMethod.Name is "Dispose" or "DisposeAsync":
                case IConditionalAccessOperation { WhenNotNull: IInvocationOperation { TargetMethod.Name: "Dispose" or "DisposeAsync" } } access when access.Operation == value:
                case IUsingOperation:
                case IArgumentOperation:
                case IReturnOperation:
                case IVariableInitializerOperation:
                case ISimpleAssignmentOperation assignment when assignment.Value == value && assignment.Target is not IDiscardOperation:
                case ICollectionExpressionOperation:
                case IArrayInitializerOperation:
                case ITupleOperation:
                case ISpreadOperation:
                    return true;
            }
        }

        return false;
    }
}
