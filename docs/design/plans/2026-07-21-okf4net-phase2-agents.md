# OKF4net Phase 2 — `OKF4net.Agents` (tools Microsoft Agent Framework)

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Exposer les capacités OKF4net aux agents Microsoft Agent Framework : un projet `OKF4net.Agents` contenant `OkfBundleTools` (9 tools `AIFunction`) + tests sans LLM + une démo d'intégration `ChatClientAgent` pilotée par un `IChatClient` scripté (zéro réseau).

**Architecture:** `src/OKF4net.Agents/` est le SEUL projet référençant `Microsoft.Agents.AI` (le core et le CLI restent zéro-dépendance). `OkfBundleTools` s'instancie sur un répertoire de bundle, expose des méthodes `[Description]` transformées en `AIFunction` via `AIFunctionFactory.Create`, avec un cache de `Bundle` invalidé après chaque écriture. Les tools retournent du **markdown agent-friendly** (les agents lisent du texte) sauf `okf_validate_bundle` qui retourne le rapport formaté comme le CLI.

**Tech Stack:** net10.0, C# 14. `OKF4net.Agents` : PackageReference `Microsoft.Agents.AI` (dernière stable — vérifier sur NuGet au moment de la Task 1 ; v1.13.x au moment de la rédaction ; amène `Microsoft.Extensions.AI.Abstractions` transitivement). Tests : xUnit existant + `Microsoft.Extensions.AI` (pour le `IChatClient` scripté de la Task 7, test-only).

## Global Constraints

- `src/OKF4net/` et `src/OKF4net.Cli/` restent **zéro PackageReference** — ne pas les toucher sauf mention explicite.
- CLAUDE.md (racine) fait loi : SPDX `// SPDX-License-Identifier: LGPL-3.0-or-later` en 1re ligne de chaque nouveau fichier ; file-scoped namespaces ; XML doc sur l'API publique ; nullable ; `TreatWarningsAsErrors`.
- **CI oblige `dotnet format OKF4net.sln --verify-no-changes`** : exécuter `dotnet format` avant CHAQUE commit.
- Ne jamais toucher `tests/fixtures/`. La suite existante (218 tests + goldens 5/5) doit rester verte à chaque tâche.
- Noms des tools : snake_case préfixé `okf_` (contrat du spec Phase 2) — c'est le nom vu par le LLM, le stabiliser dès maintenant.
- Écritures de fichiers : UTF-8 sans BOM, fins de ligne `\n` (mêmes règles que le core).
- Le contenu des bundles est **non-fiable** : les tools ne doivent jamais renvoyer d'instructions systèmes ; les descriptions `[Description]` documentent ce que fait le tool, pas le contenu du bundle.
- Ne pas toucher aux fichiers non suivis `.claude/` ni au stash git.

---

### Task 1: Scaffolding `OKF4net.Agents` + mise à jour CLAUDE.md

**Files:**
- Create: `src/OKF4net.Agents/OKF4net.Agents.csproj`, `src/OKF4net.Agents/OkfBundleTools.cs` (squelette)
- Modify: `OKF4net.sln` (add project), `tests/OKF4net.Tests/OKF4net.Tests.csproj` (ProjectReference vers OKF4net.Agents), `CLAUDE.md`
- Test: `tests/OKF4net.Tests/Agents/OkfBundleToolsTests.cs` (squelette + 1er test)

**Interfaces:**
- Consumes: `Bundle`, `OkfDocument` (core, inchangés).
- Produces:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Agents;

/// <summary>OKF bundle operations exposed as Microsoft Agent Framework function tools.</summary>
public sealed class OkfBundleTools
{
    public OkfBundleTools(string bundleRoot);          // valide que le répertoire existe (sinon ArgumentException)
    public string BundleRoot { get; }
    internal Bundle GetBundle();                        // cache paresseux : Bundle.Load au 1er accès
    internal void InvalidateBundle();                   // appelé après chaque écriture
}
```

Csproj : `net10.0`, `<PackageReference Include="Microsoft.Agents.AI" Version="<dernière stable — vérifier NuGet>" />`, mêmes props héritées de Directory.Build.props.

- [ ] **Step 1:** `dotnet new classlib -n OKF4net.Agents -o src/OKF4net.Agents -f net10.0`, supprimer Class1.cs, ajouter au sln, ajouter le PackageReference (vérifier la dernière version stable sur nuget.org — `dotnet add package Microsoft.Agents.AI` sans version fige la dernière), référencer `src/OKF4net`, référencer le nouveau projet depuis le csproj de tests.
- [ ] **Step 2:** Test qui échoue — `new OkfBundleTools("nonexistent-dir")` jette `ArgumentException` ; `new OkfBundleTools(fixtureAppendixA).GetBundle().Count == 4` (réutiliser le pattern `RepoRoot()` des tests existants pour localiser `tests/fixtures/appendix_a`). Run: `dotnet test --filter OkfBundleToolsTests` → FAIL (types absents).
- [ ] **Step 3:** Implémenter le squelette (constructeur + cache `Bundle?` + `GetBundle`/`InvalidateBundle`). → PASS.
- [ ] **Step 4:** Mettre à jour `CLAUDE.md` : section Architecture passe à **quatre** projets (ajouter la ligne `src/OKF4net.Agents/` — seule à dépendre de `Microsoft.Agents.AI`) et reformuler la hard rule zéro-dépendance : « `src/OKF4net/` et `src/OKF4net.Cli/` : BCL uniquement ; `src/OKF4net.Agents/` référence exclusivement `Microsoft.Agents.AI` ».
- [ ] **Step 5:** `dotnet format OKF4net.sln` puis suite complète → 218+2 verts, goldens 5/5. Commit `feat: scaffold OKF4net.Agents project`.

---

### Task 2: Tools de lecture — `okf_read_concept`, `okf_browse`, `okf_graph`

**Files:**
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs`
- Test: `tests/OKF4net.Tests/Agents/OkfBundleToolsTests.cs`

**Interfaces:**
- Produces (méthodes publiques sur `OkfBundleTools` ; les `[Description]` sont le contrat LLM — les rédiger précisément) :

```csharp
[Description("Read one concept from the OKF bundle: its frontmatter, body, outgoing links and backlinks.")]
public string ReadConcept([Description("The concept id, e.g. 'tables/orders'.")] string conceptId);

[Description("Browse the bundle via its index files (progressive disclosure). Without a path, lists the bundle root.")]
public string Browse([Description("Optional directory path within the bundle, e.g. 'tables'.")] string? path = null);

[Description("Inspect the cross-link graph. With a concept id: its outgoing links, backlinks and broken links. Without: bundle-wide stats.")]
public string Graph([Description("Optional concept id to focus on.")] string? conceptId = null);
```

Comportements :
- `ReadConcept` : `ConceptId.TryParse` → si invalide ou absent du bundle, retourner un message d'erreur textuel utile (`"Concept 'x' not found. Use okf_browse to list available concepts."`) — **jamais d'exception vers le LLM** pour les erreurs attendues. Sortie markdown : `# <title>` + bloc frontmatter (clé: valeur), body, sections `## Outgoing links` (avec marqueur `(broken)` si `!Exists`) et `## Backlinks`.
- `Browse` : lit l'`index.md` du répertoire demandé s'il existe (contenu brut — c'est déjà du markdown de navigation) ; sinon liste les concepts et sous-répertoires du niveau via le `Bundle`. Path invalide/hors bundle → message d'erreur textuel (pas de path traversal : rejeter `..`).
- `Graph` sans argument : nb concepts, nb liens, nb liens cassés, liste des liens cassés (source → cible). Avec argument : détail du concept.

- [ ] **Step 1:** Tests d'abord (sur la fixture appendix_a copiée en TempDir — PAS le répertoire fixtures directement, pour ne rien risquer) : `ReadConcept("tables/orders")` contient le titre + `## Backlinks` listant `datasets/sales` et `tables/customers` ; `ReadConcept("nope")` contient `not found` ; `Browse(null)` contient les entrées de l'index racine ; `Browse("../etc")` contient un message d'erreur ; `Graph(null)` contient `4 concepts`. → FAIL.
- [ ] **Step 2:** Implémenter les trois méthodes. → PASS. `dotnet format`, suite complète, commit `feat: read-only agent tools (read_concept, browse, graph)`.

---

### Task 3: `okf_search` — recherche full-text

**Files:**
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs`
- Test: `tests/OKF4net.Tests/Agents/OkfSearchTests.cs`

**Interfaces:**

```csharp
[Description("Full-text search across concept titles, descriptions, tags and bodies. Returns matching concept ids ranked by relevance.")]
public string Search([Description("The search query (case-insensitive substring terms).")] string query,
                     [Description("Optional tag filter: only concepts carrying this tag.")] string? tag = null);
```

Sémantique (nouvelle fonctionnalité, pas un port — rester simple, YAGNI) : découpe la requête en termes (espaces), matching substring **OrdinalIgnoreCase** ; score = pondération titre (×3) > tags/description (×2) > body (×1), somme des termes trouvés ; tri score décroissant puis id ordinal ; sortie markdown : liste `* <id> — <title> (score)` + extrait de la 1re ligne du body contenant un terme ; borné aux 20 premiers résultats avec mention du total ; requête vide → message d'usage.

- [ ] **Step 1:** Tests : recherche `"orders"` sur appendix_a → `tables/orders` premier (titre match) ; filtre `tag` réduit ; terme absent → `No results`; requête vide → message. → FAIL.
- [ ] **Step 2:** Implémenter. → PASS. Format, suite, commit `feat: okf_search full-text tool`.

---

### Task 4: Tools d'écriture — `okf_write_concept`, `okf_append_log`, `okf_regenerate_indexes`

**Files:**
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs`
- Test: `tests/OKF4net.Tests/Agents/OkfWriteToolsTests.cs`

**Interfaces:**

```csharp
[Description("Create or update a concept document. The frontmatter must contain non-empty type, title, description and timestamp (producer-grade validation is enforced before writing).")]
public string WriteConcept(
    [Description("The concept id (path without .md), e.g. 'tables/refunds'.")] string conceptId,
    [Description("Frontmatter as 'key: value' lines (YAML subset).")] string frontmatterYaml,
    [Description("The markdown body.")] string body);

[Description("Append an entry to the bundle root log.md under today's date (ISO).")]
public string AppendLog(
    [Description("Entry kind, e.g. 'Update' or 'Creation'.")] string kind,
    [Description("The entry text.")] string text);

[Description("Regenerate every index.md in the bundle (progressive-disclosure listings). Run after adding or changing concepts.")]
public string RegenerateIndexes();
```

Comportements :
- `WriteConcept` : `ConceptId.TryParse` (refus si invalide, message textuel) ; parse du frontmatter via `YamlValue.Parse` (erreur → message avec la ligne) ; construit `OkfDocument`, appelle **`Validate()` strict producteur AVANT écriture** — échec → message listant `MissingKeys`, rien n'est écrit ; refuse les noms réservés (`index.md`/`log.md` — i.e. id se terminant par `index` ou `log` au niveau fichier ? NON : refuser précisément les ids dont le dernier segment est `index` ou `log`, car `<seg>.md` collisionnerait avec les fichiers réservés) ; écrit `doc.Serialize()` (UTF-8 sans BOM, création des répertoires parents), `InvalidateBundle()`, retourne `"Written <id> (<n> bytes). Remember to run okf_regenerate_indexes."`.
- `AppendLog` : lit `log.md` racine s'il existe (`ChangeLog.Parse` — permissif), insère l'entrée sous la date du jour (nouveau `LogDay` en tête si la date n'existe pas — convention newest-first du spec §7), réécrit `ToMarkdown()`, `InvalidateBundle()`. Date du jour : `DateTime.UtcNow.ToString("yyyy-MM-dd")`.
- `RegenerateIndexes` : `IndexGenerator.RegenerateIndexes(BundleRoot)`, `InvalidateBundle()`, retourne la liste des fichiers écrits (chemins relatifs, séparateur `/`).

- [ ] **Step 1:** Tests (TempDir, copie d'appendix_a) : write valide → fichier sur disque, `GetBundle().Count` passe à 5 (preuve d'invalidation) ; write sans `description` → message `Missing`, **fichier absent** ; id `tables/index` → refusé ; frontmatter YAML invalide → message d'erreur avec ligne ; `AppendLog` sur bundle sans log.md → crée le fichier avec `## <today>` ; `AppendLog` deux fois même jour → 2 entrées sous une seule date ; `RegenerateIndexes` après un write → l'index du répertoire liste le nouveau concept. → FAIL.
- [ ] **Step 2:** Implémenter. → PASS. Format, suite, commit `feat: write agent tools (write_concept, append_log, regenerate_indexes)`.

---

### Task 5: `okf_validate_bundle` + `okf_changes_since`

**Files:**
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs`
- Test: `tests/OKF4net.Tests/Agents/OkfValidateChangesTests.cs`

**Interfaces:**

```csharp
[Description("Validate the bundle against OKF v0.1 conformance (§9). Returns the diagnostics report.")]
public string ValidateBundle();

[Description("Summarize bundle changes since a given ISO date, aggregated from every log.md in the bundle.")]
public string ChangesSince([Description("ISO date (yyyy-MM-dd), inclusive.")] string sinceDate);
```

- `ValidateBundle` : `BundleValidator.Validate(GetBundle())` → même rendu texte que le CLI (`[severity] path/concept: message` + ligne de synthèse conformant/non + comptages). Réutiliser le rendu de `Diagnostic.ToString()` existant.
- `ChangesSince` : date invalide (`ChangeLog.IsIsoDate` false) → message d'usage ; agrège `Bundle.LogFiles` (chaque fichier : `ChangeLog.Parse`), filtre les `LogDay` dont `Date` (comparaison ordinale de chaînes ISO — suffisant) ≥ sinceDate, groupe par fichier log (chemin relatif), sortie markdown par date décroissante ; aucun changement → `"No changes since <date>."`.

- [ ] **Step 1:** Tests : bundle appendix_a → `ValidateBundle()` contient `warning` (users.md non-strict) et `conformant` ; `ChangesSince("2020-01-01")` liste les entrées du log fixture ; `ChangesSince("2999-01-01")` → No changes ; `ChangesSince("pas-une-date")` → message d'usage. → FAIL.
- [ ] **Step 2:** Implémenter. → PASS. Format, suite, commit `feat: validate and changes-since agent tools`.

---

### Task 6: `GetTools()` — exposition `AIFunction`

**Files:**
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs`
- Test: `tests/OKF4net.Tests/Agents/AIFunctionExposureTests.cs`

**Interfaces:**

```csharp
/// <summary>All nine OKF tools as Agent Framework AIFunctions, ready for AsAIAgent(tools: ...).</summary>
public IList<AITool> GetTools();   // AITool: Microsoft.Extensions.AI.Abstractions
```

Implémentation : `AIFunctionFactory.Create(ReadConcept, "okf_read_concept")`, etc. — les 9, avec les noms snake_case explicites (le nom par défaut serait le nom C# de la méthode). Ordre stable (lecture → recherche → écriture → validation).

- [ ] **Step 1:** Tests SANS LLM, au niveau `AIFunction` : `GetTools()` a 9 éléments ; les noms sont exactement `okf_read_concept, okf_browse, okf_graph, okf_search, okf_write_concept, okf_append_log, okf_regenerate_indexes, okf_validate_bundle, okf_changes_since` ; chaque `AIFunction` a une Description non vide et un JSON schema dont les paramètres requis correspondent aux signatures ; invocation réelle via `AIFunction.InvokeAsync` avec un dictionnaire d'arguments (`okf_read_concept` avec `{"conceptId":"tables/orders"}` retourne le même texte que l'appel direct) — cela valide le binding d'arguments de bout en bout. → FAIL puis PASS.
- [ ] **Step 2:** Format, suite, commit `feat: expose the nine tools as AIFunctions (GetTools)`.

---

### Task 7: Démo d'intégration `ChatClientAgent` scriptée (zéro réseau)

**Files:**
- Create: `tests/OKF4net.Tests/Agents/ScriptedChatClient.cs`, `tests/OKF4net.Tests/Agents/AgentIntegrationTests.cs`

**Interfaces:**
- `ScriptedChatClient : IChatClient` (test-only) : rejoue une séquence prédéfinie de réponses ; à chaque tour, si le script dit « appelle le tool X avec args Y », il émet un `FunctionCallContent` ; le pipeline function-invoking d'Agent Framework exécute réellement l'`AIFunction` et renvoie le `FunctionResultContent` au client scripté, qui vérifie/consomme le résultat puis passe au tour suivant.

- [ ] **Step 1:** Scénario de bout en bout sur une copie d'appendix_a : (1) l'agent reçoit « Ajoute un concept tables/refunds puis mets à jour les index » ; le script appelle `okf_write_concept` (frontmatter complet), vérifie que le résultat contient `Written`, appelle `okf_regenerate_indexes`, appelle `okf_validate_bundle`, vérifie `conformant`, répond un résumé final. Assertions : le fichier `tables/refunds.md` existe sur disque avec le frontmatter attendu, l'index `tables/index.md` le liste, la réponse finale de l'agent est le texte scripté. Construire l'agent réel : `new ChatClientAgent(scriptedClient, options avec tools = okfTools.GetTools())` (adapter au constructeur/factory exact du package — c'est le SEUL point où l'API du package peut différer de ce plan : l'implémenteur consulte la doc du package installé, https://learn.microsoft.com/agent-framework/agents/tools/function-tools, et adapte en le notant dans son rapport).
- [ ] **Step 2:** FAIL → implémenter `ScriptedChatClient` → PASS. Format, suite complète, commit `test: end-to-end scripted ChatClientAgent integration`.

---

### Task 8: Documentation

**Files:**
- Modify: `README.md` (section « Using OKF4net with Microsoft Agent Framework » : exemple `AsAIAgent(tools: new OkfBundleTools(root).GetTools())`, tableau des 9 tools avec une ligne de description chacun, note sécurité : contenu de bundle non-fiable + tool approval du framework pour les écritures), `CLAUDE.md` si nécessaire (commandes de test du projet Agents).

- [ ] **Step 1:** Rédiger, `dotnet format`, build+suite complète (goldens 5/5), commit `docs: Agent Framework integration guide`.

---

## Self-Review (fait à la rédaction)

- Couverture spec Phase 2 : les 9 tools du spec → Tasks 2–5 ; `GetTools`/AIFunction → Task 6 ; critère de sortie « tools testés unitairement + démo d'intégration ChatClientAgent » → Tasks 2–7 ; tool approval = mécanisme du framework, rien à coder (documenté Task 8).
- Décisions verrouillées ici : sorties markdown agent-friendly ; erreurs attendues = messages textuels, jamais d'exceptions vers le LLM ; cache Bundle avec invalidation après écriture ; refus des ids réservés à l'écriture ; recherche = substring OrdinalIgnoreCase pondérée (YAGNI, extensible plus tard).
- Risque connu : l'API exacte de construction `ChatClientAgent` (Task 7) peut différer selon la version du package — l'implémenteur adapte avec la doc du package installé et documente l'écart éventuel.
- Hors périmètre Phase 2 : `OkfContextProvider` (Phase 3), recherche sémantique, publication NuGet.
