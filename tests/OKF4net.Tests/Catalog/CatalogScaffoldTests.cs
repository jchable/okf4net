// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Catalog;
using OKF4net.Catalog.Hosting;

namespace OKF4net.Tests.Catalog;

/// <summary>
/// Compile-smoke coverage for the <c>OKF4net.Catalog</c> and
/// <c>OKF4net.Catalog.Hosting</c> project scaffold: proves the reference
/// graph (Hosting -&gt; Catalog -&gt; OKF4net, plus this test project
/// referencing both) actually builds and resolves types at runtime.
/// </summary>
public class CatalogScaffoldTests
{
    [Fact]
    public void Catalog_assembly_marker_reports_expected_name()
    {
        Assert.Equal("OKF4net.Catalog", CatalogAssemblyMarker.Name);
    }

    [Fact]
    public void Catalog_hosting_assembly_marker_reports_expected_name()
    {
        Assert.Equal("OKF4net.Catalog.Hosting", CatalogHostingAssemblyMarker.Name);
    }
}
