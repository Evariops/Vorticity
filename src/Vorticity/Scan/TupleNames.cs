using System.Globalization;

namespace Vorticity;

/// <summary>
/// The names a tuple gives its elements, <c>Item1</c> and on, which name the columns of a selection that returns a tuple.
/// </summary>
/// <remarks>
/// The names of the first elements are made once for the process, so that a query does not format them: a name formatted
/// for each column cost its string, and a box more while the JIT had not optimized the formatting yet, which made what a
/// query allocates depend on what the process had run before it.
/// </remarks>
internal static class TupleNames
{
    private static readonly string[] Items = Made(32);

    /// <summary>The name of the element at <paramref name="index"/>, from 0: <c>Item1</c> for the first.</summary>
    internal static string Item(int index) =>
        (uint)index < (uint)Items.Length ? Items[index] : Name(index);

    private static string[] Made(int count)
    {
        string[] names = new string[count];
        for (int i = 0; i < names.Length; i++)
        {
            names[i] = Name(i);
        }

        return names;
    }

    private static string Name(int index) => "Item" + (index + 1).ToString(CultureInfo.InvariantCulture);
}
