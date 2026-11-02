// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Catalog;

/// <summary>
/// Enumerates every way a <c>catalog.json</c> manifest can be rejected by
/// <see cref="CatalogManifestParser.TryParse"/>, plus every way a source's
/// <c>path</c> can be rejected by <see cref="CatalogPathResolver.TryResolve"/>
/// once the manifest itself is structurally valid. Both lists are closed: no
/// additional codes are introduced ad hoc by either implementation.
/// </summary>
public enum CatalogDiagnosticCode
{
    /// <summary>The input was not syntactically valid JSON, or not a JSON object where one was required.</summary>
    ParseError,

    /// <summary>The manifest root object contains a property other than <c>version</c> or <c>sources</c>.</summary>
    UnknownRootProperty,

    /// <summary>A source entry contains a property other than <c>id</c>, <c>path</c>, <c>priority</c>, <c>enabled</c>, <c>role</c>, <c>tier</c>, <c>provider</c>, or <c>settings</c>.</summary>
    UnknownSourceProperty,

    /// <summary>The root <c>version</c> property is missing, not a number, or not exactly <c>1</c>.</summary>
    WrongVersion,

    /// <summary>The root <c>sources</c> property is missing, not an array, or an empty array.</summary>
    EmptySources,

    /// <summary>Two or more source entries share the same <c>id</c>.</summary>
    DuplicateSourceId,

    /// <summary>A source's <c>id</c> is not a valid single concept-id segment (see <see cref="OKF4net.ConceptId.ValidateSegment"/>).</summary>
    InvalidSourceId,

    /// <summary>A source's <c>path</c> is missing, not a string, or empty.</summary>
    EmptyPath,

    /// <summary>A string value anywhere in the manifest contains an embedded NUL character (<c>'\0'</c>).</summary>
    EmbeddedNul,

    /// <summary>A source's <c>priority</c> is present but not an integer.</summary>
    MalformedPriority,

    /// <summary>A source's <c>enabled</c> is present but not a boolean.</summary>
    MalformedEnabled,

    /// <summary>A source's <c>role</c> is present but is not the string <c>"knowledge"</c> or <c>"memory"</c>.</summary>
    IllegalRole,

    /// <summary>A <c>role:"memory"</c> source is missing a valid <c>tier</c>, or a non-memory source carries a <c>tier</c>.</summary>
    IllegalTier,

    /// <summary>Two or more <c>role:"memory"</c> sources declare the same <c>tier</c> (one memory source per tier is allowed).</summary>
    DuplicateMemoryTier,

    /// <summary>
    /// A source's resolved <c>path</c> could not be canonicalized at all (e.g. an embedded
    /// NUL character or another OS-rejected character), as reported by <see cref="CatalogPathResolver.TryResolve"/>.
    /// </summary>
    InvalidPath,

    /// <summary>A source's <c>path</c> is rooted (absolute) rather than relative to the manifest directory (see <see cref="CatalogPathResolver.TryResolve"/>).</summary>
    AbsolutePath,

    /// <summary>A source's <c>path</c>, resolved against the manifest directory, escapes the catalog root (see <see cref="CatalogPathResolver.TryResolve"/>).</summary>
    OutsideRoot,

    /// <summary>A source's resolved <c>path</c> is not an existing directory (see <see cref="CatalogPathResolver.TryResolve"/>).</summary>
    TargetNotFound,

    /// <summary>
    /// A source's resolved <c>path</c>, or a directory strictly between it and the catalog
    /// root, is a filesystem reparse point (symlink/junction/mount point) -- see
    /// <see cref="CatalogPathResolver.TryResolve"/>. The catalog root itself is exempt: a
    /// symlinked/mounted root is a legitimate, explicit operator choice.
    /// </summary>
    ReparsePointInPath,

    // The members below are appended, never inserted: the numeric value of every member above
    // shipped (0.7.0) and a host built against it compares codes by value.

    /// <summary>A source's <c>provider</c> is present but not a non-empty string.</summary>
    MalformedProvider,

    /// <summary>A source's <c>settings</c> is present but not a JSON object whose values are all strings, or names a setting twice.</summary>
    MalformedSettings,

    /// <summary>A source carries a setting its provider does not accept: any setting for the built-in <c>filesystem</c> provider, or a name the provider's factory does not list in <see cref="IBundleProviderFactory.AcceptedSettings"/>.</summary>
    IllegalSetting,

    /// <summary>A source whose <c>provider</c> is not <c>filesystem</c> carries a <c>path</c>, which names a directory only for the filesystem provider.</summary>
    IllegalPath,

    /// <summary>A <c>role:"memory"</c> source names a <c>provider</c> other than <c>filesystem</c>: memory stays on the filesystem (spec §9).</summary>
    IllegalMemoryProvider,

    /// <summary>A source names a <c>provider</c> for which the host registered no <see cref="IBundleProviderFactory"/> (<see cref="KnowledgeCatalogOptions.ProviderFactories"/>).</summary>
    UnknownProvider,

    /// <summary>A source's provider could not be created: its factory's <see cref="IBundleProviderFactory.Create"/>, or the handle over the provider it returned, threw.</summary>
    ProviderUnavailable,
}
