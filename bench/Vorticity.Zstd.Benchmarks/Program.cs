using System;
using System.Linq;
using BenchmarkDotNet.Running;

namespace Vorticity.Zstd.Benchmarks;

public static class Program
{
    public static void Main(string[] args)
    {
        BenchmarkConfig.InProcess = args.Contains("--inprocess");
        string[] rest = args.Where(a => a != "--inprocess").ToArray();
        BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(rest, new BenchmarkConfig());
    }
}
