// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using OKF4net.Attestation;
using Xunit;

namespace OKF4net.Tests.Attestation;

public class AttestationValuesTests
{
    [Fact]
    public void Registry_returns_registered_runtime_and_misses_unknown()
    {
        var rt = new FakeRuntime();
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = rt });
        Assert.True(reg.TryGet("bigquery", out var found));
        Assert.Same(rt, found);
        Assert.False(reg.TryGet("python", out _));
    }
}
