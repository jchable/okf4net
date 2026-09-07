// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;

namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// Builds the <c>name/version</c> tokens §6.2 puts in <c>overview</c>'s <c>generated.engines</c>.
///
/// <para>Here rather than in either engine project so the two cannot come to spell a version
/// differently, and so <c>OkfProducer.Core</c> -- which references neither engine -- can define the
/// shape without knowing who fills it. Each engine names ITSELF by handing over its own assembly;
/// the composition root collects the tokens and passes them to <c>GenerateOptions</c>.</para>
/// </summary>
public static class EngineVersions
{
    /// <summary>
    /// <paramref name="name"/> joined to <paramref name="assembly"/>'s version by <c>/</c>.
    ///
    /// <para>The informational version is preferred because it identifies the exact BUILD, where the
    /// assembly version is often frozen across a package's whole major line -- and identifying the
    /// build is what §6.2 needs, since determinism holds at a fixed extractor and two builds of one
    /// major line can move symbols. Anything after a <c>+</c> is dropped: SourceLink appends the commit
    /// sha there, which would put a 40-character hash into every regenerated bundle and make the field
    /// churn on rebuilds that changed no dependency.</para>
    ///
    /// <para><b>It is NOT always the NuGet package version, and this comment used to say it was.</b>
    /// Measured on the pinned <c>Microsoft.CodeAnalysis.CSharp</c>: the package is <c>5.3.0</c> in both
    /// the <c>.csproj</c> and <c>packages.lock.json</c>, while the informational version is
    /// <c>5.3.0-2.26078.5+16f9bd2</c>, so the recorded token is <c>roslyn/5.3.0-2.26078.5</c> -- a
    /// Roslyn build id that matches no package a reader can look up. That is the right value to record
    /// anyway, for the reason above; what was wrong was the promise that a reader could resolve it to a
    /// package. For tree-sitter the two coincide (<c>tree-sitter/1.3.0</c>), which is how the claim
    /// went unchallenged.</para>
    ///
    /// <para>An assembly that declares neither falls back to <c>unknown</c> rather than being omitted:
    /// a run whose engine version could not be read is a run whose determinism claim is weaker, and
    /// saying so is the point of the field.</para>
    /// </summary>
    public static string Token(string name, Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var plus = informational.IndexOf('+', StringComparison.Ordinal);
            return $"{name}/{(plus < 0 ? informational : informational[..plus])}";
        }

        return $"{name}/{assembly.GetName().Version?.ToString(3) ?? "unknown"}";
    }
}
