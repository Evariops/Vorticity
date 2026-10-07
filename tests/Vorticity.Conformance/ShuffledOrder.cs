using System.Runtime.CompilerServices;
using Vorticity.Aggregating;

namespace Vorticity.Conformance;

/// <summary>
/// The groups of a query come in a shuffled order throughout these tests: none relies on the order the
/// engine happens to give groups no <c>OrderBy</c> sorts.
/// </summary>
internal static class ShuffledOrder
{
    [ModuleInitializer]
    internal static void Shuffle() => AggregationPlan.ShuffledOrder = true;
}
