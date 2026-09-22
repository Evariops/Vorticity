using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Vorticity;

namespace MustNotCompile.Cases;

// A batch's columns are borrowed for one iteration of the loop: the compiler keeps them there.
internal static class Borrowing
{
    internal static async Task AcrossAnAwait(VortexFile file)
    {
        await foreach (Columns<Reading> batch in file.Scan<Reading>())
        {
            Column<double?> celsius = batch.Celsius;
            await Task.Yield();
            _ = celsius.Length; // expect: CS4007
        }
    }

    internal static async Task IntoAList(VortexFile file)
    {
        List<Columns<Reading>> kept = []; // expect: CS9244
        await foreach (Columns<Reading> batch in file.Scan<Reading>())
        {
            kept.Add(batch);
        }
    }

    internal static async Task IntoALambda(VortexFile file)
    {
        await foreach (Columns<Reading> batch in file.Scan<Reading>())
        {
            Func<int> count = () => batch.RowCount; // expect: CS8175
            _ = count;
        }
    }

    internal static async Task OutOfTheBody(VortexFile file)
    {
        ReadOnlySpan<int> last = default;
        await foreach (Columns<Reading> batch in file.Scan<Reading>())
        {
            last = batch.Day.Values; // expect: VX1001
        }
    }
}
