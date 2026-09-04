// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.CodeGraph.Roslyn;

/// <summary>Whether <see cref="RoslynResolver"/> could build a usable compilation for one project.</summary>
public enum RoslynProjectAvailability
{
    /// <summary>Compiled with zero errors; its files are resolved exactly.</summary>
    Compiled,

    /// <summary>
    /// <c>dotnet msbuild</c> could not be run, timed out, exited non-zero, or did not print the JSON
    /// its <c>-getItem</c>/<c>-getProperty</c> switches promise. The usual cause is a project that has
    /// not been restored, or no dotnet CLI on <c>PATH</c> at all.
    /// </summary>
    MsBuildQueryFailed,

    /// <summary>
    /// MSBuild answered, but a reference it named could not be turned into metadata and was not a
    /// project this run compiled from source -- so the compilation would be missing symbols it needs.
    ///
    /// <para>
    /// Two causes, told apart by <see cref="RoslynProjectReport.Detail"/> rather than by two enum
    /// members, because a caller does the same thing for both: the reference is <i>absent</i> from disk
    /// (a repository restored but never built), or it is <i>present and unreadable</i> (a concurrent
    /// build holding it, a denying ACL, a file deleted between the existence check and the read). The
    /// second used to be no status at all -- it threw <see cref="IOException"/> out through
    /// <see cref="RoslynResolver.Create"/> and ended the whole run.
    /// </para>
    /// </summary>
    ReferencesUnresolved,

    /// <summary>
    /// The compilation was built and reported errors. Deliberately treated as unusable rather than
    /// "mostly fine": an incomplete symbol table does not fail to resolve calls, it resolves them to
    /// the wrong declaration, and a confident wrong edge is worse than an honest unresolved one (2.1).
    /// </summary>
    CompilationHadErrors,

    /// <summary>
    /// The project pins a <c>LangVersion</c> this build of <c>Microsoft.CodeAnalysis.CSharp</c> does
    /// not recognise (correction 3). Its own status rather than a flavour of
    /// <see cref="CompilationHadErrors"/>, because the remedy is entirely different: nothing is wrong
    /// with the project, the pinned Roslyn package is behind the SDK.
    /// </summary>
    UnknownLanguageVersion,
}

/// <summary>
/// What happened to one project <see cref="RoslynResolver"/> tried to compile -- the record that lets
/// a caller tell "resolved nothing" from "could not run".
/// </summary>
/// <param name="ProjectPath">Absolute path of the <c>.csproj</c>.</param>
/// <param name="Availability">Whether this project produced a usable compilation.</param>
/// <param name="Detail">
/// A one-line, human-readable reason when <paramref name="Availability"/> is not
/// <see cref="RoslynProjectAvailability.Compiled"/>; empty otherwise. For
/// <see cref="RoslynProjectAvailability.CompilationHadErrors"/> it names the error count and the most
/// common diagnostic ids, which is what actually identifies the cause (a repository that was restored
/// but never built shows up as <c>CS0234</c>/<c>CS0246</c> on its own namespaces).
/// </param>
public sealed record RoslynProjectReport(string ProjectPath, RoslynProjectAvailability Availability, string Detail);
