# `okf passages` — plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** exporter un bundle OKF en passages JSONL (une section de heading par passage, avec ids stables, `embedHash` et projection v0.2) pour un pipeline d'embedding externe, via `okf passages`.

**Architecture:** trois corrections de briques partagées d'abord (`LinkScanner`, `Lifecycle`), puis deux unités pures du cœur (`PassageSplitter`, `PassageExport`) et un utilitaire interne (`PassageText`), enfin un verbe CLI qui sérialise ligne à ligne par `CliJsonContext`. Aucune seconde grammaire markdown : tout vient de la passe de blocs de `LinkScanner`.

**Tech Stack:** C# 14 / .NET 10, BCL seule dans `src/OKF4net` et `src/OKF4net.Cli`, xunit, System.Text.Json en génération de source.

**Spec:** `docs/superpowers/specs/2026-11-18-okf-passages-design.md` (désigné « spec » ci-dessous, avec ses numéros de section). Le second document (`2026-11-18-passage-search-design.md`) n'est **pas** implémenté ici.

## Global Constraints

- Zéro `PackageReference` ajouté dans `src/OKF4net/` et `src/OKF4net.Cli/` (`CLAUDE.md`, « Hard rules »).
- Chaque nouveau fichier source commence par `// SPDX-License-Identifier: LGPL-3.0-or-later`, namespace de fichier, doc XML sur toute API publique (warnings = erreurs).
- `CA2007` : `ConfigureAwait(false)` sur tout `await` dans `src/**` hors exécutables (aucun `await` n'est prévu ici).
- Lignes : toujours `Internal/LfLines` (`Split` / `SplitSpans`), jamais un `Split('\n')` privé.
- Goldens : jamais régénérés pour faire passer un test. Un golden existant qui change = arrêt et arbitrage (`tests/fixtures/README.md`, régimes). Le seul golden **nouveau** est `passages.jsonl`.
- Valeurs fixées par la spec : format `okf-passages/1` ; troncature 200 points de code ellipse comprise (`…`) ; slug coupé à 64 points de code ; séparateur d'occurrence `.` (k ≥ 2), de morceau `~` ; préambule `<conceptId>#` ; en-tête joint par ` > ` ; préfixe = en-tête + `"\n\n"` ; hash `sha256:` + hex minuscule ; `staleAfter` au format `yyyy-MM-ddTHH:mm:ss.fffffffZ` ; `staleAfterEpochMs` arrondi au supérieur.
- Après toute tâche qui change une API publique du cœur (tâches 3, 5, 6, 7) : `dotnet test producers/OkfProducer.sln` doit rester vert (`CLAUDE.md`).
- Commandes de vérification de fin de tâche : `dotnet build OKF4net.sln` puis `dotnet test OKF4net.sln`, et `dotnet format OKF4net.sln --verify-no-changes`.

## Review Focus

1. **Corps CRLF** (bundles écrits sous Windows) : mêmes passages, mêmes ids et mêmes `embedHash` que la version LF. → test dans la tâche 6.
2. **Concept sans aucun heading** (le cas le plus courant) : un seul passage, id `<conceptId>#`, en-tête = titre seul. → test dans la tâche 7.
3. **Nom de fichier contenant `#`** (`a#b.md`, id de concept `a#b`) : les ids restent uniques dans le bundle, car un slug ne contient jamais `#` (le **dernier** `#` sépare concept et chemin). → test dans la tâche 7.
4. **Concept à body vide ou fait seulement de blancs** : il disparaît de l'export (préambule vide omis), sans avertissement ni code d'erreur. → test dans la tâche 7.
5. **Fichiers réservés** `index.md` et `log.md` : jamais exportés (ce ne sont pas des concepts). → test dans la tâche 9.

---

## Carte des fichiers

| Fichier | Rôle | Tâche |
|---|---|---|
| `src/OKF4net/Links.cs` | prédicat d'espaces (3 sites) ; structure de blocs ; `AtxHeadingContent` en `internal` | 1, 2 |
| `src/OKF4net/Internal/BlockStructure.cs` | types de la structure de blocs (headings de ligne, blocs atomiques) | 2 |
| `src/OKF4net/Lifecycle.cs`, `Frontmatter.cs`, `Validate.cs` | drapeaux non scalaires, warning `StaleAfterNotScalar` | 3 |
| `src/OKF4net/Internal/DisplayText.cs` | coupe en points de code partagée (extraite du viewer) | 4 |
| `src/OKF4net/Internal/OkfTimestamp.cs` | `CeilingUnixMilliseconds` partagé (extrait du viewer), format fixe 7 décimales | 4 |
| `src/OKF4net.Viewer/SiteIndex.cs` | appelle les deux helpers partagés au lieu de ses copies | 4 |
| `src/OKF4net/Internal/PassageText.cs` | normalisation `N`, mesure par défaut, slug, en-tête, `embedText`, hash | 5 |
| `src/OKF4net/PassageSplitter.cs` | `PassageSplitter`, `PassageBudget`, `PassageSection` | 6 |
| `src/OKF4net/PassageExport.cs` | `PassageExport`, `Passage` (ids, omissions, projection) | 7 |
| `src/OKF4net.Cli/OkfCli.cs` | délégué `VerbSpec.Run` + `stderr` ; verbe `passages` | 8, 9 |
| `src/OKF4net.Cli/JsonOutput.cs` | `PassageJson`, enregistrement dans `CliJsonContext` | 9 |
| `tests/fixtures/golden/passages.jsonl`, `tests/fixtures/README.md` | nouveau golden et sa ligne de tableau | 9 |
| `.github/workflows/ci.yml` | appel `okf passages` dans le smoke test Native AOT | 9 |
| `README.md`, `CHANGELOG.md`, `CLAUDE.md` | documentation | 10 |

---

### Task 1 : Prérequis A — prédicat d'espaces de `LinkScanner`

Spec §2.3 A.

**Files:**
- Modify: `src/OKF4net/Links.cs` (sites `:763` fermeture de fence, `:1358` soulignement setext, `:1442` balise HTML seule sur sa ligne)
- Test: `tests/OKF4net.Tests/LinksTests.cs`

**Interfaces:**
- Produces: `private static bool IsSpacesOrTabs(ReadOnlySpan<char> s)` dans `LinkScanner` (vrai si vide ou seulement U+0020/U+0009). Aucun changement d'API publique.

- [ ] **Step 1: Écrire les trois tests qui échouent**

```csharp
[Fact]
public void Fence_close_followed_by_nbsp_does_not_close()
{
    // CommonMark §4.5: only spaces and tabs may follow a closing fence.
    var body = "~~~\n~~~ \n# H\nx\n~~~\n";
    Assert.Empty(LinkScanner.ExtractAtxHeadings(body));
}

[Fact]
public void Setext_underline_followed_by_nbsp_is_not_an_underline()
{
    // "=== " is paragraph text, so "# H" after the blank line stays the only heading
    // and the paragraph does not become a setext heading that ends early.
    var body = "Title\n=== \n[x](a.md)\n";
    Assert.Equal(["a.md"], LinkScanner.ExtractLinks(body).Select(l => l.Target));
}

[Fact]
public void Complete_tag_followed_by_nbsp_does_not_open_html_block()
{
    // Kind-7 HTML block needs only whitespace after the tag; NBSP is not whitespace there,
    // so the line is a paragraph and the link on the next line is visible.
    var body = "<span> \n[x](a.md)\n";
    Assert.Equal(["a.md"], LinkScanner.ExtractLinks(body).Select(l => l.Target));
}
```

Vérifier avant d'implémenter que chaque test **échoue** pour la raison annoncée. Si le second ou le troisième passe déjà (parce que le chemin réel diffère de cette lecture), remplacer son entrée par une entrée qui exerce effectivement le site, et noter l'entrée retenue dans le message de commit.

- [ ] **Step 2: Lancer** `dotnet test OKF4net.sln --filter "FullyQualifiedName~LinksTests"` → les trois nouveaux tests échouent.

- [ ] **Step 3: Implémenter** `IsSpacesOrTabs` et remplacer `.IsWhiteSpace()` par cet appel **aux trois sites seulement**. Les autres usages d'espaces de la passe (labels, `char.IsWhiteSpace` de la ligne `:1059`) ne changent pas.

- [ ] **Step 4: Lancer** `dotnet test OKF4net.sln` → tout passe, y compris `GoldenParityTests` sans régénération (aucune fixture ne contient ces espaces, spec §2.3 A).

- [ ] **Step 5: Test de l'effet voulu au chargement**

```csharp
[Fact]
public void Bundle_load_drops_link_hidden_by_nbsp_fence_close()
{
    using var dir = new TempDir();
    dir.Write("c.md", "---\ntype: Note\n---\n~~~\n~~~ \n[x](missing.md)\n~~~\n");
    var bundle = Bundle.Load(dir.Path);
    Assert.Empty(bundle.LinksFrom(ConceptId.Parse("c")));
}
```

- [ ] **Step 6: Commit** `fix(links): only spaces and tabs may follow a closing fence, setext underline or lone HTML tag (CommonMark)`

---

### Task 2 : Prérequis B — structure de blocs exposée par la passe

Spec §2.2, §2.3 B.

**Files:**
- Create: `src/OKF4net/Internal/BlockStructure.cs`
- Modify: `src/OKF4net/Links.cs` (`CodeFreeLinePairs` : paramètre enregistreur optionnel ; `AtxHeadingContent` → `internal`)
- Test: `tests/OKF4net.Tests/BlockStructureTests.cs`

**Interfaces:**
- Produces (`namespace OKF4net.Internal`) :
  - `internal enum AtomicBlockKind { FencedCode, IndentedCode, Html }`
  - `internal readonly record struct AtxHeadingLine(int LineIndex, int Level, int ContainerDepth)`
  - `internal readonly record struct AtomicBlock(int FirstLine, int LastLine, AtomicBlockKind Kind)` (bornes incluses, index de ligne au sens de `LfLines.Split`)
  - `internal sealed record BlockStructure(IReadOnlyList<AtxHeadingLine> Headings, IReadOnlyList<AtomicBlock> AtomicBlocks)`
  - `internal static BlockStructure LinkScanner.ScanBlockStructure(string body)`
  - `internal static (int Level, int Start, int Length)? LinkScanner.AtxHeadingContent(string line)` (visibilité seulement)

- [ ] **Step 1: Tests qui échouent**, un `[Fact]` par cas, chacun sur `LinkScanner.ScanBlockStructure(body)` :

| Test | Body | Attendu |
|---|---|---|
| `Top_level_heading_has_depth_zero` | `"# A\nx\n"` | `Headings == [(0, 1, 0)]` |
| `Heading_in_block_quote_has_depth_one` | `"> # A\n"` | `Headings == [(0, 1, 1)]` |
| `Heading_in_list_item_has_depth_one` | `"- # A\n"` | `Headings == [(0, 1, 1)]` |
| `Fenced_block_spans_its_fences` | `"~~~\n# H\n~~~\n"` | `Headings` vide ; `AtomicBlocks == [(0, 2, FencedCode)]` |
| `Unclosed_fence_runs_to_end` | `"```\na\n\nb\n"` | `[(0, 3, FencedCode)]` |
| `Indented_code_keeps_inner_blank_not_trailing` | `"    a\n\n    b\n\np\n"` | `[(0, 2, IndentedCode)]` |
| `Two_list_items_give_two_indented_blocks` | `"-     a\n\n-     b\n"` | deux blocs `IndentedCode`, `(0,0)` et `(2,2)` |
| `Html_block_is_atomic` | `"<pre>\n# H\n\nx\n</pre>\n"` | `Headings` vide ; `[(0, 4, Html)]` |
| `Atx_content_shared_helper` | lignes du tableau de spec §3.1 | `AtxHeadingContent(line)` → contenus `foo ### b`, `foo \###`, `foo#`, `foo`, `""`, `A  B` |

- [ ] **Step 2: Lancer** `--filter "FullyQualifiedName~BlockStructureTests"` → échec (type absent).

- [ ] **Step 3: Implémenter.** `CodeFreeLinePairs` reçoit un paramètre optionnel `BlockStructureRecorder? structure = null` (classe privée imbriquée qui accumule les deux listes). Points d'enregistrement : la profondeur d'un heading ATX est `containers.Count` à la branche `TryParseAtxHeading` (après fermeture et ouverture des conteneurs, spec §2.2) ; un bloc délimité commence à `OpensFence` et finit à sa fermeture ou à la fermeture d'un conteneur ; un bloc HTML de même. Pour le code indenté : garder le début du bloc et une plage de lignes blanches en attente, inclure la plage si une ligne indentée suit dans le même conteneur, l'exclure sinon ; tout changement de conteneur ferme le bloc. **Les paires `Raw`/`Blanked` produites ne changent pas.** `ScanBlockStructure` appelle `CodeFreeLinePairs(body, structure: recorder)`.

- [ ] **Step 4: Lancer** `dotnet test OKF4net.sln` → tout passe. Les tests existants de `LinksTests` (liens, notes, headings, entrées d'index) sont la preuve que la sortie existante est inchangée.

- [ ] **Step 5: Commit** `feat(links): expose ATX heading depth and atomic block ranges from the block pass`

---

### Task 3 : Prérequis C — valeurs de cycle de vie non scalaires

Spec §2.3 C (tableau des quatre cas).

**Files:**
- Modify: `src/OKF4net/Lifecycle.cs`, `src/OKF4net/Frontmatter.cs:151`, `src/OKF4net/Validate.cs` (enum `DiagnosticCode` après `KeyNotPortable` ; contrôle après `StaleAfterRaw`)
- Test: `tests/OKF4net.Tests/LifecycleTests.cs`, `tests/OKF4net.Tests/FrontmatterTests.cs`, `tests/OKF4net.Tests/ValidateTests.cs`

**Interfaces:**
- Produces :
  - `public bool Lifecycle.StatusNotScalar { get; init; }`, `public bool Lifecycle.StaleAfterNotScalar { get; init; }` (les quatre paramètres positionnels ne changent pas)
  - `DiagnosticCode.StaleAfterNotScalar` (dernier membre)
  - message : ``"stale_after is not a scalar timestamp"`` (sévérité `Warning`, champ `"stale_after"`)

- [ ] **Step 1: Tests qui échouent**

```csharp
[Theory]
[InlineData("status: [deprecated]", true, false)]
[InlineData("status: {a: b}", true, false)]
[InlineData("status: []", true, false)]
[InlineData("stale_after: [x]", false, true)]
[InlineData("stale_after: {bad: value}", false, true)]
[InlineData("status: null", false, false)]
[InlineData("status: ~", false, false)]
[InlineData("status:", false, false)]
[InlineData("status: \"\"", false, false)]
public void NotScalar_flags_cover_sequences_and_mappings_only(string line, bool statusNotScalar, bool staleNotScalar)
{
    var fm = OkfDocument.Parse($"---\ntype: Note\n{line}\n---\nx\n").Frontmatter;
    Assert.Equal(statusNotScalar, fm.Lifecycle.StatusNotScalar);
    Assert.Equal(staleNotScalar, fm.Lifecycle.StaleAfterNotScalar);
}
```

Dans `FrontmatterTests`, vérifier pour les mêmes entrées que `StatusIsKnown`, `StaleAfterRaw` et `StaleAfterMalformed` gardent **exactement** leur valeur actuelle (lue avant la modification, pas supposée).

Dans `ValidateTests`, sur un concept `status: [deprecated]` + `stale_after: { bad: value }` (avec `title`/`description` pour éviter les avertissements recommandés), la liste filtrée sur ces deux champs vaut exactement `[StatusNotScalar, StaleAfterNotScalar]`, une fois chacun, sans `StatusUnknown` ni `StaleAfterInvalid`. Et pour `stale_after: null` : aucun diagnostic sur `stale_after`.

- [ ] **Step 2: Lancer** les trois classes de tests → échec.

- [ ] **Step 3: Implémenter.** `Frontmatter.Lifecycle` lit les deux `YamlValue` et pose les drapeaux par `value is YamlSequence or YamlMapping` (avec `with { … }` sur le résultat de `Lifecycle.From`). Le validateur ajoute un warning `StaleAfterNotScalar` quand `fm.Lifecycle.StaleAfterNotScalar`. Rien d'autre ne change : `ConceptAudit` transporte le `Lifecycle` entier, et `JsonOutput` projette explicitement ses champs, donc aucun rendu existant ne bouge.

- [ ] **Step 4: Lancer** `dotnet test OKF4net.sln` → tout passe, goldens inchangés. Puis `dotnet test producers/OkfProducer.sln`.

- [ ] **Step 5: Commit** `fix(lifecycle): flag non-scalar status and stale_after; validator warns on non-scalar stale_after`

---

### Task 4 : Helpers d'affichage partagés (coupe en points de code, epoch arrondi)

Spec §4.1 (troncature), §4.4 (`staleAfterEpochMs`). Évite deux copies : `SiteIndex` porte aujourd'hui les deux en privé.

**Files:**
- Create: `src/OKF4net/Internal/DisplayText.cs`
- Modify: `src/OKF4net/Internal/OkfTimestamp.cs`, `src/OKF4net.Viewer/SiteIndex.cs`
- Test: `tests/OKF4net.Tests/DisplayTextTests.cs`, `tests/OKF4net.Tests/OkfTimestampTests.cs`

**Interfaces:**
- Produces :
  - `internal static string DisplayText.TruncateCodePoints(string text, int limit)` : texte inchangé si ≤ `limit` points de code, sinon les `limit − 1` premiers points de code, `TrimEnd()`, puis `…` (U+2026). Une paire de surrogates compte pour un point de code et n'est jamais coupée.
  - `internal static long? OkfTimestamp.CeilingUnixMilliseconds(DateTimeOffset? instant)`
  - `internal static string OkfTimestamp.FormatFixedUtc(DateTimeOffset instant)` → `yyyy-MM-ddTHH:mm:ss.fffffffZ` (7 décimales toujours)

- [ ] **Step 1: Tests qui échouent** : `TruncateCodePoints(new string('a', 200), 200)` inchangé ; `TruncateCodePoints(new string('a', 201), 200)` == 199 `a` + `…` (200 points de code) ; une chaîne de 201 emoji `😀` donne 199 emoji + `…` sans surrogate isolé ; `TruncateCodePoints("a" + new string(' ', 198) + "bc", 200)` == `"a…"`. `CeilingUnixMilliseconds` : `1970-01-01T00:00:00.0000001Z` → 1, `…:00.001Z` → 1, `null` → `null`. `FormatFixedUtc` d'un instant à la seconde pleine → `…:00.0000000Z`.

- [ ] **Step 2: Lancer** → échec.

- [ ] **Step 3: Implémenter**, puis faire appeler ces deux helpers par `SiteIndex` (le repli des espaces propre au viewer reste dans `SiteIndex`, avant l'appel à `TruncateCodePoints`). Supprimer les deux copies privées de `SiteIndex`.

- [ ] **Step 4: Lancer** `dotnet test OKF4net.sln` → tout passe, y compris les tests du viewer, sans changement. Lancer aussi le harnais `tools/viewer-security-check` (job CI `viewer sanitizer (JS)`) si `okf-index.js` est régénéré par ses tests : la sortie doit être identique.

- [ ] **Step 5: Commit** `refactor: share code-point truncation and ceiling-ms helpers between viewer and core`

---

### Task 5 : `PassageText` — normalisation, mesure, slug, en-tête, hash

Spec §3 (mesure par défaut), §3.2 (`N`), §4.1 (en-tête, troncature, hash), §4.3 (slug).

**Files:**
- Create: `src/OKF4net/Internal/PassageText.cs`
- Test: `tests/OKF4net.Tests/PassageTextTests.cs`

**Interfaces:**
- Consumes : `DisplayText.TruncateCodePoints` (tâche 4).
- Produces (`internal static class PassageText`) :
  - `const int ElementLimit = 200`, `const int SlugLimit = 64`, `const string HeaderSeparator = " > "`, `const string PrefixTerminator = "\n\n"`
  - `string Normalize(string body)` : `N` = `\r\n` → `\n`, surrogate isolé → U+FFFD ; `\r` isolé conservé
  - `int CountCodePoints(string text)` : la mesure par défaut
  - `string Slug(string headingContent)` : règle de spec §4.3, coupe à 64, vide → `section`
  - `string Header(string titleOrId, IReadOnlyList<string> headings)` : éléments tronqués à 200, joints par ` > `
  - `string Prefix(string header)` → `header + "\n\n"`
  - `string EmbedText(string header, string text)` → `Prefix(header) + text`
  - `string EmbedHash(string embedText)` → `"sha256:" + hex minuscule`

- [ ] **Step 1: Tests qui échouent**, un par règle : `Normalize("a\r\nb\rc")` == `"a\nb\rc"` ; `Normalize("a\uD800b")` == `"a�b"` ; `CountCodePoints("😀a")` == 2 ; `Slug("**A** &amp; B")` == `"a-amp-b"` ; `Slug("[A](x)")` == `"ax"` ; `Slug("!!!")` == `"section"` ; `Slug("Été 2026")` == `"été-2026"` ; `Slug(new string('a', 100)).Length` == 64 ; `Slug` ne contient jamais `.`, `~`, `/`, `#` (propriété sur un échantillon de 1 000 chaînes aléatoires, graine fixe) ; `Header("Orders", ["Schema", "Partitioning"])` == `"Orders > Schema > Partitioning"` ; `Header("T", [])` == `"T"` ; `Header("T", [new string('h', 300)])` == `"T > " + new string('h', 199) + "…"` ; `EmbedText("T", "x")` == `"T\n\nx"` ; `EmbedHash("T\n\nx")` égal au SHA-256 calculé dans le test par `SHA256.HashData`.

- [ ] **Step 2: Lancer** → échec. **Step 3: Implémenter** (BCL : `Rune`, `SHA256.HashData`, `Convert.ToHexStringLower`). Le slug minuscule utilise la culture invariante (`ToLowerInvariant` sur chaque `Rune`). **Step 4: Lancer** → vert.

- [ ] **Step 5: Commit** `feat(passages): PassageText normalisation, slug, header and embed hash`

---

### Task 6 : `PassageSplitter` — sections et budget

Spec §3 (tout).

**Files:**
- Create: `src/OKF4net/PassageSplitter.cs`
- Test: `tests/OKF4net.Tests/PassageSplitterTests.cs`, `tests/OKF4net.Tests/PassageSplitterHostileTests.cs`

**Interfaces:**
- Consumes : `LinkScanner.ScanBlockStructure`, `LinkScanner.AtxHeadingContent` (tâche 2) ; `PassageText` (tâche 5) ; `LfLines.SplitSpans`.
- Produces (public) :
  - `public sealed record PassageBudget(int MaxSize, Func<string, int>? Measure = null)` (constructeur : `MaxSize <= 0` → `ArgumentOutOfRangeException`)
  - `public sealed record PassageSection(IReadOnlyList<string> Headings, IReadOnlyList<int> HeadingLevels, string Text, int Part, bool Oversize)` — `HeadingLevels` est ajouté pour que l'export calcule les parents sans reparser ; **même longueur que `Headings`**
  - `public static IReadOnlyList<PassageSection> PassageSplitter.Split(string body, string contextTitle, PassageBudget? budget = null)`
  - `internal static IReadOnlyList<PassageSection> PassageSplitter.Split(string body, string contextTitle, PassageBudget? budget, ref long measuredCodePoints)` : surcharge de comptage pour les tests de coût (le compteur additionne les points de code de chaque chaîne passée à la mesure)

Note pour l'implémenteur : la signature publique de spec §3 n'avait pas `HeadingLevels`. Il est ajouté parce que la définition récursive des ids (spec §4.3) a besoin des niveaux, et que les retrouver depuis le texte serait une seconde grammaire.

- [ ] **Step 1: Tests de partition et de coupure** (`PassageSplitterTests`), chacun avec l'entrée exacte et le découpage attendu (`Headings`, `Text` par section) :
  - propriété : pour chaque `Concept.Document.Body` de `bundles/` et `tests/fixtures/` (énumérés par `Bundle.Load`), `string.Concat(sections.Select(s => s.Text)) == PassageText.Normalize(body)` ;
  - avec et sans `\n` final ; `\r` isolé ; surrogate isolé ;
  - **Review Focus 1** : un body CRLF et le même body LF donnent des `Headings` et des `Text` identiques ;
  - les cas de spec §6.1 « Points de coupure » (indentation 0–3 contre 4, `#x`, `#` seul, fermeture après tabulation, citation, élément de liste, `<pre>`, fence dans un élément de liste, fence et fermeture indentées de 4, info string avec backtick, fence + U+00A0, bloc non fermé, setext, saut de niveau H1 → H3, dépilement) ;
  - les trois contre-exemples Codex sur `ComputationExtractor` : `"    ```\n# H\nx\n"` (une coupure `H`), `"```\n    ```\n# H\nx\n"` (aucune coupure), ``"```a`b\n# H\nx\n"`` (une coupure `H`) ;
  - `Headings` en contenu brut : `# **A** &amp; B` → `**A** &amp; B`, `# [A](x)` → `[A](x)`.

- [ ] **Step 2: Tests du budget** :
  - section qui tient avec budget : `Part == 0` ;
  - séparateur : `Split("a\n\nb", "T", new PassageBudget(5))` → deux sections, `Part` 1 et 2, `Text` `"a\n\n"` et `"b"`, `Oversize` vrai pour la seconde si son `embedText` dépasse (vérifier la valeur exacte : `"T\n\nb"` = 4 ≤ 5, donc `false` ; `"T\n\na\n\n"` = 6 > 5, donc `true`) ;
  - code indenté `"    a\n\n    b\n"` jamais coupé ; fence, HTML atomiques ;
  - rattachements : blancs en tête de section, blancs après la ligne de heading, ligne de heading attachée au premier bloc à contenu ;
  - section réduite à sa ligne de heading, section faite de blancs, `Split("", "T", new PassageBudget(1))` → une section, `Part == 0`, `Oversize == true` ;
  - préambule redécoupé : `Part` 1..n sur la section sans heading ;
  - mesure enregistreuse : le préfixe (`"T\n\n"` + en-tête) est mesuré une fois, chaque bloc une fois, chaque regroupement vérifié une fois ;
  - mesure non additive (double de test) : la dichotomie coupe à ⌊k/2⌋ et termine sur des singletons.

- [ ] **Step 3: Tests hostiles** (`PassageSplitterHostileTests`) : les entrées de spec §6.2 (100 000 headings, 10 000 frères homonymes, ligne de 10 Mo, heading d'1 Mo + 10 000 sous-sections, cas glouton `N` blocs `x` / budget `3N/2 + 3`, cas dichotomique, fences non fermées ou alternées, titres C0/C1/bidi/surrogates). Pour chacun : la partition tient. Avec la mesure par défaut, `measuredCodePoints <= 2 * |N(body)| + 3 * |sortie|`, où |sortie| = somme des `CountCodePoints(EmbedText(...))` de **toutes** les sections retournées. Avec la mesure non additive : `measuredCodePoints <= 4 * (|N(body)| + |sortie|) * (1 + log2(k))`. Aucune mesure de temps.

- [ ] **Step 4: Lancer** → échec. **Step 5: Implémenter** selon spec §3.1–§3.3 : `N(body)`, `SplitSpans`, `ScanBlockStructure`, coupures sur les headings `ContainerDepth == 0`, pile par niveau, contenu par `AtxHeadingContent` de la ligne ; budget en quatre étapes de spec §3.3, sommes en `long`, coupure à ⌊k/2⌋. **Step 6: Lancer** → vert ; `dotnet test producers/OkfProducer.sln`.

- [ ] **Step 7: Commit** `feat(passages): PassageSplitter with heading cuts and linear-work budget`

---

### Task 7 : `PassageExport` — ids, omissions, projection v0.2

Spec §4 (tout).

**Files:**
- Create: `src/OKF4net/PassageExport.cs`
- Test: `tests/OKF4net.Tests/PassageExportTests.cs`

**Interfaces:**
- Consumes : `PassageSplitter.Split`, `PassageText`, `ConceptAudit.Run(bundle, AuditQuery.All)`, `AuditFinding`, `OkfTimestamp.CeilingUnixMilliseconds` (tâche 4).
- Produces (public) :
  - `public sealed record Passage(string Id, string SectionId, ConceptId ConceptId, int Ordinal, IReadOnlyList<string> Headings, string Text, string EmbedText, string EmbedHash, bool Oversize, string? Type, string? Title, IReadOnlyList<string> Tags, TrustTier Trust, bool ReviewOutdated, ConceptStatus Status, bool StatusKnown, DateTimeOffset? StaleAfter, long? StaleAfterEpochMs, bool StaleAfterMalformed)`
  - `public static class PassageExport` avec `public const string FormatVersion = "okf-passages/1"` et `public static IEnumerable<Passage> Enumerate(Bundle bundle, PassageBudget? budget = null)` (itérateur paresseux : l'audit est fait au premier `MoveNext`)

- [ ] **Step 1: Tests qui échouent**, chacun avec bundle construit par `TempDir` :
  - projection : `Trust`, `ReviewOutdated` (= `finding.IsReviewOutdated`), `Status`, `StaleAfter` égaux à l'`AuditFinding` du concept ; `StatusKnown == lc.StatusIsKnown && !lc.StatusNotScalar` ; `StaleAfterMalformed == lc.StaleAfterMalformed || lc.StaleAfterNotScalar` ; `StaleAfterEpochMs == OkfTimestamp.CeilingUnixMilliseconds(lc.StaleAfter)` ;
  - concepts malformés de spec §6.3 (`retired`/`not-a-date`, `[deprecated]`/`{bad: value}`) et les cas `null`, `~`, vide non cité, `""` contre le tableau de spec §2.3 C ;
  - `ReviewOutdated` : `generated.at` postérieur à la relecture → `true` ; seulement `timestamp` postérieur → `true` ; jamais relu → `false` ; `generated.at` illisible avec `timestamp` postérieur → `false` ;
  - ordre : concepts par `ConceptId` ordinal, passages dans l'ordre du document ;
  - omissions avant budget ; une section omise compte dans les rangs : `"# A\n# A\nx\n"` → seul passage émis `c#a.2` ;
  - ids : `"# A\nx\n# A\ny\n# A-1\nz\n"` → `c#a`, `c#a.2`, `c#a-1` ; `"# A\n## X\nun\n# A\n## X\ndeux\n"` → `c#a/x`, `c#a.2/x`, et après insertion d'un `# A` entre les deux, le second devient `c#a.3/x` ; morceaux `~1`, `~2`, `SectionId` sans suffixe ; préambule redécoupé `c#~1` ; `Ordinal` 0..n parmi les émis ;
  - deux headings de 100 caractères identiques sur leurs 64 premiers → `x…`, `x….2` (slug coupé puis rang) ;
  - stabilité : `"# [A][r]\nx\n# Z\ny\n"` et la même chaîne + `"\n[r]: /target\n"` → même `SectionId` pour la première section ; exemple de la fence de spec §4.3 (`c#b/c` → `c#a/c`) ;
  - troncature : `Headings` tronqués à 200 ; `Title` **intégral** ; en-tête de `EmbedText` avec le titre tronqué ;
  - `EmbedHash` recalculé dans le test ;
  - **Review Focus 2** : concept sans heading → un passage `c#`, `Headings` vide, `EmbedText == Title + "\n\n" + body` ;
  - **Review Focus 3** : fichiers `a.md` (heading `b`) et `a#b.md` → ids `a#b` et `a#b#` distincts ; toutes les ids du bundle sont uniques. Si le chargement refuse un `#` dans un nom (`BundleKeyValidator`), le test affirme ce refus à la place, et la phrase « le dernier `#` sépare » de la documentation est retirée ;
  - **Review Focus 4** : concept à body vide ou de blancs → aucun passage ;
  - paresse : avec un bundle de 3 concepts, le premier `MoveNext` rend un passage du premier concept sans que `PassageSplitter` ait été appelé pour le troisième (compteur via la surcharge `internal` de la tâche 6, ou un `Func` de mesure qui compte les appels par concept).

- [ ] **Step 2: Lancer** → échec. **Step 3: Implémenter** selon spec §4–§4.3 : un dictionnaire `ConceptId → AuditFinding` construit au premier `MoveNext` ; pour chaque concept, `Split(body, title ?? id, budget)`, attribution des ids sur la sortie **complète** (compteur par (chemin du parent, slug) ; parent = heading précédent de niveau strictement inférieur, via `HeadingLevels`), puis omission, puis `Ordinal`. **Step 4: Lancer** → vert ; `dotnet test producers/OkfProducer.sln`.

- [ ] **Step 5: Commit** `feat(passages): PassageExport with recursive ids, omissions and v0.2 projection`

---

### Task 8 : CLI — `stderr` pour les handlers de verbe

Spec §5.2 « Changement transverse ».

**Files:**
- Modify: `src/OKF4net.Cli/OkfCli.cs` (`VerbSpec.Run`, appel `spec.Run(...)` vers `:289`, les huit `Cmd*`)
- Test: aucun nouveau test ; la preuve est l'invariance.

**Interfaces:**
- Produces : `Func<CliArgs, TextReader, TextWriter, TextWriter, int> Run` ; signature de handler `private static int CmdX(CliArgs parsed, TextReader stdin, TextWriter stdout, TextWriter stderr)`.

- [ ] **Step 1:** changer le type du délégué, l'appel (`spec.Run(parsed, stdin, stdout, stderr)`) et les huit signatures. Aucun handler n'écrit sur `stderr`.
- [ ] **Step 2: Lancer** `dotnet test OKF4net.sln` → tout passe, **aucun golden modifié** (`git status tests/fixtures` vide).
- [ ] **Step 3: Commit** `refactor(cli): pass stderr to verb handlers`

---

### Task 9 : Le verbe `okf passages`

Spec §5, §6.4.

**Files:**
- Modify: `src/OKF4net.Cli/OkfCli.cs` (texte `Usage` : ligne `COMMANDS` et ligne `OPTIONS` ; entrée `Verbs["passages"]` ; `CmdPassages`), `src/OKF4net.Cli/JsonOutput.cs`, `.github/workflows/ci.yml` (étape « Run published okf »), `tests/fixtures/README.md`
- Create: `tests/fixtures/golden/passages.jsonl`
- Test: `tests/OKF4net.Tests/CliTests.cs`, `tests/OKF4net.Tests/MachineOutputTests.cs`, `tests/OKF4net.Tests/GoldenParityTests.cs`

**Interfaces:**
- Consumes : `PassageExport.Enumerate`, `PassageExport.FormatVersion`, `AuditVocabulary.Name`, `OkfTimestamp.FormatFixedUtc`.
- Produces :
  - `internal sealed record PassageJson(string Format, string Id, string SectionId, string ConceptId, int Ordinal, IReadOnlyList<string> Headings, string Text, string EmbedText, string EmbedHash, bool Oversize, string? Type, string? Title, IReadOnlyList<string> Tags, string Trust, bool ReviewOutdated, string Status, bool StatusKnown, string? StaleAfter, long? StaleAfterEpochMs, bool StaleAfterMalformed)` — l'ordre des paramètres **est** l'ordre JSON de spec §4.1 ; `[JsonSerializable(typeof(PassageJson))]` sur `CliJsonContext`
  - verbe : usage `okf passages <bundle> [--max-chars <n>]`, résumé `Export a bundle as JSONL passages for an embedding pipeline.`, valued flag `--max-chars`, option line `    --max-chars <n>  Re-split sections whose embedText exceeds n characters`
  - erreur d'usage : ``--max-chars expects a positive integer, got '<valeur>'`` (via `CliArgumentException`)
  - avertissement : ``warning: skipped <display path>: <error>`` (`bundle.DisplayPath(problem)`, `problem.Error`, comme `CmdInfo`)

- [ ] **Step 1: Tests CLI qui échouent** (`CliTests`) :
  - `--max-chars 0`, `-1`, `abc`, absent après le flag → code 1, stdout vide, message attendu ; le bundle n'est **pas** lu (passer un chemin inexistant : le message est celui de `--max-chars`, pas celui du bundle) ;
  - bundle inexistant → code 1, stdout vide ;
  - bundle avec un concept valide et un fichier au frontmatter illisible → stdout = les lignes du concept valide, stderr = une ligne `warning: skipped …`, code 1 ;
  - même cas sans aucun concept valide → stdout vide, code 1 ;
  - bundle valide sans problème → code 0, stderr vide ;
  - **Review Focus 5** : un bundle contenant `index.md` et `log.md` → aucune ligne avec un `conceptId` `index` ou `log` ;
  - `okf passages --help` → code 0, l'usage du verbe ; `okf --help` contient la ligne `passages`.

- [ ] **Step 2: Tests machine** (`MachineOutputTests`) sur un bundle construit dans le test (deux concepts, un heading dupliqué, un `stale_after`, un concept relu puis modifié) : chaque ligne se parse avec `JsonDocument` ; les noms de propriétés, dans l'ordre, valent exactement la liste de spec §4.1 ; les types (chaîne, nombre, booléen, tableau, `null`) sont ceux attendus ; `format == "okf-passages/1"` ; `text` égal à la tranche de `N(body)` calculée dans le test ; `embedHash` recalculé ; `staleAfter` au format fixe 7 décimales et `staleAfterEpochMs` cohérent.

- [ ] **Step 3: Lancer** → échec. **Step 4: Implémenter** `CmdPassages` : valider `--max-chars` d'abord, puis `Load(path)` (le chargement est terminé avant toute écriture), puis `foreach` sur `PassageExport.Enumerate` en écrivant chaque ligne (`JsonSerializer.Serialize(json, CliJsonContext.Default.PassageJson)` + `"\n"`), puis les avertissements sur `stderr`, puis le code (1 si `bundle.ParseProblems.Count > 0`). **Step 5: Lancer** → vert.

- [ ] **Step 6: Golden.** Ajouter `Passages_jsonl_matches_golden` dans `GoldenParityTests` (`Run("passages", "tests/fixtures/okf_v02")` via `WithRepoRootAsCwd`, code 0, comparaison au fichier). Créer `tests/fixtures/golden/passages.jsonl` par le mode `OKF_UPDATE_GOLDEN=Passages_jsonl_matches_golden` (deux exécutions, la première échoue par conception), **lire le fichier produit** et vérifier à la main 3 lignes contre `tests/fixtures/okf_v02`. Ajouter sa ligne au tableau de `tests/fixtures/README.md` (invocation, ce que le golden seul garde : l'ordre des lignes et l'échappement ; ce qui est figé ailleurs : `MachineOutputTests`, `PassageExportTests`). `git status` ne doit montrer **aucun autre** golden modifié.

- [ ] **Step 7: CI.** Dans `ci.yml`, étape « Run published okf », après `validate` :

```powershell
$lines = & $env:OKF_BIN passages tests/fixtures/okf_v02
if ($LASTEXITCODE -ne 0) { throw "okf passages failed" }
if (-not $lines) { throw "okf passages wrote nothing" }
foreach ($l in $lines) {
  $o = $l | ConvertFrom-Json
  if ($o.format -ne 'okf-passages/1' -or -not $o.embedHash.StartsWith('sha256:')) { throw "unexpected passage line: $l" }
}
```

- [ ] **Step 8: Lancer** `dotnet test OKF4net.sln`, puis `dotnet publish src/OKF4net.Cli -c Release` et exécuter le binaire publié sur `tests/fixtures/okf_v02` : la sortie est identique au golden.

- [ ] **Step 9: Commit** `feat(cli): okf passages exports a bundle as JSONL passages`

---

### Task 10 : Documentation

Spec §7.

**Files:**
- Modify: `README.md` (section `okf passages`, ligne dans la liste des commandes), `CHANGELOG.md` (`[Unreleased]`), `CLAUDE.md` (architecture)

- [ ] **Step 1: README.** Une section `okf passages` qui contient : un exemple de ligne (copiée du golden), le filtre Python de spec §7 avec `now_ms = time.time_ns() // 1_000_000`, les trois règles de spec §4.5, la règle de lecture de spec §4.7 (ignorer les propriétés inconnues, refuser une version inconnue), le préfixage multi-bundle (§4.3), la jointure par `conceptId` pour les champs exclus (§4.6), la publication conditionnelle (`> out.jsonl.tmp && mv …`, §5.2), et l'API (`PassageExport.Enumerate`, `PassageBudget` avec un exemple `Measure: t => tokenizer.CountTokens(t)`).
- [ ] **Step 2: CHANGELOG** `[Unreleased]` : *Added* `okf passages` et l'API ; *Fixed* tâche 1 (avec l'effet sur les liens), tâche 3 (avec le warning `StaleAfterNotScalar`, changement de sortie de `okf validate` pour les bundles malformés) ; *Changed* : `VerbSpec.Run` reçoit `stderr` (interne, sans effet visible).
- [ ] **Step 3: CLAUDE.md.** Dans l'architecture de `src/OKF4net/` : une puce `PassageSplitter`/`PassageExport` (le découpeur unique ; ids et omissions dans une seule unité) ; une puce sur `LinkScanner.ScanBlockStructure` comme seule grammaire de blocs pour tout nouveau consommateur ; `Internal/DisplayText` et `OkfTimestamp.CeilingUnixMilliseconds` comme helpers partagés avec le viewer. Ajouter `passages` à la liste des verbes de `src/OKF4net.Cli/`.
- [ ] **Step 4: Vérifier** `dotnet build OKF4net.sln`, `dotnet test OKF4net.sln`, `dotnet format OKF4net.sln --verify-no-changes`, `dotnet test producers/OkfProducer.sln`, puis `graphify update .`.
- [ ] **Step 5: Commit** `docs: okf passages in README, CHANGELOG and CLAUDE.md`
