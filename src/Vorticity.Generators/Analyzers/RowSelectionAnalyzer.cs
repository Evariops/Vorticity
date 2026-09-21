using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Vorticity.Generators.Analyzers;

/// <summary>
/// VX1004: <c>Rows(RowRange)</c> and <c>Rows(params ReadOnlySpan&lt;long&gt;)</c> on one scan, which
/// throws at the second call: in one fluent chain, or on a local that holds the scan and is never
/// reassigned.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class RowSelectionAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Descriptors.RowsByRangeAndIndices];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            KnownSymbols known = new KnownSymbols(start.Compilation);
            if (known.RowRange is null || (known.TypedScan is null && known.ToolScan is null))
            {
                return;
            }

            start.RegisterOperationAction(operation => Analyze(operation, known), OperationKind.Invocation);
        });
    }

    private static void Analyze(OperationAnalysisContext context, KnownSymbols known)
    {
        IInvocationOperation invocation = (IInvocationOperation)context.Operation;
        if (Selection(invocation, known) is not bool byRange)
        {
            return;
        }

        IOperation? receiver = OperationPaths.Inner(invocation.Instance);
        if (Conflicts(Chain(receiver, known), byRange, known))
        {
            Report(context, invocation, byRange);
            return;
        }

        IOperation? root = Root(receiver, known);
        if (root is not ILocalReferenceOperation { Local: ILocalSymbol local } || Reassigned(local, invocation))
        {
            return;
        }

        foreach (IInvocationOperation earlier in OperationPaths.Body(invocation).Descendants().OfType<IInvocationOperation>())
        {
            if (earlier.Syntax.Span.End <= invocation.Syntax.SpanStart && Selection(earlier, known) == !byRange
                && Root(OperationPaths.Inner(earlier.Instance), known) is ILocalReferenceOperation other
                && SymbolEqualityComparer.Default.Equals(other.Local, local))
            {
                Report(context, invocation, byRange);
                return;
            }
        }

        if (Initializer(local, invocation) is IOperation initializer && Conflicts(Chain(initializer, known), byRange, known))
        {
            Report(context, invocation, byRange);
        }
    }

    /// <summary>True for <c>Rows(RowRange)</c>, false for <c>Rows</c> by indices, null for any other call.</summary>
    private static bool? Selection(IInvocationOperation invocation, KnownSymbols known)
    {
        IMethodSymbol method = invocation.TargetMethod;
        if (method.Name != "Rows" || method.Parameters.Length != 1 || !IsScan(method.ContainingType, known))
        {
            return null;
        }

        return KnownSymbols.Is(method.Parameters[0].Type, known.RowRange);
    }

    private static bool IsScan(ITypeSymbol? type, KnownSymbols known) =>
        KnownSymbols.Is(type, known.TypedScan) || KnownSymbols.Is(type, known.ToolScan);

    /// <summary>The calls of a fluent chain that returns the scan, from <paramref name="receiver"/> back to where the scan comes from.</summary>
    private static IEnumerable<IInvocationOperation> Chain(IOperation? receiver, KnownSymbols known)
    {
        while (receiver is IInvocationOperation call && IsScan(call.TargetMethod.ReturnType, known) && IsScan(call.TargetMethod.ContainingType, known))
        {
            yield return call;
            receiver = OperationPaths.Inner(call.Instance);
        }
    }

    private static bool Conflicts(IEnumerable<IInvocationOperation> chain, bool byRange, KnownSymbols known) =>
        chain.Any(call => Selection(call, known) == !byRange);

    /// <summary>What the chain starts from: the call that made the scan, or the local that holds it.</summary>
    private static IOperation? Root(IOperation? receiver, KnownSymbols known)
    {
        IOperation? current = receiver;
        while (current is IInvocationOperation call && IsScan(call.TargetMethod.ReturnType, known) && IsScan(call.TargetMethod.ContainingType, known))
        {
            current = OperationPaths.Inner(call.Instance);
        }

        return current;
    }

    private static bool Reassigned(ILocalSymbol local, IOperation within) =>
        OperationPaths.Body(within).Descendants().OfType<ISimpleAssignmentOperation>().Any(assignment =>
            assignment.Target is ILocalReferenceOperation target && SymbolEqualityComparer.Default.Equals(target.Local, local));

    private static IOperation? Initializer(ILocalSymbol local, IOperation within) =>
        OperationPaths.Body(within).Descendants().OfType<IVariableDeclaratorOperation>()
            .FirstOrDefault(declarator => SymbolEqualityComparer.Default.Equals(declarator.Symbol, local))?.Initializer?.Value is IOperation value
            ? OperationPaths.Inner(value)
            : null;

    private static void Report(OperationAnalysisContext context, IInvocationOperation invocation, bool byRange)
    {
        Location location = invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
            ? access.Name.GetLocation()
            : invocation.Syntax.GetLocation();
        context.ReportDiagnostic(Diagnostic.Create(Descriptors.RowsByRangeAndIndices, location, byRange ? "by indices" : "by range"));
    }
}
