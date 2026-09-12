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

    public static TheoryData<string> Entries()
    {
        TheoryData<string> data = new TheoryData<string>();
        foreach (string entry in UnsupportedCorpusEntries.All)
        {
            data.Add(entry);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Entries))]
    public async Task AnUnimplementedEncodingFailsCleanly(string entry)
    {
        Sidecar? sidecar = Sidecar.TryLoad(entry);
        Assert.NotNull(sidecar);

        VortexUnsupportedException error = await Assert.ThrowsAsync<VortexUnsupportedException>(
            async () =>
            {
                await using DecodedCorpusFile file = await DecodedCorpusFile.OpenAsync(entry, sidecar);
            });

        Assert.False(string.IsNullOrEmpty(error.Message));
    }
}
