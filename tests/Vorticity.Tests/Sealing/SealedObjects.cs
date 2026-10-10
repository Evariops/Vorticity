using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Vorticity.IO;
using Vorticity.Sealing;
using Vorticity.Tests.Api;
using Vorticity.Types;
using Vorticity.Writing;

namespace Vorticity.Tests.Sealing;

/// <summary>Seals and opens objects through the internal stages, without a session.</summary>
internal static class SealedObjects
{
    /// <summary>A keyring over one fixed key, <c>test-key</c>.</summary>
    internal static VortexKeyring Keyring() => VortexKeyring.FromKeys(new VortexKey("test-key", Key(1)));

    /// <summary>32 bytes, all <paramref name="fill"/>.</summary>
    internal static byte[] Key(byte fill)
    {
        byte[] key = new byte[32];
        Array.Fill(key, fill);
        return key;
    }

    /// <summary>Seals <paramref name="plaintext"/>, written in pieces of <paramref name="chunk"/> bytes.</summary>
    internal static async Task<byte[]> SealAsync(
        ReadOnlyMemory<byte> plaintext, DataKey key, SealParameters parameters, int chunk, CancellationToken cancellationToken)
    {
        Pipe pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        SealingSegmentSink sink = new SealingSegmentSink(pipe.Writer, parameters, _ => new ValueTask<DataKey>(key));
        try
        {
            for (int at = 0; at < plaintext.Length; at += chunk)
            {
                await sink.WriteAsync(plaintext.Slice(at, Math.Min(chunk, plaintext.Length - at)), cancellationToken);
            }

            await sink.FinishAsync(cancellationToken);
            await sink.FlushAsync(cancellationToken);
            await pipe.Writer.CompleteAsync();
            return await DrainAsync(pipe.Reader, cancellationToken);
        }
        finally
        {
            sink.Release();
        }
    }

    /// <summary>Writes <paramref name="rows"/> as a Vortex file through a sealing stage.</summary>
    internal static async Task<byte[]> WriteAsync(
        IReadOnlyList<Reading> rows, DataKey key, SealParameters parameters, CancellationToken cancellationToken)
    {
        Pipe pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 0, resumeWriterThreshold: 0));
        SealingSegmentSink sink = new SealingSegmentSink(pipe.Writer, parameters, _ => new ValueTask<DataKey>(key));
        await using (VortexFileWriter writer = VortexFileWriter.Create(sink, VortexTypes.ToDType(Reading.Schema, new DTypeArena()), VortexWriteOptions.Default))
        {
            writer.Declare(Reading.Schema);
            Reading[] all = [.. rows];
            await writer.WriteAsync<Reading>(all, cancellationToken);
            await writer.CompleteAsync(cancellationToken);
        }

        await pipe.Writer.CompleteAsync();
        return await DrainAsync(pipe.Reader, cancellationToken);
    }

    /// <summary>Opens <paramref name="sealedBytes"/> with <paramref name="keyring"/>, the keys it unwraps kept in <paramref name="keys"/>.</summary>
    internal static ValueTask<SealedSegmentReader> OpenAsync(byte[] sealedBytes, VortexKeyring keyring, List<DataKey> keys, CancellationToken cancellationToken) =>
        SealedSegmentReader.OpenAsync(
            new MemorySegmentSource(sealedBytes),
            ownsInner: true,
            async (descriptor, ct) =>
            {
                DataKey key = await keyring.UnwrapAsync(descriptor.KeyId, descriptor.WrappedKey, descriptor.KeyContext, ct);
                lock (keys)
                {
                    keys.Add(key);
                }

                return key;
            },
            cancellationToken);

    /// <summary>The whole plaintext of <paramref name="sealedBytes"/>, read as one range.</summary>
    internal static async Task<byte[]> OpenAllAsync(byte[] sealedBytes, VortexKeyring keyring, CancellationToken cancellationToken)
    {
        List<DataKey> keys = [];
        try
        {
            await using SealedSegmentReader reader = await OpenAsync(sealedBytes, keyring, keys, cancellationToken);
            long length = await reader.GetLengthAsync(cancellationToken);
            if (length == 0)
            {
                return [];
            }

            using Vorticity.Buffers.SegmentOwner all = await reader.ReadRangeAsync(0, checked((int)length), 1, cancellationToken);
            return all.Buffer.Span.ToArray();
        }
        finally
        {
            foreach (DataKey key in keys)
            {
                key.Dispose();
            }
        }
    }

    /// <summary>The rows of the sealed Vortex file <paramref name="sealedBytes"/>.</summary>
    internal static async Task<List<Reading>> ReadRowsAsync(byte[] sealedBytes, VortexKeyring keyring, CancellationToken cancellationToken)
    {
        List<DataKey> keys = [];
        try
        {
            SealedSegmentReader reader = await OpenAsync(sealedBytes, keyring, keys, cancellationToken);
            await using VortexFile file = await VortexFile.OpenAsync(reader, VortexOpenOptions.Default, cancellationToken);
            List<Reading> rows = [];
            await foreach (Reading row in file.Scan<Reading>().ToRecordsAsync(cancellationToken))
            {
                rows.Add(row);
            }

            return rows;
        }
        finally
        {
            foreach (DataKey key in keys)
            {
                key.Dispose();
            }
        }
    }

    /// <summary>Rows whose bytes do not repeat much, so that the file is not tiny.</summary>
    internal static List<Reading> Rows(int count)
    {
        string[] cities = ["Paris", "Lyon", "Nantes", "Lille", "Rennes", "Brest", "Nice", "Metz"];
        List<Reading> rows = new List<Reading>(count);
        for (int i = 0; i < count; i++)
        {
            rows.Add(new Reading(i, i % 50 == 0 ? null : ((i * 7919) % 4000) / 100.0, cities[(i * 31) % cities.Length]));
        }

        return rows;
    }

    /// <summary>Bytes that differ at every offset within a frame: <c>i * 31 + i / 251</c>, truncated.</summary>
    internal static byte[] Pattern(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31) + (i / 251));
        }

        return bytes;
    }

    private static async Task<byte[]> DrainAsync(PipeReader reader, CancellationToken cancellationToken)
    {
        using MemoryStream all = new MemoryStream();
        while (true)
        {
            ReadResult result = await reader.ReadAsync(cancellationToken);
            foreach (ReadOnlyMemory<byte> segment in result.Buffer)
            {
                all.Write(segment.Span);
            }

            reader.AdvanceTo(result.Buffer.End);
            if (result.IsCompleted)
            {
                break;
            }
        }

        await reader.CompleteAsync();
        return all.ToArray();
    }
}
