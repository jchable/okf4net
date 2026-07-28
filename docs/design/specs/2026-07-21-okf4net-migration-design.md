# OKF4net — Migration du projet `okf` (Rust) vers .NET pour Microsoft Agent Framework

> **Document historique.** Ce document décrit la migration ponctuelle qui a
> produit OKF4net. Le projet est aujourd'hui une **implémentation .NET
> indépendante** de la spec OKF et ne suit plus aucune autre implémentation ;
> les mentions de parité avec le Rust ci-dessous sont un instantané de
> l'époque, pas un objectif courant.

**Date** : 2026-07-21
**Statut** : validé en brainstorming, en attente de plan d'implémentation

## Contexte et objectif

Le repo contient `okf`, une implémentation **Rust pure, zéro dépendance** de l'[Open Knowledge Format (OKF) v0.1](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md) de Google : parseur de documents markdown + frontmatter YAML, graphe de cross-links, validation de conformité, génération d'`index.md`, parsing de `log.md`, et un CLI (`okf validate/info/index/graph/parse/fmt`). ~3 800 lignes de Rust, tests compris. Aucune ligne de Go (contrairement à l'intitulé initial de la demande).

**Objectif** : porter l'intégralité du projet en .NET sous le nom **OKF4net**, supprimer tout le code Rust, et exposer les capacités OKF aux agents construits sur **Microsoft Agent Framework** (`Microsoft.Agents.AI`).

## Décisions de cadrage

| Décision | Choix retenu |
|---|---|
| Framework agent cible | Microsoft Agent Framework (.NET), package `Microsoft.Agents.AI` |
| Périmètre | Lib complète + couche tools agent + CLI |
| YAML | Port fidèle du parseur maison (pas de YamlDotNet) — zéro dépendance NuGet du core |
| Cible technique | .NET 10 (net10.0), C# 14, nullable activé |
| Fidélité | Port 1:1 de la sémantique Rust, port de tous les tests, API idiomatique C# |
| Sort du Rust | Supprimé en fin de Phase 1, après preuve de parité par golden tests |
| Nommage | Solution `OKF4net.sln` ; projets `OKF4net`, `OKF4net.Cli`, `OKF4net.Agents`, `OKF4net.Tests` ; namespace racine `OKF4net` |

## Cas d'usage agents

Retenus au cadrage :
1. **Lire / naviguer la connaissance** (progressive disclosure via `index.md`, suivi des cross-links)
2. **Produire / mettre à jour des concepts** (écriture validée, `log.md`, régénération d'index)
3. **Valider en CI / gouvernance** (conformité §9, exit codes)
4. **Analyser le graphe de liens** (backlinks, liens cassés, stats)
5. **Mémoire long-terme d'agent** (le bundle OKF comme mémoire persistante, versionnée git)
6. **Context provider avec budget tokens** (`AIContextProvider`)
7. **Recherche dans le bundle** (full-text — nouveau, absent du Rust)
8. **Diff / résumé de changements** (agrégation `log.md`)

## Architecture

Approche retenue (« A ») : port en couches, solution multi-projets, 3 phases. Le core reste
zéro-dépendance ; seul `OKF4net.Agents` référence Microsoft Agent Framework.

```
OKF4net.sln
├── src/OKF4net/            Lib core — ZÉRO dépendance NuGet
│   ├── Yaml/               YamlValue, YamlMapping, YamlParser, YamlEmitter
│   ├── OkfDocument, Frontmatter, ConceptId, LinkScanner
│   ├── Bundle, IndexGenerator, ChangeLog, BundleValidator
├── src/OKF4net.Cli/        okf (validate, info, index, graph, parse, fmt) — Native AOT
├── src/OKF4net.Agents/     Dépend de Microsoft.Agents.AI
│   ├── OkfBundleTools      9 tools AIFunction
│   └── OkfContextProvider  AIContextProvider (contexte à budget + mémoire)
└── tests/OKF4net.Tests/    xUnit — port des tests Rust + golden tests de parité
```

## Section 1 — Bibliothèque core `OKF4net`

Projet `src/OKF4net/`, net10.0, **aucune dépendance NuGet**.

### Mapping des modules Rust → C#

| Rust | C# | Notes |
|---|---|---|
| `yaml::Value` | `YamlValue` — hiérarchie scellée : `YamlScalar` (null/bool/long/double/string), `YamlSequence`, `YamlMapping` | Records scellés + pattern matching (pas d'unions en C#) |
| `yaml::Mapping` | `YamlMapping` | **Ordre d'insertion préservé** : `List<KeyValuePair<string, YamlValue>>` + index `Dictionary` interne. Exigence round-trip de la spec |
| `yaml::parser` | `YamlParser` (interne) ; API publique `YamlValue.Parse(...)` | Même sous-ensemble : collections block/flow, scalaires quotés/plain, block scalars `\|`/`>`, commentaires. **Rejette anchors, tags, multi-documents avec les mêmes messages d'erreur que le Rust** |
| `yaml::emitter` | `YamlEmitter` | Sortie octet-pour-octet identique au Rust (golden tests) |
| `frontmatter::Frontmatter` | `Frontmatter` | Wrappe le `YamlMapping` complet ; accesseurs typés `Type`, `Title`, `Description`, `Tags`, `Timestamp`, clés d'extension. Les clés inconnues survivent au round-trip |
| `concept_id::ConceptId` | `ConceptId` (readonly record struct) | `Parse`/`TryParse`, conversion chemin ↔ id (`tables/users.md` → `tables/users`), validation des segments. Séparateur `/` normalisé, y compris sous Windows |
| `document::Document` | `OkfDocument` | `Parse`, `Serialize` (préserve ordre des clés + body), `ValidateConformance` (§9 : `type` non vide), `Validate` (strict producteur : + `title`, `description`, `timestamp`), `Links`, `Citations` |
| `links` | `LinkScanner` + `ConceptLink`, `Citation` | Extraction des liens markdown, classification (absolu bundle-relatif, relatif, externe), résolution, sections citations (§8). Ignore les liens dans le code inline/blocs |
| `bundle::Bundle` | `Bundle` | `Bundle.Load(path)` **permissif** : ne lève jamais sur un fichier invalide — collecte dans `ParseErrors`, conserve les liens cassés comme arêtes vers concepts inexistants. `LinksFrom(id)`, `Backlinks(id)`, `OkfVersion`, `Count`. Noms réservés : `index.md`, `log.md` |
| `index` | `IndexGenerator` | `RegenerateIndexes(bundle)` — sortie identique au Rust |
| `log` | `ChangeLog` | Parse/build de `log.md` (historique groupé par date, dates ISO) |
| `validate` | `BundleValidator` → `ValidationReport` | Diagnostics avec sévérité, `IsConformant`, comptages |

### Décisions transverses

- **Erreurs** : exceptions dédiées (`OkfException` base → `YamlParseException`, `DocumentParseException`, `BundleLoadException`) + variantes `TryParse` là où le Rust est utilisé en mode essai. `Bundle.Load` ne lève pas pour les erreurs par fichier (elles restent des données, comme en Rust).
- **Déterminisme** : comparaisons ordinales (`StringComparison.Ordinal`), UTF-8 strict, tri stable des entrées d'index. Tout écart casse la parité golden.
- **API idiomatique C#** : propriétés, `IReadOnlyList<T>`/`IReadOnlyDictionary<>`, nullable reference types — mais sémantique strictement identique au Rust.

## Section 2 — CLI `OKF4net.Cli`

Port direct de `src/bin/okf.rs` :

- Binaire `okf`, six commandes : `validate`, `info`, `index`, `graph` (`--dot`), `parse`, `fmt` (`-w`).
- **Parsing d'arguments à la main** (pas de System.CommandLine) : fidélité des messages et codes de sortie, zéro dépendance, **Native AOT** (binaire autonome, démarrage instantané pour la CI).
- Codes de sortie identiques au Rust — `okf validate` sort non-zéro si non conforme (contrat CI).
- Sorties texte identiques au Rust là où observables (couvertes par les golden tests).

## Section 3 — Couche `OKF4net.Agents`

Seul projet référençant `Microsoft.Agents.AI` (+ `Microsoft.Extensions.AI.Abstractions`).

### 3a. `OkfBundleTools`

Classe instanciée sur un répertoire de bundle. Méthodes publiques annotées
`[System.ComponentModel.Description]` (méthode + paramètres), transformées en `AIFunction` via
`AIFunctionFactory.Create`. Méthode `GetTools()` retournant `IList<AITool>` prête pour
`AsAIAgent(tools: ...)`.

| Tool | Cas d'usage | S'appuie sur |
|---|---|---|
| `okf_read_concept(id)` | Navigation | `Bundle` + `OkfDocument` — frontmatter + body + liens sortants/backlinks |
| `okf_browse(path?)` | Progressive disclosure | `index.md` du répertoire donné, racine par défaut |
| `okf_search(query, tags?)` | Recherche | Full-text titres/tags/description/corps (nouveau) |
| `okf_write_concept(id, frontmatter, body)` | Production | `Validate` strict producteur **avant** écriture ; refuse un document invalide |
| `okf_append_log(entry)` | Production | `ChangeLog` — entrée sous la date du jour |
| `okf_regenerate_indexes()` | Production | `IndexGenerator` |
| `okf_validate_bundle()` | Validation/CI | `BundleValidator` — rapport sérialisé |
| `okf_graph(concept_id?)` | Analyse | Liens sortants/backlinks/liens cassés ; sans argument : stats globales |
| `okf_changes_since(date)` | Diff | Agrégation des `log.md` depuis une date |

Les tools d'écriture s'appuient sur le mécanisme standard de **tool approval** d'Agent
Framework — pas de garde-fou maison.

### 3b. `OkfContextProvider : AIContextProvider` (Phase 3)

- `ProvideAIContextAsync(InvokingContext)` : injecte l'`index.md` racine + les concepts
  pertinents pour le dernier message utilisateur, en descendant le graphe
  (index → concept → liens) **sous un budget de tokens configurable**
  (estimation ~4 caractères/token, aucune dépendance tokenizer).
- `StoreAIContextAsync(InvokedContext)` : mémoire long-terme — extrait les apprentissages de
  l'échange et les écrit comme concepts OKF + entrée `log.md`. Le bundle devient la mémoire
  persistante de l'agent, versionnable par git.
- État par session via `ProviderSessionState<TState>` (le provider est partagé entre sessions).
- Sécurité : le contenu des bundles est traité comme non-fiable — jamais injecté avec le rôle
  `system`, conformément à la note de la doc `AIContextProvider` (risque de prompt injection
  indirecte).

## Section 4 — Tests, parité, phasage

### Tests

- `tests/OKF4net.Tests/` : xUnit. Port 1:1 des cinq fichiers de tests Rust
  (`tests/yaml.rs` → `YamlTests.cs`, `document.rs`, `links.rs`, `bundle.rs`, `index.rs`)
  + helper `TempDir` C# (remplace `tests/common/mod.rs`).
- **Golden tests de parité** : avant suppression du Rust, générer avec le binaire Rust des
  fixtures de référence (sorties de `fmt`, `index`, `graph --dot`, `validate` sur des bundles
  d'exemple), archivées dans `tests/fixtures/`. La suite C# doit les reproduire
  octet-pour-octet. C'est la preuve de parité qui autorise la suppression du Rust.
- Couche Agents : tests unitaires des tools sans LLM (simples méthodes C#) ; provider testé
  avec des `InvokingContext`/`InvokedContext` construits à la main.

### Phasage

| Phase | Livrable | Critère de sortie |
|---|---|---|
| **1** | `OKF4net` core + `OKF4net.Tests` (tests portés + golden) + `OKF4net.Cli` | Tous les tests passent, parité golden démontrée → **suppression du code Rust** (`Cargo.toml`, `Cargo.lock`, `src/`, `tests/` Rust) |
| **2** | `OKF4net.Agents` : `OkfBundleTools` (9 tools) | Tools testés unitairement, démo d'intégration avec un `ChatClientAgent` |
| **3** | `OkfContextProvider` : contexte à budget tokens + mémoire long-terme | Provider testé, scénario mémoire de bout en bout |

## Hors périmètre

- Recherche sémantique/vectorielle (le tool `okf_search` est full-text ; extension possible plus tard via les intégrations RAG d'Agent Framework).
- Publication NuGet publique (décision reportée ; la structure multi-projets la rend triviale le moment venu).
- Fédération multi-bundles.
- Toute évolution du format OKF au-delà de la v0.1.
