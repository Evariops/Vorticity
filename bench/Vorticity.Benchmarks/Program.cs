// The benchmark host.
//
// `dotnet run -c Release --project bench/Vorticity.Benchmarks` runs everything;
// `-- --filter '*Decode*'` narrows it, which is what a kernel change wants.
using BenchmarkDotNet.Running;

namespace Vorticity.Benchmarks;

internal static class Program
{
    private static void Main(string[] args) =>
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);
}
