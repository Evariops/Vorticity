using System;
using System.IO;
using System.Threading.Tasks;

namespace Vorticity.Benchmarks.Queries;

/// <summary>
/// The files the queries read, written once by this library's writer and kept under
/// ~/.cache/vorticity/queries: the same bytes on every run, so two runs compare the code and not the
/// data.
/// </summary>
internal static class Fixtures
{
    internal static readonly string[] Cities = ["Paris", "Lyon", "Marseille", "Toulouse", "Nice", "Nantes", "Strasbourg", "Lille"];

    internal static readonly string[] Endpoints =
    [
        "/api/users", "/api/orders", "/api/cart", "/api/search", "/api/login", "/api/logout", "/api/items",
        "/api/payments", "/api/shipping", "/api/reviews", "/api/stock", "/api/admin", "/health", "/metrics",
    ];

    /// <summary>The distinct users of the request log: a key that rarely repeats in a batch.</summary>
    internal const int Users = 1_000_000;

    private static readonly DateTime Start = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static string Directory
    {
        get
        {
            string root = Environment.GetEnvironmentVariable("VORTICITY_QUERIES_CORPUS")
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".cache", "vorticity", "queries");
            System.IO.Directory.CreateDirectory(root);
            return root;
        }
    }

    /// <summary>The readings file of <paramref name="rows"/> rows, written on first use.</summary>
    internal static async ValueTask<string> ReadingsAsync(int rows)
    {
        string path = Path.Combine(Directory, $"readings-{rows}.vortex");
        if (System.IO.File.Exists(path))
        {
            return path;
        }

        string partial = path + ".partial";
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Reading>(partial))
        {
            Reading[] block = new Reading[writer.BlockRows];
            for (int first = 0; first < rows; first += block.Length)
            {
                int count = Math.Min(block.Length, rows - first);
                for (int i = 0; i < count; i++)
                {
                    int row = first + i;
                    block[i] = new Reading(row / 1_000, row % 50 == 0 ? null : 10.0 + (row % 400 / 10.0), Cities[row / 7 % Cities.Length]);
                }

                await writer.WriteAsync<Reading>(block.AsSpan(0, count)).ConfigureAwait(false);
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }

        System.IO.File.Move(partial, path, overwrite: true);
        return path;
    }

    /// <summary>The request log of <paramref name="rows"/> rows, one a second, written on first use.</summary>
    internal static async ValueTask<string> RequestsAsync(int rows)
    {
        string path = Path.Combine(Directory, $"requests-{rows}.vortex");
        if (System.IO.File.Exists(path))
        {
            return path;
        }

        string partial = path + ".partial";
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<Request>(partial))
        {
            Request[] block = new Request[writer.BlockRows];
            for (int first = 0; first < rows; first += block.Length)
            {
                int count = Math.Min(block.Length, rows - first);
                for (int i = 0; i < count; i++)
                {
                    int row = first + i;
                    ulong mix = Mix((ulong)row);
                    int duration = (int)(mix % 2_000);
                    block[i] = new Request(
                        Start.AddSeconds(row),
                        (int)((mix >> 20) % Users),
                        Endpoints[(int)((mix >> 40) % (ulong)Endpoints.Length)],
                        (mix >> 50) % 50 == 0 ? 500 : 200,
                        duration,
                        duration + ((mix >> 8) % 1_000 / 1_000.0));
                }

                await writer.WriteAsync<Request>(block.AsSpan(0, count)).ConfigureAwait(false);
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }

        System.IO.File.Move(partial, path, overwrite: true);
        return path;
    }

    /// <summary>
    /// Keys of 4, 100, 1 000, 100 000 and 1 000 000 values in a scattered order, stored canonical:
    /// what a table of keys, a merge and a top-k on the key are judged on, whatever the encodings.
    /// The two largest are a permutation of the row numbers, each value as many times as the others.
    /// </summary>
    internal static ValueTask<string> DrawsAsync(int rows) => WriteOnceAsync($"draws-{rows}.vortex", rows, canonical: true, row =>
    {
        ulong mix = Mix((ulong)row);
        int scattered = (int)((long)row * 7_919 % 1_000_000);
        return new Draw(
            (int)(mix % 4), (int)((mix >> 8) % 100), (int)((mix >> 16) % 1_000), scattered % 100_000, scattered,
            (long)((mix >> 24) % 10_000), (mix >> 12) % 100_000 / 100.0);
    });

    /// <summary>A key one row in three holds, the other rows over a million rare keys: a hot key and a long tail.</summary>
    internal static ValueTask<string> SkewedAsync(int rows) => WriteOnceAsync($"skewed-{rows}.vortex", rows, canonical: true, row =>
    {
        ulong mix = Mix((ulong)row);
        return new Keyed(mix % 10 < 3 ? 0 : 1 + (int)((mix >> 8) % 1_000_000), (long)((mix >> 32) % 1_000));
    });

    /// <summary>Two hundred thousand keys, each seen about twenty times: the cardinality where routing a key costs at each of its rows.</summary>
    internal static ValueTask<string> MediumAsync(int rows) => WriteOnceAsync($"medium-{rows}.vortex", rows, canonical: true, row =>
    {
        ulong mix = Mix((ulong)row);
        return new Keyed((int)((mix >> 8) % 200_000), (long)((mix >> 32) % 1_000));
    });

    /// <summary>A text key of a million values, each as often as the others, in a scattered order, stored canonical.</summary>
    internal static ValueTask<string> NamesAsync(int rows) => WriteOnceAsync($"names-{rows}.vortex", rows, canonical: true, row =>
        new Named(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"user-{(long)row * 7_919 % 1_000_000:D7}"), (long)(Mix((ulong)row) % 1_000)));

    /// <summary>
    /// The readings of the readings file of <paramref name="rows"/> rows as a dataset of
    /// <paramref name="objects"/> objects of equal rows, written on first use in a directory; with
    /// <paramref name="deleted"/>, Paris's rows deleted, an eighth of every object.
    /// </summary>
    internal static async ValueTask<string> ReadingsDatasetAsync(int rows, int objects, bool deleted)
    {
        string path = Path.Combine(Directory, $"readings-{rows}-{objects}{(deleted ? "-deleted" : string.Empty)}.dataset");
        if (System.IO.Directory.Exists(path))
        {
            return path;
        }

        string partial = path + ".partial";
        if (System.IO.Directory.Exists(partial))
        {
            System.IO.Directory.Delete(partial, recursive: true);
        }

        string file = await ReadingsAsync(rows).ConfigureAwait(false);
        await using (Vorticity.Dataset.FileObjectStore store = new Vorticity.Dataset.FileObjectStore(partial))
        {
            await using Vorticity.Dataset.VortexDataset dataset = await Vorticity.Dataset.VortexDataset.CreateAsync(
                store, Reading.Schema, new Vorticity.Dataset.DatasetOptions()).ConfigureAwait(false);
            await using VortexFile open = await VortexFile.OpenAsync(file).ConfigureAwait(false);
            for (int o = 0; o < objects; o++)
            {
                RowRange slice = new RowRange(rows * (long)o / objects, rows * (long)(o + 1) / objects);
                await dataset.AppendAsync(open.Scan<Reading>().Rows(slice).ToBatchesAsync()).ConfigureAwait(false);
            }

            if (deleted)
            {
                await dataset.DeleteAsync<Reading>(r => r.City == "Paris").ConfigureAwait(false);
            }
        }

        System.IO.Directory.Move(partial, path);
        return path;
    }

    /// <summary>The file <paramref name="name"/> of <paramref name="rows"/> rows made by <paramref name="row"/>, written on first use; every column canonical when <paramref name="canonical"/>.</summary>
    private static async ValueTask<string> WriteOnceAsync<T>(string name, int rows, bool canonical, Func<int, T> row)
        where T : IVortexRecord<T>
    {
        string path = Path.Combine(Directory, name);
        if (System.IO.File.Exists(path))
        {
            return path;
        }

        string partial = path + ".partial";
        VortexWriteOptions options = canonical ? new VortexWriteOptions { Compression = CompressionProfile.None } : new VortexWriteOptions();
        await using (VortexFileWriter writer = VortexSession.Default.CreateWriter<T>(partial, options))
        {
            T[] block = new T[writer.BlockRows];
            for (int first = 0; first < rows; first += block.Length)
            {
                int count = Math.Min(block.Length, rows - first);
                for (int i = 0; i < count; i++)
                {
                    block[i] = row(first + i);
                }

                await writer.WriteAsync<T>(block.AsSpan(0, count)).ConfigureAwait(false);
            }

            await writer.CompleteAsync().ConfigureAwait(false);
        }

        System.IO.File.Move(partial, path, overwrite: true);
        return path;
    }

    /// <summary>SplitMix64: a fixed, well-spread stream from the row number.</summary>
    private static ulong Mix(ulong x)
    {
        x += 0x9E3779B97F4A7C15UL;
        x = (x ^ (x >> 30)) * 0xBF58476D1CE4E5B9UL;
        x = (x ^ (x >> 27)) * 0x94D049BB133111EBUL;
        return x ^ (x >> 31);
    }
}
