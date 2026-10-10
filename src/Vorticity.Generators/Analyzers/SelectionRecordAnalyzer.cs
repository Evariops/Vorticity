using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Vorticity.Generators.Analyzers;

/// <summary>
/// The records a selection of several values is read through. VX1010: an <c>As&lt;TRecord&gt;()</c>
/// or an <c>AggregateAsync&lt;TResult&gt;</c> whose record does not take the elements of the selection,
/// position by position, which they refuse when they run. VX1011: several values read without a
/// record, which the compiler refuses with an error that does not name the fix.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class SelectionRecordAnalyzer : DiagnosticAnalyzer
{
    private static readonly SymbolDisplayFormat Format = SymbolDisplayFormat.MinimallyQualifiedFormat
        .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    /// <summary>What an enumerable result offers and a result of several values does not.</summary>
    private static readonly ImmutableHashSet<string> ValueMembers =
        ["GetAsyncEnumerator", "WithCancellation", "ToListAsync", "ToArrayAsync", "ToValuesAsync"];

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } =
        [Descriptors.RecordDoesNotTakeSelection, Descriptors.SeveralValuesWithoutRecord];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            KnownSymbols known = new KnownSymbols(start.Compilation);
            if (known.TypedScan is null || known.Sym is null || known.Aggregation is null)
            {
                return;
            }

            start.RegisterOperationAction(operation => AnalyzeRecord(operation, known), OperationKind.Invocation);
            start.RegisterSyntaxNodeAction(
                node => AnalyzeEnumeration(node, known), SyntaxKind.ForEachStatement, SyntaxKind.ForEachVariableStatement);
            start.RegisterSyntaxNodeAction(node => AnalyzeMember(node, known), SyntaxKind.SimpleMemberAccessExpression);
            start.RegisterSyntaxNodeAction(node => AnalyzeAnswers(node, known), SyntaxKind.InvocationExpression);
        });
    }

    /// <summary>VX1010: the record of an <c>As</c> or an <c>AggregateAsync</c> against the elements the compiler sees.</summary>
    private static void AnalyzeRecord(OperationAnalysisContext context, KnownSymbols known)
    {
        IInvocationOperation invocation = (IInvocationOperation)context.Operation;
        IMethodSymbol method = invocation.TargetMethod;
        if (method.TypeArguments.Length != 1 || method.TypeArguments[0] is not INamedTypeSymbol record)
        {
            return;
        }

        List<ITypeSymbol>? elements = method.Name switch
        {
            "As" when KnownSymbols.Is(method.ContainingType.OriginalDefinition, known.ValueAggregation)
                || KnownSymbols.Is(method.ContainingType.OriginalDefinition, known.ValueProjection) =>
                [((INamedTypeSymbol)method.ContainingType).TypeArguments[0]],
            "As" when IsSeveral(method.ContainingType, known) => SelectedBy(invocation.Instance, known),
            "AggregateAsync" when KnownSymbols.Is(method.ContainingType.OriginalDefinition, known.TypedScan) && invocation.Arguments.Length > 0
                && method.Parameters[0].Type is INamedTypeSymbol { TypeArguments.Length: 2 } lambda && lambda.TypeArguments[1].Name == "ITuple" =>
                Elements(invocation.Arguments[0].Value, known),
            _ => null,
        };

        if (elements is null)
        {
            return;
        }

        List<(string Name, ITypeSymbol Type)> members = RecordAnalysis.ColumnMembers(record, known, context.Compilation, context.CancellationToken);
        Location location = invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access }
            ? access.Name.GetLocation()
            : invocation.Syntax.GetLocation();
        string recordName = record.ToDisplayString(Format);
        if (members.Count != elements.Count)
        {
            context.ReportDiagnostic(Diagnostic.Create(
                Descriptors.RecordDoesNotTakeSelection,
                location,
                $"{recordName} has {members.Count} members and the selection {elements.Count} elements"));
            return;
        }

        for (int i = 0; i < members.Count; i++)
        {
            if (!Takes(members[i].Type, elements[i]))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.RecordDoesNotTakeSelection,
                    location,
                    $"Element {i + 1} of the selection is of type {elements[i].ToDisplayString(Format)}, which member '{members[i].Name}' of {recordName}, of type {members[i].Type.ToDisplayString(Format)}, does not take"));
            }
        }
    }

    /// <summary>VX1011: an <c>await foreach</c> over a result of several values.</summary>
    private static void AnalyzeEnumeration(SyntaxNodeAnalysisContext context, KnownSymbols known)
    {
        CommonForEachStatementSyntax loop = (CommonForEachStatementSyntax)context.Node;
        if (loop.AwaitKeyword.IsKind(SyntaxKind.None) || !IsSeveral(context.SemanticModel.GetTypeInfo(loop.Expression, context.CancellationToken).Type, known))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.SeveralValuesWithoutRecord,
            loop.Expression.GetLocation(),
            "A selection of several values is read through a record: declare a [VortexRecord] whose members take them in order, and enumerate .As<TRecord>()"));
    }

    /// <summary>VX1011: what reads values, asked of a result of several values.</summary>
    private static void AnalyzeMember(SyntaxNodeAnalysisContext context, KnownSymbols known)
    {
        MemberAccessExpressionSyntax access = (MemberAccessExpressionSyntax)context.Node;
        if (!ValueMembers.Contains(access.Name.Identifier.ValueText)
            || !IsSeveral(context.SemanticModel.GetTypeInfo(access.Expression, context.CancellationToken).Type, known))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.SeveralValuesWithoutRecord,
            access.Name.GetLocation(),
            $"A selection of several values has no {access.Name.Identifier.ValueText}: read it through a [VortexRecord] whose members take them in order, .As<TRecord>()"));
    }

    /// <summary>VX1011: <c>AggregateAsync(a =&gt; (…))</c> with no record named.</summary>
    private static void AnalyzeAnswers(SyntaxNodeAnalysisContext context, KnownSymbols known)
    {
        InvocationExpressionSyntax invocation = (InvocationExpressionSyntax)context.Node;
        if (invocation.Expression is not MemberAccessExpressionSyntax { Name: IdentifierNameSyntax { Identifier.ValueText: "AggregateAsync" } } access
            || invocation.ArgumentList.Arguments.Count == 0
            || invocation.ArgumentList.Arguments[0].Expression is not LambdaExpressionSyntax { ExpressionBody: TupleExpressionSyntax }
            || context.SemanticModel.GetTypeInfo(access.Expression, context.CancellationToken).Type is not INamedTypeSymbol scan
            || !KnownSymbols.Is(scan.OriginalDefinition, known.TypedScan))
        {
            return;
        }

        context.ReportDiagnostic(Diagnostic.Create(
            Descriptors.SeveralValuesWithoutRecord,
            access.Name.GetLocation(),
            "Several answers go into a record: declare a [VortexRecord] whose members take them in order, and name it, AggregateAsync<TResult>(…)"));
    }

    /// <summary>
    /// The types of the elements a <c>Select</c> of several values selects, when the receiver is that
    /// call, or that call followed by what keeps its elements: <c>Distinct</c>, <c>Skip</c>, <c>Take</c>.
    /// </summary>
    private static List<ITypeSymbol>? SelectedBy(IOperation? receiver, KnownSymbols known)
    {
        IOperation? current = Unwrapped(receiver);
        while (current is IInvocationOperation { TargetMethod.Name: "Distinct" or "Skip" or "Take", Instance: { } instance })
        {
            current = Unwrapped(instance);
        }

        return current is IInvocationOperation { TargetMethod.Name: "Select", Arguments.Length: > 0 } select
            ? Elements(select.Arguments[0].Value, known)
            : null;
    }

    /// <summary>The types the symbols of the tuple a lambda returns stand for: <c>T</c> of each <c>Sym&lt;T&gt;</c>.</summary>
    private static List<ITypeSymbol>? Elements(IOperation? argument, KnownSymbols known)
    {
        IOperation? current = argument;
        while (current is IDelegateCreationOperation creation)
        {
            current = creation.Target;
        }

        if (Unwrapped(current) is not IAnonymousFunctionOperation lambda)
        {
            return null;
        }

        IReturnOperation? returned = lambda.Body.Descendants().OfType<IReturnOperation>().FirstOrDefault();
        if (Unwrapped(returned?.ReturnedValue) is not ITupleOperation tuple)
        {
            return null;
        }

        List<ITypeSymbol> elements = [];
        foreach (IOperation element in tuple.Elements)
        {
            if (Unwrapped(element)?.Type is not INamedTypeSymbol { TypeArguments.Length: 1 } symbol || !KnownSymbols.Is(symbol.OriginalDefinition, known.Sym))
            {
                return null;
            }

            elements.Add(symbol.TypeArguments[0]);
        }

        return elements;
    }

    /// <summary>Whether a member of type <paramref name="member"/> takes an element of type <paramref name="element"/>: the same type, or its nullable form.</summary>
    private static bool Takes(ITypeSymbol member, ITypeSymbol element) =>
        SymbolEqualityComparer.Default.Equals(member, element)
        || (member is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T } nullable
            && SymbolEqualityComparer.Default.Equals(nullable.TypeArguments[0], element));

    private static bool IsSeveral(ITypeSymbol? type, KnownSymbols known) =>
        type is not null && (KnownSymbols.Is(type, known.Aggregation) || KnownSymbols.Is(type, known.Projection));

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
