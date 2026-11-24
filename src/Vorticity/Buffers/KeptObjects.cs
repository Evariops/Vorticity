using System.Threading;

namespace Vorticity.Buffers;

/// <summary>
/// Objects kept from one operation to the next, up to a bound, as a holder of the registry of kept memory
/// (<see cref="HeldMemory"/>): those that sat unused since the registry last asked go to the collector, after each
/// collection of the oldest generation and at each turn of its slow timer, and every one of them when a reservation needs
/// the room. Kept outside the registry, a process held to a low limit never had them back.
/// </summary>
/// <typeparam name="T">What is kept: an object that holds nothing a release must give back.</typeparam>
/// <remarks>
/// What they let go is managed memory the next collection takes: told to the registry as nothing, which the budget then
/// reads as it collects, rather than as a guess of their bytes.
/// </remarks>
internal sealed class KeptObjects<T> : IHeldMemory
    where T : class
{
    private readonly T?[] _kept;
    private readonly Lock _gate = new Lock();
    private readonly long _bytesEach;
    private int _count;

    // The fewest kept since the registry last asked: those at the bottom that no taking reached since.
    private int _fewest;

    /// <summary>A stack of at most <paramref name="capacity"/> objects of some <paramref name="bytesEach"/> bytes, registered as <paramref name="holder"/>.</summary>
    internal KeptObjects(string holder, int capacity, long bytesEach)
    {
        Holder = holder;
        _kept = new T?[capacity];
        _bytesEach = bytesEach;
        HeldMemory.Register(this);
    }

    /// <inheritdoc/>
    public string Holder { get; }

    /// <summary>The bytes they hold, at least: their count times the bytes of one, as measured where they are kept.</summary>
    public long HeldBytes => Volatile.Read(ref _count) * _bytesEach;

    /// <summary>The objects kept now.</summary>
    internal int Count
    {
        get
        {
            lock (_gate)
            {
                return _count;
            }
        }
    }

    /// <summary>An object given back earlier, or null when none waits.</summary>
    internal T? Take()
    {
        lock (_gate)
        {
            if (_count == 0)
            {
                return null;
            }

            T kept = _kept[--_count]!;
            _kept[_count] = null;
            if (_count < _fewest)
            {
                _fewest = _count;
            }

            return kept;
        }
    }

    /// <summary>Keeps <paramref name="item"/> for a later taking; false, and it is left to the collector, past the bound.</summary>
    internal bool Give(T item)
    {
        lock (_gate)
        {
            if (_count == _kept.Length)
            {
                return false;
            }

            _kept[_count++] = item;
            return true;
        }
    }

    /// <summary>Lets go of every object kept when a reservation needs room: they rebuild without a request, as blocks do.</summary>
    public long GiveBack(KeptKind kind, long bytes)
    {
        if (kind == KeptKind.Blocks)
        {
            LetGo(int.MaxValue);
        }

        return 0;
    }

    /// <summary>Lets go of those no taking reached since the registry last asked.</summary>
    public long LetGoUnused(bool collection)
    {
        int unused;
        lock (_gate)
        {
            unused = _fewest;
        }

        LetGo(unused);
        return 0;
    }

    /// <summary>Lets go of up to <paramref name="most"/> objects from the bottom of the stack, the longest kept, and starts a new watch.</summary>
    private void LetGo(int most)
    {
        lock (_gate)
        {
            int gone = System.Math.Min(most, _count);
            if (gone > 0)
            {
                System.Array.Copy(_kept, gone, _kept, 0, _count - gone);
                System.Array.Clear(_kept, _count - gone, gone);
                _count -= gone;
            }

            _fewest = _count;
        }
    }
}
