# Design — Producer OKF natif (`producers/OkfProducer`)

- **Date** : 2026-07-31
- **Statut** : design validé en brainstorming, prêt pour plan d'implémentation
- **Contexte amont** : évaluation de `tommypacker/okf-generator` (TS, prior art) comme point de départ possible — écarté (v0.1 seulement, immature, hors écosystème C#) mais gardé comme référence de design pour la surface de commandes et les heuristiques de package-scope (aucun code ni mention créditée, simple inspiration générale — voir §7). Le producer natif construit à la place sur l'API OKF4net elle-même.
- **Dépendance (levée)** : ce producer référence quatre membres ajoutés à `src/OKF4net` par le lot « producer-ergonomics API » (`Provenance.ToYaml`, `ConceptId.Slugify`, la surcharge `BundleConceptWriter.WriteConcept(string, Frontmatter, string)`, `OkfDocumentBuilder` — voir `docs/superpowers/specs/2026-07-31-okf4net-producer-ergonomics-api-design.md`). Ce lot est mergé dans `dev` (commit `fa38494`, 903/903 tests) — plus de blocage pour démarrer le plan d'implémentation de ce producer.

## 1. Objectif

Fournir un outil qui scanne un repo git et génère un bundle OKF v0.2 le décrivant (vue d'ensemble, architecture, packages, workflows, docs), en s'appuyant sur la bibliothèque `OKF4net` plutôt qu'en réimplémentant l'écriture/la validation de bundle. Solution séparée (`producers/`), hors `OKF4net.sln`/CI, avec ses propres conventions .NET standard (pas un calque de la structure TypeScript d'okf-generator).

## 2. Solution & organisation des projets

```
producers/
  OkfProducer.sln
  Directory.Build.props        # importe le Directory.Build.props racine (Nullable/TreatWarningsAsErrors/LangVersion),
                                # override <Version> avec un cycle de version indépendant (0.1.0 pour démarrer)
  src/
    OkfProducer.Core/
      Scanning/                # parcours du repo, détection packages/docs/CI/tests, résolution package-scope
      Generation/               # RepositoryModel -> OkfDocument (via OkfDocumentBuilder), écriture via BundleConceptWriter
      Enrichment/                # ILlmEnricher + implémentation Microsoft.Extensions.AI, LlmResponseCache
      Configuration/             # POCOs bindés via le pattern Options (CLI args / fichier config / variables d'env)
      Validation/                 # wrapper fin autour de OKF4net.BundleValidator
    OkfProducer.Cli/
      Program.cs                  # bootstrap Generic Host + DI
      Commands/                    # définitions de commandes (generate, validate)
  tests/
    OkfProducer.Tests/             # xunit, hors OKF4net.sln/CI
```

Choix « standard .NET » plutôt que portage 1:1 de la structure TypeScript source :
- **`System.CommandLine`** pour le parsing d'arguments (package Microsoft officiel), plutôt que l'arg-parser fait main qu'utilise `OKF4net.Cli` par contrainte zero-dependency — contrainte qui ne s'applique pas ici.
- **`Microsoft.Extensions.Hosting` + `Microsoft.Extensions.DependencyInjection`** comme composition root.
- **Pattern Options** (`IOptions<GenerateOptions>`) pour la config.
- **`Microsoft.Extensions.Http`** (`IHttpClientFactory`) pour le client HTTP sous-jacent à l'enrichissement LLM.
- Dossiers organisés par responsabilité (Scanning/Generation/Enrichment/Configuration/Validation), pas par nom de fichier source TS.
- Mêmes conventions de style que le reste du repo (namespaces file-scoped, nullable, XML doc sur l'API publique) héritées du `Directory.Build.props` racine.

`OkfProducer.Core` référence `OKF4net` (ProjectReference) pour `Frontmatter`/`OkfDocument`/`OkfDocumentBuilder`/`BundleConceptWriter`/`ConceptId`/`BundleValidator`. `OkfProducer.Cli` référence `OkfProducer.Core` + `System.CommandLine` + `Microsoft.Extensions.Hosting` + `Microsoft.Extensions.AI` (+ son connecteur OpenAI-compatible). Contrairement à `src/OKF4net`, ces projets **peuvent** avoir des dépendances NuGet — seule la bibliothèque cœur reste zero-dependency.

## 3. Flux de données & modèle de domaine

**Pipeline** (orchestré par le handler de la commande `generate`, services résolus par DI) :

1. `IRepositoryScanner.ScanAsync(repoPath, ScanOptions)` → `RepositorySnapshot` (info repo, manifests de packages par écosystème npm/NuGet/Cargo/go.mod/pyproject, docs, workflows CI, suites de tests, fichiers de config). `IPackageScopeResolver` applique le filtre `--package-scope primary/workspaces/all`.
2. `IConceptGenerator.GenerateAsync(snapshot, mode)` → `IReadOnlyList<OkfDocument>`, construits via `OkfDocumentBuilder` (§3.4 du lot ergonomie). En mode `scan`, un `NullLlmEnricher` no-op ; en `quick`/`explore`, appel à `ILlmEnricher` par concept (voir §4).
3. Avant toute écriture, l'orchestrateur `generate` applique la précondition `--update`/`--reset`/`--force` sur `--out` (§4) — `WriteConcept` lui-même ignore ces flags, il écrit/écrase un concept donné sans connaître la politique de dossier. Une fois la précondition validée, chaque document est écrit via `BundleConceptWriter.WriteConcept(string, Frontmatter, string)`.
4. Une fois les concepts écrits, appel à l'`IndexGenerator` existant d'OKF4net pour produire `index.md` — pas de générateur d'index réinventé. Le tampon `generated.at` par concept est géré nativement par `BundleConceptWriter`.

**Concepts générés pour la v1** : vue d'ensemble du repo, vue d'ensemble d'architecture, un concept par package/workspace détecté, concepts CLI (bin/executables des manifests), workflows dev/test/release, concepts docs/config/CI/tests. L'id de chaque concept (hors les deux vues d'ensemble, à id fixe `overview`/`architecture`) est dérivé de son nom naturel (nom de package, titre de doc, nom de workflow) via `ConceptId.Slugify` — c'est la raison d'être directe de ce helper (voir le lot ergonomie, §3.2). Deux slugs identiques (ex. deux packages de noms différents slugifiant vers le même segment) sont désambiguïsés par le générateur en suffixant un compteur (`-2`, `-3`, ...) — la dédup reste, comme documenté, la responsabilité de l'appelant de `Slugify`, pas de `Slugify` lui-même.

**Réutilisation de l'`IndexGenerator` existant** : après l'écriture des concepts, appel à `IndexGenerator.RegenerateIndexes(string bundleRoot) -> IReadOnlyList<string>` (signature vérifiée dans `src/OKF4net/IndexGenerator.cs`) — pas un appel à une méthode `Generate`/`Build` hypothétique.

**Commandes v1** : `generate` (scan → enrichissement optionnel → écriture bundle v0.2 conforme) et `validate` (réutilise `BundleValidator` d'OKF4net). Pas de `diff`/`explain`/`init` en v1.

**Surface de flags `generate`** (pour ne rien laisser d'implicite au moment du plan) :

| Flag | Type | Défaut | Rôle |
|---|---|---|---|
| `--repo <path>` | string, requis | — | Racine du repo à scanner |
| `--out <path>` | string, requis | — | Racine du bundle OKF de sortie |
| `--update` | flag | absent | Autorise l'écriture dans un `--out` non vide, en préservant les fichiers non générés |
| `--reset` | flag | absent | Supprime puis recrée `--out` avant écriture |
| `--force` | flag | absent | Alias de `--reset` (parité avec okf-generator, pas de sémantique propre) |
| `--package-scope <primary\|workspaces\|all>` | enum | `primary` | Portée de détection des packages (§3, `IPackageScopeResolver`) |
| `--mode <scan\|quick\|explore>` | enum | `scan` | Niveau d'enrichissement LLM (§4) |
| `--llm-model <name>` | string | — | Modèle passé au connecteur `Microsoft.Extensions.AI`, requis si `--mode` ≠ `scan` |
| `--llm-base-url <url>` | string | endpoint OpenAI par défaut | Endpoint OpenAI-compatible (DeepSeek etc.) |
| `--cache-dir <path>` | string | `<repo>/.okf-producer-cache` | Racine du cache-bundle LLM (§4) |
| `--no-cache` | flag | absent | Désactive le cache LLM |
| `--quiet` | flag | absent | Supprime les messages de progression sur stderr |

**Surface de flags `validate`** : `--okf <path>` (requis, racine du bundle à valider) uniquement pour la v1 — pas de `--check`/mode CI dédié (différent du `diff --check` d'okf-generator, hors scope ici puisque `diff` lui-même est hors scope).

## 4. Enrichissement LLM, cache, gestion d'erreurs

**Enrichissement** (`Microsoft.Extensions.AI`) : `ILlmEnricher.EnrichAsync(EvidenceBundle, CancellationToken) -> EnrichmentResult`, implémenté via l'abstraction `IChatClient` de `Microsoft.Extensions.AI` (connecteur OpenAI-compatible, base URL configurable — DeepSeek etc. comme tout endpoint compatible). Mode `scan` (défaut) → `NullLlmEnricher` no-op, 100% offline/déterministe ; `quick`/`explore` → appel réel. **Une tentative de retry** sur erreur transitoire (timeout/5xx), puis dégradation vers le contenu non-enrichi pour ce concept + warning stderr — jamais d'exception remontée à l'appelant.

**Cache LLM** : mini-bundle OKF interne (pas le bundle de sortie livré à l'utilisateur), à `.okf-producer-cache/` par défaut à la racine du repo scanné (overridable `--cache-dir`, désactivable `--no-cache`). Clé = `SHA256($"{prompt.Length}:{prompt}{model.Length}:{model}{baseUrl.Length}:{baseUrl}")` en hexadécimal minuscule — préfixer chaque composant par sa longueur rend la concaténation sans ambiguïté (un simple séparateur littéral, ex. un espace, ne suffit pas : `prompt="a b", model="c"` donnerait la même chaîne concaténée que `prompt="a", model="b c"`). Un hex digest est déjà un segment `ConceptId` valide (charset ASCII), pas besoin de `Slugify`. Id de concept = le hash directement comme seul segment, à la racine du cache-bundle (`ConceptId.Parse(hash)`, pas de sous-répertoire de préfixe façon `objects/ab/cdef...` — le volume attendu par run ne justifie pas cette optimisation en v1). Écriture d'une entrée via `BundleConceptWriter.WriteConcept` (`type: "LLM Cache Entry"`, `title`/`description` minimaux pour satisfaire `OkfDocument.Validate()`, `body` = réponse brute) — réutilise l'atomicité/le verrouillage déjà éprouvés plutôt que de réinventer un format de cache fichier. Lecture : pas besoin de charger tout le bundle (`Bundle.Load`) — le path exact est connu via `ConceptId.ToPath`, un `File.Exists`/`OkfDocument.Parse` direct suffit pour un hit. Contourner le pipeline de garde anti-reparse-point de `BundleConceptWriter` en lecture est sûr ici : le hash qui compose le path est calculé par le producer lui-même (jamais dérivé d'une entrée utilisateur/repo-scanné non fiable), donc aucune des menaces que ce pipeline défend (concept id hostile visant une évasion du bundle root) ne s'applique à ce chemin de lecture interne.

**Gestion d'erreurs**, quatre cas distincts (pas trois — la précondition globale et un échec d'écriture par concept sont deux choses différentes) :
- **Scan** (fichier illisible, manifeste malformé) : permissif, diagnostics collectés, n'interrompt pas la génération (même philosophie que `Bundle.ParseErrors`).
- **LLM** : dégradation par concept après un retry (voir ci-dessus), jamais fatal pour la commande entière.
- **Précondition d'écriture globale** (dossier de sortie non vide sans `--update`/`--reset`) : vérifiée une fois avant tout écriture — seul cas vraiment fatal pour toute la commande, message clair + exit non-zero, rien n'est écrit.
- **Échec d'écriture d'un concept individuel** (ex. permission refusée sur un fichier précis, `WriteConcept` retourne une string `"Error: ..."` plutôt que de lever une exception, cf. le contrat documenté de `BundleConceptWriter`) : **non fatal pour la commande entière** — même philosophie permissive que le scan. Le producer collecte l'erreur, continue avec les concepts suivants, et le rapport final de `generate` liste les concepts en échec ; le code de sortie reflète leur présence (non-zero si au moins un concept a échoué à s'écrire) sans avoir avorté prématurément le reste de la génération.

## 5. Tests

- `IRepositoryScanner` : fixtures de répertoires temporaires (pas de golden byte-exact requis — ce n'est pas `OKF4net.Cli`).
- `IConceptGenerator` : sur un `RepositorySnapshot` construit à la main → assertions sur les `OkfDocument` produits.
- `ILlmEnricher` : testé contre un `IChatClient` factice (pas d'appel réseau réel).
- `LlmResponseCache` : testé en isolation (hit/miss/écriture, sur un répertoire de cache temporaire).
- Politique d'écriture `--update`/`--reset`/`--force` : dossier de sortie vide (écriture directe), non vide sans flag (refus, rien écrit), non vide avec `--update` (préserve les fichiers non générés), non vide avec `--reset`/`--force` (supprime puis recrée).
- `validate` (commande) : `Bundle.Load` → `BundleValidator.Validate` → rapport texte stdout, exit non-zero si erreurs — format propre à cet outil, pas de contrainte de parité avec `okf validate`.
- Un test bout-en-bout `generate --mode scan` sur un petit repo fixture, sortie validée via `BundleValidator`.

## 6. Hors scope

- **Intégration graphify comme source d'analyse** : envisagée puis écartée pour la v1 (choix explicite) — le scanner reste indépendant (parcours direct du repo), fonctionne sans présupposer qu'un `graphify-out/` existe, et ne couple pas ce producer à un format de graphe externe non stabilisé.
- Commandes `diff`/`explain`/`init` (peuvent venir plus tard si le besoin se confirme).
- Sérialisation `usage_window` (hors périmètre du lot ergonomie sous-jacent).
- Toute modification à `ConceptId.ValidateSegment` (charset ASCII) — question ouverte remontée en amont (voir ROADMAP), indépendante de ce producer.
- Attribution/mention d'okf-generator dans le code ou la documentation (voir §7).

## 7. Licence & attribution

Aucune mention d'okf-generator dans le code, les commentaires, le NOTICE ou la documentation de ce producer — okf-generator a servi d'inspiration générale de conception (existence de la fonctionnalité, heuristiques de package-scope, surface de commandes) évaluée puis écartée comme dépendance directe, sans code ni texte réutilisé. Pas de NOTICE spécifique requis pour ce sous-projet au-delà du header SPDX standard sur chaque fichier.

## 8. Contraintes respectées

- `producers/` reste hors `OKF4net.sln`/CI ; `src/OKF4net` (référencé, pas modifié par ce lot) garde son zero-dependency strict.
- `OkfProducer.Core`/`OkfProducer.Cli` peuvent avoir des dépendances NuGet (System.CommandLine, Microsoft.Extensions.*) — exception assumée, propre à ce sous-projet.
- SPDX header, nullable, XML doc sur l'API publique, `dotnet format`-compatible — mêmes conventions de style que le reste du repo, héritées via `Directory.Build.props`.
- Pas de nouvelle golden fixture (`tests/fixtures/` est la propriété d'`OKF4net.Cli`, non touché ici).
