using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Vorticity.Samples;

/// <summary>The pages that are not a single read or write case: sessions, errors, the satellites, the tool.</summary>
internal static class MoreCases
{
    internal static void Register(Dictionary<string, Func<Task>> samples)
    {
        samples["records"] = Records.RunAsync;
        samples["errors"] = Errors.RunAsync;
        samples["limits"] = Limits.RunAsync;
        samples["observability"] = Observability.RunAsync;
        samples["datasets"] = Datasets.RunAsync;
        samples["dataset-maintenance"] = DatasetMaintenance.RunAsync;
        samples["object-store"] = ObjectStore.RunAsync;
        samples["row-keys"] = RowKeys.RunAsync;
        samples["how-it-works"] = HowItWorks.RunAsync;
    }
}
