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
        Create(inputs, projectCompilations: null, out _);

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
    public static CSharpCompilation Create(
        ProjectInputs inputs,
        IReadOnlyDictionary<string, CSharpCompilation>? projectCompilations,
        out IReadOnlyList<string> missingReferences)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        var parseOptions = ParseOptionsFor(inputs);

        var trees = new List<SyntaxTree>(inputs.CompileFiles.Count);
        foreach (var file in inputs.CompileFiles)
        {
            var tree = TryParse(file, parseOptions);
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
    private static SyntaxTree? TryParse(string path, CSharpParseOptions parseOptions)
    {
        byte[] bytes;
        try
        {
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

    private static OutputKind OutputKindFor(string outputType) =>
        outputType switch
        {
            "Exe" => OutputKind.ConsoleApplication,
            "WinExe" => OutputKind.WindowsApplication,
            "Module" => OutputKind.NetModule,
            _ => OutputKind.DynamicallyLinkedLibrary,
        };
}
