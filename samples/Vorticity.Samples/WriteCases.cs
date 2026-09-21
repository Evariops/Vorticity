using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Vorticity.Samples;

/// <summary>The write cases, one sample per case.</summary>
internal static class WriteCases
{
    internal static void Register(Dictionary<string, Func<Task>> samples)
    {
        samples["write-a-file"] = WriteAFile.RunAsync;
        samples["write-rows"] = WriteRows.RunAsync;
        samples["write-text"] = WriteText.RunAsync;
        samples["write-nulls"] = WriteNulls.RunAsync;
        samples["write-lists-and-records"] = WriteListsAndRecords.RunAsync;
        samples["blocks-and-chunks"] = BlocksAndChunks.RunAsync;
        samples["append-and-repair"] = AppendAndRepair.RunAsync;
        samples["writer-options"] = WriterOptions.RunAsync;
        samples["indexes"] = Indexes.RunAsync;
        samples["editions"] = Editions.RunAsync;
        samples["stream-to-an-object"] = StreamToAnObject.RunAsync;
        samples["copy-a-file"] = CopyAFile.RunAsync;
        samples["write-without-a-record"] = WriteWithoutARecord.RunAsync;
    }
}
