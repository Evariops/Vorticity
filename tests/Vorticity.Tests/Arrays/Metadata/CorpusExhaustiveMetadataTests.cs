// EXHAUSTIVE corpus coverage: every distinct `metadata_b64` value that appears anywhere in the
// 819 golden sidecars under tests/Vorticity.Conformance/corpus, for every encoding this component
// owns. 583 payloads in total, of which the 19 encodings below account for the ones with a codec.
//
// Each payload is parsed and then re-serialized, and the bytes must match exactly. Vortex 0.86.1
// wrote every one of them, so byte-exactness is a direct statement that this codec agrees with
// upstream on tag numbers, on field order, and on which fields have implicit versus explicit
// presence - the three things a plausible-but-wrong transcription gets away with when it is only
// ever tested against itself.
//
// The list is checked in rather than read from disk so the test stays deterministic and needs no
// filesystem. Regenerate it by collecting `metadata_b64` from the sidecars' array_tree nodes.
using System;
using Vorticity.Arrays.Metadata;
using Vorticity.Serialization.Protobuf;
using Vorticity.Types;
using Vorticity.Types.Serialization;
using Xunit;

namespace Vorticity.Tests.Arrays.Metadata;

public sealed class CorpusExhaustiveMetadataTests
{
    private delegate void BodyWriter(ref ProtoWriter writer);

    private static byte[] Serialize(BodyWriter write)
    {
        ProtoWriter writer = new ProtoWriter(128);
        try
        {
            write(ref writer);
            return writer.WrittenSpan.ToArray();
        }
        finally
        {
            writer.Dispose();
        }
    }

    private static void AssertExact(string base64, byte[] actual) =>
        Assert.Equal(
            Convert.ToHexString(Convert.FromBase64String(base64)),
            Convert.ToHexString(actual));

    // vortex.bool: 4 distinct payloads across the 819-file corpus.
    private static readonly string[] BoolPayloads =
    {
        "",
        "CAM=",
        "CAU=",
        "CAc=",
    };

    // vortex.decimal: 6 distinct payloads across the 819-file corpus.
    private static readonly string[] DecimalPayloads =
    {
        "",
        "CAE=",
        "CAI=",
        "CAM=",
        "CAQ=",
        "CAU=",
    };

    // vortex.dict: 27 distinct payloads across the 819-file corpus.
    private static readonly string[] DictPayloads =
    {
        "CAIYACAB",
        "CAQYACAB",
        "CAUQARgAIAA=",
        "CAUQARgBIAA=",
        "CAUQAhgAIAA=",
        "CAUQAxgAIAA=",
        "CAgYACAB",
        "CAoYACAB",
        "CBAYACAB",
        "CFoYACAB",
        "CGAYACAB",
        "CGIYACAB",
        "CGwYACAB",
        "CHMYACAB",
        "CHUYACAB",
        "CIIGEAEYACAB",
        "CIMBGAAgAQ==",
        "CIkBGAAgAQ==",
        "CJABGAAgAQ==",
        "CJcDEAEYACAB",
        "CJkDEAEYACAB",
        "CKIBGAAgAQ==",
        "CKgBGAAgAQ==",
        "CKkDEAEYACAB",
        "CKsDEAEYACAB",
        "CLUBGAAgAQ==",
        "CMgBGAAgAA==",
    };

    // vortex.list: 22 distinct payloads across the 819-file corpus.
    private static readonly string[] ListPayloads =
    {
        "",
        "CIAMEAE=",
        "CIAMEAY=",
        "CIAwEAE=",
        "CIAwEAY=",
        "CIBgEAE=",
        "CJMB",
        "CKAKEAE=",
        "CKBSEAE=",
        "CKMKEAE=",
        "CKNSEAE=",
        "CKsFEAE=",
        "CKwFEAE=",
        "CMcCEAE=",
        "CMsEEAE=",
        "CNQqEAE=",
        "CNUqEAE=",
        "CNcqEAE=",
        "CP0LEAE=",
        "CP0LEAY=",
        "CP1fEAE=",
        "EAY=",
    };

    // vortex.listview: 20 distinct payloads across the 819-file corpus.
    private static readonly string[] ListViewPayloads =
    {
        "",
        "CIAIEAE=",
        "CIAIEAMYAw==",
        "CIAMEAMYAw==",
        "CIAwEAMYAw==",
        "CIFAEAE=",
        "CNsB",
        "CO02EAE=",
        "CO4GEAE=",
        "CO82EAE=",
        "CO8GEAE=",
        "COw2EAE=",
        "CP0LEAMYAw==",
        "CP4/EAE=",
        "CP8/EAE=",
        "CP8HEAE=",
        "CP8HEAMYAw==",
        "CP8fEAE=",
        "CP8fEAMYAw==",
        "EAMYAw==",
    };

    // vortex.varbin: 3 distinct payloads across the 819-file corpus.
    private static readonly string[] VarBinPayloads =
    {
        "",
        "CAE=",
        "CAI=",
    };

    // vortex.sparse: 12 distinct payloads across the 819-file corpus.
    private static readonly string[] SparsePayloads =
    {
        "CgQICxgB",
        "CgQIDxgD",
        "CgQIEBgD",
        "CgQIERgB",
        "CgQIHRgC",
        "CgQIIRgB",
        "CgQIMRgB",
        "CgQIQBgD",
        "CgQIQhgB",
        "CgQIUhgB",
        "CgQIVRgB",
        "CgQIYhgB",
    };

    // vortex.runend: 7 distinct payloads across the 819-file corpus.
    private static readonly string[] RunEndPayloads =
    {
        "CAEQBA==",
        "CAEQEA==",
        "CAEQEQ==",
        "CAEQIA==",
        "CAEQQA==",
        "EAE=",
        "EAM=",
    };

    // fastlanes.bitpacked: 53 distinct payloads across the 819-file corpus.
    private static readonly string[] BitPackedPayloads =
    {
        "CA0=",
        "CA0aCggBGAEgCSgAMAA=",
        "CA4=",
        "CA4aCAgCIAEoADAA",
        "CA4aCAgCIAIoADAA",
        "CA4aCAgCIAgoADAA",
        "CA4aCAgCIAkoADAA",
        "CA4aCggBGAEgCSgAMAA=",
        "CA8=",
        "CA8aCAgGIAEoADAA",
        "CAE=",
        "CAI=",
        "CAM=",
        "CAQ=",
        "CAY=",
        "CAc=",
        "CAcaCggDGAEgASgAMAA=",
        "CAg=",
        "CAgaCggFGAEgASgAMAA=",
        "CAk=",
        "CAkaCggJGAEgASgAMAA=",
        "CAo=",
        "CAoaBAgIGAM=",
        "CAoaCggBGAEgAigAMAA=",
        "CAs=",
        "CAsaCAgCIAEoADAA",
        "CAsaCggBGAEgAigAMAA=",
        "CAw=",
        "CB4=",
        "CB4aCAgCIAEoADAA",
        "CB4aCAgCIAgoADAA",
        "CBA=",
        "CBE=",
        "CBEaCAgCIAgoADAA",
        "CBEaCAgCIAkoADAA",
        "CBEaCAgGIAEoADAA",
        "CBEaCAgGIAIoADAA",
        "CBEaCAgHIAEoADAA",
        "CBEaCAgHIAIoADAA",
        "CBI=",
        "CBQ=",
        "CC0aCAgCIAgoADAA",
        "CC0aCAgCIAkoADAA",
        "CCE=",
        "CCU=",
        "CCg=",
        "CCoaCAgCIAEoADAA",
        "CDA=",
        "CDM=",
        "CDQ=",
        "CDwaCAgCIAEoADAA",
        "CDwaCAgCIAgoADAA",
        "GggIASABKAAwAA==",
    };

    // fastlanes.rle: 4 distinct payloads across the 819-file corpus.
    private static readonly string[] RlePayloads =
    {
        "CAEQgAgYASABKAM=",
        "CEAQgAgYASABKAM=",
        "CEEQgBAYASACKAM=",
        "CIACEIAgGAEgBCgD",
    };

    // vortex.sequence: 105 distinct payloads across the 819-file corpus.
    private static readonly string[] SequencePayloads =
    {
        "CgIYABICGA4=",
        "CgIYABICGAI=",
        "CgIYABICGAo=",
        "CgIYABIDGMIB",
        "CgIYABIDGMgB",
        "CgIYAhIDGMgB",
        "CgIYBBIDGMgB",
        "CgIYBhIDGMgB",
        "CgIYCBIDGMgB",
        "CgIYChIDGMgB",
        "CgIYDBIDGMgB",
        "CgIYDRICGAI=",
        "CgIYDRICGAY=",
        "CgIYDhIDGMgB",
        "CgIYEBIDGMgB",
        "CgIYEhIDGMgB",
        "CgIYFBIDGMgB",
        "CgIYFhIDGMgB",
        "CgIgABICGAI=",
        "CgIgABICGAY=",
        "CgIgABIDGMIB",
        "CgIgQRICGAI=",
        "CgMY0A8SAhgO",
        "CgMg3wcSAhgC",
        "CgMg7wcSAhgC",
        "CgMgngcSAhgC",
        "CgMgrgcSAhgC",
        "CgMgvgcSAhgC",
        "CgMgzwcSAhgC",
        "CgQY+rh6EgMYgDA=",
        "CgQY/L4HEgIYAg==",
        "CgQY3K4HEgIYAg==",
        "CgQY8KgCEgIYAg==",
        "CgQYgIl6EgIYBg==",
        "CgQYgIl6EgMYgDA=",
        "CgQYgIl9EgIYBg==",
        "CgQYgJl+EgIYBg==",
        "CgQYgJl7EgIYBg==",
        "CgQYgKl/EgIYBg==",
        "CgQYgKl8EgIYBg==",
        "CgQYgLl6EgIYBg==",
        "CgQYgLl9EgIYBg==",
        "CgQYgMl+EgIYBg==",
        "CgQYgMl7EgIYBg==",
        "CgQYgNl/EgIYBg==",
        "CgQYgNl8EgIYBg==",
        "CgQYgOl6EgIYBg==",
        "CgQYgOl9EgIYBg==",
        "CgQYgPl+EgIYBg==",
        "CgQYgPl7EgIYBg==",
        "CgQYns8HEgIYAg==",
        "CgQYvJ4HEgIYAg==",
        "CgQYvt8HEgIYAg==",
        "CgQgjpkEEgIYAg==",
        "CgUYgImAARICGAY=",
        "CgUYgImDARICGAY=",
        "CgUYgImGARICGAY=",
        "CgUYgImJARICGAY=",
        "CgUYgImMARICGAY=",
        "CgUYgImPARICGAY=",
        "CgUYgJmBARICGAY=",
        "CgUYgJmEARICGAY=",
        "CgUYgJmHARICGAY=",
        "CgUYgJmKARICGAY=",
        "CgUYgJmNARICGAY=",
        "CgUYgJmQARICGAY=",
        "CgUYgKmCARICGAY=",
        "CgUYgKmFARICGAY=",
        "CgUYgKmIARICGAY=",
        "CgUYgKmLARICGAY=",
        "CgUYgKmOARICGAY=",
        "CgUYgKmRARICGAY=",
        "CgUYgLmAARICGAY=",
        "CgUYgLmDARICGAY=",
        "CgUYgLmGARICGAY=",
        "CgUYgLmJARICGAY=",
        "CgUYgLmMARICGAY=",
        "CgUYgLmPARICGAY=",
        "CgUYgMmBARICGAY=",
        "CgUYgMmEARICGAY=",
        "CgUYgMmHARICGAY=",
        "CgUYgMmKARICGAY=",
        "CgUYgMmNARICGAY=",
        "CgUYgMmQARICGAY=",
        "CgUYgNmCARICGAY=",
        "CgUYgNmFARICGAY=",
        "CgUYgNmIARICGAY=",
        "CgUYgNmLARICGAY=",
        "CgUYgNmOARICGAY=",
        "CgUYgNmRARICGAY=",
        "CgUYgOmAARICGAY=",
        "CgUYgOmDARICGAY=",
        "CgUYgOmGARICGAY=",
        "CgUYgOmJARICGAY=",
        "CgUYgOmMARICGAY=",
        "CgUYgOmPARICGAY=",
        "CgUYgPmBARICGAY=",
        "CgUYgPmEARICGAY=",
        "CgUYgPmHARICGAY=",
        "CgUYgPmKARICGAY=",
        "CgUYgPmNARICGAY=",
        "CgUYgPmQARICGAY=",
        "CgcYgIDh78ZfEgUYgPCyUg==",
        "CgcYgKCr/vliEgIYBA==",
        "CgoYgIDQ4sa/zpcvEgQYgol6",
    };

    // vortex.alp: 87 distinct payloads across the 819-file corpus.
    private static readonly string[] AlpPayloads =
    {
        "",
        "CA4QDRoICD0gASgAMAA=",
        "CA4QDRoICD4gASgAMAA=",
        "CA4QDRoICD8gASgAMAA=",
        "CA4QDRoICDkgASgAMAA=",
        "CA4QDRoICDogASgAMAA=",
        "CA4QDRoICDsgASgAMAA=",
        "CA4QDRoICDwgASgAMAA=",
        "CAIaCAgFIAgoADAA",
        "CAIaCAgFIAkoADAA",
        "CAIaCAgGIAgoADAA",
        "CAIaCAgGIAkoADAA",
        "CAQQAhoLCPQHGAMgBCgDMAA=",
        "CAUQAxoICAUgASgAMAA=",
        "CAUQAxoICAYgASgAMAA=",
        "CAkQBxoLCLYCGAEgASgAMAA=",
        "CAkQBxoLCLYCGAMgASgDMAA=",
        "CAkQBxoLCLYCGAMgAigDMAA=",
        "CAoQCBoLCKQTGAEgCSgBMAA=",
        "CBAQDRoICAUgASgAMAA=",
        "CBAQDRoICAUgAigAMAA=",
        "CBAQDRoICAUgCCgAMAA=",
        "CBAQDRoICAUgCSgAMAA=",
        "CBAQDRoICAYgASgAMAA=",
        "CBAQDRoICAYgAigAMAA=",
        "CBAQDRoICAYgCCgAMAA=",
        "CBAQDRoICAYgCSgAMAA=",
        "CBAQDhoLCOwJGAEgCCgBMAA=",
        "CBAQDw==",
        "CBAQDxoICA0gASgAMAA=",
        "CBAQDxoICA4gASgAMAA=",
        "CBAQDxoICA8gASgAMAA=",
        "CBAQDxoICAEgASgAMAA=",
        "CBAQDxoICAMgASgAMAA=",
        "CBAQDxoICAYgASgAMAA=",
        "CBAQDxoICAcgASgAMAA=",
        "CBAQDxoICAsgASgAMAA=",
        "CBAQDxoICAwgASgAMAA=",
        "CBAQDxoICB0gASgAMAA=",
        "CBAQDxoICB4gASgAMAA=",
        "CBAQDxoICB8gASgAMAA=",
        "CBAQDxoICBAgASgAMAA=",
        "CBAQDxoICBEgASgAMAA=",
        "CBAQDxoICBIgASgAMAA=",
        "CBAQDxoICBMgASgAMAA=",
        "CBAQDxoICBQgASgAMAA=",
        "CBAQDxoICBUgASgAMAA=",
        "CBAQDxoICBYgASgAMAA=",
        "CBAQDxoICBcgASgAMAA=",
        "CBAQDxoICBggASgAMAA=",
        "CBAQDxoICBkgASgAMAA=",
        "CBAQDxoICBogASgAMAA=",
        "CBAQDxoICBsgASgAMAA=",
        "CBAQDxoICBwgASgAMAA=",
        "CBAQDxoICC0gASgAMAA=",
        "CBAQDxoICC4gASgAMAA=",
        "CBAQDxoICC8gASgAMAA=",
        "CBAQDxoICCAgASgAMAA=",
        "CBAQDxoICCEgASgAMAA=",
        "CBAQDxoICCIgASgAMAA=",
        "CBAQDxoICCMgASgAMAA=",
        "CBAQDxoICCQgASgAMAA=",
        "CBAQDxoICCUgASgAMAA=",
        "CBAQDxoICCYgASgAMAA=",
        "CBAQDxoICCcgASgAMAA=",
        "CBAQDxoICCggASgAMAA=",
        "CBAQDxoICCkgASgAMAA=",
        "CBAQDxoICCogASgAMAA=",
        "CBAQDxoICCsgASgAMAA=",
        "CBAQDxoICCwgASgAMAA=",
        "CBAQDxoICDAgASgAMAA=",
        "CBAQDxoICDEgASgAMAA=",
        "CBAQDxoICDIgASgAMAA=",
        "CBAQDxoICDMgASgAMAA=",
        "CBAQDxoICDQgASgAMAA=",
        "CBAQDxoICDUgASgAMAA=",
        "CBAQDxoICDYgASgAMAA=",
        "CBAQDxoICDcgASgAMAA=",
        "CBAQDxoICDggASgAMAA=",
        "CBEQDw==",
        "CBEQEBoICAIgASgAMAA=",
        "CBEQEBoICAQgASgAMAA=",
        "CBEQEBoICAUgASgAMAA=",
        "CBEQEBoICAggASgAMAA=",
        "CBEQEBoICAkgASgAMAA=",
        "CBEQEBoICAogASgAMAA=",
        "GgQICBgD",
    };

    // vortex.alprd: 3 distinct payloads across the 819-file corpus.
    private static readonly string[] AlpRdPayloads =
    {
        "CDAQARoC438gAQ==",
        "CDMQAhoE/A/9DyAB",
        "CDQQAhoE/gf/ByAB",
    };

    // vortex.fsst: 4 distinct payloads across the 819-file corpus.
    private static readonly string[] FsstPayloads =
    {
        "",
        "CAYQBg==",
        "EAE=",
        "EAI=",
    };

    // vortex.onpair: 53 distinct payloads across the 819-file corpus.
    private static readonly string[] OnPairPayloads =
    {
        "CAIYgAIgESgCMAE4Ag==",
        "CAIYiwIgq5kEKAE4Ag==",
        "CAIYvAIg+RAoAjABOAI=",
        "CAIYvAIg9RAoAjABOAI=",
        "CAIYvQIggEAoAjABOAI=",
        "GJ0DIJobKAEwATgB",
        "GJgDIKYcKAEwATgB",
        "GKQDIK4dKAEwATgB",
        "GM4CIN0TKAE4AQ==",
        "GM4CIPAOKAEwATgB",
        "GM8CINUTKAE4AQ==",
        "GM8CIOoTKAE4AQ==",
        "GNACIM0TKAE4AQ==",
        "GNACINATKAE4AQ==",
        "GNACINYTKAE4AQ==",
        "GNACINwTKAE4AQ==",
        "GNECIM0TKAE4AQ==",
        "GNECIMkTKAE4AQ==",
        "GNECINMTKAE4AQ==",
        "GNECINcTKAE4AQ==",
        "GNECINoTKAE4AQ==",
        "GNICINETKAE4AQ==",
        "GNICINQTKAE4AQ==",
        "GNICINUTKAE4AQ==",
        "GNICINwTKAE4AQ==",
        "GNMCIMYTKAE4AQ==",
        "GNMCIMoTKAE4AQ==",
        "GNMCINETKAE4AQ==",
        "GNQCIL0TKAE4AQ==",
        "GNQCIMITKAE4AQ==",
        "GNQCIMQTKAE4AQ==",
        "GNQCIMYTKAE4AQ==",
        "GNQCIMwTKAE4AQ==",
        "GNQCINQNKAEwATgB",
        "GNUCILoTKAE4AQ==",
        "GNgDIOu9ASgBMAE4AQ==",
        "GNoFIJ3rAigBMAE4AQ==",
        "GNsCIMAOKAEwATgB",
        "GNsCIMYOKAEwATgB",
        "GOACIK0LKAEwATgB",
        "GOQCII0NKAEwATgB",
        "GOQCII8NKAEwATgB",
        "GOgCIMAxKAE4AQ==",
        "GOgCIMgOKAEwATgB",
        "GOkCIL4OKAEwATgB",
        "GOoCIMIVKAEwATgB",
        "GP0CII4UKAEwATgB",
        "GP0CIMUWKAEwATgB",
        "GPECINgRKAEwATgB",
        "GPECINoRKAEwATgB",
        "GPMCIO0OKAEwATgB",
        "GPMCIPAOKAEwATgB",
        "GPwCIMsXKAEwATgB",
    };

    // vortex.datetimeparts: 2 distinct payloads across the 819-file corpus.
    private static readonly string[] DateTimePartsPayloads =
    {
        "CAEQAhgB",
        "CAcQBhgG",
    };

    // vortex.decimal_byte_parts: 3 distinct payloads across the 819-file corpus.
    private static readonly string[] DecimalBytePartsPayloads =
    {
        "CAU=",
        "CAY=",
        "CAc=",
    };

    // vortex.zstd: 4 distinct payloads across the 819-file corpus.
    private static readonly string[] ZstdPayloads =
    {
        "EgQIHxAB",
        "EgcI4P4BEIAIEgQIHxAB",
        "EgcI4P4BEIAIEgcI4P4BEIAIEgcI4P4BEIAIEgcI4P4BEIAI",
        "EgcIwP4BEP8H",
    };

    // fastlanes.for: 136 distinct payloads across the 819-file corpus.
    private static readonly string[] ForReferencePayloads =
    {
        "GA0=",
        "GAE=",
        "GI7HAw==",
        "GI7yEA==",
        "GICA0OLGv86XLw==",
        "GICA4e/GXw==",
        "GICJeg==",
        "GICgq/75Yg==",
        "GICo1rkH",
        "GIJB",
        "GIKRCA==",
        "GISCAQ==",
        "GISiEA==",
        "GIazGA==",
        "GIbDAQ==",
        "GIiEAg==",
        "GIjEIA==",
        "GIpQ",
        "GIrFAg==",
        "GIyGAw==",
        "GIzhCA==",
        "GJ7wAQ==",
        "GJCDGQ==",
        "GJCIBA==",
        "GJCOeQ==",
        "GJKUIQ==",
        "GJLJBA==",
        "GJSKBQ==",
        "GJSgAQ==",
        "GJaxCQ==",
        "GJbLBQ==",
        "GJiMBg==",
        "GJjCEQ==",
        "GJrNBg==",
        "GJrTGQ==",
        "GJyOBw==",
        "GJzkIQ==",
        "GK7XAw==",
        "GK7zGg==",
        "GKAQ",
        "GKCBCg==",
        "GKJR",
        "GKKSEg==",
        "GKSSAQ==",
        "GKSjGg==",
        "GKa0Ig==",
        "GKbTAQ==",
        "GKiUAg==",
        "GKjAAg==",
        "GKmSBg==",
        "GKnyfA==",
        "GKrRCg==",
        "GKrVAg==",
        "GKyWAw==",
        "GKziEg==",
        "GL7xCw==",
        "GLCEIw==",
        "GLCYBA==",
        "GLKQAw==",
        "GLLZBA==",
        "GLSaBQ==",
        "GLShCw==",
        "GLayEw==",
        "GLbbBQ==",
        "GLicBg==",
        "GLjDGw==",
        "GLrUIw==",
        "GLrdBg==",
        "GLzgAw==",
        "GM70JA==",
        "GM7nAw==",
        "GMAg",
        "GMCCFA==",
        "GMJh",
        "GMKTHA==",
        "GMSiAQ==",
        "GMSkJA==",
        "GMawBA==",
        "GMbjAQ==",
        "GMikAg==",
        "GMjBDA==",
        "GMrSFA==",
        "GMrlAg==",
        "GMymAw==",
        "GMzCcg==",
        "GMzjHA==",
        "GN7yFQ==",
        "GNCABQ==",
        "GNCoBA==",
        "GNKRDQ==",
        "GNLpBA==",
        "GNSiFQ==",
        "GNSqBQ==",
        "GNazHQ==",
        "GNbrBQ==",
        "GNisBg==",
        "GNjEJQ==",
        "GNrQBQ==",
        "GNrtBg==",
        "GNzhDQ==",
        "GO73Aw==",
        "GO7wBg==",
        "GOAw",
        "GOCDHg==",
        "GOJx",
        "GOKUJg==",
        "GOSgBg==",
        "GOSyAQ==",
        "GOaxDg==",
        "GObzAQ==",
        "GOi0Ag==",
        "GOjCFg==",
        "GOr1Ag==",
        "GOrTHg==",
        "GOy2Aw==",
        "GOzkJg==",
        "GP3/n/b0rNvgGw==",
        "GP7zHw==",
        "GPC4BA==",
        "GPCBDw==",
        "GPCoAg==",
        "GPKSFw==",
        "GPL5BA==",
        "GPOIeg==",
        "GPS6BQ==",
        "GPSjHw==",
        "GPYB",
        "GPa0Jw==",
        "GPb7BQ==",
        "GPeIeg==",
        "GPi8Bg==",
        "GPjABw==",
        "GPr9Bg==",
        "GPrRDw==",
        "GPziFw==",
        "IPHcupO73uPzyAE=",
    };

    [Fact]
    public void EveryBoolPayloadRoundTripsExactly()
    {
        foreach (string base64 in BoolPayloads)
        {
            BoolMetadata value = BoolMetadata.Read(Convert.FromBase64String(base64));
            Assert.True(value.Offset < BoolMetadata.OffsetLimit);
            AssertExact(base64, Serialize((ref ProtoWriter w) => BoolMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EveryDecimalPayloadRoundTripsExactly()
    {
        foreach (string base64 in DecimalPayloads)
        {
            DecimalMetadata value = DecimalMetadata.Read(Convert.FromBase64String(base64));
            AssertExact(base64, Serialize((ref ProtoWriter w) => DecimalMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EveryDictPayloadRoundTripsExactly()
    {
        foreach (string base64 in DictPayloads)
        {
            DictMetadata value = DictMetadata.Read(Convert.FromBase64String(base64));
            AssertExact(base64, Serialize((ref ProtoWriter w) => DictMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EveryListPayloadRoundTripsExactly()
    {
        foreach (string base64 in ListPayloads)
        {
            ListMetadata value = ListMetadata.Read(Convert.FromBase64String(base64));
            AssertExact(base64, Serialize((ref ProtoWriter w) => ListMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EveryListViewPayloadRoundTripsExactly()
    {
        foreach (string base64 in ListViewPayloads)
        {
            ListViewMetadata value = ListViewMetadata.Read(Convert.FromBase64String(base64));
            AssertExact(base64, Serialize((ref ProtoWriter w) => ListViewMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EveryVarBinPayloadRoundTripsExactly()
    {
        foreach (string base64 in VarBinPayloads)
        {
            VarBinMetadata value = VarBinMetadata.Read(Convert.FromBase64String(base64));
            AssertExact(base64, Serialize((ref ProtoWriter w) => VarBinMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EverySparsePayloadRoundTripsExactly()
    {
        foreach (string base64 in SparsePayloads)
        {
            SparseMetadata value = SparseMetadata.Read(Convert.FromBase64String(base64));
            Assert.True(value.Patches.IndicesPType.IsUnsignedInteger());
            AssertExact(base64, Serialize((ref ProtoWriter w) => SparseMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EveryRunEndPayloadRoundTripsExactly()
    {
        foreach (string base64 in RunEndPayloads)
        {
            RunEndMetadata value = RunEndMetadata.Read(Convert.FromBase64String(base64));
            AssertExact(base64, Serialize((ref ProtoWriter w) => RunEndMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EveryBitPackedPayloadRoundTripsExactly()
    {
        foreach (string base64 in BitPackedPayloads)
        {
            BitPackedMetadata value = BitPackedMetadata.Read(Convert.FromBase64String(base64));
            Assert.True(value.Offset < BitPackedMetadata.OffsetLimit);
            AssertExact(base64, Serialize((ref ProtoWriter w) => BitPackedMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EveryRlePayloadRoundTripsExactly()
    {
        foreach (string base64 in RlePayloads)
        {
            RleMetadata value = RleMetadata.Read(Convert.FromBase64String(base64));
            Assert.True(value.Offset < RleMetadata.OffsetLimit);
            AssertExact(base64, Serialize((ref ProtoWriter w) => RleMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EverySequencePayloadRoundTripsExactly()
    {
        ScalarStore store = new ScalarStore();
        DTypeArena arena = new DTypeArena();
        foreach (string base64 in SequencePayloads)
        {
            SequenceMetadata value = SequenceMetadata.Read(Convert.FromBase64String(base64), store, arena);
            Assert.False(value.Base.IsAbsent);
            Assert.False(value.Multiplier.IsAbsent);
            AssertExact(base64, Serialize((ref ProtoWriter w) => SequenceMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EveryAlpPayloadRoundTripsExactly()
    {
        foreach (string base64 in AlpPayloads)
        {
            AlpMetadata value = AlpMetadata.Read(Convert.FromBase64String(base64));
            AssertExact(base64, Serialize((ref ProtoWriter w) => AlpMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EveryAlpRdPayloadRoundTripsExactly()
    {
        Span<uint> dictionary = stackalloc uint[AlpRdMetadata.TypicalDictionaryLength];
        foreach (string base64 in AlpRdPayloads)
        {
            byte[] metadata = Convert.FromBase64String(base64);
            AlpRdMetadata value = AlpRdMetadata.Read(metadata, dictionary);
            uint[] entries = dictionary[..value.DictionaryEntryCount].ToArray();
            AssertExact(base64, Serialize((ref ProtoWriter w) => AlpRdMetadata.Write(ref w, in value, entries)));
        }
    }

    [Fact]
    public void EveryFsstPayloadRoundTripsExactly()
    {
        foreach (string base64 in FsstPayloads)
        {
            FsstMetadata value = FsstMetadata.Read(Convert.FromBase64String(base64));
            AssertExact(base64, Serialize((ref ProtoWriter w) => FsstMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EveryOnPairPayloadRoundTripsExactly()
    {
        foreach (string base64 in OnPairPayloads)
        {
            OnPairMetadata value = OnPairMetadata.Read(Convert.FromBase64String(base64));
            AssertExact(base64, Serialize((ref ProtoWriter w) => OnPairMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EveryDateTimePartsPayloadRoundTripsExactly()
    {
        foreach (string base64 in DateTimePartsPayloads)
        {
            DateTimePartsMetadata value = DateTimePartsMetadata.Read(Convert.FromBase64String(base64));
            AssertExact(base64, Serialize((ref ProtoWriter w) => DateTimePartsMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EveryDecimalBytePartsPayloadRoundTripsExactly()
    {
        foreach (string base64 in DecimalBytePartsPayloads)
        {
            DecimalBytePartsMetadata value =
                DecimalBytePartsMetadata.Read(Convert.FromBase64String(base64));
            AssertExact(
                base64, Serialize((ref ProtoWriter w) => DecimalBytePartsMetadata.Write(ref w, in value)));
        }
    }

    [Fact]
    public void EveryZstdPayloadRoundTripsExactly()
    {
        Span<ZstdFrameMetadata> buffer = stackalloc ZstdFrameMetadata[64];
        foreach (string base64 in ZstdPayloads)
        {
            byte[] metadata = Convert.FromBase64String(base64);
            int frameCount = ZstdMetadata.CountFrames(metadata);
            Assert.InRange(frameCount, 0, buffer.Length);
            Span<ZstdFrameMetadata> frames = buffer[..frameCount];
            ZstdMetadata value = ZstdMetadata.Read(metadata, frames);
            Assert.Equal(frameCount, value.FrameCount);
            ZstdFrameMetadata[] copy = frames[..value.FrameCount].ToArray();
            AssertExact(base64, Serialize((ref ProtoWriter w) => ZstdMetadata.Write(ref w, in value, copy)));
        }
    }

    [Fact]
    public void EveryForReferenceScalarRoundTripsExactly()
    {
        // fastlanes.for carries a bare ScalarValue, not a message (Phase 1 contract §0a C2).
        ScalarStore store = new ScalarStore();
        DTypeArena arena = new DTypeArena();
        foreach (string base64 in ForReferencePayloads)
        {
            byte[] metadata = Convert.FromBase64String(base64);
            ScalarValue value = EncodingMetadata.ReadReferenceScalar(metadata, store, arena);
            Assert.False(value.IsAbsent);
            AssertExact(base64, ScalarProtobuf.SerializeValue(value));
        }
    }

    [Fact]
    public void NoOwnedPayloadIsEmptyExceptWhereTheDefaultIsMeaningful()
    {
        // A zero-length payload is legal for a message whose every field defaults, and the corpus
        // does contain some. It is never legal for fastlanes.for, whose bare ScalarValue would then
        // decode to a null reference.
        foreach (string base64 in ForReferencePayloads)
        {
            Assert.NotEqual(string.Empty, base64);
        }
    }
}
