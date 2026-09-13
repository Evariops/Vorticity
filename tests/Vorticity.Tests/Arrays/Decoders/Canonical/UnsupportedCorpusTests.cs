// The other half of the corpus guarantee: 110 real files whose array trees reach an encoding this
// component does not own. Every one must fail the same way - VortexUnsupportedException naming the
// id - and none may throw anything else, hang, or return a value.
//
// This is what makes "malformed or unsupported input yields exactly one of two exception types"
// testable against bytes we did not write. It also exercises the blob reader, the validity rule and
// the canonical decoders on the path down to the unsupported node.
using System;
using System.Threading.Tasks;
using Vorticity.Arrays.Decoders.Canonical;
using Xunit;

namespace Vorticity.Tests.Arrays.Decoders.Canonical;

public sealed class UnsupportedCorpusTests
{
    static UnsupportedCorpusTests() => CanonicalDecoders.RegisterAll();

    /// <summary>
    /// THE LIST IS EMPTY, and that is what this test now asserts.
    /// </summary>
    /// <remarks>
    /// It ran over 110 files at its widest, then 8, and now none: `vortex.variant` and
    /// `vortex.parquet.variant` were the last encodings of the corpus without a decoder. An empty
    /// theory is a test that passes by finding nothing, which is the failure mode the header of
    /// `CorpusEntries.cs` was written against -- so the theory is replaced by an assertion that the
    /// partition is exactly what it claims, and the PROPERTY it protected (an unsupported encoding
    /// raises `VortexUnsupportedException` naming the id, never a wrong value) is still tested
    /// against a forged id in `ScanContextTests` and against a shredded variant by the decoders
    /// themselves.
    /// </remarks>
    [Fact]
    public void NoCorpusEntryReachesAnUnimplementedEncoding()
    {
        Assert.Empty(UnsupportedCorpusEntries.All);
        Assert.NotEmpty(CorpusEntries.All);
    }
}
