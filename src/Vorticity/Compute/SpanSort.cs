using System;
using System.Collections.Generic;
using System.Numerics;

namespace Vorticity.Compute;

/// <summary>
/// An in-place sort that allocates nothing. <c>Array.Sort</c> and <c>MemoryExtensions.Sort</c> build a
/// comparison delegate on every call under Native AOT, which on the paths that sort a window's rows,
/// a take's indices or a plan's boundaries is an object per call.
/// </summary>
/// <remarks>
/// An introsort: an insertion sort below sixteen elements, a quicksort on the median of three above
/// it, and a heapsort once the partitions nest deeper than twice the logarithm of the length, so the
/// worst case stays n log n. The comparer is a value type, so each instantiation compiles its
/// comparison inline. Not stable, which no caller needs: what they sort is distinct or totally
/// ordered.
/// </remarks>
internal static class SpanSort
{
    private const int InsertionThreshold = 16;

    /// <summary>Sorts <paramref name="keys"/> ascending.</summary>
    internal static void Sort(Span<long> keys) => Sort(keys, default(Ascending));

    /// <summary>Sorts <paramref name="keys"/> in the order <paramref name="comparer"/> gives.</summary>
    internal static void Sort<T, TComparer>(Span<T> keys, TComparer comparer)
        where TComparer : struct, IComparer<T>
    {
        if (keys.Length > 1)
        {
            IntroSort(keys, comparer, 2 * (BitOperations.Log2((uint)keys.Length) + 1));
        }
    }

    private static void IntroSort<T, TComparer>(Span<T> keys, TComparer comparer, int depthLimit)
        where TComparer : struct, IComparer<T>
    {
        int size = keys.Length;
        while (size > 1)
        {
            if (size <= InsertionThreshold)
            {
                InsertionSort(keys[..size], comparer);
                return;
            }

            if (depthLimit == 0)
            {
                HeapSort(keys[..size], comparer);
                return;
            }

            depthLimit--;
            int pivot = Partition(keys[..size], comparer);

            // The larger side is not guaranteed to be the left one, but the depth limit bounds the
            // recursion either way; the left side is looped on so that one side costs no frame.
            IntroSort(keys[(pivot + 1)..size], comparer, depthLimit);
            size = pivot;
        }
    }

    /// <summary>Partitions around the median of the first, middle and last keys; returns the pivot's place.</summary>
    private static int Partition<T, TComparer>(Span<T> keys, TComparer comparer)
        where TComparer : struct, IComparer<T>
    {
        int high = keys.Length - 1;
        int middle = high >> 1;
        SwapIfGreater(keys, comparer, 0, middle);
        SwapIfGreater(keys, comparer, 0, high);
        SwapIfGreater(keys, comparer, middle, high);

        T pivot = keys[middle];
        Swap(keys, middle, high - 1);
        int left = 0;
        int right = high - 1;
        while (left < right)
        {
            while (comparer.Compare(keys[++left], pivot) < 0)
            {
            }

            while (comparer.Compare(pivot, keys[--right]) < 0)
            {
            }

            if (left >= right)
            {
                break;
            }

            Swap(keys, left, right);
        }

        if (left != high - 1)
        {
            Swap(keys, left, high - 1);
        }

        return left;
    }

    private static void InsertionSort<T, TComparer>(Span<T> keys, TComparer comparer)
        where TComparer : struct, IComparer<T>
    {
        for (int i = 0; i < keys.Length - 1; i++)
        {
            T key = keys[i + 1];
            int j = i;
            while (j >= 0 && comparer.Compare(key, keys[j]) < 0)
            {
                keys[j + 1] = keys[j];
                j--;
            }

            keys[j + 1] = key;
        }
    }

    private static void HeapSort<T, TComparer>(Span<T> keys, TComparer comparer)
        where TComparer : struct, IComparer<T>
    {
        int n = keys.Length;
        for (int i = n >> 1; i >= 1; i--)
        {
            DownHeap(keys, comparer, i, n);
        }

        for (int i = n; i > 1; i--)
        {
            Swap(keys, 0, i - 1);
            DownHeap(keys, comparer, 1, i - 1);
        }
    }

    /// <summary>Sifts the one-based node <paramref name="i"/> down a heap of <paramref name="n"/> keys.</summary>
    private static void DownHeap<T, TComparer>(Span<T> keys, TComparer comparer, int i, int n)
        where TComparer : struct, IComparer<T>
    {
        T key = keys[i - 1];
        while (i <= n >> 1)
        {
            int child = 2 * i;
            if (child < n && comparer.Compare(keys[child - 1], keys[child]) < 0)
            {
                child++;
            }

            if (comparer.Compare(key, keys[child - 1]) >= 0)
            {
                break;
            }

            keys[i - 1] = keys[child - 1];
            i = child;
        }

        keys[i - 1] = key;
    }

    private static void SwapIfGreater<T, TComparer>(Span<T> keys, TComparer comparer, int i, int j)
        where TComparer : struct, IComparer<T>
    {
        if (comparer.Compare(keys[i], keys[j]) > 0)
        {
            Swap(keys, i, j);
        }
    }

    private static void Swap<T>(Span<T> keys, int i, int j) => (keys[i], keys[j]) = (keys[j], keys[i]);

    /// <summary>The natural order of <see cref="long"/>.</summary>
    private readonly struct Ascending : IComparer<long>
    {
        public int Compare(long x, long y) => x.CompareTo(y);
    }
}
