using System;
using System.Threading.Tasks;
using Vorticity.IO;
using static Vorticity.Tests.IO.IoTestData;

namespace Vorticity.Tests.IO;

/// <summary>Which <see cref="ISegmentReader"/> a shared contract test is running against.</summary>
public enum SegmentSourceKind
{
    /// <summary><see cref="MemoryMappedSegmentSource"/> — zero-copy over a mapping.</summary>
    MemoryMapped,

    /// <summary><see cref="FileSegmentSource"/> — positional reads into aligned buffers.</summary>
    RandomAccess,

    /// <summary><see cref="HttpRangeSegmentSource"/> — the reference out-of-core implementation.</summary>
    HttpRange,
}

/// <summary>
/// Builds any of the three sources over the same bytes, so one suite can assert that all three
/// honour the same contract. That the test double passes the identical suite is the executable
/// proof that the seam holds for an implementation outside the core.
/// </summary>
internal sealed class SegmentSourceHarness : IAsyncDisposable
{
    private readonly TempFile? _file;

    internal SegmentSourceHarness(SegmentSourceKind kind, byte[] content, SegmentReadOptions? options = null)
    {
        Content = content;

        switch (kind)
        {
            case SegmentSourceKind.MemoryMapped:
                _file = new TempFile(content);
                Source = MemoryMappedSegmentSource.Open(_file.Path_);
                break;

            case SegmentSourceKind.RandomAccess:
                _file = new TempFile(content);
                Source = options is null
                    ? FileSegmentSource.Open(_file.Path_)
                    : new FileSegmentSource(
                        System.IO.File.OpenHandle(
                            _file.Path_,
                            System.IO.FileMode.Open,
                            System.IO.FileAccess.Read,
                            System.IO.FileShare.Read,
                            System.IO.FileOptions.Asynchronous),
                        ownsHandle: true,
                        options);
                break;

            case SegmentSourceKind.HttpRange:
                Transport = new InMemoryRangeTransport(content);
                Source = new HttpRangeSegmentSource(Transport, options);
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(kind), kind, null);
        }
    }

    internal ISegmentReader Source { get; }

    internal byte[] Content { get; }

    /// <summary>Non-null only for <see cref="SegmentSourceKind.HttpRange"/>.</summary>
    internal InMemoryRangeTransport? Transport { get; }

    public async ValueTask DisposeAsync()
    {
        await Source.DisposeAsync().ConfigureAwait(false);
        _file?.Dispose();
    }
}
