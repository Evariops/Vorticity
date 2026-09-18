// An index fragment bound to one version of one file - docs/13-dataset.md §7 and §6.4. These were
// the sidecar's binding tests (step 26); step 42d retired the sidecar and kept its container, which
// is the fragment, so they hold the same promises of the fragment.
//
// WHAT IS HELD: a fragment records the file's length, identity, store token and XXH3-128; reading it
// reads no byte of the file beyond the tail the open already took; a file rewritten at the same
// length is refused by its identity, without a hash; a file without an identity is bound by its
// store token, which a touch breaks and a source without a path cannot give; a fragment that binds
// only by a SHA-256 is refused with the reason; and a byte changed under an unchanged identity passes
// every reader check and fails the recorded hash, which is what `VerifyIndexesAsync` compares.
using System;
using System.IO;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Buffers;
using Vorticity.Columns;
using Vorticity.Expressions;
using Vorticity.File;
using Vorticity.Indexes;
using Vorticity.IO;
using Vorticity.Scan;
using Vorticity.Serialization.Schemas;
using Vorticity.Tests.Scan;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Indexes;

public sealed class FragmentBindingTests
{
    private const int Rows = 20_000;

    private static readonly DTypeArena Types = new DTypeArena();

    private static readonly DType Schema = Types.Struct(
        ["id"], [Types.Primitive(PType.I64, Nullability.NonNullable)], Nullability.NonNullable);

    private static readonly WritePolicy Policy = WritePolicy.None.For("id", IndexPolicy.SortedRuns);

    private static long Id(int row) => (row * 7_919L) % 100_003;

    [Fact]
    public async Task AFragmentBindsByIdentityAndIsReadWithoutReadingTheFile()
    {
        Decoders.EnsureRegistered();
        using Temp temp = new Temp();
        await WriteAsync(temp.Path, Guid.NewGuid());
        byte[] fragment = await FragmentAsync(temp.Path);

        // What it records.
        IndexDirectory recorded = await RecordedAsync(temp.Path, fragment);
        await using (VortexFile file = await VortexFile.OpenAsync(temp.Path))
        {
            Assert.Equal((ulong)file.FileLength, recorded.FileLength);
            Assert.Equal(file.Identity, recorded.FileIdentity);
        }

        Assert.StartsWith("fs:", recorded.FileToken, StringComparison.Ordinal);
        Assert.Equal(XxHash128.HashToUInt128(await System.IO.File.ReadAllBytesAsync(temp.Path)), recorded.FileHash);
        Assert.False(recorded.LegacySha256);

        // Reading it reads nothing of the file past the open's own tail.
        CountingSource source = new CountingSource(MemoryMappedSegmentSource.Open(temp.Path));
        await using VortexFile counted = await VortexFile.OpenAsync(source, With(fragment));
        int atOpen = source.Reads;
        IndexDirectory? directory = await counted.ReadIndexDirectoryAsync();
        Assert.True(directory is not null, Assert.Single(counted.IndexFragmentRefusals));
        Assert.Equal(atOpen, source.Reads);

        // And it answers.
        VortexExpr equal = Expr.Eq(Expr.Field("id"), Expr.Literal(FilterLiteral.From(Id(4_321))));
        Assert.Equal(1, await counted.Scan().Where(equal).CountAsync());
    }

    [Fact]
    public async Task AFileRewrittenAtTheSameLengthIsRefusedByItsIdentity()
    {
        Decoders.EnsureRegistered();
        using Temp temp = new Temp();
        await WriteAsync(temp.Path, Guid.NewGuid());
        byte[] fragment = await FragmentAsync(temp.Path);
        long length = new FileInfo(temp.Path).Length;

        // The same rows written again: the same length, another version.
        await WriteAsync(temp.Path, Guid.NewGuid());
        Assert.Equal(length, new FileInfo(temp.Path).Length);

        CountingSource source = new CountingSource(MemoryMappedSegmentSource.Open(temp.Path));
        await using VortexFile file = await VortexFile.OpenAsync(source, With(fragment));
        int atOpen = source.Reads;
        Assert.Null(await file.ReadIndexDirectoryAsync());
        string refusal = Assert.Single(file.IndexFragmentRefusals)!;
        Assert.Contains("stale", refusal, StringComparison.Ordinal);
        Assert.Contains("version", refusal, StringComparison.Ordinal);
        Assert.Equal(atOpen, source.Reads);

        // The scan answers without it.
        VortexExpr equal = Expr.Eq(Expr.Field("id"), Expr.Literal(FilterLiteral.From(Id(4_321))));
        Assert.Equal(1, await file.Scan().Where(equal).CountAsync());
    }

    [Fact]
    public async Task AFileWithoutAnIdentityIsBoundByItsStoreToken()
    {
        Decoders.EnsureRegistered();
        using Temp temp = new Temp();

        // A file of this writer's shape whose identity entry is renamed, one byte: to a reader it
        // is a file from another writer, with a metadata key it does not know.
        await WriteAsync(temp.Path, Guid.NewGuid());
        byte[] bytes = await System.IO.File.ReadAllBytesAsync(temp.Path);
        int key = bytes.AsSpan().LastIndexOf(FileIdentity.MetadataKeyUtf8);
        Assert.True(key > 0);
        bytes[key + FileIdentity.MetadataKeyUtf8.Length - 1] = (byte)'Y';
        await System.IO.File.WriteAllBytesAsync(temp.Path, bytes);
        await using (VortexFile foreign = await VortexFile.OpenAsync(temp.Path))
        {
            Assert.Null(foreign.Identity);
        }

        byte[] fragment = await FragmentAsync(temp.Path);
        IndexDirectory recorded = await RecordedAsync(temp.Path, fragment);
        Assert.Null(recorded.FileIdentity);
        Assert.Equal(IndexContainer.TokenOf(temp.Path), recorded.FileToken);

        await using (VortexFile bound = await VortexFile.OpenAsync(temp.Path, With(fragment)))
        {
            Assert.True(await bound.ReadIndexDirectoryAsync() is not null, Assert.Single(bound.IndexFragmentRefusals));
        }

        // Without a path, there is no token to compare.
        await using (VortexFile unbound = await VortexFile.OpenAsync(MemoryMappedSegmentSource.Open(temp.Path), With(fragment)))
        {
            Assert.Null(await unbound.ReadIndexDirectoryAsync());
            Assert.Contains("store token", Assert.Single(unbound.IndexFragmentRefusals), StringComparison.Ordinal);
        }

        // A touch changes the token: the heuristic refuses.
        System.IO.File.SetLastWriteTimeUtc(temp.Path, DateTime.UtcNow.AddMinutes(-5));
        await using VortexFile touched = await VortexFile.OpenAsync(temp.Path, With(fragment));
        Assert.Null(await touched.ReadIndexDirectoryAsync());
        Assert.Contains("heuristic", Assert.Single(touched.IndexFragmentRefusals), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AFragmentBoundOnlyByASha256IsRefusedWithItsReason()
    {
        Decoders.EnsureRegistered();
        using Temp temp = new Temp();
        await WriteAsync(temp.Path, Guid.NewGuid());
        IndexDirectory recorded = await RecordedAsync(temp.Path, await FragmentAsync(temp.Path));
        await using VortexFile file = await VortexFile.OpenAsync(temp.Path);

        Assert.Null(IndexContainer.Unbound(recorded, file));
        IndexDirectory legacy = recorded with { FileIdentity = null, FileToken = null, LegacySha256 = true };
        Assert.Contains("SHA-256", IndexContainer.Unbound(legacy, file), StringComparison.Ordinal);
        IndexDirectory nothing = legacy with { LegacySha256 = false };
        Assert.Contains("neither", IndexContainer.Unbound(nothing, file), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AByteChangedUnderTheIdentityPassesTheReaderAndFailsTheRecordedHash()
    {
        // 13 §7: no reader computes the hash. A byte of data changed in place keeps the length and
        // the identity, so the fragment still binds; only the offline check sees it.
        Decoders.EnsureRegistered();
        using Temp temp = new Temp();
        await WriteAsync(temp.Path, Guid.NewGuid());
        byte[] fragment = await FragmentAsync(temp.Path);

        await using (VortexFile whole = await VortexFile.OpenAsync(temp.Path, With(fragment)))
        {
            VortexIndexVerification clean = await whole.VerifyIndexesAsync();
            Assert.True(clean.Holds);
            Assert.True(clean.FileHashHolds);
            Assert.True(clean.Held > 0);
        }

        byte[] bytes = await System.IO.File.ReadAllBytesAsync(temp.Path);
        bytes[bytes.Length / 3] ^= 0x10;
        await System.IO.File.WriteAllBytesAsync(temp.Path, bytes);

        await using VortexFile file = await VortexFile.OpenAsync(temp.Path, With(fragment));
        Assert.True(await file.ReadIndexDirectoryAsync() is not null, Assert.Single(file.IndexFragmentRefusals));
        VortexIndexVerification changed = await file.VerifyIndexesAsync();
        Assert.False(changed.FileHashHolds);
        Assert.False(changed.Holds);
        Assert.Empty(changed.Torn);
    }

    // ------------------------------------------------------------------------------ helpers

    private static VortexOpenOptions With(byte[] fragment) =>
        new VortexOpenOptions { Read = new VortexReadOptions { IndexFragments = [fragment] } };

    /// <summary>
    /// A fragment over the whole file, bound as the retired sidecar was: by its identity, the file
    /// system's token, and the hash of its bytes, which this indexer knows because it read them.
    /// </summary>
    private static async Task<byte[]> FragmentAsync(string path)
    {
        UInt128 hash = XxHash128.HashToUInt128(await System.IO.File.ReadAllBytesAsync(path));
        await using VortexFile file = await VortexFile.OpenAsync(path);
        IndexFragment fragment = await VortexFileIndexer.BuildFragmentAsync(
            file, Policy, new RowRange(0, file.RowCount), IndexContainer.TokenOf(path), hash,
            new VortexWriteOptions { IndexBudgetPerMille = 1_000_000 });

        // The runs outweigh this small file's compressed data: a budget of the data's size abandons
        // them, and a fragment with no entry would bind and prove nothing.
        Assert.All(fragment.Reports, report => Assert.Equal(IndexOutcome.Built, report.Outcome));
        return fragment.Bytes.ToArray();
    }

    /// <summary>The directory a fragment holds, as written.</summary>
    private static async Task<IndexDirectory> RecordedAsync(string path, byte[] fragment)
    {
        ReadOnlySpan<byte> trailer = fragment.AsSpan(fragment.Length - IndexContainer.TrailerSize);
        long offset = (long)System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(trailer);
        int length = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(trailer[8..]);
        await using VortexFile file = await VortexFile.OpenAsync(path);
        Assert.True(IndexDirectory.TryParse(
            fragment.AsSpan((int)offset, length), (ulong)file.RowCount, (ulong)offset, out IndexDirectory? directory, out string? reason), reason);
        return directory!;
    }

    private sealed class Temp : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"vorticity-fragment-{Guid.NewGuid():N}.vortex");

        public void Dispose() => System.IO.File.Delete(Path);
    }

    /// <summary>Counts what goes through a source.</summary>
    private sealed class CountingSource(ISegmentSource inner) : ISegmentSource
    {
        internal int Reads { get; private set; }

        public ValueTask<long> GetLengthAsync(CancellationToken cancellationToken) => inner.GetLengthAsync(cancellationToken);

        public ValueTask<SegmentOwner> ReadAsync(SegmentSpec spec, CancellationToken cancellationToken)
        {
            Reads++;
            return inner.ReadAsync(spec, cancellationToken);
        }

        public ValueTask ReadManyAsync(SegmentRequestSet requests, CancellationToken cancellationToken)
        {
            Reads += requests.Count;
            return inner.ReadManyAsync(requests, cancellationToken);
        }

        public ValueTask<SegmentOwner> ReadRangeAsync(long offset, int length, int alignment, CancellationToken cancellationToken)
        {
            Reads++;
            return inner.ReadRangeAsync(offset, length, alignment, cancellationToken);
        }

        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }

    private static async Task WriteAsync(string path, Guid identity)
    {
        VortexWriteOptions options = new VortexWriteOptions
        {
            RowBlockSize = 1_024,
            Indexes = WritePolicy.None,
            Identity = identity,
        };
        await using VortexFileWriter writer = VortexFileWriter.Create(path, Schema, options);
        CanonicalArena arena = new CanonicalArena();
        try
        {
            VortexBuffer buffer = arena.Allocate(Rows * sizeof(long), sizeof(long), out Span<byte> bytes);
            Span<long> values = MemoryMarshal.Cast<byte, long>(bytes);
            for (int i = 0; i < Rows; i++)
            {
                values[i] = Id(i);
            }

            int column = arena.AddPrimitive(Schema.GetField(0), Rows, Validity.NonNullable, PType.I64, buffer);
            using RecordBatch batch = new RecordBatch(arena, arena.AddStruct(Schema, Rows, Validity.NonNullable, [column]), 0);
            await writer.WriteAsync(batch);
            await writer.CompleteAsync();
        }
        finally
        {
            arena.Reset();
        }
    }
}
