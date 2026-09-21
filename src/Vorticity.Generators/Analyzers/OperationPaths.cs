using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Vorticity.Generators.Analyzers;

/// <summary>Walks through the conversions the compiler inserts, which say nothing about where a value goes.</summary>
internal static class OperationPaths
{
    /// <summary>The operation beneath its conversions.</summary>
    public static IOperation? Inner(IOperation? operation)
    {
        while (operation is IConversionOperation conversion)
        {
            operation = conversion.Operand;
        }

        return operation;
    }

    /// <summary>The outermost conversion of <paramref name="operation"/>, whose parent is what consumes the value.</summary>
    public static IOperation Outer(IOperation operation)
    {
        while (operation.Parent is IConversionOperation conversion)
        {
            operation = conversion;
        }

        return operation;
    }

    /// <summary>The body the operation belongs to: the method's, the lambda's or the initializer's.</summary>
    public static IOperation Body(IOperation operation)
    {
        while (operation.Parent is not null)
        {
            operation = operation.Parent;
        }

        return operation;
    }

    /// <summary>Whether <paramref name="operation"/> lies inside a lambda or a local function nested in <paramref name="body"/>.</summary>
    public static bool IsCaptured(IOperation operation, IOperation body)
    {
        for (IOperation? current = operation.Parent; current is not null && current != body; current = current.Parent)
        {
            if (current is IAnonymousFunctionOperation or ILocalFunctionOperation)
            {
                return true;
            }
        }

        return false;
    }
}
