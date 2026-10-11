using System;
using Vorticity.IO;

namespace Vorticity.ObjectStorage.Http;

/// <summary>
/// The refusals every store words the same way as the core's stores do, written here so that the
/// package rests on the core's public surface only.
/// </summary>
internal static class StoreFailures
{
    /// <summary>The refusal of a read past an object's end.</summary>
    internal static ArgumentOutOfRangeException PastEnd(string key, long offset, long length) =>
        new ArgumentOutOfRangeException(nameof(offset), offset, $"'{key}' holds {length} bytes, so the range starts past its end.");

    /// <summary>The refusal of a read whose token condition failed: the object changed under its reader.</summary>
    internal static VortexFormatException Changed(string key, string expected, string actual) =>
        new VortexFormatException($"'{key}' changed under this reader: it was {expected} and is now {actual}.");
}
