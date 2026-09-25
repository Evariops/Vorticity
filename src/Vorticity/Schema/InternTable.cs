using System.Collections.Concurrent;
using System.Threading;

namespace Vorticity;

/// <summary>
/// Hands back one instance of each value, for as long as the values it keeps weigh no more than its
/// budget; past it, a new value is handed back as it came and compares by value, so a process that
/// meets ever new values keeps a bounded table.
/// </summary>
/// <param name="budget">What the values kept may weigh at most.</param>
internal sealed class InternTable<T>(long budget)
    where T : class
{
    private readonly ConcurrentDictionary<T, T> _values = new();
    private long _weight;

    /// <summary>What the values kept weigh.</summary>
    internal long Weight => Interlocked.Read(ref _weight);

    /// <summary>The instance equal to <paramref name="value"/> the table keeps; else <paramref name="value"/>, kept when the budget allows.</summary>
    /// <param name="value">The value.</param>
    /// <param name="weight">What keeping it costs.</param>
    internal T Intern(T value, long weight)
    {
        if (_values.TryGetValue(value, out T? kept))
        {
            return kept;
        }

        if (Interlocked.Read(ref _weight) + weight > budget)
        {
            return value;
        }

        kept = _values.GetOrAdd(value, value);
        if (ReferenceEquals(kept, value))
        {
            Interlocked.Add(ref _weight, weight);
        }

        return kept;
    }
}
