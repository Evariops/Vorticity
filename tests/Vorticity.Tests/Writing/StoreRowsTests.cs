using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Arrays;
using Vorticity.Scanning;
using Vorticity.Types;
using Vorticity.Writing;
using Xunit;

namespace Vorticity.Tests.Writing;

/// <summary>
/// The rows of a canonical node appended whole to a store of its type: a struct that holds null rows, a variant among
/// them, keeps its fields and its nulls, as the rows a read delivers and as the rows of a gather.
/// </summary>
public sealed partial class StoreRowsTests
{
    [Fact]
    public async Task ANullableVariantKeepsItsNullRowsAndItsValues()
    {
        Row[] rows = new Row[3_000];
        for (int i = 0; i < rows.Length; i++)
        {
            rows[i] = new Row(i, (i % 3) switch
            {
                0 => (Variant?)null,
                1 when i % 7 == 0 => Variant.Null,
                _ => Variant.Object(("n", Variant.From(i)), ("list", Variant.Array(Variant.From("a"), Variant.From(i % 5)))),
            });
        }

        string path = Path.Combine(AppContext.BaseDirectory, $"store-rows-{Environment.ProcessId}-{Guid.NewGuid():N}.vortex");
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Row>(path, new VortexWriteOptions { RowBlockSize = 512 }))
        {
            await writer.WriteAsync<Row>(rows, Ct);
            await writer.CompleteAsync(Ct);
        }

        try
        {
            VortexSessionOptions options = VortexSession.Default.Options;
            DType dtype = VortexTypes.ToDType(VortexType.Variant.Nullable, new DTypeArena());
            ColumnStore whole = ColumnStores.Create(dtype, options.EnginePool, options.Extensions);
            ColumnStore odd = ColumnStores.Create(dtype, options.EnginePool, options.Extensions);
            List<int> oddRows = [];
            int seen = 0;
            await using VortexFile file = await VortexFile.OpenAsync(path, Ct);
            await foreach (RecordBatch batch in file.ScanBuilder().Project("Extra").ExecuteAsync().WithCancellation(Ct))
            {
                using (batch)
                {
                    int node = batch.Arena.GetNode(batch.RootIndex).GetFieldIndex(0);
                    StoreRows.Append(whole, batch.Arena, node);

                    // A gather of the odd rows, which keeps nulls where they fall.
                    List<int> picked = [];
                    for (int row = 1; row < batch.RowCount; row += 2)
                    {
                        picked.Add(row);
                        oddRows.Add(seen + row);
                    }

                    StoreRows.Append(odd, batch.Arena, Vorticity.Compute.CanonicalFilter.Apply(batch.Arena, node, [.. picked]));
                    seen += batch.RowCount;
                }
            }

            Assert.Equal(rows.Length, seen);
            Assert.Equal(Texts(rows, null), Read(whole));
            Assert.Equal(Texts(rows, oddRows), Read(odd));
            whole.Release();
            odd.Release();
        }
        finally
        {
            System.IO.File.Delete(path);
        }
    }

    /// <summary>The rows' variants as text, null for a null row: those of <paramref name="picked"/>, or all.</summary>
    private static List<string?> Texts(Row[] rows, List<int>? picked)
    {
        List<string?> texts = [];
        foreach (int row in picked ?? [.. System.Linq.Enumerable.Range(0, rows.Length)])
        {
            texts.Add(rows[row].Extra?.ToJson());
        }

        return texts;
    }

    /// <summary>What <paramref name="store"/> holds, built into an arena and read back as variants.</summary>
    private static List<string?> Read(ColumnStore store)
    {
        CanonicalArena arena = new CanonicalArena(16, VortexSession.Default.Options.EnginePool);
        try
        {
            int rows = store.Rows;
            int node = store.Build(arena, rows);
            Variant?[] values = new Variant?[rows];
            ColumnData.CopyVariants(arena, node, values.AsSpan());
            List<string?> texts = [];
            foreach (Variant? value in values)
            {
                texts.Add(value?.ToJson());
            }

            return texts;
        }
        finally
        {
            arena.Reset();
        }
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [VortexRecord]
    public partial record struct Row(int Id, Variant? Extra);
}
