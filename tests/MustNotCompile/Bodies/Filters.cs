using Vorticity;

namespace MustNotCompile.Cases;

// A filter is recorded, not evaluated: what the scan cannot push down has no operator to call.
internal static class Filters
{
    internal static Scan<Reading> AddConstant(VortexFile file) =>
        file.Scan<Reading>().Where(r => r.Day + 1 > 3); // expect: CS0619 "Vortex does not push arithmetic down."

    internal static Scan<Reading> MultiplyNullable(VortexFile file) =>
        file.Scan<Reading>().Where(r => r.Celsius * 2.0 > 30.0); // expect: CS0619 "Vortex does not push arithmetic down."

    internal static Scan<Reading> SubtractColumns(VortexFile file) =>
        file.Scan<Reading>().Where(r => r.Day - r.Day == 0); // expect: CS0619 "Vortex does not push arithmetic down."

    internal static Scan<Reading> Modulo(VortexFile file) =>
        file.Scan<Reading>().Where(r => r.Day % 7 == 0); // expect: CS0619 "Vortex does not push arithmetic down."

    internal static Scan<Reading> TextAgainstNumber(VortexFile file) =>
        file.Scan<Reading>().Where(r => r.Day == "3"); // expect: CS0019

    internal static Scan<Reading> WiderLiteral(VortexFile file) =>
        file.Scan<Reading>().Where(r => r.Day > 3L); // expect: CS0019

    internal static Scan<Reading> ColumnsOfTwoTypes(VortexFile file) =>
        file.Scan<Reading>().Where(r => r.Day == r.City); // expect: CS0019
}
