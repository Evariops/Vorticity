using System.Runtime.CompilerServices;
using Vorticity.Aggregating;

namespace Vorticity.Tests;

/// <summary>
/// Every array a group by gives back to a shelf is poisoned throughout these tests: the shelves lend
/// them again, to another table of the query or of the next one, so a table that still read one after
/// giving it would read another's groups in production; here it reads garbage, and its test fails.
/// </summary>
internal static class PoisonedShelves
{
    [ModuleInitializer]
    internal static void Poison() => ArrayShelf.PoisonsGiven = true;
}
