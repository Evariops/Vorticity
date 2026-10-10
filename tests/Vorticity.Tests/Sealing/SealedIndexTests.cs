// Indexes built after the write, over sealed files: appended as an epoch, or carried by a fragment.
//
// WHAT IS HELD: a session that holds the key indexes a sealed file in place, the runs and the new tail
// sealed as an epoch of its own, the file answering as before and its index used, no plaintext left in
// it or beside it; the default session is told the file is sealed. A fragment of a sealed file, or one
// built by a session that seals, is sealed, holds none of the values, and is used by a session that
// holds its key; a session without a keyring leaves it out with its reason, as a session that refuses
// plaintext leaves out a plain one, and neither fails the open.
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Sealing;
using Vorticity.Tests.Api;
using Xunit;

namespace Vorticity.Tests.Sealing;

public sealed class SealedIndexTests : IDisposable
{
    private static readonly IndexPolicy Policy = IndexPolicy.None.Postings("City", true);

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vorticity-sealed-index-tests", Guid.NewGuid().ToString("N"));

    public SealedIndexTests() => Directory.CreateDirectory(_directory);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_directory, recursive: true);
        }
        catch (IOException)
        {
            // A test failure must not be masked by a cleanup failure.
        }
    }

    [Fact]
    public async Task ASealedFileIsIndexedInPlaceAsAnEpoch()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        List<Reading> rows = SealedObjects.Rows(40_000);
        string path = Path.Combine(_directory, "sealed.vortex");
        await using VortexSession session = SealingSession(keyring);
        await WriteAsync(session, path, rows, ct);
        long expected = rows.Count(r => r.City == "Nantes");

        VortexEncryptionException keyless = await Assert.ThrowsAsync<VortexEncryptionException>(
            async () => await VortexFileIndexer.AppendIndexesAsync(path, Policy, ct));
        Assert.Equal(VortexEncryptionError.NoKey, keyless.Error);

        IReadOnlyList<IndexWriteReport> reports = await session.AppendIndexesAsync(path, Policy, ct);
        Assert.Contains(reports, r => r.Column == "City" && r.Outcome == IndexOutcome.Built);

        await using (VortexFile file = await session.OpenAsync(path, cancellationToken: ct))
        {
            Assert.Equal(2, file.SealedLayout!.Epochs.Length);
            Assert.Contains(await file.ReadIndexesAsync(ct), index => index.Column == "City");
            Assert.Equal(expected, await file.Scan<Reading>().Where(r => r.City == "Nantes").CountAsync(ct));
            Assert.Equal(rows, await SealedObjects.RowsOfAsync(file, ct));
        }

        byte[] bytes = await System.IO.File.ReadAllBytesAsync(path, ct);
        Assert.True(bytes.AsSpan().IndexOf("Nantes"u8) < 0, "A city's name lies in plain in the indexed file.");
        Assert.Equal([Path.GetFileName(path)], Directory.EnumerateFiles(_directory).Select(Path.GetFileName));
    }

    [Fact]
    public async Task AFragmentOfASealedFileIsSealedAndOpensWithItsKey()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        List<Reading> rows = SealedObjects.Rows(40_000);
        string path = Path.Combine(_directory, "sealed.vortex");
        await using VortexSession session = SealingSession(keyring);
        await WriteAsync(session, path, rows, ct);

        IndexFragment fragment = await session.BuildFragmentAsync(path, Policy, cancellationToken: ct);
        Assert.True(SealedLayout.EndsSealed(fragment.Bytes.Span));
        Assert.True(fragment.Bytes.Span.IndexOf("Nantes"u8) < 0, "A city's name lies in plain in the sealed fragment.");

        // The fragment of an open file, too.
        await using (VortexFile open = await session.OpenAsync(path, cancellationToken: ct))
        {
            IndexFragment again = await VortexFileIndexer.BuildFragmentAsync(open, Policy, cancellationToken: ct);
            Assert.True(SealedLayout.EndsSealed(again.Bytes.Span));
        }

        VortexOpenOptions withFragment = new VortexOpenOptions { IndexFragments = [fragment] };
        await using VortexFile file = await session.OpenAsync(path, withFragment, ct);
        Assert.Contains(await file.ReadIndexesAsync(ct), index => index.Column == "City");
        Assert.Equal([null], file.IndexFragmentRefusals);
        Assert.Equal(rows.Count(r => r.City == "Nantes"), await file.Scan<Reading>().Where(r => r.City == "Nantes").CountAsync(ct));
    }

    [Fact]
    public async Task AFragmentASessionCannotTrustIsLeftOutWithItsReason()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        List<Reading> rows = SealedObjects.Rows(20_000);
        string path = Path.Combine(_directory, "plain.vortex");
        await WriteAsync(VortexSession.Default, path, rows, ct);

        // A session that seals seals the fragment of a plain file it builds.
        IndexFragment sealedFragment;
        await using (VortexSession sealing = SealingSession(keyring))
        {
            sealedFragment = await sealing.BuildFragmentAsync(path, Policy, cancellationToken: ct);
        }

        Assert.True(SealedLayout.EndsSealed(sealedFragment.Bytes.Span));
        Assert.True(sealedFragment.Bytes.Span.IndexOf("Nantes"u8) < 0, "A city's name lies in plain in the sealed fragment.");
        IndexFragment plainFragment = await VortexFileIndexer.BuildFragmentAsync(path, Policy, cancellationToken: ct);
        Assert.False(SealedLayout.EndsSealed(plainFragment.Bytes.Span));

        // The plain fragment and file hold the names, which is what the sealed ones are checked for.
        Assert.True(plainFragment.Bytes.Span.IndexOf("Nantes"u8) >= 0);
        Assert.True((await System.IO.File.ReadAllBytesAsync(path, ct)).AsSpan().IndexOf("Nantes"u8) >= 0);

        // Without a keyring, the sealed fragment is left out and the file reads as before; with one, it is used.
        await using (VortexFile file = await VortexSession.Default.OpenAsync(path, With(sealedFragment), ct))
        {
            Assert.DoesNotContain(await file.ReadIndexesAsync(ct), index => index.Column == "City");
            Assert.Contains("keyring", file.IndexFragmentRefusals.Single(), StringComparison.Ordinal);
            Assert.Equal(rows.Count, await file.Scan<Reading>().CountAsync(ct));
        }

        await using (VortexSession keyed = VortexSession.Create(o => o.Keyring = keyring))
        await using (VortexFile file = await keyed.OpenAsync(path, With(sealedFragment), ct))
        {
            Assert.Contains(await file.ReadIndexesAsync(ct), index => index.Column == "City");
            Assert.Equal([null], file.IndexFragmentRefusals);
        }

        // A session that refuses plaintext leaves a plain fragment out, here one of a sealed file built
        // through the path a dataset takes, and takes a sealed one.
        string sealedPath = Path.Combine(_directory, "sealed.vortex");
        IndexFragment plainOfSealed;
        IndexFragment sealedOfSealed;
        await using (VortexSession sealing = SealingSession(keyring))
        {
            await WriteAsync(sealing, sealedPath, rows, ct);
            await using VortexFile open = await sealing.OpenAsync(sealedPath, cancellationToken: ct);
            plainOfSealed = await VortexFileIndexer.BuildFragmentAsync(open, Policy.ToWritePolicy(), new RowRange(0, open.RowCount), cancellationToken: ct);
            sealedOfSealed = await VortexFileIndexer.BuildFragmentAsync(open, Policy, cancellationToken: ct);
        }

        await using VortexSession strict = VortexSession.Create(o =>
        {
            o.Keyring = keyring;
            o.RefusePlaintext = true;
        });
        await using (VortexFile file = await strict.OpenAsync(sealedPath, With(plainOfSealed), ct))
        {
            Assert.DoesNotContain(await file.ReadIndexesAsync(ct), index => index.Column == "City");
            Assert.Contains("refuses plaintext", file.IndexFragmentRefusals.Single(), StringComparison.Ordinal);
        }

        await using (VortexFile file = await strict.OpenAsync(sealedPath, With(sealedOfSealed), ct))
        {
            Assert.Contains(await file.ReadIndexesAsync(ct), index => index.Column == "City");
            Assert.Equal([null], file.IndexFragmentRefusals);
        }
    }

    private static VortexOpenOptions With(IndexFragment fragment) => new VortexOpenOptions { IndexFragments = [fragment] };

    private static VortexSession SealingSession(VortexKeyring keyring) => VortexSession.Create(o =>
    {
        o.Keyring = keyring;
        o.EncryptFiles = true;
    });

    private static async Task WriteAsync(VortexSession session, string path, List<Reading> rows, CancellationToken ct)
    {
        await using VortexFileWriter writer = session.CreateWriter<Reading>(path);
        await writer.WriteAsync<Reading>(rows.ToArray(), ct);
        await writer.CompleteAsync(ct);
    }
}
