// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;

namespace OKF4net.Internal;

/// <summary>
/// The single reader of a build's own version, shared by this library's §7
/// producer actor (<see cref="BundleConceptWriter.DefaultProducerActor"/>) and
/// by the <c>okf</c> and <c>okf-render</c> binaries' <c>--version</c>.
///
/// The version is the assembly's <see cref="AssemblyInformationalVersionAttribute"/>,
/// which the SDK stamps from <c>&lt;Version&gt;</c> (Directory.Build.props, or
/// release.yml's <c>-p:Version</c>) — never a hand-maintained constant, which
/// drifted once (the winget package for 0.2.0 printed 0.1.0-alpha.1). The SemVer
/// build metadata the SDK may append (<c>+&lt;commit&gt;</c>) is dropped: it names
/// a source revision, not a version. Without the attribute, the assembly
/// version (three parts) is used, then <see cref="Unknown"/>.
///
/// AOT- and trim-safe: it reads an attribute on a statically-known assembly
/// (<c>typeof(X).Assembly</c>), not a dynamically discovered one; CI's Native
/// AOT job runs the published binary's <c>--version</c> to keep that honest.
/// </summary>
internal static class ProductVersion
{
    /// <summary>What is reported when an assembly carries no version at all.</summary>
    internal const string Unknown = "0.0.0-unknown";

    /// <summary>This library's own version (the <c>OKF4net</c> assembly), without build metadata.</summary>
    internal static string Library { get; } = Of(typeof(ProductVersion).Assembly);

    /// <summary>The version <paramref name="assembly"/> was built as, without build metadata.</summary>
    internal static string Of(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            var version = (plus < 0 ? informational : informational[..plus]).Trim();
            if (version.Length > 0)
            {
                return version;
            }
        }

        return assembly.GetName().Version?.ToString(3) ?? Unknown;
    }
}
