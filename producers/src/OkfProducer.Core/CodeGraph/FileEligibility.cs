// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Xml.Linq;
using OkfProducer.Core.Scanning;

namespace OkfProducer.Core.CodeGraph;

/// <summary>
/// Decides which files and symbols one extraction run covers (§5.4). Deliberately does not
/// hard-code a convention like <c>src/</c> -- that convention is only this repository's --
/// so <see cref="IsEligible"/> excludes build output and vendored/version-control directories by
/// name, excludes test projects and conventionally-named test directories (both liftable with
/// <see cref="ScopeOptions.IncludeTests"/>), and <see cref="IsInScope"/> filters declared symbols
/// purely by <see cref="SymbolFact.Visibility"/>, never by path.
/// </summary>
public static class FileEligibility
{
    private const string TestSdkPackageId = "Microsoft.NET.Test.Sdk";

    private static readonly string[] ExcludedDirectorySegments = ["bin", "obj", "node_modules", ".git"];
    private static readonly string[] TestDirectorySegments = ["test", "tests", "spec"];

    /// <summary>
    /// Whether <paramref name="relativePath"/> should be walked by <see cref="CodeGraphBuilder.Build"/>
    /// at all, before any <see cref="LanguageProfile"/> or hostile-input check runs. Build output and
    /// vendored/version-control directories (<c>bin</c>, <c>obj</c>, <c>node_modules</c>, <c>.git</c>)
    /// are always rejected. Unless <paramref name="scope"/>'s <see cref="ScopeOptions.IncludeTests"/>
    /// is set, a file owned by a project whose <c>.csproj</c> references a test SDK (e.g.
    /// <c>Microsoft.NET.Test.Sdk</c>, read from the project data <paramref name="snapshot"/> already
    /// discovered) is rejected, and so is a file under a conventionally-named <c>test</c>/
    /// <c>tests</c>/<c>spec</c> directory, even one with no owning project at all. A file this method
    /// rejects is out of scope for this run entirely: it produces no <see cref="FileStatus"/> entry
    /// and never affects <see cref="RunStatus.TraversalComplete"/> or <see cref="RunStatus.IsComplete"/>
    /// -- the same treatment <see cref="CodeGraphBuilder.Build"/> already gives a file matching no
    /// <see cref="LanguageProfile"/>.
    /// </summary>
    public static bool IsEligible(string relativePath, RepositorySnapshot snapshot, ScopeOptions scope)
    {
        var directorySegments = DirectorySegments(relativePath);

        foreach (var segment in directorySegments)
        {
            if (ContainsIgnoreCase(ExcludedDirectorySegments, segment))
            {
                return false;
            }
        }

        if (scope.IncludeTests)
        {
            return true;
        }

        foreach (var segment in directorySegments)
        {
            if (ContainsIgnoreCase(TestDirectorySegments, segment))
            {
                return false;
            }
        }

        return !IsOwnedByTestProject(relativePath, snapshot);
    }

    /// <summary>
    /// Whether <paramref name="fact"/> belongs in the extracted graph, filtered purely by
    /// <see cref="SymbolFact.Visibility"/> (§5.4) -- never by <see cref="SymbolFact.RelativePath"/>,
    /// which is exactly the rule that lets scope stay a convention-free visibility filter rather than
    /// a hard-coded path prefix. <see cref="SymbolVisibility.Public"/> is always in scope,
    /// <see cref="SymbolVisibility.Private"/> never is, and <see cref="SymbolVisibility.Internal"/>
    /// depends on <paramref name="scope"/>'s <see cref="ScopeOptions.IncludeInternal"/>.
    /// </summary>
    public static bool IsInScope(SymbolFact fact, ScopeOptions scope) =>
        fact.Visibility switch
        {
            SymbolVisibility.Public => true,
            SymbolVisibility.Internal => scope.IncludeInternal,
            _ => false,
        };

    private static string[] DirectorySegments(string relativePath)
    {
        var segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return segments.Length > 0 ? segments[..^1] : segments;
    }

    private static bool ContainsIgnoreCase(string[] names, string segment)
    {
        foreach (var name in names)
        {
            if (string.Equals(name, segment, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Finds the nuget <see cref="PackageManifest"/> (already discovered by
    /// <see cref="Scanning.RepositoryScanner"/> into <paramref name="snapshot"/>) whose directory most
    /// closely (deepest) contains <paramref name="relativePath"/>, and checks whether its
    /// <c>.csproj</c> references <see cref="TestSdkPackageId"/>. Reading the manifest's already
    /// resolved path back off disk, rather than adding raw project data to
    /// <see cref="RepositorySnapshot"/>, keeps that record's shape unchanged for every other consumer.
    /// </summary>
    private static bool IsOwnedByTestProject(string relativePath, RepositorySnapshot snapshot)
    {
        var fileDirectory = DirectorySegments(relativePath);

        string? bestProjectPath = null;
        var bestDepth = -1;

        foreach (var package in snapshot.Packages)
        {
            if (package.Ecosystem != "nuget")
            {
                continue;
            }

            var projectDirectory = DirectorySegments(package.RelativePath);
            if (!IsAncestorOrSame(projectDirectory, fileDirectory) || projectDirectory.Length <= bestDepth)
            {
                continue;
            }

            bestDepth = projectDirectory.Length;
            bestProjectPath = package.RelativePath;
        }

        if (bestProjectPath is null)
        {
            return false;
        }

        var absoluteCsprojPath = Path.Combine(snapshot.RepoPath, bestProjectPath.Replace('/', Path.DirectorySeparatorChar));
        return ReferencesTestSdk(absoluteCsprojPath);
    }

    // Ordinal, not OrdinalIgnoreCase: every other path comparison in this codebase (§6.2's "never a
    // culture-dependent comparison" rule) is Ordinal, and a case-sensitive filesystem can genuinely
    // hold both src/Foo and src/foo as distinct directories -- OrdinalIgnoreCase here could pick the
    // wrong one as a file's owning project.
    private static bool IsAncestorOrSame(string[] directory, string[] descendant)
    {
        if (directory.Length > descendant.Length)
        {
            return false;
        }

        for (var i = 0; i < directory.Length; i++)
        {
            if (!string.Equals(directory[i], descendant[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReferencesTestSdk(string absoluteCsprojPath)
    {
        if (!File.Exists(absoluteCsprojPath))
        {
            return false;
        }

        try
        {
            var xml = XDocument.Load(absoluteCsprojPath);
            return (xml.Root?.Descendants().Where(e => e.Name.LocalName == "PackageReference") ?? [])
                .Any(e => string.Equals((string?)e.Attribute("Include"), TestSdkPackageId, StringComparison.OrdinalIgnoreCase));
        }
        // Every way XDocument.Load can fail on a path that existed a moment ago, not just the two
        // that were listed here. This method is called from CodeGraphBuilder.Build's per-file loop,
        // which deliberately does NOT wrap its body -- so an exception escaping here does not degrade
        // one file, it aborts the whole repository's run. `File.Exists` above narrows nothing: it
        // answers for the instant it was asked, and it returns false for a directory, so the
        // interesting failures are the ones it cannot see -- an ACL that denies read, a path the
        // platform rejects, a file deleted between the two calls.
        //
        // NOT covered by an executable test, and that is a real gap rather than an oversight:
        // UnauthorizedAccessException and SecurityException cannot be provoked here portably (a
        // read-denying ACL is Windows-specific and needs elevation the suite does not have), and the
        // two that CAN be provoked -- XmlException, IOException -- were already caught before this
        // change. Covering the rest needs a loader seam on this type; see the register entry.
        catch (Exception e) when (e is System.Xml.XmlException
            or IOException
            or UnauthorizedAccessException
            or System.Security.SecurityException
            or NotSupportedException)
        {
            return false;
        }
    }
}
