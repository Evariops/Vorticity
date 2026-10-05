using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The yardstick of the in-cache path (PLAN-HIGH-CARDINALITY.md, H0b): the loop a caller would write
/// by hand over the decoded columns, an open-addressing table holding each key with its count and
/// sum inline, the same work as the operator's count and sum. The operator against it, in the same
/// process, at 10³ groups, is the ratio H15 is judged on.
/// </summary>
internal static class HandKernels
{
    internal static IEnumerable<(string File, Scenario Scenario)> All()
    {
        yield return ($"spread-random-{HighCardinality.SmallRows}", new Scenario("hand kernel random 1e3 small, count sum", RandomAsync, 1, Matrix.Small));
        yield return ($"spread-strided-{HighCardinality.SmallRows}", new Scenario("hand kernel strided 1e3 small, count sum", StridedAsync, 1, Matrix.Small));
    }

    private static async Task<long> RandomAsync(VortexFile file, Run run)
    {
        IntTable table = new IntTable();
        await foreach (Columns<SpreadK3> batch in file.Scan<SpreadK3>())
        {
            ReadOnlySpan<int> keys = batch.K3.Values;
            ReadOnlySpan<long> values = batch.Value.Values;
            for (int i = 0; i < keys.Length; i++)
            {
                ref IntTable.Entry entry = ref table.Find(keys[i]);
                entry.Count++;
                entry.Sum += values[i];
            }
        }

        run.Answer();
        return table.Rows();
    }

    private static async Task<long> StridedAsync(VortexFile file, Run run)
    {
        LongTable table = new LongTable();
        await foreach (Columns<StridedK3> batch in file.Scan<StridedK3>())
        {
            ReadOnlySpan<long> keys = batch.K3.Values;
            ReadOnlySpan<long> values = batch.Value.Values;
            for (int i = 0; i < keys.Length; i++)
            {
                ref LongTable.Entry entry = ref table.Find(keys[i]);
                entry.Count++;
                entry.Sum += values[i];
            }
        }

        run.Answer();
        return table.Rows();
    }

    /// <summary>Linear probing on a Fibonacci hash, at most half full, the states in the entry.</summary>
    private sealed class IntTable
    {
        private Entry[] _entries = new Entry[1 << 4];
        private int _count;

        internal struct Entry
        {
            public int Key;
            public bool Used;
            public long Count;
            public long Sum;
        }

        internal ref Entry Find(int key)
        {
            Entry[] entries = _entries;
            int mask = entries.Length - 1;
            int slot = (int)(((uint)key * 0x9E37_79B9u) >> (32 - System.Numerics.BitOperations.Log2((uint)entries.Length)));
            while (true)
            {
                ref Entry entry = ref entries[slot];
                if (entry.Used)
                {
                    if (entry.Key == key)
                    {
                        return ref entry;
                    }

                    slot = (slot + 1) & mask;
                    continue;
                }

                if (2 * (_count + 1) > entries.Length)
                {
                    Grow();
                    return ref Find(key);
                }

                entry.Used = true;
                entry.Key = key;
                _count++;
                return ref entry;
            }
        }

        internal long Rows()
        {
            long rows = 0;
            foreach (Entry entry in _entries)
            {
                rows += entry.Count;
            }

            return rows;
        }

        private void Grow()
        {
            Entry[] old = _entries;
            _entries = new Entry[old.Length * 2];
            _count = 0;
            foreach (Entry entry in old)
            {
                if (entry.Used)
                {
                    ref Entry moved = ref Find(entry.Key);
                    moved.Count = entry.Count;
                    moved.Sum = entry.Sum;
                }
            }
        }
    }

    /// <summary>The same table for a key of 64 bits.</summary>
    private sealed class LongTable
    {
        private Entry[] _entries = new Entry[1 << 4];
        private int _count;

        internal struct Entry
        {
            public long Key;
            public bool Used;
            public long Count;
            public long Sum;
        }

        internal ref Entry Find(long key)
        {
            Entry[] entries = _entries;
            int mask = entries.Length - 1;
            int slot = (int)(((ulong)key * 0x9E37_79B9_7F4A_7C15UL) >> (64 - System.Numerics.BitOperations.Log2((uint)entries.Length)));
            while (true)
            {
                ref Entry entry = ref entries[slot];
                if (entry.Used)
                {
                    if (entry.Key == key)
                    {
                        return ref entry;
                    }

                    slot = (slot + 1) & mask;
                    continue;
                }

                if (2 * (_count + 1) > entries.Length)
                {
                    Grow();
                    return ref Find(key);
                }

                entry.Used = true;
                entry.Key = key;
                _count++;
                return ref entry;
            }
        }

        internal long Rows()
        {
            long rows = 0;
            foreach (Entry entry in _entries)
            {
                rows += entry.Count;
            }

            return rows;
        }

        private void Grow()
        {
            Entry[] old = _entries;
            _entries = new Entry[old.Length * 2];
            _count = 0;
            foreach (Entry entry in old)
            {
                if (entry.Used)
                {
                    ref Entry moved = ref Find(entry.Key);
                    moved.Count = entry.Count;
                    moved.Sum = entry.Sum;
                }
            }
        }
    }
}

/// <summary>The key of a thousand values of the matrix in no order, and the value: the two columns the hand kernel reads.</summary>
[VortexRecord]
public partial record struct SpreadK3(int K3, long Value);

/// <summary>The key of a thousand values at a stride, and the value.</summary>
[VortexRecord]
public partial record struct StridedK3(long K3, long Value);
