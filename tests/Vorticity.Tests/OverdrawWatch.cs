using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using Vorticity.Aggregating;
using Xunit;

namespace Vorticity.Tests;

/// <summary>
/// Who gives back more of a query's memory than it holds: with <c>VORTICITY_OVERDRAW_WATCH</c> set to a directory, each time a
/// query gives back past what it holds, the test, the bytes and the stack are appended to a file of that directory, one a
/// process. Off, nothing is watched and nothing costs. The step that gave back first is the one to read: what comes after
/// falls short because of it.
/// </summary>
/// <remarks>
/// Run the classes that spill under a tight limit, where the governor takes the paths a roomy machine never does:
/// <c>VORTICITY_OVERDRAW_WATCH=dir VORTICITY_MEMORY_LIMIT=1g dotnet Vorticity.Tests.dll -namespace Vorticity.Tests.Aggregation</c>.
/// </remarks>
internal static class OverdrawWatch
{
    private static readonly Lock Gate = new Lock();

    [ModuleInitializer]
    internal static void Start()
    {
        if (Environment.GetEnvironmentVariable("VORTICITY_OVERDRAW_WATCH") is { Length: > 0 } directory)
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, $"overdraw-{Environment.ProcessId}.txt");
            QueryMemory.Overdrawn = (_, held, given) =>
            {
                string test = TestContext.Current.Test?.TestDisplayName ?? "(no test)";
                string line = string.Create(
                    CultureInfo.InvariantCulture,
                    $"=== {test}: held {held:N0}, gave back {given:N0}{Environment.NewLine}{new StackTrace(1, fNeedFileInfo: true)}{Environment.NewLine}");
                lock (Gate)
                {
                    System.IO.File.AppendAllText(path, line);
                }
            };
        }
    }
}
