# OKF4net — État d'intégration & plan de release

> **But de ce document.** OKF4net est développé sur plusieurs sessions/worktrees en parallèle
> (core, agents, catalogue, MCP, website, distribution). Ce fichier est le point de vérité unique
> sur **ce qui est livré**, **ce qui reste à merger**, et **comment le merger sans casse**. Il est
> destiné à survivre au contexte d'une session : n'importe quelle session future doit pouvoir le
> lire et savoir où en est le projet.
>
> Dernière mise à jour : **2026-07-26**. Autorité : le code et le README priment ; ce doc décrit
> l'état de coordination, pas la spec.

---

## 1. TL;DR

- **`v0.1.1` est PUBLIÉE** (`origin/main` @ `da22640`, tag `v0.1.1`). Périmètre : **bibliothèque
  core + CLI** (`OKF4net`, `OKF4net.Cli`) + **distribution winget** + **website**. C'est une
  release *core-lib*.
- **La `v0.2.0` se construit sur `okf4net-phase4-catalog`** (non poussée, 94 commits d'avance /
  32 de retard sur `origin/main`). Elle apporte **Agents, ContextProvider, Catalogue, MCP** —
  c'est la release *features*.
- **Le merge `phase4-catalog` → `main` est débloqué** (la `v0.1.1` a shippé). Il reste **5 fichiers
  en conflit**, tous à résolution mécanique connue (§4).
- **Backlog** : Lot 3 (mémoire V2 team-scopée, spec à écrire) + push CI 3 OS + bump version 0.2.0.

---

## 2. Les deux lignes de release

| | **v0.1.1 — SHIPPED** | **v0.2.0 — à venir (cette branche)** |
|---|---|---|
| Portée | Core format + CLI | + Agents, ContextProvider, Catalogue, MCP |
| Projets | `OKF4net`, `OKF4net.Cli` | + `OKF4net.Agents`, `OKF4net.Catalog`, `OKF4net.Catalog.Hosting`, `OKF4net.Mcp` |
| Packages NuGet | `OKF4net`, `okf` (CLI) | + `OKF4net.Agents`, `OKF4net.Catalog`, `OKF4net.Catalog.Hosting` |
| Distribution | winget (CLI), website | inchangée |
| État | `origin/main` @ `da22640`, tag `v0.1.1` | `okf4net-phase4-catalog` @ HEAD (local) |

**Décision de séquencement (validée)** : la `v0.1.1` core devait shipper *avant* d'injecter les
features, pour ne pas gonfler le périmètre d'un patch. C'est fait. La `v0.2.0` peut désormais être
mergée sans contrainte.

---

## 3. Topologie des branches & où vit quoi

```
origin/main  ── da22640  (v0.1.1 : core + CLI + winget + website)   ◄── PUBLIÉ
     │
     └─ merge-base ── 52742d6
                          │
okf4net-phase4-catalog ── HEAD   (+94 commits : chaîne phase2→phase3→phase4 + MCP mergé)
```

La branche `okf4net-phase4-catalog` **contient déjà**, empilés :
- **Phase 2 — Agents** : `OKF4net.Agents` (seul projet sur `Microsoft.Agents.AI` 1.14.0),
  `OkfBundleTools` (9 AIFunction, pattern `RunTool` never-throw, écritures sous lock par chemin,
  gardes reparse/traversal/NUL).
- **Phase 3 — ContextProvider** : `OkfContextProvider : AIContextProvider`
  (progressive disclosure sous budget tokens ; mémoire déterministe sans LLM).
- **Lot 1 — Politique mémoire** : `MemoryCaptureMode { Disabled, SharedBundle }` (opt-in, `Disabled`
  par défaut — a remplacé `bool EnableMemoryCapture`, pré-release, pas de shim) ; fix E2 (verrou
  partagé **par chemin de bundle**, registre statique) ; re-check TOCTOU reparse tardif honnête.
- **Lot 2 — Catalogue local V1** : `OKF4net.Catalog` (BCL + core), `OKF4net.Catalog.Hosting`
  (+ `Microsoft.Extensions.DependencyInjection.Abstractions` uniquement). `catalog.json` strict,
  parsers never-throw errors-as-data, `FileKnowledgeCatalog` (throw-at-construction / reload
  errors-as-data / watcher best-effort / `Generation` monotone), resolver multi-source **groupé
  sans fusion**, façade DI `AddKnowledge`/`AddCatalogFile`.
- **Cœur partagé** : `OKF4net.ConceptSearch` (`Search`/`Excerpt`) promu au core — Agents **et**
  Catalog le consomment → parité de scoring par construction.
- **MCP** : `OKF4net.Mcp` (mergé depuis le worktree `okf-mcp-server`).

**Loi de dépendances** (acyclique) : `Hosting → Catalog → OKF4net` ; `Agents → OKF4net` ;
Catalog = BCL + core seulement ; Hosting = + DI.Abstractions seulement. Version depuis
`Directory.Build.props`.

---

## 4. Plan de merge `phase4-catalog` → `main` (5 conflits, résolutions connues)

Fichiers touchés des deux côtés depuis la merge-base `52742d6`. Résolution mécanique pour chacun :

| Fichier | Côté `main` (v0.1.1) | Côté `phase4` | **Résolution** |
|---|---|---|---|
| `.gitignore` | ajoute `packaging/winget/out/`, `web/…` | ajoute `.claude/worktrees/`, `.claude/settings.local.json` | **Union** — garder les deux blocs. Pas de vrai conflit. |
| `src/OKF4net/OKF4net.csproj` | `<Version>` 0.1.0 → **0.1.1** | supprime le `<Version>` inline (centralisé dans `Directory.Build.props`) + ajoute 3 `InternalsVisibleTo` (`okf`, `OKF4net.Agents`, `OKF4net.Catalog`) | **Garder la version phase4** : conserver les `InternalsVisibleTo`, laisser la version dans `Directory.Build.props` (à bumper **0.2.0**, §5). Ne pas réintroduire le `<Version>` inline. |
| `.github/workflows/release.yml` | gating release winget (CLI) | étapes packaging Agents/Catalog | **Union sémantique** — c'est le seul merge à soigner : garder le gating winget **et** ajouter les steps `dotnet pack` des nouveaux packages. À relire à la main. |
| `docs/superpowers/plans/2026-07-22-winget-cli-distribution.md` | version finalisée (session release) | copie WIP antérieure | **Prendre `origin/main`** — la session distribution est propriétaire de ces docs. |
| `docs/superpowers/specs/2026-07-22-winget-cli-distribution-design.md` | version finalisée | copie WIP antérieure | **Prendre `origin/main`** — idem. |

**Procédure recommandée** (à exécuter quand tu donnes le go, pas avant) :
1. `git checkout okf4net-phase4-catalog && git rebase origin/main` — remonter proprement les 94
   commits au-dessus de la v0.1.1 (préféré au merge : historique linéaire, conflits résolus une
   fois pour toutes). *Ou* merge classique si l'historique de merge est souhaité.
2. Résoudre les 5 conflits selon le tableau ci-dessus.
3. Bump `Directory.Build.props` → **0.2.0** (§5).
4. `dotnet build -warnaserror` + `dotnet test` + `dotnet format --verify-no-changes` verts.
5. `git checkout main && git merge --ff-only okf4net-phase4-catalog` (ou `--no-ff` pour marquer la
   feature-line), puis tag `v0.2.0`.

> ⚠️ Ne pas éditer les fichiers untracked / stashes / `.claude/` des sessions parallèles. Ne pas
> pousser sur `origin` sans accord (l'utilisateur pousse les releases).

---

## 5. Post-merge : à faire avant de tagger `v0.2.0`

- **Version** : bumper `Directory.Build.props` de 0.1.1 → **0.2.0** (source unique ; les csproj
  n'ont plus de `<Version>` inline côté phase4). Vérifier les nuspec/metadata des 3 nouveaux
  packages.
- **CI 3 OS — jamais lancée sur cette chaîne.** `phase4-catalog` n'a jamais été poussée sur
  `jchable/okf4net` → build/test **Linux & macOS jamais exécutés** dessus. Deux durcissements
  sécurité ne se testent *réellement* que sur FS case-sensitive (Linux) :
  - **F1** — comparaison de containment OS-aware dans `CatalogPathResolver` (OrdinalIgnoreCase
    s'échappe de la racine sur Linux) ;
  - **F2** — `IndexGenerator` vérifie le nœud cible `index.md` (reparse).
  → **Pousser tôt** pour valider ces chemins en CI réelle avant de tagger.
- **Packaging** : confirmer que `release.yml` produit bien les 3 nouveaux packages + les existants.
- **Docs** : mettre à jour le tableau spec-section → type du README avec Catalog/ContextProvider.

---

## 6. Backlog

### Lot 3 — Mémoire V2 team-scopée (spec à écrire)
Notes de conception capturées : [`specs/2026-07-24-okf4net-v2-scoped-memory-notes.md`](specs/2026-07-24-okf4net-v2-scoped-memory-notes.md).
Modèle 3 couches (session / user / tenant), scope dérivé-de-session comme mécanisme unique, split
lecture-seule / inscriptible, + fusion multi-source (le resolver V1 groupe sans fusionner).
Process : brainstorming → writing-plans avant tout code.

### Choix V1 délibérés (à revisiter en V2, pas des bugs)
- Resolver multi-source **groupé par source, sans fusion** (priorité desc / id asc).
- Champ `role` du manifeste : **knowledge-only** en V1.
- `FileSystemWatcher` **best-effort** ; `ReloadAsync` est la vérité (le watcher n'est qu'un
  déclencheur opportuniste).
- `MemoryCaptureMode` = `Disabled` par défaut (capture opt-in explicite).

### Minors ouverts
- Centralisation Version : **fait** côté phase4 (`Directory.Build.props`) — vérifier qu'aucun
  `<Version>` inline ne subsiste après merge.
- Follow-ups non bloquants du ledger `.superpowers/sdd/progress.md` (fixture broken-link, garde
  chdir des `GoldenParityTests`, métadonnées NuGet) — cf. ledger.

---

## 7. État qualité (branche `okf4net-phase4-catalog`, local)

- **Tests** : suite complète verte (dernier point : Lot 2 terminé) — dont **goldens 5/5**
  byte-exacts vs captures Rust.
- **Build** : Release `-warnaserror` propre.
- **Format** : `dotnet format --verify-no-changes` propre.
- **Revues** : `/simplify` (ne rien casser) + `/code-review` MAX passés ; findings sécurité
  F1/F2 corrigés (à re-valider en CI Linux, §5).

> Rappel : jamais toucher `tests/fixtures/` (goldens byte-exacts protégés `.gitattributes -text`).
> Si le C# diverge d'un golden, c'est le port qui est faux.
