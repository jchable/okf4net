# Bundle Viewer — Static Render (`okf render`) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add an `okf render <bundle> --out <dir>` verb that generates a self-contained, browsable HTML site from an OKF bundle.

**Architecture:** A new zero-dependency `src/OKF4net.Viewer/` project splits into three units: `SiteModel` (pure `Bundle` → display-model projection, no I/O), `HtmlWriter` (model → files, the only I/O), and `ViewerAssets` (embedded CSS/JS). Markdown is rendered **client-side** by a vendored copy of marked; the C# side emits page shells carrying a JSON payload (raw markdown + link table). The CLI verb is a thin delegate following the existing `CmdGraph` pattern.

**Tech Stack:** C# / net10.0, BCL only. Vendored marked (MIT) as an embedded resource. xunit for tests.

**Spec:** `docs/superpowers/specs/2026-08-20-okf-bundle-viewer-static-render-design.md`

## Global Constraints

- **Zero third-party runtime dependencies** in `src/OKF4net.Viewer/`: no `PackageReference` at all; a single `ProjectReference` to `OKF4net`. (`CLAUDE.md`, Hard rules.)
- Every new source file starts with `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- File-scoped namespaces, XML doc comments on **all** public API, nullable enabled. `TreatWarningsAsErrors` is on — warnings break the build.
- Do **not** redeclare `<Version>` in the new csproj; it is inherited from the root `Directory.Build.props`.
- **Never touch `tests/fixtures/`.** The HTML output is not spec-covered behaviour, so no golden fixture is involved in this work.
- `dotnet format OKF4net.sln` must leave no changes (CI runs `--verify-no-changes`).
- The CLI is published Native AOT — no reflection-based serialization anywhere in the render path. JSON is hand-rolled, following the existing `src/OKF4net.Cli/JsonOutput.cs` precedent.
- Target framework `net10.0`. Build with `dotnet build OKF4net.sln`, test with `dotnet test OKF4net.sln`.

## Deviations from the spec (deliberate, approved rationale)

Two points where implementation reality differs from the design doc. Both are **stricter**, not looser.

1. **§6/§7 — one payload container, not two.** The spec described a `<script type="text/markdown">` for the body and a separate `<script type="application/json">` for the link table. Implementation uses a **single** `<script type="application/json">` carrying both. Reason: a JSON string literal with `<` escaped as `<` makes a `</script` breakout *structurally impossible*, which is exactly what §8.1 demands — whereas a raw-text `text/markdown` container has to be escaped by convention. One container, one escaping rule, one test surface.

2. **§8.2 — renderer override, not a `sanitize` option.** The spec said "marked doit être configuré pour ne pas laisser passer le HTML brut". Modern marked **removed** the `sanitize` option and passes raw HTML through by default; there is no such config flag. Raw HTML is instead suppressed by overriding the renderer's `html` hooks to emit nothing. Same guarantee, different mechanism.

## File Structure

**Created:**

| File | Responsibility |
|---|---|
| `src/OKF4net.Viewer/OKF4net.Viewer.csproj` | project definition; embeds `Assets/*` |
| `src/OKF4net.Viewer/README.md` | required by `PackageReadmeFile` |
| `src/OKF4net.Viewer/ViewerModel.cs` | the record types (`ViewerLink`, `ViewerPage`, `ViewerSite`) |
| `src/OKF4net.Viewer/SiteModel.cs` | `Bundle` → `ViewerSite`. Pure, no I/O. |
| `src/OKF4net.Viewer/HtmlSafeJson.cs` | hand-rolled HTML-safe JSON string escaping |
| `src/OKF4net.Viewer/HtmlWriter.cs` | `ViewerSite` → files on disk. Only I/O unit. |
| `src/OKF4net.Viewer/ViewerAssets.cs` | accessors for the embedded assets |
| `src/OKF4net.Viewer/Assets/viewer.css` | stylesheet (tokens copied from `web/src/styles/site.css`) |
| `src/OKF4net.Viewer/Assets/viewer.js` | client bootstrap: parse payload, render, rewire links |
| `src/OKF4net.Viewer/Assets/marked.min.js` | vendored, MIT |
| `tests/OKF4net.Tests/Viewer/SiteModelTests.cs` | model projection tests |
| `tests/OKF4net.Tests/Viewer/HtmlSafeJsonTests.cs` | escaping tests, incl. hostile input |
| `tests/OKF4net.Tests/Viewer/HtmlWriterTests.cs` | on-disk output tests |

**Modified:** `OKF4net.sln`, `tests/OKF4net.Tests/OKF4net.Tests.csproj`, `src/OKF4net.Cli/OkfCli.cs`, `tests/OKF4net.Tests/CliTests.cs`, `NOTICE`, `README.md`, `CLAUDE.md`, `CHANGELOG.md`, `ROADMAP.md`.

---

### Task 1: Scaffold the `OKF4net.Viewer` project

**Files:**
- Create: `src/OKF4net.Viewer/OKF4net.Viewer.csproj`, `src/OKF4net.Viewer/README.md`, `src/OKF4net.Viewer/ViewerAssemblyMarker.cs`
- Modify: `OKF4net.sln`, `tests/OKF4net.Tests/OKF4net.Tests.csproj`
- Test: `tests/OKF4net.Tests/Viewer/SiteModelTests.cs` (placeholder smoke test, replaced in Task 3)

**Interfaces:**
- Consumes: nothing.
- Produces: the compilable project `OKF4net.Viewer`, namespace `OKF4net.Viewer`, referenced by `OKF4net.Tests`.

- [ ] **Step 1: Write the failing test**

Create `tests/OKF4net.Tests/Viewer/SiteModelTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>Tests for the viewer's pure Bundle -> display-model projection.</summary>
public class SiteModelTests
{
    [Fact]
    public void Viewer_assembly_is_referenced()
        => Assert.Equal("OKF4net.Viewer", ViewerAssemblyMarker.Name);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~Viewer_assembly_is_referenced"`
Expected: FAIL — build error, `the type or namespace name 'Viewer' does not exist in the namespace 'OKF4net'`.

- [ ] **Step 3: Create the project**

Create `src/OKF4net.Viewer/OKF4net.Viewer.csproj` (copied from `OKF4net.Catalog.csproj`; note the extra `EmbeddedResource` group, which Catalog does not have):

```xml
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>

  <PropertyGroup Label="Packaging">
    <PackageId>OKF4net.Viewer</PackageId>
    <Authors>Julien CHABLE</Authors>
    <Description>Static HTML site generation for OKF knowledge bundles: one page per concept, generated index, navigable cross-links, over the OKF4net format core.</Description>
    <Copyright>Copyright 2026 Julien CHABLE</Copyright>
    <PackageLicenseExpression>LGPL-3.0-or-later</PackageLicenseExpression>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <PackageProjectUrl>https://github.com/jchable/okf4net</PackageProjectUrl>
    <RepositoryUrl>https://github.com/jchable/okf4net</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <PackageTags>okf;knowledge;viewer;static-site;html</PackageTags>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
  </PropertyGroup>

  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
    <None Include="..\..\NOTICE" Pack="true" PackagePath="\" />
    <None Include="..\..\LICENSE.Apache-2.0" Pack="true" PackagePath="\" />
  </ItemGroup>

  <ItemGroup>
    <EmbeddedResource Include="Assets\**\*" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\OKF4net\OKF4net.csproj" />
  </ItemGroup>

  <ItemGroup>
    <!-- Lets the test project exercise internal members directly, without
         inflating the public API surface. -->
    <InternalsVisibleTo Include="OKF4net.Tests" />
  </ItemGroup>

</Project>
```

Create `src/OKF4net.Viewer/ViewerAssemblyMarker.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Viewer;

/// <summary>Compile-smoke anchor for the viewer assembly.</summary>
public static class ViewerAssemblyMarker
{
    /// <summary>The assembly's simple name.</summary>
    public const string Name = "OKF4net.Viewer";
}
```

Create `src/OKF4net.Viewer/README.md`:

```markdown
# OKF4net.Viewer

Static HTML site generation for OKF knowledge bundles: one page per concept
(frontmatter + rendered body), a generated index, and navigable cross-links.

Zero third-party runtime dependencies — references only `OKF4net`.

Consumed by the `okf render` CLI verb. See the
[OKF4net repository](https://github.com/jchable/okf4net) for usage.

## Licensing

LGPL-3.0-or-later. The generated site embeds a vendored copy of
[marked](https://github.com/markedjs/marked) (MIT) for client-side markdown
rendering — see `NOTICE`.
```

Because `Assets/` does not exist yet and the `EmbeddedResource` glob would
match nothing, create a placeholder so the glob is valid:

```bash
mkdir -p src/OKF4net.Viewer/Assets
printf '/* placeholder, replaced in Task 7 */\n' > src/OKF4net.Viewer/Assets/viewer.css
```

- [ ] **Step 4: Register in the solution and reference from tests**

```bash
dotnet sln OKF4net.sln add src/OKF4net.Viewer/OKF4net.Viewer.csproj --solution-folder src
dotnet add tests/OKF4net.Tests/OKF4net.Tests.csproj reference src/OKF4net.Viewer/OKF4net.Viewer.csproj
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~Viewer_assembly_is_referenced"`
Expected: PASS.

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Viewer OKF4net.sln tests/OKF4net.Tests
git commit -m "feat(viewer): scaffold the zero-dep OKF4net.Viewer project"
```

---

### Task 2: Relative href computation

Computing the link from one generated page to another is pure path math, and it is the piece most likely to be silently wrong (a page nested two levels deep linking to a root concept). It gets its own task and its own tests.

**Files:**
- Create: `src/OKF4net.Viewer/SiteModel.cs`
- Test: `tests/OKF4net.Tests/Viewer/SiteModelTests.cs`

**Interfaces:**
- Consumes: `OKF4net.ConceptId` (`.Segments` → `IReadOnlyList<string>`).
- Produces: `public static string SiteModel.RelativeHref(ConceptId from, ConceptId to)` — returns a `/`-separated relative path ending in `.html`, e.g. `"../glossary/term.html"`.

- [ ] **Step 1: Write the failing test**

Append to `tests/OKF4net.Tests/Viewer/SiteModelTests.cs` (inside the class):

```csharp
    [Fact]
    public void RelativeHref_between_two_root_concepts_is_a_bare_filename()
        => Assert.Equal("b.html",
            SiteModel.RelativeHref(ConceptId.Parse("a"), ConceptId.Parse("b")));

    [Fact]
    public void RelativeHref_from_nested_to_root_walks_up()
        => Assert.Equal("../b.html",
            SiteModel.RelativeHref(ConceptId.Parse("tables/users"), ConceptId.Parse("b")));

    [Fact]
    public void RelativeHref_from_root_to_nested_walks_down()
        => Assert.Equal("tables/users.html",
            SiteModel.RelativeHref(ConceptId.Parse("a"), ConceptId.Parse("tables/users")));

    [Fact]
    public void RelativeHref_within_the_same_directory_is_a_bare_filename()
        => Assert.Equal("orders.html",
            SiteModel.RelativeHref(ConceptId.Parse("tables/users"), ConceptId.Parse("tables/orders")));

    [Fact]
    public void RelativeHref_across_sibling_directories_walks_up_then_down()
        => Assert.Equal("../glossary/term.html",
            SiteModel.RelativeHref(ConceptId.Parse("tables/users"), ConceptId.Parse("glossary/term")));

    [Fact]
    public void RelativeHref_from_deeply_nested_walks_up_once_per_level()
        => Assert.Equal("../../b.html",
            SiteModel.RelativeHref(ConceptId.Parse("a/b/c"), ConceptId.Parse("b")));
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~RelativeHref"`
Expected: FAIL — build error, `'SiteModel' does not contain a definition for 'RelativeHref'`.

- [ ] **Step 3: Write minimal implementation**

Create `src/OKF4net.Viewer/SiteModel.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Viewer;

/// <summary>
/// Projects a loaded <see cref="Bundle"/> into the display model the viewer
/// renders. Pure: performs no I/O, so it is fully testable without touching
/// the filesystem.
/// </summary>
public static class SiteModel
{
    /// <summary>
    /// The href of <paramref name="to"/>'s generated page, relative to
    /// <paramref name="from"/>'s. Always <c>/</c>-separated and suffixed
    /// <c>.html</c>, so the generated site is navigable straight off the
    /// filesystem (<c>file://</c>) at any nesting depth.
    /// </summary>
    /// <param name="from">The concept whose page contains the link.</param>
    /// <param name="to">The concept being linked to.</param>
    public static string RelativeHref(ConceptId from, ConceptId to)
    {
        // Only the directory part of `from` matters: a page at a/b/c.html
        // sits in directory a/b, so it is 2 levels deep.
        var fromDir = from.Segments.Take(from.Segments.Count - 1).ToList();
        var toPath = to.Segments;

        var common = 0;
        while (common < fromDir.Count
               && common < toPath.Count - 1
               && string.Equals(fromDir[common], toPath[common], StringComparison.Ordinal))
        {
            common++;
        }

        var up = Enumerable.Repeat("..", fromDir.Count - common);
        var down = toPath.Skip(common);
        return string.Join('/', up.Concat(down)) + ".html";
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~RelativeHref"`
Expected: PASS (6 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Viewer/SiteModel.cs tests/OKF4net.Tests/Viewer/SiteModelTests.cs
git commit -m "feat(viewer): compute relative hrefs between generated concept pages"
```

---

### Task 3: The display model — pages, frontmatter, body

**Files:**
- Create: `src/OKF4net.Viewer/ViewerModel.cs`
- Modify: `src/OKF4net.Viewer/SiteModel.cs`
- Test: `tests/OKF4net.Tests/Viewer/SiteModelTests.cs`

**Interfaces:**
- Consumes: `Bundle.Load(string)`, `bundle.Concepts`, `Concept.Id`/`.Document`, `OkfDocument.Frontmatter`/`.Body`, `Frontmatter.Title`, `Frontmatter.AsMapping().Entries` (→ `IReadOnlyList<(YamlValue Key, YamlValue Value)>`), `YamlValue.AsDisplayString()` (→ `string?`, null for non-scalars).
- Produces:
  - `public sealed record ViewerLink(string RawTarget, string Href, bool Exists)`
  - `public sealed record ViewerPage(ConceptId Id, string Title, string RelativeHtmlPath, IReadOnlyList<ViewerFrontmatterEntry> Frontmatter, string Body, IReadOnlyList<ViewerLink> Links, IReadOnlyList<ViewerLink> Backlinks)`
  - `public sealed record ViewerFrontmatterEntry(string Key, string Value)`
  - `public sealed record ViewerSite(string BundleRoot, IReadOnlyList<ViewerPage> Pages, string IndexMarkdown, IReadOnlyList<ViewerParseError> ParseErrors)`
  - `public sealed record ViewerParseError(string Path, string Error)`
  - `public static ViewerSite SiteModel.Build(Bundle bundle)`

  In this task `Links`, `Backlinks`, `IndexMarkdown`, and `ParseErrors` are populated as empty/`""`; Tasks 4 and 5 fill them in.

- [ ] **Step 1: Write the failing test**

Append to `tests/OKF4net.Tests/Viewer/SiteModelTests.cs`:

```csharp
    private static Bundle LoadBundle(TempDir tmp)
    {
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root index\nokf_version: \"0.2\"\n---\n");
        tmp.Write("tables/users.md",
            "---\ntype: table\ntitle: Users\ndescription: The users table\ntags:\n  - core\n---\nBody line about users.\n");
        return Bundle.Load(tmp.Path);
    }

    [Fact]
    public void Build_emits_one_page_per_concept()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        var page = Assert.Single(site.Pages);
        Assert.Equal("tables/users", page.Id.ToString());
    }

    [Fact]
    public void Build_uses_the_frontmatter_title_as_the_page_title()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        Assert.Equal("Users", site.Pages[0].Title);
    }

    [Fact]
    public void Build_falls_back_to_the_concept_id_when_the_title_is_missing()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        tmp.Write("untitled.md", "---\ntype: note\ntitle: \"\"\ndescription: d\n---\nBody\n");

        var site = SiteModel.Build(Bundle.Load(tmp.Path));

        Assert.Equal("untitled", Assert.Single(site.Pages).Title);
    }

    [Fact]
    public void Build_maps_the_concept_id_to_an_html_path()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        Assert.Equal("tables/users.html", site.Pages[0].RelativeHtmlPath);
    }

    [Fact]
    public void Build_carries_the_raw_markdown_body_unmodified()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        Assert.Equal("Body line about users.\n", site.Pages[0].Body);
    }

    [Fact]
    public void Build_preserves_frontmatter_key_order()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        Assert.Equal(
            ["type", "title", "description", "tags"],
            site.Pages[0].Frontmatter.Select(e => e.Key).ToArray());
    }

    [Fact]
    public void Build_renders_a_non_scalar_frontmatter_value_rather_than_dropping_it()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        // `tags` is a sequence: AsDisplayString() returns null for non-scalars,
        // so the projection must fall back rather than emit an empty cell.
        var tags = site.Pages[0].Frontmatter.Single(e => e.Key == "tags");
        Assert.Contains("core", tags.Value);
    }

    [Fact]
    public void Build_records_the_bundle_root()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        Assert.Equal(tmp.Path, site.BundleRoot);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~SiteModelTests.Build"`
Expected: FAIL — build error, `'SiteModel' does not contain a definition for 'Build'`.

- [ ] **Step 3: Write minimal implementation**

Create `src/OKF4net.Viewer/ViewerModel.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Viewer;

/// <summary>One frontmatter key/value pair, rendered for display.</summary>
/// <param name="Key">The frontmatter key, in document order.</param>
/// <param name="Value">The value, rendered as a display string.</param>
public sealed record ViewerFrontmatterEntry(string Key, string Value);

/// <summary>
/// A link from one generated page to another, resolved at generation time.
/// </summary>
/// <param name="RawTarget">The link target exactly as written in the markdown source.</param>
/// <param name="Href">The generated page's path, relative to the linking page.</param>
/// <param name="Exists">Whether the target concept exists in the bundle.</param>
public sealed record ViewerLink(string RawTarget, string Href, bool Exists);

/// <summary>One unparseable file, surfaced on the generated index page.</summary>
/// <param name="Path">The offending file's path.</param>
/// <param name="Error">The parse error reported by <see cref="Bundle"/>.</param>
public sealed record ViewerParseError(string Path, string Error);

/// <summary>One generated concept page.</summary>
/// <param name="Id">The concept's id.</param>
/// <param name="Title">The display title (frontmatter title, else the concept id).</param>
/// <param name="RelativeHtmlPath">The page's path relative to the site root, e.g. <c>tables/users.html</c>.</param>
/// <param name="Frontmatter">The frontmatter entries, in document order.</param>
/// <param name="Body">The raw markdown body, rendered client-side.</param>
/// <param name="Links">Outgoing internal links, for client-side href rewiring.</param>
/// <param name="Backlinks">Concepts linking to this one.</param>
public sealed record ViewerPage(
    ConceptId Id,
    string Title,
    string RelativeHtmlPath,
    IReadOnlyList<ViewerFrontmatterEntry> Frontmatter,
    string Body,
    IReadOnlyList<ViewerLink> Links,
    IReadOnlyList<ViewerLink> Backlinks);

/// <summary>The whole generated site, as a pure model.</summary>
/// <param name="BundleRoot">The source bundle's root directory.</param>
/// <param name="Pages">One entry per concept.</param>
/// <param name="IndexMarkdown">The index page's markdown, rendered by the same client-side path as concept bodies.</param>
/// <param name="ParseErrors">Files the bundle could not parse.</param>
public sealed record ViewerSite(
    string BundleRoot,
    IReadOnlyList<ViewerPage> Pages,
    string IndexMarkdown,
    IReadOnlyList<ViewerParseError> ParseErrors);
```

Add to `SiteModel`:

```csharp
    /// <summary>
    /// Projects <paramref name="bundle"/> into the viewer's display model.
    /// Loading is permissive upstream, so a bundle carrying parse errors
    /// still yields a site -- the errors travel in
    /// <see cref="ViewerSite.ParseErrors"/> rather than aborting.
    /// </summary>
    /// <param name="bundle">The loaded bundle to project.</param>
    public static ViewerSite Build(Bundle bundle)
    {
        var pages = bundle.Concepts.Select(c => BuildPage(bundle, c)).ToList();

        return new ViewerSite(
            bundle.Root,
            pages,
            IndexMarkdown: string.Empty,
            ParseErrors: []);
    }

    private static ViewerPage BuildPage(Bundle bundle, Concept concept)
    {
        var frontmatter = concept.Document.Frontmatter;

        var title = string.IsNullOrWhiteSpace(frontmatter.Title)
            ? concept.Id.ToString()
            : frontmatter.Title;

        var entries = frontmatter.AsMapping().Entries
            .Select(e => new ViewerFrontmatterEntry(
                e.Key.AsDisplayString() ?? e.Key.ToYamlString().TrimEnd('\n'),
                DisplayValue(e.Value)))
            .ToList();

        return new ViewerPage(
            concept.Id,
            title,
            concept.Id.ToString() + ".html",
            entries,
            concept.Document.Body,
            Links: [],
            Backlinks: []);
    }

    /// <summary>
    /// A frontmatter value as a single display string.
    /// <see cref="YamlValue.AsDisplayString"/> returns <c>null</c> for
    /// sequences and mappings, so those fall back to a compact YAML emit --
    /// dropping them would silently hide `tags`, `sources`, and every
    /// structured producer key.
    /// </summary>
    private static string DisplayValue(YamlValue value)
        => value.AsDisplayString()
           ?? value.ToYamlString().TrimEnd('\n').Replace("\n", " ");
```

Add `using OKF4net.Yaml;` at the top of `SiteModel.cs` (for `YamlValue`).

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~SiteModelTests"`
Expected: PASS (all tests, including Task 2's).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Viewer tests/OKF4net.Tests/Viewer
git commit -m "feat(viewer): project bundle concepts into the viewer display model"
```

---

### Task 4: Link table and backlinks

**Files:**
- Modify: `src/OKF4net.Viewer/SiteModel.cs`
- Test: `tests/OKF4net.Tests/Viewer/SiteModelTests.cs`

**Interfaces:**
- Consumes: `bundle.LinksFrom(ConceptId)` → `IReadOnlyList<ResolvedLink>` where `ResolvedLink` is `(ConceptId Target, bool Exists, string Text, string Raw)`; `bundle.Backlinks(ConceptId)` → `IReadOnlyList<ConceptId>`.
- Produces: `ViewerPage.Links` and `ViewerPage.Backlinks` populated. `Links` maps each raw target to the relative href of its generated page. Only internal links appear — `LinksFrom` already excludes external and anchor links, which stay untouched in the rendered HTML.

- [ ] **Step 1: Write the failing test**

Append to `tests/OKF4net.Tests/Viewer/SiteModelTests.cs`:

```csharp
    private static Bundle LoadLinkedBundle(TempDir tmp)
    {
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        tmp.Write("tables/users.md",
            "---\ntype: table\ntitle: Users\ndescription: d\n---\n"
            + "See [term](../glossary/term.md) and [gone](../glossary/missing.md).\n"
            + "External [site](https://example.com) and [anchor](#section).\n");
        tmp.Write("glossary/term.md", "---\ntype: term\ntitle: Term\ndescription: d\n---\nA term.\n");
        return Bundle.Load(tmp.Path);
    }

    [Fact]
    public void Build_resolves_an_internal_link_to_the_target_pages_relative_href()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadLinkedBundle(tmp));

        var users = site.Pages.Single(p => p.Id.ToString() == "tables/users");
        var link = users.Links.Single(l => l.RawTarget == "../glossary/term.md");
        Assert.Equal("../glossary/term.html", link.Href);
        Assert.True(link.Exists);
    }

    [Fact]
    public void Build_marks_a_link_to_a_missing_concept_as_broken()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadLinkedBundle(tmp));

        var users = site.Pages.Single(p => p.Id.ToString() == "tables/users");
        var link = users.Links.Single(l => l.RawTarget == "../glossary/missing.md");
        Assert.False(link.Exists);
    }

    [Fact]
    public void Build_leaves_external_and_anchor_links_out_of_the_rewiring_table()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadLinkedBundle(tmp));

        var users = site.Pages.Single(p => p.Id.ToString() == "tables/users");
        Assert.DoesNotContain(users.Links, l => l.RawTarget.StartsWith("https://", StringComparison.Ordinal));
        Assert.DoesNotContain(users.Links, l => l.RawTarget.StartsWith("#", StringComparison.Ordinal));
    }

    [Fact]
    public void Build_records_backlinks_pointing_at_a_concept()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadLinkedBundle(tmp));

        var term = site.Pages.Single(p => p.Id.ToString() == "glossary/term");
        var backlink = Assert.Single(term.Backlinks);
        Assert.Equal("../tables/users.html", backlink.Href);
        Assert.True(backlink.Exists);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~SiteModelTests.Build_resolves_an_internal_link"`
Expected: FAIL — `Assert.Single() Failure: The collection was empty` (`Links` is still `[]`).

- [ ] **Step 3: Write minimal implementation**

In `SiteModel.BuildPage`, replace the `Links: []` and `Backlinks: []` arguments with computed values, and pass `bundle` through:

```csharp
        var links = bundle.LinksFrom(concept.Id)
            .Select(l => new ViewerLink(l.Raw, RelativeHref(concept.Id, l.Target), l.Exists))
            .ToList();

        var backlinks = bundle.Backlinks(concept.Id)
            .Select(source => new ViewerLink(
                source.ToString(),
                RelativeHref(concept.Id, source),
                Exists: true))
            .ToList();
```

then:

```csharp
        return new ViewerPage(
            concept.Id,
            title,
            concept.Id.ToString() + ".html",
            entries,
            concept.Document.Body,
            links,
            backlinks);
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~SiteModelTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Viewer/SiteModel.cs tests/OKF4net.Tests/Viewer/SiteModelTests.cs
git commit -m "feat(viewer): resolve inter-concept links and backlinks for rewiring"
```

---

### Task 5: Index page and parse errors

**Files:**
- Modify: `src/OKF4net.Viewer/SiteModel.cs`
- Test: `tests/OKF4net.Tests/Viewer/SiteModelTests.cs`

**Interfaces:**
- Consumes: `IndexGenerator.BuildIndexText(IReadOnlyList<IndexEntry>)`, `IndexEntry(string Type, string Title, string Link, string Description)`; `bundle.ParseErrors` → `IReadOnlyList<(string Path, string Error)>`.
- Produces: `ViewerSite.IndexMarkdown` and `ViewerSite.ParseErrors` populated.

**Design note:** index entries are built with `Link` set to the **generated `.html` path**, not the source `.md` path. The index page therefore needs no link table at all — its markdown already points at the generated pages.

- [ ] **Step 1: Write the failing test**

Append to `tests/OKF4net.Tests/Viewer/SiteModelTests.cs`:

```csharp
    [Fact]
    public void Build_generates_index_markdown_linking_to_generated_pages()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        Assert.Contains("[Users](tables/users.html)", site.IndexMarkdown);
    }

    [Fact]
    public void Build_groups_index_entries_by_concept_type()
    {
        using var tmp = new TempDir();
        var site = SiteModel.Build(LoadBundle(tmp));

        Assert.Contains("# table", site.IndexMarkdown);
    }

    [Fact]
    public void Build_surfaces_parse_errors_rather_than_dropping_them()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        tmp.Write("good.md", "---\ntype: note\ntitle: Good\ndescription: d\n---\nBody\n");
        tmp.Write("broken.md", "---\ntitle: No type\n---\nBody\n");

        var site = SiteModel.Build(Bundle.Load(tmp.Path));

        var error = Assert.Single(site.ParseErrors);
        Assert.Contains("broken.md", error.Path);
        Assert.False(string.IsNullOrWhiteSpace(error.Error));
    }

    [Fact]
    public void Build_on_an_empty_bundle_yields_an_empty_site_not_an_error()
    {
        using var tmp = new TempDir();
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");

        var site = SiteModel.Build(Bundle.Load(tmp.Path));

        Assert.Empty(site.Pages);
        Assert.Empty(site.ParseErrors);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~SiteModelTests.Build_generates_index_markdown"`
Expected: FAIL — `Assert.Contains() Failure` on an empty `IndexMarkdown`.

- [ ] **Step 3: Write minimal implementation**

In `SiteModel.Build`, replace the `IndexMarkdown` and `ParseErrors` arguments:

```csharp
        var entries = pages
            .Select(p => new IndexEntry(
                Type: TypeOf(bundle, p.Id),
                Title: p.Title,
                Link: p.RelativeHtmlPath,
                Description: DescriptionOf(bundle, p.Id)))
            .ToList();

        return new ViewerSite(
            bundle.Root,
            pages,
            IndexGenerator.BuildIndexText(entries),
            bundle.ParseErrors.Select(e => new ViewerParseError(e.Path, e.Error)).ToList());
```

and add the two lookups:

```csharp
    private static string TypeOf(Bundle bundle, ConceptId id)
        => bundle.Get(id)?.Document.Frontmatter.Type ?? string.Empty;

    private static string DescriptionOf(Bundle bundle, ConceptId id)
        => bundle.Get(id)?.Document.Frontmatter.Description ?? string.Empty;
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~SiteModelTests"`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Viewer/SiteModel.cs tests/OKF4net.Tests/Viewer/SiteModelTests.cs
git commit -m "feat(viewer): generate the index page and surface bundle parse errors"
```

---

### Task 6: HTML-safe JSON escaping

**This task is security-critical (spec §8.1).** The page payload is JSON embedded inside a `<script>` element; if a concept body can emit a literal `</script`, it breaks out of the container and injects markup into the generated page.

**Files:**
- Create: `src/OKF4net.Viewer/HtmlSafeJson.cs`
- Test: `tests/OKF4net.Tests/Viewer/HtmlSafeJsonTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static string HtmlSafeJson.Quote(string value)` — returns a complete JSON string literal **including surrounding double quotes**, with `<`, `>`, `&`, U+2028 and U+2029 escaped as `\uXXXX`.

**Why not reuse `src/OKF4net.Cli/JsonOutput.cs`:** `OKF4net.Cli` references `OKF4net.Viewer`, not the reverse, so the dependency cannot be inverted. The requirements also differ — `JsonOutput` produces plain JSON for a terminal, this produces JSON safe to embed in HTML, which must escape three characters plain JSON leaves alone.

- [ ] **Step 1: Write the failing test**

Create `tests/OKF4net.Tests/Viewer/HtmlSafeJsonTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>
/// Tests for the viewer's HTML-safe JSON string escaping. The generated page
/// embeds its payload inside a &lt;script&gt; element, so escaping here is a
/// security boundary, not a formatting detail (design spec §8.1).
/// </summary>
public class HtmlSafeJsonTests
{
    [Fact]
    public void Quote_wraps_the_value_in_double_quotes()
        => Assert.Equal("\"hello\"", HtmlSafeJson.Quote("hello"));

    [Fact]
    public void Quote_escapes_quotes_and_backslashes()
        => Assert.Equal("\"a\\\"b\\\\c\"", HtmlSafeJson.Quote("a\"b\\c"));

    [Fact]
    public void Quote_escapes_control_characters()
        => Assert.Equal("\"a\\nb\\tc\"", HtmlSafeJson.Quote("a\nb\tc"));

    // --- security: script-container breakout (spec §8.1) ---

    [Fact]
    public void Quote_escapes_a_closing_script_tag_so_it_cannot_break_out()
    {
        var hostile = "</script><img src=x onerror=alert(1)>";
        var quoted = HtmlSafeJson.Quote(hostile);

        Assert.DoesNotContain("</script", quoted, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("<", quoted, StringComparison.Ordinal);
    }

    [Fact]
    public void Quote_escapes_angle_brackets_and_ampersands()
    {
        var quoted = HtmlSafeJson.Quote("<&>");
        Assert.Equal("\"\\u003c\\u0026\\u003e\"", quoted);
    }

    [Fact]
    public void Quote_escapes_the_line_and_paragraph_separators_that_break_js_string_literals()
    {
        var quoted = HtmlSafeJson.Quote("a\u2028b\u2029c");
        Assert.Equal("\"a\\u2028b\\u2029c\"", quoted);
    }

    [Fact]
    public void Quote_leaves_ordinary_markdown_untouched()
        => Assert.Equal("\"# Title\\n\\n- item\"", HtmlSafeJson.Quote("# Title\n\n- item"));
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~HtmlSafeJsonTests"`
Expected: FAIL — build error, `The name 'HtmlSafeJson' does not exist`.

- [ ] **Step 3: Write minimal implementation**

Create `src/OKF4net.Viewer/HtmlSafeJson.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;

namespace OKF4net.Viewer;

/// <summary>
/// Hand-rolled JSON string escaping, safe to embed inside an HTML
/// <c>&lt;script&gt;</c> element. Hand-rolled rather than
/// <c>System.Text.Json</c> because the CLI consuming this is published
/// Native AOT and must stay free of reflection-based serialization.
/// </summary>
/// <remarks>
/// Beyond the JSON minimum this escapes <c>&lt;</c>, <c>&gt;</c> and
/// <c>&amp;</c> as <c>\uXXXX</c>. That is what makes a <c>&lt;/script&gt;</c>
/// sequence in untrusted bundle content unable to terminate the container
/// element early. U+2028/U+2029 are escaped too: they are valid JSON but
/// terminate a JavaScript string literal.
/// </remarks>
public static class HtmlSafeJson
{
    /// <summary>
    /// <paramref name="value"/> as a complete JSON string literal, including
    /// its surrounding double quotes.
    /// </summary>
    /// <param name="value">The string to quote and escape.</param>
    public static string Quote(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');

        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                // Escaped so untrusted content cannot close the surrounding
                // <script> element or inject markup into the page.
                case '<': case '>': case '&':
                case '\u2028': case '\u2029':
                    AppendUnicodeEscape(sb, c);
                    break;
                default:
                    if (c < ' ')
                    {
                        AppendUnicodeEscape(sb, c);
                    }
                    else
                    {
                        sb.Append(c);
                    }

                    break;
            }
        }

        sb.Append('"');
        return sb.ToString();
    }

    private static void AppendUnicodeEscape(StringBuilder sb, char c)
        => sb.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~HtmlSafeJsonTests"`
Expected: PASS (7 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Viewer/HtmlSafeJson.cs tests/OKF4net.Tests/Viewer/HtmlSafeJsonTests.cs
git commit -m "feat(viewer): HTML-safe JSON escaping for the embedded page payload"
```

---

### Task 7: Vendored assets — marked, CSS, client bootstrap

**Files:**
- Create: `src/OKF4net.Viewer/Assets/marked.min.js`, `src/OKF4net.Viewer/Assets/viewer.js`, `src/OKF4net.Viewer/ViewerAssets.cs`
- Modify: `src/OKF4net.Viewer/Assets/viewer.css` (replaces the Task 1 placeholder), `NOTICE`
- Test: `tests/OKF4net.Tests/Viewer/ViewerAssetsTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `public static class ViewerAssets` with `public static string Css { get; }`, `public static string MarkedJs { get; }`, `public static string ViewerJs { get; }`.

**Security (spec §8.2):** marked passes raw HTML through by default — the old `sanitize` option was removed and there is no config flag for this. Raw HTML is suppressed by overriding the renderer's `html` hooks in `viewer.js`. This is the deviation recorded at the top of this plan.

- [ ] **Step 1: Vendor marked**

```bash
curl -sSL https://cdn.jsdelivr.net/npm/marked/marked.min.js -o src/OKF4net.Viewer/Assets/marked.min.js
head -c 400 src/OKF4net.Viewer/Assets/marked.min.js
```

Read the version from the banner comment at the top of the downloaded file and keep it — the next step records it in `NOTICE`. Do **not** strip the banner: it carries the MIT copyright notice, whose retention the licence requires.

- [ ] **Step 2: Credit marked in NOTICE**

Append to `NOTICE` (substituting the actual version observed in Step 1 for `<VERSION>`):

```text
------------------------------------------------------------------------------

The static site generated by `OKF4net.Viewer` (the `okf render` command)
embeds a vendored copy of marked <VERSION>

    marked
    Copyright (c) 2011-2018, Christopher Jeffrey
    https://github.com/markedjs/marked

licensed under the MIT License. The copy lives at
`src/OKF4net.Viewer/Assets/marked.min.js` with its original copyright banner
intact. It is used for client-side markdown rendering in generated pages only;
it is not a dependency of any OKF4net library or of the `okf` binary itself.
```

- [ ] **Step 3: Write the failing test**

Create `tests/OKF4net.Tests/Viewer/ViewerAssetsTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>
/// Tests that the viewer's embedded assets are present and carry the
/// guarantees the generated pages depend on.
/// </summary>
public class ViewerAssetsTests
{
    [Fact]
    public void Css_is_embedded_and_non_empty()
        => Assert.False(string.IsNullOrWhiteSpace(ViewerAssets.Css));

    [Fact]
    public void MarkedJs_is_embedded_and_non_empty()
        => Assert.False(string.IsNullOrWhiteSpace(ViewerAssets.MarkedJs));

    [Fact]
    public void MarkedJs_retains_its_MIT_copyright_banner()
        => Assert.Contains("marked", ViewerAssets.MarkedJs, StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void ViewerJs_is_embedded_and_non_empty()
        => Assert.False(string.IsNullOrWhiteSpace(ViewerAssets.ViewerJs));

    [Fact]
    public void ViewerJs_disables_raw_html_passthrough()
    {
        // marked renders raw HTML by default and has no `sanitize` option any
        // more, so suppression happens via the renderer's html hooks. If this
        // override is ever dropped, a concept body can inject script into the
        // generated page (design spec §8.2).
        Assert.Contains("html:", ViewerAssets.ViewerJs);
        Assert.Contains("renderer", ViewerAssets.ViewerJs);
    }
}
```

- [ ] **Step 4: Run test to verify it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ViewerAssetsTests"`
Expected: FAIL — build error, `The name 'ViewerAssets' does not exist`.

- [ ] **Step 5: Write the assets and the accessor**

Replace `src/OKF4net.Viewer/Assets/viewer.css` (tokens copied from `web/src/styles/site.css` — an intentional copy, maintained independently of the website):

```css
/* OKF4net bundle viewer — visual language copied from the project website
   ("The §Document" theme). Maintained independently of web/: the two are
   deliberately not coupled. */
:root {
  --white: #ffffff;
  --ink: #101014;
  --blue: #1a3fd6;
  --blue-soft: #eef1fd;
  --gray: #6a6a72;
  --hair: #e3e3e8;
  --red: #c0392b;
  --display: "Inter Tight", "Arial Narrow", sans-serif;
  --body: "Inter", "Helvetica Neue", sans-serif;
  --mono: "Space Mono", Consolas, monospace;
}
* { margin: 0; padding: 0; box-sizing: border-box; }
body {
  background: var(--white); color: var(--ink);
  font-family: var(--body); font-size: 16px; line-height: 1.6;
}
.topline { height: 6px; background: var(--blue); }
header.bar { border-bottom: 1px solid var(--hair); }
.bar-in {
  max-width: 900px; margin: 0 auto; padding: 16px clamp(16px, 3.5vw, 48px);
  display: flex; align-items: center; gap: 24px;
}
.wordmark {
  font-family: var(--display); font-weight: 900; font-size: 20px;
  letter-spacing: -.02em; color: var(--ink); text-decoration: none;
}
.wordmark sup { color: var(--blue); font-family: var(--mono); font-weight: 700; }
main { max-width: 900px; margin: 0 auto; padding: 32px clamp(16px, 3.5vw, 48px) 96px; }
h1, h2, h3 { font-family: var(--display); letter-spacing: -.02em; margin: 1.4em 0 .5em; }
h1 { font-size: 32px; margin-top: 0; }
p, ul, ol, table, pre { margin: 0 0 1em; }
ul, ol { padding-left: 1.4em; }
a { color: var(--blue); }
a.broken { color: var(--red); text-decoration: line-through; cursor: not-allowed; }
code { font-family: var(--mono); font-size: .92em; background: var(--blue-soft); padding: .1em .35em; }
pre { background: var(--blue-soft); padding: 16px; overflow-x: auto; }
pre code { background: none; padding: 0; }
table.frontmatter { border-collapse: collapse; width: 100%; font-size: 14px; }
table.frontmatter th, table.frontmatter td {
  text-align: left; vertical-align: top; padding: 6px 12px 6px 0;
  border-bottom: 1px solid var(--hair);
}
table.frontmatter th { font-family: var(--mono); font-weight: 400; color: var(--gray); width: 200px; }
.meta { font-family: var(--mono); font-size: 13px; color: var(--gray); }
.errors { border-left: 3px solid var(--red); padding-left: 16px; margin-bottom: 32px; }
.errors h2 { color: var(--red); font-size: 18px; }
@media (prefers-color-scheme: dark) {
  :root { --white: #101014; --ink: #f2f2f5; --blue: #8fa5f5; --blue-soft: #1a1a22; --hair: #2a2a33; --gray: #9a9aa2; }
}
```

Create `src/OKF4net.Viewer/Assets/viewer.js`:

```javascript
// SPDX-License-Identifier: LGPL-3.0-or-later
// Client bootstrap for a generated OKF bundle page: read the embedded JSON
// payload, render its markdown, then rewire inter-concept links.
(function () {
  "use strict";

  // marked renders raw HTML by default and no longer exposes a `sanitize`
  // option, so raw HTML is suppressed at the renderer level. Without this,
  // a concept body could inject arbitrary script into the generated page.
  marked.use({
    renderer: {
      html: function () { return ""; },
    },
  });

  var el = document.getElementById("okf-payload");
  if (!el) { return; }
  var payload = JSON.parse(el.textContent);

  var target = document.getElementById("okf-body");
  target.innerHTML = marked.parse(payload.body || "");

  // Rewire internal links from the generation-time table. Anything absent
  // from the table (external URLs, anchors) is left exactly as authored.
  var map = payload.links || {};
  var anchors = target.getElementsByTagName("a");
  for (var i = 0; i < anchors.length; i++) {
    var raw = anchors[i].getAttribute("href");
    if (!raw || !Object.prototype.hasOwnProperty.call(map, raw)) { continue; }
    var entry = map[raw];
    if (entry.exists) {
      anchors[i].setAttribute("href", entry.href);
    } else {
      anchors[i].removeAttribute("href");
      anchors[i].className = "broken";
      anchors[i].setAttribute("title", "broken link: " + raw);
    }
  }
})();
```

Create `src/OKF4net.Viewer/ViewerAssets.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;

namespace OKF4net.Viewer;

/// <summary>
/// The viewer's static assets, embedded in the assembly so the Native AOT
/// <c>okf</c> binary stays self-contained (no files to ship alongside it).
/// </summary>
public static class ViewerAssets
{
    /// <summary>The generated site's stylesheet.</summary>
    public static string Css { get; } = Read("viewer.css");

    /// <summary>The vendored marked bundle (MIT) used for client-side markdown rendering.</summary>
    public static string MarkedJs { get; } = Read("marked.min.js");

    /// <summary>The client bootstrap that renders a page's payload and rewires its links.</summary>
    public static string ViewerJs { get; } = Read("viewer.js");

    private static string Read(string name)
    {
        var assembly = typeof(ViewerAssets).Assembly;
        var resource = $"OKF4net.Viewer.Assets.{name}";
        using var stream = assembly.GetManifestResourceStream(resource)
            ?? throw new InvalidOperationException($"embedded asset not found: {resource}");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
```

- [ ] **Step 6: Run test to verify it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ViewerAssetsTests"`
Expected: PASS (5 tests).

If `MarkedJs` throws "embedded asset not found", the resource name does not match: run
`dotnet build src/OKF4net.Viewer` then inspect the actual names with a scratch call to
`typeof(ViewerAssets).Assembly.GetManifestResourceNames()` and align the prefix.

- [ ] **Step 7: Commit**

```bash
git add src/OKF4net.Viewer/Assets src/OKF4net.Viewer/ViewerAssets.cs NOTICE tests/OKF4net.Tests/Viewer/ViewerAssetsTests.cs
git commit -m "feat(viewer): vendor marked, add stylesheet and client bootstrap"
```

---

### Task 8: `HtmlWriter` — emit the site to disk

**Files:**
- Create: `src/OKF4net.Viewer/HtmlWriter.cs`
- Test: `tests/OKF4net.Tests/Viewer/HtmlWriterTests.cs`

**Interfaces:**
- Consumes: `ViewerSite`, `ViewerPage`, `ViewerLink`, `HtmlSafeJson.Quote`, `ViewerAssets`.
- Produces: `public static IReadOnlyList<string> HtmlWriter.Write(ViewerSite site, string outDir)` — returns the relative paths written, in write order. Throws `ArgumentException` when `outDir` resolves inside `site.BundleRoot`.

- [ ] **Step 1: Write the failing test**

Create `tests/OKF4net.Tests/Viewer/HtmlWriterTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Viewer;

namespace OKF4net.Tests.Viewer;

/// <summary>Tests for writing a <see cref="ViewerSite"/> out as a static site.</summary>
public class HtmlWriterTests
{
    private static Bundle SampleBundle(TempDir tmp)
    {
        tmp.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        tmp.Write("tables/users.md",
            "---\ntype: table\ntitle: Users\ndescription: The users table\n---\nSome **body**.\n");
        return Bundle.Load(tmp.Path);
    }

    [Fact]
    public void Write_creates_the_index_and_one_page_per_concept()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        var written = HtmlWriter.Write(site, dest.Path);

        Assert.True(File.Exists(Path.Combine(dest.Path, "index.html")));
        Assert.True(File.Exists(Path.Combine(dest.Path, "tables", "users.html")));
        Assert.Contains("index.html", written);
    }

    [Fact]
    public void Write_emits_the_shared_assets_once()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        HtmlWriter.Write(site, dest.Path);

        Assert.True(File.Exists(Path.Combine(dest.Path, "assets", "viewer.css")));
        Assert.True(File.Exists(Path.Combine(dest.Path, "assets", "viewer.js")));
        Assert.True(File.Exists(Path.Combine(dest.Path, "assets", "marked.min.js")));
    }

    [Fact]
    public void Write_links_assets_with_a_path_relative_to_the_page_depth()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        HtmlWriter.Write(site, dest.Path);

        var nested = File.ReadAllText(Path.Combine(dest.Path, "tables", "users.html"));
        Assert.Contains("../assets/viewer.css", nested);

        var root = File.ReadAllText(Path.Combine(dest.Path, "index.html"));
        Assert.Contains("assets/viewer.css", root);
        Assert.DoesNotContain("../assets/viewer.css", root);
    }

    [Fact]
    public void Write_embeds_the_body_as_json_rather_than_pre_rendered_html()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        HtmlWriter.Write(site, dest.Path);

        var page = File.ReadAllText(Path.Combine(dest.Path, "tables", "users.html"));
        Assert.Contains("id=\"okf-payload\"", page);
        Assert.Contains("Some **body**.", page);
    }

    [Fact]
    public void Write_renders_the_frontmatter_table()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        HtmlWriter.Write(site, dest.Path);

        var page = File.ReadAllText(Path.Combine(dest.Path, "tables", "users.html"));
        Assert.Contains("The users table", page);
    }

    [Fact]
    public void Write_creates_the_output_directory_when_missing()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));
        var target = Path.Combine(dest.Path, "does", "not", "exist");

        HtmlWriter.Write(site, target);

        Assert.True(File.Exists(Path.Combine(target, "index.html")));
    }

    [Fact]
    public void Write_leaves_unrelated_files_in_the_output_directory_alone()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        var keep = Path.Combine(dest.Path, "keep-me.txt");
        File.WriteAllText(keep, "untouched");
        var site = SiteModel.Build(SampleBundle(src));

        HtmlWriter.Write(site, dest.Path);

        Assert.Equal("untouched", File.ReadAllText(keep));
    }

    [Fact]
    public void Write_refuses_to_write_inside_the_bundle_it_renders()
    {
        using var src = new TempDir();
        var site = SiteModel.Build(SampleBundle(src));

        var ex = Assert.Throws<ArgumentException>(
            () => HtmlWriter.Write(site, Path.Combine(src.Path, "site")));
        Assert.Contains("bundle", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Write_surfaces_parse_errors_on_the_index_page()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        src.Write("broken.md", "---\ntitle: No type\n---\nBody\n");
        var site = SiteModel.Build(Bundle.Load(src.Path));

        HtmlWriter.Write(site, dest.Path);

        var index = File.ReadAllText(Path.Combine(dest.Path, "index.html"));
        Assert.Contains("broken.md", index);
    }

    [Fact]
    public void Write_escapes_html_metacharacters_in_a_concept_title()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        src.Write("evil.md",
            "---\ntype: note\ntitle: \"<img src=x onerror=alert(1)>\"\ndescription: d\n---\nBody\n");
        var site = SiteModel.Build(Bundle.Load(src.Path));

        HtmlWriter.Write(site, dest.Path);

        var page = File.ReadAllText(Path.Combine(dest.Path, "evil.html"));
        Assert.DoesNotContain("<img src=x", page);
        Assert.Contains("&lt;img", page);
    }

    [Fact]
    public void Write_keeps_a_script_closing_tag_in_a_body_inside_the_payload()
    {
        using var src = new TempDir();
        using var dest = new TempDir();
        src.Write("index.md", "---\ntype: index\ntitle: Root\ndescription: Root\n---\n");
        src.Write("evil.md",
            "---\ntype: note\ntitle: Evil\ndescription: d\n---\n</script><img src=x onerror=alert(1)>\n");
        var site = SiteModel.Build(Bundle.Load(src.Path));

        HtmlWriter.Write(site, dest.Path);

        var page = File.ReadAllText(Path.Combine(dest.Path, "evil.html"));
        // Exactly one </script> in the document: the payload container's own.
        // A second one would mean the body broke out of it.
        Assert.Equal(1, CountOccurrences(page, "</script>"));
    }

    private static int CountOccurrences(string haystack, string needle)
    {
        var count = 0;
        var index = 0;
        while ((index = haystack.IndexOf(needle, index, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            count++;
            index += needle.Length;
        }

        return count;
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~HtmlWriterTests"`
Expected: FAIL — build error, `The name 'HtmlWriter' does not exist`.

- [ ] **Step 3: Write minimal implementation**

Create `src/OKF4net.Viewer/HtmlWriter.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OKF4net.Viewer;

/// <summary>
/// Writes a <see cref="ViewerSite"/> out as a self-contained static site.
/// The only unit in the viewer that touches the filesystem.
/// </summary>
public static class HtmlWriter
{
    /// <summary>
    /// Writes <paramref name="site"/> into <paramref name="outDir"/>, creating
    /// it if needed, and returns the site-relative paths written in write
    /// order. Existing files with the same names are overwritten; nothing else
    /// in the directory is removed.
    /// </summary>
    /// <param name="site">The site model to write.</param>
    /// <param name="outDir">The output directory.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="outDir"/> resolves inside the rendered bundle, which
    /// would pollute the bundle being viewed.
    /// </exception>
    public static IReadOnlyList<string> Write(ViewerSite site, string outDir)
    {
        GuardOutputDirectory(site.BundleRoot, outDir);

        var written = new List<string>();
        Directory.CreateDirectory(outDir);

        WriteAsset(outDir, "viewer.css", ViewerAssets.Css, written);
        WriteAsset(outDir, "viewer.js", ViewerAssets.ViewerJs, written);
        WriteAsset(outDir, "marked.min.js", ViewerAssets.MarkedJs, written);

        WriteFile(outDir, "index.html", RenderIndex(site), written);

        foreach (var page in site.Pages)
        {
            WriteFile(outDir, page.RelativeHtmlPath, RenderPage(page), written);
        }

        return written;
    }

    /// <summary>
    /// Rejects an output directory inside the bundle being rendered: writing
    /// there would add generated files to the very bundle the site describes.
    /// </summary>
    private static void GuardOutputDirectory(string bundleRoot, string outDir)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundleRoot));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(outDir));

        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(root, target, comparison)
            || target.StartsWith(root + Path.DirectorySeparatorChar, comparison))
        {
            throw new ArgumentException(
                $"refusing to render into '{outDir}': it is inside the bundle being rendered ('{bundleRoot}')",
                nameof(outDir));
        }
    }

    private static void WriteAsset(string outDir, string name, string content, List<string> written)
        => WriteFile(outDir, "assets/" + name, content, written);

    private static void WriteFile(string outDir, string relativePath, string content, List<string> written)
    {
        var full = Path.Combine(outDir, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new UTF8Encoding(false));
        written.Add(relativePath);
    }

    /// <summary>The <c>../</c> prefix taking a page at <paramref name="relativePath"/> back to the site root.</summary>
    private static string RootPrefix(string relativePath)
    {
        var depth = relativePath.Count(c => c == '/');
        return string.Concat(Enumerable.Repeat("../", depth));
    }

    private static string RenderPage(ViewerPage page)
    {
        var prefix = RootPrefix(page.RelativeHtmlPath);
        var body = new StringBuilder();

        body.Append("<h1>").Append(HtmlEscape(page.Title)).Append("</h1>\n");
        body.Append("<p class=\"meta\">").Append(HtmlEscape(page.Id.ToString())).Append("</p>\n");
        body.Append(RenderFrontmatter(page.Frontmatter));
        body.Append("<div id=\"okf-body\"></div>\n");
        body.Append(RenderBacklinks(page.Backlinks));

        return RenderShell(page.Title, prefix, body.ToString(), Payload(page));
    }

    private static string RenderIndex(ViewerSite site)
    {
        var body = new StringBuilder();
        body.Append("<h1>Bundle index</h1>\n");
        body.Append("<p class=\"meta\">")
            .Append(site.Pages.Count)
            .Append(site.Pages.Count == 1 ? " concept" : " concepts")
            .Append("</p>\n");

        if (site.ParseErrors.Count > 0)
        {
            body.Append("<div class=\"errors\">\n<h2>Parse errors</h2>\n<ul>\n");
            foreach (var error in site.ParseErrors)
            {
                body.Append("<li><code>").Append(HtmlEscape(error.Path)).Append("</code> — ")
                    .Append(HtmlEscape(error.Error)).Append("</li>\n");
            }

            body.Append("</ul>\n</div>\n");
        }

        body.Append("<div id=\"okf-body\"></div>\n");

        // The index's links already point at generated .html paths, so its
        // rewiring table is deliberately empty.
        var payload = $"{{\"body\":{HtmlSafeJson.Quote(site.IndexMarkdown)},\"links\":{{}}}}";
        return RenderShell("Bundle index", string.Empty, body.ToString(), payload);
    }

    private static string RenderFrontmatter(IReadOnlyList<ViewerFrontmatterEntry> entries)
    {
        if (entries.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder("<table class=\"frontmatter\">\n");
        foreach (var entry in entries)
        {
            sb.Append("<tr><th>").Append(HtmlEscape(entry.Key)).Append("</th><td>")
              .Append(HtmlEscape(entry.Value)).Append("</td></tr>\n");
        }

        return sb.Append("</table>\n").ToString();
    }

    private static string RenderBacklinks(IReadOnlyList<ViewerLink> backlinks)
    {
        if (backlinks.Count == 0)
        {
            return string.Empty;
        }

        var sb = new StringBuilder("<h2>Referenced by</h2>\n<ul>\n");
        foreach (var link in backlinks)
        {
            sb.Append("<li><a href=\"").Append(HtmlEscape(link.Href)).Append("\">")
              .Append(HtmlEscape(link.RawTarget)).Append("</a></li>\n");
        }

        return sb.Append("</ul>\n").ToString();
    }

    private static string Payload(ViewerPage page)
    {
        var links = new StringBuilder("{");
        for (var i = 0; i < page.Links.Count; i++)
        {
            var link = page.Links[i];
            if (i > 0)
            {
                links.Append(',');
            }

            links.Append(HtmlSafeJson.Quote(link.RawTarget))
                 .Append(":{\"href\":").Append(HtmlSafeJson.Quote(link.Href))
                 .Append(",\"exists\":").Append(link.Exists ? "true" : "false")
                 .Append('}');
        }

        links.Append('}');
        return $"{{\"body\":{HtmlSafeJson.Quote(page.Body)},\"links\":{links}}}";
    }

    private static string RenderShell(string title, string rootPrefix, string body, string payload)
        => $"""
        <!doctype html>
        <html lang="en">
        <head>
        <meta charset="utf-8">
        <meta name="viewport" content="width=device-width, initial-scale=1">
        <title>{HtmlEscape(title)}</title>
        <link rel="stylesheet" href="{rootPrefix}assets/viewer.css">
        </head>
        <body>
        <div class="topline"></div>
        <header class="bar"><div class="bar-in">
        <a class="wordmark" href="{rootPrefix}index.html">OKF<sup>§</sup></a>
        </div></header>
        <main>
        {body}</main>
        <script type="application/json" id="okf-payload">{payload}</script>
        <script src="{rootPrefix}assets/marked.min.js"></script>
        <script src="{rootPrefix}assets/viewer.js"></script>
        </body>
        </html>

        """;

    /// <summary>
    /// Escapes text interpolated into the generated markup. Bundle content is
    /// semi-trusted -- a bundle may come from a third-party repository -- so
    /// every value reaching the page goes through this.
    /// </summary>
    private static string HtmlEscape(string value)
        => value.Replace("&", "&amp;")
                .Replace("<", "&lt;")
                .Replace(">", "&gt;")
                .Replace("\"", "&quot;");
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~HtmlWriterTests"`
Expected: PASS (11 tests).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Viewer/HtmlWriter.cs tests/OKF4net.Tests/Viewer/HtmlWriterTests.cs
git commit -m "feat(viewer): write the generated site to disk"
```

---

### Task 9: The `okf render` CLI verb

**Files:**
- Modify: `src/OKF4net.Cli/OkfCli.cs`, `src/OKF4net.Cli/OKF4net.Cli.csproj`
- Test: `tests/OKF4net.Tests/CliTests.cs`

**Interfaces:**
- Consumes: `SiteModel.Build(Bundle)`, `HtmlWriter.Write(ViewerSite, string)`, and the CLI's existing `Positional`, `HasFlag`, `Load`, `CliOperationException` helpers.
- Produces: the `render` subcommand. Exit `0` on success (printing the count of files written), exit `1` with `error: …` on stderr otherwise.

- [ ] **Step 1: Write the failing test**

Append to `tests/OKF4net.Tests/CliTests.cs` (inside the class):

```csharp
    [Fact]
    public void Render_writes_a_site_and_reports_success()
    {
        using var dest = new TempDir();
        var outDir = Path.Combine(dest.Path, "site");

        var r = Run("render", BundlePath, "--out", outDir);

        Assert.Equal(0, r.Code);
        Assert.Equal("", r.Err);
        Assert.True(File.Exists(Path.Combine(outDir, "index.html")));
    }

    [Fact]
    public void Render_without_out_fails()
    {
        var r = Run("render", BundlePath);
        Assert.Equal(1, r.Code);
        Assert.Contains("--out", r.Err);
    }

    [Fact]
    public void Render_without_a_bundle_fails()
    {
        var r = Run("render");
        Assert.Equal(1, r.Code);
        Assert.Contains("error:", r.Err);
    }

    [Fact]
    public void Render_into_the_bundle_itself_fails()
    {
        var r = Run("render", BundlePath, "--out", Path.Combine(BundlePath, "site"));
        Assert.Equal(1, r.Code);
        Assert.Contains("error:", r.Err);
    }

    [Fact]
    public void Usage_mentions_the_render_verb()
    {
        var r = Run("--help");
        Assert.Equal(0, r.Code);
        Assert.Contains("render", r.Out);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~CliTests.Render"`
Expected: FAIL — exit code 1 with `unknown subcommand: render`.

- [ ] **Step 3: Reference the viewer from the CLI**

```bash
dotnet add src/OKF4net.Cli/OKF4net.Cli.csproj reference src/OKF4net.Viewer/OKF4net.Viewer.csproj
```

- [ ] **Step 4: Write minimal implementation**

Add `using OKF4net.Viewer;` at the top of `src/OKF4net.Cli/OkfCli.cs`.

In the `Usage` constant, add the verb to the commands list and the flag to `OPTIONS:`:

```text
        "    render <bundle> --out <dir>   Generate a browsable HTML site from a bundle";
```
```text
        "        --out <dir>      Output directory for `render`";
```

In the `cmd switch` in `Run`, add:

```csharp
            "render" => CmdRender(rest, stdout),
```

Add the option reader next to `HasFlag`:

```csharp
    /// <summary>
    /// The value following <paramref name="flag"/>, or <c>null</c> when the
    /// flag is absent. Throws when the flag is present but unvalued.
    /// </summary>
    private static string? FlagValue(string[] args, string flag)
    {
        var index = Array.IndexOf(args, flag);
        if (index < 0)
        {
            return null;
        }

        if (index + 1 >= args.Length)
        {
            throw new CliOperationException($"{flag} requires a value");
        }

        return args[index + 1];
    }
```

Add the verb implementation next to `CmdGraph`:

```csharp
    /// <summary>Implements the <c>render</c> subcommand.</summary>
    private static int CmdRender(string[] args, TextWriter stdout)
    {
        var path = Positional(args, "<bundle>");
        var outDir = FlagValue(args, "--out")
            ?? throw new CliOperationException("render requires --out <dir>");

        var bundle = Load(path);
        var site = SiteModel.Build(bundle);

        IReadOnlyList<string> written;
        try
        {
            written = HtmlWriter.Write(site, outDir);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new CliOperationException(e.Message);
        }

        stdout.Write($"wrote {written.Count} files to {outDir}\n");
        return 0;
    }
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~CliTests"`
Expected: PASS.

- [ ] **Step 6: Verify the Native AOT publish still succeeds**

Run: `dotnet publish src/OKF4net.Cli -c Release`
Expected: build succeeds with no trim/AOT warnings. `ViewerAssets` uses
`GetManifestResourceStream`, which is AOT-safe (embedded resources are not
trimmed), but this must be confirmed rather than assumed — CI runs this exact
check.

- [ ] **Step 7: Commit**

```bash
git add src/OKF4net.Cli tests/OKF4net.Tests/CliTests.cs
git commit -m "feat(cli): add the render verb generating a static bundle site"
```

---

### Task 10: Documentation

**Files:**
- Modify: `README.md`, `CLAUDE.md`, `CHANGELOG.md`, `ROADMAP.md`

**Interfaces:**
- Consumes: the finished `render` verb.
- Produces: no code.

- [ ] **Step 1: Update README.md**

In the `### As a CLI` section, after the `graph` example, add:

````markdown
Generate a browsable HTML site from a bundle:

```sh
okf render bundles/ga4 --out /tmp/ga4-site
# then open /tmp/ga4-site/index.html
```

The generated site is self-contained and opens straight off the filesystem —
no server needed. It is read-only; full-text search arrives with the planned
`okf serve` companion.
````

Add `OKF4net.Viewer` to the project table alongside the other `src/` projects.

- [ ] **Step 2: Update CLAUDE.md**

In the Architecture section, after the `OKF4net.Catalog.Hosting` bullet, add:

```markdown
- **`src/OKF4net.Viewer/`** — static HTML site generation for a bundle, referencing only `OKF4net` (BCL otherwise; zero `PackageReference`). Backs the `okf render` verb. Three units: `SiteModel` (pure `Bundle` → display-model projection), `HtmlWriter` (the only I/O), `ViewerAssets` (embedded CSS/JS). Markdown is rendered **client-side** by a vendored copy of marked (MIT, credited in `NOTICE`) — the generated page carries its raw markdown plus a link-rewiring table as an HTML-safe JSON payload, escaped by `HtmlSafeJson` so untrusted bundle content cannot break out of the `<script>` container. Raw HTML passthrough is disabled by overriding marked's `html` renderer hooks; marked has no `sanitize` option any more, so dropping that override silently re-enables script injection. No full-text search by design: a static site has no server to run `ConceptSearch`, and mirroring its weights in JS would fork the scorer — search lands with the planned `okf serve`.
```

Update the `okf` verb list in the same file from `(validate/info/index/graph/parse/fmt)` to `(validate/info/index/graph/parse/fmt/render)`.

- [ ] **Step 3: Update CHANGELOG.md**

Under the `Unreleased` heading (creating it if absent), add:

```markdown
### Added

- `okf render <bundle> --out <dir>` generates a self-contained, browsable HTML
  site from a bundle: one page per concept (frontmatter table + rendered body),
  a generated index, navigable cross-links with broken links flagged, and
  backlinks. Backed by the new zero-dependency `OKF4net.Viewer` project.
  Markdown renders client-side via a vendored copy of marked (MIT).
```

- [ ] **Step 4: Update ROADMAP.md**

Replace the `Bundle viewer` bullet under `## Next` with:

```markdown
- Bundle viewer: **static render shipped** as `okf render` (`OKF4net.Viewer`).
  The live-server half of [#40](https://github.com/jchable/okf4net/issues/40)
  remains open — it is what unlocks full-text search in the viewer, since a
  server can run `ConceptSearch` directly instead of mirroring its weights in
  JavaScript. Its implementation approach (zero-dep `HttpListener`, ASP.NET
  Core, or a standalone web tool) is still open.
```

- [ ] **Step 5: Verify the whole suite and formatting**

```bash
dotnet test OKF4net.sln
dotnet format OKF4net.sln --verify-no-changes
```

Expected: all tests pass; formatting produces no changes.

- [ ] **Step 6: Commit**

```bash
git add README.md CLAUDE.md CHANGELOG.md ROADMAP.md
git commit -m "docs(viewer): document the render verb and the viewer project"
```

---

### Task 11: Firsthand browser verification

Every guarantee so far is asserted by C# tests that never execute the JavaScript. The client-side rendering path — marked parsing, link rewiring, and the raw-HTML suppression of §8.2 — has **not actually run** at this point. This task runs it.

**Files:** none modified (unless a defect is found).

- [ ] **Step 1: Render a real bundle**

```bash
dotnet run --project src/OKF4net.Cli -- render bundles/ga4 --out /tmp/okf-viewer-check
```

Expected: `wrote N files to /tmp/okf-viewer-check`.

- [ ] **Step 2: Open the generated index in a browser**

Open `file:///tmp/okf-viewer-check/index.html` (use the Playwright MCP browser tools if available).

Verify, and report what you actually observed rather than what was expected:
- the index lists concepts grouped by type;
- clicking a concept link opens its page (no 404, no broken relative path);
- the concept page shows its frontmatter table and a **rendered** body — headings and lists are formatted, not raw markdown;
- a nested concept page loads its stylesheet (it is styled, not bare HTML);
- the browser console reports no errors.

- [ ] **Step 3: Verify raw HTML is suppressed with a hostile concept**

```bash
mkdir -p /tmp/okf-hostile
cat > /tmp/okf-hostile/index.md <<'EOF'
---
type: index
title: Hostile
description: XSS check bundle
---
EOF
cat > /tmp/okf-hostile/evil.md <<'EOF'
---
type: note
title: Evil
description: XSS check
---
Before.

<img src=x onerror="document.title='XSS'">
<script>document.title='XSS'</script>

After.
EOF
dotnet run --project src/OKF4net.Cli -- render /tmp/okf-hostile --out /tmp/okf-hostile-site
```

Open `file:///tmp/okf-hostile-site/evil.html` and verify:
- the page title is still `Evil`, **not** `XSS`;
- no image-load error fires from an injected `<img>`;
- the words `Before.` and `After.` both render.

**If the title becomes `XSS`, stop.** The renderer override in `viewer.js` is not taking effect — fix it before proceeding, and add a test that would have caught it.

- [ ] **Step 4: Report findings**

Report what was observed in Steps 2 and 3, including anything that did not work. Do not claim the viewer works without having run these steps.

---

## Self-Review

**Spec coverage:**

| Spec section | Covered by |
|---|---|
| §2 zero-dep project | Task 1 |
| §3 decisions (client render, marked, location, no search, copied style) | Tasks 1, 7, 9; search absent by construction |
| §5 architecture (SiteModel / HtmlWriter / ViewerAssets) | Tasks 2–8 |
| §6 output shape, `file://`, inline payload, shared index render path | Tasks 5, 8 |
| §7 link rewiring via table, relative hrefs, broken links | Tasks 2, 4, 7, 8 |
| §8.1 script-container escaping | Task 6 + Task 8 breakout test |
| §8.2 no raw HTML passthrough | Task 7 + Task 11 Step 3 |
| §9 edge cases (parse errors, `--out` behaviour, empty bundle, reserved files) | Tasks 5, 8, 9 |
| §10 tests, AOT, no goldens | Tasks 2–9; AOT in Task 9 Step 6 |
| §12 docs | Task 10 |

§9's path-traversal row is covered structurally rather than by a new test: `ConceptId` already rejects `..` segments and `ConceptIdTests.FromPath_still_rejects_dotdot_segments` locks that, so a concept id can never produce an output path outside `outDir`.

**Type consistency:** `ViewerLink(RawTarget, Href, Exists)`, `ViewerPage(Id, Title, RelativeHtmlPath, Frontmatter, Body, Links, Backlinks)`, `ViewerSite(BundleRoot, Pages, IndexMarkdown, ParseErrors)`, `ViewerFrontmatterEntry(Key, Value)`, `ViewerParseError(Path, Error)` are used identically in Tasks 3–9. `SiteModel.Build`/`RelativeHref`, `HtmlWriter.Write`, `HtmlSafeJson.Quote`, `ViewerAssets.Css`/`MarkedJs`/`ViewerJs` keep the same signatures throughout. `Backlinks` is typed `IReadOnlyList<ViewerLink>` (not `ConceptId`) in both its definition (Task 3) and its uses (Tasks 4, 8).

**Known risk:** the exact embedded-resource name prefix in Task 7 depends on the project's root namespace; Task 7 Step 6 carries the diagnostic if it does not resolve.
