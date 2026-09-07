// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OkfProducer.CodeGraph.Roslyn;

/// <summary>
/// One resolved reference of a project, as MSBuild reported it.
///
/// <para>
/// <see cref="ProjectPath"/> is what makes this a record rather than a bare path string, and it is
/// not a convenience: it is the join key correction 2 needs. MSBuild tags every <c>ReferencePath</c>
/// item that came from a <c>ProjectReference</c> with <c>ReferenceSourceTarget=ProjectReference</c>
/// and an <c>MSBuildSourceProjectFile</c> naming the <c>.csproj</c> it came from, so
/// <see cref="CompilationFactory"/> can swap that reference for a <c>CompilationReference</c> to the
/// same project compiled from source. Without this metadata the only way to tell a project output
/// from a NuGet assembly is to guess from the path (does it contain <c>/bin/</c>? is it under the
/// repository root?), and a guess here fails in the direction that matters: a missed substitution
/// silently reintroduces the built-repository requirement, and a false one drops a real dependency.
/// </para>
/// </summary>
/// <param name="AssemblyPath">Absolute path of the assembly MSBuild resolved this reference to.</param>
/// <param name="ProjectPath">
/// Absolute path of the <c>.csproj</c> this reference is the output of, or <see langword="null"/> for
/// a reference that is not a project output (a NuGet package, or a framework assembly).
/// </param>
public sealed record ProjectReferenceInput(string AssemblyPath, string? ProjectPath);

/// <summary>
/// Everything <see cref="CompilationFactory"/> needs to build one project's <c>CSharpCompilation</c>,
/// as read out of one <see cref="MsBuildProjectQuery.Query"/> call.
/// </summary>
/// <param name="ProjectPath">Absolute path of the <c>.csproj</c> these inputs describe.</param>
/// <param name="AssemblyName">
/// The project's <c>AssemblyName</c> property, not its file name: the two differ (<c>OKF4net.Mcp.csproj</c>
/// builds <c>okf-mcp</c>), and <c>InternalsVisibleTo</c> is matched on the assembly name, so using the
/// file name would make every <c>internal</c> member of a friend assembly inaccessible -- an error per
/// use site, in a compilation whose whole value depends on having no errors.
/// </param>
/// <param name="CompileFiles">
/// Absolute paths of the project's <c>Compile</c> items, including the SDK-generated ones under
/// <c>obj/</c> that only appear when <c>GenerateGlobalUsings</c> and <c>GenerateAssemblyInfo</c> have
/// run (correction 1).
/// </param>
/// <param name="References">The project's resolved <c>ReferencePath</c> items, with their provenance.</param>
/// <param name="DefineConstants">The project's <c>DefineConstants</c>, still <c>;</c>-separated as MSBuild reports it.</param>
/// <param name="LangVersion">The project's <c>LangVersion</c>, verbatim -- never normalised or defaulted here (correction 3).</param>
/// <param name="Nullable">Whether the project's <c>Nullable</c> property is <c>enable</c>.</param>
/// <param name="AllowUnsafe">The project's <c>AllowUnsafeBlocks</c> property.</param>
/// <param name="OutputType">The project's <c>OutputType</c> (<c>Exe</c>, <c>WinExe</c>, <c>Library</c>, ...).</param>
/// <param name="TargetFramework">The project's single <c>TargetFramework</c>, carried for diagnostics.</param>
public sealed record ProjectInputs(
    string ProjectPath,
    string AssemblyName,
    IReadOnlyList<string> CompileFiles,
    IReadOnlyList<ProjectReferenceInput> References,
    string DefineConstants,
    string LangVersion,
    bool Nullable,
    bool AllowUnsafe,
    string OutputType,
    string TargetFramework);
