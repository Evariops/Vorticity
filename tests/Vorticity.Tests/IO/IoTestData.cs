using System;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Vorticity.Buffers;
using Vorticity.Serialization.Schemas;

namespace Vorticity.Tests.IO;

/// <summary>Deterministic fixtures for the segment I/O tests: no clock, no unseeded randomness.</summary>
internal static class IoTestData
{
    /// <summary>A byte pattern that makes an off-by-one in an offset visible.</summary>
    internal static byte[] Pattern(int length)
    {
        byte[] bytes = new byte[length];
        for (int i = 0; i < length; i++)
        {
            bytes[i] = (byte)((i * 31) + 7);
        }

        return bytes;
    }

    /// <summary>Builds a spec, defaulting the two reserved fields.</summary>
    internal static SegmentSpec Spec(ulong offset, uint length, byte alignmentExponent = 0) =>
        new SegmentSpec(offset, length, alignmentExponent, 0, 0);

    /// <summary>The base address of a buffer, so a test can check alignment for itself.</summary>
    internal static unsafe nuint AddressOf(VortexBuffer buffer) =>
        buffer.IsEmpty
            ? 0
            : (nuint)Unsafe.AsPointer(ref MemoryMarshal.GetReference(buffer.Span));

    /// <summary>A temporary file that deletes itself, and the directory it lives in.</summary>
    internal sealed class TempFile : IDisposable
    {
        private readonly string _directory;

        internal TempFile(byte[] content)
        {
            _directory = Path.Combine(
                Path.GetTempPath(), "vorticity-io-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(_directory);
            Path_ = Path.Combine(_directory, "segments.bin");
            global::System.IO.File.WriteAllBytes(Path_, content);
            Content = content;
        }

        internal string Path_ { get; }

        internal byte[] Content { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(_directory, recursive: true);
            }
            catch (IOException)
            {
                // A test failure must not be masked by a cleanup failure.
            }
        }
    }
}
