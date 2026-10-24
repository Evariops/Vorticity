using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Serialization.Schemas;

namespace Vorticity;

/// <summary>
/// An object's reader through a <see cref="SynopsisCache"/>: a region its table of contents declares, with a
/// checksum, or a page or a segment of an index's run whose reader names it with its checksum, comes from the
/// disk when the cache holds it, and goes into the cache when the store sends it; every other read, its data and
/// its footer, goes to the store untouched.
/// </summary>
/// <remarks>
/// Readers that miss the same region at once wait for one read of it. A request set whose regions another
/// reader is fetching reads the rest beside that read, so the wait costs no round trip of its own.
/// </remarks>
internal sealed class SynopsisCacheReader : ISegmentReader, IIndexRegionReader
{
    private readonly ISegmentReader _inner;
    private readonly SynopsisCache _cache;
    private readonly UInt128 _uid;
    private readonly IReadOnlyDictionary<(ulong Offset, uint Length), IndexSegment> _regions;

    private SynopsisCacheReader(ISegmentReader inner, SynopsisCache cache, UInt128 uid, IReadOnlyDictionary<(ulong, uint), IndexSegment> regions)
    {
        _inner = inner;
        _cache = cache;
        _uid = uid;
        _regions = regions;
    }

    /// <summary>
    /// <paramref name="inner"/> through <paramref name="cache"/> for the regions <paramref name="contents"/>
    /// declares; <paramref name="inner"/> itself without a cache, an object's uid, or a region with a checksum.
    /// </summary>
    internal static ISegmentReader Over(ISegmentReader inner, SynopsisCache? cache, UInt128 uid, SynopsisContents contents)
    {
        if (cache is null || uid == UInt128.Zero)
        {
            return inner;
        }

        IReadOnlyDictionary<(ulong, uint), IndexSegment> regions = contents.DeclaredRegions;
        return regions.Count == 0 ? inner : new SynopsisCacheReader(inner, cache, uid, regions);
    }

    /// <summary>The store's reader under the cache's.</summary>
    internal ISegmentReader Inner => _inner;

    /// <summary>What the store's reader reads first of the object's tail.</summary>
    public int TailReadSize => _inner.TailReadSize;

    public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) => _inner.GetLengthAsync(cancellationToken);

    public ValueTask<SegmentOwner> ReadRangeAsync(long offset, int length, int alignment, CancellationToken cancellationToken) =>
        _inner.ReadRangeAsync(offset, length, alignment, cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    public async ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
    {
        if (!_regions.TryGetValue((spec.Offset, spec.Length), out IndexSegment region))
        {
            return await _inner.ReadAsync(spec, cancellationToken).ConfigureAwait(false);
        }

        _cache.Asked?.Invoke(_uid, region);
        for (bool waited = false; ; waited = true)
        {
            if (_cache.TryRead(_uid, region, out SegmentOwner cached))
            {
                return cached;
            }

            if (!_cache.TryBeginRead(_uid, region, out TaskCompletionSource? mine, out Task? theirs) && !waited)
            {
#pragma warning disable VSTHRD003
                await theirs!.WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
                continue;
            }

            try
            {
                SegmentOwner owner = await _inner.ReadAsync(spec, cancellationToken).ConfigureAwait(false);
                _cache.Admit(_uid, region, owner, owner.Buffer);
                return owner;
            }
            finally
            {
                if (mine is not null)
                {
                    _cache.EndRead(_uid, region, mine);
                }
            }
        }
    }

    public ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken) =>
        ReadSetAsync(requests, default, cancellationToken);

    /// <summary>
    /// As <see cref="ReadManyAsync"/>, the regions of an index's runs among the slots kept as those the contents declare:
    /// a lookup of an identifier asked the store for its fence page and its runs' segments at every question, a round
    /// trip each, where the directory before them came from the disk.
    /// </summary>
    public ValueTask ReadRegionsAsync(SegmentRequestSet requests, ReadOnlyMemory<IndexSegment> regions, CancellationToken cancellationToken) =>
        ReadSetAsync(requests, regions, cancellationToken);

    /// <summary>
    /// The region slot <paramref name="slot"/> of a set reads, which the cache keeps: one the contents declare, or the
    /// index region <paramref name="named"/> gives the slot, at its place, with a checksum.
    /// </summary>
    private bool TryKept(SegmentSpec spec, int slot, ReadOnlyMemory<IndexSegment> named, out IndexSegment region)
    {
        if (_regions.TryGetValue((spec.Offset, spec.Length), out region))
        {
            return true;
        }

        region = slot < named.Length ? named.Span[slot] : default;
        return region.Checksum is not null && region.Offset == spec.Offset && region.Length == spec.Length;
    }

    /// <summary>Reads <paramref name="requests"/>, its slot <c>i</c> the index region <paramref name="named"/>[i] where there is one.</summary>
    /// <remarks>Its state is pooled: every read of an object a dataset opened goes through it, the data's too.</remarks>
    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask ReadSetAsync(SegmentRequestSet requests, ReadOnlyMemory<IndexSegment> named, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(requests);
        if (requests.IsPopulated)
        {
            return;
        }

        // The regions kept: those the disk holds fill their slots, those another reader is fetching are waited
        // for, and this reader fetches the rest.
        List<(int Slot, IndexSegment Region, TaskCompletionSource Mine)> mine = [];
        List<(int Slot, IndexSegment Region, Task Theirs)>? waited = null;
        try
        {
            for (int slot = 0; slot < requests.Count; slot++)
            {
                SegmentSpec spec = requests.GetSpec(slot);
                if (requests.IsFilled(slot) || !TryKept(spec, slot, named, out IndexSegment region))
                {
                    continue;
                }

                _cache.Asked?.Invoke(_uid, region);
                if (_cache.TryRead(_uid, region, out SegmentOwner cached))
                {
                    requests.SetResult(slot, cached);
                    requests.NoteCacheHit();
                }
                else if (_cache.TryBeginRead(_uid, region, out TaskCompletionSource? reading, out Task? theirs))
                {
                    mine.Add((slot, region, reading!));
                }
                else
                {
                    (waited ??= []).Add((slot, region, theirs!));
                }
            }

            if (waited is null)
            {
                await _inner.ReadManyAsync(requests, cancellationToken).ConfigureAwait(false);
            }
            else
            {
                await ReadBesideAsync(requests, waited, cancellationToken).ConfigureAwait(false);
            }

            foreach ((int slot, IndexSegment region, _) in mine)
            {
                _cache.Admit(_uid, region, requests.GetOwner(slot), requests.GetBuffer(slot));
            }
        }
        catch
        {
            requests.AbandonPending();
            throw;
        }
        finally
        {
            foreach ((_, IndexSegment region, TaskCompletionSource reading) in mine)
            {
                _cache.EndRead(_uid, region, reading);
            }
        }
    }

    /// <summary>
    /// Reads the slots of <paramref name="requests"/> no other reader is fetching in a set of their own, beside
    /// the reads <paramref name="waited"/> waits for; then those slots from the disk, or from the store when
    /// the other read did not leave them there.
    /// </summary>
    private async ValueTask ReadBesideAsync(
        SegmentRequestSet requests, List<(int Slot, IndexSegment Region, Task Theirs)> waited, CancellationToken cancellationToken)
    {
        HashSet<int> theirs = [];
        Task[] others = new Task[waited.Count];
        for (int i = 0; i < waited.Count; i++)
        {
            theirs.Add(waited[i].Slot);
            others[i] = waited[i].Theirs;
        }

        using SegmentRequestSet own = new SegmentRequestSet();
        List<(int Slot, int Own)> placed = [];
        for (int slot = 0; slot < requests.Count; slot++)
        {
            if (!requests.IsFilled(slot) && !theirs.Contains(slot))
            {
                placed.Add((slot, own.Add(requests.GetSpec(slot))));
            }
        }

        Task reading = placed.Count > 0 ? _inner.ReadManyAsync(own, cancellationToken).AsTask() : Task.CompletedTask;
        try
        {
            await Task.WhenAll(others).WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            // This read's own segments are taken, or given back, whatever the wait did.
#pragma warning disable VSTHRD003
            await reading.ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }

        foreach ((int slot, int at) in placed)
        {
            requests.SetSharedResult(slot, own.GetOwner(at), own.GetBuffer(at));
        }

        using SegmentRequestSet late = new SegmentRequestSet();
        List<(int Slot, int Late)> missed = [];
        foreach ((int slot, IndexSegment region, _) in waited)
        {
            if (_cache.TryRead(_uid, region, out SegmentOwner cached))
            {
                requests.SetResult(slot, cached);
                requests.NoteCacheHit();
            }
            else
            {
                missed.Add((slot, late.Add(requests.GetSpec(slot))));
            }
        }

        if (missed.Count > 0)
        {
            await _inner.ReadManyAsync(late, cancellationToken).ConfigureAwait(false);
            foreach ((int slot, int at) in missed)
            {
                requests.SetSharedResult(slot, late.GetOwner(at), late.GetBuffer(at));
            }
        }

        requests.Complete();
    }

    public ValueTask DisposeAsync() => _inner.DisposeAsync();
}
