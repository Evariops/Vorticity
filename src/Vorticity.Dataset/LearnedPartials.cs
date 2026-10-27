using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Hashing;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;
using Vorticity.IO;

namespace Vorticity.Dataset;

/// <summary>
/// The partials a dataset's handle learned of its objects, each of one family (<see cref="Aggregating.LearnedFamily"/>):
/// kept in memory, under a budget of bytes, in the session's synopsis cache when it has one, which keeps them for the
/// handles and the processes after, and in the dataset's store, which keeps them for every node (spec H, 3). An object
/// never changes, so a partial of it stays exact as long as the object lives, under the identity its postscript carries.
/// </summary>
/// <remarks>
/// In the cache and in the store, a partial's bytes are followed by their XXH3-128, which a read checks: bytes torn are no
/// partial, and the object is read as rows again.
/// </remarks>
internal sealed class LearnedPartials
{
    /// <summary>The bytes the handle keeps in memory at most; past them, partials are kept in the cache and the store alone.</summary>
    internal const long Budget = 64L << 20;

    /// <summary>
    /// The bytes of a partial kept at least, whatever its object's size: some 10⁴ groups of four measures. Past a sixty-fourth of
    /// its object (<see cref="Cap"/>), a family's partial answers the question and is not kept.
    /// </summary>
    internal const int MaxBytes = 1 << 20;

    /// <summary>The bytes of a partial read back at most, from the cache or the store: a sixty-fourth of an object of 16 GiB.</summary>
    internal const int MostBytes = 256 << 20;

    /// <summary>
    /// The bytes a partial of an object of <paramref name="objectBytes"/> keeps at most (spec H, 3.3): a sixty-fourth of the
    /// object, a mebibyte at least. Route by hour over an object of a day, ~70 MB of ~173 GB, is learned.
    /// </summary>
    internal static long Cap(long objectBytes) => Math.Min(Math.Max(MaxBytes, objectBytes / 64), MostBytes);

    private readonly ConcurrentDictionary<(UInt128 Object, UInt128 Family), AggregationPartial> _held = new();
    private long _bytes;

    // Per family, the objects whose partial the store keeps and the bytes of each, from the one listing this handle made;
    // and the listings on their way, shared by the questions that ask at once.
    private readonly ConcurrentDictionary<UInt128, ConcurrentDictionary<UInt128, long>> _listed = new();
    private readonly ConcurrentDictionary<UInt128, Task<ConcurrentDictionary<UInt128, long>>> _listing = new();

    // The writes to the store on their way, which a flush waits for, and the families whose text this handle wrote.
    private readonly List<Task> _writes = [];
    private readonly ConcurrentDictionary<UInt128, bool> _described = new();

    // The families whose partial an object would hold past MaxBytes: their questions read its rows, learning nothing.
    private readonly ConcurrentDictionary<(UInt128 Object, UInt128 Family), bool> _tooLarge = new();

    /// <summary>Whether family <paramref name="family"/> learned of the object <paramref name="uid"/> a partial too large to keep, which its questions no longer learn.</summary>
    internal bool TooLarge(UInt128 uid, UInt128 family) => _tooLarge.ContainsKey((uid, family));

    /// <summary>The partial of family <paramref name="family"/> of the object <paramref name="uid"/>, from memory or the cache; false when neither holds it.</summary>
    internal bool TryGet(UInt128 uid, UInt128 family, SynopsisCache? cache, out AggregationPartial partial)
    {
        if (_held.TryGetValue((uid, family), out partial!))
        {
            return true;
        }

        if (cache is null || !cache.TryReadSealed(NameOf(uid, family), MostBytes + 16, out SegmentOwner owner))
        {
            return false;
        }

        using (owner)
        {
            ReadOnlySpan<byte> bytes = owner.Buffer.Span;
            if (bytes.Length < 16 || XxHash128.HashToUInt128(bytes[..^16]) != ReadHash(bytes[^16..]))
            {
                cache.DropSealed(NameOf(uid, family));
                return false;
            }

            partial = AggregationPartial.Of(bytes[..^16].ToArray());
        }

        Hold(uid, family, partial);
        return true;
    }

    // The objects a compaction merged whole into one, by the one's identity: their partials sum to its own.
    private readonly ConcurrentDictionary<UInt128, UInt128[]> _merged = new();

    /// <summary>
    /// Records that the object <paramref name="output"/> holds the rows of <paramref name="inputs"/>, all of them and no
    /// other, as a compaction that wrote it alone from them, none with a row marked, says once it committed: the partials of
    /// the inputs, summed, are its own, in the handle and in the session's cache.
    /// </summary>
    internal void Inherit(UInt128 output, UInt128[] inputs, SynopsisCache? cache)
    {
        _merged[output] = inputs;
        if (cache is not null)
        {
            byte[] kept = new byte[(inputs.Length * 16) + 16];
            for (int i = 0; i < inputs.Length; i++)
            {
                WriteHash(kept.AsSpan(i * 16), inputs[i]);
            }

            WriteHash(kept.AsSpan(inputs.Length * 16), XxHash128.HashToUInt128(kept.AsSpan(0, inputs.Length * 16)));
            cache.AdmitSealed(output, LineageName(output), kept);
        }
    }

    /// <summary>The objects whose rows the object <paramref name="uid"/> holds, which a compaction merged into it alone; null when none is known.</summary>
    internal UInt128[]? MergedInto(UInt128 uid, SynopsisCache? cache)
    {
        if (_merged.TryGetValue(uid, out UInt128[]? inputs))
        {
            return inputs;
        }

        if (cache is null || !cache.TryReadSealed(LineageName(uid), MaxInputs * 16 + 16, out SegmentOwner owner))
        {
            return null;
        }

        using (owner)
        {
            ReadOnlySpan<byte> bytes = owner.Buffer.Span;
            if (bytes.Length < 32 || bytes.Length % 16 != 0 || XxHash128.HashToUInt128(bytes[..^16]) != ReadHash(bytes[^16..]))
            {
                cache.DropSealed(LineageName(uid));
                return null;
            }

            inputs = new UInt128[(bytes.Length / 16) - 1];
            for (int i = 0; i < inputs.Length; i++)
            {
                inputs[i] = ReadHash(bytes[(i * 16)..]);
            }
        }

        _merged[uid] = inputs;
        return inputs;
    }

    /// <summary>The objects a compaction merges whose lineage is kept at most.</summary>
    internal const int MaxInputs = 4_096;

    private static string LineageName(UInt128 uid) => string.Create(CultureInfo.InvariantCulture, $"{uid:x32}-lineage");

    /// <summary>
    /// The families the handle keeps up at most, those its questions asked last (spec H, 3.3): at some 1.5 ms of a core a
    /// family for each object appended, about a tenth of a second an object.
    /// </summary>
    internal const int MaxLive = 64;

    // The families the handle's questions asked, by their identity, with when each was asked last: an object it appends learns
    // their partials as it is written (KeptUpAsync).
    private readonly ConcurrentDictionary<UInt128, (Aggregating.LearnedFamily Family, long Ticks)> _live = new();

    /// <summary>Notes that a question asked <paramref name="family"/> at <paramref name="ticks"/>, the least recent of the families kept up let go past <see cref="MaxLive"/>.</summary>
    internal void Asked(Aggregating.LearnedFamily family, long ticks)
    {
        _live[family.Identity] = (family, ticks);
        if (_live.Count > MaxLive)
        {
            UInt128 oldest = default;
            long least = long.MaxValue;
            foreach (KeyValuePair<UInt128, (Aggregating.LearnedFamily Family, long Ticks)> live in _live)
            {
                if (live.Value.Ticks < least)
                {
                    (oldest, least) = (live.Key, live.Value.Ticks);
                }
            }

            _live.TryRemove(oldest, out _);
        }
    }

    /// <summary>The families the handle keeps up: asked at or after <paramref name="since"/>, in ticks.</summary>
    internal List<Aggregating.LearnedFamily> Live(long since)
    {
        List<Aggregating.LearnedFamily> live = [];
        foreach ((Aggregating.LearnedFamily family, long ticks) in _live.Values)
        {
            if (ticks >= since)
            {
                live.Add(family);
            }
        }

        return live;
    }

    /// <summary>Whether the handle holds the partial of family <paramref name="family"/> of the object <paramref name="uid"/> in memory.</summary>
    internal bool InMemory(UInt128 uid, UInt128 family) => _held.ContainsKey((uid, family));

    /// <summary>
    /// Whether the handle, the cache or the store holds the partial of family <paramref name="family"/> of the object
    /// <paramref name="uid"/>, or of a family that serves it (<see cref="CoveringAsync"/>), with no read of it: the store as
    /// each family's listing said it (<see cref="ListAsync"/>).
    /// </summary>
    internal bool Holds(UInt128 uid, UInt128 family, SynopsisCache? cache)
    {
        if (HoldsOwn(uid, family, cache))
        {
            return true;
        }

        if (_covering.TryGetValue(family, out (int Known, Covering[] Families) covering))
        {
            foreach (Covering wider in covering.Families)
            {
                if (HoldsOwn(uid, wider.Key, cache))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>Whether the handle, the cache or the store holds the partial of family <paramref name="family"/> itself of the object <paramref name="uid"/>.</summary>
    internal bool HoldsOwn(UInt128 uid, UInt128 family, SynopsisCache? cache) =>
        _held.ContainsKey((uid, family)) || cache?.HoldsSealed(NameOf(uid, family)) == true || Stored(uid, family);

    /// <summary>A family that serves another (<see cref="Aggregating.LearnedFamily.Covers"/>): the key of its partials, and the place of each key of the other among its own.</summary>
    internal readonly record struct Covering(UInt128 Key, int[] KeyOf);

    /// <summary>The families that serve one, at most: each one's partials listed once for the handle, and read where the family's own are missing.</summary>
    internal const int MaxCovering = 4;

    /// <summary>The bytes of a family's text read at most: its keys, aggregates and filter.</summary>
    private const int MaxText = 64 << 10;

    // The families' texts the handle knows, by the key of their partials, with the identity of each, and how many: those whose
    // partials it kept, and those the store lists, read once (spec H, 3.5). Per family's key, the families that serve it,
    // found while the handle knew as many texts.
    private readonly ConcurrentDictionary<UInt128, (string Text, UInt128 Identity)> _texts = new();
    private int _known;
    private Task? _families;
    private object? _familiesLock;
    private readonly ConcurrentDictionary<UInt128, (int Known, Covering[] Families)> _covering = new();

    /// <summary>
    /// Finds which families serve <paramref name="family"/>, whose partials are kept under <paramref name="key"/>
    /// (<see cref="Aggregating.LearnedFamily.Covers"/>), once for the handle while it knows as many: of the same columns,
    /// among those whose partials it kept and those <paramref name="store"/> lists under <c>learned/families/</c>, each text
    /// read once; the shortest texts first, of the fewest keys and aggregates, <see cref="MaxCovering"/> at most, each one's
    /// partials listed (<see cref="ListAsync"/>).
    /// </summary>
    internal async ValueTask CoveringAsync(IObjectStore? store, Aggregating.LearnedFamily family, UInt128 key, CancellationToken cancellationToken)
    {
        if (store is not null)
        {
            // The listing of the families this handle started, which every question awaits.
#pragma warning disable VSTHRD003
            await LazyInitializer.EnsureInitialized(ref _families, ref _familiesLock, () => FamiliesAsync(store)).WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
        }

        int known = Volatile.Read(ref _known);
        if (_covering.TryGetValue(key, out (int Known, Covering[] Families) found) && found.Known == known)
        {
            return;
        }

        // A family's key is its identity and its columns' (FamilyProof): one of the same columns differs from it as its own.
        UInt128 columns = key ^ family.Identity;
        List<(Covering Family, int Length)> wider = [];
        foreach (KeyValuePair<UInt128, (string Text, UInt128 Identity)> other in _texts)
        {
            if (other.Key != key && (other.Key ^ other.Value.Identity) == columns
                && Aggregating.LearnedFamily.Covers(other.Value.Text, family.Text) is { } keyOf)
            {
                wider.Add((new Covering(other.Key, keyOf), other.Value.Text.Length));
            }
        }

        wider.Sort(static (a, b) => a.Length.CompareTo(b.Length));
        Covering[] covering = new Covering[Math.Min(wider.Count, MaxCovering)];
        for (int i = 0; i < covering.Length; i++)
        {
            covering[i] = wider[i].Family;
            if (store is not null)
            {
                await ListAsync(store, covering[i].Key, cancellationToken).ConfigureAwait(false);
            }
        }

        _covering[key] = (known, covering);
    }

    /// <summary>
    /// The partial of a family that serves <paramref name="family"/> of the object <paramref name="uid"/> (<see cref="CoveringAsync"/>),
    /// from memory, the cache or <paramref name="store"/>, and the place of each of <paramref name="family"/>'s keys among
    /// that family's; null when none holds one.
    /// </summary>
    internal async ValueTask<(AggregationPartial Partial, int[] KeyOf)?> CoveredAsync(
        UInt128 uid, UInt128 family, SynopsisCache? cache, IObjectStore? store, CancellationToken cancellationToken)
    {
        if (!_covering.TryGetValue(family, out (int Known, Covering[] Families) covering))
        {
            return null;
        }

        foreach (Covering wider in covering.Families)
        {
            if (HoldsOwn(uid, wider.Key, cache) && await GetAsync(uid, wider.Key, cache, store, cancellationToken).ConfigureAwait(false) is { } partial)
            {
                return (partial, wider.KeyOf);
            }
        }

        return null;
    }

    /// <summary>Learns the text of the family whose partials are kept under <paramref name="key"/>.</summary>
    private void Know(UInt128 key, string text)
    {
        if (_texts.TryAdd(key, (text, XxHash128.HashToUInt128(Encoding.UTF8.GetBytes(text)))))
        {
            Interlocked.Increment(ref _known);
        }
    }

    /// <summary>Reads the texts of the families <paramref name="store"/> lists that the handle does not know, side by side; none when it lists or reads none.</summary>
    private async Task FamiliesAsync(IObjectStore store)
    {
        List<(UInt128 Key, int Length)> unknown = [];
        try
        {
            await foreach (ObjectListing listing in store.ListAsync(CommitKey.FamiliesPrefix, null, CancellationToken.None).ConfigureAwait(false))
            {
                if (CommitKey.TryParseLearned(listing.Key, out UInt128 family, out UInt128? uid) && uid is null && !_texts.ContainsKey(family)
                    && listing.Head.Length is > 0 and <= MaxText)
                {
                    unknown.Add((family, (int)listing.Head.Length));
                }
            }

            await Parallel.ForEachAsync(unknown, new ParallelOptions { MaxDegreeOfParallelism = 16 }, async (described, cancellationToken) =>
            {
                byte[] bytes = new byte[described.Length];
                ObjectRead read = await store.ReadAsync(CommitKey.ForFamily(described.Key), 0, bytes, null, cancellationToken).ConfigureAwait(false);
                if (read.BytesRead == bytes.Length)
                {
                    Know(described.Key, Encoding.UTF8.GetString(bytes));
                }
            }).ConfigureAwait(false);
        }
        catch (Exception failure) when (failure is ObjectStoreException or ObjectNotFoundException or IOException)
        {
            // A store that lists or reads no family, or a family a vacuum took since the listing: those known serve.
        }
    }

    /// <summary>
    /// Reads, once for the handle, which objects' partials of <paramref name="family"/> <paramref name="store"/> keeps: a
    /// listing of the family's prefix, which <see cref="Holds"/> and <see cref="GetAsync"/> read after (spec H, 3.2).
    /// </summary>
    internal async ValueTask ListAsync(IObjectStore store, UInt128 family, CancellationToken cancellationToken)
    {
        if (_listed.ContainsKey(family))
        {
            return;
        }

        // The listing this handle started, which every question of the family awaits.
#pragma warning disable VSTHRD003
        ConcurrentDictionary<UInt128, long> held = await _listing.GetOrAdd(family, f => ListedAsync(store, f)).WaitAsync(cancellationToken).ConfigureAwait(false);
#pragma warning restore VSTHRD003
        _listed.TryAdd(family, held);
    }

    /// <summary>
    /// The partial of family <paramref name="family"/> of the object <paramref name="uid"/>: from memory, the cache, or
    /// <paramref name="store"/> as the family's listing names it, which the cache and the memory keep after; null when none
    /// holds it.
    /// </summary>
    internal async ValueTask<AggregationPartial?> GetAsync(
        UInt128 uid, UInt128 family, SynopsisCache? cache, IObjectStore? store, CancellationToken cancellationToken)
    {
        if (TryGet(uid, family, cache, out AggregationPartial held))
        {
            return held;
        }

        if (store is null || !_listed.TryGetValue(family, out ConcurrentDictionary<UInt128, long>? listed)
            || !listed.TryGetValue(uid, out long length) || length is < 16 or > MostBytes + 16L)
        {
            return null;
        }

        byte[] bytes = GC.AllocateUninitializedArray<byte>((int)length);
        try
        {
            ObjectRead read = await store.ReadAsync(CommitKey.ForLearned(family, uid), 0, bytes, null, cancellationToken).ConfigureAwait(false);
            if (read.BytesRead != bytes.Length)
            {
                return null;
            }
        }
        catch (ObjectNotFoundException)
        {
            // A vacuum took it since the listing.
            listed.TryRemove(uid, out _);
            return null;
        }

        ReadOnlySpan<byte> kept = bytes;
        if (XxHash128.HashToUInt128(kept[..^16]) != ReadHash(kept[^16..]))
        {
            return null;
        }

        AggregationPartial partial = AggregationPartial.FromBytes(bytes.AsMemory(0, bytes.Length - 16));
        Hold(uid, family, partial);
        cache?.AdmitSealed(uid, NameOf(uid, family), bytes);
        return partial;
    }

    /// <summary>
    /// Keeps <paramref name="partial"/>, of family <paramref name="family"/> of the object <paramref name="uid"/> of
    /// <paramref name="objectBytes"/>, when it is no larger than <see cref="Cap"/>: in memory, in the cache, and in
    /// <paramref name="store"/> under the family's <paramref name="text"/>, written in the background (spec H, 3.1).
    /// </summary>
    internal void Keep(UInt128 uid, UInt128 family, AggregationPartial partial, SynopsisCache? cache, long objectBytes, IObjectStore? store, string? text)
    {
        ReadOnlySpan<byte> bytes = partial.Bytes.Span;
        if (bytes.Length > Cap(objectBytes))
        {
            _tooLarge.TryAdd((uid, family), true);
            return;
        }

        Hold(uid, family, partial);
        if (text is not null && !_texts.ContainsKey(family))
        {
            Know(family, text);
        }

        if (cache is null && (store is null || text is null))
        {
            return;
        }

        byte[] kept = new byte[bytes.Length + 16];
        bytes.CopyTo(kept);
        WriteHash(kept.AsSpan(bytes.Length), XxHash128.HashToUInt128(bytes));
        cache?.AdmitSealed(uid, NameOf(uid, family), kept);
        if (store is not null && text is not null && !Stored(uid, family))
        {
            Task write = WriteAsync(store, uid, family, text, kept);
            lock (_writes)
            {
                _writes.RemoveAll(static task => task.IsCompleted);
                _writes.Add(write);
            }
        }
    }

    /// <summary>Waits for the partials on their way to the store: what a handle's disposal does, so that its learning outlives it.</summary>
    internal async ValueTask FlushAsync()
    {
        Task[] writes;
        lock (_writes)
        {
            writes = [.. _writes];
            _writes.Clear();
        }

        await Task.WhenAll(writes).ConfigureAwait(false);
    }

    /// <summary>Whether the family's listing names the object's partial.</summary>
    private bool Stored(UInt128 uid, UInt128 family) => _listed.TryGetValue(family, out ConcurrentDictionary<UInt128, long>? held) && held.ContainsKey(uid);

    /// <summary>The objects whose partial of <paramref name="family"/> the store keeps, and their bytes; none when it lists none.</summary>
    private static async Task<ConcurrentDictionary<UInt128, long>> ListedAsync(IObjectStore store, UInt128 family)
    {
        ConcurrentDictionary<UInt128, long> held = new();
        try
        {
            await foreach (ObjectListing listing in store.ListAsync(CommitKey.LearnedOf(family), null, CancellationToken.None).ConfigureAwait(false))
            {
                if (CommitKey.TryParseLearned(listing.Key, out _, out UInt128? uid) && uid is { } stored)
                {
                    held[stored] = listing.Head.Length;
                }
            }
        }
        catch (ObjectStoreException)
        {
            // A store that lists nothing here: the questions answer from the rows, and learn.
        }

        return held;
    }

    /// <summary>Writes the partial to the store, the family's text first when this handle wrote none; a failure leaves it unkept there.</summary>
    private async Task WriteAsync(IObjectStore store, UInt128 uid, UInt128 family, string text, byte[] kept)
    {
        try
        {
            if (_described.TryAdd(family, true))
            {
                await store.PutAsync(CommitKey.ForFamily(family), new ReadOnlySequence<byte>(Encoding.UTF8.GetBytes(text)), PutCondition.None, CancellationToken.None)
                    .ConfigureAwait(false);
            }

            await store.PutAsync(CommitKey.ForLearned(family, uid), new ReadOnlySequence<byte>(kept), PutCondition.None, CancellationToken.None).ConfigureAwait(false);
            if (_listed.TryGetValue(family, out ConcurrentDictionary<UInt128, long>? held))
            {
                held[uid] = kept.Length;
            }
        }
        catch (Exception failure) when (failure is ObjectStoreException or IOException or NotSupportedException or UnauthorizedAccessException)
        {
            // The store keeps no partial: the next question of another node learns it again.
            _described.TryRemove(family, out _);
        }
    }

    private void Hold(UInt128 uid, UInt128 family, AggregationPartial partial)
    {
        long bytes = partial.Bytes.Length;
        if (Interlocked.Add(ref _bytes, bytes) > Budget || !_held.TryAdd((uid, family), partial))
        {
            Interlocked.Add(ref _bytes, -bytes);
        }
    }

    private static string NameOf(UInt128 uid, UInt128 family) => string.Create(CultureInfo.InvariantCulture, $"{uid:x32}-{family:x32}-partial");

    private static UInt128 ReadHash(ReadOnlySpan<byte> bytes) =>
        new UInt128(System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes[8..]), System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(bytes));

    private static void WriteHash(Span<byte> destination, UInt128 hash)
    {
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination, (ulong)hash);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(destination[8..], (ulong)(hash >> 64));
    }
}
