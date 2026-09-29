using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace Vorticity.Tests.Types;

public sealed class FieldNameLookupTests
{
    [Theory]
    [InlineData(8)]
    [InlineData(40)]
    public void AFieldIsFoundByNameAndByPathWhateverTheWidth(int width)
    {
        // A walk over a few fields, a dictionary over many, and the same answers: the first of a
        // repeated name, a top-level name that holds a dot before any path, a path through a struct,
        // and nothing for a name no field bears or a path through a column without fields.
        VortexType inner = VortexType.Struct([.. Enumerable.Range(0, width).Select(i => new VortexField($"leaf{i}", VortexType.Int64))]);
        List<VortexField> fields = [.. Enumerable.Range(0, width).Select(i => new VortexField($"col{i}", VortexType.Int64))];
        fields[width - 1] = new VortexField("col1", VortexType.Utf8);
        fields.Add(new VortexField("nested", inner));
        fields.Add(new VortexField("nested.leaf3", VortexType.Bool));
        VortexSchema schema = VortexSchema.Create([.. fields]);

        for (int i = 0; i < width - 1; i++)
        {
            Assert.Equal(i, schema.IndexOf($"col{i}"));
        }

        Assert.Equal(width + 1, schema.IndexOf("nested.leaf3"));
        Assert.Equal(5, schema.IndexOf("nested.leaf5"));
        Assert.Equal(width - 1, schema.IndexOf($"nested.leaf{width - 1}"));
        Assert.Equal(-1, schema.IndexOf($"nested.leaf{width}"));
        Assert.Equal(-1, schema.IndexOf($"col{width + 5}"));
        Assert.Equal(-1, schema.IndexOf("col0.leaf1"));
        Assert.True(schema.TryGetField("nested.leaf5"u8, out int leaf));
        Assert.Equal(5, leaf);
        Assert.False(schema.TryGetField([0xC3, 0x28], out _));

        // The tool scan's column names resolve the same way, a path to its indices.
        Assert.Equal([width, 5], ToolPaths.Resolve(schema, "nested.leaf5"));
        Assert.Equal([1], ToolPaths.Resolve(schema, "col1"));
        Assert.Throws<VortexSchemaException>(() => ToolPaths.Resolve(schema, $"nested.leaf{width}"));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(40)]
    public void AMemberFindsItsColumnExactlyThenWithCaseIgnoredAndNamesTheTwoItCannotChooseBetween(int width)
    {
        VortexField[] fields = [.. Enumerable.Range(0, width).Select(i => new VortexField($"col{i}", VortexType.Int64))];
        fields[2] = new VortexField("Amount", VortexType.Int64);
        fields[3] = new VortexField("amount", VortexType.Int64);
        fields[5] = new VortexField("Total", VortexType.Int64);
        fields[width - 1] = new VortexField("AMOUNT", VortexType.Int64);
        ColumnNames names = new ColumnNames(fields);

        Assert.Equal(2, names.Exact("Amount"));
        Assert.Equal(3, names.Exact("amount"));
        Assert.Equal(-1, names.Exact("aMOUNT"));
        Assert.Equal((2, 3), names.Loose("aMOUNT"));
        Assert.Equal((5, -1), names.Loose("total"));
        Assert.Equal((-1, -1), names.Loose("missing"));
    }
}
