# Design — Producer OKF natif (`producers/OkfProducer`)

- **Date** : 2026-07-31
- **Statut** : design validé en brainstorming, prêt pour plan d'implémentation
- **Contexte amont** : évaluation de `tommypacker/okf-generator` (TS, prior art) comme point de départ possible — écarté (v0.1 seulement, immature, hors écosystème C#) mais gardé comme référence de design pour la surface de commandes et les heuristiques de package-scope (aucun code ni mention créditée, simple inspiration générale — voir §7). Le producer natif construit à la place sur l'API OKF4net elle-même.
- **Dépendance bloquante** : ce producer référence quatre membres ajoutés à `src/OKF4net` par le lot « producer-ergonomics API » (`Provenance.ToYaml`, `ConceptId.Slugify`, la surcharge `BundleConceptWriter.WriteConcept(string, Frontmatter, string)`, `OkfDocumentBuilder` — voir `docs/superpowers/specs/2026-07-31-okf4net-producer-ergonomics-api-design.md`). Au moment de ce spec, ce lot est implémenté, testé (887/887) et revu (review finale clean), mais vit sur la branche `okf4net-producer-ergonomics-api`, **pas encore mergée dans `dev`**. Le plan d'implémentation du producer devra soit attendre ce merge, soit brancher directement depuis cette branche — décision à prendre au moment d'exécuter le plan, pas ici.

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
3. Écriture des documents via `BundleConceptWriter.WriteConcept(string, Frontmatter, string)` (respecte `--update`/`--reset`/`--force`, refuse d'écrire dans un dossier non vide sans flag).
4. Une fois les concepts écrits, appel à l'`IndexGenerator` existant d'OKF4net pour produire `index.md` — pas de générateur d'index réinventé. Le tampon `generated.at` par concept est géré nativement par `BundleConceptWriter`.

**Concepts générés pour la v1** : vue d'ensemble du repo, vue d'ensemble d'architecture, un concept par package/workspace détecté, concepts CLI (bin/executables des manifests), workflows dev/test/release, concepts docs/config/CI/tests.

**Commandes v1** : `generate` (scan → enrichissement optionnel → écriture bundle v0.2 conforme) et `validate` (réutilise `BundleValidator` d'OKF4net). Pas de `diff`/`explain`/`init` en v1.

## 4. Enrichissement LLM, cache, gestion d'erreurs

**Enrichissement** (`Microsoft.Extensions.AI`) : `ILlmEnricher.EnrichAsync(EvidenceBundle, CancellationToken) -> EnrichmentResult`, implémenté via l'abstraction `IChatClient` de `Microsoft.Extensions.AI` (connecteur OpenAI-compatible, base URL configurable — DeepSeek etc. comme tout endpoint compatible). Mode `scan` (défaut) → `NullLlmEnricher` no-op, 100% offline/déterministe ; `quick`/`explore` → appel réel. **Une tentative de retry** sur erreur transitoire (timeout/5xx), puis dégradation vers le contenu non-enrichi pour ce concept + warning stderr — jamais d'exception remontée à l'appelant.

**Cache LLM** : mini-bundle OKF interne (pas le bundle de sortie livré à l'utilisateur), à `.okf-producer-cache/` par défaut à la racine du repo scanné (overridable `--cache-dir`, désactivable `--no-cache`). Clé = hash SHA-256 hexadécimal de `(prompt, model, baseUrl)` — un hex digest est déjà un segment `ConceptId` valide (charset ASCII), pas besoin de `Slugify`. Écriture d'une entrée via `BundleConceptWriter.WriteConcept` (`type: "LLM Cache Entry"`, `title`/`description` minimaux pour satisfaire `OkfDocument.Validate()`, `body` = réponse brute) — réutilise l'atomicité/le verrouillage déjà éprouvés plutôt que de réinventer un format de cache fichier. Lecture : pas besoin de charger tout le bundle (`Bundle.Load`) — le path exact est connu via `ConceptId.ToPath`, un `File.Exists`/`OkfDocument.Parse` direct suffit pour un hit.

**Gestion d'erreurs**, trois niveaux distincts :
- **Scan** (fichier illisible, manifeste malformé) : permissif, diagnostics collectés, n'interrompt pas la génération (même philosophie que `Bundle.ParseErrors`).
- **LLM** : dégradation par concept après un retry (voir ci-dessus), jamais fatal pour la commande entière.
- **Écriture** (dossier de sortie non vide sans `--update`/`--reset`) : seul cas fatal — message clair + exit non-zero.

## 5. Tests

- `IRepositoryScanner` : fixtures de répertoires temporaires (pas de golden byte-exact requis — ce n'est pas `OKF4net.Cli`).
- `IConceptGenerator` : sur un `RepositorySnapshot` construit à la main → assertions sur les `OkfDocument` produits.
- `ILlmEnricher` : testé contre un `IChatClient` factice (pas d'appel réseau réel).
- `LlmResponseCache` : testé en isolation (hit/miss/écriture, sur un répertoire de cache temporaire).
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
