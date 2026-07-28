// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Tests for the OKF spec version constant, exposed publicly as
/// <see cref="OkfSpec.Version"/>.
/// </summary>
public class OkfSpecTests
{
    [Fact]
    public void Version_is_0_2()
    {
        Assert.Equal("0.2", OkfSpec.Version);
    }
}
