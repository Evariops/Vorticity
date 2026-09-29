// What a struct reader pays per batch to find the fields a projection selects.
//
// The walk `StructLayoutReader.Execute` makes over a struct of F fields: count the selected fields,
// then visit each with its sub-mask. `Original` is the walk as it was, every field asked
// `Includes` (a binary search) and each selected one searched again by `Descend`; `Library` visits
// the selected fields by position. The work done per selected field is a sum, so what is measured
// is the walk and nothing a child's decode would hide it under.
using BenchmarkDotNet.Attributes;

using Vorticity.Layouts;

namespace Vorticity.Benchmarks.Complexity;

/// <summary>One walk of a projection mask over a struct, against the struct's width.</summary>
[Config(typeof(BenchmarkConfig))]
[BenchmarkCategory(BenchmarkConfig.Explore)]
public class StructMaskWalkBenchmarks
{
    /// <summary>The struct's field count.</summary>
    [Params(16, 100, 1_000, 10_000)]
    public int Fields { get; set; }

    /// <summary>What the projection selects.</summary>
    [Params(Selection.One, Selection.Two, Selection.Sixteen, Selection.Half, Selection.All)]
    public Selection Selects { get; set; }

    /// <summary>A projection's shape.</summary>
    public enum Selection
    {
        /// <summary>One field, whole.</summary>
        One,

        /// <summary>Two fields.</summary>
        Two,

        /// <summary>Sixteen fields spread over the struct.</summary>
        Sixteen,

        /// <summary>Every other field.</summary>
        Half,

        /// <summary>Every field, recursively.</summary>
        All,
    }

    private FieldMask _mask;

    /// <summary>Builds the mask.</summary>
    [GlobalSetup]
    public void Setup()
    {
        FieldMaskBuilder builder = new FieldMaskBuilder();
        switch (Selects)
        {
            case Selection.One:
                builder.IncludeField(Fields / 2);
                break;
            case Selection.Two:
                builder.IncludeField(Fields / 3).IncludeField(2 * Fields / 3);
                break;
            case Selection.Sixteen:
                for (int i = 0; i < 16; i++)
                {
                    builder.IncludeField(i * Fields / 16);
                }

                break;
            case Selection.Half:
                for (int i = 0; i < Fields; i += 2)
                {
                    builder.IncludeField(i);
                }

                break;
            default:
                _mask = FieldMask.All;
                return;
        }

        _mask = builder.Build();
    }

    /// <summary>Every field asked whether it is selected, twice, and each selected one searched again.</summary>
    [Benchmark(Baseline = true)]
    public int Original()
    {
        int fieldCount = Fields;
        int selected = 0;
        for (int k = 0; k < fieldCount; k++)
        {
            if (_mask.Includes(k))
            {
                selected++;
            }
        }

        int sum = selected;
        for (int k = 0; k < fieldCount; k++)
        {
            if (!_mask.Includes(k))
            {
                continue;
            }

            FieldMask child = _mask.Descend(k);
            sum += k + (child.IsAll ? 1 : 0);
        }

        return sum;
    }

    /// <summary>The selected fields visited by position.</summary>
    [Benchmark]
    public int Library()
    {
        int selected = _mask.SelectedCount(Fields);
        int sum = selected;
        for (int s = 0; s < selected; s++)
        {
            int k = _mask.SelectedField(s);
            FieldMask child = _mask.SelectedMask(s);
            sum += k + (child.IsAll ? 1 : 0);
        }

        return sum;
    }
}
