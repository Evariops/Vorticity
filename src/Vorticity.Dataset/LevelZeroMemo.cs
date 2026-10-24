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
/// reads the object whole and writes it again in memory, the keys asked shredded at the class most of their
/// values hold, with their path maps; every later question on those keys reads that copy through them
/// alone, as it reads an object a job shredded. An object whose append shredded every key asked is read as
/// it lies, by those keys, with no copy.
/// </summary>
/// <remarks>
/// <para>
/// The copy holds the object's rows in their order, those deleted included, so the entry's deletion vector
/// applies to it as to the object. Its memory, as large as the object, is reserved from the session's query
/// memory budget before it is made; when the budget refuses it, the question parses the object as it lies and
/// no copy is made. A copy is dropped once its object has left level 0, which the next read of the memo finds.
/// </para>
/// <para>
/// A question waits only on one that shreds the same object: each object has a gate of its own, held while
/// its copy is made, and a question a copy held serves waits on none.
/// </para>
/// <para>
/// A question on keys a copy lacks shreds the object again with the keys of both, so a key is parsed once
/// per object and per node.
/// </para>
/// </remarks>
internal sealed class LevelZeroMemo : IAsyncDisposable
{
    // The gates of the objects questions shred, each held while its object's copy is made and kept while a
    // question waits on it; whether a question forgets the objects gone from level 0, which the others leave it to.
    private readonly Dictionary<UInt128, Gate> _gates = [];
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
        Dictionary<string, HashSet<string>> wanted = Wanted(keys, null);
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

        // A question waits only on one that shreds the same object, and finds its copy once that one is done.
        Gate gate = Enter(entry.Uid);
        try
        {
            await gate.Lock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (Volatile.Read(ref _disposed))
                {
                    return await version.RentAsync(entry, cancellationToken).ConfigureAwait(false);
                }

                if (Leased(entry.Uid, wanted, out Copy? held) is { } shreddedMeanwhile)
                {
                    return shreddedMeanwhile;
                }

                if (held is not null)
                {
                    wanted = Wanted(keys, held.Keys);
                }

                // The copy's memory, as large as the object, reserved before it is made: refused, the question parses
                // the object as it lies, and nothing is made only to be let go.
                QueryMemoryBudget budget = dataset.Session.Options.MemoryBudget ?? QueryMemoryBudget.Process;
                long reserved = Math.Max(entry.Bytes, 1);
                if (!budget.TryReserve(reserved))
                {
                    Interlocked.Increment(ref _refused);
                    return await version.RentAsync(entry, cancellationToken).ConfigureAwait(false);
                }

                Copy? made;
                try
                {
                    ObjectLease original = await version.RentAsync(entry, cancellationToken).ConfigureAwait(false);
                    await using (original.ConfigureAwait(false))
                    {
                        made = await ShredAsync(original.File, wanted, dataset.Session, reserved, cancellationToken).ConfigureAwait(false);
                    }
                }
                catch
                {
                    budget.Release(reserved);
                    throw;
                }

                if (made is null)
                {
                    budget.Release(reserved);
                    return await version.RentAsync(entry, cancellationToken).ConfigureAwait(false);
                }

                // The reservation brought to the copy's bytes: past it, and refused the rest, the copy serves this
                // question alone and goes with its lease.
                Interlocked.Increment(ref _shredded);
                if (made.Bytes > reserved && !budget.TryReserve(made.Bytes - reserved))
                {
                    budget.Release(reserved);
                    Interlocked.Increment(ref _refused);
                    return made.Lease(dropWithLastLease: true);
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

                lock (_copies)
                {
                    if (!_disposed)
                    {
                        _copies[entry.Uid] = made;
                        return made.Lease();
                    }
                }

                // Let go meanwhile: the copy serves this question alone.
                return made.Lease(dropWithLastLease: true);
            }
            finally
            {
                gate.Lock.Release();
            }
        }
        finally
        {
            Leave(entry.Uid, gate);
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

    /// <summary>The gate of the object <paramref name="uid"/> names, one more question waiting on it.</summary>
    private Gate Enter(UInt128 uid)
    {
        lock (_gates)
        {
            if (!_gates.TryGetValue(uid, out Gate? gate))
            {
                _gates[uid] = gate = new Gate();
            }

            gate.Users++;
            return gate;
        }
    }

    /// <summary>One question fewer on <paramref name="gate"/>; the last one lets the gate go.</summary>
    private void Leave(UInt128 uid, Gate gate)
    {
        lock (_gates)
        {
            if (--gate.Users > 0)
            {
                return;
            }

            _gates.Remove(uid);
        }

        gate.Lock.Dispose();
    }

    /// <summary>The gate of one object, and the questions that hold or wait on it.</summary>
    private sealed class Gate
    {
        internal SemaphoreSlim Lock { get; } = new SemaphoreSlim(1, 1);

        internal int Users { get; set; }
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

            return new ObjectLease(file, cached: true, ReturnAsync);
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
