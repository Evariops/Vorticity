using System;
using System.Threading.Tasks;
using Vorticity;

namespace MustNotCompile.Cases;

// The surface is asynchronous, owned batches are disposed, and an answer needs a type it can hold.
internal static class Sinks
{
    internal static void Synchronously(VortexFile file)
    {
        foreach (Columns<Reading> batch in file.Scan<Reading>()) // expect: CS8414
        {
            _ = batch.RowCount;
        }
    }

    internal static async Task KeptWithoutDisposing(VortexFile file)
    {
        await foreach (Columns<Reading> batch in file.Scan<Reading>())
        {
            RecordBatch owned = batch.ToOwned(); // expect: VX1002
            _ = owned.RowCount;
        }
    }

    internal static async Task<string> SumOfText(VortexFile file) =>
        await file.Scan<Reading>().SumAsync(r => r.City); // expect: CS0311

    internal static Scan<Reading> RangeAndIndices(VortexFile file) =>
        file.Scan<Reading>().Rows(RowRange.FromLength(0, 10)).Rows(1, 2); // expect: VX1004

    internal static Scan HoleWithoutAColumnType(VortexFile file) =>
        file.Scan("Day").Where($"Day >= {TimeSpan.Zero}"); // expect: VX1003
}
