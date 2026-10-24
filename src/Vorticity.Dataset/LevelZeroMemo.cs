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
/// An object of level 0 is stored as it was appended, never shredded, so each question on a key of it would
/// parse every row. The first question reads the object whole and writes it again in memory, the keys asked
/// shredded at the class most of their values hold, with their path maps; every later question on those
/// keys reads that copy through them alone, as it reads an object a job shredded.
/// </summary>
/// <remarks>
/// <para>
/// The copy holds the object's rows in their order, those deleted included, so the entry's deletion vector
/// applies to it as to the object. It is reserved from the session's query memory budget; one the budget
/// refuses serves the question that made it and is let go, and the next question parses again. A copy is
/// dropped once its object has left level 0, which the next read of the memo finds.
/// </para>
/// <para>
/// A question on keys a copy lacks shreds the object again with the keys of both, so a key is parsed once
/// per object and per node.
/// </para>
/// </remarks>
internal sealed class LevelZeroMemo : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new SemaphoreSlim(1, 1);
    private readonly Dictionary<UInt128, Copy> _copies = [];
    private long _shredded;
    private long _served;
    private long _refused;

    // Set under the gate: a memo let go keeps no copy, and serves the objects as they lie.
    private bool _disposed;

    /// <summary>How many times an object was read whole and shredded in memory.</summary>
    internal long Shredded => Interlocked.Read(ref _shredded);

    /// <summary>How many leases a copy held served, with no parse.</summary>
    internal long Served => Interlocked.Read(ref _served);

    /// <summary>How many copies the memory budget refused to keep.</summary>
    internal long Refused => Interlocked.Read(ref _refused);

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

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return await version.RentAsync(entry, cancellationToken).ConfigureAwait(false);
            }

            await ForgetAsync(version, cancellationToken).ConfigureAwait(false);
            Dictionary<string, HashSet<string>> wanted = Wanted(keys, null);
            if (_copies.TryGetValue(entry.Uid, out Copy? held))
            {
                if (held.Covers(wanted))
                {
                    Interlocked.Increment(ref _served);
                    return held.Lease();
                }

                wanted = Wanted(keys, held.Keys);
            }

            ObjectLease original = await version.RentAsync(entry, cancellationToken).ConfigureAwait(false);
            Copy? made;
            await using (original.ConfigureAwait(false))
            {
                made = await ShredAsync(original.File, wanted, dataset.Session, cancellationToken).ConfigureAwait(false);
            }

            if (made is null)
            {
                return await version.RentAsync(entry, cancellationToken).ConfigureAwait(false);
            }

            Interlocked.Increment(ref _shredded);
            QueryMemoryBudget budget = dataset.Session.Options.MemoryBudget ?? QueryMemoryBudget.Process;
            if (!budget.TryReserve(made.Bytes))
            {
                // Refused: the copy serves this question alone, and goes with its lease.
                Interlocked.Increment(ref _refused);
                made.Release = null;
                return made.Lease(dropWithLastLease: true);
            }

            made.Release = () => budget.Release(made.Bytes);
            if (held is not null)
            {
                await DropAsync(entry.Uid, held).ConfigureAwait(false);
            }

            lock (_copies)
            {
                _copies[entry.Uid] = made;
            }

            return made.Lease();
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Drops the copies of objects no longer in level 0 of <paramref name="version"/>, once per version the
    /// node reads, and only when it holds copies.
    /// </summary>
    internal async ValueTask ForgetMovedAsync(DatasetSnapshot version, CancellationToken cancellationToken)
    {
        if (Held == 0 || Interlocked.Read(ref _checked) >= (long)version.Version || Volatile.Read(ref _disposed))
        {
            return;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_disposed)
            {
                await ForgetAsync(version, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    // The latest version whose level 0 the memo compared its copies with.
    private long _checked = -1;

    /// <summary>Drops the copies of objects no longer in level 0 of <paramref name="version"/>.</summary>
    private async ValueTask ForgetAsync(DatasetSnapshot version, CancellationToken cancellationToken)
    {
        if (_copies.Count == 0 || Interlocked.Read(ref _checked) >= (long)version.Version)
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
    /// most of their values hold, each with its path map; null when no column asked is a variant one whose rows
    /// hold a key asked.
    /// </summary>
    private static async ValueTask<Copy?> ShredAsync(
        VortexFile file, Dictionary<string, HashSet<string>> wanted, VortexSession session, CancellationToken cancellationToken)
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

        using MemoryStream stream = new MemoryStream();
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

        byte[] bytes = stream.ToArray();
        VortexFile copy = await VortexFile.OpenAsync(new MemorySegmentSource(bytes), new VortexOpenOptions(), cancellationToken).ConfigureAwait(false);
        copy.Session = session;
        return new Copy(copy, bytes.Length, shredded);
    }

    /// <summary>Drops every copy, once a rent under way has ended: one that comes after keeps none, and reads the object as it lies.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _disposed))
        {
            return;
        }

        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            Volatile.Write(ref _disposed, true);
            List<(UInt128, Copy)> all;
            lock (_copies)
            {
                all = [.. System.Linq.Enumerable.Select(_copies, c => (c.Key, c.Value))];
            }

            foreach ((UInt128 uid, Copy copy) in all)
            {
                await DropAsync(uid, copy).ConfigureAwait(false);
            }
        }
        finally
        {
            _gate.Release();
        }

        _gate.Dispose();
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
