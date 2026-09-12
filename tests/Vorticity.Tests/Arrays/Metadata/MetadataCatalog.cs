// Every message this component owns, paired with a valid payload and a description function, so
// that the adversarial sweeps in AdversarialMetadataTests run over all of them rather than over
// the handful someone remembered.
using System;
using System.Collections.Generic;
using System.Globalization;
using Vorticity.Arrays.Metadata;
using Vorticity.Types;

namespace Vorticity.Tests.Arrays.Metadata;

/// <summary>Reads <paramref name="metadata"/> and renders the parsed value as an invariant string.</summary>
internal delegate string MetadataDescriber(ReadOnlySpan<byte> metadata);

internal sealed class MetadataCase
{
    internal MetadataCase(string name, byte[] valid, MetadataDescriber describe)
    {
        Name = name;
        Valid = valid;
        Describe = describe;
    }

    internal string Name { get; }

    internal byte[] Valid { get; }

    internal MetadataDescriber Describe { get; }

    public override string ToString() => Name;
}

internal static class MetadataCatalog
{
    private static string F(ulong value) => value.ToString(CultureInfo.InvariantCulture);

    private static string F(uint value) => value.ToString(CultureInfo.InvariantCulture);

    private static string F(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string F(bool? value) => value.HasValue ? (value.Value ? "T" : "F") : "-";

    private static string Describe(in PatchesMetadata p) =>
        $"len={F(p.Length)},off={F(p.Offset)},ip={p.IndicesPType}," +
        $"co={(p.HasChunkOffsets ? p.ChunkOffsetsPType.ToString() : "-")}," +
        $"col={(p.HasChunkOffsetsLength ? F(p.ChunkOffsetsLength) : "-")}," +
        $"owc={(p.HasOffsetWithinChunk ? F(p.OffsetWithinChunk) : "-")}";

    /// <summary>All cases, each with a payload exercising every optional field it has.</summary>
    internal static IReadOnlyList<MetadataCase> All { get; } = Build();

    private static List<MetadataCase> Build()
    {
        List<MetadataCase> cases = new List<MetadataCase>();

        byte[] patchesBody = new WireBuilder()
            .VarintField(1, 310)
            .VarintField(2, 7)
            .VarintField(3, (ulong)PType.U64)
            .VarintField(4, 1)
            .VarintField(5, (ulong)PType.U32)
            .VarintField(6, 3)
            .ToArray();

        cases.Add(new MetadataCase(
            "BoolMetadata",
            new WireBuilder().VarintField(1, 7).ToArray(),
            static m => F(BoolMetadata.Read(m).Offset)));

        cases.Add(new MetadataCase(
            "DecimalMetadata",
            new WireBuilder().VarintField(1, 5).ToArray(),
            static m => DecimalMetadata.Read(m).ValuesType.ToString()));

        cases.Add(new MetadataCase(
            "DictMetadata",
            new WireBuilder()
                .VarintField(1, 5)
                .VarintField(2, (ulong)PType.U16)
                .VarintField(3, 1)
                .VarintField(4, 0)
                .ToArray(),
            static m =>
            {
                DictMetadata d = DictMetadata.Read(m);
                return $"{F(d.ValuesLength)},{d.CodesPType},{F(d.IsNullableCodes)},{F(d.AllValuesReferenced)}";
            }));

        cases.Add(new MetadataCase(
            "ListMetadata",
            new WireBuilder().VarintField(1, 1536).VarintField(2, (ulong)PType.I32).ToArray(),
            static m =>
            {
                ListMetadata l = ListMetadata.Read(m);
                return $"{F(l.ElementsLength)},{l.OffsetPType}";
            }));

        cases.Add(new MetadataCase(
            "ListViewMetadata",
            new WireBuilder()
                .VarintField(1, 6144)
                .VarintField(2, (ulong)PType.U64)
                .VarintField(3, (ulong)PType.U64)
                .ToArray(),
            static m =>
            {
                ListViewMetadata l = ListViewMetadata.Read(m);
                return $"{F(l.ElementsLength)},{l.OffsetPType},{l.SizePType}";
            }));

        cases.Add(new MetadataCase(
            "VarBinMetadata",
            new WireBuilder().VarintField(1, (ulong)PType.U32).ToArray(),
            static m => VarBinMetadata.Read(m).OffsetsPType.ToString()));

        cases.Add(new MetadataCase(
            "SparseMetadata",
            new WireBuilder().BytesField(1, patchesBody).ToArray(),
            static m =>
            {
                SparseMetadata s = SparseMetadata.Read(m);
                return Describe(s.Patches);
            }));

        cases.Add(new MetadataCase(
            "RunEndMetadata",
            new WireBuilder()
                .VarintField(1, (ulong)PType.U16)
                .VarintField(2, 17)
                .VarintField(3, 3)
                .ToArray(),
            static m =>
            {
                RunEndMetadata r = RunEndMetadata.Read(m);
                return $"{r.EndsPType},{F(r.NumRuns)},{F(r.Offset)}";
            }));

        cases.Add(new MetadataCase(
            "BitPackedMetadata",
            new WireBuilder()
                .VarintField(1, 10)
                .VarintField(2, 512)
                .BytesField(3, patchesBody)
                .ToArray(),
            static m =>
            {
                BitPackedMetadata b = BitPackedMetadata.Read(m);
                return $"{F(b.BitWidth)},{F(b.Offset)},{(b.HasPatches ? Describe(b.Patches) : "-")}";
            }));

        cases.Add(new MetadataCase(
            "RleMetadata",
            new WireBuilder()
                .VarintField(1, 1)
                .VarintField(2, 1024)
                .VarintField(3, (ulong)PType.U16)
                .VarintField(4, 1)
                .VarintField(5, (ulong)PType.U64)
                .VarintField(6, 12)
                .ToArray(),
            static m =>
            {
                RleMetadata r = RleMetadata.Read(m);
                return $"{F(r.ValuesLength)},{F(r.IndicesLength)},{r.IndicesPType}," +
                       $"{F(r.ValuesIdxOffsetsLength)},{r.ValuesIdxOffsetsPType},{F(r.Offset)}";
            }));

        cases.Add(new MetadataCase(
            "SequenceMetadata",
            new WireBuilder()
                .BytesField(1, new WireBuilder().VarintField(3, 2000).ToArray())
                .BytesField(2, new WireBuilder().VarintField(3, 14).ToArray())
                .ToArray(),
            static m =>
            {
                SequenceMetadata s = SequenceMetadata.Read(m, new ScalarStore(), new DTypeArena());
                return string.Create(
                    CultureInfo.InvariantCulture, $"{s.Base.AsInt64},{s.Multiplier.AsInt64}");
            }));

        cases.Add(new MetadataCase(
            "AlpMetadata",
            new WireBuilder()
                .VarintField(1, 9)
                .VarintField(2, 7)
                .BytesField(3, patchesBody)
                .ToArray(),
            static m =>
            {
                AlpMetadata a = AlpMetadata.Read(m);
                return $"{F(a.ExponentE)},{F(a.ExponentF)},{(a.HasPatches ? Describe(a.Patches) : "-")}";
            }));

        cases.Add(new MetadataCase(
            "AlpRdMetadata",
            new WireBuilder()
                .VarintField(1, 51)
                .VarintField(2, 2)
                .BytesField(3, new WireBuilder().Varint(2044).Varint(2045).ToArray())
                .VarintField(4, (ulong)PType.U16)
                .BytesField(5, patchesBody)
                .ToArray(),
            static m =>
            {
                Span<uint> dictionary = stackalloc uint[AlpRdMetadata.TypicalDictionaryLength];
                AlpRdMetadata a = AlpRdMetadata.Read(m, dictionary);
                string entries = string.Empty;
                for (int i = 0; i < a.DictionaryEntryCount; i++)
                {
                    entries += F(dictionary[i]) + ";";
                }

                return $"{F(a.RightBitWidth)},{F(a.DictionaryLength)},{entries},{a.LeftPartsPType}," +
                       $"{(a.HasPatches ? Describe(a.Patches) : "-")}";
            }));

        cases.Add(new MetadataCase(
            "FsstMetadata",
            new WireBuilder().VarintField(1, (ulong)PType.I32).VarintField(2, (ulong)PType.I32).ToArray(),
            static m =>
            {
                FsstMetadata f = FsstMetadata.Read(m);
                return $"{f.UncompressedLengthsPType},{f.CodesOffsetsPType}";
            }));

        cases.Add(new MetadataCase(
            "OnPairMetadata",
            new WireBuilder()
                .VarintField(1, (ulong)PType.U32)
                .VarintField(3, 316)
                .VarintField(4, 2165)
                .VarintField(5, (ulong)PType.U32)
                .VarintField(6, (ulong)PType.U16)
                .VarintField(7, (ulong)PType.U32)
                .ToArray(),
            static m =>
            {
                OnPairMetadata o = OnPairMetadata.Read(m);
                return $"{o.UncompressedLengthsPType},{F(o.DictionarySize)},{F(o.CodesLength)}," +
                       $"{o.DictionaryOffsetsPType},{o.CodesPType},{o.CodesOffsetsPType}";
            }));

        cases.Add(new MetadataCase(
            "DateTimePartsMetadata",
            new WireBuilder()
                .VarintField(1, (ulong)PType.I64)
                .VarintField(2, (ulong)PType.I32)
                .VarintField(3, (ulong)PType.I32)
                .ToArray(),
            static m =>
            {
                DateTimePartsMetadata d = DateTimePartsMetadata.Read(m);
                return $"{d.DaysPType},{d.SecondsPType},{d.SubsecondsPType}";
            }));

        cases.Add(new MetadataCase(
            "DecimalBytePartsMetadata",
            new WireBuilder().VarintField(1, (ulong)PType.I64).VarintField(2, 0).ToArray(),
            static m => DecimalBytePartsMetadata.Read(m).ZerothChildPType.ToString()));

        cases.Add(new MetadataCase(
            "ZstdMetadata",
            new WireBuilder()
                .VarintField(1, 128)
                .BytesField(2, new WireBuilder().VarintField(1, 32608).VarintField(2, 1024).ToArray())
                .BytesField(2, new WireBuilder().VarintField(1, 31).VarintField(2, 1).ToArray())
                .ToArray(),
            static m =>
            {
                Span<ZstdFrameMetadata> frames = stackalloc ZstdFrameMetadata[8];
                ZstdMetadata z = ZstdMetadata.Read(m, frames);
                string text = F(z.DictionarySize) + "|";
                for (int i = 0; i < z.FrameCount; i++)
                {
                    text += $"{F(frames[i].UncompressedSize)}:{F(frames[i].ValueCount)};";
                }

                return text;
            }));

        cases.Add(new MetadataCase(
            "FlatLayoutMetadata",
            new WireBuilder().BytesField(1, new byte[] { 9, 8, 7 }).ToArray(),
            static m =>
            {
                FlatLayoutMetadata f = FlatLayoutMetadata.Read(m);
                return $"{f.HasArrayEncodingTree},{Convert.ToHexString(f.ArrayEncodingTree)}";
            }));

        cases.Add(new MetadataCase(
            "DictLayoutMetadata",
            new WireBuilder()
                .VarintField(1, (ulong)PType.U32)
                .VarintField(2, 0)
                .VarintField(3, 1)
                .ToArray(),
            static m =>
            {
                DictLayoutMetadata d = DictLayoutMetadata.Read(m);
                return $"{d.CodesPType},{F(d.IsNullableCodes)},{F(d.AllValuesReferenced)}";
            }));

        cases.Add(new MetadataCase(
            "ListLayoutMetadata",
            new WireBuilder().VarintField(1, (ulong)PType.I32).ToArray(),
            static m => ListLayoutMetadata.Read(m).OffsetsPType.ToString()));

        return cases;
    }

    /// <summary>
    /// The <c>vortex.zoned</c> case, kept out of <see cref="All"/> because its payload is a version
    /// byte followed by the message rather than a bare message.
    /// </summary>
    internal static byte[] ZonedProtoTail { get; } = new WireBuilder()
        .VarintField(1, 8192)
        .BytesField(
            2,
            new WireBuilder()
                .BytesField(1, "vortex.min"u8)
                .BytesField(2, new byte[] { 0x08, 0x01 })
                .ToArray())
        .ToArray();

    internal static byte[] ZonedMetadataBytes { get; } =
        WireBuilder.InsertAt(ZonedProtoTail, 0, new byte[] { ZonedMetadata.SupportedVersion });

    internal static string DescribeZoned(ReadOnlySpan<byte> metadata)
    {
        AggregateSpecList specs = new AggregateSpecList();
        ZonedMetadata z = ZonedMetadata.Read(metadata, specs);
        string text = F(z.ZoneLength) + "|";
        for (int i = 0; i < z.AggregateSpecCount; i++)
        {
            text += $"{Convert.ToHexString(specs.GetIdUtf8(i))}:{Convert.ToHexString(specs.GetOptions(i))}:" +
                    $"{specs.GetAggregate(i)};";
        }

        return text;
    }
}
