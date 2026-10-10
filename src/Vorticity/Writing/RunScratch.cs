using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace Vorticity.Writing;

/// <summary>
/// An append-only byte store, in memory up to a budget and on disk beyond it. The synchronous
/// members serve it while it is in memory; its file is read and written asynchronously.
/// </summary>
/// <remarks>
/// A store whose session seals its files seals its file too: frames of 64 KiB under AES-256-GCM,
/// a key drawn for the file and held in memory only, so that after a crash the file is unreadable,
/// which is what scratch should be. Each frame is written once, when it is full, under a nonce made of
/// its index; the frame being filled stays in memory, and a read of it is served from there.
/// </remarks>
internal sealed class RunScratch : IDisposable
{
    internal const int PageBytes = 1 << 20;

    private readonly long _memoryBudget;
    private readonly string _directory;
    private readonly List<byte[]> _pages = [];
    private readonly bool _sealFile;

    /// <summary>The file's frames, once it is on disk and sealed.</summary>
    private ScratchFrames? _frames;

    /// <summary>The stream that created the file and owns its handle.</summary>
    private FileStream? _stream;

    /// <summary>The file's handle, which every read and write goes through.</summary>
    private SafeFileHandle? _file;
    private long _length;
    private long _admitted;

    /// <summary>
    /// Whether runs held outside the store may stay in memory, charged against the same budget as
    /// its pages. A refusal moves the store to disk, and once it is there, nothing more is admitted.
    /// </summary>
    internal async ValueTask<bool> AdmitAsync(long bytes, CancellationToken cancellationToken)
    {
        if (Fits(bytes))
        {
            _admitted += bytes;
            return true;
        }

        await SpillAsync(cancellationToken).ConfigureAwait(false);
        return false;
    }

    /// <summary>Gives back bytes <see cref="AdmitAsync"/> granted.</summary>
    internal void Release(long bytes) => _admitted = Math.Max(0, _admitted - bytes);

    /// <summary>A null directory spills to the system's temporary directory; <paramref name="sealFile"/> seals the file it spills to.</summary>
    internal RunScratch(long memoryBudget, string? directory, bool sealFile = false)
    {
        _memoryBudget = Math.Max(memoryBudget, 0);
        _directory = directory ?? Path.GetTempPath();
        _sealFile = sealFile;
    }

    /// <summary>Whether the store's file, once it has one, is sealed.</summary>
    internal bool SealsFile => _sealFile;

    /// <summary>The store's file once it is on disk, for a test that reads what lies there.</summary>
    internal SafeFileHandle? FileHandle => _file;

    internal long Length => _length;

    /// <summary>Whether the store has moved to its file.</summary>
    internal bool OnDisk => _file is not null;

    /// <summary>Whether bytes appended now stay in memory.</summary>
    internal bool Fits(long bytes) => _file is null && _admitted + _length + bytes <= _memoryBudget;

    /// <summary>Appends bytes that <see cref="Fits"/> accepts and returns where they start.</summary>
    internal long Append(ReadOnlySpan<byte> bytes)
    {
        long offset = _length;
        if (bytes.IsEmpty)
        {
            return offset;
        }

        if (!Fits(bytes.Length))
        {
            throw new InvalidOperationException("The scratch cannot keep these bytes in memory; they go to its file asynchronously.");
        }

        while (!bytes.IsEmpty)
        {
            int page = (int)(_length / PageBytes);
            int at = (int)(_length % PageBytes);
            if (page == _pages.Count)
            {
                _pages.Add(ArrayPool<byte>.Shared.Rent(PageBytes));
            }

            int take = Math.Min(PageBytes - at, bytes.Length);
            bytes[..take].CopyTo(_pages[page].AsSpan(at));
            bytes = bytes[take..];
            _length += take;
        }

        return offset;
    }

    /// <summary>Appends values that <see cref="Fits"/> accepts, in the platform's byte order, and returns where they start.</summary>
    internal long Append<T>(ReadOnlySpan<T> values)
        where T : unmanaged =>
        Append(MemoryMarshal.AsBytes(values));

    /// <summary>
    /// Appends the bytes and returns where they start: in memory while they fit, completing at
    /// once, and in the file past that, the store moving there first.
    /// </summary>
    internal ValueTask<long> AppendAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken) =>
        bytes.IsEmpty || Fits(bytes.Length)
            ? new ValueTask<long>(Append(bytes.Span))
            : AppendToFileAsync(bytes, cancellationToken);

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder<>))]
    private async ValueTask<long> AppendToFileAsync(ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        await SpillAsync(cancellationToken).ConfigureAwait(false);
        long offset = _length;
        if (_frames is { } frames)
        {
            await frames.AppendAsync(_file!, bytes, cancellationToken).ConfigureAwait(false);
        }
        else
        {
            await RandomAccess.WriteAsync(_file!, bytes, offset, cancellationToken).ConfigureAwait(false);
        }

        _length += bytes.Length;
        return offset;
    }

    /// <summary>Reads bytes of the store while it is in memory.</summary>
    internal void Read(long offset, Span<byte> destination)
    {
        CheckRange(offset, destination.Length);
        if (_file is not null)
        {
            throw new InvalidOperationException("The scratch is on disk; its file is read asynchronously.");
        }

        while (!destination.IsEmpty)
        {
            int page = (int)(offset / PageBytes);
            int at = (int)(offset % PageBytes);
            int take = Math.Min(PageBytes - at, destination.Length);
            _pages[page].AsSpan(at, take).CopyTo(destination);
            destination = destination[take..];
            offset += take;
        }
    }

    /// <summary>Reads values of the store while it is in memory.</summary>
    internal void Read<T>(long offset, Span<T> destination)
        where T : unmanaged =>
        Read(offset, MemoryMarshal.AsBytes(destination));

    /// <summary>Reads bytes of the store: from memory, completing at once, or from its file.</summary>
    internal ValueTask ReadAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        if (_file is null)
        {
            Read(offset, destination.Span);
            return ValueTask.CompletedTask;
        }

        CheckRange(offset, destination.Length);
        return _frames is { } frames
            ? frames.ReadAsync(_file!, offset, destination, cancellationToken)
            : ReadFileAsync(offset, destination, cancellationToken);
    }

    [AsyncMethodBuilder(typeof(PoolingAsyncValueTaskMethodBuilder))]
    private async ValueTask ReadFileAsync(long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        while (!destination.IsEmpty)
        {
            int read = await RandomAccess.ReadAsync(_file!, destination, offset, cancellationToken).ConfigureAwait(false);
            if (read <= 0)
            {
                throw new IOException($"the scratch file ended at {offset}");
            }

            destination = destination[read..];
            offset += read;
        }
    }

    private void CheckRange(long offset, int length)
    {
        if (offset < 0 || offset + length > _length)
        {
            throw new ArgumentOutOfRangeException(
                nameof(offset), $"[{offset}, {offset + length}) is outside the scratch's {_length} bytes");
        }
    }

    /// <summary>Moves the store to its file, page by page; nothing when it is there already.</summary>
    private async ValueTask SpillAsync(CancellationToken cancellationToken)
    {
        if (_file is not null)
        {
            return;
        }

        // The keys a spill holds are column values, so the file is its owner's alone, and set so by a
        // stream, the one way to give a mode as the file is created. Outside Windows, which deletes it
        // however the process ends, its name goes as soon as it is open: a killed process never
        // reaches the close that would delete it.
        string path = Path.Combine(_directory, $"vorticity-runs-{Guid.NewGuid():N}.tmp");
        FileStreamOptions options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.ReadWrite,
            Share = FileShare.None,
            Options = FileOptions.Asynchronous,
            BufferSize = 0,
        };
        if (OperatingSystem.IsWindows())
        {
            options.Options |= FileOptions.DeleteOnClose;
        }
        else
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        FileStream stream = new FileStream(path, options);
        SafeFileHandle file = stream.SafeFileHandle;
        try
        {
            if (!OperatingSystem.IsWindows())
            {
                System.IO.File.Delete(path);
            }

            ScratchFrames? frames = _sealFile ? new ScratchFrames() : null;
            try
            {
                long position = 0;
                for (int i = 0; i < _pages.Count; i++)
                {
                    int length = (int)Math.Min(PageBytes, _length - position);
                    if (frames is not null)
                    {
                        await frames.AppendAsync(file, _pages[i].AsMemory(0, length), cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        await RandomAccess.WriteAsync(file, _pages[i].AsMemory(0, length), position, cancellationToken).ConfigureAwait(false);
                    }

                    position += length;
                }
            }
            catch
            {
                frames?.Dispose();
                throw;
            }

            _frames = frames;
        }
        catch
        {
            await stream.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // The pages go only once the file holds them: a spill that fails leaves the store in memory.
        _stream = stream;
        _file = file;
        foreach (byte[] page in _pages)
        {
            ArrayPool<byte>.Shared.Return(page);
        }

        _pages.Clear();
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _stream = null;
        _file = null;
        _frames?.Dispose();
        _frames = null;
        foreach (byte[] page in _pages)
        {
            ArrayPool<byte>.Shared.Return(page, clearArray: _sealFile);
        }

        _pages.Clear();
        _length = 0;
    }
}

/// <summary>
/// A scratch file's frames: AES-256-GCM over 64 KiB of plaintext each, under a key drawn for the file,
/// each written once, at <c>index * (64 KiB + 16)</c>, its tag after it. The frame being filled is
/// held here until it is full, and served from here to a read.
/// </summary>
/// <remarks>
/// One writer appends, as the store's owner does; any number of readers read what is written, each
/// with a cipher instance of its own. The key never leaves the process, and no descriptor is written:
/// nothing outside the process ever reads the file.
/// </remarks>
internal sealed class ScratchFrames : IDisposable
{
    private const int FrameBytes = 1 << Sealing.SealedFormat.DefaultFrameLog2;
    private const int SealedFrameBytes = FrameBytes + Sealing.SealedFormat.TagBytes;

    private readonly Sealing.EpochCipher _cipher;
    private readonly byte[] _pending = GC.AllocateUninitializedArray<byte>(FrameBytes, pinned: true);
    private readonly byte[] _sealed = GC.AllocateUninitializedArray<byte>(SealedFrameBytes, pinned: true);
    private int _filled;
    private long _written;

    internal ScratchFrames()
    {
        Span<byte> key = stackalloc byte[Sealing.SealedFormat.KeyBytes];
        System.Security.Cryptography.RandomNumberGenerator.Fill(key);
        _cipher = new Sealing.EpochCipher(key);
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(key);
    }

    /// <summary>Appends plaintext: each frame it fills is sealed and written, the rest kept for the next append.</summary>
    internal async ValueTask AppendAsync(SafeFileHandle file, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
    {
        while (!bytes.IsEmpty)
        {
            int take = Math.Min(FrameBytes - _filled, bytes.Length);
            bytes.Span[..take].CopyTo(_pending.AsSpan(_filled));
            _filled += take;
            bytes = bytes[take..];
            if (_filled == FrameBytes)
            {
                Seal();
                await RandomAccess.WriteAsync(file, _sealed, _written * SealedFrameBytes, cancellationToken).ConfigureAwait(false);
                _written++;
                _filled = 0;
            }
        }
    }

    /// <summary>Reads plaintext from <paramref name="offset"/>: the frames written, read in one call and opened, and the frame being filled.</summary>
    internal async ValueTask ReadAsync(SafeFileHandle file, long offset, Memory<byte> destination, CancellationToken cancellationToken)
    {
        long writtenBytes = _written * FrameBytes;
        if (offset < writtenBytes && !destination.IsEmpty)
        {
            long first = offset / FrameBytes;
            int count = (int)Math.Min(writtenBytes - offset, destination.Length);
            long last = (offset + count - 1) / FrameBytes;
            int frames = (int)(last - first + 1);
            byte[] sealedRun = ArrayPool<byte>.Shared.Rent(frames * SealedFrameBytes);
            byte[] plain = ArrayPool<byte>.Shared.Rent(FrameBytes);
            try
            {
                Memory<byte> run = sealedRun.AsMemory(0, frames * SealedFrameBytes);
                long at = first * SealedFrameBytes;
                while (!run.IsEmpty)
                {
                    int read = await RandomAccess.ReadAsync(file, run, at, cancellationToken).ConfigureAwait(false);
                    if (read <= 0)
                    {
                        throw new IOException($"the scratch file ended at {at}");
                    }

                    run = run[read..];
                    at += read;
                }

                Open(sealedRun, first, frames, plain, offset, destination.Span[..count]);
            }
            finally
            {
                System.Security.Cryptography.CryptographicOperations.ZeroMemory(plain);
                ArrayPool<byte>.Shared.Return(plain);
                ArrayPool<byte>.Shared.Return(sealedRun);
            }

            offset += count;
            destination = destination[count..];
        }

        // What lies past the frames written is in the frame being filled.
        if (!destination.IsEmpty)
        {
            _pending.AsSpan((int)(offset - writtenBytes), destination.Length).CopyTo(destination.Span);
        }
    }

    public void Dispose()
    {
        _cipher.Dispose();
        System.Security.Cryptography.CryptographicOperations.ZeroMemory(_pending);
    }

    private void Seal()
    {
        System.Security.Cryptography.AesGcm aes = _cipher.Rent();
        try
        {
            Sealing.EpochCipher.Seal(aes, _written, final: false, _pending, _sealed.AsSpan(0, FrameBytes), _sealed.AsSpan(FrameBytes));
        }
        finally
        {
            _cipher.Return(aes);
        }
    }

    /// <summary>Opens <paramref name="frames"/> frames from <paramref name="first"/> and copies out the plaintext from <paramref name="offset"/>.</summary>
    private void Open(byte[] sealedRun, long first, int frames, byte[] plain, long offset, Span<byte> destination)
    {
        System.Security.Cryptography.AesGcm aes = _cipher.Rent();
        try
        {
            for (int f = 0; f < frames; f++)
            {
                long index = first + f;
                Sealing.EpochCipher.Open(
                    aes, index, final: false, sealedRun.AsSpan(f * SealedFrameBytes, FrameBytes),
                    sealedRun.AsSpan((f * SealedFrameBytes) + FrameBytes, Sealing.SealedFormat.TagBytes), plain.AsSpan(0, FrameBytes), index * SealedFrameBytes);
                long frameStart = index * FrameBytes;
                long from = Math.Max(offset, frameStart);
                long to = Math.Min(offset + destination.Length, frameStart + FrameBytes);
                plain.AsSpan((int)(from - frameStart), (int)(to - from)).CopyTo(destination[(int)(from - offset)..]);
            }
        }
        finally
        {
            _cipher.Return(aes);
        }
    }
}
