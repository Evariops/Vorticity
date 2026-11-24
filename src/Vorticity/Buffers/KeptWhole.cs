using System;
using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// A reserve kept under a lock of its own and let go whole, as a holder of the registry of kept memory
/// (<see cref="HeldMemory"/>): all of it goes when nothing took from it since the registry last asked, after a collection
/// of the oldest generation or at a turn of the registry's slow timer, and all of it when a reservation needs the room.
/// For a reserve <see cref="KeptObjects{T}"/> does not fit: one taken from by a property of what it keeps, or in runs.
/// </summary>
/// <remarks>
/// What it lets go is managed memory the next collection takes, told to the registry as nothing, as
/// <see cref="KeptObjects{T}"/> tells it.
/// </remarks>
internal sealed class KeptWhole : IHeldMemory
{
    private readonly Func<long> _heldBytes;
    private readonly Action _letGo;

    // Whether something took from the reserve since the registry last asked. Under the gate.
    private bool _taken;

    /// <summary>
    /// A holder named <paramref name="holder"/>, whose reserve holds <paramref name="heldBytes"/> and lets go of all it
    /// keeps by <paramref name="letGo"/>, which runs under <see cref="Gate"/>.
    /// </summary>
    internal KeptWhole(string holder, Func<long> heldBytes, Action letGo)
    {
        Holder = holder;
        _heldBytes = heldBytes;
        _letGo = letGo;
        HeldMemory.Register(this);
    }

    /// <summary>The lock the reserve is kept under, its takings and givings as its letting go.</summary>
    internal Lock Gate { get; } = new Lock();

    /// <inheritdoc/>
    public string Holder { get; }

    /// <inheritdoc/>
    public long HeldBytes => _heldBytes();

    /// <summary>Notes, under <see cref="Gate"/>, that something took from the reserve.</summary>
    internal void Took() => _taken = true;

    /// <summary>Lets go of all the reserve keeps when a reservation needs room: it rebuilds without a request.</summary>
    public long GiveBack(KeptKind kind, long bytes)
    {
        if (kind == KeptKind.Blocks)
        {
            LetGo(whatever: true);
        }

        return 0;
    }

    /// <summary>Lets go of all the reserve keeps when nothing took from it since the registry last asked.</summary>
    public long LetGoUnused(bool collection)
    {
        LetGo(whatever: false);
        return 0;
    }

    private void LetGo(bool whatever)
    {
        lock (Gate)
        {
            if (whatever || !_taken)
            {
                _letGo();
            }

            _taken = false;
        }
    }
}
