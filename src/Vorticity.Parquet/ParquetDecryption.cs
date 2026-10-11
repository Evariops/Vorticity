using System;
using System.Collections.Generic;

namespace Vorticity.Parquet;

/// <summary>A key an encrypted file asks for: its footer's, or a column's, named by the metadata its writer stored for it.</summary>
/// <param name="Column">The column's path, its names joined by dots; null for the footer's key.</param>
/// <param name="KeyMetadata">What the writer stored to name the key; empty when it stored nothing.</param>
public readonly record struct ParquetKeyRequest(string? Column, ReadOnlyMemory<byte> KeyMetadata);

/// <summary>Finds the key a request names: 16, 24 or 32 bytes, or none when it does not know it.</summary>
/// <param name="request">The key asked for.</param>
/// <returns>The key, or empty.</returns>
public delegate ReadOnlyMemory<byte> ParquetKeyResolver(ParquetKeyRequest request);

/// <summary>
/// How a file encrypted by the standard's modular encryption is read: the keys of its footer and its
/// columns, and the identity it is held to.
/// </summary>
/// <remarks>
/// A file whose footer is in plaintext reads without any: its plaintext columns read as any file's,
/// and a scan of an encrypted one fails until its key is given. Given this, the open holds a plaintext
/// footer to its signature, which needs the footer's key.
/// </remarks>
public sealed record ParquetDecryption
{
    /// <summary>The key of the footer, and of the columns encrypted with it; empty to resolve it.</summary>
    public ReadOnlyMemory<byte> FooterKey { get; init; }

    /// <summary>The keys of the columns encrypted with their own, by path, their names joined by dots; a column missing resolves its key.</summary>
    public IReadOnlyDictionary<string, ReadOnlyMemory<byte>>? ColumnKeys { get; init; }

    /// <summary>Finds a key the properties above do not give, by the metadata the file stores for it.</summary>
    public ParquetKeyResolver? KeyResolver { get; init; }

    /// <summary>
    /// The file's AAD prefix: what a file that does not store its own needs to be read, and what one
    /// that does is held to. Empty to take the file's.
    /// </summary>
    public ReadOnlyMemory<byte> AadPrefix { get; init; }

    /// <summary>Whether a plaintext footer is held to its signature, which needs the footer's key: true by default.</summary>
    public bool VerifyFooterSignature { get; init; } = true;
}
