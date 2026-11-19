// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;
using OKF4net.Providers.EmbeddedResources;

namespace OKF4net.Tests.Providers;

/// <summary>
/// D7 (spec of 2026-11-12 §4.4): the two path-based [Obsolete] members read through System.IO from
/// <see cref="Bundle.Root"/>, which for a provider other than the filesystem is a display string that
/// can name an ordinary relative directory (on Linux, "embedded:App/kb" under the current directory).
/// They refuse such a provider, like <c>BundleConceptWriter.BundleRoot</c> already does.
/// </summary>
#pragma warning disable CS0618 // the members under test are obsolete
public class ObsoletePathMembersTests
{
    public static TheoryData<string> Providers => new() { "memory", "embedded" };

    private static Bundle Load(string kind) => kind == "memory"
        ? Bundle.Load(new InMemoryBundleProvider().Add("notes/a.md", "---\ntype: Note\ntitle: A\ndescription: a.\n---\n\nA\n"))
        : Bundle.Load(new EmbeddedResourceBundleProvider(typeof(ObsoletePathMembersTests).Assembly, "contract"));

    [Theory]
    [MemberData(nameof(Providers))]
    public void TryResolveResource_refuses_a_non_filesystem_provider(string kind)
    {
        var bundle = Load(kind);
        var concept = bundle.Concepts[0];
        var e = Assert.Throws<NotSupportedException>(() => bundle.TryResolveResource(concept, "./x.sql", out _, out _));
        Assert.Contains("TryResolveResourceKey", e.Message, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Providers))]
    public void ReadResourceText_by_path_refuses_a_non_filesystem_provider(string kind)
    {
        var bundle = Load(kind);
        var e = Assert.Throws<NotSupportedException>(() => bundle.ReadResourceText("x.sql"));
        Assert.Contains("ReadResourceText(BundleKey)", e.Message, StringComparison.Ordinal);
    }
}
#pragma warning restore CS0618
