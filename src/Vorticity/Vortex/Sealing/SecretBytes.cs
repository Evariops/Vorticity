using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Threading;
using Vorticity.Buffers;

namespace Vorticity.Sealing;

/// <summary>
/// Key material in a cell of a page of the library's pinned memory (<see cref="SecretPages"/>): the page never moves, so
/// the collector leaves no copy of the key behind in the heap, and the cell is wiped before another key takes it, by
/// <see cref="Dispose"/> or, for a key nobody disposed, by the finalizer.
/// </summary>
/// <remarks>
/// A key once had a pinned array of its own, 32 bytes, for as long as its holder lived: a dataset handle keeps an epoch key
/// for each sealed object it keeps open, and those small arrays, spread over the pinned heap, kept its regions alive after
/// the large blocks beside them were gone (2026-11-24).
/// </remarks>
internal sealed class SecretBytes : IDisposable
{
    // The page and the cell the key lies in; null once wiped.
    private SecretPages.Page? _page;
    private readonly int _cell;

    /// <summary>A copy of <paramref name="secret"/>, of <see cref="SecretPages.CellBytes"/> bytes at most.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The secret is longer than a cell.</exception>
    internal SecretBytes(ReadOnlySpan<byte> secret)
    {
        ArgumentOutOfRangeException.ThrowIfGreaterThan(secret.Length, SecretPages.CellBytes, nameof(secret));
        Length = secret.Length;
        (SecretPages.Page page, int cell) = SecretPages.Take();
        secret.CopyTo(page.Cell(cell));
        _cell = cell;
        _page = page;
    }

    ~SecretBytes() => Wipe();

    /// <summary>How many bytes the secret holds.</summary>
    internal int Length { get; }

    /// <summary>The secret, valid until <see cref="Dispose"/>.</summary>
    /// <exception cref="ObjectDisposedException">The secret was wiped.</exception>
    internal ReadOnlySpan<byte> Span
    {
        get
        {
            SecretPages.Page? page = Volatile.Read(ref _page);
            ObjectDisposedException.ThrowIf(page is null, this);
            return page.Cell(_cell)[..Length];
        }
    }

    /// <summary>Wipes the secret and gives its cell back. Idempotent.</summary>
    public void Dispose()
    {
        Wipe();
        GC.SuppressFinalize(this);
    }

    private void Wipe()
    {
        if (Interlocked.Exchange(ref _page, null) is { } page)
        {
            SecretPages.Give(page, _cell);
        }
    }
}

/// <summary>
/// The pinned pages the secrets lie in: blocks of a page of the library's pool, cut in cells of 32 bytes, a key each. A
/// page goes back to the pool, wiped, once its last cell is given back.
/// </summary>
/// <remarks>
/// Every page with a key in it stays listed here, so that the collector never takes it before the keys in it are wiped: a
/// key and its page dropped together would otherwise have the page's block go back to its slab first, unwiped. One lock
/// covers the pages, taken as a key is made or wiped, never on a read.
/// </remarks>
internal static class SecretPages
{
    /// <summary>The bytes of a cell: a key of the sealed format.</summary>
    internal const int CellBytes = SealedFormat.KeyBytes;

    /// <summary>The bytes of a page: the pool's least block.</summary>
    private const int PageBytes = 4096;

    /// <summary>The cells of a page.</summary>
    private const int Cells = PageBytes / CellBytes;

    private static readonly Lock Gate = new Lock();

    // Every page that holds a key, the first ones filled first.
    private static readonly List<Page> s_pages = [];

    /// <summary>The pages that hold a key now: for the tests.</summary>
    internal static int PageCount
    {
        get
        {
            lock (Gate)
            {
                return s_pages.Count;
            }
        }
    }

    /// <summary>A free cell, of the first page that holds one, or of a new page.</summary>
    internal static (Page Page, int Cell) Take()
    {
        lock (Gate)
        {
            foreach (Page page in s_pages)
            {
                if (page.TryTake() is int cell and >= 0)
                {
                    return (page, cell);
                }
            }

            Page made = new Page(MemoryAccount.Kept.Rent(PageBytes, VortexLimits.MaxAlignment));
            s_pages.Add(made);
            return (made, made.TryTake());
        }
    }

    /// <summary>Wipes <paramref name="cell"/> of <paramref name="page"/> and gives it back: the page goes back to the pool once empty.</summary>
    internal static void Give(Page page, int cell)
    {
        lock (Gate)
        {
            CryptographicOperations.ZeroMemory(page.Cell(cell));
            if (page.Give(cell))
            {
                s_pages.Remove(page);
                page.Block.Release();
            }
        }
    }

    /// <summary>A page: its block, and which of its cells hold a key, a bit each.</summary>
    internal sealed class Page(BlockOwner block)
    {
        private readonly ulong[] _used = new ulong[Cells / 64];
        private int _count;

        /// <summary>The page's block of the pool.</summary>
        internal BlockOwner Block => block;

        /// <summary>The bytes of <paramref name="cell"/>.</summary>
        internal Span<byte> Cell(int cell) => block.WholeSpan.Slice(cell * CellBytes, CellBytes);

        /// <summary>A free cell, marked used; -1 when the page is full. Under the gate.</summary>
        internal int TryTake()
        {
            if (_count == Cells)
            {
                return -1;
            }

            for (int word = 0; word < _used.Length; word++)
            {
                ulong free = ~_used[word];
                if (free != 0)
                {
                    int bit = System.Numerics.BitOperations.TrailingZeroCount(free);
                    _used[word] |= 1UL << bit;
                    _count++;
                    return (word * 64) + bit;
                }
            }

            return -1;
        }

        /// <summary>Marks <paramref name="cell"/> free: whether the page is empty now. Under the gate.</summary>
        internal bool Give(int cell)
        {
            _used[cell / 64] &= ~(1UL << (cell % 64));
            return --_count == 0;
        }
    }
}
