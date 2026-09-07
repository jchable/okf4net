// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Xml.Linq;
using OkfProducer.Core.Generation;
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
    public static bool IsEligible(string relativePath, RepositorySnapshot snapshot, ScopeOptions scope, IFileSystemReader? reader = null)
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

        return !IsOwnedByTestProject(relativePath, snapshot, reader ?? SystemFileReader.Instance);
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

    /// <summary>
    /// <see cref="IsInScope(SymbolFact, ScopeOptions)"/> against a symbol's EFFECTIVE visibility: the
    /// least visible of its own modifier and every type that encloses it.
    ///
    /// <para><b>Why the declared modifier alone was the wrong filter.</b> C# caps a member at its
    /// container: <c>public void Never()</c> inside <c>internal class Hidden</c> is not reachable from
    /// outside the assembly, whatever the keyword says. Filtering on the modifier alone put that member
    /// in a default-scope bundle -- tagged <c>public</c>, a visibility the language does not give it --
    /// so an operator who left <c>--include-internal</c> off precisely to keep internal API out of a
    /// shipped knowledge bundle got it published anyway, and mislabelled. The producer's own fixture
    /// carries that shape, so the golden blessed it.
    /// </para>
    ///
    /// <para><b>Why it takes the whole declared set.</b> A <see cref="SymbolFact"/> names its container
    /// as a dotted path and nothing more, so the container's own visibility is not on it; the cap can
    /// only be computed where every declared symbol is in hand. <paramref name="declared"/> must be the
    /// UNFILTERED set -- filtering first would drop the very containers that do the capping, and a
    /// member whose container is missing would then look top-level and slip through.</para>
    ///
    /// <para>A container that is not among <paramref name="declared"/> caps nothing: it is a namespace,
    /// or a type in a file this run did not read. Treating an unknown container as private would delete
    /// concepts over a file the run merely failed to open, which §2.3 rates well below keeping them.</para>
    /// </summary>
    /// <param name="fact">The symbol to test.</param>
    /// <param name="declared">Every symbol this run extracted, before any scope filtering.</param>
    /// <param name="scope">The run's scope options.</param>
    public static bool IsInScope(SymbolFact fact, IReadOnlyDictionary<(string Container, string Name), SymbolFact> declared, ScopeOptions scope)
    {
        ArgumentNullException.ThrowIfNull(fact);
        ArgumentNullException.ThrowIfNull(declared);

        var effective = fact.Visibility;
        var container = fact.Container;

        // Upward one type at a time, taking the least visible seen. Bounded by the container's segment
        // count, so a cycle is not expressible: each step strictly shortens the path.
        while (container.Length > 0 && effective != SymbolVisibility.Private)
        {
            var lastDot = container.LastIndexOf('.');
            var parentContainer = lastDot < 0 ? string.Empty : container[..lastDot];
            var name = lastDot < 0 ? container : container[(lastDot + 1)..];

            if (declared.TryGetValue((parentContainer, name), out var enclosing) && enclosing.Kind == SymbolKind.Type)
            {
                effective = Least(effective, enclosing.Visibility);
            }

            container = parentContainer;
        }

        return IsInScope(fact with { Visibility = effective }, scope);
    }

    /// <summary>
    /// The less visible of two tiers. Written as an explicit ladder rather than as a comparison on the
    /// enum's numeric order: the members happen to be declared most-visible-first today, so
    /// <c>left > right</c> would work and would silently invert if anyone reordered them.
    /// </summary>
    private static SymbolVisibility Least(SymbolVisibility left, SymbolVisibility right)
    {
        if (left == SymbolVisibility.Private || right == SymbolVisibility.Private)
        {
            return SymbolVisibility.Private;
        }

        return left == SymbolVisibility.Internal || right == SymbolVisibility.Internal
            ? SymbolVisibility.Internal
            : SymbolVisibility.Public;
    }

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
    private static bool IsOwnedByTestProject(string relativePath, RepositorySnapshot snapshot, IFileSystemReader reader)
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
        return ReferencesTestSdk(absoluteCsprojPath, reader);
    }

    // Case-insensitively on Windows and ordinally elsewhere -- BundlePaths.PathComparison, the
    // codebase's one answer to "are these the same path", rather than a second rule written here.
    //
    // This was a flat Ordinal, argued for on the grounds that a case-sensitive filesystem can hold
    // both src/Foo and src/foo as distinct directories, which is true and is exactly what
    // PathComparison already encodes. What the argument missed is where the two sides come from. A
    // file's path is walked off disk; the project's comes from `PackageManifest.RelativePath`, which
    // RepositoryScanner may have read out of a `.sln`'s TEXT and never normalised against disk. On
    // Windows a differently-cased entry there still passes `File.Exists`, so the project was found,
    // its `.csproj` was read, and then this comparison rejected it -- and a test project in a
    // non-conventionally-named directory was silently INCLUDED with `--include-tests` off, which is
    // the one direction §5.4 cannot afford to get wrong.
    //
    // Still never culture-dependent, which is what §6.2 forbids: OrdinalIgnoreCase is not a
    // linguistic comparison.
    private static bool IsAncestorOrSame(string[] directory, string[] descendant)
    {
        if (directory.Length > descendant.Length)
        {
            return false;
        }

        for (var i = 0; i < directory.Length; i++)
        {
            if (!string.Equals(directory[i], descendant[i], BundlePaths.PathComparison))
            {
                return false;
            }
        }

        return true;
    }

    private static bool ReferencesTestSdk(string absoluteCsprojPath, IFileSystemReader reader)
    {
        if (reader.TryGetLength(absoluteCsprojPath) is null)
        {
            return false;
        }

        try
        {
            using var stream = reader.OpenRead(absoluteCsprojPath);
            var xml = XDocument.Load(stream);
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
