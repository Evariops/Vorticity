// What a full verification checks of a dataset's objects against its layout (spec A, 3.1; A10): objects that do not lie
// in the layout's order are named, though every promise the store's checks hold still holds.
//
// THE ORACLE IS A HEADER REWRITTEN: the commit's header made to state another key than the one its objects were written
// under, its pages and fragments where they lay, so that each object holds what the store's checks expect and breaks the
// key the layout now states.
using System;
using System.Buffers;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.IO;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class DatasetLayoutVerifyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// A dataset keyed by its tenant then its sequence, verified as written; then its header made to key it by the sequence
    /// alone: a full verification names the rows out of the key's order (I2) and the tree key that is not the first row's
    /// key (I4), while the store's own checks still hold.
    /// </summary>
    [Fact]
    public async Task ObjectsOutOfTheLayoutsOrderAreNamed()
    {
        await using MemoryObjectStore store = new MemoryObjectStore();
        string key;
        await using (VortexDataset dataset = await VortexDataset.CreateAsync(store, Row.Schema, new DatasetOptions
        {
            Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Row.Tenant)), LayoutValue.Column(nameof(Row.Sequence))] },
            ContentsObjectBytes = 1,
        }, Ct))
        {
            await using (ObjectDraft draft = dataset.StartObject())
            {
                await draft.Writer.WriteAsync<Row>([.. Enumerable.Range(0, 5_000).Select(i => new Row($"tenant-{i % 7}", i))], Ct);
                await dataset.AppendAsync(draft, Ct);
            }

            DatasetVerification written = await dataset.VerifyAsync(cancellationToken: Ct);
            Assert.True(written.Holds, string.Join('\n', written.Problems));
            key = (await dataset.ScanBuilder().ObjectsAsync(Ct).SingleAsync(Ct)).Entry.Key;
            await RewriteHeaderAsync(store, dataset.Version, header => header with
            {
                Layout = new DatasetLayout { SortKey = [LayoutValue.Column(nameof(Row.Sequence))] },
            });
        }

        await using VortexDataset reopened = await VortexDataset.OpenAsync(store, new DatasetOptions(), Ct);
        DatasetVerification verified = await reopened.VerifyAsync(cancellationToken: Ct);
        Assert.False(verified.Holds);
        Assert.All(verified.Problems, problem => Assert.Matches(@"\(I[1-7]\)$", problem));
        Assert.Contains(verified.Problems, problem => problem.Contains("(I2)", StringComparison.Ordinal) && problem.Contains(key, StringComparison.Ordinal));
        Assert.Contains(verified.Problems, problem => problem.Contains("(I4)", StringComparison.Ordinal));
    }

    /// <summary>A commit object written again with <paramref name="change"/> made to its header, its pages and fragments where they lay.</summary>
    private static async Task RewriteHeaderAsync(MemoryObjectStore store, ulong version, Func<CommitHeader, CommitHeader> change)
    {
        string key = CommitKey.For(version);
        long length = (await store.HeadAsync(key, default))!.Value.Length;
        byte[] bytes;
        using (ObjectRange range = await store.GetRangeAsync(key, 0, (int)length, default))
        {
            bytes = range.Bytes.ToArray();
        }

        CommitObject commit = CommitObject.Open(bytes, bytes.Length);
        CommitObjectBuilder builder = new CommitObjectBuilder(version);
        List<(PageReference Reference, bool Page)> body = [.. commit.Table.Pages.Select(page => (page, true)), .. commit.Table.Fragments.Select(fragment => (fragment, false))];
        foreach ((PageReference reference, bool page) in body.OrderBy(item => item.Reference.Offset))
        {
            ReadOnlySpan<byte> held = bytes.AsSpan((int)(commit.HeaderEnd + reference.Offset), reference.Length);
            Assert.Equal(reference, page ? builder.AddPage(held) : builder.AddFragment(held));
        }

        await store.DeleteAsync(key, default);
        Assert.Equal(PutOutcome.Created, await store.PutIfAbsentAsync(key, builder.Build(change(commit.Header)), default));
    }

    [VortexRecord]
    public partial record struct Row(string Tenant, long Sequence);
}
