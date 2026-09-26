using System;
using System.Runtime.InteropServices;

namespace Vorticity.Buffers;

/// <summary>What a <see cref="CollectionSweeper{T}"/> sweeps after collections.</summary>
internal interface ISweptAfterCollections
{
    /// <summary>Lets go of what has not been used lately; runs on the finalizer thread.</summary>
    void Sweep();
}

/// <summary>
/// An object nothing references, whose finalizer runs after a collection, and after collections
/// of the oldest generation only once it has been promoted there: each run sweeps its target and
/// registers the sweeper again, for as long as the target lives.
/// </summary>
/// <typeparam name="T">The swept type.</typeparam>
internal sealed class CollectionSweeper<T>
    where T : class, ISweptAfterCollections
{
    // A handle and not a WeakReference, whose own finalizer would run with this one and let the
    // target go first.
    private WeakGCHandle<T> _target;

    private CollectionSweeper(T target)
    {
        _target = new WeakGCHandle<T>(target);
    }

    ~CollectionSweeper()
    {
        if (_target.TryGetTarget(out T? target))
        {
            target.Sweep();
            GC.ReRegisterForFinalize(this);
        }
        else
        {
            _target.Dispose();
        }
    }

    /// <summary>Sweeps <paramref name="target"/> after collections, for as long as it lives.</summary>
    /// <param name="target">The swept object.</param>
    internal static void Register(T target) => _ = new CollectionSweeper<T>(target);
}
