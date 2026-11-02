// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;
using OKF4net.Tests.Providers;
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>
/// <see cref="SiteModel.Build"/> reads the bundle's <see cref="Bundle.Location"/> and the §8
/// display path of each <see cref="Bundle.ParseProblems"/> entry (spec §9, Viewer row), so the
/// strings a site carries are the ones the path-based projections printed: the location as
/// given, and for a problem the location plus the name with the OS separator — a rejected raw
/// name (no key) included. Driven by an <see cref="InMemoryBundleProvider"/> so the rejected name
/// needs no filesystem that can hold it.
/// </summary>
public class SiteModelLocationTests
{
    private const string Doc = "---\ntype: Note\ntitle: A\ndescription: a.\n---\n\nA.\n";

    [Fact]
    public void Build_takes_the_bundle_location_and_the_display_path_of_every_problem()
    {
        var storage = new InMemoryBundleProvider(id: "site")
            .Add("a.md", Doc)
            .Add("broken.md", "---\ntype: Note\ntitle: never closed\n")
            .Add("bad\tname.md", Doc);
        var bundle = Bundle.Load(storage);

        var site = SiteModel.Build(bundle);

        Assert.Equal("memory://site", bundle.Location);
        Assert.Equal(bundle.Location, site.BundleRoot);
        Assert.Equal("site", site.BundleName);
        Assert.Equal(2, bundle.ParseProblems.Count);
        Assert.Equal(
            bundle.ParseProblems.Select(p => (bundle.DisplayPath(p), p.Error)).ToList(),
            site.ParseErrors.Select(e => (e.Path, e.Error)).ToList());

        // The rejected raw name has no key: its display path is the raw name under the location.
        var rejected = Assert.Single(bundle.ParseProblems, p => p.Key is null);
        Assert.Equal("bad\tname.md", rejected.RawName);
        Assert.Equal(Path.Combine("memory://site", "bad\tname.md"), bundle.DisplayPath(rejected));
        Assert.Contains(site.ParseErrors, e => e.Path == Path.Combine("memory://site", "bad\tname.md"));

        // The unparseable concept has a key: its display path is the key's segments under the location.
        var broken = Assert.Single(bundle.ParseProblems, p => p.Key is not null);
        Assert.Equal(Path.Combine("memory://site", "broken.md"), bundle.DisplayPath(broken));
    }

    [Fact]
    public void DisplayPath_joins_a_nested_key_with_the_OS_separator()
    {
        var storage = new InMemoryBundleProvider(id: "site").Add("a/b/c.md", "---\ntype: Note\ntitle: never closed\n");
        var bundle = Bundle.Load(storage);

        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal(Path.Combine("memory://site", "a", "b", "c.md"), bundle.DisplayPath(problem));
        Assert.Throws<ArgumentNullException>(() => bundle.DisplayPath(null!));
    }
}
