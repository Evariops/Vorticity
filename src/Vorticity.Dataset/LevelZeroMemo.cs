using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Types.Variants;
using Vorticity.Writing;

namespace Vorticity.Dataset;

/// <summary>
/// The objects of level 0 a query node read through keys of their variant columns, shredded in memory once.
/// An object of level 0 is stored as it was appended, shredded only in the declared paths when its handle
/// shredded on append, so each question on another key of it would parse every row. The first question
/// parses the object as it lies, its keys read in place, while the object is read whole beside it and written
/// again in memory, the keys asked shredded at the class most of their values hold, with their path maps;
/// every later question on those keys, once the copy is made, reads it through them alone, as it reads an
/// object a job shredded. An object whose append shredded every key asked is read as it lies, by those keys,
/// with no copy.
/// </summary>
/// <remarks>
/// <para>
/// The copy holds the object's rows in their order, those deleted included, so the entry's deletion vector
/// applies to it as to the object. Its memory, as large as the object, is reserved from the session's query
/// memory budget before it is made; when the budget refuses it, no copy is made, and the questions parse the
/// object as it lies. A copy is dropped once its object has left level 0, which the next read of the memo finds.
/// </para>
/// <para>
/// No question waits on a copy: one copy of an object is made at a time, beside the questions, which parse the
/// object until it is done; one a copy held serves reads it.
/// </para>
/// <para>
/// A question on keys a copy lacks shreds the object again with the keys of both, so a key is parsed once
/// per object and per node.
/// </para>
/// </remarks>
internal sealed class LevelZeroMemo : IAsyncDisposable
{
    // The copies being made, by object, one at a time each, and what stops them when the memo is let go; whether a question
    // forgets the objects gone from level 0, which the others leave it to.
    private readonly Dictionary<UInt128, Task> _making = [];
    private readonly CancellationTokenSource _stopping = new CancellationTokenSource();
    private int _forgetting;
    private readonly Dictionary<UInt128, Copy> _copies = [];
    private long _shredded;
    private long _served;
    private long _refused;

    // Set under the lock of the copies: a memo let go keeps no copy, and serves the objects as they lie.
    private bool _disposed;

    /// <summary>How many times an object was read whole and shredded in memory.</summary>
    internal long Shredded => Interlocked.Read(ref _shredded);

    /// <summary>How many leases a copy held served, with no parse.</summary>
    internal long Served => Interlocked.Read(ref _served);

    /// <summary>How many copies the memory budget refused to keep.</summary>
    internal long Refused => Interlocked.Read(ref _refused);

    /// <summary>How many leases an object served as it lies, an append having shredded the keys asked.</summary>
    internal long AsAppended => Interlocked.Read(ref _asAppended);

    private long _asAppended;

    // The keys, by column, each object an append shredded was found to read by: a question on them leases it as it lies.
    // Under the lock of the copies.
    private readonly Dictionary<UInt128, Dictionary<string, HashSet<string>>> _lying = [];

    // The keys a question asks, as sets by column, made once a question, which hands every object of level 0 it reads the
    // same keys: made again an object, they cost a question half a kilobyte an object. Read only; kept as long as the
    // question's keys live.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<IReadOnlyDictionary<string, string[]>, Dictionary<string, HashSet<string>>> Asked = new();

    /// <summary>Whether the object <paramref name="uid"/> names was found to read by every key of <paramref name="wanted"/>.</summary>
    private bool Lying(UInt128 uid, Dictionary<string, HashSet<string>> wanted)
    {
        lock (_copies)
        {
            if (!_lying.TryGetValue(uid, out Dictionary<string, HashSet<string>>? found))
            {
                return false;
            }

            foreach ((string column, HashSet<string> names) in wanted)
            {
                if (!found.TryGetValue(column, out HashSet<string>? held) || !held.IsSupersetOf(names))
                {
                    return false;
                }
            }

            return true;
        }
    }

    /// <summary>The objects whose copies the memo holds.</summary>
    internal int Held
    {
        get
        {
            lock (_copies)
            {
                return _copies.Count;
            }
        }
    }

    /// <summary>
    /// A lease of the object <paramref name="entry"/> names, of level 0 in <paramref name="version"/>, for a
    /// question that reads the keys <paramref name="keys"/> of its variant columns: its copy shredded in
    /// memory when the memo holds one with those keys or can make one, the object itself otherwise.
    /// </summary>
    internal async ValueTask<ObjectLease> RentAsync(
        VortexDataset dataset, DatasetSnapshot version, ObjectEntry entry, IReadOnlyDictionary<string, string[]> keys, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _disposed))
        {
            return await version.RentAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        await ForgetMovedAsync(version, cancellationToken).ConfigureAwait(false);
        Dictionary<string, HashSet<string>> wanted = Asked.GetValue(keys, static asked => Wanted(asked, null));
        if (Leased(entry.Uid, wanted, out _) is { } served)
        {
            return served;
        }

        // An object an append shredded the keys of (DatasetOptions.ShredOnAppend), as a job shreds them, is read by those
        // keys as it lies: no copy is made of it, and a later question on them reads it with no look at its path maps.
        if (Lying(entry.Uid, wanted))
        {
            Interlocked.Increment(ref _asAppended);
            return await version.RentAsync(entry, cancellationToken).ConfigureAwait(false);
        }

        ObjectLease? lying = await version.RentAsync(entry, cancellationToken).ConfigureAwait(false);
        try
        {
            if (await Indexes.VariantKeyReads.ShredsEveryAsync(lying.File, keys, cancellationToken).ConfigureAwait(false))
            {
                Interlocked.Increment(ref _asAppended);
                lock (_copies)
                {
                    if (!_disposed)
                    {
                        _lying[entry.Uid] = Wanted(keys, _lying.GetValueOrDefault(entry.Uid));
                    }
                }

                (ObjectLease handed, lying) = (lying, null);
                return handed;
            }
        }
        finally
        {
            if (lying is not null)
            {
                await lying.DisposeAsync().ConfigureAwait(false);
            }
        }

        // Not copied yet, or not with these keys: the copy is made beside the question, which reads the object as it lies,
        // its keys parsed in place, and a later question finds it once it is done. Made on the first question's path, a
        // copy kept it four to six times longer, for later questions faster by a quarter at one lane and alike at 32: the
        // services under a status of 500 or more over the logs of the short S1, 1 045 ms at first at 32 lanes, 221 parsed.
        StartCopy(dataset, version, entry, keys);
        return await version.RentAsync(entry, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the copy of the object <paramref name="entry"/> names with the keys <paramref name="keys"/> beside the question
    /// that asks them (<see cref="CopyBesideAsync"/>), unless one of it is being made already.
    /// </summary>
    private void StartCopy(VortexDataset dataset, DatasetSnapshot version, ObjectEntry entry, IReadOnlyDictionary<string, string[]> keys)
    {
        lock (_copies)
        {
            if (_disposed || _making.ContainsKey(entry.Uid))
            {
                return;
            }

            _making[entry.Uid] = Task.Run(() => CopyBesideAsync(dataset, version, entry, keys));
        }
    }

    /// <summary>
    /// The copy of the object <paramref name="entry"/> names, its keys <paramref name="keys"/> and those of the copy held
    /// shredded, made beside the questions and kept for those after: its memory, as large as the object, reserved before it
    /// is made, and refused, nothing made only to be let go. A copy that fails leaves the questions to parse the object as
    /// it lies.
    /// </summary>
    private async Task CopyBesideAsync(VortexDataset dataset, DatasetSnapshot version, ObjectEntry entry, IReadOnlyDictionary<string, string[]> keys)
    {
        CancellationToken stopping = _stopping.Token;
        try
        {
            Dictionary<string, HashSet<string>> wanted = Wanted(keys, null);
            Copy? held;
            lock (_copies)
            {
                _copies.TryGetValue(entry.Uid, out held);
            }

            if (held is not null)
            {
                if (held.Covers(wanted))
                {
                    return;
                }

                wanted = Wanted(keys, held.Keys);
            }

            QueryMemoryBudget budget = dataset.Session.Options.MemoryBudget ?? QueryMemoryBudget.Process;
            long reserved = Math.Max(entry.Bytes, 1);
            if (!budget.TryReserve(reserved))
            {
                Interlocked.Increment(ref _refused);
                return;
            }

            Copy? made;
            try
            {
                made = await CopyAsync(dataset, version, entry, wanted, budget, reserved, stopping).ConfigureAwait(false);
            }
            catch
            {
                budget.Release(reserved);
                throw;
            }

            if (made is null)
            {
                budget.Release(reserved);
                return;
            }

            // The reservation brought to the copy's bytes: past it, and refused the rest, the copy is let go.
            Interlocked.Increment(ref _shredded);
            if (made.Bytes > reserved && !budget.TryReserve(made.Bytes - reserved))
            {
                budget.Release(reserved);
                Interlocked.Increment(ref _refused);
                await made.DropAsync().ConfigureAwait(false);
                return;
            }

            if (made.Bytes < reserved)
            {
                budget.Release(reserved - made.Bytes);
            }

            made.Release = () => budget.Release(made.Bytes);
            if (held is not null)
            {
                await DropAsync(entry.Uid, held).ConfigureAwait(false);
            }

            bool kept;
            lock (_copies)
            {
                kept = !_disposed;
                if (kept)
                {
                    _copies[entry.Uid] = made;
                }
            }

            if (!kept)
            {
                await made.DropAsync().ConfigureAwait(false);
            }
        }
        catch (Exception)
        {
            // A copy that fails, or that the memo's close stops, leaves the questions to parse the object as it lies.
        }
        finally
        {
            lock (_copies)
            {
                _making.Remove(entry.Uid);
            }
        }
    }

    /// <summary>Waits for the copies being made: what a question finds after is what they made.</summary>
    internal async Task WhenMadeAsync()
    {
        while (true)
        {
            Task[] making;
            lock (_copies)
            {
                making = [.. _making.Values];
            }

            if (making.Length == 0)
            {
                return;
            }

            await Task.WhenAll(making).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A lease of the copy of the object <paramref name="uid"/> names when the memo holds one with the keys
    /// <paramref name="wanted"/>, taken under the lock of the copies, so that no drop closes it under the question; null
    /// otherwise, and the copy held without them in <paramref name="held"/>.
    /// </summary>
    private ObjectLease? Leased(UInt128 uid, Dictionary<string, HashSet<string>> wanted, out Copy? held)
    {
        lock (_copies)
        {
            if (!_copies.TryGetValue(uid, out held) || !held.Covers(wanted))
            {
                return null;
            }

            Interlocked.Increment(ref _served);
            return held.Lease();
        }
    }

    /// <summary>
    /// Drops the copies of objects no longer in level 0 of <paramref name="version"/>, once per version the
    /// node reads, and only when it holds copies; a question that finds another forgetting leaves it to that one.
    /// </summary>
    internal async ValueTask ForgetMovedAsync(DatasetSnapshot version, CancellationToken cancellationToken)
    {
        if (!Remembers() || Interlocked.Read(ref _checked) >= (long)version.Version || Volatile.Read(ref _disposed))
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _forgetting, 1, 0) != 0)
        {
            return;
        }

        try
        {
            if (!Volatile.Read(ref _disposed))
            {
                await ForgetAsync(version, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            Volatile.Write(ref _forgetting, 0);
        }
    }

    // The latest version whose level 0 the memo compared its copies with.
    private long _checked = -1;

    /// <summary>Whether the memo holds a copy, or remembers an object found to read by its keys as it lies.</summary>
    private bool Remembers()
    {
        lock (_copies)
        {
            return _copies.Count > 0 || _lying.Count > 0;
        }
    }

    /// <summary>Drops the copies of objects no longer in level 0 of <paramref name="version"/>, and forgets those it read as they lie.</summary>
    private async ValueTask ForgetAsync(DatasetSnapshot version, CancellationToken cancellationToken)
    {
        if (!Remembers() || Interlocked.Read(ref _checked) >= (long)version.Version)
        {
            return;
        }

        Interlocked.Exchange(ref _checked, (long)version.Version);

        HashSet<UInt128> levelZero = [];
        await foreach (TreeEntry leaf in version.Levels[0].EnumerateAsync(version.Pages, cancellationToken).ConfigureAwait(false))
        {
            levelZero.Add(ObjectEntry.FromBytes(leaf.Value.Span).Uid);
        }

        List<(UInt128, Copy)> gone = [];
        lock (_copies)
        {
            foreach (UInt128 uid in _lying.Keys)
            {
                if (!levelZero.Contains(uid))
                {
                    _lying.Remove(uid);
                }
            }

            foreach ((UInt128 uid, Copy copy) in _copies)
            {
                if (!levelZero.Contains(uid))
                {
                    gone.Add((uid, copy));
                }
            }
        }

        foreach ((UInt128 uid, Copy copy) in gone)
        {
            await DropAsync(uid, copy).ConfigureAwait(false);
        }
    }

    private async ValueTask DropAsync(UInt128 uid, Copy copy)
    {
        lock (_copies)
        {
            _copies.Remove(uid);
        }

        await copy.DropAsync().ConfigureAwait(false);
    }

    /// <summary>The keys asked, by column, with those a copy already holds.</summary>
    private static Dictionary<string, HashSet<string>> Wanted(IReadOnlyDictionary<string, string[]> keys, Dictionary<string, HashSet<string>>? held)
    {
        Dictionary<string, HashSet<string>> wanted = new(StringComparer.Ordinal);
        foreach ((string column, string[] names) in keys)
        {
            wanted[column] = new HashSet<string>(names, StringComparer.Ordinal);
        }

        foreach ((string column, HashSet<string> names) in held ?? [])
        {
            if (!wanted.TryGetValue(column, out HashSet<string>? into))
            {
                wanted[column] = into = new HashSet<string>(StringComparer.Ordinal);
            }

            into.UnionWith(names);
        }

        return wanted;
    }

    /// <summary>
    /// The object <paramref name="entry"/> names written again in memory (<see cref="ShredAsync"/>), its keys counted, then
    /// its rows written, two passes over its rows: over its bytes read once into memory the budget holds as long as the copy
    /// is made, as large as the object; through its open, which reads it twice, when the budget refuses them. Three objects
    /// of level 0 of the logs of S1, of 9 to 12 MB, read 66 MB for a question on one of their keys, twice their bytes.
    /// </summary>
    private static async ValueTask<Copy?> CopyAsync(
        VortexDataset dataset, DatasetSnapshot version, ObjectEntry entry, Dictionary<string, HashSet<string>> wanted, QueryMemoryBudget budget,
        long reserved, CancellationToken cancellationToken)
    {
        if (entry.Bytes <= 0 || entry.Bytes > Array.MaxLength || !budget.TryReserve(entry.Bytes))
        {
            ObjectLease original = await version.RentAsync(entry, cancellationToken).ConfigureAwait(false);
            await using (original.ConfigureAwait(false))
            {
                return await ShredAsync(original.File, wanted, dataset.Session, reserved, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            byte[] bytes = new byte[entry.Bytes];
            for (int at = 0; at < bytes.Length;)
            {
                ObjectRead read = await dataset.Store.ReadAsync(entry.Key, at, bytes.AsMemory(at), null, cancellationToken).ConfigureAwait(false);
                if (read.BytesRead <= 0)
                {
                    throw new VortexFormatException($"The object '{entry.Key}' holds {at} bytes where its entry names {entry.Bytes}.");
                }

                at += read.BytesRead;
            }

            VortexFile lying = await VortexFile.OpenAsync(new MemorySegmentSource(bytes), new VortexOpenOptions(), cancellationToken).ConfigureAwait(false);
            await using (lying.ConfigureAwait(false))
            {
                lying.Session = dataset.Session;
                return await ShredAsync(lying, wanted, dataset.Session, reserved, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            budget.Release(entry.Bytes);
        }
    }

    /// <summary>
    /// <paramref name="file"/> written again in memory, the keys <paramref name="wanted"/> shredded at the class
    /// most of their values hold, each with its path map, in a buffer of <paramref name="reserved"/> bytes to start with;
    /// null when no column asked is a variant one whose rows hold a key asked.
    /// </summary>
    private static async ValueTask<Copy?> ShredAsync(
        VortexFile file, Dictionary<string, HashSet<string>> wanted, VortexSession session, long reserved, CancellationToken cancellationToken)
    {
        // Each variant column's keys counted over its rows.
        DType schema = file.DType;
        Dictionary<int, PathCensus> censuses = [];
        foreach (string column in wanted.Keys)
        {
            int field = schema.IndexOfField(column);
            if (field >= 0 && schema.GetField(field).Kind == DTypeKind.Variant)
            {
                censuses[field] = new PathCensus();
            }
        }

        if (censuses.Count == 0)
        {
            return null;
        }

        await foreach (RecordBatch batch in file.ScanBuilder().WithCompaction(false).WithEncodings(false, false).ExecuteAsync()
            .WithCancellation(cancellationToken).ConfigureAwait(false))
        {
            using (batch)
            {
                foreach ((int field, PathCensus census) in censuses)
                {
                    census.Add(batch, field);
                }
            }
        }

        Dictionary<string, VariantShredding> plans = new(StringComparer.Ordinal);
        Dictionary<string, HashSet<string>> shredded = new(StringComparer.Ordinal);
        foreach ((int field, PathCensus census) in censuses)
        {
            string column = schema.GetFieldName(field);
            List<(string, VariantShredding)> fields = [];
            foreach (string key in wanted[column])
            {
                if (!census.Root.Keys.TryGetValue(key, out PathCensus.PathCount? count))
                {
                    continue;
                }

                VariantClass best = VariantClass.Integer;
                foreach (VariantClass @class in (ReadOnlySpan<VariantClass>)[VariantClass.Double, VariantClass.String, VariantClass.Boolean])
                {
                    best = count.Of(@class) > count.Of(best) ? @class : best;
                }

                fields.Add((key, VariantShredding.Primitive(best)));
            }

            if (fields.Count > 0)
            {
                plans[column] = VariantShredding.Object([.. fields]);
                shredded[column] = new HashSet<string>(wanted[column], StringComparer.Ordinal);
            }
        }

        if (plans.Count == 0)
        {
            return null;
        }

        // Written into the room reserved for it, the object's bytes, and opened where it lies: a stream that grew by
        // doubling, then copied out, held two or three times the copy for a moment.
        using MemoryStream stream = new MemoryStream((int)Math.Min(reserved, Array.MaxLength));
        VortexFileWriter writer = VortexFileWriter.Create(new StreamSegmentSink(stream), schema, new VortexWriteOptions { ShreddedVariants = plans }, session);
        await using (writer.ConfigureAwait(false))
        {
            await foreach (RecordBatch batch in file.ScanBuilder().WithCompaction(false).WithEncodings(false, false).ExecuteAsync()
                .WithCancellation(cancellationToken).ConfigureAwait(false))
            {
                using (batch)
                {
                    await writer.WriteBatchAsync(batch, cancellationToken).ConfigureAwait(false);
                }
            }

            await writer.CompleteAsync(cancellationToken).ConfigureAwait(false);
        }

        byte[] bytes = stream.GetBuffer();
        VortexFile copy = await VortexFile.OpenAsync(new MemorySegmentSource(bytes.AsMemory(0, (int)stream.Length)), new VortexOpenOptions(), cancellationToken).ConfigureAwait(false);
        copy.Session = session;
        return new Copy(copy, bytes.Length, shredded);
    }

    /// <summary>Drops every copy: a rent under way keeps none of the one it makes, and one that comes after reads the object as it lies.</summary>
    public async ValueTask DisposeAsync()
    {
        List<(UInt128, Copy)> all;
        lock (_copies)
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _disposed, true);
            _lying.Clear();
        }

        // The copies being made stopped and awaited first: each lets its copy go, the memo being let go.
        await _stopping.CancelAsync().ConfigureAwait(false);
        await WhenMadeAsync().ConfigureAwait(false);
        _stopping.Dispose();
        lock (_copies)
        {
            all = [.. System.Linq.Enumerable.Select(_copies, c => (c.Key, c.Value))];
        }

        foreach ((UInt128 uid, Copy copy) in all)
        {
            await DropAsync(uid, copy).ConfigureAwait(false);
        }
    }

    /// <summary>One object's copy: the open file, its bytes, the keys it shreds; closed once dropped and no lease holds it.</summary>
    private sealed class Copy(VortexFile file, long bytes, Dictionary<string, HashSet<string>> keys)
    {
        private readonly Lock _sync = new Lock();
        private int _leases;
        private bool _dropped;

        internal long Bytes { get; } = bytes;

        internal Dictionary<string, HashSet<string>> Keys { get; } = keys;

        /// <summary>What gives the copy's bytes back to the budget once it is closed; null when the budget never held them.</summary>
        internal Action? Release { get; set; }

        internal bool Covers(Dictionary<string, HashSet<string>> wanted)
        {
            foreach ((string column, HashSet<string> names) in wanted)
            {
                if (!Keys.TryGetValue(column, out HashSet<string>? held) || !held.IsSupersetOf(names))
                {
                    return false;
                }
            }

            return true;
        }

        internal ObjectLease Lease(bool dropWithLastLease = false)
        {
            lock (_sync)
            {
                _leases++;
                _dropped |= dropWithLastLease;
            }

            return new ObjectLease(file, cached: true, ReturnAsync) { IsCopy = true };
        }

        internal ValueTask DropAsync()
        {
            lock (_sync)
            {
                _dropped = true;
                if (_leases > 0)
                {
                    return ValueTask.CompletedTask;
                }
            }

            return CloseAsync();
        }

        private ValueTask ReturnAsync()
        {
            lock (_sync)
            {
                if (--_leases > 0 || !_dropped)
                {
                    return ValueTask.CompletedTask;
                }
            }

            return CloseAsync();
        }

        private async ValueTask CloseAsync()
        {
            Release?.Invoke();
            Release = null;
            await file.DisposeAsync().ConfigureAwait(false);
        }
    }
}
