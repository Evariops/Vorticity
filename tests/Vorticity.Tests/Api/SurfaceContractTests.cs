using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Xunit;

namespace Vorticity.Tests.Api;

/// <summary>The promise about vxdump: the public surface is enough for it.</summary>
[Trait("Category", "ApiContract")]
public sealed class SurfaceContractTests
{
    [Fact]
    public void NoLibraryOpensItsInternalsToVxdump()
    {
        Assembly[] libraries =
        [
            typeof(VortexFile).Assembly,
            typeof(Vorticity.Dataset.VortexDataset).Assembly,
            typeof(Vorticity.RowEncoding.RowEncoder).Assembly,
        ];

        // The attribute is read rather than the project files, because it is what the compiler
        // honours; this test project's own name among the friends shows the reading works.
        List<(string Library, string Friend)> friends = [];
        foreach (Assembly library in libraries)
        {
            foreach (InternalsVisibleToAttribute friend in library.GetCustomAttributes<InternalsVisibleToAttribute>())
            {
                friends.Add((library.GetName().Name!, friend.AssemblyName.Split(',')[0].Trim()));
            }
        }

        Assert.Contains(friends, pair => pair.Friend == typeof(SurfaceContractTests).Assembly.GetName().Name);
        Assert.DoesNotContain(friends, pair => pair.Friend.Equals("vxdump", StringComparison.OrdinalIgnoreCase));
    }
}
