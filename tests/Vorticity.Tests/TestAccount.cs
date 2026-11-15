using System.Threading;
using Vorticity.Buffers;

namespace Vorticity.Tests;

/// <summary>
/// An account the tests rent their blocks under: it refuses nothing and counts what its blocks hold, which a test reads to
/// see a rent counted and given back. The process's budget never sees it, so a test that drops a block leaks nothing past
/// itself.
/// </summary>
internal sealed class TestAccount : MemoryAccount
{
    private long _held;

    /// <summary>The account the tests that need one and read none share.</summary>
    internal static TestAccount Shared { get; } = new TestAccount();

    /// <summary>The bytes its blocks hold now.</summary>
    internal long Held => Interlocked.Read(ref _held);

    internal override bool TryTake(long bytes)
    {
        Interlocked.Add(ref _held, bytes);
        return true;
    }

    internal override void Give(long bytes) => Interlocked.Add(ref _held, -bytes);

    internal override VortexMemoryException Refused(long bytes) => new VortexMemoryException("A test's account refuses nothing.");
}
