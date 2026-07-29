# Design — OKF §10 Attested Computation dans OKF4net

- **Date** : 2026-07-29
- **Statut** : design validé en brainstorming, en attente de relecture avant plan d'implémentation
- **Spec de référence** : OKF v0.2 — [`GoogleCloudPlatform/knowledge-catalog/okf/SPEC.md`](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md), sections **§10** (+ dépendances **§4.2**, **§6.2**)
- **Contexte amont** : [`2026-07-27-okf-v0.2-upgrade-design.md`](2026-07-27-okf-v0.2-upgrade-design.md) — le bump v0.2 a **différé §10** (décision assumée). Ce document lève ce report.
- **Branche cible** : à créer en worktree, basée sur `dev` **après merge de PR #37** (stratégies de resolver — voir §16).

## 1. Objectif et positionnement

OKF4net implémente aujourd'hui OKF v0.2 **sauf §10** (Attested Computation), différé lors du bump. Ce design implémente **§10 + ses deux dépendances** (§4.2 heading `# Computation`, §6.2 champs *path-valued* en frontmatter) pour atteindre la **conformité v0.2 complète** dans la **0.3.0** (pré-release, pas encore taguée).

**Principe directeur.** OKF4net **parse / valide / navigue** le format et **orchestre** ce qui est *runtime-agnostique* (charger le contrat, valider la forme du receipt, gater sur verdict + `stale_after`) ; il **délègue** ce qui est *runtime-spécifique* (binder les paramètres, exécuter, attester) à des **interfaces branchées par l'hôte**. **OKF4net n'exécute jamais de code** (SQL, Python, dbt…) — la règle zéro-dépendance reste intacte : les interfaces sont de simples contrats BCL.

## 2. Périmètre

### Dans le périmètre (niveau 3 retenu)

- **Couche format (cœur `OKF4net`)** : projections typées du contrat §10.2, accesseur `Computation()` (§10.3 inline/fichier), extension `KnownKeys`.
- **Validation (cœur)** : diagnostics §10 + §6.2, **tous en Warning/Info** (Error reste §11-only).
- **API §6.2 (cœur)** : énumération + résolution path-safe des champs *path-valued*, **distincte** du graphe concept↔concept.
- **Orchestration (nouveau projet zéro-dep `OKF4net.Attestation`)** : interfaces host (`IParameterBinder`, `IComputationExecutor`, `IAttester`), résolution par runtime, orchestrateur `bind → execute → valide receipt → attest → gate`.
- **Surface agent (`OKF4net.Agents`)** : découverte (`okf_get_computation`, enrichissement `okf_read_concept`) **et** exécution (`okf_run_computation` via l'orchestrateur, si l'hôte l'a câblé).

### Hors périmètre (différé, décision assumée)

- **Exécution côté CLI** (décision 10) : le binaire `okf` est Native AOT + zéro-dép ; il ne peut ni charger un executor host ni embarquer un driver de runtime. `okf validate` couvre §10/§6.2 gratuitement ; **aucun verbe d'exécution**.
- **Adaptateurs de runtime concrets** (BigQuery/Postgres/dbt/Python) : territoire de l'hôte, pas d'OKF4net. Un éventuel *sample* est une suite possible, hors de ce lot.
- **Écriture de l'attestation dans le bundle** : §10.6 est explicite — l'attestation est **par-run, non stockée**. L'orchestrateur ne persiste jamais de résultat d'attestation.
- **Lifting du type `Attested Computation` dans `index.md`** : §10.5 le dit *« liftable »* (informatif), non requis. Noté comme extension future possible.

Un bundle contenant un `Attested Computation` **se charge, se valide et se navigue déjà sans erreur** aujourd'hui (le `type` est une string libre, les clés §10 sont des extensions préservées). Ce lot ajoute la **logique dédiée**.

## 3. Journal des décisions (arbitrages validés en brainstorming)

1. **Périmètre = niveau 3** : lecture + validation + navigation + exposition agent + hooks d'orchestration host.
2. **Orchestration = orchestrateur inclus + `IParameterBinder` pluggable** : OKF4net séquence toutes les étapes ; le *bind* (runtime-spécifique) est délégué à une interface host mais **appelé** par l'orchestrateur.
3. **Packaging** : couche format au **cœur** ; orchestration dans un **nouveau projet zéro-dép `OKF4net.Attestation`** (référence `OKF4net` seul, miroir de la séparation `Catalog`) ; surface agent dans `OKF4net.Agents`.
4. **§6.2 = validation broken-path + API distincte** (`FrontmatterResources()`), **hors** graphe concept (`okf graph` inchangé, golden intact).
5. **Agent = découverte + exécution** : `okf_get_computation` (+ enrichissement `okf_read_concept`) toujours présents ; `okf_run_computation` présent **uniquement si** un orchestrateur est câblé.
6. **Extraction §10.3 = premier bloc de code fencé (` ``` `/`~~~`) sous `# Computation`** — on suit **le texte de la spec** (« a single fenced code block »), pas l'exemple indenté.
7. **Fichier `computation:` = résolution path-safe + lecture UTF-8 stricte** → texte de computation **uniforme** fourni au binder, que la source soit inline ou fichier.
8. **`IAttester` reçoit un `AttestationContext` complet** (contract + computation sanctionnée + bound + valeurs + receipt) pour pouvoir honorer §10.5(a)(b) : vérifier que le run correspond à la computation sanctionnée bindée, et que la valeur vient de la source autoritative.
9. **`runtime` REQUIRED → Warning** (pas Error). Error reste **strictement §11** ; §11/§5.3 : *« consumers MUST NOT reject »*.
10. **CLI = option a** : aucun verbe d'exécution ; `okf validate` couvre §10/§6.2.

## 4. Conformité à la spec (§10 / §6.2 / §4.2 → design)

| Exigence spec | Design | Conforme |
|---|---|---|
| §10.1 — concept autonome (`type: Attested Computation`), lié par markdown | `IsAttestedComputation` ; liens body = §6.1 déjà géré (`LinkScanner`) | ✅ |
| §10.2 — `runtime` **REQUIRED** | projeté `Runtime` ; **Warning** si absent (décision 9 ; jamais de rejet) | ✅ (soft, cohérent v0.2) |
| §10.2 — `parameters` `{name,type,required}`, `computation`, `executor{resource,receipt}`, `attester{resource}` | value types (§6) ; clés ajoutées à `KnownKeys` | ✅ |
| §10.3 — inline fence **XOR** fichier ; l'agent fournit **seulement des valeurs**, ne DOIT PAS écrire la computation | `Computation()` inline/fichier ; `okf_run_computation(conceptId, values)` **sans** paramètre de computation | ✅ |
| §10.4 — chaque computation atteste indépendamment | orchestration par concept-computation | ✅ |
| §10.5 — discover → load → parameterize → execute → attest → gate | orchestrateur : load → bind → execute → **valide forme receipt** → attest → gate | ✅ |
| §10.5(a)(b) — l'attester vérifie que le run = computation sanctionnée bindée (pas du code inventé) et que la valeur vient de la source autoritative | `IAttester` reçoit `AttestationContext` complet (décision 8) | ✅ |
| §10.6 — `verified` (doc, dans le bundle) ≠ attestation (par-run, hors bundle) | `verified`/§5.2 inchangé ; orchestrateur **n'écrit jamais** l'attestation | ✅ |
| §6.2 — 5 champs path-valued ; URL / bundle-relatif (`/…`) / relatif | `FrontmatterResources()` + résolution path-safe + validation broken-path (URLs ignorées) | ✅ |
| §4.2 — heading `# Computation` | reconnu comme emplacement de la computation sanctionnée | ✅ |
| §11 — conformance : `type` + parseable + fichiers réservés | inchangé ; **tout §10/§6.2 est Warning/Info** | ✅ |

## 5. Architecture — graphe de projets

Ajout d'**un seul projet**, `OKF4net.Attestation`, strictement zéro-dépendance (référence `OKF4net` seul), miroir de la séparation `Catalog` :

```
OKF4net                     (cœur, format §1–§13 ; +§10 couche format, +§6.2 résolution)
  ├── OKF4net.Cli           (okf ; validate couvre §10/§6.2 ; pas d'exécution)
  ├── OKF4net.Attestation   (NOUVEAU, zéro-dep : interfaces + orchestrateur §10.5)
  │     └── référencé par OKF4net.Agents
  ├── OKF4net.Agents        (+ ref projet → OKF4net.Attestation ; okf_get/run_computation)
  ├── OKF4net.Catalog
  │     └── OKF4net.Catalog.Hosting
  └── OKF4net.Mcp
```

`OKF4net.Agents` gagne **une référence de projet** vers `OKF4net.Attestation`. La règle « Agents ne référence exclusivement que `Microsoft.Agents.AI` » vise les **PackageReference** tierces ; une **ProjectReference** first-party (Agents référence déjà `OKF4net`) est autorisée. `OKF4net.Attestation` reste zéro-`PackageReference`.

## 6. Cœur `OKF4net` — couche format (§10.2 / §10.3)

Nouveaux value types (`readonly record struct` sauf indication, BCL-only, **projections paresseuses jamais throw** — miroir exact des types §5) :

```csharp
// §10.2
public readonly record struct ComputationParameter(string Name, string? Type, bool Required);
public readonly record struct Executor(string? Resource, IReadOnlyList<string> Receipt);   // executor.resource + receipt[]
public readonly record struct Attester(string? Resource);
public readonly record struct AttestedComputationContract(
    string? Runtime,                                  // §10.2 REQUIRED ; null → Warning validateur
    IReadOnlyList<ComputationParameter> Parameters,   // [] si absent
    string? ComputationPath,                          // frontmatter `computation` ; null ⇒ computation inline
    Executor? Executor,
    Attester? Attester);

// §10.3 — la computation sanctionnée résolue
public enum ComputationSource { Inline, File }
public readonly record struct SanctionedComputation(
    ComputationSource Source,
    string? InlineCode,       // texte du bloc fencé (fences retirées) quand Source=Inline
    string? Path);            // valeur brute de `computation:` quand Source=File
```

**Accès via `Frontmatter`** (comme `.Generated`, `.Sources`, `.Lifecycle`) :

```csharp
public bool IsAttestedComputation => string.Equals(Type, "Attested Computation", StringComparison.Ordinal);
public AttestedComputationContract ComputationContract { get; }   // projection paresseuse, tolérante
```

`KnownKeys` étendu de `runtime, parameters, computation, executor, attester` (§10) — ces clés cessent d'être des extensions.

**Parsing YAML — aucune extension nécessaire (vérifié sur `YamlParser`).** Le sous-ensemble YAML (parseur récursif descendant) gère déjà les formes §10 : un **map imbriqué contenant une liste** (`executor: { resource: …, receipt: [ … ] }`, en bloc comme en flow) et une **liste de maps** (`parameters: [ { name: …, type: …, required: … } ]`, items bloc ou flow). Ces cas empruntent exactement les chemins déjà exercés par `sources`/`verified`/`usage_window` (§5, testés au Plan 1) : `ParseMappingCore → ParseNested` pour la valeur imbriquée, `ParseInlineValue → FlowParser` pour la flow-list `receipt`, `ParseSequence` pour la liste de maps. **Aucune modification du parseur/emitter n'est requise** ; le plan n'ajoute que des tests de round-trip sur les formes §10.

**Extraction de la computation (§10.3 + §4.2)** — `OkfDocument.Computation()` → `SanctionedComputation` :

- si `ComputationContract.ComputationPath` est non nul → `Source=File, Path=…` (le contenu est résolu/lu **par l'orchestrateur**, pas ici — voir §9) ;
- sinon → extraction du **premier bloc de code fencé** sous le heading ATX H1 `# Computation` :
  - repérage du heading : première ligne dont le contenu *trimmé* vaut exactement `# Computation` (H1, texte `Computation`, sensible à la casse) ;
  - après ce heading, **premier bloc fencé** ouvert par ` ``` ` **ou** `~~~` (info-string/langage ignoré), capturé jusqu'à la fence de fermeture correspondante ; le texte renvoyé **exclut** les lignes de fence ;
  - aucun heading, ou heading sans bloc fencé ⇒ `InlineCode=null` (et le validateur avertit s'il n'y a pas non plus de `computation:` — voir §7).
- Implémentation dans un `Internal/ComputationExtractor` (précédent : `LinkScanner`, extraction des citations ; utilise `Internal/LfLines`).

**Distinction** : `Computation()` **ne lit pas** de fichier et **ne throw jamais** ; il ne fait qu'exposer *quelle* est la source. La résolution+lecture path-safe du cas fichier est une préoccupation d'orchestration (§9), qui réutilise la primitive de résolution §6.2 du cœur (§8).

## 7. Cœur `OKF4net` — validation (§10 + §6.2)

Nouveaux diagnostics `BundleValidator.Validate(Bundle, IOkfClock?)`. **Sévérité `Error` inchangée (§11 uniquement)** ; tout ce qui suit est **Warning** (sauf mention) et n'invalide jamais le bundle.

| Déclencheur (sur un concept `type: Attested Computation`, sauf §6.2) | Sévérité | Message (esprit) |
|---|---|---|
| `runtime` absent/vide | Warning | attested computation missing required `runtime` |
| `parameters[]` entrée mal formée (pas un mapping, ou `name` absent/vide) | Warning | parameter entry missing `name` |
| ni fence `# Computation` ni `computation:` | Warning | attested computation has no computation (inline `# Computation` or `computation:` path) |
| fence `# Computation` **et** `computation:` présents ensemble | Warning | computation specified both inline and via `computation:` — keep one |
| `executor.receipt` présent mais non-liste | Warning | executor.receipt must be a list of field names |
| `attester.resource` présent mais vide | Warning | attester.resource is empty |
| **§6.2** — champ path-valued bundle-relatif/relatif dont le fichier **n'existe pas** | Warning | frontmatter path `<field>` → `<path>` not found |
| **§6.2** — champ path-valued dont le chemin **s'échappe** de la racine du bundle ou traverse un reparse point | Warning | frontmatter path `<field>` → `<path>` escapes the bundle |

Champs §6.2 contrôlés : `resource`, `sources[].resource`, `computation`, `executor.resource`, `attester.resource`. **Les URLs (schéma `xxx://`) sont ignorées** (pas de résolution disque). Résolution : bundle-relatif (`/…`) depuis la racine du bundle ; relatif depuis le **répertoire du concept**. Contrôles de sécurité (containment + reparse) via la primitive §6.2 (§8), qui réutilise `Internal/ReparsePoints`.

**Rationale décision 9** : `runtime` est *REQUIRED* au sens §10.2 (forme d'un `Attested Computation` bien constitué), mais la **validité du bundle** (§11) n'exige que `type`. Émettre un `Error` rejetterait le bundle, ce que §11/§5.3 interdisent explicitement aux consommateurs. On reste donc en Warning, cohérent avec le traitement de tous les champs optionnels §5.

## 8. Cœur `OKF4net` — API §6.2 (résolution + énumération)

Primitive de résolution *path-safe* partagée par le validateur (§7) **et** l'orchestrateur (§9), pour ne pas dupliquer la logique de sécurité :

```csharp
public enum FrontmatterResourceKind { Url, BundleRelative, Relative }
public enum ResourceResolutionStatus { Url, Resolved, Missing, Unsafe }
public readonly record struct FrontmatterResource(string Field, string RawPath, FrontmatterResourceKind Kind);

// Énumération (distincte du graphe concept↔concept) :
//   parcourt resource, sources[].resource, computation, executor.resource, attester.resource
public IReadOnlyList<FrontmatterResource> OkfDocument.FrontmatterResources();

// Résolution + sécurité (containment racine bundle + rejet reparse via ReparsePoints) :
public bool Bundle.TryResolveResource(
    Concept concept, string rawPath,
    out string? absolutePath, out ResourceResolutionStatus status);
//   Url      → absolutePath=null (externe, non résolu)
//   Resolved → absolutePath sûr et le fichier existe
//   Missing  → chemin sûr mais fichier absent  (→ Warning validateur)
//   Unsafe   → échappe la racine / reparse       (→ Warning validateur)

// Lecture UTF-8 stricte d'une ressource déjà résolue (Resolved), encodage centralisé (OkfEncodings.Strict) :
public string Bundle.ReadResourceText(string absolutePath);
```

Classification `FrontmatterResourceKind` : `Url` si le chemin matche `^[a-zA-Z][a-zA-Z0-9+.-]*://` ; `BundleRelative` si commence par `/` ; sinon `Relative`. **Ces arêtes ne sont PAS injectées dans `okf graph`** (qui reste = liens body concept↔concept) → aucun impact sur le golden `graph.dot`.

## 9. `OKF4net.Attestation` (nouveau projet, zéro-dép)

### 9.1 Contrats host (interfaces BCL, asynchrones)

```csharp
public interface IParameterBinder
{
    // Binde les valeurs dans la computation sanctionnée pour un runtime donné → artefact exécutable opaque.
    ValueTask<BoundComputation> BindAsync(
        AttestedComputationContract contract,
        SanctionedComputation computation,          // InlineCode OU (pour File) texte déjà lu, voir 9.3
        IReadOnlyDictionary<string, object?> values,
        CancellationToken cancellationToken = default);
}

public interface IComputationExecutor
{
    // Exécute l'artefact bindé → receipt façonné par executor.receipt.
    ValueTask<Receipt> ExecuteAsync(
        BoundComputation bound,
        AttestedComputationContract contract,
        CancellationToken cancellationToken = default);
}

public interface IAttester
{
    // Vérification déterministe (sans LLM) sur le receipt → verdict.  Reçoit TOUT le contexte (décision 8).
    ValueTask<AttestationVerdict> AttestAsync(
        AttestationContext context,
        CancellationToken cancellationToken = default);
}

// Résolution par runtime : l'hôte enregistre un triplet par nom de runtime (bigquery, python, Looker…).
public interface IAttestationRuntime
{
    IParameterBinder Binder { get; }
    IComputationExecutor Executor { get; }
    IAttester Attester { get; }
}
public interface IAttestationRuntimeRegistry
{
    bool TryGet(string runtime, out IAttestationRuntime? found);   // correspondance exacte du nom
}
```

### 9.2 Value types

```csharp
// Artefact bindé, opaque pour OKF4net (le binder le produit, l'executor le consomme — même runtime host).
public sealed record BoundComputation(
    string Runtime,
    string? BoundText,                                  // computation avec valeurs bindées, si textuelle (ex. SQL) — support de §10.5(a)
    object? Payload,                                    // porteur runtime-spécifique optionnel
    IReadOnlyDictionary<string, object?> Values);

// Preuve d'un run, façonnée par executor.receipt.
public sealed record Receipt(IReadOnlyDictionary<string, object?> Fields);

public readonly record struct AttestationVerdict(bool Passed, string? Detail);

// Contexte complet remis à l'attester (décision 8, §10.5(a)(b)).
public sealed record AttestationContext(
    AttestedComputationContract Contract,
    SanctionedComputation Computation,
    BoundComputation Bound,
    IReadOnlyDictionary<string, object?> Values,
    Receipt Receipt);

public enum StaleState { Fresh, Stale, Unknown }

// Résultat gaté, jamais throw pour un échec attendu (errors-as-data).
public sealed record AttestationOutcome(
    bool Displayable,                                  // = ReceiptShapeOk && Verdict.Passed && StaleAdmitted
    AttestationVerdict? Verdict,
    Receipt? Receipt,
    bool ReceiptShapeOk,
    StaleState Stale,
    IReadOnlyList<string> Reasons,                     // explique tout non-Displayable
    Exception? Error);                                 // exception binder/executor/attester capturée, le cas échéant
```

### 9.3 Orchestrateur

```csharp
public sealed class AttestationOrchestrator
{
    public AttestationOrchestrator(
        IAttestationRuntimeRegistry runtimes,
        IOkfClock? clock = null,                        // défaut SystemClock (cœur)
        StalePolicy? defaultPolicy = null);             // défaut StalePolicy.Use (cœur)

    public ValueTask<AttestationOutcome> RunAsync(
        Bundle bundle, ConceptId conceptId,
        IReadOnlyDictionary<string, object?> parameterValues,
        StalePolicy? policy = null,
        CancellationToken cancellationToken = default);
}
```

**Séquence de `RunAsync`** (chaque échec attendu → `AttestationOutcome` `Displayable=false` + `Reasons`, **jamais** d'exception ; les exceptions du binder/executor/attester sont **capturées** dans `Error` + `Reasons`) :

1. **Charge** le concept depuis `bundle` ; si introuvable ou `!IsAttestedComputation` → outcome d'erreur.
2. **Résout la computation** via `OkfDocument.Computation()` : `Inline` → `InlineCode` ; `File` → `bundle.TryResolveResource(concept, path)` puis `ReadResourceText` (path-safe, §8) ; statut `Missing`/`Unsafe`/absent → outcome d'erreur.
3. **Résout le runtime** : `runtimes.TryGet(contract.Runtime)` ; absent/non enregistré → outcome `no runtime configured for '<runtime>'`.
4. **Valide les paramètres** fournis contre `contract.Parameters` : chaque `Required` doit être présent → sinon outcome `missing required parameter '<name>'` ; les valeurs surnuméraires sont ignorées (l'agent ne peut fournir que des valeurs, §10.3).
5. **Bind** : `runtime.Binder.BindAsync(contract, computation, values)` → `BoundComputation`.
6. **Execute** : `runtime.Executor.ExecuteAsync(bound, contract)` → `Receipt`.
7. **Valide la forme du receipt** : tous les noms de `contract.Executor.Receipt` présents dans `Receipt.Fields` → `ReceiptShapeOk` ; sinon `false` + raison (et on **n'atteste pas**).
8. **Attest** : `runtime.Attester.AttestAsync(AttestationContext{…})` → `AttestationVerdict`.
9. **Gate** : `StaleState` calculé depuis `frontmatter.Lifecycle` + `clock.Today` ; `staleAdmitted = (policy ?? defaultPolicy).Admits(lifecycle, today)` ; `Displayable = ReceiptShapeOk && Verdict.Passed && staleAdmitted`.
10. **Retourne** `AttestationOutcome` avec verdict, receipt, état stale, et `Reasons` détaillant tout blocage.

Le *gating* réutilise `IOkfClock`/`StalePolicy` **du cœur** (déjà partagés). L'orchestrateur **n'écrit rien** dans le bundle (§10.6).

## 10. `OKF4net.Agents` — surface agent

- **`okf_read_concept`** : quand `IsAttestedComputation`, ajoute au rendu un résumé compact du contrat (runtime, paramètres, source de la computation inline/fichier, executor/attester resources).
- **`okf_get_computation(conceptId)`** *(toujours présent, lecture seule)* : renvoie le contrat complet **et** la computation résolue (code inline, ou texte du fichier résolu path-safe) en markdown structuré. Pattern `RunTool` *never-throw*.
- **`okf_run_computation(conceptId, parameterValues)`** *(présent uniquement si un orchestrateur est câblé)* : invoque `AttestationOrchestrator.RunAsync` et rend l'`AttestationOutcome` (Displayable, verdict, résumé du receipt, raisons). Si aucun orchestrateur n'est câblé, le tool **n'est pas exposé** (plutôt qu'un tool mort). `parameterValues` = objet JSON (mapping) matérialisé par `AIFunctionFactory`. **Aucun paramètre de computation** dans la signature (§10.3 : l'agent ne fournit que des valeurs).

**Câblage** : `OkfBundleTools` reçoit un `AttestationOrchestrator?` optionnel (nouvelle surcharge de constructeur ; `null` par défaut → `okf_run_computation` non exposé). `GetTools()` renvoie donc **10 tools** (les 9 actuels + `okf_get_computation`) ou **11** (+ `okf_run_computation`) selon le câblage. Les tests de schéma des tools et le `Mcp` sont mis à jour en conséquence.

## 11. CLI (`OKF4net.Cli`) — option a

- `okf validate` couvre §10 + §6.2 **automatiquement** (via `BundleValidator`) — pas d'exécution, AOT + zéro-dép intacts.
- **Aucun nouveau verbe.** `info`/`parse`/`graph`/`fmt` inchangés ; `graph` inchangé (§6.2 hors graphe).
- Goldens existants **inchangés** ; **un nouveau golden** `validate` sur la fixture §10 (voir §12).

## 12. Plan de test + fixtures

**Nouvelle fixture** `tests/fixtures/okf_v02_computation/` (aucune fixture existante touchée) :

- `computations/revenue.md` — Attested Computation **inline** : `# Computation` fencé, `runtime: bigquery`, `parameters: [{name: year, type: integer, required: true}]`, `executor: {resource: references/skills/run-on-bq.md, receipt: [job_id, executed_sql, result]}`, `attester: {resource: references/attesters/revenue.py}`, + `generated`/`verified`/`stale_after`/`sources`.
- `computations/revenue-file.md` — variante **fichier** : `computation: references/computations/revenue.sql`, pas de fence.
- `references/computations/revenue.sql`, `references/skills/run-on-bq.md`, `references/attesters/revenue.py` — cibles path-valued **existantes**.
- `metrics/revenue.md` — `Metric` liant la computation par markdown (§10.4).
- Cas mal formés (pour les Warnings) : un `Attested Computation` **sans `runtime`** ; un avec **fence + `computation:`** ; un `executor.resource` **cassé** ; une entrée `parameters` **sans `name`**.

**Tests**

- **Cœur (format)** : projection `ComputationContract` (tous champs + absents) ; `Computation()` inline vs fichier ; `ComputationExtractor` (heading présent/absent, fence ` ``` `/`~~~`, info-string ignorée, pas de fence) ; `FrontmatterResources()` (classification URL/bundle-relatif/relatif) ; `Bundle.TryResolveResource` (Resolved/Missing/Unsafe/Url).
- **Cœur (validation)** : chaque Warning §10 + §6.2 sous `FixedClock` ; **`Error` toujours §11-only** (un `Attested Computation` mal formé reste conformant, exit 0).
- **Attestation** : orchestrateur *happy path* avec runtime **fake** in-memory (binder/executor/attester) ; échec de forme de receipt → non-Displayable ; verdict d'attestation négatif → non-Displayable ; stale gaté sous `FixedClock` × `StalePolicy` (Use/Strict/Tolerate) ; runtime absent ; paramètre requis manquant ; executor qui **throw** → `Error` capturé, non-Displayable ; résolution par runtime ; computation fichier résolue+lue.
- **Agents** : `okf_get_computation` (inline + fichier) ; enrichissement `okf_read_concept` ; `okf_run_computation` avec orchestrateur fake (Displayable + raisons) ; **absence** d'orchestrateur → tool non exposé.
- **CLI/Golden** : nouveau golden `validate` sur `okf_v02_computation` (oracle v0.2 maison, relu ligne à ligne) ; goldens existants inchangés ; `CliTests`.
- **Helpers test-only** : `FixedClock` (déjà présent), runtime/binder/executor/attester *fakes*.

## 13. Packaging / versionnage / docs

- **Nouveau projet** `src/OKF4net.Attestation/` (+ `tests/OKF4net.Tests/Attestation/`), ajouté à `OKF4net.sln`.
- **Package NuGet** `OKF4net.Attestation` **0.3.0** : `PackageId`/`Description`/licence `LGPL-3.0-or-later`, zéro `PackageReference`, ajouté à la liste de pack de `release.yml`.
- `OKF4net.Agents.csproj` : **ProjectReference → `OKF4net.Attestation`**.
- **CHANGELOG** : entrée sous **`[Unreleased]`** « Attested Computation (§10) » — la section `[0.3.0]` est figée, le travail parallèle (PR #37) accumule déjà sous `[Unreleased]` ; tout sera plié dans la 0.3.0 au moment de la release.
- **README** : table *spec-section → type* étendue (§10/§6.2/§4.2 + nouveau package) + court snippet d'usage `AttestationOrchestrator`.
- **CLAUDE.md** : règle zéro-dép mise à jour (`OKF4net.Attestation` = zéro-dépendance, référence `OKF4net` seul) + entrée dans la section Architecture. *(Nit connexe repéré, hors périmètre : la section Architecture de CLAUDE.md cite encore des numéros de section v0.1 pour Links/Index/Log/Validate — à corriger dans une passe doc séparée.)*
- **Version reste `0.3.0`** (pré-release ; ce lot atterrit avant le tag).

## 14. Contraintes projet respectées

- **Zéro-dépendance** : cœur + `OKF4net.Attestation` BCL-only ; les interfaces host sont de purs contrats ; Agents ne gagne qu'une ProjectReference first-party.
- **Chargement permissif §3** : les projections §10 ne throwent jamais ; l'orchestrateur est *errors-as-data* (échecs → `AttestationOutcome`, exceptions capturées).
- **Sécurité chemins** : la résolution §6.2 réutilise `ReparsePoints` (containment + rejet reparse) ; aucune lecture de fichier hors du bundle.
- **SPDX header**, file-scoped namespaces, nullable, XML doc sur l'API publique, `dotnet format` clean, warnings = erreurs.

## 15. Hors périmètre / suites possibles

- Adaptateurs de runtime concrets (BigQuery/Postgres/dbt/Python) — sample host éventuel.
- Lifting du type `Attested Computation` dans `index.md` (informatif §10.5).
- Persistance d'un journal d'attestation (délibérément **exclu** par §10.6).
- Exécution CLI (délibérément exclue, décision 10).

## 16. Coordination inter-sessions

- **Base = `dev` après merge de PR #37** (stratégies de resolver ; `DefaultKnowledgeResolver` → `GroupedKnowledgeResolver`, 704 tests, section CHANGELOG `[Unreleased]`).
- §10 est **orthogonal** au resolver : le *gating* utilise `StalePolicy`/`IOkfClock` **du cœur**, jamais le resolver. **Aucun couplage**, aucune référence à `DefaultKnowledgeResolver` dans ce design.
- Le worktree/branche §10 sera créé une fois #37 mergé, pour éviter tout empilement ; l'entrée CHANGELOG va sous `[Unreleased]`.
