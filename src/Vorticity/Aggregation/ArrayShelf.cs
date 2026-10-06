using System;
using System.Collections.Generic;
using System.Threading;

namespace Vorticity.Aggregating;

/// <summary>
/// The arrays the sub-tables of one query leave as they grow and split, taken again by the next that
/// needs one of the same type and length (PLAN-HIGH-CARDINALITY, H4): the core's pool. A doubling and a
/// split make the same few lengths over and over, so that past its first sub-tables a query allocates
/// little more than the memory it holds. The shelf dies with its query.
/// </summary>
/// <remarks>
/// One lock for every type and length: a sub-table takes and gives arrays when it grows or splits, a
/// few times for thousands of entries, and the lanes that hold parts rarely meet on it. The arrays are
/// ordinary ones: pinned, each small one went through the lock the runtime takes for the pinned heap,
/// which fourteen lanes splitting at once waited on more than they worked.
/// </remarks>
internal sealed class ArrayShelf
{
    private readonly Lock _gate = new Lock();
    private readonly Dictionary<(Type Type, int Length), Stack<Array>> _arrays = [];

    /// <summary>An array of <paramref name="length"/> elements, from the shelf or new; zeroed when <paramref name="zeroed"/>.</summary>
    internal T[] Take<T>(int length, bool zeroed)
    {
        Array? found = null;
        lock (_gate)
        {
            if (_arrays.TryGetValue((typeof(T), length), out Stack<Array>? stack) && stack.Count > 0)
            {
                found = stack.Pop();
            }
        }

        if (found is T[] array)
        {
            if (zeroed)
            {
                Array.Clear(array);
            }

            return array;
        }

        return zeroed ? new T[length] : GC.AllocateUninitializedArray<T>(length);
    }

    /// <summary>Puts an array no table holds any more back on the shelf.</summary>
    internal void Give<T>(T[] array)
    {
        if (array.Length == 0)
        {
            return;
        }

        lock (_gate)
        {
            if (!_arrays.TryGetValue((typeof(T), array.Length), out Stack<Array>? stack))
            {
                stack = new Stack<Array>();
                _arrays.Add((typeof(T), array.Length), stack);
            }

            stack.Push(array);
        }
    }
}
