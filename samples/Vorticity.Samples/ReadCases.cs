using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Vorticity.Samples;

/// <summary>The read cases, one sample per case.</summary>
internal static class ReadCases
{
    internal static void Register(Dictionary<string, Func<Task>> samples)
    {
        samples["getting-started"] = GettingStarted.RunAsync;
        samples["open-a-file"] = OpenAFile.RunAsync;
        samples["scan-a-table"] = ScanATable.RunAsync;
        samples["project-columns"] = ProjectColumns.RunAsync;
        samples["filter-rows"] = FilterRows.RunAsync;
        samples["nullable-columns"] = NullableColumns.RunAsync;
        samples["text-columns"] = TextColumns.RunAsync;
        samples["encoded-forms"] = EncodedForms.RunAsync;
        samples["selection"] = SelectionCase.RunAsync;
        samples["aggregates"] = Aggregates.RunAsync;
    }
}
