// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>Tests for the viewer's pure Bundle -> display-model projection.</summary>
public class SiteModelTests
{
    [Fact]
    public void Viewer_assembly_is_referenced()
        => Assert.Equal("OKF4net.Viewer", ViewerAssemblyMarker.Name);
}
