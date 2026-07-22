// SPDX-License-Identifier: LGPL-3.0-or-later
using System.ComponentModel;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.AI;
using OKF4net.Internal;
using OKF4net.Yaml;

namespace OKF4net.Agents;

/// <summary>OKF bundle operations exposed as Microsoft Agent Framework function tools.</summary>
public sealed class OkfBundleTools
{
    private const string IndexFilename = "index.md";
    private const string LogFilename = "log.md";
    private const string NoneLine = "(none)";

    private const string SearchUsageMessage =
        "Usage: okf_search requires a non-empty query — one or more terms to match "
        + "(case-insensitive substring) against concept titles, descriptions, tags and "
        + "bodies. Example: okf_search(\"orders\").";

    private const string ChangesSinceUsageMessage =
        "Usage: okf_changes_since requires a valid ISO date (yyyy-MM-dd), inclusive. "
        + "Example: okf_changes_since(\"2026-01-01\").";

    /// <summary>
    /// Guards <see cref="_bundle"/> and every write this class performs to
    /// disk. Agent hosts may invoke tool methods concurrently from multiple
    /// threads, so the lazy cache in <see cref="GetBundle"/> and the
    /// invalidation in <see cref="InvalidateBundle"/> must not race; the same
    /// lock also serializes <see cref="WriteConcept"/>, <see cref="AppendLog"/>,
    /// and <see cref="RegenerateIndexes"/>'s own read-modify-write sequences
    /// (each holds it around the read, the write, and its own cache
    /// invalidation), so two concurrent calls into the same tool can't
    /// interleave and lose one side's update.
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
    /// The current UTC time, consulted by <see cref="AppendLog"/> to compute
    /// "today"'s ISO date heading. Defaults to <see cref="DateTime.UtcNow"/>;
    /// overridable so tests can pin the date deterministically. Internal: an
    /// implementation seam, not part of the tool's public surface.
    /// </summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

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
    /// All nine OKF tools as Agent Framework <see cref="AIFunction"/>s (via
    /// <see cref="AITool"/>), ready for <c>AsAIAgent(tools: ...)</c>. Each
    /// call returns a fresh list of freshly-created <see cref="AIFunction"/>
    /// instances bound to this <see cref="OkfBundleTools"/> — invoking one
    /// invokes the corresponding public method above, including its
    /// never-throw behavior.
    ///
    /// Names are explicit snake_case (the default would be the C# method
    /// name); descriptions are omitted here and instead derived by
    /// <see cref="AIFunctionFactory"/> from each method's own
    /// <see cref="DescriptionAttribute"/> — the single source of truth, so
    /// the two can never drift apart. The order is stable: read → browse →
    /// graph → search → write → append → regenerate → validate →
    /// changes-since.
    /// </summary>
    public IList<AITool> GetTools() =>
    [
        AIFunctionFactory.Create(ReadConcept, "okf_read_concept"),
        AIFunctionFactory.Create(Browse, "okf_browse"),
        AIFunctionFactory.Create(Graph, "okf_graph"),
        AIFunctionFactory.Create(Search, "okf_search"),
        AIFunctionFactory.Create(WriteConcept, "okf_write_concept"),
        AIFunctionFactory.Create(AppendLog, "okf_append_log"),
        AIFunctionFactory.Create(RegenerateIndexes, "okf_regenerate_indexes"),
        AIFunctionFactory.Create(ValidateBundle, "okf_validate_bundle"),
        AIFunctionFactory.Create(ChangesSince, "okf_changes_since"),
    ];

    /// <summary>
    /// Reads one concept: its frontmatter, body, outgoing links, and
    /// backlinks, rendered as agent-friendly markdown. Never throws for
    /// expected errors (a null/blank, malformed, or unknown concept id, or a
    /// bundle that fails to (re)load) — those are reported as a plain-text
    /// message instead.
    /// </summary>
    /// <param name="conceptId">The concept id, e.g. <c>tables/orders</c>.</param>
    [Description("Read one concept from the OKF bundle: its frontmatter, body, outgoing links and backlinks.")]
    public string ReadConcept([Description("The concept id, e.g. 'tables/orders'.")] string conceptId)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return ConceptNotFoundMessage(conceptId ?? string.Empty);
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        return RunTool(() =>
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
        });
    }

    /// <summary>
    /// Browses the bundle via its <c>index.md</c> files (progressive
    /// disclosure): returns the raw content of the requested directory's
    /// index if one exists, otherwise a generated listing of the concepts
    /// and subdirectories at that level. Never throws for expected errors
    /// (an invalid, traversing, or out-of-bundle path, or a bundle that
    /// fails to (re)load) — those are reported as a plain-text message
    /// instead.
    /// </summary>
    /// <param name="path">Optional directory path within the bundle, e.g. <c>tables</c>. Omit to list the bundle root.</param>
    [Description("Browse the bundle via its index files (progressive disclosure). Without a path, lists the bundle root.")]
    public string Browse([Description("Optional directory path within the bundle, e.g. 'tables'.")] string? path = null)
    {
        if (path is not null && path.Contains('\0'))
        {
            return "Error: invalid path — it must not contain a null character.";
        }

        return RunTool(() =>
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

            if (!IsWithinBundleRoot(bundle.Root, fullDir)
                || !Directory.Exists(fullDir)
                || HasReparsePointAncestor(bundle.Root, fullDir))
            {
                return $"Error: path '{path}' not found in the bundle. Use okf_browse to list available directories.";
            }

            var indexPath = Path.Combine(fullDir, IndexFilename);
            if (File.Exists(indexPath))
            {
                return File.ReadAllText(indexPath);
            }

            return BuildLevelListing(bundle, segments, relPath);
        });
    }

    /// <summary>
    /// Inspects the cross-link graph: bundle-wide stats, or (with a concept
    /// id) that concept's outgoing links, backlinks, and broken links.
    /// Never throws for expected errors (an unknown concept id, or a bundle
    /// that fails to (re)load) — reported as a plain-text message instead.
    /// </summary>
    /// <param name="conceptId">Optional concept id to focus on.</param>
    [Description("Inspect the cross-link graph. With a concept id: its outgoing links, backlinks and broken links. Without: bundle-wide stats.")]
    public string Graph([Description("Optional concept id to focus on.")] string? conceptId = null)
    {
        if (conceptId is not null && conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        return RunTool(() =>
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
        });
    }

    /// <summary>
    /// Full-text search across concept titles, descriptions, tags and
    /// bodies. Never throws for expected errors (a null/blank query, a
    /// query or tag containing a null character, or a bundle that fails to
    /// (re)load) — those are reported as a plain-text message instead.
    ///
    /// The query is split into terms on whitespace; each term is matched as
    /// an <see cref="StringComparison.OrdinalIgnoreCase"/> substring. A
    /// concept's score is the sum, over all terms, of the weights of every
    /// field the term is found in: title ×3, tags/description ×2, body ×1.
    /// Concepts scoring zero are dropped. Results are sorted by descending
    /// score, then ascending concept id (ordinal), bounded to the top 20
    /// with the total match count reported alongside.
    /// </summary>
    /// <param name="query">The search query (case-insensitive substring terms).</param>
    /// <param name="tag">Optional tag filter: only concepts carrying this tag.</param>
    [Description("Full-text search across concept titles, descriptions, tags and bodies. Returns matching concept ids ranked by relevance.")]
    public string Search(
        [Description("The search query (case-insensitive substring terms).")] string query,
        [Description("Optional tag filter: only concepts carrying this tag.")] string? tag = null)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            return SearchUsageMessage;
        }

        if (query.Contains('\0'))
        {
            return "Error: invalid query — it must not contain a null character.";
        }

        if (tag is not null && tag.Contains('\0'))
        {
            return "Error: invalid tag — it must not contain a null character.";
        }

        return RunTool(() =>
        {
            var terms = query.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (terms.Length == 0)
            {
                return SearchUsageMessage;
            }

            var bundle = GetBundle();
            var effectiveTag = string.IsNullOrWhiteSpace(tag) ? null : tag;

            var candidates = effectiveTag is null
                ? bundle.Concepts
                : bundle.Concepts.Where(c => c.Document.Frontmatter.Tags.Any(t => string.Equals(t, effectiveTag, StringComparison.OrdinalIgnoreCase)));

            var scored = candidates
                .Select(c => (Concept: c, Score: ScoreConcept(c, terms)))
                .Where(x => x.Score > 0)
                .OrderByDescending(x => x.Score)
                .ThenBy(x => x.Concept.Id)
                .ToList();

            if (scored.Count == 0)
            {
                return effectiveTag is null
                    ? $"No results for query '{query}'."
                    : $"No results for query '{query}' with tag '{effectiveTag}'.";
            }

            return FormatSearchResults(query, effectiveTag, terms, scored);
        });
    }

    /// <summary>
    /// Creates or updates one concept document. Producer-grade validation
    /// (<see cref="OkfDocument.Validate"/>: non-empty <c>type</c>,
    /// <c>title</c>, <c>description</c> and <c>timestamp</c>) runs BEFORE
    /// anything is written — on failure, the file on disk (if any) is left
    /// untouched. Never throws for expected errors (a null/blank/malformed
    /// concept id, a reserved id, invalid frontmatter YAML, or a failed
    /// validation) — those are reported as a plain-text message instead.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>), e.g. <c>tables/refunds</c>.</param>
    /// <param name="frontmatterYaml">Frontmatter as <c>key: value</c> lines (the same YAML subset used inside a document's frontmatter block, without the <c>---</c> delimiters).</param>
    /// <param name="body">The markdown body.</param>
    [Description("Create or update a concept document. The frontmatter must contain non-empty type, title, description and timestamp (producer-grade validation is enforced before writing).")]
    public string WriteConcept(
        [Description("The concept id (path without .md), e.g. 'tables/refunds'.")] string conceptId,
        [Description("Frontmatter as 'key: value' lines (YAML subset).")] string frontmatterYaml,
        [Description("The markdown body.")] string body)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return "Error: invalid concept id — it must not be empty.";
        }

        if (conceptId.Contains('\0'))
        {
            return "Error: invalid concept id — it must not contain a null character.";
        }

        if (frontmatterYaml is null)
        {
            return "Error: frontmatter must not be null.";
        }

        if (frontmatterYaml.Contains('\0'))
        {
            return "Error: invalid frontmatter — it must not contain a null character.";
        }

        if (body is null)
        {
            return "Error: body must not be null.";
        }

        if (body.Contains('\0'))
        {
            return "Error: invalid body — it must not contain a null character.";
        }

        return RunTool(() =>
        {
            if (!ConceptId.TryParse(conceptId, out var id))
            {
                return $"Error: invalid concept id '{conceptId}'. Concept ids are '/'-separated "
                    + "segments matching [A-Za-z0-9_][A-Za-z0-9_.-]*.";
            }

            if (string.Equals(id.Name, "index", StringComparison.OrdinalIgnoreCase)
                || string.Equals(id.Name, "log", StringComparison.OrdinalIgnoreCase))
            {
                return $"Error: '{id}' is a reserved concept id — the last segment must not be "
                    + "'index' or 'log' in any casing (these would collide with the bundle's "
                    + "index.md/log.md files on case-insensitive filesystems).";
            }

            // Throws YamlParseException (line-tagged message) on malformed input;
            // caught by RunTool's catch-all below, before anything is written.
            var yaml = YamlValue.Parse(frontmatterYaml);
            Frontmatter? frontmatter = yaml switch
            {
                YamlNull => new Frontmatter(),
                YamlMapping map => Frontmatter.FromMapping(map),
                _ => null,
            };

            if (frontmatter is null)
            {
                return "Error: frontmatter must be a YAML mapping of 'key: value' lines, not a list or scalar.";
            }

            var doc = new OkfDocument(frontmatter, body);

            // Strict producer validation BEFORE any write. On failure this
            // throws DocumentValidationException (message lists MissingKeys),
            // caught by RunTool below -- nothing is written for a failed write.
            doc.Validate();

            var targetPath = id.ToPath(BundleRoot);

            // Belt and braces alongside ConceptId's own segment validation
            // (which already forbids '..' and '/' inside a segment): the same
            // defense-in-depth check Browse uses before touching disk.
            if (!IsWithinBundleRoot(BundleRoot, targetPath))
            {
                return $"Error: '{id}' resolves outside the bundle root.";
            }

            // Reject a reparse point (symlink/junction) anywhere between the
            // bundle root and the target's parent directory: the lexical
            // check above would happily accept "tables/refunds" even if
            // "tables" is a junction pointing outside the bundle -- the OS
            // follows it when Directory.CreateDirectory/File.WriteAllText
            // below actually touch disk. Same guard Browse uses.
            var targetParentDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetParentDir) && HasReparsePointAncestor(BundleRoot, targetParentDir))
            {
                return $"Error: '{id}' resolves through a reparse point (symlink/junction) inside the bundle, which is not allowed.";
            }

            var content = doc.Serialize();
            bool existed;

            // Serialized under _bundleLock (shared with AppendLog and
            // RegenerateIndexes) so concurrent writers can't interleave an
            // existence check with another writer's write, and so the cache
            // invalidation below is atomic with the write it follows.
            lock (_bundleLock)
            {
                existed = File.Exists(targetPath);

                var parentDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(parentDir))
                {
                    Directory.CreateDirectory(parentDir);
                }

                File.WriteAllText(targetPath, content, OkfEncodings.NoBom);
                _bundle = null;
            }

            var byteCount = OkfEncodings.NoBom.GetByteCount(content);
            var status = existed ? "updated" : "new";
            return $"Written {id} ({status}, {byteCount} bytes). Remember to run okf_regenerate_indexes.";
        });
    }

    /// <summary>
    /// Appends one entry to the bundle root's <c>log.md</c> under today's
    /// (UTC) ISO date, creating the file if it does not yet exist. If a
    /// heading for today's date already exists, the entry is appended to the
    /// end of that day's entries (days are newest-first by convention (§7),
    /// but entries within a day stay chronological). The read-modify-write is
    /// serialized under <see cref="_bundleLock"/> (shared with
    /// <see cref="WriteConcept"/> and <see cref="RegenerateIndexes"/>) so
    /// concurrent calls can't lose an update to each other. The existing file,
    /// if any, is read with the same strict-UTF-8 decoding <see cref="ChangesSince"/>
    /// uses, then re-rendered through <see cref="ChangeLog.ToMarkdown"/> — the
    /// strict §7 model — so any non-conforming prose or comments in a
    /// hand-authored <c>log.md</c> are not preserved. Never throws for
    /// expected errors (a null/blank/embedded-null <paramref name="kind"/> or
    /// <paramref name="text"/>, or a <c>log.md</c> that fails strict UTF-8
    /// decoding) — those are reported as a plain-text message instead.
    /// </summary>
    /// <param name="kind">Entry kind, e.g. <c>Update</c> or <c>Creation</c>.</param>
    /// <param name="text">The entry text.</param>
    [Description("Append an entry to the bundle root log.md under today's date (ISO). Note: log.md is re-rendered through the strict §7 model, so non-conforming prose or comments in a hand-authored log.md are not preserved.")]
    public string AppendLog(
        [Description("Entry kind, e.g. 'Update' or 'Creation'.")] string kind,
        [Description("The entry text.")] string text)
    {
        if (string.IsNullOrWhiteSpace(kind))
        {
            return "Error: invalid kind — it must not be empty.";
        }

        if (kind.Contains('\0'))
        {
            return "Error: invalid kind — it must not contain a null character.";
        }

        if (kind.Contains('\n') || kind.Contains('\r'))
        {
            return "Error: invalid kind — it must not contain a line break (this would let it "
                + "forge fake '## date' or '* entry' lines in log.md).";
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return "Error: invalid text — it must not be empty.";
        }

        if (text.Contains('\0'))
        {
            return "Error: invalid text — it must not contain a null character.";
        }

        if (text.Contains('\n') || text.Contains('\r'))
        {
            return "Error: invalid text — it must not contain a line break (this would let it "
                + "forge fake '## date' or '* entry' lines in log.md).";
        }

        return RunTool(() =>
        {
            var logPath = Path.Combine(BundleRoot, LogFilename);
            var today = UtcNow().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            var entry = new LogEntry(kind.Trim(), text.Trim());

            // Serialized under _bundleLock (shared with WriteConcept and
            // RegenerateIndexes): without it, two concurrent AppendLog calls
            // could both read the same "before" text, each append their own
            // entry to it, and the second write would silently clobber the
            // first (a lost update). Locking the whole read-modify-write
            // makes it atomic.
            lock (_bundleLock)
            {
                // Strict UTF-8, matching ChangesSince's AppendLogFileChanges:
                // a non-UTF-8 log.md throws DecoderFallbackException (caught
                // by RunTool below) instead of being silently re-decoded with
                // U+FFFD replacement characters and then rewritten that way.
                var existingText = File.Exists(logPath)
                    ? OkfEncodings.Strict.GetString(File.ReadAllBytes(logPath))
                    : string.Empty;

                // ChangeLog.Parse is permissive (never throws); used here only to
                // locate today's day (if any) among the existing entries.
                var changeLog = ChangeLog.Parse(existingText);

                var days = changeLog.Days.ToList();
                var dayIndex = days.FindIndex(d => string.Equals(d.Date, today, StringComparison.Ordinal));
                if (dayIndex >= 0)
                {
                    var day = days[dayIndex];
                    days[dayIndex] = day with { Entries = [.. day.Entries, entry] };
                }
                else
                {
                    // New LogDay at the head: days are newest-first (§7).
                    days.Insert(0, new LogDay(today, [entry]));
                }

                File.WriteAllText(logPath, new ChangeLog(changeLog.Title, days).ToMarkdown(), OkfEncodings.NoBom);
                _bundle = null;
            }

            return $"Appended a '{kind}' entry under {today} in log.md.";
        });
    }

    /// <summary>
    /// Regenerates every <c>index.md</c> in the bundle (progressive
    /// disclosure listings). The regeneration and cache invalidation are
    /// serialized under <see cref="_bundleLock"/> (shared with
    /// <see cref="WriteConcept"/> and <see cref="AppendLog"/>) so a
    /// concurrent write can't be missed by (or interleave with) this pass.
    /// Never throws for expected errors (a bundle root that disappeared out
    /// from under it) — reported as a plain-text message instead.
    /// </summary>
    [Description("Regenerate every index.md in the bundle (progressive-disclosure listings). Run after adding or changing concepts.")]
    public string RegenerateIndexes()
    {
        return RunTool(() =>
        {
            IReadOnlyList<string> written;
            lock (_bundleLock)
            {
                written = IndexGenerator.RegenerateIndexes(BundleRoot);
                _bundle = null;
            }

            if (written.Count == 0)
            {
                return "No index.md files were regenerated (empty bundle?).";
            }

            var relative = written
                .Select(p => Path.GetRelativePath(BundleRoot, p).Replace('\\', '/'))
                .ToList();

            var sb = new StringBuilder();
            sb.Append("Regenerated ").Append(relative.Count).Append(" index file(s):").Append('\n');
            foreach (var rel in relative)
            {
                sb.Append("- ").Append(rel).Append('\n');
            }

            return sb.ToString();
        });
    }

    /// <summary>
    /// Validates the bundle against OKF v0.1 conformance (§9) and renders the
    /// report the same way the CLI's <c>validate</c> command does: one line
    /// per <see cref="Diagnostic"/> (via its own <see cref="Diagnostic.ToString"/>),
    /// then a summary line with the concept/error/warning/info counts and a
    /// conformant ✓/✗ verdict. Never throws for expected errors (a bundle
    /// that fails to (re)load) — reported as a plain-text message instead.
    /// </summary>
    [Description("Validate the bundle against OKF v0.1 conformance (§9). Returns the diagnostics report.")]
    public string ValidateBundle()
    {
        return RunTool(() =>
        {
            var bundle = GetBundle();
            var report = BundleValidator.Validate(bundle);

            var sb = new StringBuilder();
            foreach (var diagnostic in report.Diagnostics)
            {
                sb.Append(diagnostic).Append('\n');
            }

            var errors = report.ErrorCount;
            var warnings = report.WarningCount;
            var infos = report.Of(Severity.Info).Count();
            sb.Append('\n')
                .Append(bundle.Count).Append(" concept(s); ")
                .Append(errors).Append(" error(s), ")
                .Append(warnings).Append(" warning(s), ")
                .Append(infos).Append(" info.").Append('\n');

            sb.Append(report.IsConformant
                ? $"✓ conformant with OKF v{OkfSpec.Version}"
                : $"✗ not conformant with OKF v{OkfSpec.Version}").Append('\n');

            return sb.ToString();
        });
    }

    /// <summary>
    /// Summarizes bundle changes since a given ISO date (inclusive),
    /// aggregated across every <c>log.md</c> in the bundle (<see cref="Bundle.LogFiles"/>).
    /// Each log is parsed with <see cref="ChangeLog.Parse"/>, filtered to the
    /// <see cref="LogDay"/>s whose <see cref="LogDay.Date"/> is a valid ISO
    /// date (<see cref="ChangeLog.IsIsoDate"/>) greater than or equal to
    /// <paramref name="sinceDate"/> (ordinal string comparison — sufficient
    /// for well-formed ISO dates; non-ISO headings, e.g. a stray
    /// <c>## Notes</c> section, are excluded rather than risk a bogus
    /// ordinal comparison — <see cref="BundleValidator"/> is what reports
    /// those as diagnostics), and rendered newest-first, grouped by the log
    /// file's path relative to the bundle root ('/' separators). A log file
    /// that fails strict UTF-8 decoding is skipped with a note line rather
    /// than aborting the whole report (mirroring <see cref="BundleValidator"/>'s
    /// permissive handling of reserved files); that note is preserved even
    /// when no other log contributes matching days. Never throws for
    /// expected errors (a null/blank/invalid date, or a bundle that fails to
    /// (re)load) — those are reported as a plain-text message instead.
    /// </summary>
    /// <param name="sinceDate">ISO date (<c>yyyy-MM-dd</c>), inclusive.</param>
    [Description("Summarize bundle changes since a given ISO date, aggregated from every log.md in the bundle.")]
    public string ChangesSince([Description("ISO date (yyyy-MM-dd), inclusive.")] string sinceDate)
    {
        if (string.IsNullOrWhiteSpace(sinceDate))
        {
            return ChangesSinceUsageMessage;
        }

        if (sinceDate.Contains('\0'))
        {
            return "Error: invalid date — it must not contain a null character.";
        }

        var date = sinceDate.Trim();
        if (!ChangeLog.IsIsoDate(date))
        {
            return ChangesSinceUsageMessage;
        }

        return RunTool(() =>
        {
            var bundle = GetBundle();
            var notes = new StringBuilder();
            var changes = new StringBuilder();
            var any = false;

            foreach (var logPath in bundle.LogFiles)
            {
                any |= AppendLogFileChanges(bundle.Root, logPath, date, notes, changes);
            }

            if (!any)
            {
                // Preserve any skip notes even when nothing matched — a
                // silently-discarded note would hide a real read failure
                // behind an otherwise-correct "no changes" report.
                return notes.Length == 0 ? $"No changes since {date}." : notes + $"No changes since {date}.";
            }

            var sb = new StringBuilder();
            sb.Append("# Changes since ").Append(date).Append('\n').Append('\n');
            sb.Append(notes);
            sb.Append(changes);
            return sb.ToString();
        });
    }

    /// <summary>
    /// Processes one <c>log.md</c> for <see cref="ChangesSince"/>: on a
    /// strict-UTF-8 read failure, appends a skip note to <paramref name="notes"/>
    /// and returns <c>false</c>; otherwise parses the log, filters to valid-ISO
    /// days at or after <paramref name="date"/> (descending), and — if any
    /// matched — appends a <c>## {relative path}</c> section to <paramref name="changes"/>
    /// and returns <c>true</c>.
    /// </summary>
    private static bool AppendLogFileChanges(string bundleRoot, string logPath, string date, StringBuilder notes, StringBuilder changes)
    {
        var rel = Path.GetRelativePath(bundleRoot, logPath).Replace('\\', '/');

        string text;
        try
        {
            text = OkfEncodings.Strict.GetString(File.ReadAllBytes(logPath));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            notes.Append("> Skipped ").Append(rel).Append(" (could not be read: ")
                .Append(SkipReason(ex)).Append(").").Append('\n').Append('\n');
            return false;
        }

        var matchingDays = ChangeLog.Parse(text).Days
            .Where(d => ChangeLog.IsIsoDate(d.Date) && string.CompareOrdinal(d.Date, date) >= 0)
            .OrderByDescending(d => d.Date, StringComparer.Ordinal)
            .ToList();

        if (matchingDays.Count == 0)
        {
            return false;
        }

        changes.Append("## ").Append(rel).Append('\n');
        foreach (var day in matchingDays)
        {
            AppendLogDay(changes, day);
        }

        changes.Append('\n');
        return true;
    }

    /// <summary>Appends one <c>### {date}</c> section and its bulleted entries (bold <c>Kind</c> when present) to <paramref name="sb"/>.</summary>
    private static void AppendLogDay(StringBuilder sb, LogDay day)
    {
        sb.Append("### ").Append(day.Date).Append('\n');
        foreach (var entry in day.Entries)
        {
            if (entry.Kind is not null)
            {
                sb.Append("- **").Append(entry.Kind).Append("**: ").Append(entry.Text).Append('\n');
            }
            else
            {
                sb.Append("- ").Append(entry.Text).Append('\n');
            }
        }
    }

    /// <summary>Brief, non-sensitive reason category for a log-file read failure, used by <see cref="ChangesSince"/>'s skip note.</summary>
    private static string SkipReason(Exception ex) => ex switch
    {
        DecoderFallbackException => "not valid UTF-8",
        UnauthorizedAccessException => "access denied",
        IOException => "I/O error",
        _ => "unreadable",
    };

    /// <summary>Renders the ranked, bounded (top 20) search results as markdown, with the total match count.</summary>
    private static string FormatSearchResults(string query, string? tag, IReadOnlyList<string> terms, IReadOnlyList<(Concept Concept, int Score)> scored)
    {
        const int MaxResults = 20;
        var shown = scored.Take(MaxResults).ToList();

        var sb = new StringBuilder();
        sb.Append("# Search: \"").Append(query).Append('"');
        if (tag is not null)
        {
            sb.Append(" (tag: ").Append(tag).Append(')');
        }

        sb.Append('\n').Append('\n');
        sb.Append("Showing ").Append(shown.Count).Append(" of ").Append(scored.Count).Append(" result(s).").Append('\n').Append('\n');

        foreach (var (concept, score) in shown)
        {
            var title = concept.Document.Frontmatter.Title ?? concept.Id.ToString();
            sb.Append("* ").Append(concept.Id).Append(" — ").Append(title).Append(" (").Append(score).Append(')').Append('\n');

            var excerpt = FindExcerpt(concept.Document.Body, terms);
            if (excerpt is not null)
            {
                sb.Append("  ").Append(excerpt).Append('\n');
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// A concept's relevance score for <paramref name="terms"/>: the sum,
    /// over all terms, of the weights of every field the term is found in
    /// (<see cref="StringComparison.OrdinalIgnoreCase"/> substring match):
    /// title ×3, tags/description ×2, body ×1.
    /// </summary>
    private static int ScoreConcept(Concept concept, IReadOnlyList<string> terms)
    {
        var frontmatter = concept.Document.Frontmatter;
        var title = frontmatter.Title ?? string.Empty;
        var tagsAndDescription = string.Join(' ', frontmatter.Tags) + ' ' + (frontmatter.Description ?? string.Empty);
        var body = concept.Document.Body;

        var score = 0;
        foreach (var term in terms)
        {
            if (title.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 3;
            }

            if (tagsAndDescription.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 2;
            }

            if (body.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                score += 1;
            }
        }

        return score;
    }

    /// <summary>The first non-blank line of <paramref name="body"/> containing any of <paramref name="terms"/> (substring, ordinal-ignore-case), or <c>null</c> if none does.</summary>
    private static string? FindExcerpt(string body, IReadOnlyList<string> terms)
    {
        foreach (var rawLine in body.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (terms.Any(term => line.Contains(term, StringComparison.OrdinalIgnoreCase)))
            {
                return line;
            }
        }

        return null;
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

    /// <summary>
    /// Runs a tool method body, converting any exception that a well-formed
    /// but unlucky input could still trigger — a bundle that fails to
    /// (re)load (<see cref="OkfException"/>, e.g. <see cref="BundleLoadException"/>
    /// for I/O failures, a missing root, or non-UTF-8 content), a rejected
    /// argument surfaced late by a BCL API (<see cref="ArgumentException"/>),
    /// a filesystem read failure (<see cref="IOException"/>,
    /// <see cref="UnauthorizedAccessException"/>), or a strict-UTF-8 decode
    /// failure reading an existing reserved file directly
    /// (<see cref="DecoderFallbackException"/>, e.g. <see cref="AppendLog"/>
    /// reading a non-UTF-8 <c>log.md</c>) — into a plain-text message. This
    /// is the single enforcement point for the "tools never throw toward the
    /// LLM" rule: callers still perform their own null/whitespace and
    /// null-character guards up front (for a precise, tool-specific message),
    /// but this catch-all is what makes every public tool method structurally
    /// unable to throw for any string input, now and for tools added later.
    /// </summary>
    private static string RunTool(Func<string> body)
    {
        try
        {
            return body();
        }
        catch (Exception ex) when (ex is OkfException or ArgumentException or IOException or UnauthorizedAccessException or DecoderFallbackException)
        {
            return $"Error: {ex.Message}";
        }
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
    /// <c>true</c> if <paramref name="path"/> itself, or any directory
    /// strictly between it and <paramref name="bundleRoot"/>, is a
    /// filesystem reparse point (symlink, junction, mount point) -- checked
    /// via <see cref="ReparsePoints.IsReparsePoint"/>, which reports the
    /// entry's own type (lstat-like) without following it.
    ///
    /// <see cref="IsWithinBundleRoot"/> only compares resolved path STRINGS:
    /// a junction at, say, <c>bundleRoot/tables</c> pointing at an external
    /// directory still lexically resolves to a path under
    /// <paramref name="bundleRoot"/> via <see cref="Path.GetFullPath(string)"/>,
    /// so that check alone would accept it -- but the OS follows the
    /// junction the moment <see cref="Browse"/> or <see cref="WriteConcept"/>
    /// actually touches disk (<see cref="Directory.Exists(string)"/>,
    /// <see cref="File.ReadAllText(string)"/>, <see cref="File.WriteAllText(string, string)"/>),
    /// silently reading or writing outside the bundle. Walking every
    /// intermediate directory and rejecting on the first reparse point
    /// closes that gap.
    ///
    /// Used by <see cref="Browse"/> (on the resolved directory) and
    /// <see cref="WriteConcept"/> (on the target file's parent directory).
    /// Not needed by <see cref="ReadConcept"/>, <see cref="Graph"/>, or
    /// <see cref="Search"/> -- they only query the already-loaded
    /// <see cref="Bundle"/>, whose own load walk already skips reparse-point
    /// entries (mirroring Rust's lstat-based <c>collect_markdown</c>); nor by
    /// <see cref="RegenerateIndexes"/>, whose whole-root walk (<see cref="IndexGenerator"/>)
    /// likewise already skips reparse-point directories via the same core
    /// helper; nor by <see cref="AppendLog"/>, which only ever writes
    /// <c>log.md</c> directly at <see cref="BundleRoot"/> and never resolves
    /// a caller-supplied sub-path.
    /// </summary>
    private static bool HasReparsePointAncestor(string bundleRoot, string path)
    {
        var fullRoot = Path.GetFullPath(bundleRoot);
        var current = Path.GetFullPath(path);

        while (!string.Equals(current, fullRoot, StringComparison.OrdinalIgnoreCase))
        {
            if (ReparsePoints.IsReparsePoint(current))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                // Walked past the filesystem root without ever reaching
                // bundleRoot -- callers already guard containment via
                // IsWithinBundleRoot, but stop here rather than loop forever.
                break;
            }

            current = parent;
        }

        return false;
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
