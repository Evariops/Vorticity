// Appends to a file at a location: the object replaced at completion by its old bytes and the new
// ones, only while nobody else replaced it, and left as it was by an append given up. A store that
// copies nothing on its service has the old bytes read back, bound to their token, and sent again.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Vorticity.Tests.Dataset;
using Xunit;
using Reading = Vorticity.Tests.ObjectStorage.LocationTests.Reading;

namespace Vorticity.Tests.ObjectStorage;

public sealed class LocationAppendTests
{
    private const string Location = "mem://lake/readings.vortex";

    [Fact]
    public async Task AFileAtALocationTakesAppendsAndReadsBackWhole()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using CountingObjectStore counting = new CountingObjectStore(memory);
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("mem://lake", counting));
        Reading[] rows = Rows(18_000);
        await WriteAsync(session, rows[..10_000], ct);
        byte[] written = (await BytesAsync(memory, "readings.vortex"))!;

        counting.Reset();
        await AppendAsync(session, rows[10_000..15_000], ct);

        // One put of the old bytes and the new, the old ones intact at the start: an append writes
        // after the old end.
        Assert.Equal(1, counting.CountOf(ObjectOperation.Put));
        byte[] grown = (await BytesAsync(memory, "readings.vortex"))!;
        Assert.True(grown.Length > written.Length);
        Assert.Equal(written, grown.AsSpan(0, written.Length).ToArray());

        await AppendAsync(session, rows[15_000..], ct);
        Assert.Equal(rows, await RowsAsync(session, ct));
    }

    [Fact]
    public async Task AnAppendIsRefusedWhenAnotherWriterReplacedTheObjectInBetween()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("mem://lake", memory));
        Reading[] rows = Rows(12_000);
        await WriteAsync(session, rows[..10_000], ct);

        await using VortexFileWriter writer = await session.OpenWriterAsync(Location, cancellationToken: ct);
        await writer.WriteAsync<Reading>(rows[10_000..], ct);

        // Another writer replaces the object while the rows are appended.
        await WriteAsync(session, rows[..5_000], ct);
        byte[]? theirs = await BytesAsync(memory, "readings.vortex");

        ObjectStoreException refused = await Assert.ThrowsAsync<ObjectStoreException>(async () => await writer.CompleteAsync(ct));
        Assert.Contains("was replaced while rows were appended", refused.Message, StringComparison.Ordinal);
        Assert.Equal(theirs, await BytesAsync(memory, "readings.vortex"));
        Assert.Equal(rows[..5_000], await RowsAsync(session, ct));
    }

    [Fact]
    public async Task AnAppendGivenUpLeavesTheObjectAsItWas()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("mem://lake", memory));
        Reading[] rows = Rows(12_000);
        await WriteAsync(session, rows[..10_000], ct);
        byte[]? before = await BytesAsync(memory, "readings.vortex");

        await using (VortexFileWriter writer = await session.OpenWriterAsync(Location, cancellationToken: ct))
        {
            await writer.WriteAsync<Reading>(rows[10_000..], ct);
        }

        Assert.Equal(before, await BytesAsync(memory, "readings.vortex"));
        Assert.Equal(rows[..10_000], await RowsAsync(session, ct));
    }

    [Fact]
    public async Task ASealedFileAtALocationTakesAppendsUnderItsOwnKey()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        using VortexKeyring keyring = Vorticity.Tests.Sealing.SealedObjects.Keyring();
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(o =>
        {
            o.Keyring = keyring;
            o.EncryptFiles = true;
            o.ObjectStores.Add("mem://lake", memory);
        });
        Reading[] rows = Rows(16_000);
        await WriteAsync(session, rows[..10_000], ct);
        await AppendAsync(session, rows[10_000..], ct);

        Assert.Equal(rows, await RowsAsync(session, ct));
        byte[] bytes = (await BytesAsync(memory, "readings.vortex"))!;
        Assert.Equal("VXSEALED"u8.ToArray(), bytes.AsSpan(0, 8).ToArray());
    }

    [Fact]
    public async Task AStoreThatCannotReplaceOnATokensMatchRefusesTheAppendBeforeARowIsWritten()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        string directory = Path.Combine(Path.GetTempPath(), "vorticity-location-append", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await using FileObjectStore files = new FileObjectStore(directory);
            await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("mem://lake", files));
            await WriteAsync(session, Rows(1_000), ct);
            byte[] before = await System.IO.File.ReadAllBytesAsync(Path.Combine(directory, "readings.vortex"), ct);

            await Assert.ThrowsAsync<NotSupportedException>(async () => await session.OpenWriterAsync(Location, cancellationToken: ct));
            Assert.Equal(before, await System.IO.File.ReadAllBytesAsync(Path.Combine(directory, "readings.vortex"), ct));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task AViewUnderAPrefixAppendsThroughTheStoreBelowIt()
    {
        CancellationToken ct = TestContext.Current.CancellationToken;
        await using MemoryObjectStore memory = new MemoryObjectStore();
        await using VortexSession session = VortexSession.Create(o => o.ObjectStores.Add("mem://lake/tables/", memory));
        Reading[] rows = Rows(11_000);
        await using (VortexFileWriter writer = session.CreateWriter<Reading>("mem://lake/tables/a.vortex"))
        {
            await writer.WriteAsync<Reading>(rows[..10_000], ct);
            await writer.CompleteAsync(ct);
        }

        await using (VortexFileWriter writer = await session.OpenWriterAsync("mem://lake/tables/a.vortex", cancellationToken: ct))
        {
            await writer.WriteAsync<Reading>(rows[10_000..], ct);
            await writer.CompleteAsync(ct);
        }

        await using VortexFile file = await session.OpenAsync("mem://lake/tables/a.vortex", cancellationToken: ct);
        Assert.Equal(rows, await ReadAllAsync(file, ct));
        Assert.NotNull(await BytesAsync(memory, "tables/a.vortex"));
    }

    internal static Reading[] Rows(int count) =>
        [.. Enumerable.Range(0, count).Select(i => new Reading(i / 100, (i % 400) / 10.0))];

    /// <summary>The object's bytes, or null when there is none.</summary>
    internal static async Task<byte[]?> BytesAsync(IObjectStore store, string key)
    {
        if (await store.HeadAsync(key, default) is not { } head)
        {
            return null;
        }

        using ObjectRange range = await store.GetRangeAsync(key, 0, (int)head.Length, default);
        return range.Bytes.ToArray();
    }

    private static async Task WriteAsync(VortexSession session, Reading[] rows, CancellationToken ct)
    {
        await using VortexFileWriter writer = session.CreateWriter<Reading>(Location);
        await writer.WriteAsync<Reading>(rows, ct);
        await writer.CompleteAsync(ct);
    }

    private static async Task AppendAsync(VortexSession session, Reading[] rows, CancellationToken ct)
    {
        await using VortexFileWriter writer = await session.OpenWriterAsync(Location, cancellationToken: ct);
        await writer.WriteAsync<Reading>(rows, ct);
        await writer.CompleteAsync(ct);
    }

    private static async Task<Reading[]> RowsAsync(VortexSession session, CancellationToken ct)
    {
        await using VortexFile file = await session.OpenAsync(Location, cancellationToken: ct);
        return await ReadAllAsync(file, ct);
    }

    internal static async Task<Reading[]> ReadAllAsync(VortexFile file, CancellationToken ct)
    {
        List<Reading> read = [];
        await foreach (Reading reading in file.Scan<Reading>().ToRecordsAsync(ct))
        {
            read.Add(reading);
        }

        return [.. read];
    }
}
