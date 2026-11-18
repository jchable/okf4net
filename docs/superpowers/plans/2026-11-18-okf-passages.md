# `okf passages` — plan d'implémentation

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** exporter un bundle OKF en passages JSONL (une section de heading par passage, avec ids stables, `embedHash` et projection v0.2) pour un pipeline d'embedding externe, via `okf passages`.

**Architecture:** trois corrections de briques partagées d'abord (`LinkScanner`, `Lifecycle`), puis deux helpers extraits du viewer, puis deux unités pures du cœur (`PassageSplitter`, `PassageExport`) et un utilitaire interne (`PassageText`), enfin un verbe CLI qui sérialise ligne à ligne par `CliJsonContext`. Aucune seconde grammaire markdown : tout vient de la passe de blocs de `LinkScanner`.

**Tech Stack:** C# 14 / .NET 10, BCL seule dans `src/OKF4net` et `src/OKF4net.Cli`, xunit, System.Text.Json en génération de source.

**Spec:** `docs/superpowers/specs/2026-11-18-okf-passages-design.md` (désigné « spec » ci-dessous, avec ses numéros de section). Le second document (`2026-11-18-passage-search-design.md`) n'est **pas** implémenté ici.

## Global Constraints

- Zéro `PackageReference` ajouté dans `src/OKF4net/` et `src/OKF4net.Cli/` (`CLAUDE.md`, « Hard rules »).
- Chaque nouveau fichier source commence par `// SPDX-License-Identifier: LGPL-3.0-or-later`, namespace de fichier, doc XML sur toute API publique (warnings = erreurs).
- Lignes : toujours `Internal/LfLines` (`Split` / `SplitSpans`), jamais un `Split('\n')` privé.
- **Caractères invisibles dans les tests** : toujours écrits en échappement C# (`"\u00A0"`, `"\uD800"`, `"\uFFFD"`), jamais en caractère réel. Un éditeur ou un outil les perd ou les décode silencieusement.
- Goldens : jamais régénérés pour faire passer un test. Un golden existant qui change = arrêt et arbitrage (`tests/fixtures/README.md`, régimes). Le seul golden **nouveau** est `passages.jsonl`.
- Valeurs fixées par la spec : format `okf-passages/1` ; troncature 200 points de code ellipse comprise (`…`, U+2026) ; slug coupé à 64 points de code ; séparateur d'occurrence `.` (k ≥ 2), de morceau `~` ; préambule `<conceptId>#` ; en-tête joint par ` > ` ; préfixe = en-tête + `"\n\n"` ; hash `sha256:` + hex minuscule ; `staleAfter` au format `yyyy-MM-ddTHH:mm:ss.fffffffZ` ; `staleAfterEpochMs` arrondi au supérieur.
- **Ordre des concepts** : `ConceptId.CompareTo` (segment par segment, `ConceptId.cs:330`), c'est-à-dire l'ordre de `ConceptAudit.Run(...).Findings`. Ce n'est **pas** l'ordre de `Bundle.Concepts` (chemins de clés avec `.md`, `a/b.md` avant `a.md`), ni l'ordre ordinal de la chaîne entière.
- **Headings bruts ou tronqués** : `PassageSection.Headings` est **brut** (contenu ATX). La troncature à 200 s'applique seulement dans `PassageText.Header` (en-tête de `embedText`) et dans `Passage.Headings` (export). Le slug part toujours du brut.
- Après toute tâche qui change une API publique du cœur (tâches 3, 6, 7) : `dotnet test producers/OkfProducer.sln` doit rester vert (`CLAUDE.md`).
- Vérification de fin de tâche : `dotnet build OKF4net.sln`, `dotnet test OKF4net.sln`, `dotnet format OKF4net.sln --verify-no-changes`, et `git status tests/fixtures/golden` sans fichier modifié (sauf `passages.jsonl` créé en tâche 9).

## Review Focus

1. **Corps CRLF** (bundles écrits sous Windows) : mêmes passages, mêmes ids et mêmes `embedHash` que la version LF. → tâche 6.
2. **Concept sans aucun heading** (le cas le plus courant) : un seul passage, id `<conceptId>#`, en-tête = titre seul. → tâche 7.
3. **Fichier dont le nom n'est pas un id de concept valide** (`a#b.md`, ou un nom avec espace) : un `ParseProblem`, aucun passage, et en CLI un avertissement et le code 1. Un segment de `ConceptId` n'admet que `[A-Za-z0-9_][A-Za-z0-9_.\-]*` (`ConceptId.cs:190`), donc un id de passage n'a jamais d'autre `#` que son séparateur. → tâches 7 et 9.
4. **Concept à body vide ou fait seulement de blancs** : il disparaît de l'export (préambule vide omis), sans avertissement ni code d'erreur. → tâche 7.
5. **Fichiers réservés** `index.md` et `log.md` : jamais exportés (ce ne sont pas des concepts). → tâche 9.

---

## Carte des fichiers

| Fichier | Rôle | Tâche |
|---|---|---|
| `src/OKF4net/Links.cs` | prédicat d'espaces (3 sites) ; structure de blocs ; `AtxHeadingContent(string)` en `internal` | 1, 2 |
| `src/OKF4net/Internal/BlockStructure.cs` | types de la structure de blocs | 2 |
| `src/OKF4net/Lifecycle.cs`, `Frontmatter.cs`, `Validate.cs` | drapeaux non scalaires, warning `StaleAfterNotScalar` | 3 |
| `src/OKF4net/Internal/DisplayText.cs` | coupe en points de code, partagée et bornée | 4 |
| `src/OKF4net/Internal/OkfTimestamp.cs` | `CeilingUnixMilliseconds`, `FormatFixedUtc` | 4 |
| `src/OKF4net.Viewer/SiteIndex.cs` | appelle les deux helpers partagés | 4 |
| `src/OKF4net/Internal/PassageText.cs` | `N`, mesure par défaut, slug, en-tête, `embedText`, hash | 5 |
| `src/OKF4net/PassageSplitter.cs` | `PassageSplitter`, `PassageBudget`, `PassageSection`, compteur de travail interne | 6 |
| `src/OKF4net/PassageExport.cs` | `PassageExport`, `Passage` (ids, omissions, projection) | 7 |
| `src/OKF4net.Cli/OkfCli.cs` | délégué `VerbSpec.Run` + `stderr` ; verbe `passages` ; doc XML et `Usage` | 8, 9 |
| `src/OKF4net.Cli/JsonOutput.cs` | `PassageJson`, enregistrement dans `CliJsonContext` | 9 |
| `tests/OKF4net.Tests/GoldenUpdate.cs` | groupe `Passages_jsonl_matches_golden` | 9 |
| `tests/fixtures/golden/passages.jsonl`, `tests/fixtures/README.md` | nouveau golden, sa ligne, « five machine outputs » | 9 |
| `.github/workflows/ci.yml` | appel `okf passages` dans le smoke test Native AOT | 9 |
| `README.md`, `CHANGELOG.md`, `CLAUDE.md` | documentation | 10 |

---

### Task 1 : Prérequis A — prédicat d'espaces de `LinkScanner`

Spec §2.3 A.

**Files:**
- Modify: `src/OKF4net/Links.cs` (sites `:763` fermeture de fence, `:1358` soulignement setext, `:1442` balise HTML seule sur sa ligne)
- Test: `tests/OKF4net.Tests/LinksTests.cs`

**Interfaces:**
- Produces: `private static bool IsSpacesOrTabs(ReadOnlySpan<char> s)` dans `LinkScanner` (vrai si vide ou seulement U+0020/U+0009). Aucune API publique ne change.

- [ ] **Step 1: Écrire les trois tests qui échouent**

```csharp
[Fact]
public void Fence_close_followed_by_nbsp_does_not_close()
{
    // CommonMark §4.5: only spaces and tabs may follow a closing fence.
    Assert.Empty(LinkScanner.ExtractAtxHeadings("~~~\n~~~\u00A0\n# H\nx\n~~~\n"));
}

[Fact]
public void Setext_underline_followed_by_nbsp_is_paragraph_text()
{
    // Today "===\u00A0" is taken as an underline and closes the paragraph, so the next
    // line (indented 4) is indented code and its link is hidden. Fixed: "===\u00A0" is
    // continuation text, the indented line too, and the link is visible.
    Assert.Equal(["a.md"], LinkScanner.ExtractLinks("Title\n===\u00A0\n    [x](a.md)\n").Select(l => l.Target));
}

[Fact]
public void Complete_tag_followed_by_nbsp_does_not_open_html_block()
{
    // A kind-7 HTML block needs only spaces/tabs after the tag. With NBSP the line is a
    // paragraph, so the link on the next line is visible.
    Assert.Equal(["a.md"], LinkScanner.ExtractLinks("<span>\u00A0\n[x](a.md)\n").Select(l => l.Target));
}
```

- [ ] **Step 2: Lancer** `dotnet test OKF4net.sln --filter "FullyQualifiedName~LinksTests"` → les trois nouveaux tests échouent.

- [ ] **Step 3: Implémenter** `IsSpacesOrTabs` et remplacer `.IsWhiteSpace()` par cet appel **aux trois sites seulement**. Les autres usages d'espaces de la passe (labels, `char.IsWhiteSpace` de `:1059`) ne changent pas.

- [ ] **Step 4: Test de l'effet voulu au chargement**

```csharp
[Fact]
public void Bundle_load_drops_link_hidden_by_nbsp_fence_close()
{
    using var dir = new TempDir();
    dir.Write("c.md", "---\ntype: Note\n---\n~~~\n~~~\u00A0\n[x](missing.md)\n~~~\n");
    var bundle = Bundle.Load(dir.Path);
    Assert.Empty(bundle.LinksFrom(ConceptId.Parse("c")));
}
```

- [ ] **Step 5: Lancer** `dotnet test OKF4net.sln` → tout passe, goldens inchangés (aucune fixture de `tests/fixtures` ni de `bundles/` ne contient d'espace Unicode).

- [ ] **Step 6: Commit** `fix(links): only spaces and tabs may follow a closing fence, setext underline or lone HTML tag (CommonMark)`

---

### Task 2 : Prérequis B — structure de blocs exposée par la passe

Spec §2.2, §2.3 B.

**Files:**
- Create: `src/OKF4net/Internal/BlockStructure.cs`
- Modify: `src/OKF4net/Links.cs` (`CodeFreeLinePairs` : paramètre enregistreur optionnel ; le wrapper `AtxHeadingContent(string)` passe en `internal`)
- Test: `tests/OKF4net.Tests/BlockStructureTests.cs`

**Interfaces:**
- Produces (`namespace OKF4net.Internal`) :
  - `internal enum AtomicBlockKind { FencedCode, IndentedCode, Html }`
  - `internal readonly record struct AtxHeadingLine(int LineIndex, int Level, int ContainerDepth)`
  - `internal readonly record struct AtomicBlock(int FirstLine, int LastLine, AtomicBlockKind Kind)` (bornes incluses, index de ligne au sens de `LfLines.Split`)
  - `internal sealed record BlockStructure(IReadOnlyList<AtxHeadingLine> Headings, IReadOnlyList<AtomicBlock> AtomicBlocks)`, blocs triés par `FirstLine`, sans chevauchement
  - `internal static BlockStructure LinkScanner.ScanBlockStructure(string body)`
  - `internal static (int Level, int Start, int Length)? LinkScanner.AtxHeadingContent(string line)` : on partage **ce wrapper** (qui saute l'indentation puis appelle `AtxHeadingContentAt`), pas `AtxHeadingContentAt` lui-même. Il ne sert que sur une ligne que la passe a déjà classée heading à profondeur 0.

**Règles de bornes**, à respecter dans l'implémentation et figées par les tests :
- fence : de la ligne d'ouverture à la ligne de fermeture **incluse** ; non fermée, jusqu'à la dernière ligne ; fermée par la sortie de son conteneur, jusqu'à la ligne **précédant** celle qui ferme le conteneur ;
- HTML de type 1 à 5 (fin par marqueur) : jusqu'à la ligne du marqueur **incluse**, éventuellement la ligne d'ouverture elle-même ;
- HTML de type 6 et 7 (fin par ligne blanche) : jusqu'à la ligne **précédant** la ligne blanche, qui n'appartient pas au bloc ;
- HTML de tout type fermé par la sortie de son conteneur : jusqu'à la ligne **précédant** celle qui ferme le conteneur (même règle que la fence, `Links.cs:843-845`) ;
- code indenté : de la première à la dernière ligne indentée ; les lignes blanches **entre** deux lignes indentées du même conteneur en font partie, les lignes blanches finales non ; un changement de conteneur ferme le bloc.

- [ ] **Step 1: Tests qui échouent**, un `[Fact]` par ligne, chacun sur `LinkScanner.ScanBlockStructure(body)` :

| Test | Body | Attendu |
|---|---|---|
| `Top_level_heading_has_depth_zero` | `"# A\nx\n"` | `Headings == [(0, 1, 0)]` |
| `Heading_in_block_quote_has_depth_one` | `"> # A\n"` | `Headings == [(0, 1, 1)]` |
| `Heading_in_list_item_has_depth_one` | `"- # A\n"` | `Headings == [(0, 1, 1)]` |
| `Fenced_block_spans_its_fences` | `"~~~\n# H\n~~~\n"` | `Headings` vide ; `[(0, 2, FencedCode)]` |
| `Unclosed_fence_runs_to_end` | `"```\na\n\nb\n"` | `[(0, 3, FencedCode)]` |
| `Fence_closed_by_container_exit` | `"- ~~~\n  a\n\n  b\nx\n"` | `[(0, 3, FencedCode)]` |
| `Fence_in_list_then_heading` | `"- ~~~\n  x\n# H\n"` | `[(0, 1, FencedCode)]` ; `Headings == [(2, 1, 0)]` |
| `Indented_code_keeps_inner_blank_not_trailing` | `"    a\n\n    b\n\np\n"` | `[(0, 2, IndentedCode)]` |
| `Indented_code_at_end_excludes_trailing_blanks` | `"    a\n\n"` | `[(0, 0, IndentedCode)]` |
| `Two_list_items_give_two_indented_blocks` | `"-     a\n\n-     b\n"` | `[(0, 0, IndentedCode), (2, 2, IndentedCode)]` |
| `Html_pre_block_ends_at_marker` | `"<pre>\n# H\n\nx\n</pre>\n"` | `Headings` vide ; `[(0, 4, Html)]` |
| `Html_div_block_ends_before_blank` | `"<div>\na\n\n# H\n"` | `[(0, 1, Html)]` ; `Headings == [(3, 1, 0)]` |
| `Html_comment_closed_on_its_line` | `"<!-- x -->\n# H\n"` | `[(0, 0, Html)]` ; `Headings == [(1, 1, 0)]` |
| `Html_closed_by_container_exit` | `"> <pre>\n> a\nx\n"` | `[(0, 1, Html)]` (la ligne `x` ne continue pas la citation : un bloc HTML n'admet pas de continuation paresseuse) |

Plus un `[Theory]` `Atx_content_shared_helper` sur `AtxHeadingContent(line)` avec les six lignes du tableau de spec §3.1 et leurs contenus (`foo ### b`, `foo \###`, `foo#`, `foo`, chaîne vide, `A  B`). La ligne `# foo<TAB>###<TAB>` s'écrit `"# foo\t###\t"`.

- [ ] **Step 2: Lancer** `--filter "FullyQualifiedName~BlockStructureTests"` → échec (type absent).

- [ ] **Step 3: Implémenter.** `CodeFreeLinePairs` reçoit un paramètre optionnel `BlockStructureRecorder? structure = null` (classe privée imbriquée qui accumule les deux listes). La profondeur d'un heading ATX est `containers.Count` à la branche `TryParseAtxHeading`, après fermeture et ouverture des conteneurs. Les bornes suivent les règles ci-dessus, aux branches existantes (fence `:759-768` et `:876-881`, HTML `:772-781` et `:883-888`, fermeture de conteneur `:843-845`, code indenté `:868-874` avec une plage de blancs en attente). **Les paires `Raw`/`Blanked` produites ne changent pas.** `ScanBlockStructure` appelle `CodeFreeLinePairs(body, structure: recorder)`.

- [ ] **Step 4: Lancer** `dotnet test OKF4net.sln` → tout passe. Les tests existants de `LinksTests` (liens, notes, headings, entrées d'index) prouvent que la sortie existante n'a pas changé.

- [ ] **Step 5: Commit** `feat(links): expose ATX heading depth and atomic block ranges from the block pass`

---

### Task 3 : Prérequis C — valeurs de cycle de vie non scalaires

Spec §2.3 C (tableau des quatre cas).

**Files:**
- Modify: `src/OKF4net/Lifecycle.cs`, `src/OKF4net/Frontmatter.cs:151`, `src/OKF4net/Validate.cs` (enum `DiagnosticCode` après `KeyNotPortable` ; contrôle juste après celui de `StaleAfterRaw`)
- Test: `tests/OKF4net.Tests/LifecycleTests.cs`, `tests/OKF4net.Tests/FrontmatterTests.cs`, `tests/OKF4net.Tests/ValidateTests.cs`

**Interfaces:**
- Produces :
  - `public bool Lifecycle.StatusNotScalar { get; init; }`, `public bool Lifecycle.StaleAfterNotScalar { get; init; }` (les quatre paramètres positionnels ne changent pas)
  - `DiagnosticCode.StaleAfterNotScalar` (dernier membre de l'enum)
  - message : ``"stale_after is not a scalar timestamp"``, sévérité `Warning`, champ `"stale_after"`

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
[InlineData("stale_after: null", false, false)]
[InlineData("stale_after: \"\"", false, false)]
public void NotScalar_flags_cover_sequences_and_mappings_only(string line, bool statusNotScalar, bool staleNotScalar)
{
    var fm = OkfDocument.Parse($"---\ntype: Note\n{line}\n---\nx\n").Frontmatter;
    Assert.Equal(statusNotScalar, fm.Lifecycle.StatusNotScalar);
    Assert.Equal(staleNotScalar, fm.Lifecycle.StaleAfterNotScalar);
}
```

Dans `FrontmatterTests`, pour les mêmes entrées, `StatusIsKnown`, `StaleAfterRaw` et `StaleAfterMalformed` gardent **exactement** leur valeur actuelle. Ces valeurs sont lues en lançant le test sur le code d'avant la modification, puis figées en littéraux, pas supposées.

Dans `ValidateTests` :
- un concept avec `title`, `description`, `status: [deprecated]` et `stale_after: { bad: value }` donne, filtrés sur `status` et `stale_after`, exactement `[StatusNotScalar, StaleAfterNotScalar]` dans cet ordre, une fois chacun, sans `StatusUnknown` ni `StaleAfterInvalid` ;
- `stale_after: null` ne donne aucun diagnostic sur `stale_after`.

- [ ] **Step 2: Lancer** les trois classes de tests → échec.

- [ ] **Step 3: Implémenter.** `Frontmatter.Lifecycle` lit les deux `YamlValue` et pose les drapeaux par `value is YamlSequence or YamlMapping`, avec `with { … }` sur le résultat de `Lifecycle.From`. Le validateur ajoute le warning quand `fm.Lifecycle.StaleAfterNotScalar`. Rien d'autre ne change : `ConceptAudit` transporte le `Lifecycle` entier, et `JsonOutput` projette explicitement ses champs, donc aucun rendu existant ne bouge.

- [ ] **Step 4: Lancer** `dotnet test OKF4net.sln` → tout passe, goldens inchangés. Puis `dotnet test producers/OkfProducer.sln`.

- [ ] **Step 5: Commit** `fix(lifecycle): flag non-scalar status and stale_after; validator warns on non-scalar stale_after`

---

### Task 4 : Helpers partagés avec le viewer (coupe bornée, epoch arrondi, format fixe)

Spec §4.1 (troncature), §4.4 (`staleAfterEpochMs`, `staleAfter`). `SiteIndex` porte aujourd'hui les deux premiers en privé. Les extraire évite deux copies.

**Files:**
- Create: `src/OKF4net/Internal/DisplayText.cs`
- Modify: `src/OKF4net/Internal/OkfTimestamp.cs`, `src/OKF4net.Viewer/SiteIndex.cs`
- Test: `tests/OKF4net.Tests/DisplayTextTests.cs`, `tests/OKF4net.Tests/OkfTimestampTests.cs`

**Interfaces:**
- Produces :
  - `internal static string DisplayText.TruncateCodePoints(string text, int limit)` : texte inchangé si ≤ `limit` points de code, sinon les `limit − 1` premiers points de code, `TrimEnd()`, puis `…`. Une paire de surrogates compte pour un point de code et n'est jamais coupée. **Le parcours s'arrête dès le `limit + 1`-ième point de code** : coût O(limit), jamais O(|text|). La boucle actuelle de `SiteIndex.cs:176-184` parcourt toute la chaîne ; la version partagée corrige ce coût sans changer la sortie.
  - `internal static long? OkfTimestamp.CeilingUnixMilliseconds(DateTimeOffset? instant)`
  - `internal static string OkfTimestamp.FormatFixedUtc(DateTimeOffset instant)` → `yyyy-MM-ddTHH:mm:ss.fffffffZ` (7 décimales toujours)

- [ ] **Step 1: Tests qui échouent** :
  - `TruncateCodePoints(new string('a', 200), 200)` inchangé ;
  - `TruncateCodePoints(new string('a', 201), 200)` == 199 `a` + `…` ;
  - 201 emoji `"\U0001F600"` → 199 emoji + `…`, sans surrogate isolé ;
  - `TruncateCodePoints("a" + new string(' ', 198) + "bc", 200)` == `"a…"` ;
  - coût : sur une chaîne d'1 Mio, la troncature visite au plus `limit + 1` points de code (surcharge `internal` avec `ref int visited`, ou assertion sur un compteur de parcours) ;
  - `CeilingUnixMilliseconds` : `1970-01-01T00:00:00.0000001Z` → 1, `…:00.001Z` → 1, `null` → `null` ;
  - `FormatFixedUtc` d'un instant à la seconde pleine → `…:00.0000000Z` ; un instant à `+02:00` est converti en UTC.

- [ ] **Step 2: Lancer** → échec.

- [ ] **Step 3: Implémenter**, puis faire appeler ces deux helpers par `SiteIndex`. Le repli des espaces propre au viewer reste dans `SiteIndex`, avant l'appel à `TruncateCodePoints`. Supprimer les deux copies privées.

- [ ] **Step 4: Lancer** `dotnet test OKF4net.sln` → tout passe, tests du viewer inchangés. Si un test du viewer régénère `okf-index.js`, sa sortie doit être identique (aucun test ni harnais à modifier).

- [ ] **Step 5: Commit** `refactor: share bounded code-point truncation and ceiling-ms helpers between viewer and core`

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
  - `string Slug(string rawHeadingContent)` : règle de spec §4.3, minuscules par **`UnicodeCaseFold.ToLowercase`** (la casse complète Unicode déjà partagée avec le tri des titres, `Internal/UnicodeCaseFold.cs` ; jamais `Rune.ToLowerInvariant` seul, qui laisse `İ` inchangé). `ToLowercase` s'applique **une seule fois au contenu brut complet** : la règle Final_Sigma lit la suite de la chaîne, donc convertir un préfixe changerait un `Σ` en bordure de coupe. Seul le **filtrage** qui suit s'arrête dès que 64 points de code ont été gardés. Le coût est linéaire dans la longueur du heading, et chaque heading n'est slugué qu'une fois (tâche 7). Vide → `section`
  - `string TruncateElement(string element)` → `DisplayText.TruncateCodePoints(element, ElementLimit)`
  - `string Header(string titleOrId, IReadOnlyList<string> rawHeadings)` : chaque élément passé par `TruncateElement`, joints par ` > `
  - `string Prefix(string header)` → `header + PrefixTerminator`
  - `string EmbedText(string header, string text)` → `Prefix(header) + text`
  - `string EmbedHash(string embedText)` → `"sha256:" + hex minuscule`

- [ ] **Step 1: Tests qui échouent**, un par règle :
  - `Normalize("a\r\nb\rc")` == `"a\nb\rc"` ; `Normalize("a\uD800b")` == `"a\uFFFDb"` ;
  - `CountCodePoints("\U0001F600a")` == 2 ;
  - `Slug("**A** &amp; B")` == `"a-amp-b"` ; `Slug("[A](x)")` == `"ax"` ; `Slug("!!!")` == `"section"` ; `Slug("Été 2026")` == `"été-2026"` ; `Slug("Ωmega")` == `"ωmega"` ;
  - `Slug("Ωmega İstanbul")` == `"ωmega-istanbul"` : `UnicodeCaseFold.ToLowercase` donne `i` + U+0307 pour `İ`, et la marque U+0307 n'est ni lettre ni chiffre, donc retirée. Le même littéral est vérifié contre le binaire AOT en tâche 9 ;
  - `Slug(new string('a', 100))` == `new string('a', 64)` ; `Slug(new string('!', 199) + "abc")` == `"abc"` (le slug part du brut) ;
  - Final_Sigma en bordure de coupe : `Slug(new string('a', 63) + "ΣA")` == `new string('a', 63) + "σ"` (σ, pas ς : le Σ est suivi d'une lettre dans le contenu complet) ;
  - propriété : sur 1 000 chaînes aléatoires (graine fixe), `Slug` ne contient jamais `.`, `~`, `/` ni `#` ;
  - `Header("Orders", ["Schema", "Partitioning"])` == `"Orders > Schema > Partitioning"` ; `Header("T", [])` == `"T"` ; `Header("T", [new string('h', 300)])` == `"T > " + new string('h', 199) + "…"` ;
  - `EmbedText("T", "x")` == `"T\n\nx"` ; `EmbedHash("T\n\nx")` égal au hash calculé dans le test par `"sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("T\n\nx")))`.

- [ ] **Step 2: Lancer** → échec. **Step 3: Implémenter** (BCL : `Rune`, `UnicodeCaseFold.ToLowercase`, `SHA256.HashData`, `Convert.ToHexStringLower`). **Step 4: Lancer** → vert.

- [ ] **Step 5: Commit** `feat(passages): PassageText normalisation, slug, header and embed hash`

---

### Task 6 : `PassageSplitter` — sections et budget

Spec §3 (tout).

**Files:**
- Create: `src/OKF4net/PassageSplitter.cs`
- Test: `tests/OKF4net.Tests/PassageSplitterTests.cs`, `tests/OKF4net.Tests/PassageSplitterHostileTests.cs`

**Interfaces:**
- Consumes : `LinkScanner.ScanBlockStructure`, `LinkScanner.AtxHeadingContent` (tâche 2) ; `PassageText` (tâche 5) ; `LfLines.SplitSpans`.
- Produces (public, **exactement la signature de spec §3**) :
  - `public sealed record PassageBudget(int MaxSize, Func<string, int>? Measure = null)` ; `MaxSize <= 0` → `ArgumentOutOfRangeException`
  - `public sealed record PassageSection(IReadOnlyList<string> Headings, string Text, int Part, bool Oversize)` : `Headings` = pile **brute** des ancêtres et de la section (`Headings.Count` = profondeur dans l'arbre, y compris avec un saut de niveau)
  - `public static IReadOnlyList<PassageSection> PassageSplitter.Split(string body, string contextTitle, PassageBudget? budget = null)`
- Produces (interne, pour les tests de coût) :
  - `internal sealed class PassageWork { public long MeasuredCodePoints; public long VisitedLines; }`
  - `internal static IReadOnlyList<PassageSection> PassageSplitter.Split(string body, string contextTitle, PassageBudget? budget, PassageWork work)`, où `MeasuredCodePoints` additionne les points de code de chaque chaîne passée à la mesure.
  - **Points de comptage de `VisitedLines`**, et aucun autre : +1 par ligne dans la passe de coupure (affectation de chaque ligne de `N(body)` à sa section) ; +1 par ligne d'une section qui dépasse le budget, dans la formation de ses blocs. Les parcours internes de `LfLines` et de `LinkScanner.ScanBlockStructure` ne comptent pas. D'où `VisitedLines ≤ 2 × L`, avec L = nombre de lignes de `N(body)`.
  - **Structure de données imposée** : les blocs d'une section sont calculés **une fois** (plages d'indices de lignes, et offsets dans `N(body)`). Les regroupements, la dichotomie et les morceaux manipulent des plages de blocs et produisent `Text` comme tranche de `N(body)` par offsets, sans jamais reparcourir les lignes.
- **Sections d'origine et morceaux** : une section d'origine produit soit un élément `Part = 0`, soit des éléments `Part = 1..n` consécutifs. Un élément `Part ≥ 2` continue la section d'origine de l'élément précédent.
- **Coût hors mesure** : le découpeur avance avec des curseurs monotones sur les lignes, les headings et les blocs atomiques. Il ne parcourt jamais toutes les lignes ni tous les blocs pour chaque section.

- [ ] **Step 1: Tests de partition et de coupure** (`PassageSplitterTests`), chacun avec l'entrée exacte et le découpage attendu (`Headings`, `Text` par section) :
  - propriété : pour chaque concept de `bundles/` et de `tests/fixtures/` (chargés par `Bundle.Load`), `string.Concat(sections.Select(s => s.Text)) == PassageText.Normalize(body)` ;
  - avec et sans `\n` final ; `\r` isolé ; `"\uD800"` isolé ;
  - **Review Focus 1** : un body CRLF et le même body LF donnent des `Headings` et des `Text` identiques ;
  - les cas de spec §6.1 « Points de coupure » : indentation 0–3 contre 4 ; `#x` ; `#` seul ; fermeture après tabulation ; citation et élément de liste (pas de coupure) ; `<pre>` ; fence dans un élément de liste ; fence et fermeture indentées de 4 ; info string avec backtick ; fence + `"\u00A0"` (pas de fermeture) ; bloc non fermé ; setext (pas de coupure) ; saut H1 → H3 (`Headings == ["A", "C"]`) ; dépilement ;
  - les trois contre-exemples Codex sur `ComputationExtractor` : `"    ```\n# H\nx\n"` (une coupure `H`), `"```\n    ```\n# H\nx\n"` (aucune), ``"```a`b\n# H\nx\n"`` (une coupure `H`) ;
  - `Headings` bruts : `# **A** &amp; B` → `**A** &amp; B` ; `# [A](x)` → `[A](x)` ; un heading de 300 caractères reste entier dans `Headings`.

- [ ] **Step 2: Tests du budget** :
  - section qui tient avec un budget : `Part == 0` ;
  - séparateur : `Split("a\n\nb", "T", new PassageBudget(5))` → deux éléments, `(Part 1, Text "a\n\n", Oversize true)` puis `(Part 2, Text "b", Oversize false)`. Calcul : préfixe `"T\n\n"` = 3 ; `"T\n\na\n\n"` = 6 > 5, singleton, donc trop gros ; `"T\n\nb"` = 4 ;
  - points de code contre UTF-16 : un body de 10 emoji `"\U0001F600"` avec `MaxSize = 13` (préfixe 3 + 10) n'est pas redécoupé, alors que `string.Length` dépasserait ; de même avec 10 idéogrammes **hors plan de base** `"\U00020000"` (les idéogrammes courants U+4E00–U+9FFF ont `string.Length` 1 et ne prouveraient rien) ;
  - code indenté `"    a\n\n    b\n"` jamais coupé ; fence et HTML atomiques ;
  - rattachements : blancs en tête de section, blancs après la ligne de heading, ligne de heading attachée au premier bloc à contenu ;
  - section réduite à sa ligne de heading, section faite de blancs, `Split("", "T", new PassageBudget(1))` → un élément, `Part == 0`, `Oversize == true` ;
  - préambule redécoupé : `Part` 1..n sur la section sans heading ;
  - mesure enregistreuse : elle reçoit d'abord l'`embedText` complet de la section (`Prefix(Header(...)) + Text`), puis le préfixe exact `Header(...) + "\n\n"` une fois, puis chaque bloc une fois, puis l'`embedText` exact de chaque regroupement une fois ;
  - mesure non additive (double de test) : la dichotomie coupe à ⌊k/2⌋ et termine sur des singletons.

- [ ] **Step 3: Tests hostiles** (`PassageSplitterHostileTests`) : les entrées de spec §6.2 (100 000 headings, 10 000 frères homonymes, ligne de 10 Mo, heading d'1 Mo + 10 000 sous-sections, cas glouton `N` blocs `x` / budget `3N/2 + 3`, cas dichotomique, fences non fermées ou alternées, titres C0/C1/bidi/surrogates isolés). Pour chacun, avec `PassageWork` :
  - la partition tient ;
  - `VisitedLines <= 2 * L`, L = nombre de lignes de `N(body)` ;
  - mesure par défaut : `MeasuredCodePoints <= 2 * |N(body)| + 3 * |sortie|`, où |sortie| = somme des `CountCodePoints(EmbedText(...))` de **tous** les éléments retournés ;
  - mesure non additive : `MeasuredCodePoints <= 4 * (|N(body)| + |sortie|) * (1 + log2(k))`, avec `k = max(1, L)` (majorant sûr du nombre de blocs d'un regroupement, calculable par le test, et jamais nul : `Split("", "T", budget)` mesure 3 points de code pour zéro bloc) ;
  - aucune mesure de temps.

- [ ] **Step 4: Lancer** → échec. **Step 5: Implémenter** selon spec §3.1–§3.3 : `N(body)`, `SplitSpans`, `ScanBlockStructure`, coupures sur les headings `ContainerDepth == 0`, pile par niveau, contenu par `AtxHeadingContent` de la ligne ; budget en quatre étapes de spec §3.3, sommes en `long`, coupure à ⌊k/2⌋. **Step 6: Lancer** → vert ; `dotnet test producers/OkfProducer.sln`.

- [ ] **Step 7: Commit** `feat(passages): PassageSplitter with heading cuts and linear-work budget`

---

### Task 7 : `PassageExport` — ids, omissions, projection v0.2

Spec §4 (tout).

**Files:**
- Create: `src/OKF4net/PassageExport.cs`
- Test: `tests/OKF4net.Tests/PassageExportTests.cs`

**Interfaces:**
- Consumes : `PassageSplitter.Split`, `PassageSection` (tâche 6), `PassageText` (tâche 5), `ConceptAudit.Run(bundle, AuditQuery.All)`, `AuditFinding`, `OkfTimestamp.CeilingUnixMilliseconds` (tâche 4).
- Produces (public) :
  - `public sealed record Passage(string Id, string SectionId, ConceptId ConceptId, int Ordinal, IReadOnlyList<string> Headings, string Text, string EmbedText, string EmbedHash, bool Oversize, string? Type, string? Title, IReadOnlyList<string> Tags, TrustTier Trust, bool ReviewOutdated, ConceptStatus Status, bool StatusKnown, DateTimeOffset? StaleAfter, long? StaleAfterEpochMs, bool StaleAfterMalformed)`. `Headings` y est **tronqué** (200, ellipse comprise) ; `Title` est **intégral**.
  - `public static class PassageExport` avec `public const string FormatVersion = "okf-passages/1"` et `public static IEnumerable<Passage> Enumerate(Bundle bundle, PassageBudget? budget = null)` : itérateur paresseux, l'audit se fait au premier `MoveNext`.

**Algorithme d'identité**, qui n'est pas laissé au choix :
1. Parcourir les concepts dans l'ordre de `ConceptAudit.Run(bundle, AuditQuery.All).Findings` (déjà trié par `ConceptId.CompareTo`), en associant chaque finding à son `Concept` par id.
2. Pour un concept : `Split(body, title ?? id.ToString(), budget)`.
3. Regrouper les éléments en sections d'origine (un `Part ∈ {0, 1}` ouvre une section d'origine, un `Part ≥ 2` continue la précédente).
4. Pour chaque section d'origine, dans l'ordre, une **seule fois**, avec sa profondeur `d = Headings.Count` et une pile de chemins désambiguïsés indexée par profondeur :
   - **`d == 0` (préambule)** : chemin vide, ni slug ni compteur. La pile est réinitialisée avec le chemin vide à la profondeur 0.
   - **`d > 0`** : le parent est l'entrée de profondeur `d − 1` de la pile. On calcule `slug = Slug(Headings[d − 1])`, puis le rang `k` avec un compteur par `(chemin du parent, slug)`. Le segment vaut `slug` si `k = 1`, sinon `slug.k`. Le chemin vaut `segment` si `d = 1`, sinon `parent + "/" + segment`. On le place à la profondeur `d` et on tronque la pile au-delà.
5. Le compteur compte **toutes** les sections d'origine, omises comprises. L'omission (spec §4.2) se décide ensuite, une fois par section d'origine, sur le texte de la section entière : privé de sa ligne de heading si `d > 0`, entier pour le préambule.
6. Chaque élément émis prend `SectionId = conceptId + "#" + chemin`, puis `Id = SectionId` si `Part = 0`, sinon `SectionId + "~" + Part`. `Ordinal` compte les éléments émis du concept, à partir de 0.

- [ ] **Step 1: Tests qui échouent**, chacun avec un bundle construit par `TempDir` :
  - projection : `Trust`, `ReviewOutdated` (= `finding.IsReviewOutdated`), `Status`, `StaleAfter` égaux à l'`AuditFinding` du concept ; `StatusKnown == lc.StatusIsKnown && !lc.StatusNotScalar` ; `StaleAfterMalformed == lc.StaleAfterMalformed || lc.StaleAfterNotScalar` ; `StaleAfterEpochMs == OkfTimestamp.CeilingUnixMilliseconds(lc.StaleAfter)` ;
  - `stale_after: 2030-01-01T02:00:00+02:00` → `StaleAfter` à `2030-01-01T00:00:00Z` ;
  - concepts malformés de spec §6.3 (`retired`/`not-a-date`, `[deprecated]`/`{bad: value}`), et les cas `null`, `~`, vide non cité, `""` contre le tableau de spec §2.3 C ;
  - `ReviewOutdated` : `generated.at` postérieur à la relecture → `true` ; seulement `timestamp` postérieur → `true` ; jamais relu → `false` ; `generated.at` illisible avec `timestamp` postérieur → `false` ;
  - **ordre** : fichiers `a.md`, `a/b.md`, `a-b.md` → concepts dans l'ordre littéral `["a", "a/b", "a-b"]` (alors que `bundle.Concepts` mettrait `a/b` avant `a`) ; passages dans l'ordre du document ;
  - omissions avant budget, et une section omise compte dans les rangs : `"# A\n# A\nx\n"` → seul passage émis `c#a.2` ;
  - ids : `"# A\nx\n# A\ny\n# A-1\nz\n"` → `c#a`, `c#a.2`, `c#a-1` ; `"# !!!\nx\n# ???\ny\n# section-1\nz\n"` → `c#section`, `c#section.2`, `c#section-1` ; `"# A\n## X\nun\n# A\n## X\ndeux\n"` → `c#a/x`, `c#a.2/x`, et après insertion d'un `# A` entre les deux, le second devient `c#a.3/x` ;
  - morceaux : `~1`, `~2`, `SectionId` sans suffixe ; préambule redécoupé `c#~1` ;
  - **morceaux et homonymes** : `"# A\npara1\n\npara2\n# A\ny\n## B\nz\n"`, titre `T`, d'abord avec `MaxSize = 20`, puis sans budget. Calcul : la première section fait 24 (> 20) et se coupe en morceaux de 18 et 13 ; la seconde `# A` fait 13 ; `## B` fait 18. Attendu avec budget : `c#a~1`, `c#a~2`, `c#a.2`, `c#a.2/b`, ordinaux 0 à 3, aucun `Oversize`. Sans budget : `c#a`, `c#a.2`, `c#a.2/b`, ordinaux 0 à 2. Après regroupement des morceaux consécutifs par section d'origine, les `SectionId` des deux exports sont identiques, section par section ;
  - deux headings `new string('x', 64) + "AAA"` et `new string('x', 64) + "BBB"` → segments `new string('x', 64)` et `new string('x', 64) + ".2"` ;
  - stabilité : `"# [A][r]\nx\n# Z\ny\n"` et la même chaîne + `"\n[r]: /target\n"` → même `SectionId` pour la première section ; l'exemple de la fence de spec §4.3 (`c#b/c` → `c#a/c`) ;
  - troncature : `Headings` tronqués à 200 ; `Title` d'un titre de 300 caractères **intégral** ; l'en-tête de `EmbedText` contient le titre tronqué ;
  - `EmbedHash` recalculé dans le test ;
  - **taille hostile** : heading d'1 Mo suivi de 10 000 sous-sections → pour chaque passage, `Id.Length`, `SectionId.Length` et la somme des longueurs de `Headings` restent sous `10 * (64 + 8)` et `7 * 200` respectivement ; 10 000 frères homonymes → 10 000 `Id` distincts ;
  - **Review Focus 2** : concept sans heading → un passage `c#`, `Headings` vide, `EmbedText == Title + "\n\n" + body` ;
  - **Review Focus 3** : un fichier `a#b.md` à côté de `a.md` → `bundle.ParseProblems` contient `a#b.md`, aucun passage pour lui, ids tous uniques ;
  - **Review Focus 4** : concept à body vide ou de blancs → aucun passage ;
  - paresse : trois concepts aux titres distincts et un budget dont la `Measure` enregistre ses arguments. Après le premier `MoveNext`, aucun argument enregistré ne commence par le titre du troisième concept.

- [ ] **Step 2: Lancer** → échec. **Step 3: Implémenter** selon l'algorithme ci-dessus. **Step 4: Lancer** → vert ; `dotnet test producers/OkfProducer.sln`.

- [ ] **Step 5: Commit** `feat(passages): PassageExport with recursive ids, omissions and v0.2 projection`

---

### Task 8 : CLI — `stderr` pour les handlers de verbe

Spec §5.2 « Changement transverse ».

**Files:**
- Modify: `src/OKF4net.Cli/OkfCli.cs` (`VerbSpec.Run`, appel `spec.Run(...)` vers `:289`, les huit `Cmd*`)
- Test: aucun nouveau test. La preuve est l'invariance.

**Interfaces:**
- Produces : `Func<CliArgs, TextReader, TextWriter, TextWriter, int> Run` ; signature de handler `private static int CmdX(CliArgs parsed, TextReader stdin, TextWriter stdout, TextWriter stderr)`.

- [ ] **Step 1:** changer le type du délégué, l'appel (`spec.Run(parsed, stdin, stdout, stderr)`), les huit signatures, et la doc XML de `VerbSpec` qui décrit le délégué. Aucun handler n'écrit sur `stderr`.
- [ ] **Step 2: Lancer** `dotnet test OKF4net.sln` → tout passe, `git status tests/fixtures` vide.
- [ ] **Step 3: Commit** `refactor(cli): pass stderr to verb handlers`

---

### Task 9 : Le verbe `okf passages`

Spec §5, §6.4.

**Files:**
- Modify: `src/OKF4net.Cli/OkfCli.cs` (doc XML « Eight subcommands » → nine ; texte `Usage` : ligne `COMMANDS` et ligne `OPTIONS` ; entrée `Verbs["passages"]` ; `CmdPassages`), `src/OKF4net.Cli/JsonOutput.cs`, `tests/OKF4net.Tests/GoldenUpdate.cs`, `.github/workflows/ci.yml` (étape « Run published okf »), `tests/fixtures/README.md`
- Create: `tests/fixtures/golden/passages.jsonl`
- Test: `tests/OKF4net.Tests/CliTests.cs` (dont `AllVerbs` et les théories `Unknown_option_is_rejected` / `Extra_positional_is_rejected`), `tests/OKF4net.Tests/MachineOutputTests.cs`, `tests/OKF4net.Tests/GoldenParityTests.cs`

**Interfaces:**
- Consumes : `PassageExport.Enumerate`, `PassageExport.FormatVersion`, `AuditVocabulary.Name`, `OkfTimestamp.FormatFixedUtc`.
- Produces :
  - `internal sealed record PassageJson(string Format, string Id, string SectionId, string ConceptId, int Ordinal, IReadOnlyList<string> Headings, string Text, string EmbedText, string EmbedHash, bool Oversize, string? Type, string? Title, IReadOnlyList<string> Tags, string Trust, bool ReviewOutdated, string Status, bool StatusKnown, string? StaleAfter, long? StaleAfterEpochMs, bool StaleAfterMalformed)`. L'ordre des paramètres **est** l'ordre JSON de spec §4.1 ; ajouter `[JsonSerializable(typeof(PassageJson))]` sur `CliJsonContext`.
  - verbe : usage `okf passages <bundle> [--max-chars <n>]` ; résumé `Export a bundle as JSONL passages for an embedding pipeline.` ; valued flag `--max-chars` ; option line `    --max-chars <n>  Re-split sections whose embedText exceeds n characters`
  - parsing : `int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var n) && n > 0`, sinon `CliArgumentException` ``--max-chars expects a positive integer, got '<valeur>'``. Un `--max-chars` sans valeur est déjà refusé par `CliArgs` avec ``--max-chars requires a value`` (`CliArgs.cs:221`).
  - avertissement : ``warning: skipped <display path>: <error>`` (`bundle.DisplayPath(problem)`, `problem.Error`, comme `CmdInfo`)

- [ ] **Step 1: Tests CLI qui échouent** (`CliTests`) :
  - `passages` ajouté à `AllVerbs` et aux deux théories existantes ;
  - `--max-chars 0`, `-1`, `abc`, `+5`, `" 5"`, `2147483648` → code 1, stdout vide, ``--max-chars expects a positive integer, got '…'`` ; le bundle n'est **pas** lu : sur un chemin inexistant, le message reste celui de `--max-chars` ;
  - `--max-chars` valide qui coupe : un concept dont une section dépasse → au moins une ligne dont l'`id` se termine par `~1`, code 0 ;
  - `--max-chars` en dernier argument → code 1, ``--max-chars requires a value`` ;
  - bundle inexistant → code 1, stdout vide ;
  - **échec fatal après un problème récupérable** : `a-valid.md` (concept valide), `b-malformed.md` (frontmatter illisible) et `z-invalid-utf8.md` (octets UTF-8 invalides, écrits par `File.WriteAllBytes`, comme `BundleTests.cs:193`). Les noms fixent l'ordre des clés : le problème récupérable est rencontré **avant** l'erreur fatale (`Bundle.cs:393`). Attendu : code 1, stdout **vide**, une seule ligne `error: …` sur stderr et **aucune** ligne `warning:` ;
  - un concept valide et un fichier au frontmatter illisible → stdout = les lignes du concept valide, stderr = une ligne `warning: skipped …`, code 1 ;
  - même cas sans aucun concept valide → stdout vide, code 1 ;
  - **Review Focus 3** : `a.md` valide et `a#b.md` → une ligne `warning: skipped …a#b.md…`, code 1 ;
  - bundle vide (dossier sans fichier) → stdout vide, stderr vide, code 0 ;
  - bundle valide sans problème → code 0, stderr vide ;
  - **Review Focus 5** : un bundle avec `index.md` et `log.md` → aucune ligne dont `conceptId` vaut `index` ou `log` ;
  - `okf passages --help` → code 0 et l'usage du verbe ; `okf --help` contient la ligne `passages`.

- [ ] **Step 2: Tests machine** (`MachineOutputTests`) sur un bundle construit dans le test, aux valeurs **contrastées** : un premier concept `human-reviewed` (une entrée `verified` `human:…`), `deprecated`, avec `stale_after`, un heading dupliqué, et un `generated.at` **postérieur** à sa relecture, donc `reviewOutdated: true` ; un second `unverified` (aucune entrée `verified`), `draft`, sans échéance ni `generated`, donc `reviewOutdated: false`. Un concept `unverified` ne peut pas avoir de relecture dépassée : `Trust.DeriveTier` et `IsReviewOutdated` lisent la même liste `verified`. Pour **chaque ligne**, un attendu complet des **20 propriétés**, dérivé à la main dans le test (pas recopié d'une sortie), comparé valeur par valeur après `JsonDocument.Parse`. Plus : les noms de propriétés, dans l'ordre, valent exactement la liste de spec §4.1 ; `text` égal à la tranche de `N(body)` calculée dans le test ; `embedHash` recalculé. Mettre à jour la phrase « the four machine outputs » de `tests/fixtures/README.md` (cinq, avec `passages`).

- [ ] **Step 3: Lancer** → échec. **Step 4: Implémenter** `CmdPassages` : valider `--max-chars` d'abord, puis `Load(path)` (le chargement est terminé avant toute écriture), puis `foreach` sur `PassageExport.Enumerate` en écrivant chaque ligne (`JsonSerializer.Serialize(json, CliJsonContext.Default.PassageJson)` + `"\n"`), puis les avertissements sur `stderr`, puis le code (1 si `bundle.ParseProblems.Count > 0`). **Step 5: Lancer** → vert.

- [ ] **Step 6: Golden.**
  1. Ajouter `["Passages_jsonl_matches_golden"] = ["passages.jsonl"]` à `GoldenUpdate.Groups`.
  2. Ajouter `Passages_jsonl_matches_golden` dans `GoldenParityTests` : `WithRepoRootAsCwd(() => Run("passages", "tests/fixtures/okf_v02"))`, code 0, comparaison par le même helper que les autres groupes (`AssertGolden` ou son équivalent dans ce fichier).
  3. Capture, en deux exécutions, dans la forme décrite par `tests/fixtures/README.md`. En PowerShell (cette machine) :
     - `$env:OKF_UPDATE_GOLDEN = 'Passages_jsonl_matches_golden'; dotnet test OKF4net.sln --filter "FullyQualifiedName~GoldenParityTests.Passages_jsonl_matches_golden"`, qui écrit le fichier puis échoue par conception ;
     - `Remove-Item Env:OKF_UPDATE_GOLDEN`, puis la même commande `dotnet test`, qui passe.
  4. **Lire** `passages.jsonl` : il contient 2 lignes (`metrics/dau#`, et `metrics/legacy#citations`, le préambule vide de ce concept étant omis). Vérifier chaque champ à la main contre `tests/fixtures/okf_v02`.
  5. Ajouter la ligne du tableau de `tests/fixtures/README.md` : invocation ; ce que le golden seul garde (l'ordre des lignes, l'échappement JSON) ; ce qui est figé ailleurs (`MachineOutputTests`, `PassageExportTests`).
  6. `git status` ne montre **aucun autre** golden modifié, et `GoldenUpdateTests` passe.

- [ ] **Step 7: CI.** Dans `ci.yml`, étape « Run published okf », après `validate` :

```powershell
$expected = 'format,id,sectionId,conceptId,ordinal,headings,text,embedText,embedHash,oversize,type,title,tags,trust,reviewOutdated,status,statusKnown,staleAfter,staleAfterEpochMs,staleAfterMalformed'
$lines = & $env:OKF_BIN passages tests/fixtures/okf_v02
if ($LASTEXITCODE -ne 0) { throw "okf passages failed" }
if (-not $lines) { throw "okf passages wrote nothing" }
foreach ($l in $lines) {
  $o = $l | ConvertFrom-Json
  $names = ($o.PSObject.Properties | ForEach-Object Name) -join ','
  if ($names -cne $expected) { throw "unexpected passage properties: $names" }
  if ($o.format -cne 'okf-passages/1' -or -not $o.embedHash.StartsWith('sha256:')) { throw "unexpected passage line: $l" }
}
$dir = New-Item -ItemType Directory -Force "dist/passages-smoke-${{ matrix.rid }}"
Set-Content -Encoding utf8NoBOM -Path "$dir/c.md" -Value "---`ntype: Note`ntitle: T`n---`n# Ωmega İstanbul`nx`n"
$slugLines = @(& $env:OKF_BIN passages $dir)
if ($LASTEXITCODE -ne 0) { throw "okf passages failed on the Unicode smoke bundle" }
if ($slugLines.Count -ne 1) { throw "expected exactly one passage, got $($slugLines.Count)" }
$o = $slugLines[0] | ConvertFrom-Json
if ($o.id -cne 'c#ωmega-istanbul') { throw "AOT slug differs from the pinned one: $($o.id)" }
```

Les comparaisons utilisent `-cne` : `-ne` ignore la casse en PowerShell et laisserait passer `Ωmega` ou `SectionId`. L'attendu `c#ωmega-istanbul` est aussi figé par le test xunit de la tâche 5 (`Slug("Ωmega İstanbul")`). **Un écart entre l'hôte et le binaire AOT est un échec à investiguer**, jamais un littéral à réaligner sur la sortie observée.

- [ ] **Step 8: Lancer** `dotnet test OKF4net.sln`, puis `dotnet publish src/OKF4net.Cli -c Release`, et exécuter le binaire publié sur `tests/fixtures/okf_v02` : sortie identique au golden.

- [ ] **Step 9: Commit** `feat(cli): okf passages exports a bundle as JSONL passages`

---

### Task 10 : Documentation

Spec §7.

**Files:**
- Modify: `README.md` (section `okf passages`, ligne dans la liste des commandes), `CHANGELOG.md` (`[Unreleased]`), `CLAUDE.md` (architecture)

- [ ] **Step 1: README.** Une section `okf passages` qui contient :
  - un exemple de ligne, copiée du golden ;
  - le filtre Python de spec §7, avec `now_ms = time.time_ns() // 1_000_000` ;
  - la construction de `now` pour comparer `staleAfter` comme une chaîne : `datetime.now(timezone.utc).strftime("%Y-%m-%dT%H:%M:%S.%f") + "0Z"` (7 décimales) ;
  - les trois règles de spec §4.5 ;
  - la règle de version de spec §4.7 : ignorer les propriétés inconnues, refuser une version inconnue ; un ajout de champ garde `okf-passages/1`, tout le reste change la version ;
  - le préfixage multi-bundle (§4.3) ; un `conceptId` ne contient jamais `#` ;
  - la jointure par `conceptId` pour les champs exclus (§4.6) ;
  - la publication conditionnelle (`> out.jsonl.tmp && mv …`, §5.2) ;
  - les limites : seuls les headings ATX de premier niveau coupent (pas setext) ; les ids dépendent de l'arbre des headings (§4.3) ; le bundle ne doit pas être modifié pendant `Enumerate` ; la mise en minuscules des slugs s'appuie sur les tables de casse du runtime, qui peuvent différer entre un hôte ICU et le binaire `okf` (`InvariantGlobalization`) pour des caractères rares ou récemment encodés, et donc donner un id différent pour un même heading (`UnicodeCaseFold.cs`, commentaire sur les versions Unicode) ;
  - l'API : `PassageExport.Enumerate` et `PassageBudget`, avec un exemple `Measure: t => tokenizer.CountTokens(t)`.
- [ ] **Step 2: CHANGELOG** `[Unreleased]` :
  - *Added* : `okf passages` et l'API publique (`PassageSplitter`, `PassageBudget`, `PassageSection`, `PassageExport`, `Passage`) ;
  - *Fixed* : tâche 1 (avec l'effet sur les liens) ; tâche 3 (warning `StaleAfterNotScalar` : changement de sortie de `okf validate` pour les bundles malformés) ;
  - *Changed* : `LinkScanner` expose la structure de blocs (tâche 2, interne, sans effet visible) ; `VerbSpec.Run` reçoit `stderr` (interne) ; la troncature des descriptions du viewer est désormais bornée en coût (sortie identique).
- [ ] **Step 3: CLAUDE.md.**
  - Dans l'architecture de `src/OKF4net/` : une puce `PassageSplitter`/`PassageExport` (le découpeur unique ; ids et omissions dans une seule unité, ordre `ConceptId.CompareTo`) ; une puce sur `LinkScanner.ScanBlockStructure` comme seule grammaire de blocs pour tout nouveau consommateur ; `Internal/DisplayText` et `OkfTimestamp.CeilingUnixMilliseconds` comme helpers partagés avec le viewer.
  - Dans `src/OKF4net.Cli/` : ajouter `passages` à la liste des verbes, et corriger la phrase qui dit que le handler d'un verbe reçoit seulement `stdin` et `stdout` : il reçoit aussi `stderr`.
  - Mettre à jour les listes des sorties machine (`passages` en cinquième).
- [ ] **Step 4: Vérifier** `dotnet build OKF4net.sln`, `dotnet test OKF4net.sln`, `dotnet format OKF4net.sln --verify-no-changes`, `dotnet test producers/OkfProducer.sln`, puis `graphify update .`.
- [ ] **Step 5: Commit** `docs: okf passages in README, CHANGELOG and CLAUDE.md`
