using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Vorticity.Generators.Analyzers;

/// <summary>
/// VX1001: a span, a column or a view borrowed from the batch an enumeration delivered, assigned to
/// a variable declared outside the enumeration's body. The compiler keeps a <c>Columns&lt;T&gt;</c>
/// inside the body; it does not see a span of it copied out.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class BorrowedSpanAnalyzer : DiagnosticAnalyzer
{
    private const int MaxDepth = 4;

    /// <inheritdoc/>
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = [Descriptors.BorrowedSpanEscapes];

    /// <inheritdoc/>
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterCompilationStartAction(start =>
        {
            KnownSymbols known = new KnownSymbols(start.Compilation);
            if (known.Column is null || known.Columns is null)
            {
                return;
            }

            start.RegisterOperationAction(operation => Analyze(operation, known), OperationKind.SimpleAssignment);
        });
    }

    private static void Analyze(OperationAnalysisContext context, KnownSymbols known)
    {
        ISimpleAssignmentOperation assignment = (ISimpleAssignmentOperation)context.Operation;
        if (assignment.IsRef || !IsBorrowed(assignment.Target.Type, known))
        {
            return;
        }

        ISymbol? target = assignment.Target switch
        {
            ILocalReferenceOperation local => local.Local,
            IParameterReferenceOperation { Parameter.RefKind: RefKind.Out or RefKind.Ref } parameter => parameter.Parameter,
            _ => null,
        };
        if (target is null || assignment.SemanticModel is not SemanticModel model)
        {
            return;
        }

        foreach (ILocalReferenceOperation reference in assignment.Value.DescendantsAndSelf().OfType<ILocalReferenceOperation>())
        {
            if (!IsBorrowed(reference.Local.Type, known)
                || Enumeration(reference.Local, model, known, 0, context.CancellationToken) is not CommonForEachStatementSyntax loop)
            {
                continue;
            }

            if (loop.Statement.Span.Contains(assignment.Syntax.Span) && !DeclaredIn(target, loop, context))
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    Descriptors.BorrowedSpanEscapes, assignment.Syntax.GetLocation(), assignment.Value.Syntax.ToString(), target.Name));
                return;
            }
        }
    }

    /// <summary>Whether values of <paramref name="type"/> point into a delivered batch: a span, a column, the columns, a view, a selection.</summary>
    private static bool IsBorrowed(ITypeSymbol? type, KnownSymbols known) =>
        KnownSymbols.Is(type, known.Span) || KnownSymbols.Is(type, known.ReadOnlySpan) || IsBatchPart(type, known);

    private static bool IsBatchPart(ITypeSymbol? type, KnownSymbols known) =>
        KnownSymbols.Is(type, known.Column) || KnownSymbols.Is(type, known.Columns) || KnownSymbols.Is(type, known.BatchView)
        || KnownSymbols.Is(type, known.Selection) || KnownSymbols.Is(type, known.DictionaryView) || KnownSymbols.Is(type, known.RunEndView);

    /// <summary>
    /// The enumeration whose delivered batch <paramref name="local"/> borrows from: the <c>foreach</c>
    /// that declares it over batches, or, for a local declared in a body, the one its initializer
    /// borrows from.
    /// </summary>
    private static CommonForEachStatementSyntax? Enumeration(ILocalSymbol local, SemanticModel model, KnownSymbols known, int depth, System.Threading.CancellationToken cancellationToken)
    {
        if (depth > MaxDepth || local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is not SyntaxNode declaration
            || declaration.SyntaxTree != model.SyntaxTree)
        {
            return null;
        }

        CommonForEachStatementSyntax? loop = declaration.AncestorsAndSelf().OfType<CommonForEachStatementSyntax>().FirstOrDefault();
        bool declaredByLoop = loop is ForEachStatementSyntax && loop == declaration
            || loop is ForEachVariableStatementSyntax variable && variable.Variable.Span.Contains(declaration.Span);
        if (loop is not null && declaredByLoop)
        {
            return IsBatchPart(model.GetForEachStatementInfo(loop).ElementType, known) ? loop : null;
        }

        if (declaration is not VariableDeclaratorSyntax { Initializer.Value: { } initializer }
            || model.GetOperation(initializer, cancellationToken) is not IOperation value)
        {
            return null;
        }

        foreach (ILocalReferenceOperation reference in value.DescendantsAndSelf().OfType<ILocalReferenceOperation>())
        {
            if (IsBorrowed(reference.Local.Type, known)
                && Enumeration(reference.Local, model, known, depth + 1, cancellationToken) is CommonForEachStatementSyntax found)
            {
                return found;
            }
        }

        return null;
    }

    private static bool DeclaredIn(ISymbol target, CommonForEachStatementSyntax loop, OperationAnalysisContext context) =>
        target is ILocalSymbol && target.DeclaringSyntaxReferences.Any(reference =>
            reference.SyntaxTree == loop.SyntaxTree && loop.Span.Contains(reference.GetSyntax(context.CancellationToken).Span));
}
