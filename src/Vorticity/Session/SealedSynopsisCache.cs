using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.Indexes;
using Vorticity.Sealing;

namespace Vorticity;

/// <summary>
/// What a sealed object's reader keeps in a <see cref="SynopsisCache"/>: the object's trailer, the frames that hold a
/// region its table of contents declares, and, when the cache has a quota for data, the frames of the other segments it
/// reads, all as the store holds them. The reader decrypts them at each read, so the disk holds neither a key nor a
/// plaintext byte.
/// </summary>
internal sealed class SealedSynopsisCache : ISealedFrameCache
{
    private static readonly IReadOnlyDictionary<(ulong, uint), IndexSegment> NoRegions = new Dictionary<(ulong, uint), IndexSegment>();

    private readonly SynopsisCache _cache;
    private readonly UInt128 _uid;
    private readonly IReadOnlyDictionary<(ulong Offset, uint Length), IndexSegment> _regions;
    private readonly string _trailer;
    private long _admitted;

    private SealedSynopsisCache(SynopsisCache cache, UInt128 uid, IReadOnlyDictionary<(ulong, uint), IndexSegment> regions)
    {
        _cache = cache;
        _uid = uid;
        _regions = regions;
        _trailer = SynopsisCache.TrailerNameOf(uid);
    }

    /// <summary>
    /// The frames of object <paramref name="uid"/> in <paramref name="cache"/>, for the regions <paramref name="contents"/>
    /// declares, and for its data when the cache keeps data; null without a cache or an object's uid, and when the cache
    /// would keep nothing of the object: no data, and no region with a checksum.
    /// </summary>
    internal static SealedSynopsisCache? Over(SynopsisCache? cache, UInt128 uid, ObjectContents? contents)
    {
        if (cache is null || uid == UInt128.Zero)
        {
            return null;
        }

        IReadOnlyDictionary<(ulong, uint), IndexSegment> regions = contents?.DeclaredRegions ?? NoRegions;
        return regions.Count == 0 && !cache.KeepsData ? null : new SealedSynopsisCache(cache, uid, regions);
    }

    /// <summary>The bytes this object's reader handed to the cache, its trailer and its frames: what it read from the store for the cache.</summary>
    internal long AdmittedBytes => Interlocked.Read(ref _admitted);

    public bool KeepsData => _cache.KeepsData;

    public bool TryReadTrailer(out SegmentOwner trailer) => _cache.TryReadSealed(_trailer, SealedFormat.MaxTrailerBytes, out trailer);

    public void AdmitTrailer(ReadOnlySpan<byte> trailer)
    {
        if (!_cache.HoldsSealed(_trailer))
        {
            _cache.AdmitSealed(_uid, _trailer, trailer);
            Interlocked.Add(ref _admitted, trailer.Length);
        }
    }

    public void DropTrailer() => _cache.DropSealed(_trailer);

    public bool Declares(ulong offset, uint length) => _regions.ContainsKey((offset, length));

    public bool Holds(ulong offset, uint length, ReadOnlySpan<byte> plain) =>
        _regions.TryGetValue((offset, length), out IndexSegment region) && region.Holds(plain);

    public bool HoldsFrame(long cipherOffset, int length) => _cache.HoldsFrame(_uid, cipherOffset, length);

    public bool TryReadFrame(long cipherOffset, int length, out SegmentOwner frame) => _cache.TryReadFrame(_uid, cipherOffset, length, out frame);

    public void AdmitFrame(long cipherOffset, ReadOnlySpan<byte> frame)
    {
        if (!_cache.HoldsFrame(_uid, cipherOffset, frame.Length))
        {
            _cache.AdmitSealed(_uid, SynopsisCache.FrameNameOf(_uid, cipherOffset, frame.Length), frame);
            Interlocked.Add(ref _admitted, frame.Length);
        }
    }

    public void AdmitDataFrame(long cipherOffset, SegmentOwner owner, VortexBuffer frame)
    {
        if (_cache.AdmitDataFrame(_uid, cipherOffset, owner, frame))
        {
            Interlocked.Add(ref _admitted, frame.Length);
        }
    }

    public void DropFrame(long cipherOffset, int length) => _cache.DropFrame(_uid, cipherOffset, length);

    public void BeginReadFrames(ReadOnlySpan<long> frames, TaskCompletionSource mine, List<long> registered, List<Task> theirs) =>
        _cache.BeginReadFrames(_uid, frames, mine, registered, theirs);

    public void EndReadFrames(ReadOnlySpan<long> frames, TaskCompletionSource mine) => _cache.EndReadFrames(_uid, frames, mine);
}
