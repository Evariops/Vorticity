using System;
using System.Globalization;
using System.Threading;

namespace Vorticity;

/// <summary>
/// The memory the process is held to, as the host sets it: <see cref="SetLimit"/>, or the <c>VORTICITY_MEMORY_LIMIT</c>
/// environment variable read when the library first needs a limit. A limit set is taken as a container's limit of that
/// size would be, and in a container the least of the two holds.
/// </summary>
/// <remarks>
/// <para>
/// The collector's heap is held to three quarters of the limit, what the runtime gives itself in a container: past it, the
/// collector runs full collections before it lets an allocation fail. The library's own work, the blocks of the questions
/// and the jobs, is held below that by its budgets (<see cref="QueryMemoryBudget"/>), which read what the process uses
/// every second, its resident memory outside a container, as they read a container's. The quarter left is for what the
/// collector does not hold: the runtime, the stacks of the threads, the native allocator.
/// </para>
/// <para>
/// Without a limit set, outside a container, the process is held to the collector's limit: the machine's memory, or the
/// heap's hard limit the host configured.
/// </para>
/// </remarks>
public static class VortexMemory
{
    /// <summary>The environment variable read for a limit when none was set: bytes, or a count of <c>k</c>, <c>m</c>, <c>g</c> or <c>t</c>, such as <c>4g</c>.</summary>
    public const string LimitVariable = "VORTICITY_MEMORY_LIMIT";

    /// <summary>The share of the limit, one in this many, the collector's heap leaves to the rest of the process: a quarter, as the runtime leaves in a container.</summary>
    private const int NativeShare = 4;

    private static readonly Lock Gate = new Lock();

    // What the collector may commit when the process started: a hard limit configured, its share of a container's, or the
    // machine's memory; what it may commit now, once a limit set held it to less.
    private static readonly long s_collectorAtStart = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
    private static long s_collector = s_collectorAtStart;

    // The limit set; 0 for none.
    private static long s_limit;

    // Whether a limit set holds the collector's heap.
    private static bool s_heapHeld;

    // 1 once the variable was read and its limit held, or once a limit set made it moot.
    private static int s_variableRead;

    /// <summary>The limit the host set, through <see cref="SetLimit"/> or <see cref="LimitVariable"/>; null when it set none.</summary>
    /// <exception cref="InvalidOperationException"><see cref="LimitVariable"/> is not a count of bytes.</exception>
    public static long? LimitBytes
    {
        get
        {
            ReadVariable();
            return Volatile.Read(ref s_limit) is > 0 and long set ? set : null;
        }
    }

    /// <summary>What the collector may commit now: a hard limit, its share of a container's limit, or the machine's memory.</summary>
    /// <exception cref="InvalidOperationException"><see cref="LimitVariable"/> is not a count of bytes.</exception>
    internal static long CollectorBytes
    {
        get
        {
            ReadVariable();
            return Volatile.Read(ref s_collector);
        }
    }

    /// <summary>
    /// Holds the process to the limit <see cref="LimitVariable"/> names, the first time the library needs a limit and none
    /// was set. A value that is no count of bytes fails that need, and each one after, with the same message: thrown from
    /// the type's initializer, it left the type unusable, and its message behind a <see cref="TypeInitializationException"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException"><see cref="LimitVariable"/> is not a count of bytes.</exception>
    private static void ReadVariable()
    {
        if (Volatile.Read(ref s_variableRead) == 0 && VariableError() is { } error)
        {
            throw new InvalidOperationException(error);
        }
    }

    /// <summary>
    /// Holds the process to the limit <see cref="LimitVariable"/> names, once: what is wrong with its value, or null when it
    /// names a count of bytes or nothing.
    /// </summary>
    private static string? VariableError()
    {
        lock (Gate)
        {
            if (s_variableRead != 0)
            {
                return null;
            }

            if (Environment.GetEnvironmentVariable(LimitVariable) is { Length: > 0 } text)
            {
                if (ParseBytes(text) is not long bytes)
                {
                    return $"{LimitVariable} is '{text}', which is not a count of bytes: 4294967296, 4096m and 4g are the same limit.";
                }

                HoldLocked(bytes);
            }

            Volatile.Write(ref s_variableRead, 1);
            return null;
        }
    }

    /// <summary>
    /// Holds the process to <paramref name="bytes"/> from now on, as a container's limit of that size would: the collector's
    /// heap to three quarters of it, the library's work within what the process leaves of it. Set again, the new limit
    /// replaces the last; in a container, the container's holds when it is less.
    /// </summary>
    /// <param name="bytes">The memory the process may use, in bytes.</param>
    /// <exception cref="ArgumentOutOfRangeException">The limit is not positive.</exception>
    /// <exception cref="InvalidOperationException">The collector already holds more than three quarters of the limit.</exception>
    public static void SetLimit(long bytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(bytes);
        Hold(bytes);
        QueryMemoryBudget.Process.ReadMemory();
    }

    /// <summary>Holds the collector's heap to its share of <paramref name="bytes"/>, then the library to the limit: the variable's no more.</summary>
    private static void Hold(long bytes)
    {
        lock (Gate)
        {
            HoldLocked(bytes);
            Volatile.Write(ref s_variableRead, 1);
        }
    }

    /// <summary>Under <see cref="Gate"/>: holds the collector's heap to its share of <paramref name="bytes"/>, then the library to the limit.</summary>
    private static void HoldLocked(long bytes)
    {
        long heap = bytes - (bytes / NativeShare);
        if (heap < s_collectorAtStart)
        {
            HoldHeap(heap, bytes);
            s_heapHeld = true;
        }
        else if (s_heapHeld)
        {
            // A limit whose heap passes the collector's own: the collector's own again.
            HoldHeap(s_collectorAtStart, bytes);
            s_heapHeld = false;
        }

        Volatile.Write(ref s_collector, GC.GetGCMemoryInfo().TotalAvailableMemoryBytes);
        Volatile.Write(ref s_limit, bytes);
    }

    /// <summary>
    /// Holds the collector's heap to <paramref name="heap"/>: a full collection first when it holds more than that, and a
    /// failure, the heap's limit as it was, when it still does.
    /// </summary>
    private static void HoldHeap(long heap, long limit)
    {
        object? previous = AppContext.GetData("GCHeapHardLimit");
        AppContext.SetData("GCHeapHardLimit", (ulong)heap);
        try
        {
            GC.RefreshMemoryLimit();
        }
        catch (InvalidOperationException)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
            try
            {
                GC.RefreshMemoryLimit();
            }
            catch (InvalidOperationException e)
            {
                AppContext.SetData("GCHeapHardLimit", previous);
                throw new InvalidOperationException($"The collector holds {GC.GetGCMemoryInfo().TotalCommittedBytes} bytes, more than the {heap} bytes of heap a limit of {limit} bytes leaves it.", e);
            }
        }
    }

    /// <summary>
    /// What the process uses against its limit now: the container's reading, under the limit set when that is less; outside a
    /// container, the limit set and the process's resident memory. Null when nothing holds the process but the collector.
    /// </summary>
    /// <remarks>
    /// A variable that is no count of bytes leaves the process held by its container alone here: what fails is each need
    /// of the limit, <see cref="LimitBytes"/> and the budgets', not the budget's type as it is made nor its timer.
    /// </remarks>
    internal static ContainerMemory? Read()
    {
        _ = VariableError();
        ContainerMemory? container = ContainerMemory.Read();
        long set = Volatile.Read(ref s_limit);
        if (set <= 0)
        {
            return container;
        }

        if (container is null)
        {
            return new ContainerMemory(set, Environment.WorkingSet);
        }

        return container.Limit <= set ? container : container with { Limit = set };
    }

    /// <summary>
    /// A count of bytes as a host writes it: digits, then at most a unit among <c>k</c>, <c>m</c>, <c>g</c> and <c>t</c> in
    /// either case, powers of 1 024, followed or not by <c>i</c> and <c>b</c>, as <c>4g</c>, <c>4G</c>, <c>4Gi</c> and
    /// <c>4GiB</c> are; null for what does not parse.
    /// </summary>
    internal static long? ParseBytes(string text)
    {
        ReadOnlySpan<char> span = text.AsSpan().Trim();
        int digits = 0;
        while (digits < span.Length && char.IsAsciiDigit(span[digits]))
        {
            digits++;
        }

        if (digits == 0 || !long.TryParse(span[..digits], NumberStyles.None, CultureInfo.InvariantCulture, out long count))
        {
            return null;
        }

        ReadOnlySpan<char> unit = span[digits..].TrimStart();
        int shift = unit.IsEmpty ? 0 : char.ToLowerInvariant(unit[0]) switch
        {
            'k' => 10,
            'm' => 20,
            'g' => 30,
            't' => 40,
            'b' when unit.Length == 1 => 0,
            _ => -1,
        };
        if (shift < 0 || !(unit.Length <= 1 || unit[1..] is "i" or "I" or "b" or "B" or "ib" or "iB" or "IB" or "Ib"))
        {
            return null;
        }

        return count > 0 && count <= long.MaxValue >> shift >> 1 ? count << shift : null;
    }
}
