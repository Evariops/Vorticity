using System;
using System.Buffers;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.Buffers;

namespace Vorticity.IO;

/// <summary>Where a file's bytes come from: a local file, a mapping, memory, or an object store.</summary>
/// <remarks>
/// A scan asks its source for each segment it needs at most once, and asks for the segments of a
/// batch together through <see cref="ReadAsync(ReadOnlyMemory{SegmentRange}, Memory{SegmentLease}, CancellationToken)"/>,
/// so a source that can coalesce (one <c>preadv</c>, one ranged request) sees every range at once.
/// Implementations are thread-safe: the scans of a session share a source.
/// </remarks>
public interface ISegmentSource : IAsyncDisposable
{
    /// <summary>The length of the file in bytes.</summary>
    long Length { get; }

    /// <summary>Reads one range.</summary>
    /// <param name="range">The range, inside the file.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The bytes; the caller disposes the lease.</returns>
    ValueTask<SegmentLease> ReadAsync(SegmentRange range, CancellationToken cancellationToken);

    /// <summary>Reads several ranges, in as few requests as the source can coalesce them into.</summary>
    /// <param name="ranges">The ranges, in any order.</param>
    /// <param name="leases">One lease per range, written at the range's index; the caller disposes every one.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>A task that completes when every lease is written; on failure no lease is left to dispose.</returns>
    ValueTask ReadAsync(ReadOnlyMemory<SegmentRange> ranges, Memory<SegmentLease> leases, CancellationToken cancellationToken);
}

/// <summary>A range of bytes in a file.</summary>
/// <param name="Offset">The first byte.</param>
/// <param name="Length">The number of bytes.</param>
public readonly record struct SegmentRange(long Offset, int Length)
{
    /// <summary>The byte after the last.</summary>
    public long End => Offset + Length;
}

/// <summary>Bytes a source handed out, held until the lease is disposed.</summary>
/// <remarks>
/// <para>
/// A source builds a lease over memory it keeps alive and names what releases it; a lease over
/// memory nothing needs to release takes no owner.
/// </para>
/// <para>
/// A lease is a value, and a copy of it names the same owner: dispose one copy, once.
/// <see cref="Dispose"/> forgets the owner in the copy it is called on only, so disposing a second
/// copy releases the owner again. Once the lease is disposed, its bytes may already serve another
/// read or be unmapped: <see cref="Bytes"/> and <see cref="Memory"/> are not read after, as with
/// the memory of any <see cref="IMemoryOwner{T}"/>.
/// </para>
/// </remarks>
public struct SegmentLease : IDisposable
{
    private IDisposable? _owner;

    /// <summary>A lease over one contiguous block.</summary>
    /// <param name="memory">The bytes.</param>
    /// <param name="owner">What <see cref="Dispose"/> releases, or null.</param>
    public SegmentLease(ReadOnlyMemory<byte> memory, IDisposable? owner = null)
    {
        Bytes = new ReadOnlySequence<byte>(memory);
        _owner = owner;
    }

    /// <summary>A lease over bytes that arrived in pieces.</summary>
    /// <param name="bytes">The bytes.</param>
    /// <param name="owner">What <see cref="Dispose"/> releases, or null.</param>
    public SegmentLease(ReadOnlySequence<byte> bytes, IDisposable? owner = null)
    {
        Bytes = bytes;
        _owner = owner;
    }

    /// <summary>The bytes: a single segment when the source can, several when a response arrived in pieces.</summary>
    public ReadOnlySequence<byte> Bytes { get; }

    /// <summary>Whether the bytes are one block, so <see cref="Memory"/> is valid.</summary>
    public readonly bool IsContiguous => Bytes.IsSingleSegment;

    /// <summary>The bytes as one block.</summary>
    /// <exception cref="InvalidOperationException">The lease is not contiguous.</exception>
    public readonly ReadOnlyMemory<byte> Memory =>
        Bytes.IsSingleSegment ? Bytes.First : throw new InvalidOperationException("The lease holds several blocks; read Bytes.");

    /// <summary>Releases the bytes. Idempotent for this copy of the lease; another copy still names the owner.</summary>
    public void Dispose()
    {
        IDisposable? owner = _owner;
        _owner = null;
        owner?.Dispose();
    }
}

/// <summary>A segment owner seen as <see cref="Memory{T}"/>, for a lease; disposing it releases the owner's reference.</summary>
internal sealed unsafe class SegmentOwnerMemory : MemoryManager<byte>
{
    private SegmentOwner? _owner;
    private readonly VortexBuffer _buffer;

    /// <summary>Takes over one reference to <paramref name="owner"/>, viewing <paramref name="buffer"/>.</summary>
    internal SegmentOwnerMemory(SegmentOwner owner, VortexBuffer buffer)
    {
        _owner = owner;
        _buffer = buffer;
    }

    internal static SegmentLease Lease(SegmentOwner owner) => Lease(owner, owner.Buffer);

    internal static SegmentLease Lease(SegmentOwner owner, VortexBuffer buffer)
    {
        SegmentOwnerMemory memory = new SegmentOwnerMemory(owner, buffer);
        return new SegmentLease(memory.Memory, memory);
    }

    public override Span<byte> GetSpan()
    {
        ReadOnlySpan<byte> span = _buffer.Span;
        return MemoryMarshal.CreateSpan(ref MemoryMarshal.GetReference(span), span.Length);
    }

    public override MemoryHandle Pin(int elementIndex = 0)
    {
        if ((uint)elementIndex > (uint)_buffer.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(elementIndex));
        }

        // Every segment buffer is native or pinned for its owner's life, so its address is stable.
        return new MemoryHandle(Unsafe.AsPointer(ref Unsafe.Add(ref MemoryMarshal.GetReference(_buffer.Span), elementIndex)));
    }

    public override void Unpin()
    {
    }

    protected override void Dispose(bool disposing) => Interlocked.Exchange(ref _owner, null)?.Release();
}
