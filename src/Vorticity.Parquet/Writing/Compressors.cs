using System.Collections.Generic;
using System.Threading;
using Vorticity.Writing;
using Vorticity.Zstd;

namespace Vorticity.Parquet.Writing;

/// <summary>
/// The zstd compressors a writer's columns compress their pages with: one for each page being
/// compressed at once, rented for the page and given back after, so that columns closing their pages
/// on the writer's threads never share one.
/// </summary>
/// <remarks>
/// A compressor writes whole frames, one a call, each the same bytes whatever it compressed before,
/// so which compressor a page takes changes nothing of the file. At the default level they come from
/// the writers' shared pool and go back to it with the writer.
/// </remarks>
internal sealed class Compressors(int level)
{
    private readonly Stack<ZstdCompressor> _free = new();
    private readonly Lock _gate = new();

    /// <summary>A compressor at the writer's level, which <see cref="Return"/> takes back.</summary>
    internal ZstdCompressor Rent()
    {
        lock (_gate)
        {
            if (_free.Count > 0)
            {
                return _free.Pop();
            }
        }

        return level == ZstdCompressor.DefaultLevel ? ZstdEncoders.Rent() : new ZstdCompressor(level);
    }

    /// <summary>Takes back a compressor <see cref="Rent"/> gave.</summary>
    internal void Return(ZstdCompressor compressor)
    {
        lock (_gate)
        {
            _free.Push(compressor);
        }
    }

    /// <summary>Gives the compressors of the default level back to the writers' pool, as the writer closes.</summary>
    internal void Release()
    {
        lock (_gate)
        {
            if (level == ZstdCompressor.DefaultLevel)
            {
                foreach (ZstdCompressor compressor in _free)
                {
                    ZstdEncoders.Return(compressor);
                }
            }

            _free.Clear();
        }
    }
}
