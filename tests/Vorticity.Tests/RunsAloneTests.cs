using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Xunit;

namespace Vorticity.Tests;

/// <summary>
/// The tests of a collection that disables parallelization measure what anything beside them would
/// move: allocations, peaks, finalizers, a shared pool. CI gives them a runner of their own, selected by
/// the trait <c>Category=RunsAlone</c>, and runs the rest of the suite on others without them. A class
/// that joins such a collection without the trait would still run alone, but at the end of a shard, and
/// a class that carries the trait outside one would leave its shard for the runner that measures.
/// </summary>
public sealed class RunsAloneTests
{
    [Fact]
    public void EveryClassOfACollectionThatRunsAloneCarriesTheTrait()
    {
        Type[] types = typeof(RunsAloneTests).Assembly.GetTypes();
        HashSet<string> alone = [.. types
            .Select(type => type.GetCustomAttribute<CollectionDefinitionAttribute>())
            .Where(definition => definition is { DisableParallelization: true, Name: not null })
            .Select(definition => definition!.Name!)];
        Assert.NotEmpty(alone);

        List<string> wrong = [];
        foreach (Type type in types)
        {
            string? collection = type.GetCustomAttribute<CollectionAttribute>()?.Name;
            bool runsAlone = collection is not null && alone.Contains(collection);
            bool marked = type.GetCustomAttributes<TraitAttribute>().Any(trait => trait is { Name: "Category", Value: "RunsAlone" });
            if (runsAlone != marked)
            {
                wrong.Add(runsAlone
                    ? $"{type.FullName} runs alone in {collection} without [Trait(\"Category\", \"RunsAlone\")]"
                    : $"{type.FullName} carries [Trait(\"Category\", \"RunsAlone\")] in no collection that runs alone");
            }
        }

        Assert.True(wrong.Count == 0, string.Join("\n", wrong));
    }
}
