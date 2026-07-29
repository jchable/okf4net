# OKF §10 Attested Computation — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implémenter OKF spec **§10 Attested Computation** (+ §4.2 heading `# Computation`, §6.2 champs *path-valued*) dans OKF4net pour atteindre la conformité v0.2 complète (0.3.0 pré-release).

**Architecture:** Trois couches. (1) **Cœur `OKF4net`** — projections typées du contrat §10.2, extraction de la computation §10.3, API de résolution §6.2, diagnostics validateur (tous Warning ; `Error` reste §11-only). (2) **Nouveau projet zéro-dép `OKF4net.Attestation`** — interfaces host (`IParameterBinder`/`IComputationExecutor`/`IAttester`) + orchestrateur `load → bind → execute → valide-receipt → attest → gate`, *errors-as-data*. (3) **`OKF4net.Agents`** — `okf_get_computation` (toujours) + `okf_run_computation` (si orchestrateur câblé) + enrichissement `okf_read_concept`.

**Tech Stack:** .NET 10 / C# 14, BCL only (zéro `PackageReference` hors Agents→Microsoft.Agents.AI), xUnit, YAML-subset maison (déjà compatible §10, vérifié), Native AOT pour le CLI.

**Design de référence (à lire avant de commencer) :** [`docs/superpowers/specs/2026-07-29-okf-attested-computation-design.md`](../specs/2026-07-29-okf-attested-computation-design.md) — signatures exactes, table de conformité §10/§6.2/§4.2, séquence de l'orchestrateur.

## Global Constraints

- **Zéro dépendance tierce, par projet.** `OKF4net` et `OKF4net.Attestation` : **BCL uniquement**, zéro `PackageReference`. `OKF4net.Agents` gagne **une `ProjectReference` first-party** vers `OKF4net.Attestation` (autorisé : la règle vise les packages tiers ; Agents référence déjà `OKF4net`). `OKF4net.Attestation` référence **uniquement** `OKF4net`.
- **`Error` = strictement §11.** Tous les diagnostics §10/§6.2 sont **Warning** (jamais Error) — §11/§5.3 : *« consumers MUST NOT reject »*. Un `Attested Computation` mal formé reste **conformant** (exit 0).
- **Chargement permissif §3.** Les projections ne throwent **jamais** ; l'orchestrateur est *errors-as-data* (échec attendu → `AttestationOutcome`, exception host capturée dans `Error`).
- **Chaque nouveau fichier .cs** commence par `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- **File-scoped namespaces, nullable enabled, XML doc sur toute API publique.** `dotnet format` clean ; **warnings = erreurs**.
- **Ne jamais éditer `tests/fixtures/golden/` existants** pour faire passer un test. Ce lot **ajoute** un nouveau golden (`validate-computation`), hand-vérifié contre le texte v0.2 (exception spec-bump documentée dans `tests/fixtures/README.md`).
- **Type de concept** : la string exacte est `Attested Computation` (avec espace), comparaison **Ordinal**.
- **Commandes** : `dotnet build OKF4net.sln` · `dotnet test OKF4net.sln` · `dotnet format OKF4net.sln --verify-no-changes`. Filtre ciblé : `dotnet test OKF4net.sln --filter "FullyQualifiedName~<Classe>"`.
- **Worktree** : créé à l'exécution via `superpowers:using-git-worktrees`, basé sur `dev` (qui inclut déjà les stratégies de resolver et les travaux de la session parallèle). Ne pas empiler sur une autre branche.

---

## File Structure

**Cœur `OKF4net` (créés)**
- `src/OKF4net/AttestedComputation.cs` — value types §10.2 (`ComputationParameter`, `Executor`, `Attester`, `AttestedComputationContract`) + `SanctionedComputation`/`ComputationSource` (§10.3).
- `src/OKF4net/FrontmatterResource.cs` — `FrontmatterResource`, `FrontmatterResourceKind`, `ResourceResolutionStatus` (§6.2).
- `src/OKF4net/Internal/ComputationExtractor.cs` — extraction du premier bloc fencé sous `# Computation`.

**Cœur `OKF4net` (modifiés)**
- `src/OKF4net/Frontmatter.cs` — `IsAttestedComputation`, `ComputationContract`, `KnownKeys` étendu.
- `src/OKF4net/OkfDocument.cs` — `Computation()`, `FrontmatterResources()`.
- `src/OKF4net/Bundle.cs` — `TryResolveResource(...)`, `ReadResourceText(...)`.
- `src/OKF4net/Validate.cs` — diagnostics §10 + §6.2.

**Nouveau projet `OKF4net.Attestation`**
- `src/OKF4net.Attestation/OKF4net.Attestation.csproj`
- `src/OKF4net.Attestation/Contracts.cs` — `IParameterBinder`, `IComputationExecutor`, `IAttester`, `IAttestationRuntime`, `IAttestationRuntimeRegistry`.
- `src/OKF4net.Attestation/Values.cs` — `BoundComputation`, `Receipt`, `AttestationVerdict`, `AttestationContext`, `AttestationOutcome`, `StaleState`.
- `src/OKF4net.Attestation/AttestationRuntimeRegistry.cs` — impl dictionnaire concrète.
- `src/OKF4net.Attestation/AttestationOrchestrator.cs` — orchestrateur.

**`OKF4net.Agents` (modifiés)**
- `src/OKF4net.Agents/OkfBundleTools.cs` — 3 tools + ctor orchestrateur optionnel.
- `src/OKF4net.Agents/OKF4net.Agents.csproj` — `ProjectReference` → Attestation.

**Tests (créés)** : `AttestedComputationTests.cs`, `ComputationExtractorTests.cs`, `FrontmatterResourceTests.cs`, `Attestation/AttestationOrchestratorTests.cs` (+ `Attestation/FakeRuntime.cs`), `Agents/OkfComputationToolsTests.cs`. **(modifiés)** : `ValidateTests.cs`, `GoldenParityTests.cs`.

**Fixtures/goldens (créés)** : `tests/fixtures/okf_v02_computation/**`, `tests/fixtures/golden/validate-computation.out`, `validate-computation.exitcode`.

**Packaging/docs (modifiés)** : `OKF4net.sln`, `.github/workflows/release.yml`, `CHANGELOG.md`, `README.md`, `CLAUDE.md`, `tests/fixtures/README.md`.

---

## Task 1 : Value types du contrat §10.2 + accès Frontmatter

**Files:**
- Create: `src/OKF4net/AttestedComputation.cs`
- Modify: `src/OKF4net/Frontmatter.cs` (ajouter `IsAttestedComputation`, `ComputationContract` ; étendre `KnownKeys`)
- Test: `tests/OKF4net.Tests/AttestedComputationTests.cs`

**Interfaces:**
- Consumes: `Frontmatter` (getters paresseux existants — s'inspirer de `Lifecycle`/`Provenance` dans `Frontmatter.cs`), `YamlMapping`/`YamlSequence`/`YamlString` (`OKF4net.Yaml`), `Actor` (§7, existant).
- Produces:
  ```csharp
  public enum ComputationSource { Inline, File }
  public readonly record struct ComputationParameter(string Name, string? Type, bool Required);
  public readonly record struct Executor(string? Resource, IReadOnlyList<string> Receipt);
  public readonly record struct Attester(string? Resource);
  public readonly record struct AttestedComputationContract(
      string? Runtime, IReadOnlyList<ComputationParameter> Parameters,
      string? ComputationPath, Executor? Executor, Attester? Attester);
  public readonly record struct SanctionedComputation(ComputationSource Source, string? InlineCode, string? Path);
  // sur Frontmatter :
  public bool IsAttestedComputation { get; }
  public AttestedComputationContract ComputationContract { get; }
  ```

- [ ] **Step 1 : Test qui échoue** — `tests/OKF4net.Tests/AttestedComputationTests.cs`

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using OKF4net.Yaml;
using Xunit;

namespace OKF4net.Tests;

public class AttestedComputationTests
{
    private static Frontmatter Parse(string yaml) =>
        Frontmatter.FromMapping((YamlMapping)YamlValue.Parse(yaml));

    [Fact]
    public void Projects_full_contract_from_frontmatter()
    {
        var fm = Parse(
            "type: Attested Computation\n" +
            "runtime: bigquery\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            "computation: references/computations/revenue.sql\n" +
            "executor:\n  resource: references/skills/run-on-bq.md\n  receipt: [job_id, executed_sql, result]\n" +
            "attester:\n  resource: references/attesters/revenue.py\n");

        Assert.True(fm.IsAttestedComputation);
        var c = fm.ComputationContract;
        Assert.Equal("bigquery", c.Runtime);
        var p = Assert.Single(c.Parameters);
        Assert.Equal("year", p.Name);
        Assert.Equal("integer", p.Type);
        Assert.True(p.Required);
        Assert.Equal("references/computations/revenue.sql", c.ComputationPath);
        Assert.Equal("references/skills/run-on-bq.md", c.Executor!.Value.Resource);
        Assert.Equal(new[] { "job_id", "executed_sql", "result" }, c.Executor!.Value.Receipt);
        Assert.Equal("references/attesters/revenue.py", c.Attester!.Value.Resource);
    }

    [Fact]
    public void Non_computation_type_is_not_attested_and_projects_empty()
    {
        var fm = Parse("type: Metric\ntitle: Revenue\n");
        Assert.False(fm.IsAttestedComputation);
        Assert.Null(fm.ComputationContract.Runtime);
        Assert.Empty(fm.ComputationContract.Parameters);
    }

    [Fact]
    public void Malformed_fields_never_throw_and_degrade()
    {
        // runtime absent ; parameters entrée sans name ; executor.receipt non-liste
        var fm = Parse(
            "type: Attested Computation\n" +
            "parameters:\n  - { type: integer }\n" +
            "executor:\n  receipt: nope\n");
        var c = fm.ComputationContract;              // ne throw pas
        Assert.Null(c.Runtime);
        Assert.Equal(string.Empty, c.Parameters[0].Name);   // name absent → ""
        Assert.Empty(c.Executor!.Value.Receipt);            // receipt non-liste → []
    }
}
```

- [ ] **Step 2 : Vérifier l'échec** — `dotnet test OKF4net.sln --filter "FullyQualifiedName~AttestedComputationTests"` → FAIL (types/membres inexistants).

- [ ] **Step 3 : Implémenter** — `src/OKF4net/AttestedComputation.cs` : les value types ci-dessus + un projecteur tolérant. **Pattern à suivre** : `Trust.cs` (`Trust.ParseGenerated`/`ParseVerified` — le plus proche analogue multi-champs). Le vrai style §5 est des méthodes `public static` prenant **un seul champ `YamlValue?`** (ex. `Provenance.ParseSources(_map.Get("sources"))`). Ici, comme le contrat §10 corrèle plusieurs clés, on **déroge volontairement** avec un projecteur whole-map `public static AttestedComputationContract AttestedComputation.Project(YamlMapping map)` — jamais throw. Points clés :
  - `Parameters` : lire la valeur `parameters` ; si `YamlSequence`, mapper chaque `YamlMapping` → `ComputationParameter(name ?? "", type, required)` (`required` = `YamlBool` true sinon false) ; sinon `[]`.
  - `Executor` : si `executor` est un `YamlMapping` → `new Executor(resource-string-or-null, receipt-list-or-empty)` où `Receipt` = les items string d'un `YamlSequence`, sinon `[]`. Absent → `null`.
  - `Attester` : idem, `Resource` = string ou null. Absent → `null`.
  - `Runtime`/`ComputationPath` : lecture scalaire string ou null.

  Dans `src/OKF4net/Frontmatter.cs` : ajouter
  ```csharp
  public bool IsAttestedComputation =>
      string.Equals(Type, "Attested Computation", StringComparison.Ordinal);
  public AttestedComputationContract ComputationContract => AttestedComputation.Project(_map);
  ```
  et étendre `KnownKeys` avec `"runtime", "parameters", "computation", "executor", "attester"` (commentaire `// §10`). *(Si `Frontmatter.Type` n'existe pas comme getter, utiliser la lecture scalaire de `type` déjà employée par `RequiredKeys`/`Validate`.)*

- [ ] **Step 4 : Vérifier le succès** — `dotnet test OKF4net.sln --filter "FullyQualifiedName~AttestedComputationTests"` → PASS.

- [ ] **Step 5 : Commit**
```bash
git add src/OKF4net/AttestedComputation.cs src/OKF4net/Frontmatter.cs tests/OKF4net.Tests/AttestedComputationTests.cs
git commit -m "feat(core): project §10 Attested Computation contract from frontmatter"
```

---

## Task 2 : Extraction de la computation §10.3 / §4.2

**Files:**
- Create: `src/OKF4net/Internal/ComputationExtractor.cs`
- Modify: `src/OKF4net/OkfDocument.cs` (ajouter `Computation()`)
- Test: `tests/OKF4net.Tests/ComputationExtractorTests.cs`

**Interfaces:**
- Consumes: `Internal/LfLines.Split`, `Frontmatter.ComputationContract` (Task 1), `OkfDocument.Body`/`.Frontmatter`.
- Produces: `public SanctionedComputation OkfDocument.Computation();`

- [ ] **Step 1 : Test qui échoue** — `tests/OKF4net.Tests/ComputationExtractorTests.cs`

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net;
using Xunit;

namespace OKF4net.Tests;

public class ComputationExtractorTests
{
    private static OkfDocument Doc(string frontmatter, string body) =>
        OkfDocument.Parse("---\n" + frontmatter + "---\n" + body);

    [Fact]
    public void Inline_extracts_first_fenced_block_under_Computation_heading()
    {
        var doc = Doc("type: Attested Computation\nruntime: bigquery\n",
            "# Computation\n\n```sql\nSELECT SUM(amount) AS revenue\nFROM t\nWHERE fiscal_year = @year\n```\n");
        var c = doc.Computation();
        Assert.Equal(ComputationSource.Inline, c.Source);
        Assert.Equal("SELECT SUM(amount) AS revenue\nFROM t\nWHERE fiscal_year = @year", c.InlineCode);
        Assert.Null(c.Path);
    }

    [Fact]
    public void File_based_takes_path_and_ignores_body()
    {
        var doc = Doc("type: Attested Computation\ncomputation: references/computations/revenue.sql\n", "no fence here\n");
        var c = doc.Computation();
        Assert.Equal(ComputationSource.File, c.Source);
        Assert.Equal("references/computations/revenue.sql", c.Path);
        Assert.Null(c.InlineCode);
    }

    [Fact]
    public void Tilde_fence_supported_and_indented_block_ignored()
    {
        var doc = Doc("type: Attested Computation\n",
            "# Computation\n\n~~~\nSELECT 1\n~~~\n");
        Assert.Equal("SELECT 1", doc.Computation().InlineCode);

        var indented = Doc("type: Attested Computation\n", "# Computation\n\n    SELECT 1\n");
        Assert.Null(indented.Computation().InlineCode);   // indenté ≠ fencé (on suit le texte spec)
    }

    [Fact]
    public void No_heading_or_no_fence_yields_no_inline()
    {
        Assert.Null(Doc("type: Attested Computation\n", "no heading\n").Computation().InlineCode);
        Assert.Null(Doc("type: Attested Computation\n", "# Computation\n\nprose only\n").Computation().InlineCode);
    }
}
```

- [ ] **Step 2 : Vérifier l'échec** — `dotnet test OKF4net.sln --filter "FullyQualifiedName~ComputationExtractorTests"` → FAIL.

- [ ] **Step 3 : Implémenter** — `src/OKF4net/Internal/ComputationExtractor.cs` :
  ```csharp
  internal static class ComputationExtractor
  {
      // Renvoie le texte du premier bloc fencé (``` ou ~~~) suivant un heading
      // ATX H1 dont le texte trimmé vaut exactement "Computation", fences exclues ;
      // null si absent. Splitte via LfLines.Split.
      internal static string? ExtractInline(string body) { /* voir règles ci-dessous */ }
  }
  ```
  Règles : parcourir les lignes ; repérer la première dont `TrimEnd()` == `"# Computation"` ; à partir de la ligne suivante, ignorer les blanches, trouver la première ligne dont le `TrimStart()` commence par ` ``` ` **ou** `~~~` (mémoriser le marqueur + sa longueur ≥3) ; accumuler les lignes suivantes jusqu'à la ligne de fermeture (même marqueur, longueur ≥ ouverture) ; renvoyer `string.Join("\n", corps)`. Aucune fence trouvée avant une nouvelle ligne non-blanche/non-fence → renvoyer `null`. (Ne PAS extraire de bloc indenté.)

  Dans `src/OKF4net/OkfDocument.cs` :
  ```csharp
  public SanctionedComputation Computation()
  {
      var path = Frontmatter.ComputationContract.ComputationPath;
      if (!string.IsNullOrEmpty(path))
          return new SanctionedComputation(ComputationSource.File, null, path);
      return new SanctionedComputation(ComputationSource.Inline, ComputationExtractor.ExtractInline(Body), null);
  }
  ```

- [ ] **Step 4 : Vérifier le succès** — filtre `ComputationExtractorTests` → PASS.

- [ ] **Step 5 : Commit**
```bash
git add src/OKF4net/Internal/ComputationExtractor.cs src/OKF4net/OkfDocument.cs tests/OKF4net.Tests/ComputationExtractorTests.cs
git commit -m "feat(core): extract §10.3 sanctioned computation (fenced # Computation or file path)"
```

---

## Task 3 : API §6.2 — énumération + résolution path-safe

**Files:**
- Create: `src/OKF4net/FrontmatterResource.cs`
- Modify: `src/OKF4net/OkfDocument.cs` (`FrontmatterResources()`), `src/OKF4net/Bundle.cs` (`TryResolveResource`, `ReadResourceText`)
- Test: `tests/OKF4net.Tests/FrontmatterResourceTests.cs`

**Interfaces:**
- Consumes: `Internal/ReparsePoints` (`CanonicalizeRoot`, `IsReparsePoint`, `HasReparsePointAncestor`), `Internal/OkfEncodings.Strict`, `Concept` (`.Path`), `Bundle.Root`, `Frontmatter.ComputationContract` + lecture `resource`/`sources[].resource`.
- Produces:
  ```csharp
  public enum FrontmatterResourceKind { Url, BundleRelative, Relative }
  public enum ResourceResolutionStatus { Url, Resolved, Missing, Unsafe }
  public readonly record struct FrontmatterResource(string Field, string RawPath, FrontmatterResourceKind Kind);
  public IReadOnlyList<FrontmatterResource> OkfDocument.FrontmatterResources();
  public bool Bundle.TryResolveResource(Concept concept, string rawPath, out string? absolutePath, out ResourceResolutionStatus status);
  public string Bundle.ReadResourceText(string absolutePath);
  ```

- [ ] **Step 1 : Test qui échoue** — `tests/OKF4net.Tests/FrontmatterResourceTests.cs`

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Linq;
using OKF4net;
using Xunit;

namespace OKF4net.Tests;

public class FrontmatterResourceTests
{
    [Fact]
    public void Enumerates_and_classifies_the_five_path_valued_fields()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md",
            "---\ntype: Attested Computation\ncomputation: ../refs/revenue.sql\n" +
            "executor: { resource: /skills/run.md, receipt: [job_id] }\n" +
            "attester: { resource: https://ex/att.py }\n" +
            "sources:\n  - { id: s, resource: ./policy.md }\n---\nbody\n");
        var bundle = Bundle.Load(tmp.Path);
        var doc = bundle.Concepts.Single(c => c.Id.ToString() == "c/comp").Document;

        var res = doc.FrontmatterResources();
        Assert.Contains(res, r => r.Field == "computation" && r.Kind == FrontmatterResourceKind.Relative);
        Assert.Contains(res, r => r.Field == "executor.resource" && r.Kind == FrontmatterResourceKind.BundleRelative);
        Assert.Contains(res, r => r.Field == "attester.resource" && r.Kind == FrontmatterResourceKind.Url);
        Assert.Contains(res, r => r.Field == "sources[0].resource" && r.Kind == FrontmatterResourceKind.Relative);
    }

    [Fact]
    public void Resolves_missing_relative_path_as_Missing()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\ncomputation: ./nope.sql\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single();
        Assert.True(bundle.TryResolveResource(concept, "./nope.sql", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Missing, status);

        tmp.Write("c/revenue.sql", "SELECT 1\n");
        var bundle2 = Bundle.Load(tmp.Path);
        var c2 = bundle2.Concepts.Single(c => c.Id.ToString() == "c/comp");
        Assert.True(bundle2.TryResolveResource(c2, "./revenue.sql", out var abs2, out var st2));
        Assert.Equal(ResourceResolutionStatus.Resolved, st2);
        Assert.Equal("SELECT 1\n", bundle2.ReadResourceText(abs2!));
    }

    [Fact]
    public void Url_is_not_resolved()
    {
        using var tmp = new TempDir();
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        Assert.True(bundle.TryResolveResource(bundle.Concepts.Single(), "https://x/y", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Url, status);
        Assert.Null(abs);
    }

    [Fact]
    public void Bundle_relative_resolves_from_root_and_escaping_path_is_unsafe()
    {
        using var tmp = new TempDir();
        tmp.Write("skills/run.md", "run\n");
        tmp.Write("c/comp.md", "---\ntype: Attested Computation\n---\n");
        var bundle = Bundle.Load(tmp.Path);
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "c/comp");

        // "/skills/run.md" resolves from the BUNDLE ROOT (not the concept dir).
        Assert.True(bundle.TryResolveResource(concept, "/skills/run.md", out var abs, out var status));
        Assert.Equal(ResourceResolutionStatus.Resolved, status);
        Assert.Equal("run\n", bundle.ReadResourceText(abs!));

        // A relative path that climbs above the bundle root is Unsafe.
        Assert.True(bundle.TryResolveResource(concept, "../../escape.txt", out _, out var escaped));
        Assert.Equal(ResourceResolutionStatus.Unsafe, escaped);
    }
}
```

- [ ] **Step 2 : Vérifier l'échec** — filtre `FrontmatterResourceTests` → FAIL.

- [ ] **Step 3 : Implémenter** —
  - `src/OKF4net/FrontmatterResource.cs` : les enums + record + classification `KindOf(rawPath)` : `Url` si match `^[A-Za-z][A-Za-z0-9+.-]*://` ; `BundleRelative` si commence par `/` ; sinon `Relative`.
  - `OkfDocument.FrontmatterResources()` : collecter, dans l'ordre, `resource`, `sources[i].resource` (labellisé `sources[i].resource`), `computation`, `executor.resource`, `attester.resource` (ne garder que les valeurs string non vides). Réutiliser `ComputationContract` (Task 1) pour computation/executor/attester ; lire `resource`/`sources` via les getters existants (`Frontmatter.Sources` etc.).
  - `Bundle.TryResolveResource` : `Url` → `absolutePath=null, status=Url, return true`. Sinon, calculer le chemin candidat :
    - **`BundleRelative`** (commence par `/`) : **retirer les séparateurs de tête** (`rawPath.TrimStart('/', '\\')`) AVANT de combiner avec `Bundle.Root` — sinon `Path.Combine(root, "/x")` **ignore `root`** sur Windows (footgun). Puis `Path.GetFullPath`.
    - **`Relative`** : combiner avec le répertoire du concept `Path.GetDirectoryName(concept.Path)` + `Path.GetFullPath`.
    - **Sécurité** (réutiliser les helpers existants de `Internal/ReparsePoints.cs`) : `fullRoot = ReparsePoints.CanonicalizeRoot(Root)` ; si `!ReparsePoints.IsWithinBundleRoot(fullRoot, candidate)` **ou** `ReparsePoints.IsReparsePoint(candidate)` **ou** `ReparsePoints.HasReparsePointAncestor(fullRoot, candidate)` → `status=Unsafe`. Sinon `File.Exists(candidate)` → `Resolved` / `Missing`.
  - `Bundle.ReadResourceText(absolutePath)` : `File.ReadAllText(absolutePath, OkfEncodings.Strict)`. *(À n'appeler que sur un `absolutePath` issu d'un `Resolved` — la sécurité est faite en amont.)*

- [ ] **Step 4 : Vérifier le succès** — filtre `FrontmatterResourceTests` → PASS.

- [ ] **Step 5 : Commit**
```bash
git add src/OKF4net/FrontmatterResource.cs src/OKF4net/OkfDocument.cs src/OKF4net/Bundle.cs tests/OKF4net.Tests/FrontmatterResourceTests.cs
git commit -m "feat(core): §6.2 path-valued frontmatter resource enumeration + path-safe resolution"
```

---

## Task 4 : Diagnostics validateur §10 + §6.2

**Files:**
- Modify: `src/OKF4net/Validate.cs`
- Test: `tests/OKF4net.Tests/ValidateTests.cs` (ajouts)

**Interfaces:**
- Consumes: `Frontmatter.IsAttestedComputation`/`ComputationContract` (T1), `OkfDocument.Computation()` (T2), `OkfDocument.FrontmatterResources()` + `Bundle.TryResolveResource` (T3), l'API existante `Diagnostic(Severity, path, conceptId, message)` + `Severity` enum.
- Produces: nouveaux `Warning` (voir tableau) ; **aucun** `Error` nouveau.

- [ ] **Step 1 : Test qui échoue** — ajouter à `tests/OKF4net.Tests/ValidateTests.cs`

```csharp
[Fact]
public void Attested_computation_missing_runtime_warns_but_stays_conformant()
{
    using var tmp = new TempDir();
    tmp.Write("c/comp.md", "---\ntype: Attested Computation\n# Computation absent + pas de computation:\n---\n");
    var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
    Assert.True(report.IsConformant);                                   // Error reste §11-only
    Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("runtime"));
    Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("no computation"));
}

[Fact]
public void Both_inline_and_path_warns()
{
    using var tmp = new TempDir();
    tmp.Write("c/comp.md",
        "---\ntype: Attested Computation\nruntime: bigquery\ncomputation: ./x.sql\n---\n# Computation\n\n```\nSELECT 1\n```\n");
    tmp.Write("c/x.sql", "SELECT 1\n");
    var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
    Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("both inline and"));
}

[Fact]
public void Broken_frontmatter_path_warns()
{
    using var tmp = new TempDir();
    tmp.Write("c/comp.md",
        "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { resource: ./missing.md, receipt: [job_id] }\n---\n# Computation\n\n```\nSELECT 1\n```\n");
    var report = BundleValidator.Validate(Bundle.Load(tmp.Path));
    Assert.Contains(report.Diagnostics, d => d.Severity == Severity.Warning && d.Message.Contains("not found"));
}
```
*(Adapter `BundleValidator.Validate(...)` / `report.IsConformant` / `report.Diagnostics` aux noms réels du projet — voir les tests §5 existants dans `ValidateTests.cs`.)*

- [ ] **Step 2 : Vérifier l'échec** — filtre `ValidateTests` → FAIL sur les 3 nouveaux.

- [ ] **Step 3 : Implémenter** — dans `Validate.cs`, après les checks §5, pour chaque concept :
  - **§10** (si `fm.IsAttestedComputation`) : `runtime` vide → Warning `attested computation missing required 'runtime'` ; chaque `parameters` sans `name` → Warning `parameter entry missing 'name'` ; `Computation()` `InlineCode==null` **et** `ComputationPath==null` → Warning `attested computation has no computation (inline '# Computation' or 'computation:' path)` ; `InlineCode!=null` **et** `ComputationPath!=null` → Warning `computation specified both inline and via 'computation:'` ; `executor` présent avec `receipt` non-liste (détecté en projetant : si la clé `executor.receipt` existe dans le YAML mais `Executor.Receipt` est vide alors qu'un scalaire était présent — voir note) → Warning ; `attester.resource` chaîne vide → Warning.
  - **§6.2** (tous concepts) : pour chaque `doc.FrontmatterResources()` de `Kind != Url`, `bundle.TryResolveResource(...)` → `Missing` → Warning `frontmatter path '<field>' → '<raw>' not found` ; `Unsafe` → Warning `... escapes the bundle`.
  - **Sévérité : Warning uniquement.** Ne jamais toucher `IsConformant`.
  - *Note receipt-non-liste* : le plus simple est de re-lire la valeur brute `executor.receipt` du `YamlMapping` ; si présente et pas un `YamlSequence` → Warning. (La projection `Executor.Receipt` renvoie déjà `[]` dans ce cas ; le validateur distingue « absent » de « présent mal typé ».)

- [ ] **Step 4 : Vérifier le succès** — filtre `ValidateTests` → PASS ; **puis suite complète du cœur** `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests"` pour garantir zéro régression des goldens/validate existants.

- [ ] **Step 5 : Commit**
```bash
git add src/OKF4net/Validate.cs tests/OKF4net.Tests/ValidateTests.cs
git commit -m "feat(core): validator warnings for §10 attested computation + §6.2 broken/unsafe paths"
```

---

## Task 5 : Projet `OKF4net.Attestation` — interfaces + value types + registre

**Files:**
- Create: `src/OKF4net.Attestation/OKF4net.Attestation.csproj`, `Contracts.cs`, `Values.cs`, `AttestationRuntimeRegistry.cs`
- Modify: `OKF4net.sln` (ajouter le projet)
- Test: `tests/OKF4net.Tests/Attestation/AttestationValuesTests.cs`

**Interfaces:**
- Consumes: `OKF4net` (`AttestedComputationContract`, `SanctionedComputation`).
- Produces: toutes les interfaces + value types de la §9 du design (voir `Contracts.cs`/`Values.cs` ci-dessous) + `AttestationRuntimeRegistry : IAttestationRuntimeRegistry`.

- [ ] **Step 1 : csproj + README + sln** — `src/OKF4net.Attestation/OKF4net.Attestation.csproj` : cible `net10.0`, `PackageId=OKF4net.Attestation`, `Description` (« Host-plugged orchestration of OKF v0.2 §10 Attested Computations… »), `PackageLicenseExpression=LGPL-3.0-or-later`, **zéro `PackageReference`**, une `ProjectReference` vers `../OKF4net/OKF4net.csproj`. Reprendre le `PropertyGroup` de packaging de `OKF4net.Catalog.csproj` **verbatim** — MAIS il contient `<PackageReadmeFile>README.md</PackageReadmeFile>` + un `<None Include="README.md" Pack="true" PackagePath="\" />` **et** le pack de `NOTICE`/`LICENSE.Apache-2.0` : donc **créer `src/OKF4net.Attestation/README.md`** (sinon `dotnet pack` échoue `NU5039`, découvert tard en Task 9/CI) et garder les items `None` `NOTICE`/`LICENSE.Apache-2.0` comme Catalog. Le README : titre + 1 paragraphe (rôle : orchestration host de §10 au-dessus d'`OKF4net`, zéro-dép) + un mini snippet `AttestationOrchestrator`. Ajouter le projet à `OKF4net.sln` (`dotnet sln OKF4net.sln add src/OKF4net.Attestation/OKF4net.Attestation.csproj`).

- [ ] **Step 2 : Test qui échoue** — `tests/OKF4net.Tests/Attestation/AttestationValuesTests.cs`

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using OKF4net.Attestation;
using Xunit;

namespace OKF4net.Tests.Attestation;

public class AttestationValuesTests
{
    [Fact]
    public void Registry_returns_registered_runtime_and_misses_unknown()
    {
        var rt = new FakeRuntime();
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = rt });
        Assert.True(reg.TryGet("bigquery", out var found));
        Assert.Same(rt, found);
        Assert.False(reg.TryGet("python", out _));
    }
}
```
*(FakeRuntime est créé au Step 3 — voir aussi Task 6.)*

- [ ] **Step 3 : Implémenter** —
  - `Contracts.cs` : `IParameterBinder`, `IComputationExecutor`, `IAttester`, `IAttestationRuntime`, `IAttestationRuntimeRegistry` — signatures exactes du design §9.1 (méthodes `ValueTask<…> …Async(..., CancellationToken cancellationToken = default)`).
  - `Values.cs` : `BoundComputation`, `Receipt`, `AttestationVerdict`, `AttestationContext`, `AttestationOutcome`, `enum StaleState { Fresh, Stale, Unknown }` — signatures exactes du design §9.2.
  - `AttestationRuntimeRegistry.cs` :
    ```csharp
    public sealed class AttestationRuntimeRegistry(IReadOnlyDictionary<string, IAttestationRuntime> runtimes)
        : IAttestationRuntimeRegistry
    {
        public bool TryGet(string runtime, out IAttestationRuntime? found)
        {
            if (runtime is not null && runtimes.TryGetValue(runtime, out var r)) { found = r; return true; }
            found = null; return false;
        }
    }
    ```
  - `tests/OKF4net.Tests/Attestation/FakeRuntime.cs` (helper test-only) : un `IAttestationRuntime` configurable dont `Binder`/`Executor`/`Attester` délèguent à des `Func`/valeurs fixées (utilisé ici + Task 6). Champs : `Func<...,BoundComputation>`, `Func<...,Receipt>`, `Func<...,AttestationVerdict>`, avec des défauts *happy path*.

- [ ] **Step 4 : Vérifier le succès** — `dotnet build OKF4net.sln` (0 warning) + `dotnet test OKF4net.sln --filter "FullyQualifiedName~AttestationValuesTests"` → PASS.

- [ ] **Step 5 : Commit**
```bash
git add src/OKF4net.Attestation/ OKF4net.sln tests/OKF4net.Tests/Attestation/AttestationValuesTests.cs tests/OKF4net.Tests/Attestation/FakeRuntime.cs
git commit -m "feat(attestation): new zero-dep project — host contracts, value types, runtime registry"
```

---

## Task 6 : `AttestationOrchestrator`

**Files:**
- Create: `src/OKF4net.Attestation/AttestationOrchestrator.cs`
- Test: `tests/OKF4net.Tests/Attestation/AttestationOrchestratorTests.cs`

**Interfaces:**
- Consumes: `Bundle` (`Concepts`, `TryResolveResource`, `ReadResourceText`), `OkfDocument.Computation()`, `Frontmatter.ComputationContract`/`Lifecycle`, `IOkfClock`/`SystemClock`/`StalePolicy` (cœur), les contrats/value types (Task 5), `FakeRuntime` (Task 5).
- Produces:
  ```csharp
  public sealed class AttestationOrchestrator
  {
      public AttestationOrchestrator(IAttestationRuntimeRegistry runtimes, IOkfClock? clock = null, StalePolicy? defaultPolicy = null);
      public ValueTask<AttestationOutcome> RunAsync(Bundle bundle, ConceptId conceptId,
          IReadOnlyDictionary<string, object?> parameterValues, StalePolicy? policy = null, CancellationToken cancellationToken = default);
  }
  ```

- [ ] **Step 1 : Test qui échoue** — `tests/OKF4net.Tests/Attestation/AttestationOrchestratorTests.cs` (extraits représentatifs — écrire les 8 cas)

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Threading.Tasks;
using OKF4net;
using OKF4net.Attestation;
using Xunit;

namespace OKF4net.Tests.Attestation;

public class AttestationOrchestratorTests
{
    private static (Bundle, ConceptId) InlineComputation(TempDir tmp)
    {
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\n" +
            "parameters:\n  - { name: year, type: integer, required: true }\n" +
            "executor: { resource: references/run.md, receipt: [job_id, result] }\n" +
            "attester: { resource: references/att.py }\n---\n# Computation\n\n```sql\nSELECT @year\n```\n");
        return (Bundle.Load(tmp.Path), ConceptId.Parse("c/rev"));
    }

    [Fact]
    public async Task Happy_path_is_displayable()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(receipt: new() { ["job_id"] = "j1", ["result"] = 42 })
        });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new(2026, 1, 1)));
        var outcome = await orch.RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.True(outcome.Displayable);
        Assert.True(outcome.Verdict!.Value.Passed);
        Assert.True(outcome.ReceiptShapeOk);
    }

    [Fact]
    public async Task Receipt_missing_declared_field_is_not_displayable()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(receipt: new() { ["job_id"] = "j1" })   // 'result' manquant
        });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.False(outcome.ReceiptShapeOk);
        Assert.False(outcome.Displayable);
    }

    [Fact]
    public async Task Missing_required_parameter_fails_before_binding()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(bundle, id, new Dictionary<string, object?>());
        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("year"));
    }

    [Fact]
    public async Task Unregistered_runtime_reports_reason()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var outcome = await new AttestationOrchestrator(new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>()))
            .RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.False(outcome.Displayable);
        Assert.Contains(outcome.Reasons, r => r.Contains("runtime"));
    }

    [Fact]
    public async Task Stale_concept_gated_under_strict_policy()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nstale_after: 2025-01-01\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.Passing() });
        var orch = new AttestationOrchestrator(reg, clock: new FixedClock(new(2026, 1, 1)));
        var outcome = await orch.RunAsync(bundle: Bundle.Load(tmp.Path), conceptId: ConceptId.Parse("c/rev"),
            parameterValues: new Dictionary<string, object?>(), policy: StalePolicy.Strict);
        Assert.Equal(StaleState.Stale, outcome.Stale);
        Assert.False(outcome.Displayable);
    }

    [Fact]
    public async Task Executor_exception_is_captured_not_thrown()
    {
        using var tmp = new TempDir();
        var (bundle, id) = InlineComputation(tmp);
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = FakeRuntime.ThrowingExecutor() });
        var outcome = await new AttestationOrchestrator(reg).RunAsync(bundle, id, new Dictionary<string, object?> { ["year"] = 2026 });
        Assert.False(outcome.Displayable);
        Assert.NotNull(outcome.Error);
    }

    // + Attest_negative_verdict_not_displayable, File_based_computation_resolved_and_read
}
```

- [ ] **Step 2 : Vérifier l'échec** — filtre `AttestationOrchestratorTests` → FAIL.

- [ ] **Step 3 : Implémenter** `AttestationOrchestrator.RunAsync` selon la **séquence exacte du design §9.3** (10 étapes), *errors-as-data* (jamais throw ; `try/catch` autour de bind/execute/attest → `Error` + `Reasons`, `Displayable=false`) :
  1. `bundle.Concepts` → concept par `conceptId` ; introuvable ou `!IsAttestedComputation` → outcome échec.
  2. `doc.Computation()` : `Inline` → `InlineCode` (null → échec) ; `File` → `bundle.TryResolveResource(concept, path)` ; `Resolved` → `ReadResourceText`, sinon échec.
  3. `runtimes.TryGet(contract.Runtime)` → sinon `no runtime configured for '<runtime>'`.
  4. paramètres requis présents dans `parameterValues` → sinon `missing required parameter '<name>'`.
  5. `Binder.BindAsync(contract, computation, values)` → `BoundComputation`.
  6. `Executor.ExecuteAsync(bound, contract)` → `Receipt`.
  7. `ReceiptShapeOk` = tous les `contract.Executor?.Receipt` ⊆ `receipt.Fields.Keys` (si pas d'executor/receipt → true).
  8. si `ReceiptShapeOk` : `Attester.AttestAsync(new AttestationContext(contract, computation, bound, values, receipt))` → verdict ; sinon on n'atteste pas.
  9. `Stale` depuis `fm.Lifecycle` + `clock.Today` ; `staleAdmitted = (policy ?? defaultPolicy ?? StalePolicy.Use).Admits(fm.Lifecycle, clock.Today)`.
  10. `Displayable = ReceiptShapeOk && (verdict?.Passed == true) && staleAdmitted` ; agréger `Reasons` ; retourner l'`AttestationOutcome`.
  Constructeur : `clock ??= new SystemClock()`, `defaultPolicy ??= StalePolicy.Use`.

- [ ] **Step 4 : Vérifier le succès** — filtre `AttestationOrchestratorTests` → PASS.

- [ ] **Step 5 : Commit**
```bash
git add src/OKF4net.Attestation/AttestationOrchestrator.cs tests/OKF4net.Tests/Attestation/AttestationOrchestratorTests.cs
git commit -m "feat(attestation): orchestrator — load→bind→execute→validate-receipt→attest→gate (errors-as-data)"
```

---

## Task 7 : Surface Agents (`okf_get_computation`, `okf_run_computation`, enrichissement read)

**Files:**
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs`, `src/OKF4net.Agents/OKF4net.Agents.csproj`
- Create: `tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs`
- Modify (tool-count/name assertions — **NE PAS OUBLIER**) : `tests/OKF4net.Tests/Agents/AIFunctionExposureTests.cs` (c'est ICI que vivent les asserts « 9 tools » : `GetTools_returns_exactly_nine_tools` ~L36 `Assert.Equal(9, …)`, `GetTools_names_are_the_nine_snake_case_names_in_stable_order` ~L40–44, tableau `ExpectedNamesInOrder` ~L19–30, + doc de classe) **et** `tests/OKF4net.Tests/Mcp/OkfMcpServerTests.cs` (le set de 9 noms ~L90–99, `Assert.Equal(9, options.ToolCollection?.Count)` ~L142, et le read-only `Assert.Equal(6, …)` ~L120/L160).

**Impact tools (à intégrer aux tests ci-dessus).** `okf_get_computation` est **toujours** exposé (lecture seule) et n'exige **pas** d'orchestrateur. `OkfMcpToolset.Build` construit `OkfBundleTools` **sans** orchestrateur et itère `okf.GetTools()` en retirant les 3 write tools en read-only → donc, après ce lot : **MCP = 10 tools full / 7 read-only** (`okf_get_computation` étant read-only, il apparaît AUSSI en read-only — c'est **voulu**). `okf_run_computation` n'est jamais câblé dans MCP (pas d'orchestrateur) → absent partout côté MCP. Mettre à jour : `AIFunctionExposureTests` 9→10 + ajouter `okf_get_computation` au tableau de noms (à sa position dans `GetTools()`) ; `OkfMcpServerTests` 9→10, 6→7, et le set de noms.

**Interfaces:**
- Consumes: `OkfBundleTools` (patterns existants : `GetBundle()`, `RunTool` never-throw synchrone `Func<string>` ~L1022, `GetTools()` → `IList<AITool>` ~L148, `AIFunctionFactory.Create(method, "name")`), `AttestationOrchestrator` (Task 6).
- Produces (méthodes publiques `OkfBundleTools`) :
  ```csharp
  [Description("…")] public string GetComputation(string conceptId);
  [Description("…")] public string RunComputation(string conceptId, IReadOnlyDictionary<string, object?> parameterValues);
  // + surcharge ctor : public OkfBundleTools(string bundleRoot, AttestationOrchestrator? orchestrator)
  // + GetTools() ajoute okf_get_computation (toujours) et okf_run_computation (si orchestrator != null)
  ```

- [ ] **Step 1 : csproj** — ajouter `<ProjectReference Include="..\OKF4net.Attestation\OKF4net.Attestation.csproj" />` à `OKF4net.Agents.csproj`.

- [ ] **Step 2 : Test qui échoue** — `tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs`

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using OKF4net.Agents;
using OKF4net.Attestation;
using Xunit;

namespace OKF4net.Tests.Agents;

public class OkfComputationToolsTests
{
    [Fact]
    public void Get_computation_returns_contract_and_inline_code()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\n---\n# Computation\n\n```sql\nSELECT 1\n```\n");
        var tools = new OkfBundleTools(tmp.Path);
        var s = tools.GetComputation("c/rev");
        Assert.Contains("bigquery", s);
        Assert.Contains("SELECT 1", s);
    }

    [Fact]
    public void Run_computation_tool_absent_without_orchestrator()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\n---\n");
        var names = new OkfBundleTools(tmp.Path).GetTools().Select(t => t.Name).ToList();
        Assert.Contains("okf_get_computation", names);
        Assert.DoesNotContain("okf_run_computation", names);
    }

    [Fact]
    public async Task Run_computation_invokes_orchestrator_when_wired()
    {
        using var tmp = new TempDir();
        tmp.Write("c/rev.md", "---\ntype: Attested Computation\nruntime: bigquery\nexecutor: { resource: r.md, receipt: [job_id] }\n---\n# Computation\n\n```\nX\n```\n");
        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime>
        {
            ["bigquery"] = FakeRuntime.Passing(receipt: new() { ["job_id"] = "j1" })
        });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));
        Assert.Contains("okf_run_computation", tools.GetTools().Select(t => t.Name));
        var s = await Task.FromResult(tools.RunComputation("c/rev", new Dictionary<string, object?>()));
        Assert.Contains("displayable", s.ToLowerInvariant());
    }
}
```

- [ ] **Step 3 : Vérifier l'échec** — filtre `OkfComputationToolsTests` → FAIL.

- [ ] **Step 4 : Implémenter** — dans `OkfBundleTools.cs` :
  - Champ `private readonly AttestationOrchestrator? _orchestrator;` + surcharge de constructeur qui l'affecte (le ctor existant délègue avec `orchestrator: null`).
  - `GetComputation(conceptId)` : `RunTool` (never-throw) → charge le concept ; si `!IsAttestedComputation` → message d'erreur lisible ; sinon rend contrat (`ComputationContract`) + `Computation()` (inline code, ou texte résolu via `GetBundle().TryResolveResource`+`ReadResourceText` pour le cas fichier) en markdown.
  - `RunComputation(conceptId, values)` : `RunTool` → si `_orchestrator is null` → `"Error: no attestation runtime configured."` ; sinon `_orchestrator.RunAsync(GetBundle(), ConceptId.Parse(conceptId), values).GetAwaiter().GetResult()` et rend l'`AttestationOutcome` (Displayable, verdict, résumé receipt, raisons). *(AIFunction synchrone comme les autres tools ; l'orchestrateur est async, on bloque au bord — cohérent avec les tools sync existants.)*
    - **⚠️ Binding du paramètre dict — à vérifier.** Aucun tool existant ne prend un `IReadOnlyDictionary<string, object?>` (tous prennent des scalaires `string`). Vérifier que `AIFunctionFactory.Create` binde bien un paramètre `IReadOnlyDictionary<string, object?> parameterValues` depuis un objet JSON (écrire un test de binding e2e, cf. `AIFunctionExposureTests` ~L183 qui ne binde qu'un string). **Si le binding dict ne fonctionne pas**, replier sur `RunComputation(string conceptId, string parameterValuesJson)` où l'implémentation parse le JSON en `Dictionary<string,object?>` (via le YAML-subset maison ou `System.Text.Json` déjà transitivement dispo côté Agents) — documenter le choix retenu.
  - `ReadConcept` : après le rendu existant, si `IsAttestedComputation`, ajouter un bloc résumé du contrat.
  - `GetTools()` : insérer `AIFunctionFactory.Create(GetComputation, "okf_get_computation")` (**toujours**) et, si `_orchestrator != null`, `AIFunctionFactory.Create(RunComputation, "okf_run_computation")`. Puis mettre à jour les tests de comptage/nom listés dans **Files** (`AIFunctionExposureTests` + `OkfMcpServerTests`) — **pas** `OkfBundleToolsTests`.

- [ ] **Step 5 : Vérifier le succès** — `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Agents"` **ET** `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Mcp"` → PASS (dont les comptages 10 / 7 mis à jour).

- [ ] **Step 6 : Commit**
```bash
git add src/OKF4net.Agents/ tests/OKF4net.Tests/Agents/OkfComputationToolsTests.cs tests/OKF4net.Tests/Agents/AIFunctionExposureTests.cs tests/OKF4net.Tests/Mcp/OkfMcpServerTests.cs
git commit -m "feat(agents): okf_get_computation + conditional okf_run_computation + read enrichment"
```

---

## Task 8 : Fixture §10 + golden `validate-computation`

**Files:**
- Create: `tests/fixtures/okf_v02_computation/**`, `tests/fixtures/golden/validate-computation.out`, `validate-computation.exitcode`
- Modify: `tests/OKF4net.Tests/GoldenParityTests.cs`, `tests/fixtures/README.md`

**Interfaces:**
- Consumes: `OkfCli.Run(["validate", <fixture>], out, err)` (in-process), le mécanisme golden existant.

- [ ] **Step 1 : Créer la fixture** `tests/fixtures/okf_v02_computation/` :
  - `computations/revenue.md` — inline (voir §12 du design : runtime bigquery, parameters year, executor{resource,receipt}, attester{resource}, generated/verified/stale_after futur/sources), avec `# Computation` fencé.
  - `computations/revenue-file.md` — `computation: references/computations/revenue.sql`, pas de fence.
  - `references/computations/revenue.sql`, `references/skills/run-on-bq.md`, `references/attesters/revenue.py` — cibles existantes.
  - `metrics/revenue.md` — `Metric` liant `../computations/revenue.md`.
  - `malformed/no-runtime.md` (sans runtime), `malformed/both.md` (fence + `computation:`), `malformed/broken-exec.md` (`executor.resource` cassé).
  - `index.md` racine : `okf_version: "0.2"`.
  - **LF endings** ; respecter `.gitattributes -text` pour les goldens.

- [ ] **Step 2 : Générer le golden (hand-vérifié)** — lancer `okf validate tests/fixtures/okf_v02_computation` en in-process, **relire ligne à ligne** chaque diagnostic contre le texte v0.2, l'enregistrer comme `tests/fixtures/golden/validate-computation.out` + `.exitcode` (attendu `0` — tout est Warning). Documenter la provenance dans `tests/fixtures/README.md` (nouvelle fixture v0.2, oracle maison hand-vérifié — pas de binaire de référence).

- [ ] **Step 3 : Câbler le test golden** — ajouter le cas `validate-computation` dans `GoldenParityTests.cs` (même patron que `validate-v02`).

- [ ] **Step 4 : Vérifier** — `dotnet test OKF4net.sln --filter "FullyQualifiedName~GoldenParityTests"` → PASS ; confirmer que les goldens **existants** sont inchangés (`git status` ne montre aucune modif de golden pré-existant).

- [ ] **Step 5 : Commit**
```bash
git add tests/fixtures/okf_v02_computation/ tests/fixtures/golden/validate-computation.* tests/OKF4net.Tests/GoldenParityTests.cs tests/fixtures/README.md
git commit -m "test(fixtures): §10 attested-computation fixture + hand-verified validate golden"
```

---

## Task 9 : Packaging, CHANGELOG, README, CLAUDE.md + vérif finale

**Files:**
- Modify: `.github/workflows/release.yml`, `CHANGELOG.md`, `README.md`, `CLAUDE.md`

- [ ] **Step 1 : `release.yml`** — ajouter un step `Pack (Attestation)` **après** le bloc `Pack (Catalog)` (~L41–45), sur le même patron (`dotnet pack src/OKF4net.Attestation/OKF4net.Attestation.csproj -c Release -o artifacts`). *(Note : `release.yml` publie déjà `.Mcp`, `.Catalog`, `.Catalog.Hosting` — Attestation s'ajoute simplement à la liste des 5 packages → 6.)*

- [ ] **Step 2 : `CHANGELOG.md`** — sous **`[Unreleased]` › Added**, ajouter :
  ```markdown
  - **Attested Computation (§10).** Full v0.2 §10 support: `Frontmatter.ComputationContract`
    projects the runtime/parameters/computation/executor/attester contract; `OkfDocument.Computation()`
    returns the sanctioned computation (fenced `# Computation` or `computation:` file); `okf validate`
    emits §10 + §6.2 soft-guidance warnings (never Error). New zero-dep **`OKF4net.Attestation`**
    package: host-plugged `IParameterBinder`/`IComputationExecutor`/`IAttester` and an
    `AttestationOrchestrator` (load → bind → execute → receipt-shape check → attest → gate on
    verdict + `stale_after`), errors-as-data. `OKF4net.Agents` gains `okf_get_computation` and, when
    an orchestrator is wired, `okf_run_computation`.
  - **§6.2 path-valued frontmatter resolution** — `OkfDocument.FrontmatterResources()` +
    `Bundle.TryResolveResource`/`ReadResourceText`, with broken/unsafe-path validator warnings.
  ```

- [ ] **Step 3 : `README.md`** — étendre la table *spec-section → type* (§10/§6.2/§4.2 + `OKF4net.Attestation`) ; mettre à jour le décompte de tools (« nine » → « ten », + `okf_run_computation` optionnel) ; court snippet `AttestationOrchestrator`.

- [ ] **Step 4 : `CLAUDE.md`** — dans la règle zéro-dép, ajouter `OKF4net.Attestation` (zéro-dép, référence `OKF4net` seul ; référencé par Agents) ; ajouter une puce Architecture pour le projet. *(Ne pas corriger ici les numéros de section v0.1 résiduels de la section Architecture — passe doc séparée.)*

- [ ] **Step 5 : Vérification finale complète**
```bash
dotnet build OKF4net.sln            # 0 warning (warnings=errors)
dotnet test OKF4net.sln             # suite complète verte (base + tous les nouveaux)
dotnet format OKF4net.sln --verify-no-changes
```
Confirmer : build 0 warning, **suite complète verte**, format clean. *(L'AOT publish du CLI est validé par la CI ; ce lot n'ajoute rien au CLI.)*

- [ ] **Step 6 : Commit**
```bash
git add .github/workflows/release.yml CHANGELOG.md README.md CLAUDE.md
git commit -m "chore(release): package OKF4net.Attestation; document §10 support"
```

---

## Self-Review (rempli par l'auteur du plan)

- **Couverture spec** : §10.1 (T1 type) · §10.2 contrat (T1) · §10.3 inline/fichier + « agent values only » (T2, T7 signature) · §10.4 lien markdown (existant, fixture T8) · §10.5 workflow (T6 orchestrateur) · §10.5(a)(b) contexte attester (T6, `AttestationContext`) · §10.6 verified≠attestation, non stocké (T6, aucune écriture) · §6.2 (T3+T4) · §4.2 heading (T2). Packaging/CHANGELOG/README/CLAUDE (T9). **Aucun trou.**
- **Placeholders** : aucun ; chaque tâche a du code de test réel + signatures exactes + logique clé. Les points « voir §5 » renvoient à des **patterns existants** (Lifecycle/Provenance/LinkScanner/ValidateTests), pas à des TODO.
- **Cohérence des types** : `AttestedComputationContract`/`SanctionedComputation`/`Executor`/`Attester` (T1) réutilisés à l'identique T2/T3/T4/T6/T7 ; `AttestationOutcome`/`AttestationContext`/`Receipt`/`BoundComputation` (T5) réutilisés T6/T7 ; `Bundle.TryResolveResource`/`ReadResourceText` (T3) réutilisés T4/T6/T7.

## Notes d'exécution

- **Base** : worktree off `dev` (inclut resolver strategies + travaux session parallèle). Créer via `superpowers:using-git-worktrees`.
- **Ordre strict** : T1 → T2 → T3 → T4 (cœur) ; T5 → T6 (attestation) ; T7 (agents) ; T8 (fixtures/golden) ; T9 (packaging/finalisation). T5 ne dépend que de T1 ; T3 est prérequis de T4/T6/T7 (résolution fichier).
- **Attention shared-checkout** : une session parallèle commite sur `dev` — travailler exclusivement dans le worktree §10, ne jamais `--amend`/rebaser sur `dev` partagé.
