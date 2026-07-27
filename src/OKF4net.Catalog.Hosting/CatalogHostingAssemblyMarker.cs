// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog.Hosting;

/// <summary>
/// Placeholder marker type for the <c>OKF4net.Catalog.Hosting</c> assembly.
/// </summary>
/// <remarks>
/// Exists solely to prove the project scaffold (build, references, dependency
/// boundaries -- including the <c>Microsoft.Extensions.DependencyInjection.Abstractions</c>
/// exception) end to end before the real DI registration surface lands. It
/// carries no behaviour and will be removed once a genuine public type takes
/// over as the project's compile-smoke anchor.
/// </remarks>
public static class CatalogHostingAssemblyMarker
{
    /// <summary>The assembly's short name, for smoke-test assertions.</summary>
    public const string Name = "OKF4net.Catalog.Hosting";
}
