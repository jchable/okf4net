# Interactive viewer — P1 (explorer, palette, contents) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give every page `okf-render` generates a tree explorer, a "Jump to" palette, a theme toggle and a right-hand context panel (contents list, heading anchors, author fragments, backlinks), fed by a generated `assets/okf-index.js`, with no change to the OKF4net core API and no new dependency.

**Architecture:** A new pure projection (`SiteIndex.Build`) turns a `Bundle` into a `ViewerIndex`; `IndexScript.Render` serializes it as a classic script that assigns `window.OKF_INDEX`; `HtmlWriter` writes it beside five new classic scripts and lays every page out in three zones. The scripts (`okf-theme.js` in `<head>`; `okf-site.js`, `okf-explorer.js`, `okf-palette.js`, `okf-toc.js` after `viewer.js`) read the index through one guarded accessor and touch the DOM through `textContent` and fixed-name attributes only. `viewer.js` is not modified. The JS guard is the existing Node/jsdom harness, extended to load pages of a site the real `okf-render` generates from a hostile fixture bundle.

**Tech Stack:** C# 14 / .NET 10, xunit, BCL only (`OKF4net.Viewer`); plain ES2018 classic scripts; Node 22 + jsdom 29 (existing `tools/viewer-security-check/`).

**Spec:** `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md` (revision 4). This plan implements its slice **P1** (§9): §2, §2.1, §3 (all), §4.1, §4.4, §4.6, §4.7, §5, §6, §7 (controls 3–8 and the xunit list), §8 (palette, shortcuts, explorer, theme). P2 (local graph) and P3 (global graph, simulation, facets, controls 1, 2 and 9) are out of scope and get their own plans.

## Revision history

- **r1 (2026-10-06)** — first version, commit `244359d`.
- **r2 (2026-10-06)** — after an external review that applied the plan's blocks in a throwaway copy (verdict "executable after named corrections"). Every finding was checked before being taken:
  - Task 5: jsdom 29.1.1 exports no `ResourceLoader` (`Class extends value undefined`); the harness serves the site through `resources.interceptors` + `requestInterceptor`, never reaching the network, and gains `opts.override`.
  - Task 3: the existing `Write_keeps_a_script_closing_tag_in_a_body_inside_the_payload` counted 3 `</script>`; the shell emits 9 — new Step 7 updates it.
  - Task 5: errors raised by handlers after load were never checked (a mutation left the run green); every page a case opens is now checked when the case returns, then closed; Task 6 Step 8 proves it with that mutation.
  - Task 7: the dialog ignores keys during IME composition; the active option is scrolled into view; **no result cap** (owner decision) — the 50-result limit is gone, with a 60-match test.
  - Task 8: a click on `#usage` becomes a real fragment navigation to the generated id (URL, history, Back/Forward stay native); modified clicks are left to the browser.
  - Task 9: the AOT check executes the native binary's `okf-index.js` with `check-index.js` instead of a prefix test.
  - Task 6: the theme button re-announces its state when the system preference changes.
  - Task 4: **no size threshold** (owner decision) — the measurement is recorded, the owner judges it.

## Global Constraints

- Work in the worktree `.claude/worktrees/viewer-interactive-spec`, on a new branch `feat/viewer-interactive-p1` created from `docs/viewer-interactive-spec` (Task 1, Step 0). Stage files by name; never `git add -A`.
- **No change to any public API of `src/OKF4net/`** (spec §2.1). If a task seems to need one, stop and ask: it is a spec change.
- **`viewer.js` is not modified** (spec §4.1: the VS Code extension reuses its `{ body, links }` contract).
- No new `PackageReference` in any project; no new JS dependency (the harness keeps `jsdom` only).
- New source files (C# and JS) start with `// SPDX-License-Identifier: LGPL-3.0-or-later`. File-scoped namespaces, XML doc comments on public API, nullable enabled; `TreatWarningsAsErrors` is on. `dotnet format OKF4net.sln --verify-no-changes` must pass.
- Classic scripts only, never `type="module"` (blocked under `file://`).
- Bundle text reaches the DOM through `textContent`, or `setAttribute` on a fixed attribute name; never `innerHTML`, never a markup string.
- No object keyed by a bundle string (id, title, tag) in the index or in JS: arrays, fixed-key records, `Map`.
- One comparator for everything ordered: `ConceptId.CompareTo` (C# sorts; JS breaks ties by index position).
- Staleness: the index carries `staleAfterMs` (deadline rounded **up** to a whole millisecond) and `staleAfterDate` (`Lifecycle.StaleAfterDate`); the browser only compares `Date.now() >= staleAfterMs`, on load and on `visibilitychange` to `visible`.
- Never type a `\uXXXX` escape into a file through an editor tool: it is decoded on write. Build such characters at run time (`(char)0x2028` in C#, `String.fromCharCode(0x2028)` in JS).
- Commit messages end with the attribution trailer the session's system reminder specifies.

## Review Focus

1. **A large bundle in a real browser** (≈800 concepts, the measured OKF4net bundle): pages must open without a visible stall and the explorer must stay usable — jsdom cannot measure this; Task 4 measures the index size and Task 9's acceptance checklist covers opening speed in Chrome, Edge and Firefox.
2. **A page nested several levels deep** (`a/b/c/d.html`): every explorer and palette link must resolve from the site root, not from the page's directory — pinned by Task 3's `A_deeply_nested_page_declares_one_parent_step_per_level` and Task 6's `../foo.html` assertion.
3. **An empty bundle** (zero concepts): `okf-render` must still write a valid `okf-index.js`, and the explorer and palette must render empty without errors — pinned by Task 2's `An_empty_bundle_renders_the_empty_index`.
4. **Phone width** (390 px): the three zones must stack without horizontal scrolling and the palette must fit — CSS only, covered by Task 9's acceptance checklist.
5. **Back/forward through fragments**: returning to `page.html#usage` must land on the generated heading again — Task 8 handles `hashchange`; the checklist exercises the browser buttons.

---

### Task 1: Site index model and projection

**Files:**
- Create: `src/OKF4net.Viewer/ViewerIndex.cs`
- Create: `src/OKF4net.Viewer/SiteIndex.cs`
- Modify: `src/OKF4net.Viewer/SiteModel.cs` (title and page-path helpers, `Index` populated)
- Modify: `src/OKF4net.Viewer/ViewerModel.cs` (`ViewerSite.Index`)
- Test: `tests/OKF4net.Tests/Viewer/SiteIndexTests.cs`

**Interfaces:**
- Consumes: `Bundle.Concepts`, `Bundle.LinksFrom`, `ConceptAudit.Run(Bundle, AuditQuery, IOkfClock?)`, `AuditFinding.Trust`/`.Lifecycle`, `Lifecycle.StaleAfter`/`.StaleAfterDate`, `AuditVocabulary.Name(TrustTier)`, `FixedClock`, `ConceptId.CompareTo`.
- Produces:
  - `public sealed record IndexConcept(ConceptId Id, string Title, string Type, IReadOnlyList<string> Tags, string Path, string Trust, long? StaleAfterMs, string? StaleAfterDate)`
  - `public sealed record IndexGhost(ConceptId Id)`
  - `public sealed record IndexEdge(int From, int To, int Count, bool ToGhost)`
  - `public sealed record IndexTreeNode(string Name, int Concept, IReadOnlyList<IndexTreeNode> Children)`
  - `public sealed record ViewerIndex(IReadOnlyList<IndexConcept> Concepts, IReadOnlyList<IndexGhost> Ghosts, IReadOnlyList<IndexEdge> Edges, IReadOnlyList<IndexTreeNode> Tree)` with `public static ViewerIndex Empty`
  - `public static class SiteIndex { public static ViewerIndex Build(Bundle bundle); }`
  - `ViewerSite.Index` (`ViewerIndex`, init-only, defaults to `ViewerIndex.Empty`)
  - `internal static string SiteModel.DisplayTitle(Concept)`, `internal static string SiteModel.PagePath(ConceptId)`

- [ ] **Step 0: Create the implementation branch**

```bash
cd E:/Sources/okf/.claude/worktrees/viewer-interactive-spec
git switch -c feat/viewer-interactive-p1
```

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Viewer/SiteIndexTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>Tests for the site index projection behind <c>assets/okf-index.js</c> (spec §3).</summary>
public class SiteIndexTests
{
    private static string Concept(string title, string extra = "", string body = "")
        => $"---\ntype: Note\ntitle: {title}\ndescription: d\n{extra}---\n{body}";

    private static ViewerIndex Build(TempDir tmp) => SiteIndex.Build(Bundle.Load(tmp.Path));

    [Fact]
    public void Concepts_are_ordered_by_ConceptId_CompareTo()
    {
        using var tmp = new TempDir();
        tmp.Write("a-b.md", Concept("AB"));
        tmp.Write("a/b.md", Concept("A slash B"));
        tmp.Write("a.md", Concept("A"));

        // Segment-wise ordinal: "a/b" sorts before "a-b", which a comparison
        // of the whole strings would reverse ('/' 0x2F > '-' 0x2D).
        Assert.Equal(new[] { "a", "a/b", "a-b" }, Build(tmp).Concepts.Select(c => c.Id.ToString()));
    }

    [Fact]
    public void A_concept_that_is_also_a_folder_is_one_node_with_a_destination_and_children()
    {
        using var tmp = new TempDir();
        tmp.Write("foo.md", Concept("Foo"));
        tmp.Write("foo/bar.md", Concept("Bar"));
        var index = Build(tmp);

        var foo = Assert.Single(index.Tree);
        Assert.Equal("foo", foo.Name);
        Assert.Equal("foo", index.Concepts[foo.Concept].Id.ToString());
        var bar = Assert.Single(foo.Children);
        Assert.Equal("bar", bar.Name);
        Assert.Equal("foo/bar", index.Concepts[bar.Concept].Id.ToString());
        Assert.Empty(bar.Children);
    }

    [Fact]
    public void A_folder_without_its_own_concept_has_no_destination()
    {
        using var tmp = new TempDir();
        tmp.Write("tables/users.md", Concept("Users"));

        var tables = Assert.Single(Build(tmp).Tree);
        Assert.Equal(-1, tables.Concept);
        Assert.Single(tables.Children);
    }

    [Theory]
    [InlineData("", "unverified")]
    [InlineData("verified:\n  - { by: \"human:jsmith\", at: \"2026-07-01T09:00:00Z\" }\n", "human-reviewed")]
    [InlineData("verified:\n  - { by: \"tool:okf-ci\", at: \"2026-07-01T09:00:00Z\" }\n", "machine-confirmed")]
    public void Trust_is_the_ConceptAudit_tier_under_its_vocabulary_name(string extra, string expected)
    {
        using var tmp = new TempDir();
        tmp.Write("c.md", Concept("C", extra));

        Assert.Equal(expected, Assert.Single(Build(tmp).Concepts).Trust);
    }

    [Theory]
    [InlineData("2026-10-06T00:00:00Z", 0L, "2026-10-06")]
    [InlineData("2026-10-06T00:00:00.0001000Z", 1L, "2026-10-06")]          // rounded UP to the next whole ms
    [InlineData("2026-10-06T02:00:00+02:00", 0L, "2026-10-06")]             // offset normalized to UTC
    [InlineData("2026-10-06", 0L, "2026-10-06")]                            // legacy date-only: midnight UTC
    [InlineData("2026-10-06T23:59:59.9999Z", 86_400_000L, "2026-10-06")]    // ms tips into the next day, the date does not
    public void StaleAfter_is_exported_as_ceiling_milliseconds_and_the_Lifecycle_date(string raw, long offsetMs, string date)
    {
        using var tmp = new TempDir();
        tmp.Write("c.md", Concept("C", $"stale_after: \"{raw}\"\n"));
        var concept = Assert.Single(Build(tmp).Concepts);

        var midnight = new DateTimeOffset(2026, 10, 6, 0, 0, 0, TimeSpan.Zero).ToUnixTimeMilliseconds();
        Assert.Equal(midnight + offsetMs, concept.StaleAfterMs);
        Assert.Equal(date, concept.StaleAfterDate);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("soon")]
    public void An_absent_or_malformed_stale_after_exports_nulls(string? raw)
    {
        using var tmp = new TempDir();
        tmp.Write("c.md", Concept("C", raw is null ? "" : $"stale_after: \"{raw}\"\n"));
        var concept = Assert.Single(Build(tmp).Concepts);

        Assert.Null(concept.StaleAfterMs);
        Assert.Null(concept.StaleAfterDate);
    }

    [Fact]
    public void Repeated_links_merge_into_one_edge_and_absent_targets_become_sorted_ghosts()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", Concept("A", body: "[x](b.md) [y](b.md) [z](zz/gone.md) [w](missing.md)\n"));
        tmp.Write("b.md", Concept("B"));
        var index = Build(tmp);

        Assert.Equal(new[] { "missing", "zz/gone" }, index.Ghosts.Select(g => g.Id.ToString()));
        Assert.Equal(
            new[] { new IndexEdge(0, 1, 2, false), new IndexEdge(0, 0, 1, true), new IndexEdge(0, 1, 1, true) },
            index.Edges);
    }

    [Fact]
    public void Ids_that_name_Object_prototype_members_are_ordinary_concepts()
    {
        using var tmp = new TempDir();
        tmp.Write("__proto__.md", Concept("Proto"));
        tmp.Write("constructor.md", Concept("Constructor"));
        tmp.Write("toString.md", Concept("ToString"));

        Assert.Equal(
            new[] { "__proto__", "constructor", "toString" },
            Build(tmp).Concepts.Select(c => c.Id.ToString()));
    }

    [Fact]
    public void Title_falls_back_to_the_id_and_path_is_the_page_path()
    {
        using var tmp = new TempDir();
        tmp.Write("tables/users.md", "---\ntype: Note\ndescription: d\ntags: [core, users]\n---\n");
        var concept = Assert.Single(Build(tmp).Concepts);

        Assert.Equal("tables/users", concept.Title);
        Assert.Equal("tables/users.html", concept.Path);
        Assert.Equal("Note", concept.Type);
        Assert.Equal(new[] { "core", "users" }, concept.Tags);
    }

    [Fact]
    public void SiteModel_Build_populates_the_index()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", Concept("A"));

        var site = SiteModel.Build(Bundle.Load(tmp.Path));

        Assert.Equal("a", Assert.Single(site.Index.Concepts).Id.ToString());
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~SiteIndexTests"`
Expected: build FAILS with `CS0246: The type or namespace name 'ViewerIndex' could not be found` (and `SiteIndex`).

- [ ] **Step 3: Create the index model**

Create `src/OKF4net.Viewer/ViewerIndex.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Viewer;

/// <summary>One concept in the site index (<c>assets/okf-index.js</c>).</summary>
/// <param name="Id">The concept id (§2).</param>
/// <param name="Title">The display title: the frontmatter <c>title</c>, else the id.</param>
/// <param name="Type">The frontmatter <c>type</c>, or empty when absent.</param>
/// <param name="Tags">The frontmatter <c>tags</c>, in document order.</param>
/// <param name="Path">The page path relative to the site root, e.g. <c>tables/users.html</c>.</param>
/// <param name="Trust">The §5.3 trust tier, under its <see cref="AuditVocabulary"/> name.</param>
/// <param name="StaleAfterMs">
/// The §5.5 deadline in milliseconds since the Unix epoch, rounded UP to a
/// whole millisecond, so that <c>Date.now() &gt;= StaleAfterMs</c> in the
/// browser equals <see cref="Lifecycle.IsStale"/>; null when absent or malformed.
/// </param>
/// <param name="StaleAfterDate">
/// <see cref="Lifecycle.StaleAfterDate"/> as <c>yyyy-MM-dd</c>, for display only;
/// never derived from <paramref name="StaleAfterMs"/>, which can tip into the next day.
/// </param>
public sealed record IndexConcept(
    ConceptId Id,
    string Title,
    string Type,
    IReadOnlyList<string> Tags,
    string Path,
    string Trust,
    long? StaleAfterMs,
    string? StaleAfterDate);

/// <summary>A link target that is not a concept of the bundle (a §6.1 broken link). Never navigable.</summary>
/// <param name="Id">The absent target's id.</param>
public sealed record IndexGhost(ConceptId Id);

/// <summary>All body links from one concept to one target, merged.</summary>
/// <param name="From">Position of the source in <see cref="ViewerIndex.Concepts"/>.</param>
/// <param name="To">Position of the target in <see cref="ViewerIndex.Concepts"/>, or in <see cref="ViewerIndex.Ghosts"/> when <paramref name="ToGhost"/>.</param>
/// <param name="Count">How many links in the source's body point at that target.</param>
/// <param name="ToGhost">Whether the target is absent from the bundle.</param>
public sealed record IndexEdge(int From, int To, int Count, bool ToGhost);

/// <summary>
/// One segment of the id tree. A node can be a destination (a concept whose
/// id is this path), a folder (children), or both: <c>foo.md</c> and
/// <c>foo/bar.md</c> coexist legally.
/// </summary>
/// <param name="Name">The segment.</param>
/// <param name="Concept">Position in <see cref="ViewerIndex.Concepts"/> of the concept at this path, or -1.</param>
/// <param name="Children">Child nodes, ordered by segment (ordinal).</param>
public sealed record IndexTreeNode(string Name, int Concept, IReadOnlyList<IndexTreeNode> Children);

/// <summary>The site index the interactive viewer scripts read.</summary>
/// <param name="Concepts">Every concept, ordered by <see cref="ConceptId.CompareTo(ConceptId)"/>.</param>
/// <param name="Ghosts">Every absent link target, ordered the same way.</param>
/// <param name="Edges">Merged body links, by source then target.</param>
/// <param name="Tree">Top-level tree nodes.</param>
public sealed record ViewerIndex(
    IReadOnlyList<IndexConcept> Concepts,
    IReadOnlyList<IndexGhost> Ghosts,
    IReadOnlyList<IndexEdge> Edges,
    IReadOnlyList<IndexTreeNode> Tree)
{
    /// <summary>An index with nothing in it, for a site built by hand.</summary>
    public static ViewerIndex Empty { get; } = new([], [], [], []);
}
```

- [ ] **Step 4: Add the shared helpers to `SiteModel` and the `Index` property to `ViewerSite`**

In `src/OKF4net.Viewer/SiteModel.cs`, replace the `title` computation and the page path in `BuildPage`:

```csharp
        var title = string.IsNullOrWhiteSpace(frontmatter.Title)
            ? concept.Id.ToString()
            : frontmatter.Title;
```

with

```csharp
        var title = DisplayTitle(concept);
```

and in the `return new ViewerPage(` call replace `concept.Id.ToString() + ".html",` with `PagePath(concept.Id),`. Add these two methods to the class (after `BuildPage`):

```csharp
    /// <summary>The display title: the frontmatter <c>title</c>, else the concept id.</summary>
    internal static string DisplayTitle(Concept concept)
    {
        var title = concept.Document.Frontmatter.Title;
        return string.IsNullOrWhiteSpace(title) ? concept.Id.ToString() : title;
    }

    /// <summary>The generated page's path relative to the site root.</summary>
    internal static string PagePath(ConceptId id) => id + ".html";
```

In `Build`, replace

```csharp
        return new ViewerSite(
            bundle.Root,
            pages,
            IndexGenerator.BuildIndexText(entries),
            bundle.ParseErrors.Select(e => new ViewerParseError(e.Path, e.Error)).ToList());
```

with

```csharp
        return new ViewerSite(
            bundle.Root,
            pages,
            IndexGenerator.BuildIndexText(entries),
            bundle.ParseErrors.Select(e => new ViewerParseError(e.Path, e.Error)).ToList())
        {
            Index = SiteIndex.Build(bundle),
        };
```

In `src/OKF4net.Viewer/ViewerModel.cs`, give `ViewerSite` a body:

```csharp
public sealed record ViewerSite(
    string BundleRoot,
    IReadOnlyList<ViewerPage> Pages,
    string IndexMarkdown,
    IReadOnlyList<ViewerParseError> ParseErrors)
{
    /// <summary>
    /// The site index written to <c>assets/okf-index.js</c>.
    /// <see cref="ViewerIndex.Empty"/> for a site built by hand.
    /// </summary>
    public ViewerIndex Index { get; init; } = ViewerIndex.Empty;
}
```

- [ ] **Step 5: Write the projection**

Create `src/OKF4net.Viewer/SiteIndex.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;

namespace OKF4net.Viewer;

/// <summary>
/// Projects a loaded <see cref="Bundle"/> into the <see cref="ViewerIndex"/>
/// behind <c>assets/okf-index.js</c>. Pure: no I/O, and no clock is read.
/// </summary>
public static class SiteIndex
{
    /// <summary>Builds the site index of <paramref name="bundle"/>.</summary>
    /// <param name="bundle">The loaded bundle.</param>
    public static ViewerIndex Build(Bundle bundle)
    {
        // One comparator for everything ordered in the index (spec §3.1).
        var ordered = bundle.Concepts.OrderBy(c => c.Id, Comparer<ConceptId>.Default).ToList();

        // Only the trust tier and the parsed stale_after are read from the
        // audit. Staleness itself is evaluated in the browser (spec §4.4), so
        // the clock is pinned: the projection stays pure.
        var findings = ConceptAudit.Run(bundle, default, new FixedClock(DateTimeOffset.UnixEpoch))
            .Findings
            .ToDictionary(f => f.Id.ToString(), StringComparer.Ordinal);

        var position = new Dictionary<string, int>(StringComparer.Ordinal);
        var concepts = new List<IndexConcept>(ordered.Count);
        foreach (var concept in ordered)
        {
            var id = concept.Id.ToString();
            var finding = findings[id];
            var frontmatter = concept.Document.Frontmatter;
            position[id] = concepts.Count;
            concepts.Add(new IndexConcept(
                concept.Id,
                SiteModel.DisplayTitle(concept),
                frontmatter.Type ?? string.Empty,
                frontmatter.Tags,
                SiteModel.PagePath(concept.Id),
                AuditVocabulary.Name(finding.Trust),
                CeilingMilliseconds(finding.Lifecycle.StaleAfter),
                finding.Lifecycle.StaleAfterDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }

        var absent = new SortedSet<ConceptId>(Comparer<ConceptId>.Default);
        foreach (var concept in ordered)
        {
            foreach (var link in bundle.LinksFrom(concept.Id))
            {
                if (!link.Exists)
                {
                    absent.Add(link.Target);
                }
            }
        }

        var ghosts = absent.Select(id => new IndexGhost(id)).ToList();
        var ghostPosition = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < ghosts.Count; i++)
        {
            ghostPosition[ghosts[i].Id.ToString()] = i;
        }

        var edges = new List<IndexEdge>();
        for (var from = 0; from < ordered.Count; from++)
        {
            // Bundle.BuildGraph keeps every occurrence; repeated (source,
            // target) pairs merge into one edge with a count (spec §3.3).
            // Concept targets sort before ghosts, each by position.
            var counts = new SortedDictionary<(bool ToGhost, int To), int>();
            foreach (var link in bundle.LinksFrom(ordered[from].Id))
            {
                var target = link.Target.ToString();
                var key = link.Exists ? (false, position[target]) : (true, ghostPosition[target]);
                counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;
            }

            foreach (var (key, count) in counts)
            {
                edges.Add(new IndexEdge(from, key.To, count, key.ToGhost));
            }
        }

        return new ViewerIndex(concepts, ghosts, edges, BuildTree(concepts));
    }

    /// <summary>
    /// <paramref name="instant"/> as milliseconds since the Unix epoch,
    /// rounded UP: <c>Date.now()</c> is a whole number of milliseconds, so
    /// <c>now &gt;= ceiling(deadline)</c> holds exactly when
    /// <c>now &gt;= deadline</c> (spec §4.4).
    /// </summary>
    private static long? CeilingMilliseconds(DateTimeOffset? instant)
    {
        if (instant is not { } value)
        {
            return null;
        }

        var ticks = value.UtcTicks - DateTimeOffset.UnixEpoch.UtcTicks;
        var milliseconds = ticks / TimeSpan.TicksPerMillisecond;
        if (ticks % TimeSpan.TicksPerMillisecond > 0)
        {
            milliseconds++;
        }

        return milliseconds;
    }

    private static List<IndexTreeNode> BuildTree(List<IndexConcept> concepts)
    {
        var root = new TreeBuilder(string.Empty);
        for (var i = 0; i < concepts.Count; i++)
        {
            var node = root;
            foreach (var segment in concepts[i].Id.Segments)
            {
                node = node.Child(segment);
            }

            node.Concept = i;
        }

        return root.Freeze();
    }

    private sealed class TreeBuilder(string name)
    {
        private readonly SortedDictionary<string, TreeBuilder> _children = new(StringComparer.Ordinal);

        public string Name { get; } = name;

        public int Concept { get; set; } = -1;

        public TreeBuilder Child(string segment)
        {
            if (!_children.TryGetValue(segment, out var child))
            {
                child = new TreeBuilder(segment);
                _children.Add(segment, child);
            }

            return child;
        }

        public List<IndexTreeNode> Freeze()
            => _children.Values.Select(c => new IndexTreeNode(c.Name, c.Concept, c.Freeze())).ToList();
    }
}
```

- [ ] **Step 6: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Viewer"`
Expected: PASS (the new `SiteIndexTests` and every existing viewer test).

- [ ] **Step 7: Format and commit**

```bash
dotnet format OKF4net.sln --verify-no-changes
git add src/OKF4net.Viewer/ViewerIndex.cs src/OKF4net.Viewer/SiteIndex.cs src/OKF4net.Viewer/SiteModel.cs src/OKF4net.Viewer/ViewerModel.cs tests/OKF4net.Tests/Viewer/SiteIndexTests.cs
git commit -m "feat(viewer): site index projection for the interactive viewer"
```

---

### Task 2: Index script serialization

**Files:**
- Create: `src/OKF4net.Viewer/IndexScript.cs`
- Test: `tests/OKF4net.Tests/Viewer/IndexScriptTests.cs`

**Interfaces:**
- Consumes: `ViewerIndex` and its records (Task 1), `HtmlSafeJson.Quote(string)`.
- Produces: `public static class IndexScript { public const string Prefix = "window.OKF_INDEX = "; public static string Render(ViewerIndex index); }`. Output shape (fixed keys only): `window.OKF_INDEX = {"version":1,"concepts":[{"id":…,"title":…,"type":…,"tags":[…],"path":…,"trust":…,"staleAfterMs":<number|null>,"staleAfterDate":<string|null>}],"ghosts":[{"id":…}],"edges":[[from,to,count,0|1]],"tree":[{"name":…,"concept":<int>,"children":[…]}]};\n`

- [ ] **Step 1: Write the failing tests**

Create `tests/OKF4net.Tests/Viewer/IndexScriptTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>
/// Tests for <c>assets/okf-index.js</c> serialization (spec §3.4). These
/// prove the text round-trips as JSON; they do not prove what the script
/// creates when a browser EXECUTES it -- the jsdom harness
/// (tools/viewer-security-check/) runs a generated index for that.
/// </summary>
public class IndexScriptTests
{
    private const string EmptyScript =
        "window.OKF_INDEX = {\"version\":1,\"concepts\":[],\"ghosts\":[],\"edges\":[],\"tree\":[]};\n";

    private static JsonElement Parse(string script)
    {
        Assert.StartsWith(IndexScript.Prefix, script);
        Assert.EndsWith(";\n", script);
        var json = script[IndexScript.Prefix.Length..^2];
        return JsonDocument.Parse(json).RootElement.Clone();
    }

    [Fact]
    public void The_empty_index_is_a_classic_script_assigning_window_OKF_INDEX()
        => Assert.Equal(EmptyScript, IndexScript.Render(ViewerIndex.Empty));

    [Fact]
    public void An_empty_bundle_renders_the_empty_index()
    {
        using var tmp = new TempDir();

        Assert.Equal(EmptyScript, IndexScript.Render(SiteIndex.Build(Bundle.Load(tmp.Path))));
    }

    [Fact]
    public void Hostile_text_round_trips_and_cannot_close_a_script_or_break_a_line()
    {
        // Built at run time: a \u2028 typed into this file would be a raw
        // line separator inside a C# string literal.
        var lineSeparator = ((char)0x2028).ToString();
        var paragraphSeparator = ((char)0x2029).ToString();
        var hostile = "</script><img src=x onerror=alert(1)>" + lineSeparator + paragraphSeparator + "\"\\";
        var concept = new IndexConcept(ConceptId.Parse("a"), hostile, hostile, [hostile], "a.html", "unverified", null, null);
        var index = new ViewerIndex([concept], [], [], [new IndexTreeNode(hostile, 0, [])]);

        var script = IndexScript.Render(index);

        Assert.DoesNotContain("<", script);
        Assert.DoesNotContain(lineSeparator, script);
        Assert.DoesNotContain(paragraphSeparator, script);
        var root = Parse(script);
        var parsed = root.GetProperty("concepts")[0];
        Assert.Equal(hostile, parsed.GetProperty("title").GetString());
        Assert.Equal(hostile, parsed.GetProperty("type").GetString());
        Assert.Equal(hostile, parsed.GetProperty("tags")[0].GetString());
        Assert.Equal(hostile, root.GetProperty("tree")[0].GetProperty("name").GetString());
    }

    [Fact]
    public void Stale_fields_render_as_a_number_and_a_date_or_as_null()
    {
        var dated = new IndexConcept(ConceptId.Parse("a"), "A", "Note", [], "a.html", "unverified", 1759708800001L, "2026-10-06");
        var undated = new IndexConcept(ConceptId.Parse("b"), "B", "Note", [], "b.html", "unverified", null, null);

        var script = IndexScript.Render(new ViewerIndex([dated, undated], [], [], []));

        Assert.Contains("\"staleAfterMs\":1759708800001,\"staleAfterDate\":\"2026-10-06\"", script);
        Assert.Contains("\"staleAfterMs\":null,\"staleAfterDate\":null", script);
    }

    [Fact]
    public void Edges_render_as_quadruples_and_ghosts_as_ids()
    {
        var a = new IndexConcept(ConceptId.Parse("a"), "A", "Note", [], "a.html", "unverified", null, null);
        var b = new IndexConcept(ConceptId.Parse("b"), "B", "Note", [], "b.html", "unverified", null, null);
        var index = new ViewerIndex(
            [a, b],
            [new IndexGhost(ConceptId.Parse("gone"))],
            [new IndexEdge(0, 1, 2, false), new IndexEdge(0, 0, 1, true)],
            []);

        var script = IndexScript.Render(index);

        Assert.Contains("\"ghosts\":[{\"id\":\"gone\"}],\"edges\":[[0,1,2,0],[0,0,1,1]]", script);
    }

    [Fact]
    public void The_tree_renders_nested_nodes_with_fixed_keys()
    {
        var index = new ViewerIndex([], [], [], [new IndexTreeNode("foo", 0, [new IndexTreeNode("bar", 1, [])])]);

        Assert.Contains(
            "\"tree\":[{\"name\":\"foo\",\"concept\":0,\"children\":[{\"name\":\"bar\",\"concept\":1,\"children\":[]}]}]",
            IndexScript.Render(index));
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~IndexScriptTests"`
Expected: build FAILS with `CS0103: The name 'IndexScript' does not exist in the current context`.

- [ ] **Step 3: Write the serializer**

Create `src/OKF4net.Viewer/IndexScript.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace OKF4net.Viewer;

/// <summary>
/// Serializes a <see cref="ViewerIndex"/> as the classic script
/// <c>assets/okf-index.js</c>, which assigns <c>window.OKF_INDEX</c>.
/// </summary>
/// <remarks>
/// Every bundle-derived string goes through <see cref="HtmlSafeJson.Quote"/>
/// (hand-built rather than <c>System.Text.Json</c> by reflection, which
/// would fail under Native AOT). The object has fixed keys only -- arrays and
/// fixed-key records, never a dictionary keyed by an id or a title -- because
/// <c>__proto__</c>, <c>constructor</c> and <c>toString</c> are valid concept
/// ids, and an object literal keyed by one of them does not mean what
/// <c>JSON.parse</c> of the same text means (spec §3.4).
/// </remarks>
public static class IndexScript
{
    /// <summary>The fixed text every index script starts with.</summary>
    public const string Prefix = "window.OKF_INDEX = ";

    /// <summary>Renders <paramref name="index"/> as a complete script, ending with <c>;\n</c>.</summary>
    /// <param name="index">The site index.</param>
    public static string Render(ViewerIndex index)
    {
        var sb = new StringBuilder(Prefix);
        sb.Append("{\"version\":1,\"concepts\":[");
        for (var i = 0; i < index.Concepts.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var c = index.Concepts[i];
            sb.Append("{\"id\":").Append(HtmlSafeJson.Quote(c.Id.ToString()))
              .Append(",\"title\":").Append(HtmlSafeJson.Quote(c.Title))
              .Append(",\"type\":").Append(HtmlSafeJson.Quote(c.Type))
              .Append(",\"tags\":[");
            for (var t = 0; t < c.Tags.Count; t++)
            {
                if (t > 0)
                {
                    sb.Append(',');
                }

                sb.Append(HtmlSafeJson.Quote(c.Tags[t]));
            }

            sb.Append("],\"path\":").Append(HtmlSafeJson.Quote(c.Path))
              .Append(",\"trust\":").Append(HtmlSafeJson.Quote(c.Trust))
              .Append(",\"staleAfterMs\":")
              .Append(c.StaleAfterMs is { } ms ? ms.ToString(CultureInfo.InvariantCulture) : "null")
              .Append(",\"staleAfterDate\":")
              .Append(c.StaleAfterDate is { } date ? HtmlSafeJson.Quote(date) : "null")
              .Append('}');
        }

        sb.Append("],\"ghosts\":[");
        for (var i = 0; i < index.Ghosts.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"id\":").Append(HtmlSafeJson.Quote(index.Ghosts[i].Id.ToString())).Append('}');
        }

        sb.Append("],\"edges\":[");
        for (var i = 0; i < index.Edges.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            var e = index.Edges[i];
            sb.Append('[')
              .Append(e.From.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.To.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.ToGhost ? '1' : '0')
              .Append(']');
        }

        sb.Append("],\"tree\":");
        AppendNodes(sb, index.Tree);
        sb.Append("};\n");
        return sb.ToString();
    }

    private static void AppendNodes(StringBuilder sb, IReadOnlyList<IndexTreeNode> nodes)
    {
        sb.Append('[');
        for (var i = 0; i < nodes.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append("{\"name\":").Append(HtmlSafeJson.Quote(nodes[i].Name))
              .Append(",\"concept\":").Append(nodes[i].Concept.ToString(CultureInfo.InvariantCulture))
              .Append(",\"children\":");
            AppendNodes(sb, nodes[i].Children);
            sb.Append('}');
        }

        sb.Append(']');
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~IndexScriptTests"`
Expected: PASS (6 tests).

- [ ] **Step 5: Format and commit**

```bash
dotnet format OKF4net.sln --verify-no-changes
git add src/OKF4net.Viewer/IndexScript.cs tests/OKF4net.Tests/Viewer/IndexScriptTests.cs
git commit -m "feat(viewer): serialize the site index as assets/okf-index.js"
```

---

### Task 3: Page shell, assets and index writing

**Files:**
- Create: `src/OKF4net.Viewer/Assets/okf-theme.js`, `okf-site.js`, `okf-explorer.js`, `okf-palette.js`, `okf-toc.js` (skeletons here; Tasks 5–8 replace each with its implementation)
- Modify: `src/OKF4net.Viewer/ViewerAssets.cs`
- Modify: `src/OKF4net.Viewer/HtmlWriter.cs` (`Write`, `GuardNoCaseCollisions` remarks, `RenderPage`, `RenderIndex`, `RenderBacklinks`, `RenderShell`)
- Test: `tests/OKF4net.Tests/Viewer/HtmlWriterTests.cs`

**Interfaces:**
- Consumes: `ViewerSite.Index` (Task 1), `IndexScript.Render` (Task 2).
- Produces (the DOM contract every script relies on):
  - `<html lang="en" data-okf-root="{../ per level}">`, plus `data-okf-concept="{id}"` on concept pages only.
  - `<head>`: `<script src="{root}assets/okf-theme.js">` **before** the stylesheet link.
  - Header: `<div class="bar-tools" id="okf-tools"></div>` (buttons are added by JS).
  - `<div class="okf-layout">` containing `<nav class="okf-explorer" id="okf-explorer" aria-label="Explorer" hidden>`, `<main>`, `<aside class="okf-context" id="okf-context" aria-label="Page context">` (with ` hidden` when the page has no backlinks). The aside starts with `<section class="okf-toc" id="okf-toc" aria-labelledby="okf-toc-title" hidden><h2 id="okf-toc-title">On this page</h2><ul></ul></section>`, then the backlinks `<section class="okf-backlinks" aria-labelledby="okf-backlinks-title">`.
  - Script order at the end of `<body>`: payload, `marked.min.js`, `viewer.js`, `okf-index.js`, `okf-site.js`, `okf-explorer.js`, `okf-palette.js`, `okf-toc.js`.
  - `ViewerAssets.ThemeJs`, `.SiteJs`, `.ExplorerJs`, `.PaletteJs`, `.TocJs`.

- [ ] **Step 1: Write the failing tests**

Append to `tests/OKF4net.Tests/Viewer/HtmlWriterTests.cs`, inside the class:

```csharp
    private static readonly string[] InteractiveScripts =
        ["okf-theme.js", "okf-site.js", "okf-explorer.js", "okf-palette.js", "okf-toc.js"];

    [Fact]
    public void Write_emits_the_site_index_script()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        var written = HtmlWriter.Write(site, dest.Path);

        var script = File.ReadAllText(Path.Combine(dest.Path, "assets", "okf-index.js"));
        Assert.StartsWith(IndexScript.Prefix, script);
        Assert.Contains("\"id\":\"tables/users\"", script);
        Assert.Contains("assets/okf-index.js", written);
    }

    [Fact]
    public void Write_emits_the_interactive_scripts()
    {
        using var src = new TempDir();
        using var dest = new TempDir();

        HtmlWriter.Write(SiteModel.Build(SampleBundle(src)), dest.Path);

        foreach (var name in InteractiveScripts)
        {
            Assert.True(File.Exists(Path.Combine(dest.Path, "assets", name)), name);
        }
    }

    [Fact]
    public void A_hand_built_site_without_an_index_writes_the_empty_one()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = new ViewerSite(src.Path, [], string.Empty, []);

        HtmlWriter.Write(site, dest.Path);

        Assert.Equal(
            IndexScript.Render(ViewerIndex.Empty),
            File.ReadAllText(Path.Combine(dest.Path, "assets", "okf-index.js")));
    }

    [Fact]
    public void Pages_declare_their_site_root_and_concept()
    {
        using var src = new TempDir();
        using var dest = new TempDir();

        HtmlWriter.Write(SiteModel.Build(SampleBundle(src)), dest.Path);

        var nested = File.ReadAllText(Path.Combine(dest.Path, "tables", "users.html"));
        Assert.Contains("<html lang=\"en\" data-okf-root=\"../\" data-okf-concept=\"tables/users\">", nested);
        var root = File.ReadAllText(Path.Combine(dest.Path, "index.html"));
        Assert.Contains("<html lang=\"en\" data-okf-root=\"\">", root);
    }

    [Fact]
    public void A_deeply_nested_page_declares_one_parent_step_per_level()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("a/b/c/d.md", "---\ntype: Note\ntitle: D\ndescription: d\n---\n");

        HtmlWriter.Write(SiteModel.Build(Bundle.Load(src.Path)), dest.Path);

        var page = File.ReadAllText(Path.Combine(dest.Path, "a", "b", "c", "d.html"));
        Assert.Contains("data-okf-root=\"../../../\"", page);
        Assert.Contains("src=\"../../../assets/okf-index.js\"", page);
    }

    [Fact]
    public void The_theme_script_loads_in_head_before_the_stylesheet()
    {
        using var src = new TempDir();
        using var dest = new TempDir();

        HtmlWriter.Write(SiteModel.Build(SampleBundle(src)), dest.Path);

        var page = File.ReadAllText(Path.Combine(dest.Path, "tables", "users.html"));
        var theme = page.IndexOf("assets/okf-theme.js", StringComparison.Ordinal);
        var css = page.IndexOf("assets/viewer.css", StringComparison.Ordinal);
        var headEnd = page.IndexOf("</head>", StringComparison.Ordinal);
        Assert.True(theme >= 0 && theme < css && css < headEnd, "okf-theme.js must run in <head>, before the stylesheet");
    }

    [Fact]
    public void Interactive_scripts_run_after_viewer_js_in_a_fixed_order()
    {
        using var src = new TempDir();
        using var dest = new TempDir();

        HtmlWriter.Write(SiteModel.Build(SampleBundle(src)), dest.Path);

        var page = File.ReadAllText(Path.Combine(dest.Path, "tables", "users.html"));
        var order = new[] { "assets/viewer.js", "assets/okf-index.js", "assets/okf-site.js", "assets/okf-explorer.js", "assets/okf-palette.js", "assets/okf-toc.js" }
            .Select(s => page.IndexOf(s, StringComparison.Ordinal))
            .ToList();
        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.OrderBy(i => i), order);
    }

    [Fact]
    public void Backlinks_live_in_the_page_context_aside_which_is_hidden_without_them()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("users.md", "---\ntype: Note\ntitle: Users\ndescription: d\n---\nSee [orders](orders.md).\n");
        src.Write("orders.md", "---\ntype: Note\ntitle: Orders\ndescription: d\n---\nNo links.\n");

        HtmlWriter.Write(SiteModel.Build(Bundle.Load(src.Path)), dest.Path);

        var orders = File.ReadAllText(Path.Combine(dest.Path, "orders.html"));
        var aside = orders.IndexOf("<aside class=\"okf-context\" id=\"okf-context\" aria-label=\"Page context\">", StringComparison.Ordinal);
        var backlinks = orders.IndexOf("<h2 id=\"okf-backlinks-title\">Referenced by</h2>", StringComparison.Ordinal);
        Assert.True(aside >= 0 && backlinks > aside, "backlinks must be rendered inside the visible aside");
        var users = File.ReadAllText(Path.Combine(dest.Path, "users.html"));
        Assert.Contains("aria-label=\"Page context\" hidden>", users);
    }
```

- [ ] **Step 2: Run the tests to verify they fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~HtmlWriterTests"`
Expected: the eight new tests FAIL (no `okf-index.js`, no `data-okf-root`, no aside); existing tests PASS.

- [ ] **Step 3: Create the five script skeletons**

Each of `src/OKF4net.Viewer/Assets/okf-theme.js`, `okf-site.js`, `okf-explorer.js`, `okf-palette.js`, `okf-toc.js` gets exactly this content (Tasks 5–8 replace it; `Assets\**\*` is already embedded by `OKF4net.Viewer.csproj`):

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Interactive viewer script; implemented by the P1 plan
// (docs/superpowers/plans/2026-10-06-okf-viewer-interactive-p1.md).
(function () {
  "use strict";
})();
```

- [ ] **Step 4: Expose the new assets**

In `src/OKF4net.Viewer/ViewerAssets.cs`, add after `ViewerJs`:

```csharp
    /// <summary>Applies the stored theme before first paint and adds the theme toggle (loaded in <c>&lt;head&gt;</c>).</summary>
    public static string ThemeJs { get; } = Read("okf-theme.js");

    /// <summary>Side-effect-free helpers shared by the interactive scripts (<c>window.OkfSite</c>).</summary>
    public static string SiteJs { get; } = Read("okf-site.js");

    /// <summary>The tree explorer.</summary>
    public static string ExplorerJs { get; } = Read("okf-explorer.js");

    /// <summary>The "Jump to" palette.</summary>
    public static string PaletteJs { get; } = Read("okf-palette.js");

    /// <summary>Heading anchors, the contents list and fragment resolution.</summary>
    public static string TocJs { get; } = Read("okf-toc.js");
```

- [ ] **Step 5: Write the assets and the index**

In `src/OKF4net.Viewer/HtmlWriter.cs`, in `Write`, replace

```csharp
        WriteAsset(outDir, root, verifiedDirs, "marked.min.js", ViewerAssets.MarkedJs, written);
```

with

```csharp
        WriteAsset(outDir, root, verifiedDirs, "marked.min.js", ViewerAssets.MarkedJs, written);
        WriteAsset(outDir, root, verifiedDirs, "okf-theme.js", ViewerAssets.ThemeJs, written);
        WriteAsset(outDir, root, verifiedDirs, "okf-site.js", ViewerAssets.SiteJs, written);
        WriteAsset(outDir, root, verifiedDirs, "okf-explorer.js", ViewerAssets.ExplorerJs, written);
        WriteAsset(outDir, root, verifiedDirs, "okf-palette.js", ViewerAssets.PaletteJs, written);
        WriteAsset(outDir, root, verifiedDirs, "okf-toc.js", ViewerAssets.TocJs, written);
        WriteAsset(outDir, root, verifiedDirs, "okf-index.js", IndexScript.Render(site.Index), written);
```

In the remarks of `GuardNoCaseCollisions`, replace

```
    /// output volume. The three asset files under <c>assets/</c> are left out
    /// of the set: every generated page path ends in <c>.html</c> and every
```

with

```
    /// output volume. The asset files under <c>assets/</c> (the static scripts
    /// and stylesheet, and the generated <c>okf-index.js</c>) are left out
    /// of the set: every generated page path ends in <c>.html</c> and every
```

- [ ] **Step 6: Lay the page out in three zones**

Replace `RenderPage`, `RenderIndex`'s final `return`, `RenderBacklinks` and `RenderShell` in `HtmlWriter.cs`:

```csharp
    private static string RenderPage(ViewerPage page)
    {
        var prefix = RootPrefix(page.RelativeHtmlPath);
        var body = new StringBuilder();

        body.Append("<h1>").Append(HtmlEscape(page.Title)).Append("</h1>\n");
        body.Append("<p class=\"meta\">").Append(HtmlEscape(page.Id.ToString())).Append("</p>\n");
        body.Append(RenderFrontmatter(page.Frontmatter));
        body.Append("<div id=\"okf-body\"></div>\n");

        return RenderShell(page.Title, prefix, page.Id.ToString(), body.ToString(), RenderBacklinks(page.Backlinks), Payload(page));
    }
```

In `RenderIndex`, replace the last line `return RenderShell("Bundle index", string.Empty, body.ToString(), payload);` with:

```csharp
        return RenderShell("Bundle index", string.Empty, conceptId: null, body.ToString(), aside: string.Empty, payload);
```

```csharp
    private static string RenderBacklinks(IReadOnlyList<ViewerLink> backlinks)
    {
        if (backlinks.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder("<section class=\"okf-backlinks\" aria-labelledby=\"okf-backlinks-title\">\n<h2 id=\"okf-backlinks-title\">Referenced by</h2>\n<ul>\n");
        foreach (var link in backlinks)
        {
            sb.Append("<li><a href=\"").Append(HtmlEscape(link.Href)).Append("\">")
              .Append(HtmlEscape(link.RawTarget)).Append("</a></li>\n");
        }

        return sb.Append("</ul>\n</section>\n").ToString();
    }
```

```csharp
    /// <summary>
    /// The page frame shared by concept pages and the index. Its ids and
    /// attributes are the contract the interactive scripts rely on: the root
    /// prefix and concept id on <c>&lt;html&gt;</c>, <c>okf-tools</c>,
    /// <c>okf-explorer</c>, <c>okf-context</c> and <c>okf-toc</c>. The
    /// interactive scripts run after <c>viewer.js</c>, whose contract does not
    /// change; <c>okf-theme.js</c> runs in <c>&lt;head&gt;</c> so a stored theme
    /// applies before the first paint.
    /// </summary>
    private static string RenderShell(string title, string rootPrefix, string? conceptId, string body, string aside, string payload)
    {
        var conceptAttribute = conceptId is null ? string.Empty : $" data-okf-concept=\"{HtmlEscape(conceptId)}\"";
        var asideHidden = aside.Length == 0 ? " hidden" : string.Empty;
        return $"""
        <!doctype html>
        <html lang="en" data-okf-root="{rootPrefix}"{conceptAttribute}>
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{HtmlEscape(title)}</title>
        <script src="{rootPrefix}assets/okf-theme.js"></script>
        <link rel="stylesheet" href="{rootPrefix}assets/viewer.css">
        </head>
        <body>
        <div class="topline"></div>
        <header class="bar"><div class="bar-in">
        <a class="wordmark" href="{rootPrefix}index.html">OKF<sup>§</sup></a>
        <div class="bar-tools" id="okf-tools"></div>
        </div></header>
        <div class="okf-layout">
        <nav class="okf-explorer" id="okf-explorer" aria-label="Explorer" hidden></nav>
        <main>
        {body}</main>
        <aside class="okf-context" id="okf-context" aria-label="Page context"{asideHidden}>
        <section class="okf-toc" id="okf-toc" aria-labelledby="okf-toc-title" hidden>
        <h2 id="okf-toc-title">On this page</h2>
        <ul></ul>
        </section>
        {aside}</aside>
        </div>
        <script type="application/json" id="okf-payload">{payload}</script>
        <script src="{rootPrefix}assets/marked.min.js"></script>
        <script src="{rootPrefix}assets/viewer.js"></script>
        <script src="{rootPrefix}assets/okf-index.js"></script>
        <script src="{rootPrefix}assets/okf-site.js"></script>
        <script src="{rootPrefix}assets/okf-explorer.js"></script>
        <script src="{rootPrefix}assets/okf-palette.js"></script>
        <script src="{rootPrefix}assets/okf-toc.js"></script>
        </body>
        </html>

        """;
    }
```

- [ ] **Step 7: Update the existing script-count test to the new shell**

`Write_keeps_a_script_closing_tag_in_a_body_inside_the_payload` in `tests/OKF4net.Tests/Viewer/HtmlWriterTests.cs` counts every `</script>` of a page whose body contains a literal `</script>`: any extra one means the body broke out of the payload. The shell now emits nine script elements, so replace

```csharp
        // RenderShell always emits exactly three <script> elements (the JSON
        // payload, marked, viewer.js). A fourth </script> would mean the
        // body's own literal "</script>" text broke out of the payload
        // container instead of staying HTML-safe-JSON-escaped inside it.
        Assert.Equal(3, CountOccurrences(page, "</script>"));
```

with

```csharp
        // RenderShell always emits exactly nine <script> elements: okf-theme.js
        // in <head>, then the JSON payload, marked, viewer.js, okf-index.js,
        // okf-site.js, okf-explorer.js, okf-palette.js and okf-toc.js. A tenth
        // </script> would mean the body's own literal "</script>" text broke out
        // of the payload container instead of staying HTML-safe-JSON-escaped
        // inside it.
        Assert.Equal(9, CountOccurrences(page, "</script>"));
```

- [ ] **Step 8: Run the viewer tests**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Viewer|FullyQualifiedName~OKF4net.Tests.Render"`
Expected: PASS, including every pre-existing `HtmlWriterTests` and `OkfRenderCliTests` case.

- [ ] **Step 9: Format and commit**

```bash
dotnet format OKF4net.sln --verify-no-changes
git add src/OKF4net.Viewer/HtmlWriter.cs src/OKF4net.Viewer/ViewerAssets.cs src/OKF4net.Viewer/Assets/okf-theme.js src/OKF4net.Viewer/Assets/okf-site.js src/OKF4net.Viewer/Assets/okf-explorer.js src/OKF4net.Viewer/Assets/okf-palette.js src/OKF4net.Viewer/Assets/okf-toc.js tests/OKF4net.Tests/Viewer/HtmlWriterTests.cs
git commit -m "feat(viewer): three-zone page shell, interactive asset slots and okf-index.js"
```

---

### Task 4: Measurement (spec §3.6)

**Files:**
- Modify: `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md` (§3.6 numbers)

**Interfaces:**
- Consumes: the generator from Tasks 1–3.
- Produces: measured sizes recorded in the spec (no threshold: the owner judges the numbers).

- [ ] **Step 1: Generate the two reference sites**

```bash
cd E:/Sources/okf/.claude/worktrees/viewer-interactive-spec
M="$TEMP/okf-p1-measure"; rm -rf "$M"; mkdir -p "$M"
dotnet run --project producers/src/OkfProducer.Cli -c Release -- generate --repo . --out "$M/okf4net-bundle" --no-msbuild
dotnet run --project src/OKF4net.Render -c Release -- "$M/okf4net-bundle" --out "$M/okf4net-site"
dotnet run --project src/OKF4net.Render -c Release -- bundles/acme_retail --out "$M/acme-site"
```

Expected: both `okf-render` runs print `wrote N files to …`.

- [ ] **Step 2: Measure**

```bash
for s in okf4net-site acme-site; do
  f="$M/$s/assets/okf-index.js"
  node -e "global.window = {}; require(process.argv[1]); const i = window.OKF_INDEX; console.log(process.argv[2] + ': ' + i.concepts.length + ' concepts, ' + i.ghosts.length + ' ghosts, ' + i.edges.length + ' edges, ' + require('fs').statSync(process.argv[1]).size + ' bytes')" "$f" "$s"
done
```

Expected: two lines, e.g. `okf4net-site: 776 concepts, 40 ghosts, 891 edges, 250000 bytes`. Executing the file with Node is also a first check that the generated script runs.

There is no size threshold (owner decision, 2026-10-06): the numbers are recorded, and the owner judges them. Report them when the task ends.

- [ ] **Step 3: Record the numbers in the spec**

In `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md`, §3.6, append after the paragraph ending "les structures de graphe le sont à la demande.":

```markdown

Mesure sur le schéma définitif (P1, AAAA-MM-JJ, `--no-msbuild`, commande de la
tâche 4 du plan P1) : bundle d'OKF4net — C concepts, G fantômes, E arêtes,
`okf-index.js` de B octets ; `acme_retail` — c concepts, b octets.
```

writing the date of the run and the six numbers Step 2 printed in place of `AAAA-MM-JJ`, `C`, `G`, `E`, `B`, `c` and `b`.

- [ ] **Step 4: Commit**

```bash
git add docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md
git commit -m "docs(spec): record the measured okf-index.js size (P1)"
```

---

### Task 5: Shared helpers and the harness extension

**Files:**
- Modify: `src/OKF4net.Viewer/Assets/okf-site.js` (replace the skeleton)
- Create: `tools/viewer-security-check/fixtures/hostile-bundle/` (7 concept files below)
- Modify: `tools/viewer-security-check/run.js` (new section + async runner)
- Modify: `tools/viewer-security-check/package.json` (`pretest`)
- Modify: `tools/viewer-security-check/.gitignore`
- Modify: `.github/workflows/ci.yml` (`viewer-security` job: .NET for `pretest`)

**Interfaces:**
- Consumes: the DOM contract of Task 3, the index shape of Task 2.
- Produces:
  - `window.OkfSite` (frozen): `HEADING_PREFIX` (`"okf-h-"`), `readIndex(win) → index|null`, `normalize(text) → string`, `rank(index, query) → number[]`, `isStale(staleAfterMs, nowMs) → boolean`, `rootOf(doc) → string`, `resolve(root, path) → string`, `slugify(text) → string`, `uniqueSlugs(texts) → string[]`, `fragmentCandidates(hash) → string[]`, `element(doc, tag, className, text) → Element`.
  - Harness: `BASE` (`"https://okf.test/"`), `okfSite()` (bare window with `okf-site.js` loaded), `openPage(rel, opts) → Promise<window>` with `opts.hash`, `opts.mount` (serve the site under `BASE + mount`, i.e. a moved folder), `opts.blocked` (site-relative paths answered with an empty script), `opts.override` (site-relative path → source served instead of the generated file), `opts.now` (function returning ms), `opts.storage` (`"denied"`), `opts.storedTheme`, `opts.beforeParse(window)`; every page a case opens is checked for script errors raised after load (handler exceptions) when the case returns, then closed; `checkAsync(name, fn)`; `key(window, target, init) → KeyboardEvent`; `type(window, input, value)`; the marker comment `// --- end of async checks ---` before the runner.

- [ ] **Step 1: Write the fixture bundle**

`tools/viewer-security-check/fixtures/hostile-bundle/foo.md`:

```markdown
---
type: Note
title: '<img src=x onerror="window.__pwned=1">Foo'
description: Hostile title, repeated headings, author fragments, a clobbering attempt.
tags: ['<b>t</b>', 'core']
---
## Usage

first

## Usage

second

## Usage 1

third

## Tricky `<img src=x onerror=window.__pwned=1>`

See [the usage section](#usage) and [bar usage](foo/bar.md#usage).

<h2 id="OKF_INDEX" name="x">Clobber attempt</h2>
```

`tools/viewer-security-check/fixtures/hostile-bundle/foo/bar.md`:

```markdown
---
type: Note
title: Bar
description: Child of a concept that is also a folder; human-reviewed, long stale.
verified:
  - { by: "human:jsmith", at: "2026-07-01T09:00:00Z" }
stale_after: "2000-01-01T00:00:00Z"
---
## Usage

Bar usage.
```

`tools/viewer-security-check/fixtures/hostile-bundle/__proto__.md`, `constructor.md`, `toString.md` — same shape, titles `Proto`, `Constructor`, `ToString`:

```markdown
---
type: Note
title: Proto
description: An id that names an Object.prototype member.
---
Plain.
```

`tools/viewer-security-check/fixtures/hostile-bundle/broken.md`:

```markdown
---
type: Note
title: Broken
description: Links to a concept that does not exist; machine-confirmed; stale far in the future.
verified:
  - { by: "tool:okf-ci", at: "2026-07-01T09:00:00Z" }
stale_after: "2999-01-01T00:00:00Z"
---
See [gone](nowhere.md).
```

`tools/viewer-security-check/fixtures/hostile-bundle/edge.md`:

```markdown
---
type: Note
title: Edge
description: A deadline a tenth of a millisecond after midnight.
stale_after: "2026-10-06T00:00:00.0001000Z"
---
Plain.
```

- [ ] **Step 2: Generate the site from the harness, locally and in CI**

In `tools/viewer-security-check/package.json`, replace the `scripts` block with:

```json
  "scripts": {
    "pretest": "dotnet run --project ../../src/OKF4net.Render -c Release -- fixtures/hostile-bundle --out .generated/hostile-site",
    "test": "node run.js"
  },
```

Append to `tools/viewer-security-check/.gitignore`:

```
.generated/
```

In `.github/workflows/ci.yml`, job `viewer-security`, insert before `- name: Install harness dependencies`:

```yaml
      # `npm test`'s pretest step generates the site the interactive-viewer
      # cases load, with the real okf-render.
      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.x

```

- [ ] **Step 3: Add the helper checks and the async runner (failing)**

In `tools/viewer-security-check/run.js`, replace the last two lines

```js
console.log(`\n${passed} passed, ${failures} failed`);
process.exit(failures === 0 ? 0 : 1);
```

with:

```js
check("viewer.js alone, with only { body, links }, adds no global and still renders", () => {
  // The VS Code extension reuses viewer.js as is (ROADMAP.md): the
  // interactive scripts must never become a dependency of it.
  let before = null;
  const body = renderBody("# Title\n\ntext", {}, (window) => {
    before = new Set(Object.getOwnPropertyNames(window));
  });
  const added = Object.getOwnPropertyNames(body.ownerDocument.defaultView).filter((n) => !before.has(n));
  assert(added.length === 0, `viewer.js defined new globals: ${added.join(", ")}`);
  assert(body.querySelector("h1") && body.textContent.includes("text"), "viewer.js did not render on its own");
});

// --- interactive viewer (spec 2026-10-06, P1) -------------------------------
//
// Helper cases load the real okf-site.js into a bare window. Page cases load
// pages of a site GENERATED by the real okf-render from
// fixtures/hostile-bundle/ (npm's pretest step writes it to
// .generated/hostile-site/), with every script the page references, through
// a resource loader serving that directory. Page cases are async: they are
// queued and awaited before the summary, so a late assertion still fails
// the run.

const { requestInterceptor, VirtualConsole } = require("jsdom");
const SITE = path.join(__dirname, ".generated", "hostile-site");
const BASE = "https://okf.test/";
const siteSource = fs.readFileSync(path.join(ASSETS, "okf-site.js"), "utf8");

function okfSite() {
  const dom = new JSDOM("<!doctype html><html><body></body></html>", { runScripts: "outside-only" });
  dom.window.eval(siteSource);
  return dom.window;
}

// jsdom 29 has no ResourceLoader any more: resources go through undici
// interceptors. This one serves SITE under BASE + mount (so a case can
// "move" the generated folder) and never lets a request reach the network.
// A path in `blocked` answers with an empty script, as if the file defined
// nothing; a path in `override` answers with the given source instead of
// the generated file.
function siteResources(opts) {
  const prefix = BASE + (opts.mount || "");
  return {
    interceptors: [
      requestInterceptor((request) => {
        if (!request.url.startsWith(prefix)) { return new Response("", { status: 404 }); }
        const rel = decodeURIComponent(request.url.slice(prefix.length).split(/[?#]/)[0]);
        const type = rel.endsWith(".js") ? "application/javascript" : rel.endsWith(".css") ? "text/css" : "text/html";
        const headers = { "Content-Type": type };
        if ((opts.blocked || []).includes(rel)) { return new Response("", { headers }); }
        if (opts.override && Object.prototype.hasOwnProperty.call(opts.override, rel)) {
          return new Response(opts.override[rel], { headers });
        }
        return new Response(fs.readFileSync(path.join(SITE, rel)), { headers });
      }),
    ],
  };
}

// Pages opened by the running async case. Script errors raised AFTER load
// (an exception in a click or key handler) are recorded here and checked
// when the case returns, so a handler that throws cannot leave the run green.
let openedPages = [];

async function openPage(rel, opts = {}) {
  const file = path.join(SITE, rel);
  if (!fs.existsSync(file)) {
    throw new Error(`${file} is missing: run "npm test", whose pretest step regenerates the site with okf-render`);
  }
  const errors = [];
  const virtualConsole = new VirtualConsole();
  virtualConsole.on("jsdomError", (err) => {
    // jsdom does not implement navigation; location.assign() lands here
    // when a case does not veto the palette's okf:navigate event.
    if (!/Not implemented: navigation/.test(err.message)) { errors.push(err); }
  });
  const dom = new JSDOM(fs.readFileSync(file, "utf8"), {
    url: BASE + (opts.mount || "") + rel + (opts.hash || ""),
    runScripts: "dangerously",
    resources: siteResources(opts),
    pretendToBeVisual: true,
    virtualConsole,
    beforeParse(window) {
      if (opts.now) { window.Date.now = () => opts.now(); }
      if (opts.storage === "denied") {
        const deny = function () { throw new window.DOMException("denied", "SecurityError"); };
        window.Storage.prototype.getItem = deny;
        window.Storage.prototype.setItem = deny;
      }
      if (opts.storedTheme) { window.localStorage.setItem("okf-theme", opts.storedTheme); }
      if (opts.beforeParse) { opts.beforeParse(window); }
    },
  });
  openedPages.push({ window: dom.window, errors });
  await new Promise((resolve) => dom.window.addEventListener("load", resolve));
  if (errors.length > 0) { throw new Error(`page script error during load: ${errors[0].message}`); }
  return dom.window;
}

function key(window, target, init) {
  const event = new window.KeyboardEvent("keydown", Object.assign({ bubbles: true, cancelable: true }, init));
  target.dispatchEvent(event);
  return event;
}

function type(window, input, value) {
  input.value = value;
  input.dispatchEvent(new window.Event("input", { bubbles: true }));
}

const asyncChecks = [];

/** @param {string} name @param {() => Promise<void>} fn */
function checkAsync(name, fn) {
  asyncChecks.push({ name, fn });
}

console.log("\nInteractive viewer helpers (okf-site.js):");

check("normalize trims, collapses whitespace and lower-cases without folding accents", () => {
  const { OkfSite } = okfSite();
  assert(OkfSite.normalize("  Gross \t Margin ") === "gross margin", "whitespace not normalized");
  assert(OkfSite.normalize("Écart") === "écart", "accents must not be folded");
});

check("rank orders by fixed tiers, keeps a concept's best tier, breaks ties by index", () => {
  const { OkfSite } = okfSite();
  const index = { concepts: [
    { id: "metrics/margin", title: "Margin", tags: ["finance"] },
    { id: "margin", title: "Margin overview", tags: [] },
    { id: "glossary/gross-margin", title: "Gross margin", tags: ["margin"] },
    { id: "x", title: "Other", tags: ["Margin"] },
    { id: "y", title: "Unrelated", tags: ["margins"] },
  ] };
  const got = JSON.stringify(Array.from(OkfSite.rank(index, " MARGIN ")));
  assert(got === "[1,0,2,3]", `expected [1,0,2,3] (exact id, title prefix, id substring, exact tag), got ${got}`);
  assert(OkfSite.rank(index, "   ").length === 0, "a blank query must return nothing");
});

check("isStale compares whole milliseconds and never reports a missing deadline", () => {
  const { OkfSite } = okfSite();
  assert(!OkfSite.isStale(1000, 999), "stale before the deadline");
  assert(OkfSite.isStale(1000, 1000), "not stale at the deadline (§5.5 is now >= stale_after)");
  assert(!OkfSite.isStale(null, 1e15), "a null deadline reported stale");
});

check("uniqueSlugs never lets a generated suffix collide with a real heading", () => {
  const { OkfSite } = okfSite();
  const got = JSON.stringify(Array.from(OkfSite.uniqueSlugs(["Usage", "Usage", "Usage 1", "!!!"])));
  assert(got === JSON.stringify(["usage", "usage-1", "usage-1-1", "section"]), `got ${got}`);
});

check("fragmentCandidates keeps a prefixed fragment and slugifies an author one", () => {
  const { OkfSite } = okfSite();
  const json = (h) => JSON.stringify(Array.from(OkfSite.fragmentCandidates(h)));
  assert(json("#okf-h-usage") === JSON.stringify(["okf-h-usage"]), json("#okf-h-usage"));
  assert(json("#Usage") === JSON.stringify(["okf-h-Usage", "okf-h-usage"]), json("#Usage"));
  assert(json("#caf%C3%A9") === JSON.stringify(["okf-h-café"]), json("#caf%C3%A9"));
  assert(json("#") === "[]", json("#"));
});

check("rootOf accepts only a chain of ../", () => {
  const window = okfSite();
  const html = window.document.documentElement;
  html.setAttribute("data-okf-root", "../../");
  assert(window.OkfSite.rootOf(window.document) === "../../", "a valid root was rejected");
  html.setAttribute("data-okf-root", "javascript:alert(1)//");
  assert(window.OkfSite.rootOf(window.document) === "", "a non-../ root was accepted");
});

check("readIndex rejects an element standing in for the index (DOM clobbering)", () => {
  const window = okfSite();
  const div = window.document.createElement("div");
  div.id = "OKF_INDEX";
  window.document.body.appendChild(div);
  window.OKF_INDEX = div;
  assert(window.OkfSite.readIndex(window) === null, "an element was accepted as the index");
});

console.log("\nInteractive viewer pages (generated from fixtures/hostile-bundle):");

checkAsync("the generated index executes with hostile ids as plain values", async () => {
  const window = await openPage("foo.html");
  const ids = Array.from(window.OKF_INDEX.concepts, (c) => c.id);
  for (const id of ["__proto__", "constructor", "toString"]) {
    assert(ids.includes(id), `${id} missing from the executed index`);
  }
  assert(window.OKF_INDEX.concepts.find((c) => c.id === "__proto__").title === "Proto", "__proto__ lost its record");
  assert(Object.getPrototypeOf(window.OKF_INDEX) === window.Object.prototype, "the index object's prototype was replaced");
});

// --- end of async checks ---

async function runAsyncChecks() {
  for (const { name, fn } of asyncChecks) {
    openedPages = [];
    try {
      await fn();
      // Errors raised by handlers after load fail the case too.
      for (const page of openedPages) {
        if (page.errors.length > 0) { throw new Error(`page script error: ${page.errors[0].message}`); }
      }
      passed++;
      console.log(`  ok  - ${name}`);
    } catch (err) {
      failures++;
      console.log(`FAIL  - ${name}`);
      console.log(`        ${err.message}`);
    } finally {
      for (const page of openedPages) { page.window.close(); }
    }
  }
}

runAsyncChecks().then(
  () => {
    console.log(`\n${passed} passed, ${failures} failed`);
    process.exit(failures === 0 ? 0 : 1);
  },
  (err) => {
    console.log(`FAIL  - the async runner crashed: ${err && err.stack}`);
    process.exit(1);
  },
);
```

- [ ] **Step 4: Run the harness to verify the new cases fail**

Run: `cd tools/viewer-security-check && npm ci && npm test`
Expected: `pretest` generates `.generated/hostile-site`; the pre-existing cases and the `viewer.js alone` case pass; every `okf-site.js` helper case FAILS (`Cannot read properties of undefined` — the skeleton defines no `OkfSite`); `the generated index executes…` PASSES (the index already exists); exit code 1.

- [ ] **Step 5: Write `okf-site.js`**

Replace `src/OKF4net.Viewer/Assets/okf-site.js` with:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Side-effect-free helpers shared by the interactive viewer scripts
// (okf-explorer.js, okf-palette.js, okf-toc.js): reading the site index, the
// palette ranking, staleness, the one path resolver, heading slugs. Loading
// this file only defines window.OkfSite, so tools/viewer-security-check/
// calls every function directly. viewer.js does not use it: viewer.js keeps
// its { body, links } contract, which the VS Code extension reuses.
(function () {
  "use strict";

  var HEADING_PREFIX = "okf-h-";

  // The site index, or null when okf-index.js did not run or window.OKF_INDEX
  // is something else -- typically a DOM element reached through named
  // access (DOM clobbering).
  function readIndex(win) {
    var idx;
    try { idx = win.OKF_INDEX; } catch (e) { return null; }
    if (!idx || typeof idx !== "object" || "nodeType" in idx) { return null; }
    if (!Array.isArray(idx.concepts) || !Array.isArray(idx.ghosts)
        || !Array.isArray(idx.edges) || !Array.isArray(idx.tree)) { return null; }
    return idx;
  }

  // Spec §4.6: trim, collapse internal whitespace, locale-independent lower
  // case. Accents are NOT folded.
  function normalize(text) {
    return String(text).replace(/\s+/g, " ").trim().toLowerCase();
  }

  // Palette ranking (spec §4.6). Fixed tiers, no weights -- deliberately not
  // ConceptSearch: 0 exact id, 1 prefix of the title or id, 2 substring of
  // the title or id, 3 exact tag. Tiers are tried best first, so a concept
  // keeps its best one. Ties keep index order, which C# sorted with
  // ConceptId.CompareTo (spec §3.1). Returns concept positions.
  function rank(index, query) {
    var q = normalize(query);
    if (q === "") { return []; }
    var hits = [];
    for (var i = 0; i < index.concepts.length; i++) {
      var c = index.concepts[i];
      var id = normalize(c.id);
      var title = normalize(c.title);
      var tier = -1;
      if (id === q) {
        tier = 0;
      } else if (id.indexOf(q) === 0 || title.indexOf(q) === 0) {
        tier = 1;
      } else if (id.indexOf(q) !== -1 || title.indexOf(q) !== -1) {
        tier = 2;
      } else {
        for (var t = 0; t < c.tags.length; t++) {
          if (normalize(c.tags[t]) === q) { tier = 3; break; }
        }
      }
      if (tier !== -1) { hits.push({ i: i, tier: tier }); }
    }
    hits.sort(function (a, b) { return a.tier - b.tier || a.i - b.i; });
    return hits.map(function (h) { return h.i; });
  }

  // Spec §4.4: staleAfterMs is the deadline rounded UP to a whole millisecond
  // in C#, so this comparison equals Lifecycle.IsStale. It is the only part
  // of §5.5 redone in JavaScript (an exception recorded in CLAUDE.md).
  function isStale(staleAfterMs, nowMs) {
    return typeof staleAfterMs === "number" && nowMs >= staleAfterMs;
  }

  // This page's way back to the site root, as HtmlWriter wrote it. Anything
  // but a chain of "../" is refused: the resolver below concatenates it.
  function rootOf(doc) {
    var root = doc.documentElement.getAttribute("data-okf-root") || "";
    return /^(\.\.\/)*$/.test(root) ? root : "";
  }

  // The only resolver the interactive scripts use (spec §3.5): index paths
  // are relative to the site root, never to the page or to the script.
  function resolve(root, path) {
    return root + path;
  }

  // Heading slug (spec §5): letters, digits and hyphens only.
  function slugify(text) {
    var slug = normalize(text).replace(/[^\p{L}\p{N} -]/gu, "").replace(/ /g, "-");
    return slug === "" ? "section" : slug;
  }

  // Unique slugs in document order. A taken candidate keeps counting, so a
  // generated suffix never collides with a real heading: "Usage", "Usage",
  // "Usage 1" -> usage, usage-1, usage-1-1.
  function uniqueSlugs(texts) {
    var used = new Set();
    var out = [];
    for (var k = 0; k < texts.length; k++) {
      var base = slugify(texts[k]);
      var slug = base;
      var n = 0;
      while (used.has(slug)) { n++; slug = base + "-" + n; }
      used.add(slug);
      out.push(slug);
    }
    return out;
  }

  // Generated ids a URL fragment may designate, best first: an already
  // prefixed fragment as is; an author fragment ("#usage", "#Usage") as
  // written, then slugified.
  function fragmentCandidates(hash) {
    var h = String(hash || "");
    if (h.charAt(0) === "#") { h = h.slice(1); }
    try { h = decodeURIComponent(h); } catch (e) { /* keep it raw */ }
    if (h === "") { return []; }
    if (h.indexOf(HEADING_PREFIX) === 0) { return [h]; }
    var candidates = [HEADING_PREFIX + h];
    var slugged = HEADING_PREFIX + slugify(h);
    if (slugged !== candidates[0]) { candidates.push(slugged); }
    return candidates;
  }

  // Creates an element; text goes through textContent, never markup.
  function element(doc, tag, className, text) {
    var el = doc.createElement(tag);
    if (className) { el.className = className; }
    if (text !== undefined) { el.textContent = text; }
    return el;
  }

  window.OkfSite = Object.freeze({
    HEADING_PREFIX: HEADING_PREFIX,
    readIndex: readIndex,
    normalize: normalize,
    rank: rank,
    isStale: isStale,
    rootOf: rootOf,
    resolve: resolve,
    slugify: slugify,
    uniqueSlugs: uniqueSlugs,
    fragmentCandidates: fragmentCandidates,
    element: element,
  });
})();
```

- [ ] **Step 6: Run the harness to verify everything passes**

Run: `cd tools/viewer-security-check && npm test`
Expected: `… passed, 0 failed`, exit code 0.

- [ ] **Step 7: Commit**

```bash
git add src/OKF4net.Viewer/Assets/okf-site.js tools/viewer-security-check/run.js tools/viewer-security-check/package.json tools/viewer-security-check/.gitignore tools/viewer-security-check/fixtures/hostile-bundle .github/workflows/ci.yml
git commit -m "feat(viewer): shared interactive helpers and a harness over a generated hostile site"
```

---

### Task 6: Explorer, theme and layout

**Files:**
- Modify: `src/OKF4net.Viewer/Assets/okf-explorer.js`, `okf-theme.js` (replace the skeletons)
- Modify: `src/OKF4net.Viewer/Assets/viewer.css`
- Modify: `tools/viewer-security-check/run.js` (cases before `// --- end of async checks ---`)

**Interfaces:**
- Consumes: `OkfSite` (Task 5); `#okf-explorer`, `#okf-tools`, `data-okf-root`, `data-okf-concept` (Task 3).
- Produces: explorer DOM — `#okf-tree-filter` (search input), `ul.okf-tree` of `li` > `div.okf-tree-row` > (`button.okf-tree-toggle[aria-expanded]` | `span.okf-tree-spacer`) + (`a.okf-tree-link[title=<id>]` (`aria-current="page"` on the current page) | `span.okf-tree-folder`) + badges `span.okf-badge.okf-trust-human|okf-trust-machine` and `span.okf-badge.okf-stale` (hidden unless stale); theme toggle `button#okf-theme-toggle.okf-tool[aria-pressed]`; `localStorage` key `okf-theme` (`light`|`dark`); `data-theme` on `<html>`.

- [ ] **Step 1: Write the failing harness cases**

Insert before `// --- end of async checks ---` in `run.js`:

```js
function treeLink(window, id) {
  return Array.from(window.document.querySelectorAll("#okf-explorer a.okf-tree-link"))
    .find((a) => a.getAttribute("title") === id) || null;
}

function isShown(el) {
  return el !== null && !el.closest("[hidden]");
}

checkAsync("explorer: a node is both a page and a folder, with separate open and expand commands", async () => {
  const window = await openPage("foo/bar.html");
  assert(!window.document.getElementById("okf-explorer").hidden, "the explorer stayed hidden");
  const foo = treeLink(window, "foo");
  assert(foo && foo.getAttribute("href") === "../foo.html", `foo href: ${foo && foo.getAttribute("href")} (must resolve from the site root)`);
  const toggle = foo.parentElement.querySelector("button.okf-tree-toggle");
  assert(toggle, "foo has no expand button although foo/bar exists");
  assert(toggle.getAttribute("aria-expanded") === "true", "the path to the current page is not expanded");
  const current = window.document.querySelector('#okf-explorer a[aria-current="page"]');
  assert(current && current.getAttribute("title") === "foo/bar", "the current page is not marked");
  toggle.click();
  assert(toggle.getAttribute("aria-expanded") === "false" && !isShown(treeLink(window, "foo/bar")), "the toggle did not collapse");
});

checkAsync("explorer links resolve from the site root wherever the generated folder is moved", async () => {
  const window = await openPage("foo/bar.html", { mount: "moved/elsewhere/" });
  const foo = treeLink(window, "foo");
  assert(foo.href === `${BASE}moved/elsewhere/foo.html`, `foo resolves to ${foo.href}`);
  const proto = treeLink(window, "__proto__");
  assert(proto.href === `${BASE}moved/elsewhere/__proto__.html`, `__proto__ resolves to ${proto.href}`);
});

checkAsync("explorer: the filter keeps the ancestors of a match and hides the rest", async () => {
  const window = await openPage("index.html");
  const filter = window.document.getElementById("okf-tree-filter");
  type(window, filter, "bar");
  assert(isShown(treeLink(window, "foo/bar")), "the match is hidden");
  assert(isShown(treeLink(window, "foo")), "the match's ancestor is hidden");
  assert(!isShown(treeLink(window, "toString")), "a non-matching concept stayed visible");
  type(window, filter, "");
  assert(isShown(treeLink(window, "toString")), "clearing the filter did not restore the tree");
});

checkAsync("explorer: ids naming Object.prototype members are distinct entries", async () => {
  const window = await openPage("index.html");
  for (const id of ["__proto__", "constructor", "toString"]) {
    const link = treeLink(window, id);
    assert(link && link.getAttribute("href") === `${id}.html`, `${id}: ${link && link.getAttribute("href")}`);
  }
});

checkAsync("explorer: trust badges follow ConceptAudit's tiers", async () => {
  const window = await openPage("index.html");
  const row = (id) => treeLink(window, id).parentElement;
  assert(row("foo/bar").querySelector(".okf-trust-human"), "human-reviewed badge missing");
  assert(row("broken").querySelector(".okf-trust-machine"), "machine-confirmed badge missing");
  assert(!row("toString").querySelector(".okf-trust-human, .okf-trust-machine"), "an unverified concept got a trust badge");
});

checkAsync("staleness is evaluated at reading time and refreshed when the page becomes visible", async () => {
  let now = Date.UTC(2026, 9, 6);
  const window = await openPage("index.html", { now: () => now });
  const stale = (id) => treeLink(window, id).parentElement.querySelector(".okf-stale");
  assert(!stale("foo/bar").hidden, "a 2000 deadline is not shown as stale in 2026");
  assert(stale("broken").hidden, "a 2999 deadline is shown as stale in 2026");
  assert(stale("toString") === null, "a concept without stale_after got a stale mark");
  now = Date.UTC(3000, 0, 1);
  window.document.dispatchEvent(new window.Event("visibilitychange"));
  assert(!stale("broken").hidden, "visibilitychange did not refresh staleness");
});

checkAsync("a sub-millisecond deadline is stale from the next whole millisecond, as in C#", async () => {
  const midnight = Date.UTC(2026, 9, 6);
  const window = await openPage("index.html", { now: () => midnight });
  const edge = window.OKF_INDEX.concepts.find((c) => c.id === "edge");
  assert(edge.staleAfterMs === midnight + 1, `staleAfterMs ${edge.staleAfterMs}, expected ${midnight + 1}`);
  assert(edge.staleAfterDate === "2026-10-06", `staleAfterDate ${edge.staleAfterDate}`);
  assert(treeLink(window, "edge").parentElement.querySelector(".okf-stale").hidden, "stale at .000 although the deadline is .0001");
  assert(window.OkfSite.isStale(edge.staleAfterMs, midnight + 1), "not stale one millisecond later");
});

checkAsync("theme: a stored choice applies from <head>; denied storage degrades without errors", async () => {
  const window = await openPage("index.html", { storedTheme: "dark" });
  const html = window.document.documentElement;
  assert(html.getAttribute("data-theme") === "dark", "the stored theme was not applied");
  const toggle = window.document.getElementById("okf-theme-toggle");
  assert(toggle && toggle.getAttribute("aria-pressed") === "true", "the toggle does not announce the dark state");
  toggle.click();
  assert(html.getAttribute("data-theme") === "light", "the toggle did not switch");
  assert(window.localStorage.getItem("okf-theme") === "light", "the choice was not stored");
  const denied = await openPage("index.html", { storage: "denied" });
  const toggle2 = denied.document.getElementById("okf-theme-toggle");
  assert(toggle2, "the toggle is missing when storage is denied");
  toggle2.click();
  assert(denied.document.documentElement.getAttribute("data-theme") === "dark", "the toggle is broken when storage is denied");
});

checkAsync("theme: the announced state follows a system preference change while nothing is forced", async () => {
  let query = null;
  const window = await openPage("index.html", {
    beforeParse(w) {
      query = new w.EventTarget();
      query.matches = false;
      w.matchMedia = () => query;
    },
  });
  const toggle = window.document.getElementById("okf-theme-toggle");
  assert(toggle.getAttribute("aria-pressed") === "false", "a light system preference is announced as dark");
  query.matches = true;
  query.dispatchEvent(new window.Event("change"));
  assert(toggle.getAttribute("aria-pressed") === "true", "a change of the system preference was not re-announced");
});
```

- [ ] **Step 2: Run the harness to verify they fail**

Run: `cd tools/viewer-security-check && npm test`
Expected: the nine new cases FAIL (`the explorer stayed hidden`, `Cannot read properties of null`, `the stored theme was not applied`); exit code 1.

- [ ] **Step 3: Write `okf-theme.js`**

Replace `src/OKF4net.Viewer/Assets/okf-theme.js` with:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Theme (spec §4.7). Loaded in <head>, before the stylesheet, so a stored
// choice applies before the first paint on every page. Storage can be
// unavailable (file:// in some browsers, privacy modes) and, even when a
// write succeeds, local pages may not share it: every access is guarded and
// without it the page follows prefers-color-scheme.
(function () {
  "use strict";
  var KEY = "okf-theme";
  var root = document.documentElement;

  function stored() {
    try {
      var value = window.localStorage.getItem(KEY);
      return value === "light" || value === "dark" ? value : null;
    } catch (e) {
      return null;
    }
  }

  function store(value) {
    try { window.localStorage.setItem(KEY, value); } catch (e) { /* not persisted */ }
  }

  var initial = stored();
  if (initial) { root.setAttribute("data-theme", initial); }

  function effective() {
    var forced = root.getAttribute("data-theme");
    if (forced === "light" || forced === "dark") { return forced; }
    var query = typeof window.matchMedia === "function" ? window.matchMedia("(prefers-color-scheme: dark)") : null;
    return query && query.matches ? "dark" : "light";
  }

  function addToggle() {
    var tools = document.getElementById("okf-tools");
    if (!tools) { return; }
    var button = document.createElement("button");
    button.type = "button";
    button.id = "okf-theme-toggle";
    button.className = "okf-tool";
    button.textContent = "Dark theme";
    function sync() {
      button.setAttribute("aria-pressed", effective() === "dark" ? "true" : "false");
    }
    button.addEventListener("click", function () {
      var next = effective() === "dark" ? "light" : "dark";
      root.setAttribute("data-theme", next);
      store(next);
      sync();
    });
    // While no theme is forced, the pressed state follows the system
    // preference: re-announce it when that preference changes.
    if (typeof window.matchMedia === "function") {
      var query = window.matchMedia("(prefers-color-scheme: dark)");
      if (query && typeof query.addEventListener === "function") {
        query.addEventListener("change", sync);
      } else if (query && typeof query.addListener === "function") {
        query.addListener(sync);
      }
    }
    sync();
    tools.appendChild(button);
  }

  if (document.readyState === "loading") {
    document.addEventListener("DOMContentLoaded", addToggle);
  } else {
    addToggle();
  }
})();
```

- [ ] **Step 4: Write `okf-explorer.js`**

Replace `src/OKF4net.Viewer/Assets/okf-explorer.js` with:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The tree explorer (spec §3.2, §8). Reads the site index through
// OkfSite.readIndex and renders into #okf-explorer; without an index the
// explorer stays hidden and the page still works. Bundle text reaches the
// DOM through textContent and fixed-name attributes only (spec §3.4).
(function () {
  "use strict";
  var site = window.OkfSite;
  var nav = document.getElementById("okf-explorer");
  if (!site || !nav) { return; }
  var index = site.readIndex(window);
  if (!index) { return; }

  var root = site.rootOf(document);
  var currentId = document.documentElement.getAttribute("data-okf-concept");
  var current = -1;
  for (var i = 0; i < index.concepts.length; i++) {
    if (index.concepts[i].id === currentId) { current = i; break; }
  }

  // The trust value only selects a key here; it never becomes a class name.
  var TRUST_CLASS = new Map([
    ["human-reviewed", "okf-trust-human"],
    ["machine-confirmed", "okf-trust-machine"],
  ]);

  var staleMarks = [];

  function el(tag, className, text) {
    return site.element(document, tag, className, text);
  }

  function appendBadges(row, concept) {
    var trustClass = TRUST_CLASS.get(concept.trust);
    if (trustClass) {
      var badge = el("span", "okf-badge " + trustClass);
      badge.setAttribute("title", concept.trust);
      badge.appendChild(el("span", "okf-sr", concept.trust));
      row.appendChild(badge);
    }
    if (typeof concept.staleAfterMs === "number") {
      var stale = el("span", "okf-badge okf-stale");
      stale.setAttribute("title", "stale after " + (concept.staleAfterDate || ""));
      stale.appendChild(el("span", "okf-sr", "stale"));
      staleMarks.push({ el: stale, ms: concept.staleAfterMs });
      row.appendChild(stale);
    }
  }

  function setExpanded(rec, open) {
    rec.ul.hidden = !open;
    rec.toggle.setAttribute("aria-expanded", open ? "true" : "false");
  }

  // A node can be BOTH a destination (the concept at this path) and a folder
  // (children): foo.md and foo/bar.md coexist legally, so opening (the link)
  // and expanding (the button) are separate commands (spec §3.2).
  function build(node) {
    var rec = { li: document.createElement("li"), ul: null, toggle: null, label: "", children: [], holdsCurrent: false, defaultOpen: false };
    var row = el("div", "okf-tree-row");

    if (node.children.length > 0) {
      var toggle = el("button", "okf-tree-toggle");
      toggle.type = "button";
      toggle.appendChild(el("span", "okf-sr", "Expand or collapse " + node.name));
      toggle.addEventListener("click", function () { setExpanded(rec, rec.ul.hidden); });
      rec.toggle = toggle;
      row.appendChild(toggle);
    } else {
      row.appendChild(el("span", "okf-tree-spacer"));
    }

    var concept = node.concept >= 0 ? index.concepts[node.concept] : null;
    if (concept) {
      var link = el("a", "okf-tree-link", concept.title);
      link.setAttribute("href", site.resolve(root, concept.path));
      link.setAttribute("title", concept.id);
      if (node.concept === current) {
        link.setAttribute("aria-current", "page");
        rec.holdsCurrent = true;
      }
      row.appendChild(link);
      appendBadges(row, concept);
      rec.label = site.normalize(concept.title + " " + concept.id);
    } else {
      row.appendChild(el("span", "okf-tree-folder", node.name));
      rec.label = site.normalize(node.name);
    }
    rec.li.appendChild(row);

    if (node.children.length > 0) {
      rec.ul = el("ul", "okf-tree-children");
      var below = false;
      for (var k = 0; k < node.children.length; k++) {
        var child = build(node.children[k]);
        rec.children.push(child);
        rec.ul.appendChild(child.li);
        if (child.holdsCurrent) { below = true; }
      }
      rec.li.appendChild(rec.ul);
      // Only the path down to the current page starts open.
      rec.defaultOpen = below;
      if (below) { rec.holdsCurrent = true; }
      setExpanded(rec, rec.defaultOpen);
    }
    return rec;
  }

  // Filter by name: matches and their ancestors stay visible; ancestors of a
  // match open. An empty query restores the default state.
  function applyFilter(rec, q) {
    var childVisible = false;
    for (var k = 0; k < rec.children.length; k++) {
      if (applyFilter(rec.children[k], q)) { childVisible = true; }
    }
    var visible = q === "" || rec.label.indexOf(q) !== -1 || childVisible;
    rec.li.hidden = !visible;
    if (rec.ul) { setExpanded(rec, q === "" ? rec.defaultOpen : childVisible); }
    return visible;
  }

  function refreshStale() {
    var now = Date.now();
    for (var k = 0; k < staleMarks.length; k++) {
      staleMarks[k].el.hidden = !site.isStale(staleMarks[k].ms, now);
    }
  }

  var head = el("div", "okf-explorer-head");
  var label = el("label", "okf-explorer-label", "Explorer");
  label.setAttribute("for", "okf-tree-filter");
  var filter = document.createElement("input");
  filter.type = "search";
  filter.id = "okf-tree-filter";
  filter.setAttribute("placeholder", "Filter by name");
  filter.setAttribute("autocomplete", "off");
  head.appendChild(label);
  head.appendChild(filter);

  var list = el("ul", "okf-tree");
  var tops = [];
  for (var t = 0; t < index.tree.length; t++) {
    var rec = build(index.tree[t]);
    tops.push(rec);
    list.appendChild(rec.li);
  }

  nav.appendChild(head);
  nav.appendChild(list);
  nav.hidden = false;

  filter.addEventListener("input", function () {
    var q = site.normalize(filter.value);
    for (var k = 0; k < tops.length; k++) { applyFilter(tops[k], q); }
  });

  refreshStale();
  document.addEventListener("visibilitychange", function () {
    if (document.visibilityState === "visible") { refreshStale(); }
  });
})();
```

- [ ] **Step 5: Style the layout, the explorer and both themes**

In `src/OKF4net.Viewer/Assets/viewer.css`, in the `.bar-in` rule replace `max-width: 900px;` with `max-width: 1440px;`, then replace the final block

```css
@media (prefers-color-scheme: dark) {
  :root { --white: #101014; --ink: #f2f2f5; --blue: #8fa5f5; --blue-soft: #1a1a22; --hair: #2a2a33; --gray: #9a9aa2; }
}
```

with:

```css
/* Dark theme: follows the system unless the toggle forced a theme
   (okf-theme.js sets data-theme on <html>). Same values in both blocks. */
:root[data-theme="dark"] {
  --white: #101014; --ink: #f2f2f5; --blue: #8fa5f5; --blue-soft: #1a1a22; --hair: #2a2a33; --gray: #9a9aa2;
}
@media (prefers-color-scheme: dark) {
  :root:not([data-theme="light"]) {
    --white: #101014; --ink: #f2f2f5; --blue: #8fa5f5; --blue-soft: #1a1a22; --hair: #2a2a33; --gray: #9a9aa2;
  }
}

/* Interactive viewer (spec 2026-10-06). */
.okf-sr {
  position: absolute; width: 1px; height: 1px; padding: 0; margin: -1px;
  overflow: hidden; clip: rect(0 0 0 0); white-space: nowrap; border: 0;
}
.bar-tools { margin-left: auto; display: flex; flex-wrap: wrap; gap: 8px; }
.okf-tool {
  font: inherit; font-size: 14px; min-height: 36px; padding: 0 12px; cursor: pointer;
  border: 1px solid var(--hair); background: var(--white); color: var(--ink);
}
.okf-tool[aria-pressed="true"] { border-color: var(--blue); color: var(--blue); }

.okf-layout { display: flex; flex-wrap: wrap; align-items: flex-start; max-width: 1440px; margin: 0 auto; }
.okf-layout > main { flex: 999 1 560px; min-width: 0; margin: 0; }
.okf-explorer { flex: 1 1 260px; max-width: 320px; padding: 16px; border-right: 1px solid var(--hair); }
.okf-context { flex: 1 1 260px; max-width: 320px; padding: 24px 16px; }
@media (min-width: 1100px) {
  .okf-explorer, .okf-context { position: sticky; top: 0; max-height: 100vh; overflow-y: auto; }
}
@media (max-width: 1099px) {
  .okf-explorer, .okf-context { max-width: none; flex-basis: 100%; border-right: 0; border-bottom: 1px solid var(--hair); }
}

.okf-explorer-label {
  display: block; font-family: var(--mono); font-size: 11px; letter-spacing: .06em;
  text-transform: uppercase; color: var(--gray); margin-bottom: 6px;
}
#okf-tree-filter {
  width: 100%; min-height: 36px; padding: 0 10px; font: inherit; font-size: 14px;
  border: 1px solid var(--hair); background: var(--white); color: var(--ink);
}
.okf-tree, .okf-tree-children { list-style: none; margin: 0; padding: 0; }
.okf-tree { margin-top: 12px; }
.okf-tree-children { padding-left: 16px; }
.okf-tree-row { display: flex; align-items: center; gap: 6px; min-height: 32px; font-size: 14px; }
.okf-tree-toggle { flex: none; width: 28px; height: 28px; border: 0; background: none; color: var(--gray); cursor: pointer; }
.okf-tree-toggle::before { content: "\25B8"; }
.okf-tree-toggle[aria-expanded="true"]::before { content: "\25BE"; }
.okf-tree-spacer { flex: none; width: 28px; }
.okf-tree-link { color: var(--ink); text-decoration: none; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }
.okf-tree-link:hover { text-decoration: underline; }
.okf-tree-link[aria-current="page"] { color: var(--blue); font-weight: 600; }
.okf-tree-folder { color: var(--gray); }
.okf-badge { flex: none; display: inline-block; width: 8px; height: 8px; border-radius: 50%; box-sizing: border-box; }
.okf-badge[hidden] { display: none; }
.okf-trust-human { background: var(--blue); }
.okf-trust-machine { border: 2px solid var(--blue); }
.okf-stale { border-radius: 0; width: 9px; background: #b4540a; clip-path: polygon(50% 0, 100% 100%, 0 100%); }
```

- [ ] **Step 6: Run the harness to verify everything passes**

Run: `cd tools/viewer-security-check && npm test`
Expected: `… passed, 0 failed`, exit code 0.

- [ ] **Step 7: Commit**

```bash
git add src/OKF4net.Viewer/Assets/okf-explorer.js src/OKF4net.Viewer/Assets/okf-theme.js src/OKF4net.Viewer/Assets/viewer.css tools/viewer-security-check/run.js
git commit -m "feat(viewer): tree explorer, staleness badges and theme toggle"
```

- [ ] **Step 8: Prove the harness catches an exception raised in a handler**

A guard that only checks errors during load stays green when a click handler throws. Mutate on purpose: in `src/OKF4net.Viewer/Assets/okf-theme.js`, replace the body of `store` with `window.localStorage.setItem(KEY, value);` (no `try`), then run `cd tools/viewer-security-check && npm test`.
Expected: the case `theme: a stored choice applies from <head>; denied storage degrades without errors` FAILS with `page script error: …SecurityError…`. Then restore the committed file:

```bash
git checkout -- src/OKF4net.Viewer/Assets/okf-theme.js
cd tools/viewer-security-check && npm test
```

Expected: `… passed, 0 failed`. If the mutated run was green, the error check in `runAsyncChecks` is broken: fix it before going on.

---

### Task 7: "Jump to" palette

**Files:**
- Modify: `src/OKF4net.Viewer/Assets/okf-palette.js` (replace the skeleton)
- Modify: `src/OKF4net.Viewer/Assets/viewer.css` (append)
- Modify: `tools/viewer-security-check/run.js` (cases before `// --- end of async checks ---`)

**Interfaces:**
- Consumes: `OkfSite` (Task 5), `#okf-tools` (Task 3), `#okf-tree-filter` (Task 6, an editable field for the shortcut test).
- Produces: `button.okf-tool.okf-palette-open` (first in `#okf-tools`); `div.okf-palette-backdrop[hidden]` > `div.okf-palette[role=dialog][aria-modal=true]` containing `input#okf-palette-input[role=combobox]`, `ul#okf-palette-list[role=listbox]` of `li.okf-palette-option[role=option][aria-selected]` > `span.okf-palette-title` + `span.okf-palette-id`, `p#okf-palette-status[role=status]`, `button.okf-palette-close`; the cancelable document event `okf:navigate` with `detail.href`.

- [ ] **Step 1: Write the failing harness cases**

Insert before `// --- end of async checks ---` in `run.js`:

```js
function paletteOptions(window) {
  return Array.from(window.document.querySelectorAll("#okf-palette-list [role=option]"));
}

function optionId(option) {
  return option.querySelector(".okf-palette-id").textContent;
}

checkAsync("palette: shortcuts open it only outside editable fields and IME composition", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const backdrop = doc.querySelector(".okf-palette-backdrop");
  key(window, doc.body, { key: "k", ctrlKey: true, isComposing: true });
  assert(backdrop.hidden, "opened during IME composition");
  const filter = doc.getElementById("okf-tree-filter");
  filter.focus();
  key(window, filter, { key: "/" });
  assert(backdrop.hidden, "'/' typed in the tree filter opened the palette");
  key(window, filter, { key: "k", ctrlKey: true });
  assert(backdrop.hidden, "Ctrl+K in an editable field opened the palette");
  filter.blur();
  key(window, doc.body, { key: "k", ctrlKey: true, altKey: true });
  assert(backdrop.hidden, "Ctrl+Alt+K opened the palette");
  const event = key(window, doc.body, { key: "k", ctrlKey: true });
  assert(!backdrop.hidden && event.defaultPrevented, "Ctrl+K did not open the palette, or was not prevented");
  assert(doc.activeElement === doc.getElementById("okf-palette-input"), "focus did not move into the field");
});

checkAsync("palette: modal focus, arrows, Enter, Escape and focus return", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  const opener = doc.querySelector(".okf-palette-open");
  opener.focus();
  opener.click();
  const input = doc.getElementById("okf-palette-input");
  const close = doc.querySelector(".okf-palette-close");
  assert(doc.activeElement === input, "focus is not in the field on open");
  key(window, input, { key: "Tab" });
  assert(doc.activeElement === close, "Tab did not move to Close");
  key(window, close, { key: "Tab" });
  assert(doc.activeElement === input, "Tab escaped the modal");
  type(window, input, "o");
  assert(paletteOptions(window).length > 1, "this case needs several results");
  assert(paletteOptions(window)[0].getAttribute("aria-selected") === "true", "the first option is not active");
  key(window, input, { key: "ArrowDown" });
  const second = paletteOptions(window)[1];
  assert(second.getAttribute("aria-selected") === "true", "ArrowDown did not move");
  assert(input.getAttribute("aria-activedescendant") === second.id, "aria-activedescendant not updated");
  assert(/matching concept/.test(doc.getElementById("okf-palette-status").textContent), "the result count is not announced");
  let navigated = null;
  doc.addEventListener("okf:navigate", (e) => { navigated = e.detail.href; e.preventDefault(); });
  key(window, input, { key: "Enter" });
  assert(navigated === `${optionId(second)}.html`, `navigated to ${navigated}`);
  key(window, input, { key: "Escape" });
  assert(doc.querySelector(".okf-palette-backdrop").hidden, "Escape did not close");
  assert(doc.activeElement === opener, "focus did not return to the opener");
});

checkAsync("palette: the active concept survives a narrower query, else the first option is active", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  const input = doc.getElementById("okf-palette-input");
  const selected = () => paletteOptions(window).find((o) => o.getAttribute("aria-selected") === "true");
  type(window, input, "o");
  const target = paletteOptions(window).map(optionId).indexOf("toString");
  assert(target > 0, "this case needs toString after the first result");
  for (let k = 0; k < target; k++) { key(window, input, { key: "ArrowDown" }); }
  type(window, input, "to");
  assert(optionId(selected()) === "toString", `active after narrowing: ${optionId(selected())}`);
  type(window, input, "bro");
  assert(selected() === paletteOptions(window)[0], "the first option is not active once the active one is filtered out");
});

checkAsync("palette: ids naming Object.prototype members are found as concepts", async () => {
  const window = await openPage("index.html");
  window.document.querySelector(".okf-palette-open").click();
  type(window, window.document.getElementById("okf-palette-input"), "constructor");
  assert(optionId(paletteOptions(window)[0]) === "constructor", "constructor is not the first result");
});

checkAsync("palette: keys typed during IME composition never act on the open palette", async () => {
  const window = await openPage("index.html");
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  const input = doc.getElementById("okf-palette-input");
  type(window, input, "o");
  let navigated = false;
  doc.addEventListener("okf:navigate", (e) => { navigated = true; e.preventDefault(); });
  key(window, input, { key: "ArrowDown", isComposing: true });
  assert(paletteOptions(window)[0].getAttribute("aria-selected") === "true", "ArrowDown moved during composition");
  key(window, input, { key: "Enter", isComposing: true });
  assert(!navigated, "Enter navigated during composition");
  key(window, input, { key: "Escape", isComposing: true });
  assert(!doc.querySelector(".okf-palette-backdrop").hidden, "Escape closed the palette during composition");
});

checkAsync("palette: the active option is scrolled into view", async () => {
  const window = await openPage("index.html");
  // jsdom has no scrollIntoView: a probe records the calls. The visual
  // result is checked in ACCEPTANCE.md.
  const calls = [];
  window.Element.prototype.scrollIntoView = function (options) {
    calls.push({ id: this.id, block: options && options.block });
  };
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  const input = doc.getElementById("okf-palette-input");
  type(window, input, "o");
  key(window, input, { key: "ArrowDown" });
  const last = calls[calls.length - 1];
  assert(last && last.id === "okf-palette-opt-1" && last.block === "nearest", `last scroll: ${JSON.stringify(last)}`);
});

checkAsync("palette: every match is listed and reachable, with no cap", async () => {
  const concepts = [];
  for (let k = 0; k < 60; k++) {
    const id = `item-${String(k).padStart(2, "0")}`;
    concepts.push({ id, title: `Item ${k}`, type: "Note", tags: [], path: `${id}.html`, trust: "unverified", staleAfterMs: null, staleAfterDate: null });
  }
  const source = `window.OKF_INDEX = ${JSON.stringify({ version: 1, concepts, ghosts: [], edges: [], tree: [] })};`;
  const window = await openPage("index.html", { override: { "assets/okf-index.js": source } });
  const doc = window.document;
  doc.querySelector(".okf-palette-open").click();
  const input = doc.getElementById("okf-palette-input");
  type(window, input, "item");
  assert(paletteOptions(window).length === 60, `listed ${paletteOptions(window).length} of 60`);
  const status = doc.getElementById("okf-palette-status").textContent;
  assert(status === "60 matching concepts", `status: ${status}`);
  key(window, input, { key: "ArrowUp" });
  const last = paletteOptions(window)[59];
  assert(optionId(last) === "item-59" && last.getAttribute("aria-selected") === "true", "the 60th match is not reachable");
});
```

- [ ] **Step 2: Run the harness to verify they fail**

Run: `cd tools/viewer-security-check && npm test`
Expected: the seven new cases FAIL (`Cannot read properties of null (reading 'hidden')` or `… 'click'`); exit code 1.

- [ ] **Step 3: Write `okf-palette.js`**

Replace `src/OKF4net.Viewer/Assets/okf-palette.js` with:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// The "Jump to" palette (spec §4.6, §8): a modal dialog opened by a visible
// button, Ctrl+K or "/". Results come from OkfSite.rank (fixed tiers, no
// weights -- not ConceptSearch, and never the body text). Navigating
// dispatches a cancelable "okf:navigate" event first, so a host -- or the
// test harness -- can observe or veto it.
(function () {
  "use strict";
  var site = window.OkfSite;
  var tools = document.getElementById("okf-tools");
  if (!site || !tools) { return; }
  var index = site.readIndex(window);
  if (!index) { return; }
  var root = site.rootOf(document);

  function el(tag, className, text) {
    return site.element(document, tag, className, text);
  }

  var opener = el("button", "okf-tool okf-palette-open", "Jump to...");
  opener.type = "button";
  opener.setAttribute("aria-haspopup", "dialog");
  opener.setAttribute("aria-keyshortcuts", "Control+K /");
  tools.insertBefore(opener, tools.firstChild);

  var backdrop = el("div", "okf-palette-backdrop");
  backdrop.hidden = true;
  var dialog = el("div", "okf-palette");
  dialog.setAttribute("role", "dialog");
  dialog.setAttribute("aria-modal", "true");
  dialog.setAttribute("aria-labelledby", "okf-palette-title");
  var title = el("h2", "okf-sr", "Jump to a concept");
  title.id = "okf-palette-title";
  var input = document.createElement("input");
  input.type = "text";
  input.id = "okf-palette-input";
  input.className = "okf-palette-input";
  input.setAttribute("role", "combobox");
  input.setAttribute("aria-expanded", "true");
  input.setAttribute("aria-controls", "okf-palette-list");
  input.setAttribute("aria-autocomplete", "list");
  input.setAttribute("aria-labelledby", "okf-palette-title");
  input.setAttribute("autocomplete", "off");
  var list = el("ul", "okf-palette-list");
  list.id = "okf-palette-list";
  list.setAttribute("role", "listbox");
  list.setAttribute("aria-label", "Matching concepts");
  var status = el("p", "okf-palette-status");
  status.id = "okf-palette-status";
  status.setAttribute("role", "status");
  var close = el("button", "okf-tool okf-palette-close", "Close");
  close.type = "button";
  dialog.appendChild(title);
  dialog.appendChild(input);
  dialog.appendChild(list);
  dialog.appendChild(status);
  dialog.appendChild(close);
  backdrop.appendChild(dialog);
  document.body.appendChild(backdrop);

  var shown = [];
  var active = -1;
  var returnFocus = null;

  function render() {
    while (list.firstChild) { list.removeChild(list.firstChild); }
    for (var k = 0; k < shown.length; k++) {
      var concept = index.concepts[shown[k]];
      var option = el("li", "okf-palette-option");
      option.id = "okf-palette-opt-" + k;
      option.setAttribute("role", "option");
      option.setAttribute("aria-selected", k === active ? "true" : "false");
      option.appendChild(el("span", "okf-palette-title", concept.title));
      option.appendChild(el("span", "okf-palette-id", concept.id));
      option.addEventListener("click", activate.bind(null, k));
      list.appendChild(option);
    }
    if (active >= 0) {
      input.setAttribute("aria-activedescendant", "okf-palette-opt-" + active);
      // aria-activedescendant does not move DOM focus, so nothing scrolls
      // the list by itself: keep the active option visible.
      var activeOption = list.children[active];
      if (activeOption && typeof activeOption.scrollIntoView === "function") {
        activeOption.scrollIntoView({ block: "nearest" });
      }
    } else {
      input.removeAttribute("aria-activedescendant");
    }
  }

  function update() {
    var previous = active >= 0 ? shown[active] : -1;
    // Every match is listed and reachable: no cap (owner decision, 2026-10-06).
    shown = site.rank(index, input.value);
    // Keep the active concept when it survives the new query, else the first
    // option becomes active (spec §8).
    active = shown.indexOf(previous);
    if (active === -1) { active = shown.length > 0 ? 0 : -1; }
    render();
    if (site.normalize(input.value) === "") {
      status.textContent = "";
    } else if (shown.length === 0) {
      status.textContent = "No matching concept";
    } else {
      status.textContent = shown.length + (shown.length === 1 ? " matching concept" : " matching concepts");
    }
  }

  function move(delta) {
    if (shown.length === 0) { return; }
    active = (active + delta + shown.length) % shown.length;
    render();
  }

  function activate(k) {
    if (k < 0 || k >= shown.length) { return; }
    var href = site.resolve(root, index.concepts[shown[k]].path);
    var event = new CustomEvent("okf:navigate", { cancelable: true, detail: { href: href } });
    if (document.dispatchEvent(event)) { window.location.assign(href); }
  }

  function open() {
    if (!backdrop.hidden) { return; }
    returnFocus = document.activeElement;
    input.value = "";
    active = -1;
    update();
    backdrop.hidden = false;
    input.focus();
  }

  function closePalette() {
    backdrop.hidden = true;
    var target = returnFocus && typeof returnFocus.focus === "function" && document.contains(returnFocus) && returnFocus !== document.body
      ? returnFocus
      : opener;
    returnFocus = null;
    target.focus();
  }

  opener.addEventListener("click", open);
  close.addEventListener("click", closePalette);
  backdrop.addEventListener("click", function (e) { if (e.target === backdrop) { closePalette(); } });
  input.addEventListener("input", update);

  dialog.addEventListener("keydown", function (e) {
    // Keys that confirm or cancel an IME composition belong to the IME, not
    // to the palette (Enter would navigate, Escape would close).
    if (e.isComposing || e.keyCode === 229) { return; }
    if (e.key === "Escape") {
      e.preventDefault();
      closePalette();
    } else if (e.key === "ArrowDown") {
      e.preventDefault();
      move(1);
    } else if (e.key === "ArrowUp") {
      e.preventDefault();
      move(-1);
    } else if (e.key === "Enter" && e.target === input) {
      e.preventDefault();
      activate(active);
    } else if (e.key === "Tab") {
      // Focus stays inside the modal: its only stops are the field and Close.
      e.preventDefault();
      (document.activeElement === input ? close : input).focus();
    }
  });

  function isEditable(target) {
    if (!target || target.nodeType !== 1) { return false; }
    var tag = target.tagName;
    return tag === "INPUT" || tag === "TEXTAREA" || tag === "SELECT" || target.isContentEditable === true;
  }

  // Ctrl+K and "/" are also browser shortcuts (Chrome, Firefox): they are
  // taken only outside editable fields, without other modifiers and outside
  // IME composition, and prevented only when taken (spec §8).
  document.addEventListener("keydown", function (e) {
    if (e.defaultPrevented || e.isComposing || e.keyCode === 229 || !backdrop.hidden) { return; }
    if (isEditable(e.target)) { return; }
    var ctrlK = e.ctrlKey && !e.altKey && !e.metaKey && !e.shiftKey && (e.key === "k" || e.key === "K");
    var slash = e.key === "/" && !e.ctrlKey && !e.altKey && !e.metaKey;
    if (ctrlK || slash) {
      e.preventDefault();
      open();
    }
  });
})();
```

- [ ] **Step 4: Style the palette**

Append to `src/OKF4net.Viewer/Assets/viewer.css`:

```css
.okf-palette-backdrop {
  position: fixed; inset: 0; z-index: 10; display: flex; align-items: flex-start; justify-content: center;
  padding: 96px 16px 16px; background: rgba(16, 16, 20, .4);
}
.okf-palette-backdrop[hidden] { display: none; }
.okf-palette { width: 100%; max-width: 600px; background: var(--white); color: var(--ink); border: 1px solid var(--ink); }
.okf-palette-input {
  width: 100%; min-height: 52px; padding: 0 16px; font: inherit; font-size: 17px;
  border: 0; border-bottom: 1px solid var(--hair); background: transparent; color: var(--ink);
}
.okf-palette-list { list-style: none; margin: 0; padding: 0; max-height: 50vh; overflow-y: auto; }
.okf-palette-option {
  display: flex; justify-content: space-between; gap: 12px; padding: 12px 16px;
  cursor: pointer; border-left: 3px solid transparent;
}
.okf-palette-option[aria-selected="true"] { background: var(--blue-soft); border-left-color: var(--blue); }
.okf-palette-id { font-family: var(--mono); font-size: 12px; color: var(--gray); }
.okf-palette-status { margin: 0; padding: 8px 16px; font-size: 13px; color: var(--gray); }
.okf-palette-close { margin: 0 16px 12px; }
```

- [ ] **Step 5: Run the harness to verify everything passes**

Run: `cd tools/viewer-security-check && npm test`
Expected: `… passed, 0 failed`, exit code 0.

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Viewer/Assets/okf-palette.js src/OKF4net.Viewer/Assets/viewer.css tools/viewer-security-check/run.js
git commit -m "feat(viewer): Jump to palette with fixed-tier ranking and modal keyboard contract"
```

---

### Task 8: Contents, heading anchors and fragments

**Files:**
- Modify: `src/OKF4net.Viewer/Assets/okf-toc.js` (replace the skeleton)
- Modify: `src/OKF4net.Viewer/Assets/viewer.css` (append)
- Modify: `tools/viewer-security-check/run.js` (cases before `// --- end of async checks ---`)

**Interfaces:**
- Consumes: `OkfSite.uniqueSlugs`, `.fragmentCandidates`, `.HEADING_PREFIX` (Task 5); `#okf-body` rendered by `viewer.js`; `#okf-toc`, `#okf-context` (Task 3).
- Produces: every heading of `#okf-body` gets `id="okf-h-<slug>"` and `tabindex="-1"`; `#okf-toc ul` lists the `h2`/`h3` (an `h3` item has class `okf-toc-sub`); `#okf-toc` and `#okf-context` unhidden when the list is not empty.

- [ ] **Step 1: Write the failing harness cases**

Insert before `// --- end of async checks ---` in `run.js`:

```js
checkAsync("headings get generated ids only, the contents list them, no content id survives", async () => {
  const window = await openPage("foo.html");
  const doc = window.document;
  const ids = Array.from(doc.querySelectorAll("#okf-body h1, #okf-body h2, #okf-body h3"), (h) => h.id);
  assert(JSON.stringify(ids.slice(0, 3)) === JSON.stringify(["okf-h-usage", "okf-h-usage-1", "okf-h-usage-1-1"]), `ids: ${ids}`);
  assert(ids.every((id) => id.startsWith("okf-h-")), `an id escaped the prefix: ${ids}`);
  assert(doc.getElementById("OKF_INDEX") === null, "an id from bundle content survived");
  assert(doc.querySelectorAll("#okf-body [name]").length === 0, "a name attribute from bundle content survived");
  const toc = doc.getElementById("okf-toc");
  assert(!toc.hidden && !doc.getElementById("okf-context").hidden, "the contents list was not shown");
  assert(toc.querySelector("a").getAttribute("href") === "#okf-h-usage", "the first contents link is wrong");
});

checkAsync("an author fragment resolves to the generated heading, on load and on click, as a real navigation", async () => {
  const opened = await openPage("foo/bar.html", { hash: "#usage" });
  const focused = opened.document.activeElement;
  assert(focused && focused.id === "okf-h-usage", `focus on load: ${focused && focused.id}`);
  const window = await openPage("foo.html");
  const link = Array.from(window.document.querySelectorAll("#okf-body a")).find((a) => a.getAttribute("href") === "#usage");
  assert(link, "the fixture lost its #usage link");
  const before = window.history.length;
  const changed = new Promise((resolve) => window.addEventListener("hashchange", resolve, { once: true }));
  const event = new window.MouseEvent("click", { bubbles: true, cancelable: true, button: 0 });
  link.dispatchEvent(event);
  assert(event.defaultPrevented, "the click was left to the browser, which finds no element 'usage'");
  await changed;
  assert(window.location.hash === "#okf-h-usage", `URL fragment after click: ${window.location.hash}`);
  assert(window.history.length === before + 1, `history length ${window.history.length}, expected ${before + 1}`);
  assert(window.document.activeElement.id === "okf-h-usage", `focus after click: ${window.document.activeElement.id}`);
  const modified = new window.MouseEvent("click", { bubbles: true, cancelable: true, button: 0, ctrlKey: true });
  link.dispatchEvent(modified);
  assert(!modified.defaultPrevented, "a Ctrl+click was taken over instead of left to the browser");
});

checkAsync("without okf-index.js the page renders, the explorer stays hidden and nothing is clobbered", async () => {
  const window = await openPage("foo.html", { blocked: ["assets/okf-index.js"] });
  const doc = window.document;
  assert(doc.getElementById("okf-explorer").hidden, "the explorer rendered without an index");
  assert(doc.querySelector(".okf-palette-open") === null, "the palette rendered without an index");
  assert(window.OkfSite.readIndex(window) === null, "readIndex accepted something that is not the index");
  assert(doc.getElementById("OKF_INDEX") === null, "bundle content created an element named OKF_INDEX");
  assert(doc.getElementById("okf-body").textContent.includes("first"), "the body did not render");
  assert(!doc.getElementById("okf-toc").hidden, "the contents list depends on the index");
});

checkAsync("explorer, palette and contents render hostile titles as inert text", async () => {
  const window = await openPage("foo.html");
  const doc = window.document;
  key(window, doc.body, { key: "/" });
  type(window, doc.getElementById("okf-palette-input"), "foo");
  for (const id of ["okf-explorer", "okf-palette-list", "okf-toc"]) {
    const root = doc.getElementById(id);
    assert(root.querySelectorAll("img, script, svg, iframe, object").length === 0, `markup from bundle text became live in #${id}`);
  }
  assert(doc.getElementById("okf-explorer").textContent.includes("<img"), "the hostile title was dropped instead of shown as text");
  assert(doc.getElementById("okf-palette-list").textContent.includes("<img"), "the palette dropped the hostile title");
  assert(doc.getElementById("okf-toc").textContent.includes("<img"), "the contents dropped the hostile heading text");
  assert(doc.querySelectorAll("[onerror]").length === 0, "an onerror attribute reached the page");
  assert(window.__pwned === undefined, "a hostile title executed");
});
```

- [ ] **Step 2: Run the harness to verify they fail**

Run: `cd tools/viewer-security-check && npm test`
Expected: the first two new cases and the fourth FAIL (no generated ids, contents hidden); the third may already pass. Exit code 1.

- [ ] **Step 3: Write `okf-toc.js`**

Replace `src/OKF4net.Viewer/Assets/okf-toc.js` with:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Heading anchors, the "On this page" list and fragment resolution (spec §5).
// Runs after viewer.js has rendered and sanitized #okf-body, and does not
// need the site index. Ids are GENERATED here, after sanitization: always
// "okf-h-" + a slug of letters, digits and hyphens. No id or name from bundle
// content is ever admitted (the sanitizer strips them), so content cannot
// clobber a global such as window.OKF_INDEX.
(function () {
  "use strict";
  var site = window.OkfSite;
  var body = document.getElementById("okf-body");
  if (!site || !body) { return; }

  var headings = body.querySelectorAll("h1, h2, h3, h4, h5, h6");
  var texts = [];
  for (var i = 0; i < headings.length; i++) { texts.push(headings[i].textContent); }
  var slugs = site.uniqueSlugs(texts);
  for (var j = 0; j < headings.length; j++) {
    headings[j].setAttribute("id", site.HEADING_PREFIX + slugs[j]);
    headings[j].setAttribute("tabindex", "-1");
  }

  var toc = document.getElementById("okf-toc");
  var list = toc ? toc.querySelector("ul") : null;
  if (list) {
    var count = 0;
    for (var k = 0; k < headings.length; k++) {
      var level = headings[k].tagName;
      if (level !== "H2" && level !== "H3") { continue; }
      var item = site.element(document, "li", level === "H3" ? "okf-toc-sub" : "");
      var link = site.element(document, "a", "", headings[k].textContent);
      link.setAttribute("href", "#" + headings[k].id);
      item.appendChild(link);
      list.appendChild(item);
      count++;
    }
    if (count > 0) {
      toc.hidden = false;
      var context = document.getElementById("okf-context");
      if (context) { context.hidden = false; }
    }
  }

  // The generated heading a fragment designates, or null. Only elements
  // inside #okf-body qualify.
  function targetOf(hash) {
    var candidates = site.fragmentCandidates(hash);
    for (var c = 0; c < candidates.length; c++) {
      var target = document.getElementById(candidates[c]);
      if (target && body.contains(target)) { return target; }
    }
    return null;
  }

  // Moves reading focus to that heading.
  function go(hash) {
    var target = targetOf(hash);
    if (!target) { return false; }
    if (typeof target.scrollIntoView === "function") { target.scrollIntoView(); }
    target.focus();
    return true;
  }

  // Author links written against the heading text ("#usage") predate the
  // generated ids. A plain click becomes a real fragment navigation to the
  // generated id -- URL, history entry and Back/Forward stay native -- and
  // hashchange then moves the focus. Modified clicks (new tab, new window)
  // are left to the browser: the new page resolves the fragment on load.
  body.addEventListener("click", function (e) {
    if (e.defaultPrevented || e.button !== 0 || e.ctrlKey || e.metaKey || e.shiftKey || e.altKey) { return; }
    var a = e.target && typeof e.target.closest === "function" ? e.target.closest("a") : null;
    if (!a || !body.contains(a)) { return; }
    var href = a.getAttribute("href");
    if (!href || href.charAt(0) !== "#") { return; }
    var target = targetOf(href);
    if (!target) { return; }
    e.preventDefault();
    if (window.location.hash === "#" + target.id) {
      go(window.location.hash);
    } else {
      window.location.hash = target.id;
    }
  });
  window.addEventListener("hashchange", function () { go(window.location.hash); });
  if (window.location.hash) { go(window.location.hash); }
})();
```

- [ ] **Step 4: Style the context panel**

Append to `src/OKF4net.Viewer/Assets/viewer.css`:

```css
.okf-toc h2, .okf-backlinks h2 {
  font-family: var(--mono); font-size: 11px; font-weight: 400; letter-spacing: .06em;
  text-transform: uppercase; color: var(--gray); margin: 0 0 8px;
}
.okf-toc ul, .okf-backlinks ul { list-style: none; margin: 0 0 24px; padding: 0; font-size: 14px; }
.okf-toc li { padding: 4px 0 4px 10px; border-left: 2px solid var(--hair); }
.okf-toc li.okf-toc-sub { padding-left: 22px; }
.okf-backlinks li { padding: 6px 0; border-bottom: 1px solid var(--hair); }
#okf-body [tabindex="-1"]:focus { outline: 2px solid var(--blue); outline-offset: 4px; }
```

- [ ] **Step 5: Run the harness, then the whole .NET suite**

Run: `cd tools/viewer-security-check && npm test`
Expected: `… passed, 0 failed`, exit code 0.

Run (repository root): `dotnet test OKF4net.sln`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Viewer/Assets/okf-toc.js src/OKF4net.Viewer/Assets/viewer.css tools/viewer-security-check/run.js
git commit -m "feat(viewer): generated heading anchors, contents list and author fragments"
```

---

### Task 9: AOT smoke check, documentation and acceptance

**Files:**
- Modify: `.github/workflows/ci.yml` (`aot-publish` job, step `Run published okf-render`)
- Modify: `CLAUDE.md`, `README.md`, `ROADMAP.md`, `CHANGELOG.md`
- Modify: `tools/viewer-security-check/README.md`
- Create: `tools/viewer-security-check/ACCEPTANCE.md`
- Create: `tools/viewer-security-check/check-index.js`

**Interfaces:**
- Consumes: everything above.
- Produces: the written acceptance checklist (spec §7, A14) and the documentation the spec requires with P1 (§9: `CLAUDE.md` exception A2 and "no full-text search", README, ROADMAP A4).

- [ ] **Step 1: Check that the index the NATIVE binary writes executes**

The jsdom harness only runs a site generated by a regular `dotnet run` build; the spec (§2.1) asks the AOT smoke test for presence **and loading** of the index. A prefix check alone would pass a truncated file, so the file is executed.

Create `tools/viewer-security-check/check-index.js`:

```js
// SPDX-License-Identifier: LGPL-3.0-or-later
//
// Executes an okf-index.js written by okf-render and checks that it defines
// a usable site index. CI runs it on the file the NATIVE AOT binary writes:
// the jsdom harness (run.js) only exercises a site from a regular build.
"use strict";
const path = require("path");

const file = path.resolve(process.argv[2] || "");
global.window = {};
require(file);
const index = global.window.OKF_INDEX;
const ok = Boolean(index)
  && Array.isArray(index.concepts) && index.concepts.length > 0
  && Array.isArray(index.ghosts) && Array.isArray(index.edges)
  && Array.isArray(index.tree) && index.tree.length > 0
  && index.concepts.every((c) => typeof c.id === "string" && c.path === `${c.id}.html`);
if (!ok) {
  console.error(`${file}: not a usable site index`);
  process.exit(1);
}
console.log(`${file}: ${index.concepts.length} concepts, ${index.edges.length} edges`);
```

In `.github/workflows/ci.yml`, job `aot-publish`, insert right after that job's `actions/setup-dotnet@v6` step:

```yaml
      # check-index.js executes the okf-index.js the native okf-render writes.
      - uses: actions/setup-node@v7
        with:
          node-version: 22

```

and in its step `Run published okf-render`, replace

```powershell
          Write-Host "OK: okf-render wrote $outDir/index.html"
```

with

```powershell
          $indexScript = "$outDir/assets/okf-index.js"
          if (-not (Test-Path $indexScript)) { throw "okf-render did not write $indexScript" }
          node tools/viewer-security-check/check-index.js $indexScript
          if ($LASTEXITCODE -ne 0) { throw "$indexScript does not execute as a usable site index" }
          Write-Host "OK: okf-render wrote $outDir/index.html and an executable $indexScript"
```

Check it locally on a regular build before committing:

```bash
dotnet run --project src/OKF4net.Render -c Release -- tests/fixtures/appendix_a --out "$TEMP/okf-appendix-site"
node tools/viewer-security-check/check-index.js "$TEMP/okf-appendix-site/assets/okf-index.js"
```

Expected: one line `…okf-index.js: N concepts, E edges` with N > 0, exit code 0. Then truncate a copy (`head -c 200 "$TEMP/okf-appendix-site/assets/okf-index.js" > "$TEMP/truncated.js"`) and run the check on it: expected a non-zero exit code.

- [ ] **Step 2: Write the acceptance checklist**

Create `tools/viewer-security-check/ACCEPTANCE.md`:

```markdown
# Interactive viewer — manual acceptance

jsdom does no layout and implements no browser shortcuts, so what follows is
checked by hand, on every slice (spec `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md`, §7, A14),
in the latest desktop Chrome, Edge and Firefox (Safari: best effort), opening
the generated files directly (`file://`), on two sites:

    dotnet run --project src/OKF4net.Render -c Release -- bundles/acme_retail --out <tmp>/acme-site
    dotnet run --project producers/src/OkfProducer.Cli -c Release -- generate --repo . --out <tmp>/okf4net-bundle --no-msbuild
    dotnet run --project src/OKF4net.Render -c Release -- <tmp>/okf4net-bundle --out <tmp>/okf4net-site

Record the date, browser versions and any failure in the pull request.

## P1

- [ ] A page of the OKF4net site opens without a visible stall; scrolling the explorer stays smooth.
- [ ] Explorer: the current page is highlighted and its ancestors are open; a folder that is also a page opens on its name and expands on its arrow; the filter keeps ancestors of matches.
- [ ] Links from a page three levels deep land on the right pages.
- [ ] Palette: the "Jump to..." button opens it; Ctrl+K and `/` open it from the page body (note what each browser does with these keys when the page has focus and when the address bar has it); Tab stays inside; Escape closes and focus returns; arrows and Enter navigate.
- [ ] Palette with many results (query `o` on the OKF4net site): arrowing past the bottom of the list keeps the active option visible; every result is reachable.
- [ ] Palette with an IME (e.g. Windows Japanese input): confirming a composition with Enter does not navigate, cancelling it with Escape does not close the palette.
- [ ] A screen reader (NVDA or Narrator) announces the palette as a dialog, the active option and the result count, and the theme button's pressed state, including after the system theme changes.
- [ ] Theme: the toggle switches; reloading and opening another page keeps the choice where the browser shares storage between `file://` pages, and never flashes the other theme on load.
- [ ] Contrast is readable in both themes (text, badges, the active palette option).
- [ ] Contents list: clicking an entry lands on the heading; Back and Forward return through the visited headings.
- [ ] Opening `<page>.html#<heading text>` (an author fragment) lands on that heading.
- [ ] At 390 px wide, the zones stack without horizontal scrolling and the palette fits.
```

- [ ] **Step 3: Document the harness changes**

In `tools/viewer-security-check/README.md`, append:

```markdown

## Interactive viewer cases

The second half of `run.js` covers the interactive viewer scripts
(`okf-site.js`, `okf-theme.js`, `okf-explorer.js`, `okf-palette.js`,
`okf-toc.js`). Helper cases load `okf-site.js` into a bare window. Page cases
load pages of a site generated by the real `okf-render` from
`fixtures/hostile-bundle/` — hostile titles, ids named `__proto__`,
`constructor` and `toString`, a concept that is also a folder, a broken link,
repeated headings, a clobbering attempt — so they execute the real
`okf-index.js` the generator writes. `npm test` runs `pretest` first, which
regenerates that site into `.generated/` with `dotnet run`, so the harness now
needs the .NET SDK. Page cases are async and are awaited before the summary.

What the harness cannot check (layout, browser shortcuts, focus rings,
contrast, absence of a theme flash) is in `ACCEPTANCE.md`.

`check-index.js` executes one `okf-index.js` and checks it defines a usable
index. CI's `aot-publish` job runs it on the file the **native** `okf-render`
writes, which the harness above never sees.
```

- [ ] **Step 4: Update `CLAUDE.md`**

In the `src/OKF4net.Viewer/` bullet:

Replace

```
Three units: `SiteModel` (pure `Bundle` → display-model projection), `HtmlWriter` (the only I/O), `ViewerAssets` (embedded CSS/JS).
```

with

```
Units: `SiteModel` (pure `Bundle` → display-model projection), `SiteIndex` + `IndexScript` (pure `Bundle` → site index → `assets/okf-index.js`, the data the interactive scripts read), `HtmlWriter` (the only I/O), `ViewerAssets` (embedded CSS/JS). The interactive scripts (`okf-theme.js` in `<head>`; `okf-site.js`, `okf-explorer.js`, `okf-palette.js`, `okf-toc.js` after `viewer.js`) are classic scripts that never touch `viewer.js`'s `{ body, links }` contract and insert bundle text through `textContent` only; their guard is the same jsdom harness, which loads a site generated from `tools/viewer-security-check/fixtures/hostile-bundle/` (design: `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md`). **One deliberate exception to "the audit computation is not forked"**: the browser compares `Date.now()` with each concept's `staleAfterMs` (§5.5 deadline rounded up to a whole millisecond in C#) so staleness is shown as of reading, not of generation; everything else about trust and staleness comes from `ConceptAudit`.
```

Replace

```
No full-text search by design: a static site has no process to run `ConceptSearch` in, and mirroring its weights in JS would fork the scorer.
```

with

```
No full-text search by design: a static site has no process to run `ConceptSearch` in, and mirroring its weights in JS would fork the scorer. The "Jump to" palette is not search: it matches titles, ids and tags by fixed tiers (exact id, prefix, substring, exact tag) and never reads bodies.
```

- [ ] **Step 5: Update `README.md`, `ROADMAP.md` and `CHANGELOG.md`**

`README.md`, section `okf-render`: replace

```
The generated site is self-contained and opens straight off the filesystem —
no server needed. It is read-only, and has no full-text search: a static site
```

with

```
The generated site is self-contained and opens straight off the filesystem —
no server needed. Every page carries a tree explorer of the bundle (with trust
and staleness badges), a "Jump to" palette (Ctrl+K or `/`, matching titles, ids
and tags), a light/dark toggle, and a side panel with the page's contents and
backlinks. It is read-only, and has no full-text search: a static site
```

`ROADMAP.md`, section `## Later`: delete the bullet that starts `- **Interactive cross-link graph explorer.**` (through `samples/` entry.`), and append to the bullet `- Bundle viewer: **static render shipped** …` (before its `- **The client-side XSS defense…` sub-bullet) the sentence:

```
  Interactive navigation is being brought into `okf-render` itself
  (design `docs/superpowers/specs/2026-10-06-okf-viewer-interactive-design.md`):
  P1 — explorer, palette, contents — has landed; P2 (local link graph) and P3
  (global link graph with facets) follow, which replaces the separate
  "interactive cross-link graph explorer" project this roadmap used to list.
```

`CHANGELOG.md`, under `## [Unreleased]` → `### Added`, add as the first entry:

```markdown
- **Interactive `okf-render` pages (P1).** Every generated page now has a tree
  explorer of the bundle (a concept that is also a folder opens and expands
  separately; trust tier and staleness badges from `ConceptAudit`, staleness
  evaluated when the page is read), a "Jump to" palette (Ctrl+K or `/`,
  fixed-tier matching on titles, ids and tags — not full-text search), a
  light/dark toggle, and a side panel with the contents and the backlinks.
  Headings get generated `okf-h-` anchors, so author links such as `#usage`
  now land. The data comes from a new generated `assets/okf-index.js`
  (`SiteIndex`, `IndexScript`); `viewer.js` and the OKF4net core API are
  unchanged. The jsdom harness now loads a site generated from a hostile
  fixture bundle and needs the .NET SDK.
```

- [ ] **Step 6: Full verification**

Run, from the repository root:

```bash
dotnet build OKF4net.sln
dotnet test OKF4net.sln
dotnet format OKF4net.sln --verify-no-changes
cd tools/viewer-security-check && npm test && cd ../..
dotnet publish src/OKF4net.Render -c Release
```

Expected: build with 0 warnings, all tests PASS, format clean, harness `… passed, 0 failed`, AOT publish succeeds. Then render `bundles/acme_retail` with the published binary and confirm `assets/okf-index.js` starts with `window.OKF_INDEX = {"version":1,`.

- [ ] **Step 7: Run the acceptance checklist**

Walk `tools/viewer-security-check/ACCEPTANCE.md` § P1 in Chrome, Edge and Firefox. Record results for the pull request; any failure is fixed before the branch is offered for merge.

- [ ] **Step 8: Commit**

```bash
git add .github/workflows/ci.yml CLAUDE.md README.md ROADMAP.md CHANGELOG.md tools/viewer-security-check/README.md tools/viewer-security-check/ACCEPTANCE.md tools/viewer-security-check/check-index.js
git commit -m "docs(viewer): P1 interactive viewer in CLAUDE.md, README, ROADMAP, CHANGELOG; AOT index check; acceptance checklist"
```
