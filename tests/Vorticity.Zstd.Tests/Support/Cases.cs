using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;
using Xunit.Sdk;

namespace Vorticity.Zstd.Tests.Support;

/// <summary>
/// Many cases checked in one test: each runs whatever the others did, and every one that fails is
/// reported, under its name. A theory row per case would make thousands of them, each as cheap as
/// the bookkeeping around it, so a row holds the cases of one kind, or one size.
/// </summary>
internal static class Cases
{
    /// <summary>Runs <paramref name="check"/> on every case and fails with every failure, each named.</summary>
    public static void CheckAll<T>(IEnumerable<T> cases, Action<T> check) =>
        CheckAll(cases, check, @case => $"{@case}");

    /// <summary>The same, each case named by <paramref name="name"/>.</summary>
    public static void CheckAll<T>(IEnumerable<T> cases, Action<T> check, Func<T, string> name) =>
        Assert.Multiple([.. cases.Select(@case => (Action)(() => Named(@case, check, name)))]);

    private static void Named<T>(T @case, Action<T> check, Func<T, string> name)
    {
        try
        {
            check(@case);
        }
        catch (Exception error)
        {
            throw new XunitException($"{name(@case)}: {error.Message}", error);
        }
    }
}
