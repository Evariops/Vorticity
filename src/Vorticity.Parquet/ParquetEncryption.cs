using System;
using System.Collections.Generic;

namespace Vorticity.Parquet;

/// <summary>The standard's algorithms of modular encryption.</summary>
public enum ParquetEncryptionAlgorithm
{
    /// <summary><c>AES_GCM_V1</c>: every module AES-GCM, authenticated.</summary>
    AesGcm,

    /// <summary>
    /// <c>AES_GCM_CTR_V1</c>: pages in AES counter mode, which authenticates nothing, and every other
    /// module AES-GCM.
    /// </summary>
    AesGcmCtr,
}

/// <summary>A column's key, and what the file stores to name it to its readers.</summary>
/// <param name="Key">16, 24 or 32 bytes; empty to encrypt the column with the footer's key.</param>
/// <param name="KeyMetadata">What a reader's key resolver is given to find the key; empty to store nothing.</param>
public readonly record struct ParquetColumnKey(ReadOnlyMemory<byte> Key, ReadOnlyMemory<byte> KeyMetadata);

/// <summary>
/// How a file is written under the standard's modular encryption: its footer's key, the columns it
/// encrypts and with what keys, its footer encrypted or signed in plaintext, and the AAD prefix that
/// names it.
/// </summary>
/// <remarks>
/// Without <see cref="ColumnKeys"/>, every column is encrypted with the footer's key; with them, the
/// columns they name alone are, the others written in plaintext. A plaintext footer lets a reader
/// older than encryption read the plaintext columns, and holds the encrypted columns' statistics in
/// their own encrypted metadata.
/// </remarks>
public sealed record ParquetEncryption
{
    /// <summary>The footer's key: 16, 24 or 32 bytes, which encrypts or signs the footer.</summary>
    public required ReadOnlyMemory<byte> FooterKey { get; init; }

    /// <summary>What the file stores to name the footer's key; empty to store nothing.</summary>
    public ReadOnlyMemory<byte> FooterKeyMetadata { get; init; }

    /// <summary>The columns encrypted, by path, their names joined by dots, with their keys; null to encrypt every column with the footer's.</summary>
    public IReadOnlyDictionary<string, ParquetColumnKey>? ColumnKeys { get; init; }

    /// <summary>The algorithm: <see cref="ParquetEncryptionAlgorithm.AesGcm"/> by default.</summary>
    public ParquetEncryptionAlgorithm Algorithm { get; init; }

    /// <summary>Whether the footer is written in plaintext, signed, rather than encrypted.</summary>
    public bool PlaintextFooter { get; init; }

    /// <summary>The file's AAD prefix, which a reader may be held to; empty for none.</summary>
    public ReadOnlyMemory<byte> AadPrefix { get; init; }

    /// <summary>Whether the file stores <see cref="AadPrefix"/>; when not, its readers must supply it. True by default.</summary>
    public bool StoreAadPrefix { get; init; } = true;
}
