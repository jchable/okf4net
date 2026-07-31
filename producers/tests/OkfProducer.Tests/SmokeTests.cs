// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Tests;

public class SmokeTests
{
    [Fact]
    public void OKF4net_types_are_reachable_from_this_solution()
    {
        var id = OKF4net.ConceptId.Parse("smoke/test");
        Assert.Equal("smoke/test", id.ToString());
    }
}
