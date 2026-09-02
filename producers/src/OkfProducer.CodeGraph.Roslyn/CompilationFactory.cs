// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using OkfProducer.Core.CodeGraph;

namespace OkfProducer.CodeGraph.Roslyn;

/// <summary>
/// Thrown when a project pins a <c>LangVersion</c> this build of
/// <c>Microsoft.CodeAnalysis.CSharp</c> does not recognise (correction 3).
///
/// <para>
/// Derives from <see cref="InvalidOperationException"/> so the contract callers were given still
/// holds, but is its own type so <see cref="RoslynResolver"/> can catch <i>this</i> rather than
/// catching every <see cref="InvalidOperationException"/> a compilation might raise -- a broad catch
/// there would quietly turn a genuine bug into a "project unavailable" line.
/// </para>
///
/// <para>
/// Loud, but scoped to one project. Falling back to <see cref="LanguageVersion.Preview"/> is the thing
/// that must never happen, because it changes parse semantics on every file without saying so; taking
/// the whole run down is a different failure, and a needless one -- the other projects can still be
/// resolved exactly, and this one still gets the name-matching baseline.
/// </para>
/// </summary>
public sealed class UnknownLanguageVersionException : InvalidOperationException
{
    /// <summary>Creates the exception for <paramref name="langVersion"/> as pinned by <paramref name="projectPath"/>.</summary>
    public UnknownLanguageVersionException(string projectPath, string langVersion)
        : base($"LangVersion '{langVersion}' (from {projectPath}) is not known to this build of "
            + "Microsoft.CodeAnalysis.CSharp. Falling back to a preview language version would silently change "
            + "parse semantics, so this project is not compiled at all. Pin a Microsoft.CodeAnalysis.CSharp "
            + "version that knows the SDK's language version in OkfProducer.CodeGraph.Roslyn.csproj.")
    {
        ProjectPath = projectPath;
        LangVersion = langVersion;
    }

    /// <summary>The project that pinned the unrecognised version.</summary>
    public string ProjectPath { get; }

    /// <summary>The <c>LangVersion</c> value that could not be parsed.</summary>
    public string LangVersion { get; }
}

/// <summary>
/// The bounds this factory reads a <c>Compile</c> item under -- the same two
/// <c>TreeSitterExtractor.TryReadSource</c> applies to the very same files.
///
/// <para>
/// They were enforced in one engine only, which made <c>--max-file-size</c>'s help text
/// ("Largest source file, in bytes, the code stage will read") false of half the code stage: the
/// Roslyn path did a bare <see cref="File.ReadAllBytes(string)"/> on every item MSBuild listed, so a
/// <c>&lt;Compile Include="..\..\..\outside\file" /&gt;</c>, or an item behind a junction, was read
/// here after the tree-sitter path had refused it. Nothing of that content reaches the bundle --
/// symbols come only from the extractor -- so this is not a disclosure hole; it is a documented bound
/// that one of the two engines did not honour.
/// </para>
///
/// <para>
/// A refused item is dropped from the compilation exactly as a missing one is, which is the safe
/// direction: the caller's zero-errors gate then reports the project unavailable and the
/// name-matching baseline carries it, rather than an incomplete symbol table resolving calls to the
/// wrong thing.
/// </para>
/// </summary>
/// <param name="MaxFileBytes">Largest source file to read; a larger one is dropped from the compilation.</param>
/// <param name="RepositoryRoot">
/// The root the reparse-point walk stops at, or <see langword="null"/> to check only whether the file
/// itself is a link. Bounding the walk is what keeps it the same check the extractor makes -- that
/// one walks up as many levels as the file's repository-relative depth, and no further.
/// </param>
public sealed record SourceFileGate(long MaxFileBytes, string? RepositoryRoot)
{
    /// <summary>No size bound and no reparse-point walk: what a caller that has no repository gets.</summary>
    public static SourceFileGate Unbounded { get; } = new(long.MaxValue, null);
}

/// <summary>
/// Turns one project's <see cref="ProjectInputs"/> into a <see cref="CSharpCompilation"/>, with no
/// <c>MSBuildWorkspace</c> involved.
/// </summary>
public static class CompilationFactory
{
    /// <summary>
    /// Builds the compilation for <paramref name="inputs"/> from files and reference assemblies alone.
    /// </summary>
    /// <exception cref="UnknownLanguageVersionException">
    /// <paramref name="inputs"/>'s <c>LangVersion</c> is not one this Roslyn build knows.
    /// </exception>
    public static CSharpCompilation Create(ProjectInputs inputs) =>
        Create(inputs, projectCompilations: null, SourceFileGate.Unbounded, out _);

    /// <summary>
    /// As <see cref="Create(ProjectInputs, IReadOnlyDictionary{string, CSharpCompilation}, SourceFileGate, out IReadOnlyList{string})"/>,
    /// reading every <c>Compile</c> item under <see cref="SourceFileGate.Unbounded"/>.
    /// </summary>
    public static CSharpCompilation Create(
        ProjectInputs inputs,
        IReadOnlyDictionary<string, CSharpCompilation>? projectCompilations,
        out IReadOnlyList<string> missingReferences) =>
        Create(inputs, projectCompilations, SourceFileGate.Unbounded, out missingReferences);

    /// <summary>
    /// Builds the compilation for <paramref name="inputs"/>, substituting a from-source
    /// <c>CompilationReference</c> for every <c>ProjectReference</c> whose project appears in
    /// <paramref name="projectCompilations"/>.
    ///
    /// <para>
    /// That substitution is correction 2 from the spike, and it is why this route is mandatory rather
    /// than merely preferable. MSBuild resolves a <c>ProjectReference</c> to
    /// <c>bin/&lt;config&gt;/&lt;tfm&gt;/*.dll</c> -- a path that exists only after a <i>build</i>, not
    /// after a restore. Measured: dropping those references takes <c>OKF4net.Mcp</c> from 0 errors to
    /// 4 (<c>CS0234</c> on the <c>OKF4net.Agents</c> namespace, <c>CS0246</c>/<c>CS0103</c> on
    /// <c>OkfBundleTools</c>) -- the referenced project's symbols vanish entirely, and a symbol table
    /// with a hole in it does not fail to resolve calls, it resolves them to the wrong thing.
    /// Compiling the repository's own projects from source and referencing those compilations directly
    /// means a merely-restored checkout still produces an exact graph. When the substitution is
    /// unavailable (a project outside the repository, or one whose own compilation did not come out
    /// clean), the resolved <c>bin/</c> assembly is used if it is there, and reported through
    /// <paramref name="missingReferences"/> if it is not.
    /// </para>
    /// </summary>
    /// <param name="inputs">The project's MSBuild-reported inputs.</param>
    /// <param name="projectCompilations">
    /// Already-built compilations keyed by absolute <c>.csproj</c> path, or <see langword="null"/> for
    /// none. Indexed only -- never enumerated -- so its iteration order cannot reach any output.
    /// </param>
    /// <param name="missingReferences">
    /// Assembly paths MSBuild resolved that do not exist on disk and had no from-source substitute.
    /// Non-empty means the compilation is knowingly incomplete and must not be resolved from.
    /// </param>
    /// <exception cref="UnknownLanguageVersionException">
    /// <paramref name="inputs"/>'s <c>LangVersion</c> is not one this Roslyn build knows. Never
    /// degraded into a preview language version (correction 3): the spike found
    /// <c>Microsoft.CodeAnalysis.CSharp</c> 4.14.0 unable to parse <c>LangVersion 14</c>, and a silent
    /// fallback to <see cref="LanguageVersion.Preview"/> changes parse semantics -- quietly, on every
    /// file, in whichever direction the preview grammar happens to differ. The fix is to pin a Roslyn
    /// package that knows the SDK's language version, which is why this project pins one; if the SDK
    /// moves past it, this throw is the notice. It is scoped to this one project, though:
    /// <see cref="RoslynResolver"/> catches it, reports the project unavailable, and carries on with
    /// the rest -- refusing to guess at one project's language is no reason to give up the exact
    /// resolution of every other.
    /// </exception>
    /// <param name="gate">The bounds each <c>Compile</c> item is read under; see <see cref="SourceFileGate"/>.</param>
    public static CSharpCompilation Create(
        ProjectInputs inputs,
        IReadOnlyDictionary<string, CSharpCompilation>? projectCompilations,
        SourceFileGate gate,
        out IReadOnlyList<string> missingReferences)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(gate);

        var parseOptions = ParseOptionsFor(inputs);

        var trees = new List<SyntaxTree>(inputs.CompileFiles.Count);
        foreach (var file in inputs.CompileFiles)
        {
            var tree = TryParse(file, parseOptions, gate);
            if (tree is not null)
            {
                trees.Add(tree);
            }
        }

        var references = new List<MetadataReference>(inputs.References.Count);
        var missing = new List<string>();
        foreach (var reference in inputs.References)
        {
            // A ProjectReference satisfied from source contributes the CompilationReference INSTEAD of
            // the bin/ assembly, never in addition to it: two references carrying the same assembly
            // identity make every type in it ambiguous (CS0433) at every use site.
            if (reference.ProjectPath is not null
                && projectCompilations is not null
                && projectCompilations.TryGetValue(reference.ProjectPath, out var dependency))
            {
                references.Add(dependency.ToMetadataReference());
                continue;
            }

            if (File.Exists(reference.AssemblyPath))
            {
                references.Add(MetadataReference.CreateFromFile(reference.AssemblyPath));
                continue;
            }

            missing.Add(reference.AssemblyPath);
        }

        missingReferences = missing;

        return CSharpCompilation.Create(
            inputs.AssemblyName,
            trees,
            references,
            new CSharpCompilationOptions(OutputKindFor(inputs.OutputType))
                .WithNullableContextOptions(inputs.Nullable ? NullableContextOptions.Enable : NullableContextOptions.Disable)
                .WithAllowUnsafe(inputs.AllowUnsafe));
    }

    private static CSharpParseOptions ParseOptionsFor(ProjectInputs inputs)
    {
        // An ABSENT LangVersion is not an unknown one, and conflating the two would turn the loud
        // failure below into a crash on ordinary projects. MSBuild reports the property empty exactly
        // when nothing set it, which is exactly when the SDK passes csc no /langversion switch at all
        // -- and LanguageVersion.Default is, by definition, what csc uses then. Measured, because the
        // distinction is easy to get wrong in the unsafe direction: LanguageVersionFacts.TryParse("")
        // returns false, so treating empty as a parse failure would throw on any project that does not
        // pin a version. Only a version that was SPECIFIED and is not recognised is a silent-semantics
        // hazard, and only that throws.
        if (string.IsNullOrWhiteSpace(inputs.LangVersion))
        {
            return new CSharpParseOptions(LanguageVersion.Default, DocumentationMode.Parse)
                .WithPreprocessorSymbols(PreprocessorSymbols(inputs));
        }

        if (!LanguageVersionFacts.TryParse(inputs.LangVersion, out var languageVersion))
        {
            throw new UnknownLanguageVersionException(inputs.ProjectPath, inputs.LangVersion);
        }

        return new CSharpParseOptions(languageVersion, DocumentationMode.Parse)
            .WithPreprocessorSymbols(PreprocessorSymbols(inputs));
    }

    private static string[] PreprocessorSymbols(ProjectInputs inputs) =>
        inputs.DefineConstants.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

    /// <summary>
    /// Reads and parses one <c>Compile</c> item, or returns <see langword="null"/> when it is not
    /// there (a generated file whose target did not run, or a stale item) -- a missing file is a gap
    /// this method reports by omission and the caller's zero-errors gate then catches, not something
    /// to fabricate an empty tree for.
    ///
    /// <para>
    /// A file over <paramref name="gate"/>'s size cap, or reached through a symlink or junction, is
    /// reported by the same omission and for the same reason. Those are the two bounds
    /// <c>TreeSitterExtractor.TryReadSource</c> applies to the very same files, and until they were
    /// applied here too the Roslyn half of the code stage read what the tree-sitter half had refused.
    /// </para>
    ///
    /// <para>
    /// The text is decoded through <see cref="SourceDecoder.DecodeStrict"/>, the same call
    /// <c>TreeSitterExtractor</c> makes, and handed to <see cref="SourceText.From(string, Encoding, SourceHashAlgorithm)"/>
    /// unchanged. That is load-bearing rather than tidy: <see cref="CallSite.Offset"/> is a UTF-8 byte
    /// offset into the <i>decoded string</i>, so the two extractors' offsets are comparable only while
    /// both decode to the same string -- a UTF-8 BOM stripped by one and kept by the other shifts every
    /// offset in the file by three bytes and credits calls to whatever sits three bytes away.
    /// <see cref="File.ReadAllText(string)"/> would also differ: it sniffs a different set of encodings
    /// and strips BOMs on its own terms.
    /// </para>
    ///
    /// <para>
    /// A file whose bytes are not valid in the encoding its BOM selects falls back to a replacing UTF-8
    /// decode, purely so it still contributes its declarations to the compilation. Its offsets are then
    /// NOT comparable with the other extractor's -- and they never need to be:
    /// <c>TreeSitterExtractor</c> refuses the very same file with <c>FileStatus.SkippedEncoding</c>, so
    /// it produces no <see cref="CallSite"/> for anything in it and there is nothing for a shifted
    /// offset to mis-attach to.
    /// </para>
    /// </summary>
    private static SyntaxTree? TryParse(string path, CSharpParseOptions parseOptions, SourceFileGate gate)
    {
        byte[] bytes;
        try
        {
            var file = new FileInfo(path);
            if (!file.Exists || file.Length > gate.MaxFileBytes || IsBehindReparsePoint(file, gate.RepositoryRoot))
            {
                return null;
            }

            bytes = File.ReadAllBytes(path);
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }

        string text;
        try
        {
            text = SourceDecoder.DecodeStrict(bytes);
        }
        catch (DecoderFallbackException)
        {
            text = new UTF8Encoding(false, throwOnInvalidBytes: false).GetString(bytes);
        }

        return CSharpSyntaxTree.ParseText(SourceText.From(text), parseOptions, path);
    }

    /// <summary>
    /// Whether <paramref name="file"/> is a link, or sits under a directory that is one.
    ///
    /// <para>
    /// The walk stops at <paramref name="repositoryRoot"/> rather than at the drive root, which is the
    /// same bound <c>TreeSitterExtractor</c> gets from counting the separators in a file's
    /// repository-relative path: a junction somewhere above the repository is the operator's own
    /// checkout layout, not something the scanned repository chose. A file outside the root -- a
    /// linked <c>Compile</c> item from elsewhere -- keeps only the check on the file itself, since
    /// there is no bound to walk within.
    /// </para>
    /// </summary>
    private static bool IsBehindReparsePoint(FileInfo file, string? repositoryRoot)
    {
        if (file.LinkTarget is not null)
        {
            return true;
        }

        if (repositoryRoot is null)
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(repositoryRoot));
        var directory = file.DirectoryName;

        while (directory is not null
            && !string.Equals(Path.TrimEndingDirectorySeparator(directory), root, StringComparison.Ordinal))
        {
            if (new DirectoryInfo(directory).LinkTarget is not null)
            {
                return true;
            }

            directory = Path.GetDirectoryName(directory);
        }

        return false;
    }

    private static OutputKind OutputKindFor(string outputType) =>
        outputType switch
        {
            "Exe" => OutputKind.ConsoleApplication,
            "WinExe" => OutputKind.WindowsApplication,
            "Module" => OutputKind.NetModule,
            _ => OutputKind.DynamicallyLinkedLibrary,
        };
}
