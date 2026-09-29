namespace Vorticity.Keys;

/// <summary>
/// Where a key's entries end among sorted keys, found from one of them: the next entry first, then
/// entries twice as far each time, then a bisection of the last stride, so a key held by k entries
/// costs O(log k) comparisons whatever lies around it.
/// </summary>
internal static class KeyGallop
{
    /// <summary>Whether an entry holds the key followed.</summary>
    internal interface IKeyed
    {
        bool Holds(int index);
    }

    /// <summary>
    /// The index just past the key's entries from <paramref name="at"/>, going forward or back as
    /// far as <paramref name="edge"/>; -1 when the key reaches the edge.
    /// </summary>
    /// <param name="keys">The sorted keys, which say whether an index holds the key.</param>
    /// <param name="at">An index holding the key.</param>
    /// <param name="edge">The farthest index that may be looked at, in the direction gone.</param>
    /// <param name="forward">Whether to go towards the higher indexes.</param>
    internal static int Past<TKeys>(TKeys keys, int at, int edge, bool forward)
        where TKeys : IKeyed, allows ref struct
    {
        int direction = forward ? 1 : -1;
        int reach = forward ? edge - at : at - edge;
        int held = 0;
        long step = 1;
        while (step <= reach && keys.Holds(at + (direction * (int)step)))
        {
            held = (int)step;
            step <<= 1;
        }

        int past;
        if (step > reach)
        {
            if (keys.Holds(edge))
            {
                return -1;
            }

            past = reach;
        }
        else
        {
            past = (int)step;
        }

        while (past - held > 1)
        {
            int mid = held + ((past - held) >> 1);
            if (keys.Holds(at + (direction * mid)))
            {
                held = mid;
            }
            else
            {
                past = mid;
            }
        }

        return at + (direction * past);
    }
}
