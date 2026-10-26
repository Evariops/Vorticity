// A lookup of one id reads the entry of the id's postings in the object's index directory, which the table of contents
// names apart, and not the directory whole, which holds every other index of the object besides.
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Vorticity.Dataset;
using Vorticity.Indexes;
using Xunit;

namespace Vorticity.Tests.Dataset;

public sealed partial class IdentifierPostingsTests
{
    /// <summary>
    /// Two sealed hours of requests, the id declared an identifier: the table of contents of each names the entry of the
    /// id's hashed postings, a region of its own within the index directory and smaller than it; a lookup of one id asks
    /// for that entry and never for the directory, and finds the id's row. So it does in chunks of 16 rows whose directories
    /// lie in pages of four (<paramref name="paged"/>), their pages read once the postings say the blocks.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ALookupReadsTheEntryOfItsIdentifierAndNotTheWholeDirectory(bool paged)
    {
        string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "vorticity-postings-entry-" + Guid.NewGuid().ToString("N"));
        try
        {
            await using MemoryObjectStore store = new MemoryObjectStore();
            List<(UInt128 Uid, ulong Offset, uint Length)> asked = [];
            SynopsisCache cache = new SynopsisCache(directory, 64 << 20);
            cache.Asked = (uid, region) =>
            {
                lock (asked)
                {
                    asked.Add((uid, region.Offset, region.Length));
                }
            };

            await using VortexSession session = VortexSession.Create(options => options.SynopsisCache = cache);
            await using VortexDataset dataset = await VortexDataset.CreateAsync(store, Request.Schema, new DatasetOptions
            {
                Session = session,
                ClusteringKey = [nameof(Request.At)],
                Identifiers = [nameof(Request.RequestId)],
                Clustering = new ClusteringOptions { Value = ClusteringValue.Truncate(CalendarUnit.Hour) }, ContentsObjectBytes = 0, SliceTargetRows = 500,
                Write = paged ? new VortexWriteOptions { BlockRows = 16, ChunkTargetBytes = 1, ChunkPageEntries = 4 } : VortexWriteOptions.Default,
                ChunkBytes = paged ? 1 : 1 << 20,
            }, Ct);
            Random random = new Random(31);
            List<Request> appended = [];
            for (int part = 0; part < 16; part++)
            {
                Request[] rows = [.. Enumerable.Range(0, 400).Select(i => new Request(
                    Noon.AddSeconds((part * 400 + i) * 1.25), $"{random.NextInt64():x16}{random.NextInt64():x16}", 200 + random.Next(5)))];
                await using ObjectDraft draft = dataset.StartObject();
                await draft.Writer.WriteAsync<Request>(rows, Ct);
                await dataset.AppendAsync(draft, Ct);
                appended.AddRange(rows);
            }

            while (await dataset.CompactAsync(new CompactionOptions { LevelZeroCeiling = 2 }, Ct) is not null)
            {
            }

            List<ObjectEntry> hours = [];
            await foreach (TreeEntry leaf in dataset.AboveLevelZeroAsync(Ct))
            {
                hours.Add(ObjectEntry.FromBytes(leaf.Value.Span));
            }

            Assert.True(hours.Count >= 3, $"{hours.Count} objects above level 0");
            Request needle = appended[1_234];
            VortexExpr filter = VortexExpr.Parse($"RequestId = '{needle.RequestId}'");
            List<RecordBatch>?[] found = new List<RecordBatch>?[hours.Count];
            for (int h = 0; h < hours.Count; h++)
            {
                ObjectContents contents = hours[h].Contents!;
                Assert.Equal(paged, contents.Paging is not null);
                IndexSegment whole = contents.DirectoryRegion!.Value;
                LocatingEntry postings = Assert.Single(contents.Locating!, l => l.Kind == IndexKinds.PostingsHashed);
                Assert.Equal(1u, postings.Column);
                Assert.InRange(postings.Region.Offset, whole.Offset, whole.Offset + whole.Length - postings.Region.Length);
                long named = contents.Locating!.Sum(l => (long)l.Region.Length);
                Console.Out.Write(FormattableString.Invariant(
                    $"LOCATING ENTRIES: {contents.Locating!.Count} of {named} bytes, in a directory of {whole.Length}.\n"));
                Assert.True(named < whole.Length, $"{named} bytes of entries in a directory of {whole.Length}");
                Assert.Equal(contents.Locating, ObjectContents.From(contents.ToBytes())!.Locating);

                asked.Clear();
                found[h] = await dataset.LookupAsync(hours[h], filter, null, Ct);
                Assert.Contains(asked, a => a.Uid == hours[h].Uid && a.Offset == postings.Region.Offset && a.Length == postings.Region.Length);
                Assert.DoesNotContain(asked, a => a.Uid == hours[h].Uid && a.Offset == whole.Offset && a.Length == whole.Length);
            }

            Assert.Equal(1, Holding(found, needle.RequestId));
        }
        finally
        {
            if (System.IO.Directory.Exists(directory))
            {
                System.IO.Directory.Delete(directory, recursive: true);
            }
        }
    }
}
