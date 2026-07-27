# OKF Python (Google) vs OKF4net — feature & architecture diff

> Snapshot d'analyse (2026-07-27), gardé comme contexte pour la montée en spec
> v0.2. Le code et le README restent la référence autoritative du comportement
> courant. Sources : [`GoogleCloudPlatform/knowledge-catalog` `/okf`](https://github.com/GoogleCloudPlatform/knowledge-catalog/tree/main/okf),
> [`SPEC.md`](https://github.com/GoogleCloudPlatform/knowledge-catalog/blob/main/okf/SPEC.md).

## Le constat central

Les deux projets ne jouent pas dans le même camp :

- **Le Python de Google est un *producteur*.** Le package s'appelle
  `reference-agent`, pas `okf`. C'est un agent Google ADK qui introspecte un
  dataset BigQuery, fait rédiger un document OKF par table/vue via un LLM Gemini,
  puis une seconde passe web enrichit avec des citations. Plus un visualiseur
  HTML Cytoscape. Sa lib « cœur » (parser/validateur) est **minimale** :
  `bundle/document.py` ≈ `yaml.safe_load` + un `validate()` qui vérifie
  *uniquement* la présence de `type`.
- **OKF4net est un *consommateur/outillage*.** Modèle typé par section de spec,
  CLI byte-exact, scanner de liens, changelog, validation à deux niveaux, le tout
  zéro-dépendance ; puis des surcouches (Agents, Catalog, MCP) absentes du Python.

Recouvrement réel : seulement un petit noyau (`document` + `index` + `paths`), et
même là des choix opposés (PyYAML + dict non typé côté Google ; parser maison +
frontmatter typé côté OKF4net).

## Divergence #1 (actionnable) : la spec a bougé en v0.2

Ne pas confondre version *package* et version *spec* :

| | Version package | Version spec cible |
|---|---|---|
| Python `reference-agent` | 0.1.0 | **OKF v0.2** |
| OKF4net | 0.2.0 | **OKF v0.1** (`OkfSpec.Version = "0.1"`) |

La spec upstream (`okf/SPEC.md`) est passée à **v0.2**, sur-ensemble de v0.1 avec
2 renommages breaking + des ajouts. OKF4net implémente fidèlement v0.1 — donc est
en retard d'une version de spec.

### Ce que v0.2 introduit et qu'OKF4net ne gère pas encore

- **Breaking** : `timestamp` → remplacé par `generated.at` (fallback legacy
  toléré) ; heading body `# Citations` → remplacé par le champ frontmatter
  `sources`.
- **Provenance/confiance (§5)** : `sources` (+ signaux `author`, `usage_count`,
  `last_modified`, `usage_window`), `generated`/`verified`, `status`
  (draft|stable|deprecated), `stale_after`, *trust tiers* (unverified /
  machine-confirmed / human-reviewed).
- **Convention d'acteur (§7)** : `<producer>/<version>`, `human:<id>`,
  `process:<id>`.
- **Nouveau type « Attested Computation » (§10)** : `runtime`, `parameters`,
  `computation`, `executor`, `attester`, `receipt` + heading `# Computation`.
- **`okf_version` dans le `index.md` racine (§12)**.

Le socle OKF4net est bien placé : `Frontmatter` (order-preserving sur
`YamlMapping`) survit déjà aux clés inconnues, et le chargement permissif
n'aborte pas. Le travail est d'ajouter getters typés + validation, pas de
refondre le parsing.

## Diff feature par feature

| Domaine | Python (`reference-agent`) | OKF4net | Gap |
|---|---|---|---|
| Rôle | Producteur (LLM génère) | Consommateur/outillage | Complémentaires |
| Spec cible | v0.2 | v0.1 | **Python en avance** |
| Parsing YAML | PyYAML `safe_load` | Parser maison strict (rejette anchors/tags/multi-doc) | OKF4net |
| Frontmatter | `dict` non typé | `Frontmatter` typé, order-preserving | OKF4net |
| Validation | `type` présent, point | 2 niveaux + `BundleValidator` Error/Warning/Info | **OKF4net** |
| CLI | `enrich` + `visualize` | `validate`/`info`/`index`/`graph`/`parse`/`fmt` | Camps différents |
| Génération d'index | descriptions **LLM** (Gemini) | synthèse **déterministe pluggable** (delegate) | Divergence §6 |
| Graphe de liens | regex pour viewer Cytoscape | `LinkScanner` + `graph`/`--dot`, backlinks, broken | OKF4net (outil) / Python (visuel) |
| Changelog (§7) | Absent | `ChangeLog` parse/emit | **OKF4net seul** |
| Génération LLM | Cœur du produit (ADK, 2 passes BQ+web) | Absent (par design) | **Python seul** |
| Connecteurs sources | BigQuery (+ archi `Source`) | Absent | **Python seul** |
| Crawl web | `web/fetcher`, allow-list, budget, depth | Absent | Python seul |
| Visualiseur | HTML Cytoscape (JS via CDN) | Absent (DOT à la place) | Python seul |
| Couche Agents/tools | `FunctionTool` internes à l'agent | `OkfBundleTools` = 9 `AIFunction` réutilisables, robustes | **OKF4net** |
| Context provider / mémoire | Absent | `OkfContextProvider` + capture mémoire déterministe | **OKF4net seul** |
| Catalogue multi-bundles | Absent | `OKF4net.Catalog` + hosting DI + mémoire scopée V2 (partielle) | **OKF4net seul** |
| Serveur MCP | Absent | `okf-mcp` dotnet tool, stdio, read-only | **OKF4net seul** |
| Sécurité fichiers | Guards applicatifs (allow-list, augmentation guard) | Anti null-char/path-traversal, détection symlink/junction, contenu *untrusted* | OKF4net (défensif) |
| Tests | pytest, ~7 fichiers, pas de golden | xunit ~41 classes / ~494 facts, golden byte-exact | **OKF4net** |
| Dépendances | Lourdes (`google-adk`, `bigquery`, `pydantic`, `pyyaml`, `markdownify`) | Zéro au cœur ; exceptions cadrées | Objectifs opposés |
| Packaging | wheel PyPI-style + console-script | NuGet ×4 + binaire AOT `okf` + dotnet tool `okf-mcp` + winget | OKF4net |
| Licence | Apache-2.0 | LGPL-3.0-or-later (portions dérivées Apache-2.0) | — |

## Ce que chacun a en exclusivité

- **Python (producteur)** : agent LLM ADK, introspection BigQuery (collapse des
  tables date-shardées en familles wildcard), passe web d'enrichissement,
  descriptions d'index LLM, viewer Cytoscape, « augmentation guard » (refuse
  qu'une passe web rétrécisse un schéma existant).
- **OKF4net (consommateur/plateforme)** : validation multi-niveaux + diagnostics,
  CLI byte-exact, changelog, graphe DOT/backlinks, 9 tools réutilisables, context
  provider + mémoire, catalogue + hosting DI, serveur MCP, corpus golden, AOT.

## Recommandations roadmap

1. **Rattraper la spec v0.2** — seul vrai « retard », travail cadré :
   - Getters typés + validation pour `sources`, `generated`/`verified`, `status`,
     `stale_after`, signaux de crédibilité (§5).
   - Migration `timestamp` → `generated.at` avec **fallback legacy** (comme le
     Python) — ne pas casser les bundles v0.1 ni les goldens.
   - Champ frontmatter `sources` en remplacement du body `# Citations` ;
     `Citations()`/`ExtractCitations` deviennent du legacy à conserver en compat.
   - Convention d'acteur (§7) et `okf_version` racine (§12).
   - Bumper `OkfSpec.Version` → `"0.2"` (impacte le libellé CLI
     `conformant with OKF v0.X` ⇒ probablement des goldens — à traiter avec soin).
   - Évaluer le type **Attested Computation (§10)** + heading `# Computation`.
2. **Ne pas cloner le producteur.** L'agent LLM/BigQuery est à l'opposé de la
   thèse zéro-dépendance. Un éventuel besoin de génération a sa place dans
   `OKF4net.Agents` (déjà autorisé à dépendre de l'Agent Framework), pas au cœur.
   Positionnement : OKF4net *consomme et outille* ce que le reference-agent
   *produit*.
3. **Interop de conformité** : ajouter comme fixtures de *chargement* v0.2 un ou
   deux bundles produits par le reference-agent (samples `acme_retail`,
   `crypto_bitcoin`, `ga4`, `stackoverflow` checkés dans le repo Google) — preuve
   qu'OKF4net lit la sortie officielle sans broncher.
4. **Optionnel** : un `graph --html` self-contained en réponse au viewer
   Cytoscape (faible priorité ; contrainte no-CDN ⇒ inliner les libs JS).
