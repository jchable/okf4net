// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.Core.Generation;

/// <summary>
/// One project's compiler input set for one target framework, exactly as MSBuild reported it: the
/// <c>Compile</c> item group, per <c>(project, TFM)</c>.
/// </summary>
/// <param name="ProjectPath">
/// Path of the <c>.csproj</c>. Absolute (as MSBuild reports it) or already repository-relative --
/// <see cref="SourceOwnershipMap.From"/> normalizes either into the repository-relative, <c>/</c>-separated
/// form <c>PackageManifest.RelativePath</c> uses, which is the join key between a project and its
/// package concept.
/// </param>
/// <param name="TargetFramework">
/// The framework this item set was evaluated for (e.g. <c>net10.0</c>). A caller that queries a single
/// framework may pass it, or the empty string; either way a project with one entry never reports a
/// symbol as absent from a framework, because the only framework in its universe is the one that
/// claimed the file.
/// </param>
/// <param name="CompileFiles">The <c>Compile</c> items, absolute or repository-relative.</param>
public sealed record ProjectCompileItems(string ProjectPath, string TargetFramework, IReadOnlyList<string> CompileFiles);

/// <summary>
/// Which project compiles which source file -- the one honest answer to "which package does this
/// namespace belong to" (§5.1).
///
/// <para><b>Why this is passed in rather than computed here.</b> "A <c>.csproj</c> owns the files in
/// its folder" is false in MSBuild: a project can add and remove sources with <c>Compile
/// Include</c>/<c>Remove</c>, link files from outside its own directory, inherit items from a
/// <c>Directory.Build.props</c>, consume generated sources, and target several frameworks. The only
/// correct source is the evaluated <c>Compile</c> item set, which is what <c>MsBuildProjectQuery</c>
/// reads -- and that lives in <c>OkfProducer.CodeGraph.Roslyn</c>, which references this project and
/// not the reverse. So the composition root (the CLI, which references everything) runs the query and
/// hands the result in through <see cref="GenerateOptions.SourceOwnership"/>. When it hands in
/// nothing, <c>ConceptGenerator</c> emits <b>no</b> package -> namespace link and says so: an
/// incomplete spine is visible and harmless, while a namespace attributed to the wrong package by
/// guessing at the directory tree is a confident lie.</para>
///
/// <para><b>Two rules this type owns</b> (§5.1). A file claimed by more than one project -- a linked
/// file, or shared sources -- belongs to the <see cref="StringComparer.Ordinal"/>-first
/// <c>.csproj</c> path (<see cref="OwnerOf"/>), and the others are still reported
/// (<see cref="ClaimantsOf"/>) so the concept can name them rather than being duplicated. And a
/// project's files are the <b>union</b> across its target frameworks, with the frameworks that do not
/// compile a given file recoverable through <see cref="FrameworksAbsentFrom"/> -- one concept per TFM
/// would multiply the bundle for information nobody asks for at that level.</para>
///
/// <para>Every list this type returns is sorted <see cref="StringComparer.Ordinal"/>, so nothing that
/// reaches the bundle is ordered by a hash table (§6.2).</para>
/// </summary>
public sealed class SourceOwnershipMap
{
    private readonly Dictionary<string, List<string>> _claimantsByFile;
    private readonly Dictionary<string, List<string>> _frameworksByProject;
    private readonly Dictionary<string, List<string>> _frameworksByFileAndProject;

    private SourceOwnershipMap(
        Dictionary<string, List<string>> claimantsByFile,
        Dictionary<string, List<string>> frameworksByProject,
        Dictionary<string, List<string>> frameworksByFileAndProject)
    {
        _claimantsByFile = claimantsByFile;
        _frameworksByProject = frameworksByProject;
        _frameworksByFileAndProject = frameworksByFileAndProject;
    }

    /// <summary>A map that claims nothing -- every lookup is empty, so no link is ever attributed.</summary>
    public static SourceOwnershipMap Empty { get; } = new(
        new Dictionary<string, List<string>>(StringComparer.Ordinal),
        new Dictionary<string, List<string>>(StringComparer.Ordinal),
        new Dictionary<string, List<string>>(StringComparer.Ordinal));

    /// <summary>
    /// Builds the map from one <see cref="ProjectCompileItems"/> per <c>(project, TFM)</c>.
    ///
    /// <para>Both project paths and compile files are normalized against
    /// <paramref name="repositoryRoot"/> into repository-relative, <c>/</c>-separated form. A path
    /// that resolves <i>outside</i> the repository is dropped rather than stored under an absolute
    /// key: a source file the scan never walked declares no symbol this producer knows about, and an
    /// absolute path must never reach the bundle (§6.2).</para>
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="repositoryRoot"/> is null or empty.</exception>
    public static SourceOwnershipMap From(string repositoryRoot, IEnumerable<ProjectCompileItems> projects)
    {
        ArgumentException.ThrowIfNullOrEmpty(repositoryRoot);
        ArgumentNullException.ThrowIfNull(projects);

        var root = Path.GetFullPath(repositoryRoot);
        var claimantsByFile = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var frameworksByProject = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        var frameworksByFileAndProject = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);

        foreach (var project in projects)
        {
            if (project is null || Relativize(root, project.ProjectPath) is not { } projectPath)
            {
                continue;
            }

            var framework = project.TargetFramework ?? string.Empty;
            Add(frameworksByProject, projectPath, framework);

            foreach (var file in project.CompileFiles ?? [])
            {
                if (Relativize(root, file) is not { } filePath)
                {
                    continue;
                }

                Add(claimantsByFile, filePath, projectPath);
                Add(frameworksByFileAndProject, FileProjectKey(filePath, projectPath), framework);
            }
        }

        return new SourceOwnershipMap(
            Materialize(claimantsByFile),
            Materialize(frameworksByProject),
            Materialize(frameworksByFileAndProject));
    }

    /// <summary>
    /// Every project whose <c>Compile</c> item set claims <paramref name="relativePath"/>, sorted
    /// <see cref="StringComparer.Ordinal"/> by <c>.csproj</c> path -- so the first entry is
    /// <see cref="OwnerOf"/> and the rest are the other claimants the concept names.
    /// </summary>
    public IReadOnlyList<string> ClaimantsOf(string relativePath) =>
        _claimantsByFile.TryGetValue(Normalize(relativePath), out var claimants) ? claimants : [];

    /// <summary>
    /// The single project <paramref name="relativePath"/> is attributed to -- the
    /// <see cref="StringComparer.Ordinal"/>-first of <see cref="ClaimantsOf"/> -- or
    /// <see langword="null"/> when no project's <c>Compile</c> item set claims it.
    /// </summary>
    public string? OwnerOf(string relativePath) => ClaimantsOf(relativePath) is [var first, ..] ? first : null;

    /// <summary>
    /// The target frameworks <see cref="OwnerOf"/>'s project builds for but that do <b>not</b> compile
    /// <paramref name="relativePath"/> -- what lets a symbol declared in a conditionally-compiled file
    /// say so in its own body instead of silently claiming to exist under every framework. Empty for a
    /// file whose owner was reported under a single framework, which is the ordinary case.
    /// </summary>
    public IReadOnlyList<string> FrameworksAbsentFrom(string relativePath)
    {
        var path = Normalize(relativePath);
        if (OwnerOf(path) is not { } owner
            || !_frameworksByProject.TryGetValue(owner, out var all))
        {
            return [];
        }

        IReadOnlyList<string> claiming = _frameworksByFileAndProject.TryGetValue(FileProjectKey(path, owner), out var found)
            ? found
            : [];

        // `all` is already Ordinal-sorted and Except preserves its order, so the result is too.
        return [.. all.Except(claiming, StringComparer.Ordinal)];
    }

    /// <summary>
    /// The <c>(file, project)</c> composite key, joined with <c>NUL</c>: no path segment on any
    /// platform can contain it, so the key is unambiguous where a <c>/</c> join would not be.
    /// </summary>
    private static string FileProjectKey(string file, string project) => file + '\0' + project;

    private static void Add(Dictionary<string, SortedSet<string>> map, string key, string value)
    {
        if (!map.TryGetValue(key, out var values))
        {
            values = new SortedSet<string>(StringComparer.Ordinal);
            map[key] = values;
        }

        values.Add(value);
    }

    private static Dictionary<string, List<string>> Materialize(Dictionary<string, SortedSet<string>> map)
    {
        var result = new Dictionary<string, List<string>>(map.Count, StringComparer.Ordinal);
        foreach (var (key, values) in map)
        {
            result[key] = [.. values];
        }

        return result;
    }

    /// <summary>
    /// <paramref name="path"/> as a repository-relative, <c>/</c>-separated path, or
    /// <see langword="null"/> when it lies outside <paramref name="root"/>. A path that is already
    /// relative is taken as-is (only separator-normalized): the caller has then already expressed it
    /// in the same space <c>SymbolFact.RelativePath</c> and <c>PackageManifest.RelativePath</c> use.
    /// </summary>
    private static string? Relativize(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var trimmed = path.Trim();
        if (!Path.IsPathRooted(trimmed))
        {
            return Normalize(trimmed);
        }

        var relative = Normalize(Path.GetRelativePath(root, trimmed));

        return relative.StartsWith("../", StringComparison.Ordinal) || relative == ".." || Path.IsPathRooted(relative)
            ? null
            : relative;
    }

    /// <summary>
    /// Separator normalization only (<c>\</c> -> <c>/</c>, a leading <c>./</c> dropped): one spelling
    /// per path, so a lookup cannot miss on a separator the caller happened to use. Never
    /// case-folded -- §6.2 pins <see cref="StringComparer.Ordinal"/> for every path comparison in this
    /// producer, and case-folding here would drop a genuinely distinct file on a case-sensitive
    /// filesystem.
    ///
    /// <para>Public because this map's keys are a join: <c>ConceptGenerator</c> looks a project up by
    /// the <c>PackageManifest.RelativePath</c> of its manifest, and a join whose two sides normalize by
    /// different rules is one that silently returns nothing. One rule, in one place.</para>
    /// </summary>
    public static string Normalize(string path)
    {
        var normalized = path.Replace('\\', '/');
        return normalized.StartsWith("./", StringComparison.Ordinal) ? normalized[2..] : normalized;
    }
}
