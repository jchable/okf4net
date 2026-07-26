// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using OKF4net;

namespace OKF4net.Catalog;

/// <summary>
/// Strict <see cref="System.Text.Json"/>-based parser for <c>catalog.json</c>
/// manifests. Returns a validated <see cref="KnowledgeCatalogSnapshot"/> or a
/// list of <see cref="CatalogDiagnostic"/>s; never throws for malformed input.
/// </summary>
/// <remarks>
/// Only structural/JSON validation is performed here: no filesystem access
/// happens in this parser (path safety is a later concern). Snapshots it
/// produces always carry <see cref="KnowledgeCatalogSnapshot.Generation"/> 0;
/// the catalog stamps the real generation on publish.
/// </remarks>
public static class CatalogManifestParser
{
    private const string VersionProperty = "version";
    private const string SourcesProperty = "sources";
    private const string IdProperty = "id";
    private const string PathProperty = "path";
    private const string PriorityProperty = "priority";
    private const string EnabledProperty = "enabled";
    private const string RoleProperty = "role";
    private const string KnowledgeRoleValue = "knowledge";

    /// <summary>
    /// Attempts to parse and validate <paramref name="json"/> as a <c>catalog.json</c>
    /// manifest body.
    /// </summary>
    /// <param name="json">The raw manifest text.</param>
    /// <param name="manifestDirectory">
    /// The directory the manifest came from; recorded verbatim on the resulting
    /// snapshot and not otherwise inspected (no filesystem access is performed here).
    /// </param>
    /// <param name="snapshot">
    /// On success, the validated, immutable snapshot (with <c>Generation 0</c>);
    /// <see langword="null"/> when parsing/validation fails.
    /// </param>
    /// <param name="diagnostics">
    /// Every reject reason found. Empty exactly when this method returns <see langword="true"/>.
    /// </param>
    /// <returns><see langword="true"/> if the manifest is valid; otherwise <see langword="false"/>.</returns>
    public static bool TryParse(
        string json,
        string manifestDirectory,
        out KnowledgeCatalogSnapshot? snapshot,
        out IReadOnlyList<CatalogDiagnostic> diagnostics)
    {
        var diags = new List<CatalogDiagnostic>();
        snapshot = null;

        if (json is null)
        {
            diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.ParseError, "Manifest JSON must not be null."));
            diagnostics = diags.AsReadOnly();
            return false;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.ParseError, $"Malformed JSON: {ex.Message}"));
            diagnostics = diags.AsReadOnly();
            return false;
        }

        using (document)
        {
            var root = document.RootElement;

            if (HasEmbeddedNul(root))
            {
                diags.Add(new CatalogDiagnostic(
                    CatalogDiagnosticCode.EmbeddedNul,
                    "A string value in the manifest contains an embedded NUL character ('\\0')."));
            }

            if (root.ValueKind != JsonValueKind.Object)
            {
                diags.Add(new CatalogDiagnostic(
                    CatalogDiagnosticCode.ParseError,
                    $"Manifest root must be a JSON object, found {root.ValueKind}."));
                diagnostics = diags.AsReadOnly();
                return false;
            }

            foreach (var property in root.EnumerateObject())
            {
                if (property.Name != VersionProperty && property.Name != SourcesProperty)
                {
                    diags.Add(new CatalogDiagnostic(
                        CatalogDiagnosticCode.UnknownRootProperty,
                        $"Unknown root property '{property.Name}'."));
                }
            }

            var version = ParseVersion(root, diags);
            var sources = ParseSources(root, diags);

            // .AsReadOnly() wraps `diags` in a genuine ReadOnlyCollection<T>
            // view -- otherwise a caller could downcast this back to
            // List<CatalogDiagnostic> and mutate published diagnostics (F4),
            // matching the same hardening FileKnowledgeCatalog's read-failure
            // path already applies via Array.AsReadOnly(). EVERY failure exit
            // above (null / malformed JSON / non-object root) wraps identically,
            // so no path ever hands the mutable list out -- important because
            // FileKnowledgeCatalog.LastReloadDiagnostics republishes this list
            // verbatim after an invalid reload.
            diagnostics = diags.AsReadOnly();
            if (diags.Count > 0)
            {
                return false;
            }

            snapshot = new KnowledgeCatalogSnapshot(version, sources.AsReadOnly(), manifestDirectory, Generation: 0);
            return true;
        }
    }

    private static int ParseVersion(JsonElement root, List<CatalogDiagnostic> diags)
    {
        if (root.TryGetProperty(VersionProperty, out var versionProperty) &&
            versionProperty.ValueKind == JsonValueKind.Number &&
            versionProperty.TryGetInt32(out var version) &&
            version == 1)
        {
            return version;
        }

        diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.WrongVersion, "Manifest 'version' must be the integer 1."));
        return 0;
    }

    private static List<KnowledgeCatalogSource> ParseSources(JsonElement root, List<CatalogDiagnostic> diags)
    {
        var sources = new List<KnowledgeCatalogSource>();

        if (!root.TryGetProperty(SourcesProperty, out var sourcesProperty) ||
            sourcesProperty.ValueKind != JsonValueKind.Array ||
            sourcesProperty.GetArrayLength() == 0)
        {
            diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.EmptySources, "Manifest 'sources' must be a non-empty array."));
            return sources;
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        foreach (var sourceElement in sourcesProperty.EnumerateArray())
        {
            if (sourceElement.ValueKind != JsonValueKind.Object)
            {
                diags.Add(new CatalogDiagnostic(
                    CatalogDiagnosticCode.ParseError,
                    $"Each 'sources' entry must be a JSON object, found {sourceElement.ValueKind}."));
                continue;
            }

            sources.Add(ParseSource(sourceElement, seenIds, diags));
        }

        return sources;
    }

    private static KnowledgeCatalogSource ParseSource(JsonElement source, HashSet<string> seenIds, List<CatalogDiagnostic> diags)
    {
        foreach (var property in source.EnumerateObject())
        {
            if (property.Name is not (IdProperty or PathProperty or PriorityProperty or EnabledProperty or RoleProperty))
            {
                diags.Add(new CatalogDiagnostic(
                    CatalogDiagnosticCode.UnknownSourceProperty,
                    $"Unknown source property '{property.Name}'."));
            }
        }

        var id = ParseId(source, seenIds, diags);
        var path = ParsePath(source, diags);
        var priority = ParsePriority(source, diags);
        var enabled = ParseEnabled(source, diags);
        var role = ParseRole(source, diags);

        return new KnowledgeCatalogSource(id, path, priority, enabled, role);
    }

    private static string ParseId(JsonElement source, HashSet<string> seenIds, List<CatalogDiagnostic> diags)
    {
        var id = source.TryGetProperty(IdProperty, out var idProperty) && idProperty.ValueKind == JsonValueKind.String
            ? idProperty.GetString() ?? string.Empty
            : string.Empty;

        try
        {
            OKF4net.ConceptId.ValidateSegment(id);
        }
        catch (OKF4net.ConceptIdException)
        {
            diags.Add(new CatalogDiagnostic(
                CatalogDiagnosticCode.InvalidSourceId,
                $"Source 'id' '{id}' is not a valid concept-id segment."));
        }

        if (!seenIds.Add(id))
        {
            diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.DuplicateSourceId, $"Duplicate source id '{id}'."));
        }

        return id;
    }

    private static string ParsePath(JsonElement source, List<CatalogDiagnostic> diags)
    {
        if (source.TryGetProperty(PathProperty, out var pathProperty) &&
            pathProperty.ValueKind == JsonValueKind.String)
        {
            var path = pathProperty.GetString() ?? string.Empty;
            if (path.Length > 0)
            {
                return path;
            }
        }

        diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.EmptyPath, "Source 'path' must be a non-empty string."));
        return string.Empty;
    }

    private static int ParsePriority(JsonElement source, List<CatalogDiagnostic> diags)
    {
        if (!source.TryGetProperty(PriorityProperty, out var priorityProperty))
        {
            return 0;
        }

        if (priorityProperty.ValueKind == JsonValueKind.Number && priorityProperty.TryGetInt32(out var priority))
        {
            return priority;
        }

        diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.MalformedPriority, "Source 'priority' must be an integer."));
        return 0;
    }

    private static bool ParseEnabled(JsonElement source, List<CatalogDiagnostic> diags)
    {
        if (!source.TryGetProperty(EnabledProperty, out var enabledProperty))
        {
            return true;
        }

        if (enabledProperty.ValueKind == JsonValueKind.True)
        {
            return true;
        }

        if (enabledProperty.ValueKind == JsonValueKind.False)
        {
            return false;
        }

        diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.MalformedEnabled, "Source 'enabled' must be a boolean."));
        return true;
    }

    private static SourceRole ParseRole(JsonElement source, List<CatalogDiagnostic> diags)
    {
        if (!source.TryGetProperty(RoleProperty, out var roleProperty))
        {
            return SourceRole.Knowledge;
        }

        if (roleProperty.ValueKind == JsonValueKind.String && roleProperty.GetString() == KnowledgeRoleValue)
        {
            return SourceRole.Knowledge;
        }

        diags.Add(new CatalogDiagnostic(CatalogDiagnosticCode.IllegalRole, "Source 'role' must be \"knowledge\"."));
        return SourceRole.Knowledge;
    }

    private static bool HasEmbeddedNul(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                var value = element.GetString();
                return value is not null && value.Contains('\0');

            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    if (property.Name.Contains('\0') || HasEmbeddedNul(property.Value))
                    {
                        return true;
                    }
                }

                return false;

            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    if (HasEmbeddedNul(item))
                    {
                        return true;
                    }
                }

                return false;

            default:
                return false;
        }
    }
}
