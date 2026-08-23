using System;
using Xunit;
using LamSims.Core;

namespace LamSims.Core.Tests;

public class PathIdentityTests
{
    [Fact]
    public void Canonical_collapses_case_and_composition_but_not_different_paths()
    {
        var a = PathIdentity.Canonical("/games/Caf\u00e9");        // composed
        var b = PathIdentity.Canonical("/games/Cafe\u0301");        // decomposed
        var c = PathIdentity.Canonical("/games/CAF\u00c9");
        var d = PathIdentity.Canonical("/games/Other");

        Assert.Equal(a, b);
        Assert.Equal(a, c, StringComparer.OrdinalIgnoreCase);
        Assert.NotEqual(a, d, StringComparer.OrdinalIgnoreCase);
    }
}
