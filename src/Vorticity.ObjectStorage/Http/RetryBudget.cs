using System.Threading;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// A store's budget of retries, a token bucket: a retry spends tokens, a success gives one back, and
/// an empty bucket fails a request at its first failure. A store that is down then costs its callers
/// one attempt each, rather than a storm of retries that keeps it down.
/// </summary>
/// <remarks>The figures are those of the AWS SDKs' standard retry mode.</remarks>
internal sealed class RetryBudget
{
    internal const int Capacity = 500;
    internal const int RetryCost = 5;
    internal const int TimeoutCost = 10;

    private int _tokens = Capacity;

    /// <summary>The tokens left.</summary>
    internal int Tokens => Volatile.Read(ref _tokens);

    /// <summary>Takes <paramref name="cost"/> tokens for a retry, or refuses when fewer are left.</summary>
    internal bool TrySpend(int cost)
    {
        int current = Volatile.Read(ref _tokens);
        while (current >= cost)
        {
            int seen = Interlocked.CompareExchange(ref _tokens, current - cost, current);
            if (seen == current)
            {
                return true;
            }

            current = seen;
        }

        return false;
    }

    /// <summary>Gives a token back for a success, up to the capacity.</summary>
    internal void Succeeded()
    {
        int current = Volatile.Read(ref _tokens);
        while (current < Capacity)
        {
            int seen = Interlocked.CompareExchange(ref _tokens, current + 1, current);
            if (seen == current)
            {
                return;
            }

            current = seen;
        }
    }
}
