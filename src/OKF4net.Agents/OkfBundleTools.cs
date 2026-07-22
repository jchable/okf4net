// SPDX-License-Identifier: LGPL-3.0-or-later
using System.ComponentModel;
using System.Text;
using OKF4net.Yaml;

namespace OKF4net.Agents;

/// <summary>OKF bundle operations exposed as Microsoft Agent Framework function tools.</summary>
public sealed class OkfBundleTools
{
    private const string IndexFilename = "index.md";
    private const string NoneLine = "(none)";

    /// <summary>
    /// Guards <see cref="_bundle"/>. Agent hosts may invoke tool methods
    /// concurrently from multiple threads, so the lazy cache in
    /// <see cref="GetBundle"/> and the invalidation in
    /// <see cref="InvalidateBundle"/> must not race.
    /// </summary>
    private readonly Lock _bundleLock = new();

    private Bundle? _bundle;

    /// <summary>
    /// Creates the tool set rooted at <paramref name="bundleRoot"/>.
    /// </summary>
    /// <param name="bundleRoot">Path to the bundle's root directory.</param>
    /// <exception cref="ArgumentException"><paramref name="bundleRoot"/> does not exist.</exception>
    public OkfBundleTools(string bundleRoot)
    {
        if (!Directory.Exists(bundleRoot))
        {
            throw new ArgumentException($"bundle root does not exist: {bundleRoot}", nameof(bundleRoot));
        }

        BundleRoot = bundleRoot;
    }

    /// <summary>The bundle's root directory, as passed to the constructor.</summary>
    public string BundleRoot { get; }

    /// <summary>
    /// Returns the loaded bundle, loading it from <see cref="BundleRoot"/> on
    /// first access and caching it thereafter until <see cref="InvalidateBundle"/>
    /// is called.
    /// </summary>
    internal Bundle GetBundle()
    {
        lock (_bundleLock)
        {
            return _bundle ??= Bundle.Load(BundleRoot);
        }
    }

    /// <summary>
    /// Drops the cached bundle so the next <see cref="GetBundle"/> call
    /// reloads it from disk. Call after any write to <see cref="BundleRoot"/>.
    /// </summary>
    internal void InvalidateBundle()
    {
        lock (_bundleLock)
        {
            _bundle = null;
        }
    }

    /// <summary>
    /// Reads one concept: its frontmatter, body, outgoing links, and
    /// backlinks, rendered as agent-friendly markdown. Never throws for
    /// expected errors (invalid or unknown concept id) — those are reported
    /// as a plain-text message instead.
    /// </summary>
    /// <param name="conceptId">The concept id, e.g. <c>tables/orders</c>.</param>
    [Description("Read one concept from the OKF bundle: its frontmatter, body, outgoing links and backlinks.")]
    public string ReadConcept([Description("The concept id, e.g. 'tables/orders'.")] string conceptId)
    {
        var bundle = GetBundle();
        if (!ConceptId.TryParse(conceptId, out var id) || bundle.Get(id) is not { } concept)
        {
            return ConceptNotFoundMessage(conceptId);
        }

        var sb = new StringBuilder();
        sb.Append("# ").Append(concept.Document.Frontmatter.Title ?? concept.Id.ToString()).Append('\n').Append('\n');
        AppendFrontmatterBlock(sb, concept.Document.Frontmatter);
        sb.Append(concept.Document.Body.TrimEnd('\n')).Append('\n').Append('\n');
        AppendSection(sb, "Outgoing links", FormatOutgoingLinks(bundle.LinksFrom(id)));
        sb.Append('\n');
        AppendSection(sb, "Backlinks", FormatBacklinks(bundle.Backlinks(id)));

        return sb.ToString();
    }

    /// <summary>
    /// Browses the bundle via its <c>index.md</c> files (progressive
    /// disclosure): returns the raw content of the requested directory's
    /// index if one exists, otherwise a generated listing of the concepts
    /// and subdirectories at that level. Never throws for expected errors
    /// (an invalid or out-of-bundle path) — those are reported as a
    /// plain-text message instead.
    /// </summary>
    /// <param name="path">Optional directory path within the bundle, e.g. <c>tables</c>. Omit to list the bundle root.</param>
    [Description("Browse the bundle via its index files (progressive disclosure). Without a path, lists the bundle root.")]
    public string Browse([Description("Optional directory path within the bundle, e.g. 'tables'.")] string? path = null)
    {
        var relPath = path?.Trim() ?? string.Empty;
        var segments = relPath.Length == 0
            ? []
            : relPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);

        if (segments.Any(s => s == "..") || Path.IsPathRooted(relPath))
        {
            return $"Error: invalid path '{path}' — '..' segments and absolute paths are not allowed.";
        }

        var bundle = GetBundle();
        var fullDir = segments.Length == 0 ? bundle.Root : Path.Combine([bundle.Root, .. segments]);

        if (!IsWithinBundleRoot(bundle.Root, fullDir) || !Directory.Exists(fullDir))
        {
            return $"Error: path '{path}' not found in the bundle. Use okf_browse to list available directories.";
        }

        var indexPath = Path.Combine(fullDir, IndexFilename);
        if (File.Exists(indexPath))
        {
            return File.ReadAllText(indexPath);
        }

        return BuildLevelListing(bundle, segments, relPath);
    }

    /// <summary>
    /// Inspects the cross-link graph: bundle-wide stats, or (with a concept
    /// id) that concept's outgoing links, backlinks, and broken links.
    /// Never throws for an unknown concept id — reported as a plain-text
    /// message instead.
    /// </summary>
    /// <param name="conceptId">Optional concept id to focus on.</param>
    [Description("Inspect the cross-link graph. With a concept id: its outgoing links, backlinks and broken links. Without: bundle-wide stats.")]
    public string Graph([Description("Optional concept id to focus on.")] string? conceptId = null)
    {
        var bundle = GetBundle();

        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return BuildBundleGraphSummary(bundle);
        }

        if (!ConceptId.TryParse(conceptId, out var id) || bundle.Get(id) is null)
        {
            return ConceptNotFoundMessage(conceptId);
        }

        return BuildConceptGraphDetail(bundle, id);
    }

    /// <summary>
    /// Builds the fallback listing for a bundle level with no <c>index.md</c>:
    /// the subdirectories and concepts found directly under
    /// <paramref name="segments"/>, derived from <see cref="Bundle.Concepts"/>.
    /// </summary>
    private static string BuildLevelListing(Bundle bundle, IReadOnlyList<string> segments, string relPath)
    {
        var subdirectories = new SortedSet<string>(StringComparer.Ordinal);
        var concepts = new List<Concept>();
        foreach (var concept in bundle.Concepts)
        {
            var conceptSegments = concept.Id.Segments;
            if (conceptSegments.Count <= segments.Count || !conceptSegments.Take(segments.Count).SequenceEqual(segments))
            {
                continue;
            }

            if (conceptSegments.Count == segments.Count + 1)
            {
                concepts.Add(concept);
            }
            else
            {
                subdirectories.Add(conceptSegments[segments.Count]);
            }
        }

        var sb = new StringBuilder();
        sb.Append("# ").Append(segments.Count == 0 ? "(bundle root)" : relPath).Append('\n').Append('\n');

        if (subdirectories.Count == 0 && concepts.Count == 0)
        {
            sb.Append("(empty)").Append('\n');
            return sb.ToString();
        }

        if (subdirectories.Count > 0)
        {
            AppendSection(sb, "Subdirectories", subdirectories);
            sb.Append('\n');
        }

        if (concepts.Count > 0)
        {
            var lines = concepts
                .OrderBy(c => c.Id)
                .Select(c => $"{c.Id} — {c.Document.Frontmatter.Title ?? c.Id.Name}");
            AppendSection(sb, "Concepts", lines);
        }

        return sb.ToString();
    }

    /// <summary>Bundle-wide stats: concept, link, and broken-link counts, plus the broken links themselves.</summary>
    private static string BuildBundleGraphSummary(Bundle bundle)
    {
        var totalLinks = bundle.Concepts.Sum(c => bundle.LinksFrom(c.Id).Count);
        var broken = bundle.BrokenLinks();

        var sb = new StringBuilder();
        sb.Append("# Bundle graph").Append('\n').Append('\n');
        sb.Append("- ").Append(bundle.Count).Append(" concepts").Append('\n');
        sb.Append("- ").Append(totalLinks).Append(" links").Append('\n');
        sb.Append("- ").Append(broken.Count).Append(" broken links").Append('\n');

        if (broken.Count > 0)
        {
            sb.Append('\n');
            AppendSection(sb, "Broken links", broken.Select(b => $"{b.Source} -> {b.RawTarget}"));
        }

        return sb.ToString();
    }

    /// <summary>A single concept's outgoing links, backlinks, and broken outgoing links.</summary>
    private static string BuildConceptGraphDetail(Bundle bundle, ConceptId id)
    {
        var outgoing = bundle.LinksFrom(id);
        var backlinks = bundle.Backlinks(id);
        var brokenOutgoing = outgoing.Where(l => !l.Exists).ToList();

        var sb = new StringBuilder();
        sb.Append("# Graph: ").Append(id).Append('\n').Append('\n');
        AppendSection(sb, "Outgoing links", FormatOutgoingLinks(outgoing));
        sb.Append('\n');
        AppendSection(sb, "Backlinks", FormatBacklinks(backlinks));
        sb.Append('\n');
        AppendSection(sb, "Broken links", brokenOutgoing.Select(l => $"{id} -> {l.Raw}"));

        return sb.ToString();
    }

    private static IEnumerable<string> FormatOutgoingLinks(IEnumerable<ResolvedLink> links) =>
        links.Select(link => link.Target + (link.Exists ? string.Empty : " (broken)"));

    private static IEnumerable<string> FormatBacklinks(IEnumerable<ConceptId> backlinks) =>
        backlinks.Select(source => source.ToString());

    /// <summary>Appends a markdown <c>## </c>-heading section, one bullet per line, or <see cref="NoneLine"/> if empty.</summary>
    private static void AppendSection(StringBuilder sb, string heading, IEnumerable<string> lines)
    {
        sb.Append("## ").Append(heading).Append('\n');
        var any = false;
        foreach (var line in lines)
        {
            any = true;
            sb.Append("- ").Append(line).Append('\n');
        }

        if (!any)
        {
            sb.Append(NoneLine).Append('\n');
        }
    }

    private static void AppendFrontmatterBlock(StringBuilder sb, Frontmatter frontmatter)
    {
        var map = frontmatter.AsMapping();
        if (map.IsEmpty)
        {
            return;
        }

        foreach (var key in map.Keys)
        {
            sb.Append(key).Append(": ").Append(FormatFrontmatterValue(map.Get(key))).Append('\n');
        }

        sb.Append('\n');
    }

    private static string ConceptNotFoundMessage(string conceptId) =>
        $"Concept '{conceptId}' not found. Use okf_browse to list available concepts.";

    /// <summary>
    /// <c>true</c> if <paramref name="candidate"/> is <paramref name="root"/>
    /// itself or a descendant of it, comparing resolved absolute paths
    /// case-insensitively (Windows/macOS filesystems are typically
    /// case-insensitive; a stricter check would reject legitimate paths
    /// there). Defense in depth alongside the explicit <c>..</c>/rooted-path
    /// rejection in <see cref="Browse"/>.
    /// </summary>
    private static bool IsWithinBundleRoot(string root, string candidate)
    {
        var fullRoot = Path.GetFullPath(root);
        var fullCandidate = Path.GetFullPath(candidate);
        if (string.Equals(fullRoot, fullCandidate, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var rootWithSeparator = fullRoot.EndsWith(Path.DirectorySeparatorChar) ? fullRoot : fullRoot + Path.DirectorySeparatorChar;
        return fullCandidate.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Renders a frontmatter value as a single display line: scalars via
    /// <see cref="YamlValue.AsDisplayString"/>, sequences as a
    /// comma-separated flow list (e.g. <c>[sales, orders]</c>), and any
    /// other structure via its YAML text with newlines collapsed.
    /// </summary>
    private static string FormatFrontmatterValue(YamlValue? value)
    {
        if (value is null)
        {
            return string.Empty;
        }

        var display = value.AsDisplayString();
        if (display is not null)
        {
            return display;
        }

        if (value is YamlSequence seq)
        {
            return "[" + string.Join(", ", seq.Items.Select(FormatFrontmatterValue)) + "]";
        }

        return value.ToYamlString().Trim();
    }
}
