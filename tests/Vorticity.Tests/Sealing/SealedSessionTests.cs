// Encryption as a caller sees it: a session's keyring and policy, and nothing else in the API.
//
// WHAT IS HELD: a session that seals writes files only a keyring holding their key opens, and opens
// them as plain ones, scans, filters, aggregates and parallel lanes giving the same answers; a
// session without the key is told the file is sealed; a session that refuses plaintext refuses a
// plain file and one that does not opens it; a file written to a caller's pipe opens from a source;
// the keyring is asked once per data key however many files are written or opened; a sealed file
// decrypts to a plain one any session opens; a policy without a keyring is refused when the session
// is created. Appends are held in SealedAppendTests.
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.Tests.Api;
using Xunit;

namespace Vorticity.Tests.Sealing;

public sealed class SealedSessionTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "vorticity-sealing-tests", Guid.NewGuid().ToString("N"));

    public SealedSessionTests() => Directory.CreateDirectory(_directory);

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
    public async Task ASessionThatSealsWritesFilesOnlyItsKeyringOpens()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        List<Reading> rows = SealedObjects.Rows(50_000);
        string path = Path.Combine(_directory, "sealed.vortex");
        await using (VortexSession sealing = VortexSession.Create(o =>
        {
            o.Keyring = keyring;
            o.EncryptFiles = true;
        }))
        {
            await WriteAsync(sealing, path, rows, ct);
            await using VortexFile file = await sealing.OpenAsync(path, cancellationToken: ct);
            Assert.Equal(rows, await SealedObjects.RowsOfAsync(file, ct));
        }

        Assert.Equal("VXSEALED"u8.ToArray(), (await System.IO.File.ReadAllBytesAsync(path, ct)).AsSpan(0, 8).ToArray());

        VortexEncryptionException keyless = await Assert.ThrowsAsync<VortexEncryptionException>(
            async () => await VortexFile.OpenAsync(path, ct));
        Assert.Equal(VortexEncryptionError.NoKey, keyless.Error);
        Assert.Contains("sealed", keyless.Message, StringComparison.Ordinal);

        // The key is in the ring: the policy says nothing about opening.
        await using VortexSession reading = VortexSession.Create(o => o.Keyring = keyring);
        await using VortexFile again = await reading.OpenAsync(path, cancellationToken: ct);
        Assert.Equal(rows.Count, again.RowCount);
    }

    [Fact]
    public async Task QueriesOverASealedFileAnswerAsOverAPlainOne()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        List<Reading> rows = SealedObjects.Rows(200_000);
        string plainPath = Path.Combine(_directory, "plain.vortex");
        string sealedPath = Path.Combine(_directory, "sealed.vortex");
        await WriteAsync(VortexSession.Default, plainPath, rows, ct);
        await using VortexSession session = VortexSession.Create(o =>
        {
            o.Keyring = keyring;
            o.EncryptFiles = true;
            o.SegmentCache = new SegmentCache(64L << 20);
        });
        await WriteAsync(session, sealedPath, rows, ct);

        await using VortexFile plain = await VortexFile.OpenAsync(plainPath, ct);
        await using VortexFile sealedFile = await session.OpenAsync(sealedPath, cancellationToken: ct);
        foreach (int degree in new[] { 1, 4 })
        {
            ScanOptions options = new ScanOptions { DegreeOfParallelism = degree };
            Assert.Equal(
                await plain.Scan<Reading>().With(options).Where(r => r.Day >= 900 && r.Celsius > 20.0).CountAsync(ct),
                await sealedFile.Scan<Reading>().With(options).Where(r => r.Day >= 900 && r.Celsius > 20.0).CountAsync(ct));
            Assert.Equal(
                await plain.Scan<Reading>().With(options).AverageAsync(r => r.Celsius, ct),
                await sealedFile.Scan<Reading>().With(options).AverageAsync(r => r.Celsius, ct));
            Assert.Equal(
                await plain.Scan<Reading>().With(options).Where(r => r.City == "Lyon").SumAsync(r => r.Day, ct),
                await sealedFile.Scan<Reading>().With(options).Where(r => r.City == "Lyon").SumAsync(r => r.Day, ct));
        }

        // A second pass reads through the session's cache, which holds plaintext.
        Assert.Equal(rows, await SealedObjects.RowsOfAsync(sealedFile, ct));
        Assert.Equal(rows, await SealedObjects.RowsOfAsync(sealedFile, ct));
    }

    [Fact]
    public async Task ASessionThatRefusesPlaintextRefusesAPlainFile()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        string path = Path.Combine(_directory, "plain.vortex");
        await WriteAsync(VortexSession.Default, path, SealedObjects.Rows(1_000), ct);

        await using (VortexSession lenient = VortexSession.Create(o => o.Keyring = keyring))
        {
            await using VortexFile plain = await lenient.OpenAsync(path, cancellationToken: ct);
            Assert.Equal(1_000, plain.RowCount);
        }

        await using VortexSession strict = VortexSession.Create(o =>
        {
            o.Keyring = keyring;
            o.RefusePlaintext = true;
        });
        VortexEncryptionException refused = await Assert.ThrowsAsync<VortexEncryptionException>(
            async () => await strict.OpenAsync(path, cancellationToken: ct));
        Assert.Equal(VortexEncryptionError.Refused, refused.Error);
        VortexEncryptionException fromSource = await Assert.ThrowsAsync<VortexEncryptionException>(
            async () => await strict.OpenAsync(new MemorySegmentSource(await System.IO.File.ReadAllBytesAsync(path, ct)), cancellationToken: ct));
        Assert.Equal(VortexEncryptionError.Refused, fromSource.Error);
    }

    [Fact]
    public async Task AFileSealedIntoAPipeOpensFromASource()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        List<Reading> rows = SealedObjects.Rows(30_000);
        await using VortexSession session = VortexSession.Create(o =>
        {
            o.Keyring = keyring;
            o.EncryptFiles = true;
        });
        Pipe pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        await using (VortexFileWriter writer = session.CreateWriter(pipe.Writer, Reading.Schema))
        {
            await writer.WriteAsync<Reading>(rows.ToArray(), ct);
            await writer.CompleteAsync(ct);
        }

        byte[] sealedBytes = await SealedObjects.DrainAsync(pipe.Reader, ct);
        Assert.Equal("VXSEALED"u8.ToArray(), sealedBytes.AsSpan(0, 8).ToArray());
        await using (VortexFile file = await session.OpenAsync(new MemorySegmentSource(sealedBytes), cancellationToken: ct))
        {
            Assert.Equal(rows, await SealedObjects.RowsOfAsync(file, ct));
        }

        // A source the caller keeps stays open past the file.
        MemorySegmentSource kept = new MemorySegmentSource(sealedBytes);
        await using (VortexFile file = await session.OpenAsync(kept, new VortexOpenOptions { LeaveSourceOpen = true }, ct))
        {
            Assert.Equal(rows.Count, file.RowCount);
        }

        Assert.Equal(sealedBytes.Length, kept.Length);
        await kept.DisposeAsync();
    }

    [Fact]
    public async Task TheKeyringIsAskedOncePerDataKey()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using CountingKeyring keyring = new CountingKeyring(SealedObjects.Keyring());
        await using VortexSession session = VortexSession.Create(o =>
        {
            o.Keyring = keyring;
            o.EncryptFiles = true;
        });

        for (int i = 0; i < 4; i++)
        {
            await WriteAsync(session, Path.Combine(_directory, $"file-{i}.vortex"), SealedObjects.Rows(500 + i), ct);
        }

        for (int round = 0; round < 3; round++)
        {
            for (int i = 0; i < 4; i++)
            {
                await using VortexFile file = await session.OpenAsync(Path.Combine(_directory, $"file-{i}.vortex"), cancellationToken: ct);
                Assert.Equal(500 + i, file.RowCount);
            }
        }

        Assert.Equal((1, 1), (keyring.Generated, keyring.Unwrapped));
    }

    [Fact]
    public async Task ASealedFileDecryptsToAPlainOneAnySessionOpens()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = SealedObjects.Keyring();
        List<Reading> rows = SealedObjects.Rows(20_000);
        string sealedPath = Path.Combine(_directory, "sealed.vortex");
        string plainPath = Path.Combine(_directory, "plain.vortex");
        await using VortexSession session = VortexSession.Create(o =>
        {
            o.Keyring = keyring;
            o.EncryptFiles = true;
        });
        await WriteAsync(session, sealedPath, rows, ct);

        await using (FileStream plain = System.IO.File.Create(plainPath))
        {
            await session.DecryptAsync(sealedPath, plain, ct);
        }

        await using VortexFile file = await VortexFile.OpenAsync(plainPath, ct);
        Assert.Equal(rows, await SealedObjects.RowsOfAsync(file, ct));
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await VortexSession.Default.DecryptAsync(sealedPath, Stream.Null, ct));
    }

    [Fact]
    public void APolicyWithoutAKeyringIsRefusedWhenTheSessionIsCreated()
    {
        Assert.Throws<InvalidOperationException>(() => VortexSession.Create(o => o.EncryptFiles = true));
        Assert.Throws<InvalidOperationException>(() => VortexSession.Create(o => o.RefusePlaintext = true));
    }

    private static async Task WriteAsync(VortexSession session, string path, List<Reading> rows, CancellationToken ct)
    {
        await using VortexFileWriter writer = session.CreateWriter<Reading>(path);
        await writer.WriteAsync<Reading>(rows.ToArray(), ct);
        await writer.CompleteAsync(ct);
    }

    /// <summary>A keyring that counts what it is asked.</summary>
    private sealed class CountingKeyring(VortexKeyring inner) : VortexKeyring
    {
        private int _generated;
        private int _unwrapped;

        internal int Generated => Volatile.Read(ref _generated);

        internal int Unwrapped => Volatile.Read(ref _unwrapped);

        public override ValueTask<DataKey> GenerateAsync(ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _generated);
            return inner.GenerateAsync(context, cancellationToken);
        }

        public override ValueTask<DataKey> UnwrapAsync(string keyId, ReadOnlyMemory<byte> wrappedKey, ReadOnlyMemory<byte> context, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _unwrapped);
            return inner.UnwrapAsync(keyId, wrappedKey, context, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }
        }
    }
}
