// An encrypted dataset, as a caller sees it: the answers of a plain one, and nothing in the store
// that says what it holds.
//
// WHAT IS HELD: an encrypted dataset answers its scans, filters and aggregates as a plain one of the
// same rows, through a handle that wrote it and a fresh one; every commit and data object in the
// store is sealed, and no value of the rows appears in its bytes; a session without the key is told
// the dataset is encrypted; a plain dataset is refused to a handle that asks for an encrypted one; an
// object moved under another key, a commit moved under another version or taken from another
// dataset, and a plain object put in a sealed one's place are each refused; a rekey changes the key
// the next objects are sealed under and leaves the old ones readable; verification passes and finds a
// descriptor altered; compaction, vacuum and imports keep the dataset sealed whole; a torn sealed
// commit is named, and removed without the key; an index fragment lies sealed in its commit and a
// session that refuses plaintext uses it; and opening and scanning costs the requests a plain dataset
// costs.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Tests.Api;
using Xunit;

namespace Vorticity.Tests.Sealing;

public sealed class EncryptedDatasetTests
{
    private const int Rows = 4_000;

    [Fact]
    public async Task AnEncryptedDatasetAnswersAsAPlainOne()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        await using VortexSession session = Session(keyring);
        await using MemoryObjectStore plainStore = new MemoryObjectStore();
        await using MemoryObjectStore sealedStore = new MemoryObjectStore();
        await using VortexDataset plain = await VortexDataset.CreateAsync(plainStore, Reading.Schema, new DatasetOptions(), ct);
        await using VortexDataset encrypted = await VortexDataset.CreateAsync(sealedStore, Reading.Schema, Encrypted(session), ct);
        for (int i = 0; i < 4; i++)
        {
            await AppendAsync(plain, i, ct);
            await AppendAsync(encrypted, i, ct);
        }

        Assert.True(encrypted.IsEncrypted);
        Assert.False(plain.IsEncrypted);
        await AssertSameAnswersAsync(plain, encrypted, ct);

        // A fresh handle, in another session holding the same keys, reads it the same.
        await using VortexSession other = Session(keyring);
        await using VortexDataset reopened = await VortexDataset.OpenAsync(sealedStore, new DatasetOptions { Session = other }, ct);
        Assert.True(reopened.IsEncrypted);
        await AssertSameAnswersAsync(plain, reopened, ct);
    }

    [Fact]
    public async Task TheStoreHoldsSealedObjectsAndNoPlaintext()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        await using VortexSession session = Session(keyring);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, Encrypted(session), ct))
        {
            for (int i = 0; i < 3; i++)
            {
                await AppendAsync(dataset, i, ct);
            }
        }

        int objects = 0;
        await foreach (string key in store.ListAsync(string.Empty, null, ct))
        {
            byte[] bytes = await AllAsync(store, key, ct);
            Assert.True(bytes.AsSpan().StartsWith("VXSEALED"u8), $"'{key}' is not sealed");
            AssertNoPlaintext(bytes, key);
            objects++;
        }

        Assert.True(objects >= 6, $"the store holds {objects} objects, too few for three appends");

        // A sealed file holds none either.
        string path = Path.Combine(Path.GetTempPath(), $"vorticity-sealed-{Guid.NewGuid():N}.vortex");
        try
        {
            await using (VortexSession sealing = VortexSession.Create(o =>
            {
                o.Keyring = keyring;
                o.EncryptFiles = true;
            }))
            {
                await using VortexFileWriter writer = sealing.CreateWriter<Reading>(path);
                await writer.WriteAsync<Reading>(SealedObjects.Rows(20_000).ToArray(), ct);
                await writer.CompleteAsync(ct);
            }

            AssertNoPlaintext(await System.IO.File.ReadAllBytesAsync(path, ct), path);
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    [Fact]
    public async Task WithoutItsKeyAnEncryptedDatasetDoesNotOpen()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        await using MemoryObjectStore store = await EncryptedStoreAsync(keyring, 2, ct);

        VortexEncryptionException keyless = await Assert.ThrowsAsync<VortexEncryptionException>(
            async () => await VortexDataset.OpenAsync(store, new DatasetOptions(), ct));
        Assert.Equal(VortexEncryptionError.NoKey, keyless.Error);
        Assert.Contains("encrypted", keyless.Message, StringComparison.Ordinal);

        using VortexKeyring other = VortexKeyring.FromKeys(new VortexKey("another-key", SealedObjects.Key(5)));
        await using VortexSession wrong = Session(other);
        VortexEncryptionException missing = await Assert.ThrowsAsync<VortexEncryptionException>(
            async () => await VortexDataset.OpenAsync(store, new DatasetOptions { Session = wrong }, ct));
        Assert.Equal(VortexEncryptionError.NoKey, missing.Error);
    }

    [Fact]
    public async Task APlainDatasetIsRefusedWhereAnEncryptedOneIsExpected()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using (VortexDataset plain = await VortexDataset.CreateAsync(store, Reading.Schema, new DatasetOptions(), ct))
        {
            await AppendAsync(plain, 0, ct);
        }

        await using VortexSession session = Session(keyring);
        await using (VortexDataset lenient = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, ct))
        {
            Assert.False(lenient.IsEncrypted);
            Assert.Equal(Rows, lenient.RowCount);
        }

        VortexEncryptionException refused = await Assert.ThrowsAsync<VortexEncryptionException>(
            async () => await VortexDataset.OpenAsync(store, Encrypted(session), ct));
        Assert.Equal(VortexEncryptionError.Refused, refused.Error);

        await using VortexSession strict = VortexSession.Create(o =>
        {
            o.Keyring = keyring;
            o.RefusePlaintext = true;
        });
        Assert.Equal(
            VortexEncryptionError.Refused,
            (await Assert.ThrowsAsync<VortexEncryptionException>(
                async () => await VortexDataset.OpenAsync(store, new DatasetOptions { Session = strict }, ct))).Error);
    }

    [Fact]
    public async Task AnObjectOutOfItsPlaceIsRefused()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        await using VortexSession session = Session(keyring);

        // A data object copied over another's key: its envelope binds another uid.
        await using (MemoryObjectStore store = await EncryptedStoreAsync(keyring, 2, ct))
        {
            List<string> data = await KeysAsync(store, "data/", ct);
            await ReplaceAsync(store, data[1], await AllAsync(store, data[0], ct), ct);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, ct);
            // A count of every row is answered from the entries: the sum reads the objects.
            VortexEncryptionException swapped = await Assert.ThrowsAsync<VortexEncryptionException>(
                async () => await dataset.Scan<Reading>().SumAsync(r => r.Day, ct));
            Assert.Equal(VortexEncryptionError.Unauthenticated, swapped.Error);
        }

        // An older commit put back under the latest version's key: its envelope binds its own version.
        await using (MemoryObjectStore store = await EncryptedStoreAsync(keyring, 2, ct))
        {
            List<string> commits = await KeysAsync(store, "commit/", ct);
            await ReplaceAsync(store, commits[0], await AllAsync(store, commits[1], ct), ct);
            VortexEncryptionException moved = await Assert.ThrowsAsync<VortexEncryptionException>(
                async () => await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, ct));
            Assert.Equal(VortexEncryptionError.Unauthenticated, moved.Error);
        }

        // Another encrypted dataset's commit, under the same keys: its envelope binds another dataset.
        await using (MemoryObjectStore store = await EncryptedStoreAsync(keyring, 2, ct))
        await using (MemoryObjectStore elsewhere = await EncryptedStoreAsync(keyring, 2, ct))
        {
            string latest = (await KeysAsync(store, "commit/", ct))[0];
            await ReplaceAsync(store, latest, await AllAsync(elsewhere, latest, ct), ct);
            await using VortexDataset first = await VortexDataset.OpenAsync(elsewhere, new DatasetOptions { Session = session }, ct);
            Assert.True(first.IsEncrypted);

            // The handle trusts the dataset its latest commit names: the older commits then disagree.
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, ct);
            await Assert.ThrowsAnyAsync<VortexException>(async () => await dataset.Scan<Reading>().SumAsync(r => r.Day, ct));
        }

        // A plain file put where a sealed data object is expected.
        await using (MemoryObjectStore store = await EncryptedStoreAsync(keyring, 1, ct))
        {
            string data = (await KeysAsync(store, "data/", ct))[0];
            await ReplaceAsync(store, data, await PlainFileAsync(ct), ct);
            await using VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, ct);
            VortexEncryptionException plainAmongSealed = await Assert.ThrowsAsync<VortexEncryptionException>(
                async () => await dataset.Scan<Reading>().SumAsync(r => r.Day, ct));
            Assert.Equal(VortexEncryptionError.Refused, plainAmongSealed.Error);
        }
    }

    [Fact]
    public async Task ARekeySealsTheNextObjectsUnderANewKeyAndKeepsTheOldOnesReadable()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        await using VortexSession session = Session(keyring);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, Encrypted(session), ct);
        await AppendAsync(dataset, 0, ct);
        byte[] before = await WrappedKeyOfAsync(store, (await KeysAsync(store, "commit/", ct))[0], ct);

        ulong rekeyed = await dataset.RekeyAsync(ct);
        Assert.Equal(dataset.Version, rekeyed);
        await AppendAsync(dataset, 1, ct);

        List<string> commits = await KeysAsync(store, "commit/", ct);
        byte[] after = await WrappedKeyOfAsync(store, commits[0], ct);
        Assert.NotEqual(before, after);
        Assert.Equal(2 * Rows, dataset.RowCount);

        await using VortexSession fresh = Session(keyring);
        await using VortexDataset reopened = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = fresh }, ct);
        Assert.Equal(2L * Rows, await reopened.Scan<Reading>().CountAsync(ct));
        Assert.Empty((await reopened.VerifyAsync(cancellationToken: ct)).Problems);
    }

    [Fact]
    public async Task VerificationPassesAndFindsATamperedEnvelope()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        await using VortexSession session = Session(keyring);
        await using MemoryObjectStore store = await EncryptedStoreAsync(keyring, 3, ct);
        await using (VortexDataset dataset = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, ct))
        {
            DatasetVerification clean = await dataset.VerifyAsync(cancellationToken: ct);
            Assert.Empty(clean.Problems);
        }

        // The head's copy of a data object's descriptor, which a read by its tail never uses.
        string data = (await KeysAsync(store, "data/", ct))[0];
        byte[] bytes = await AllAsync(store, data, ct);
        bytes[20] ^= 0x01;
        await ReplaceAsync(store, data, bytes, ct);
        await using VortexDataset tampered = await VortexDataset.OpenAsync(store, new DatasetOptions { Session = session }, ct);
        Assert.Equal(3L * Rows, await tampered.Scan<Reading>().CountAsync(ct));
        DatasetVerification found = await tampered.VerifyAsync(cancellationToken: ct);
        Assert.Contains(found.Problems, problem => problem.Contains("two different descriptors", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CompactionVacuumAndImportsKeepItSealedWhole()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        await using VortexSession session = Session(keyring);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(
            store, Reading.Schema, Encrypted(session) with { RetentionWindow = TimeSpan.Zero }, ct);
        for (int i = 0; i < 9; i++)
        {
            await AppendAsync(dataset, i, ct);
        }

        CompactionResult? compacted = await dataset.CompactAsync(cancellationToken: ct);
        Assert.NotNull(compacted);
        Assert.Equal(9L * Rows, await dataset.Scan<Reading>().CountAsync(ct));
        await dataset.VacuumAsync(cancellationToken: ct);
        Assert.Equal(9L * Rows, await dataset.Scan<Reading>().CountAsync(ct));
        Assert.Empty((await dataset.VerifyAsync(cancellationToken: ct)).Problems);
        await foreach (string key in store.ListAsync(string.Empty, null, ct))
        {
            if (!key.StartsWith("leases/", StringComparison.Ordinal))
            {
                Assert.True((await AllAsync(store, key, ct)).AsSpan().StartsWith("VXSEALED"u8), $"'{key}' is not sealed");
            }
        }

        // A file sealed by a session of the same keys joins it; a plain one does not.
        await ReplaceAsync(store, "imports/sealed.vortex", await SealedFileAsync(keyring, ct), ct);
        await dataset.ImportAsync("imports/sealed.vortex", ct);
        Assert.Equal(9L * Rows + 1_000, await dataset.Scan<Reading>().CountAsync(ct));
        await ReplaceAsync(store, "imports/plain.vortex", await PlainFileAsync(ct), ct);
        Assert.Equal(
            VortexEncryptionError.Refused,
            (await Assert.ThrowsAsync<VortexEncryptionException>(async () => await dataset.ImportAsync("imports/plain.vortex", ct))).Error);

        // And a sealed file does not join a plain dataset.
        await using MemoryObjectStore plainStore = new MemoryObjectStore();
        await using VortexDataset plain = await VortexDataset.CreateAsync(plainStore, Reading.Schema, new DatasetOptions { Session = session }, ct);
        await ReplaceAsync(plainStore, "imports/sealed.vortex", await SealedFileAsync(keyring, ct), ct);
        Assert.Equal(
            VortexEncryptionError.Refused,
            (await Assert.ThrowsAsync<VortexEncryptionException>(async () => await plain.ImportAsync("imports/sealed.vortex", ct))).Error);
    }

    [Fact]
    public async Task ATornSealedCommitIsNamedAndRemovedWithoutTheKey()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        await using VortexSession session = Session(keyring);
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, Encrypted(session), ct))
        {
            await AppendAsync(dataset, 0, ct);
            Assert.Equal(2UL, dataset.Version);
        }

        // A whole sealed commit is told whole by its envelope alone.
        Assert.Null(await VortexDataset.RemoveTornCommitAsync(store, ct));

        // A writer that stopped halfway through the next commit.
        byte[] whole = await AllAsync(store, CommitKey.For(2), ct);
        await store.PutIfAbsentAsync(CommitKey.For(3), whole.AsSpan(0, whole.Length / 2).ToArray(), ct);
        TornCommitException torn = await Assert.ThrowsAsync<TornCommitException>(
            async () => await VortexDataset.OpenAsync(store, Encrypted(session), ct));
        Assert.Equal(3UL, torn.Version);

        Assert.Equal(3UL, await VortexDataset.RemoveTornCommitAsync(store, ct));
        Assert.Null(await VortexDataset.RemoveTornCommitAsync(store, ct));
        await using VortexDataset reopened = await VortexDataset.OpenAsync(store, Encrypted(session), ct);
        Assert.Equal(2UL, reopened.Version);
        await AppendAsync(reopened, 1, ct);
        Assert.Equal(3UL, reopened.Version);
        Assert.Equal(2L * Rows, await reopened.Scan<Reading>().CountAsync(ct));
    }

    [Fact]
    public async Task AnIndexFragmentGoesSealedInItsCommitAndASessionThatRefusesPlaintextUsesIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        await using VortexSession strict = VortexSession.Create(o =>
        {
            o.Keyring = keyring;
            o.RefusePlaintext = true;
        });
        await using MemoryObjectStore store = new MemoryObjectStore();
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, Encrypted(strict), ct);
        await AppendAsync(dataset, 0, ct);
        IndexingResult indexed = await DatasetIndexer.IndexAsync(
            dataset, await SingleObjectAsync(dataset, ct), WritePolicy.None.For("City", IndexSpec.Postings),
            options: new VortexWriteOptions { IndexBudgetPerMille = 1_000_000 }, cancellationToken: ct);
        Assert.Equal(OperationOutcome.Applied, indexed.Outcome);

        // The fragment lies in a sealed commit, so the store holds no value of it; read back plain from
        // there, it is the dataset's own, and the session takes it.
        await foreach (string key in store.ListAsync(string.Empty, null, ct))
        {
            Assert.True((await AllAsync(store, key, ct)).AsSpan().IndexOf("Nantes"u8) < 0, $"'{key}' holds a city's name in plain");
        }

        await using VortexDataset reopened = await VortexDataset.OpenAsync(store, Encrypted(strict), ct);
        PositionedObject target = await SingleObjectAsync(reopened, ct);
        ObjectLease lease = await reopened.RentAsync(target.Entry, ct);
        await using (lease)
        {
            Assert.Contains(await lease.File.ReadIndexesAsync(ct), index => index.Column == "City");
            Assert.Equal([null], lease.File.IndexFragmentRefusals);
        }

        Assert.Equal(
            SealedObjects.Rows(Rows).FindAll(r => r.City == "Nantes").Count,
            await reopened.Scan<Reading>().Where(r => r.City == "Nantes").CountAsync(ct));
    }

    [Fact]
    public async Task OpeningAndScanningCostTheRequestsOfAPlainDataset()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        await using VortexSession session = Session(keyring);
        await using MemoryObjectStore plainStore = new MemoryObjectStore();
        await using MemoryObjectStore sealedStore = new MemoryObjectStore();
        await using (VortexDataset plain = await VortexDataset.CreateAsync(plainStore, Reading.Schema, new DatasetOptions(), ct))
        await using (VortexDataset encrypted = await VortexDataset.CreateAsync(sealedStore, Reading.Schema, Encrypted(session), ct))
        {
            for (int i = 0; i < 3; i++)
            {
                await AppendAsync(plain, i, ct);
                await AppendAsync(encrypted, i, ct);
            }
        }

        (long plainOpen, long plainScan) = await RequestsAsync(plainStore, new DatasetOptions(), ct);
        await using VortexSession reading = Session(keyring);
        (long sealedOpen, long sealedScan) = await RequestsAsync(sealedStore, new DatasetOptions { Session = reading }, ct);
        Assert.Equal(plainOpen, sealedOpen);
        Assert.True(sealedScan <= plainScan, $"a scan cost {sealedScan} requests sealed and {plainScan} plain");
    }

    private static async Task<(long Open, long Scan)> RequestsAsync(IObjectStore store, DatasetOptions options, CancellationToken ct)
    {
        await using CountingObjectStore counting = new CountingObjectStore(store, ownsInner: false);
        await using VortexDataset dataset = await VortexDataset.OpenAsync(counting, options, ct);
        long open = counting.Requests;
        _ = await dataset.Scan<Reading>().Where(r => r.Day >= 100).CountAsync(ct);
        return (open, counting.Requests - open);
    }

    private static VortexSession Session(VortexKeyring keyring) => VortexSession.Create(o => o.Keyring = keyring);

    private static DatasetOptions Encrypted(VortexSession session) => new DatasetOptions { Session = session, Encrypted = true };

    private static async Task<PositionedObject> SingleObjectAsync(VortexDataset dataset, CancellationToken ct)
    {
        List<PositionedObject> objects = [];
        await foreach (PositionedObject held in dataset.ScanBuilder().ObjectsAsync().WithCancellation(ct))
        {
            objects.Add(held);
        }

        return Assert.Single(objects);
    }

    private static async Task<MemoryObjectStore> EncryptedStoreAsync(VortexKeyring keyring, int appends, CancellationToken ct)
    {
        MemoryObjectStore store = new MemoryObjectStore();
        await using VortexSession session = Session(keyring);
        await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Reading.Schema, Encrypted(session), ct);
        for (int i = 0; i < appends; i++)
        {
            await AppendAsync(dataset, i, ct);
        }

        return store;
    }

    private static async Task AppendAsync(VortexDataset dataset, int part, CancellationToken ct)
    {
        List<Reading> rows = SealedObjects.Rows(Rows * (part + 1)).GetRange(Rows * part, Rows);
        ObjectDraft draft = dataset.StartObject();
        await draft.Writer.WriteAsync<Reading>(rows.ToArray(), ct);
        await dataset.AppendAsync(draft, ct);
    }

    private static async Task AssertSameAnswersAsync(VortexDataset plain, VortexDataset encrypted, CancellationToken ct)
    {
        Assert.Equal(plain.RowCount, encrypted.RowCount);
        Assert.Equal(await RowsAsync(plain, ct), await RowsAsync(encrypted, ct));
        Assert.Equal(
            await plain.Scan<Reading>().Where(r => r.Day >= 1_500 && r.Celsius > 20.0).CountAsync(ct),
            await encrypted.Scan<Reading>().Where(r => r.Day >= 1_500 && r.Celsius > 20.0).CountAsync(ct));
        Assert.Equal(
            await plain.Scan<Reading>().AverageAsync(r => r.Celsius, ct),
            await encrypted.Scan<Reading>().AverageAsync(r => r.Celsius, ct));
    }

    private static async Task<List<Reading>> RowsAsync(VortexDataset dataset, CancellationToken ct)
    {
        List<Reading> rows = [];
        await foreach (Reading row in dataset.Scan<Reading>().ToRecordsAsync(ct))
        {
            rows.Add(row);
        }

        rows.Sort((a, b) => a.Day.CompareTo(b.Day));
        return rows;
    }

    private static void AssertNoPlaintext(byte[] bytes, string name)
    {
        foreach (string city in new[] { "Paris", "Nantes", "Rennes", "Brest" })
        {
            Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes(city)) < 0, $"'{name}' holds '{city}' in clear");
        }

        Assert.True(bytes.AsSpan().IndexOf("City"u8) < 0, $"'{name}' holds a column name in clear");
    }

    private static async Task<List<string>> KeysAsync(IObjectStore store, string prefix, CancellationToken ct)
    {
        List<string> keys = [];
        await foreach (string key in store.ListAsync(prefix, null, ct))
        {
            keys.Add(key);
        }

        return keys;
    }

    private static async Task<byte[]> AllAsync(IObjectStore store, string key, CancellationToken ct)
    {
        ObjectHead head = (await store.HeadAsync(key, ct))!.Value;
        using ObjectRange range = await store.GetRangeAsync(key, 0, (int)head.Length, ct);
        return range.Bytes.ToArray();
    }

    private static async Task ReplaceAsync(IObjectStore store, string key, byte[] bytes, CancellationToken ct)
    {
        await store.DeleteAsync([key], ct);
        Assert.Equal(PutOutcome.Created, await store.PutIfAbsentAsync(key, System.IO.Pipelines.PipeReader.Create(new System.Buffers.ReadOnlySequence<byte>(bytes)), bytes.Length, ct));
    }

    /// <summary>The wrapped data key in a sealed object's descriptor.</summary>
    private static async Task<byte[]> WrappedKeyOfAsync(IObjectStore store, string key, CancellationToken ct)
    {
        byte[] bytes = await AllAsync(store, key, ct);
        int length = BitConverter.ToInt32(bytes, 8);
        return Vorticity.Sealing.SealDescriptor.Read(bytes.AsSpan(12, length), out _).WrappedKey.Span.ToArray();
    }

    private static async Task<byte[]> PlainFileAsync(CancellationToken ct)
    {
        System.IO.Pipelines.Pipe pipe = new System.IO.Pipelines.Pipe(new System.IO.Pipelines.PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter(pipe.Writer, Reading.Schema))
        {
            await writer.WriteAsync<Reading>(SealedObjects.Rows(1_000).ToArray(), ct);
            await writer.CompleteAsync(ct);
        }

        return await SealedObjects.DrainAsync(pipe.Reader, ct);
    }

    private static async Task<byte[]> SealedFileAsync(VortexKeyring keyring, CancellationToken ct)
    {
        await using VortexSession sealing = VortexSession.Create(o =>
        {
            o.Keyring = keyring;
            o.EncryptFiles = true;
        });
        System.IO.Pipelines.Pipe pipe = new System.IO.Pipelines.Pipe(new System.IO.Pipelines.PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        await using (VortexFileWriter writer = sealing.CreateWriter(pipe.Writer, Reading.Schema))
        {
            await writer.WriteAsync<Reading>(SealedObjects.Rows(1_000).ToArray(), ct);
            await writer.CompleteAsync(ct);
        }

        return await SealedObjects.DrainAsync(pipe.Reader, ct);
    }
}
