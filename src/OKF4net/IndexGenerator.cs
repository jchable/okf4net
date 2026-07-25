// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Internal;

namespace OKF4net;

/// <summary>
/// Generation of <c>index.md</c> directory listings (§6).
///
/// This is a port of the reference <c>bundle/index.py</c>'s
/// <c>regenerate_indexes</c> and <c>_build_index_text</c>. The reference
/// synthesizes subdirectory descriptions with an LLM; since OKF tooling must
/// not require any particular model or network access, the description
/// synthesizer here is a pluggable delegate with a deterministic,
/// dependency-free default (<see cref="DefaultSynthesize"/>). Port of the
/// Rust <c>index</c> module (src/index.rs). Ported to Rust and modified from
/// the original Apache-2.0 Python source; see the NOTICE file.
/// </summary>
public static class IndexGenerator
{
    private const string IndexFile = "index.md";

    /// <summary>
    /// A synthesizer for subdirectory descriptions: given the directory's
    /// path (relative to the bundle root) and its child (title, description)
    /// pairs, returns a one-line description. Port of Rust's
    /// <c>Synthesize</c> type alias (index.rs:70).
    /// </summary>
    public delegate string Synthesize(string relativeDir, IReadOnlyList<(string Title, string Description)> children);

    /// <summary>
    /// Builds the markdown text of an <c>index.md</c> from a set of entries:
    /// entries are grouped by type under <c>#</c>-headings (types sorted
    /// ascending, ordinal), and within each group sorted by title
    /// (case-insensitive). Port of <c>build_index_text</c> (index.rs:36-65).
    /// </summary>
    public static string BuildIndexText(IReadOnlyList<IndexEntry> entries)
    {
        var grouped = new SortedDictionary<string, List<(string Title, string Link, string Description)>>(StringComparer.Ordinal);
        foreach (var e in entries)
        {
            var key = e.Type.Length == 0 ? "Other" : e.Type;
            if (!grouped.TryGetValue(key, out var list))
            {
                list = [];
                grouped[key] = list;
            }

            list.Add((e.Title, e.Link, e.Description));
        }

        var sections = new List<string>();
        foreach (var (typ, items) in grouped)
        {
            // Stable sort by lowercased title, mirroring Rust's stable
            // `sort_by` on `title.to_lowercase()`. Uses RustCaseFold rather
            // than string.ToLowerInvariant()/StringComparer.Ordinal, which
            // diverge from Rust's `to_lowercase` + `String::cmp` on
            // characters like U+0130 (İ) and on code points outside the
            // Basic Multilingual Plane -- see RustCaseFold's doc comments.
            var sorted = items
                .Select((item, ordinal) => (item, ordinal))
                .OrderBy(x => RustCaseFold.ToLowercase(x.item.Title), Comparer<string>.Create(RustCaseFold.CompareCodePoints))
                .ThenBy(x => x.ordinal)
                .Select(x => x.item)
                .ToList();

            var lines = new List<string> { $"# {typ}", string.Empty };
            foreach (var (title, link, description) in sorted)
            {
                var suffix = description.Length == 0 ? string.Empty : $" - {description}";
                lines.Add($"* [{title}]({link}){suffix}");
            }

            sections.Add(string.Join('\n', lines));
        }

        return string.Join("\n\n", sections) + "\n";
    }

    /// <summary>
    /// The default, deterministic synthesizer: lists the child titles. Used
    /// when no custom (e.g. LLM-backed) synthesizer is supplied. Port of
    /// <c>default_synthesize</c> (index.rs:74-80).
    /// </summary>
    public static string DefaultSynthesize(string relativeDir, IReadOnlyList<(string Title, string Description)> children)
    {
        if (children.Count == 0)
        {
            return string.Empty;
        }

        var titles = children.Select(c => c.Title);
        return $"Contains {children.Count}: {string.Join(", ", titles)}.";
    }

    /// <summary>
    /// Regenerates every <c>index.md</c> in the bundle using
    /// <see cref="DefaultSynthesize"/>. Port of <c>regenerate_indexes</c>
    /// (index.rs:82-85).
    /// </summary>
    public static IReadOnlyList<string> RegenerateIndexes(string bundleRoot) =>
        RegenerateIndexesWith(bundleRoot, DefaultSynthesize);

    /// <summary>
    /// Test-only hook invoked immediately before the late reparse-point
    /// re-check that runs right before each <c>index.md</c> write (see
    /// <see cref="RegenerateIndexesWith"/>'s remarks), with the directory
    /// about to be written as its argument. Used by <c>IndexTests</c> to
    /// deterministically substitute a directory that already passed the
    /// early traversal skip with a junction/symlink, in the exact narrow
    /// window such a race would need to land in -- a substitution the
    /// earlier, best-effort <see cref="CollectMarkdown"/> skip could never
    /// have caught, since the directory was still real when it ran.
    /// <c>internal</c> rather than test-conditional compilation, consistent
    /// with this assembly's <c>InternalsVisibleTo</c> grant to
    /// <c>OKF4net.Tests</c>. No-op (null) in production.
    /// </summary>
    internal static Action<string>? BeforeLateReparseCheckForTest { get; set; }

    /// <summary>
    /// Regenerates every <c>index.md</c> in the bundle, deriving each
    /// subdirectory's description with the supplied synthesizer.
    ///
    /// Directories are processed deepest-first so a parent index can reuse
    /// the descriptions computed for its children. Empty directories are
    /// skipped. Returns the paths of the index files written. Port of
    /// <c>regenerate_indexes_with</c> (index.rs:93-186).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Reparse-point safety (defense-in-depth, not a full guarantee):</b>
    /// <see cref="DirectoriesToIndex"/>'s traversal (via
    /// <see cref="CollectMarkdown"/>) already performs an EARLY, best-effort
    /// skip of reparse-point directories (symlinks, junctions, mount
    /// points), matching Rust's lstat-based <c>collect_markdown</c>. That
    /// skip only protects the directories it can see AT COLLECTION TIME,
    /// though: a directory that was a genuine directory when collected could
    /// still be replaced by a reparse point before this method gets around
    /// to writing its <c>index.md</c> -- a classic check-then-write race
    /// (TOCTOU). Since <see cref="File.WriteAllText(string, string, System.Text.Encoding)"/>
    /// has no portable no-follow mode in .NET, such a write would silently
    /// land wherever the reparse point resolves -- potentially outside
    /// <paramref name="bundleRoot"/> entirely.
    /// </para>
    /// <para>
    /// To narrow (not close) that window, this method re-checks, immediately
    /// before each <c>index.md</c> write, that the target directory itself
    /// and every ancestor directory strictly UP TO (but not including)
    /// <paramref name="bundleRoot"/> is still free of reparse points (see
    /// the private <c>HasReparsePointAncestor</c> helper, which reuses
    /// <see cref="ReparsePoints.IsReparsePoint"/> -- the same primitive the
    /// early skip uses -- rather than duplicating platform-specific reparse
    /// detection). <paramref name="bundleRoot"/> itself is deliberately
    /// exempt from this check -- see <c>HasReparsePointAncestor</c>'s own
    /// doc comment for why: a symlinked/mounted bundle root is a legitimate
    /// setup that the early traversal already indexes unconditionally, and
    /// treating it as suspect here would silently suppress every index
    /// write for such a bundle. A reparse point detected at either the
    /// early or the late point is handled the same way: that <c>index.md</c>
    /// write is SKIPPED (not included in the returned list) and
    /// regeneration continues with the remaining directories -- it never
    /// aborts the whole run and never throws.
    /// </para>
    /// <para>
    /// This still does not fully close the gap: a substitution landing in
    /// the instant between the late check and the write itself would still
    /// slip through. No portable, race-free "check and write without
    /// following a symlink" primitive exists in .NET for this case.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<string> RegenerateIndexesWith(string bundleRoot, Synthesize synthesize)
    {
        var written = new List<string>();
        if (!Directory.Exists(bundleRoot))
        {
            return written;
        }

        var directories = DirectoriesToIndex(bundleRoot);
        // Deepest-first; ties broken by path for determinism.
        directories.Sort((a, b) =>
        {
            var da = Depth(bundleRoot, a);
            var db = Depth(bundleRoot, b);
            var cmp = db.CompareTo(da);
            return cmp != 0 ? cmp : PathOrdering.CompareComponentWise(a, b);
        });

        var dirDescriptions = new Dictionary<string, string>();

        foreach (var directory in directories)
        {
            var entries = new List<IndexEntry>();

            var children = Directory.GetFileSystemEntries(directory).ToList();
            children.Sort(PathOrdering.CompareComponentWise);

            foreach (var child in children)
            {
                var name = Path.GetFileName(child);
                if (name == IndexFile)
                {
                    continue;
                }

                if (File.Exists(child) && MarkdownPaths.HasMarkdownExtension(child))
                {
                    var doc = LoadDoc(child);
                    if (doc is null)
                    {
                        continue;
                    }

                    var stem = Path.GetFileNameWithoutExtension(child);
                    var title = doc.Frontmatter.Title ?? stem;
                    var description = doc.Frontmatter.Description ?? string.Empty;
                    var type_ = doc.Frontmatter.Type ?? string.Empty;
                    entries.Add(new IndexEntry(type_, title, name, description));
                }
                else if (Directory.Exists(child))
                {
                    var description = dirDescriptions.GetValueOrDefault(child, string.Empty);
                    entries.Add(new IndexEntry("Subdirectories", name, $"{name}/{IndexFile}", description));
                }
            }

            if (entries.Count == 0)
            {
                continue;
            }

            BeforeLateReparseCheckForTest?.Invoke(directory);

            // Late, best-effort re-check -- see RegenerateIndexesWith's
            // <remarks>. `directory` passed the EARLY skip during
            // DirectoriesToIndex's traversal, but that only proves it was a
            // real directory (with no reparse-point ancestor) at collection
            // time; it could have been replaced by a symlink/junction any
            // time between then and now. Re-checking immediately before the
            // write narrows (without closing) that window.
            if (HasReparsePointAncestor(bundleRoot, directory))
            {
                continue;
            }

            var indexPath = Path.Combine(directory, IndexFile);
            File.WriteAllText(indexPath, BuildIndexText(entries), OkfEncodings.NoBom);
            written.Add(indexPath);

            if (string.Equals(directory, bundleRoot, StringComparison.Ordinal))
            {
                continue;
            }

            var pairs = entries.Select(e => (e.Title, e.Description)).ToList();
            string desc;
            if (pairs.Count == 1 && pairs[0].Description.Length != 0)
            {
                desc = pairs[0].Description;
            }
            else
            {
                var rel = Path.GetRelativePath(bundleRoot, directory);
                desc = synthesize(rel, pairs);
            }

            dirDescriptions[directory] = desc;
        }

        return written;
    }

    /// <summary>Port of <c>load_doc</c> (index.rs:188-191).</summary>
    private static OkfDocument? LoadDoc(string path)
    {
        string text;
        try
        {
            text = OkfEncodings.Strict.GetString(File.ReadAllBytes(path));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.DecoderFallbackException)
        {
            return null;
        }

        try
        {
            return OkfDocument.Parse(text);
        }
        catch (DocumentParseException)
        {
            return null;
        }
    }

    /// <summary>Port of <c>depth</c> (index.rs:193-197).</summary>
    private static int Depth(string root, string dir)
    {
        var rel = Path.GetRelativePath(root, dir);
        if (rel == ".")
        {
            return 0;
        }

        return rel.Split(['\\', '/'], StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>
    /// All directories that contain at least one <c>.md</c> file at any
    /// depth, including the bundle root (matching the reference
    /// <c>_directories_to_index</c>). Port of <c>directories_to_index</c>
    /// (index.rs:201-221).
    /// </summary>
    private static List<string> DirectoriesToIndex(string bundleRoot)
    {
        var mdFiles = new List<string>();
        CollectMarkdown(bundleRoot, mdFiles);

        var dirs = new SortedSet<string>(Comparer<string>.Create(PathOrdering.CompareComponentWise));
        var rootParent = Path.GetDirectoryName(bundleRoot);
        foreach (var md in mdFiles)
        {
            var cur = Path.GetDirectoryName(md);
            while (cur is not null)
            {
                if (string.Equals(cur, rootParent, StringComparison.Ordinal))
                {
                    break;
                }

                dirs.Add(cur);
                if (string.Equals(cur, bundleRoot, StringComparison.Ordinal))
                {
                    break;
                }

                cur = Path.GetDirectoryName(cur);
            }
        }

        return dirs.ToList();
    }

    /// <summary>Port of <c>collect_markdown</c> (index.rs:223-234).</summary>
    private static void CollectMarkdown(string dir, List<string> output)
    {
        foreach (var path in Directory.GetFileSystemEntries(dir))
        {
            // Rust checks only `entry.file_type()?.is_dir()` (lstat-based)
            // before recursing (index.rs:227) -- a symlinked directory's
            // file_type() reports is_dir() == false, so it is never
            // descended into and never contributes a directory to
            // directories_to_index. Directory.Exists instead follows the
            // link (like Rust's *non*-lstat Path::is_dir()), so reparse
            // points are excluded from the recursion arm here. The file
            // branch below is a pure extension check in Rust too -- no
            // is_file() guard -- so a symlink named `*.md` is still
            // collected either way, matching Rust exactly.
            if (!ReparsePoints.IsReparsePoint(path) && Directory.Exists(path))
            {
                CollectMarkdown(path, output);
            }
            else if (MarkdownPaths.HasMarkdownExtension(path))
            {
                output.Add(path);
            }
        }
    }

    /// <summary>
    /// <c>true</c> if <paramref name="directory"/> itself, or any directory
    /// strictly BETWEEN it and <paramref name="bundleRoot"/> (exclusive of
    /// <paramref name="bundleRoot"/> itself), is a filesystem reparse point
    /// (symlink, junction, mount point) -- checked via
    /// <see cref="ReparsePoints.IsReparsePoint"/>, the same lstat-like
    /// primitive <see cref="CollectMarkdown"/>'s early skip uses, so the
    /// early and late checks can never diverge on what counts as a reparse
    /// point.
    ///
    /// <paramref name="bundleRoot"/> is deliberately never inspected, even
    /// when <paramref name="directory"/> equals it: pointing <c>okf index</c>
    /// at a symlinked/mounted bundle root is a legitimate, common setup
    /// (symlinked project directories, container/WSL bind mounts, macOS's
    /// <c>/var</c>), and <see cref="DirectoriesToIndex"/>'s own early
    /// traversal already includes and indexes <paramref name="bundleRoot"/>
    /// unconditionally -- it never checks the walk's own starting root for
    /// being a reparse point either. Treating the root as inclusive would
    /// silently suppress every single index write for such a bundle. This
    /// mirrors the sibling <c>OkfBundleTools.HasReparsePointAncestor</c>
    /// (src/OKF4net.Agents/OkfBundleTools.cs), which stops its walk via
    /// <c>while (!Equals(current, fullRoot))</c> -- the equality-to-root
    /// check gates entry to the loop body, so the root itself is never
    /// passed to <see cref="ReparsePoints.IsReparsePoint"/>.
    ///
    /// Used only by <see cref="RegenerateIndexesWith"/>'s late, best-effort
    /// re-check immediately before each <c>index.md</c> write -- see that
    /// method's <c>&lt;remarks&gt;</c> for why a directory that already
    /// passed <see cref="DirectoriesToIndex"/>'s early skip still needs this
    /// second look.
    /// </summary>
    private static bool HasReparsePointAncestor(string bundleRoot, string directory)
    {
        var fullRoot = Path.GetFullPath(bundleRoot);
        var current = Path.GetFullPath(directory);

        while (!string.Equals(current, fullRoot, StringComparison.Ordinal))
        {
            if (ReparsePoints.IsReparsePoint(current))
            {
                return true;
            }

            var parent = Path.GetDirectoryName(current);
            if (string.IsNullOrEmpty(parent) || string.Equals(parent, current, StringComparison.Ordinal))
            {
                // Walked past the filesystem root without ever reaching
                // bundleRoot -- DirectoriesToIndex only ever yields
                // descendants of bundleRoot, so this should not happen in
                // practice; stop here rather than loop forever.
                return false;
            }

            current = parent;
        }

        return false;
    }

}

/// <summary>
/// One row in a generated index, mirroring the reference's <c>(type, title,
/// relative_link, description)</c> tuple. Port of Rust's <c>IndexEntry</c>
/// (index.rs:22-31).
/// </summary>
/// <param name="Type">The concept type, or <c>"Subdirectories"</c> for a child directory.</param>
/// <param name="Title">Display title.</param>
/// <param name="Link">Relative link target.</param>
/// <param name="Description">One-line description (may be empty).</param>
public sealed record IndexEntry(string Type, string Title, string Link, string Description);
