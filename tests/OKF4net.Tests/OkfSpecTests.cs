// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Port of the Rust <c>okf::OKF_VERSION</c> constant (former
/// <c>src/lib.rs:68</c>), now exposed publicly as <see cref="OkfSpec.Version"/>.
/// </summary>
public class OkfSpecTests
{
    [Fact]
    public void Version_is_0_1()
    {
        Assert.Equal("0.1", OkfSpec.Version);
    }
}
