# Design — Étage graphe de code du producer (`producers/OkfProducer`)

- **Date** : 2026-08-31
- **Statut** : design validé en brainstorming (sections 1 à 7), prêt pour plan d'implémentation
- **Portée** : ajoute un étage d'extraction de code (types, membres, graphe d'appels) au producer existant. Ne modifie **rien** dans `src/OKF4net` — aucune évolution de la bibliothèque n'est requise.
- **Design amont** : [`2026-07-31-okf-producer-design.md`](2026-07-31-okf-producer-design.md) (walking skeleton livré : scanner npm/NuGet/README → bundle v0.2).
- **Prior art** : **okf-rs**, producer Rust qui extrait types, fonctions, méthodes et un graphe d'appels sur 11 langages via tree-sitter. Sert de référence de capacité et de repère de comparaison ; aucun code repris.
- **Spike de faisabilité** : worktree `spike-treesitter-dotnet`. Résultats en annexe A — **provenance cassée, chiffres non reproductibles**, voir l'avertissement en tête d'annexe.

> ## ✅ Statut : les deux verrous sont levés — le design est prêt pour un plan d'implémentation
>
> Deux audits indépendants (interne et externe, 2026-08-31) ont chacun trouvé des défauts porteurs. Les révisions rédactionnelles sont appliquées, et les deux blocages sont traités :
>
> 1. ~~**L'étage Roslyn n'est pas démontré**~~ → **LEVÉ le 2026-08-31.** Le prototype atteint **zéro erreur de compilation** sur trois projets réels sans `MSBuildWorkspace` (mesure et protocole en §7.2 ; code jetable, conservé dans l'historique au commit `0db6e9a`). Il corrige au passage trois hypothèses de §7.2 — dont la plus lourde : le dépôt doit être **construit**, pas seulement restauré.
> 2. ~~**Le critère d'exploitabilité échoue**~~ → **LEVÉ le 2026-08-31.** `ConceptSearch.TopDiversified` fait tourner la sélection entre familles d'ids, branché dans `okf_search` et `OkfContextProvider`. Sur le même corpus, les places curatées dans le top 5 passent de **1/55 à 23/55**, et les requêtes sans aucun curaté dans le top 20 de **5/11 à 0/11** (§8.7).
>
> Reste à écrire : le plan d'implémentation du générateur lui-même, dont la séquence est donnée en fin de `docs/superpowers/plans/2026-08-31-okf-producer-code-graph-gates.md`.
>
> Corrigé depuis le premier jet : résolution de `resource` (§4.3), granularité de `generated.at` (§6.1), unités de colonne entre les deux moteurs (§2.1), politique d'entrée hostile (§2.3), transactionnalité de l'élagage (§6.3), définition de `--check` (§6.2), appartenance des fichiers à un projet (§5.1), provenance du spike (annexe A).

---

## 1. Objectif et constat de départ

Le producer actuel génère `overview` + `packages/*` + `docs/*` à partir des manifestes npm/NuGet et du README. C'est peu, et deux mesures faites pendant le design disent précisément en quoi :

1. **Aucun concept ne décrit le code lui-même** — ni type, ni membre, ni appel.
2. **`ConceptGenerator` n'émet aucun lien markdown.** Conséquence vérifiée : `okf graph` sur un bundle OkfProducer n'affiche *rien*. Le bundle est une liste plate de concepts non reliés, pas un graphe.

L'objectif de ce lot est donc double, et le second point n'est pas un bonus : **produire les concepts de code, et relier l'ensemble du bundle** (y compris les concepts existants) par des liens §6.

Cible de volumétrie sur ce repo : ~480 concepts et ~170 `index.md`, contre 11 concepts aujourd'hui.

**Portée langage de la v1 : C# uniquement.** L'architecture est multi-langage par construction (§2.1, §3.1), et les exemples TypeScript/Go de ce document décrivent le *seam*, pas la livraison. Seul le `LanguageProfile` C# — le seul adossé à un resolver précis (Roslyn) — est en v1.

---

## 2. Pipeline et découpage en projets

### 2.1 Le pipeline

Le pipeline existant est une chaîne à trois maillons derrière trois interfaces à implémentation unique. Le nouvel étage s'insère comme un **quatrième maillon parallèle au scanner**, pas comme une réécriture :

```
RepositoryScanner ──→ RepositorySnapshot { Packages, Docs }
                            │
                            ├──→ ICodeGraphBuilder ──→ CodeGraph { Symbols, Edges }
                            │         ├─ ILanguageExtractor   (1 impl : tree-sitter + LanguageProfile)
                            │         └─ ISymbolResolver[]    (NameMatchResolver, RoslynResolver)
                            ↓
                     ConceptGenerator ──→ GeneratedConcept[] ──→ BundleWriter
```

**Deux seams, et un seul point d'extension par langage.** `ILanguageExtractor` a **une** implémentation, paramétrée par un `LanguageProfile` ; ajouter un langage = ajouter un profil, pas une classe. Le profil porte tout ce qui est spécifique au langage : requêtes tree-sitter, règle de visibilité, forme du segment conteneur (§3), forme du commentaire de doc (§4.2).

**La détection de nature du projet existe déjà.** `RepositoryScanner` résout les `.csproj` depuis le `.sln` racine et détecte les `package.json`. Cette sortie *est* la détection : elle sélectionne les `LanguageProfile` actifs et les resolvers à brancher. Aucun mécanisme de détection nouveau.

**Les resolvers sont chaînés, pas exclusifs.** `NameMatchResolver` produit un verdict de base pour tous les langages ; `RoslynResolver` écrase ensuite les verdicts des fichiers dont il est propriétaire, à identité de site d'appel. Propriété qui en découle et qui est la raison du découpage : un resolver absent dégrade la **précision**, jamais la **forme** de la sortie.

**L'identité du site d'appel ne peut pas être `(fichier, ligne, colonne)` naïf** — les deux moteurs ne sont pas garantis de compter dans la même unité, et ça ne se suppose pas, ça se mesure. Roslyn positionne en **UTF-16**, l'unité des `string` .NET — ça, c'est un contrat documenté. Côté tree-sitter, en revanche, **la Tâche 3 a mesuré autre chose que ce que cette section affirmait initialement** : le binding `TreeSitter.DotNet` 1.3.0 ne restitue pas la sémantique brute de l'API C native (où la `column` d'un `Point` compte des octets). Quand on parse une `string` .NET — la seule entrée que son API publique accepte, il n'existe pas de surcharge `Parse(byte[])` — `Node.StartIndex`/`EndIndex` (et `Point.Column`) sont en réalité des offsets **UTF-16**, identiques à l'index dans la chaîne .NET d'origine, pas des offsets UTF-8 bruts. Mesuré, pas supposé :

| source | index UTF-16 de `Foo` | `Node.StartIndex` observé | offset UTF-8 attendu |
|---|---|---|---|
| `var café = Foo();` | 11 | 11 | 12 |
| `var x = "🎯"; Foo();` | 14 | 14 | 16 |
| `var naïve = 1;\r\nFoo();` | 16 | 16 | 17 |

Les deux moteurs divergent bien dès le premier caractère non-ASCII qui précède l'appel — un identifiant accentué, un littéral avec un emoji, un caractère hors du plan de base — mais pas dans le sens que cette section affirmait : ici, c'est Roslyn qui rejoint nativement la sortie de ce binding, ce n'est pas tree-sitter qui donne des octets gratuitement. Un raccrochage silencieusement faux reste pire qu'un raccrochage absent : il attribue un appel au mauvais symbole.

Règle, conclusion inchangée mais fondement corrigé : l'identité reste **l'offset absolu dans le fichier, normalisé en UTF-8** ; la ligne/colonne ne sert qu'à l'affichage. Ce n'est plus une propriété que tree-sitter fournirait gratuitement pour ce binding — c'est `TreeSitterExtractor` qui convertit explicitement chaque offset via `Utf8Offsets.ToUtf8` avant de construire un `SymbolFact`/`CallSite`, exactement comme le fait l'extracteur Roslyn (Tâche 6) pour ses propres offsets UTF-16. Ne pas supprimer cette conversion sous prétexte que les deux moteurs coïncident aujourd'hui pour ce binding précis : cette égalité est un artefact de la façon dont le binding est nourri (une `string` .NET plutôt que des octets), pas un contrat — une montée de version du binding, ou un futur chemin de parsing par octets, la casserait silencieusement, et dans le sens où l'échec est un mauvais raccrochage plutôt qu'un raccrochage absent. La conversion explicite coûte deux appels et rien d'autre. La conversion est un point de test obligatoire (§8.4), avec au minimum `var café = Foo();`, un emoji, un caractère astral, et un fichier en CRLF — désormais vérifié contre l'extracteur réel, pas seulement contre `Utf8Offsets` isolément (voir Tâche 3, `task-3-report.md`).

### 2.2 Découpage en projets

`OkfProducer.Core` ne référence aujourd'hui qu'`OKF4net`. Y ajouter `TreeSitter.DotNet` lui collerait ~590 Mo de natifs, y compris pour les consommateurs qui n'analysent pas de code. D'où trois projets :

| Projet | Dépendances | Rôle |
|---|---|---|
| `OkfProducer.Core` | `OKF4net` seul | contrats (`ILanguageExtractor`, `ISymbolResolver`, `ICodeGraphBuilder`), types de données (`SymbolFact`, `CallSite`, `CodeGraph`), `CodeGraphBuilder` |
| `OkfProducer.CodeGraph.TreeSitter` | Core + `TreeSitter.DotNet` | l'unique `ILanguageExtractor` + les `LanguageProfile` |
| `OkfProducer.CodeGraph.Roslyn` | Core + `Microsoft.CodeAnalysis.CSharp` | `RoslynResolver` — **sans MSBuild** (voir §7.2) |
| `OkfProducer.Cli` | tous | racine de composition (le DI est déjà en place) |

`Core` reste testable sans natif ni compilateur : les tests de `CodeGraphBuilder` et `ConceptGenerator` se nourrissent de `SymbolFact`/`CallSite` fabriqués à la main. Les deux projets lourds n'ont chacun qu'une seule chose à prouver — « tree-sitter sort les bons spans », « Roslyn épingle les bons symboles » — et leurs tests se marquent et s'excluent séparément.

### 2.3 Entrée hostile et politique d'erreur

L'extracteur ingère du **code source arbitraire**, y compris d'un dépôt qu'on n'a pas écrit. Le dépôt a déjà appris cette leçon sur son parseur markdown : le code de parsing sur entrée non fiable a besoin d'une politique explicite, pas de bonnes intentions. Un `File.ReadAllText` suivi d'un parse intégral n'en est pas une.

| Cas | Règle |
|---|---|
| Fichier volumineux ou minifié sur une ligne | plafond de taille par fichier (défaut 2 Mo, `--max-file-size`) ; au-delà, fichier ignoré par **les deux** moteurs, jamais tronqué — un parse partiel produirait des spans faux. L'extracteur tree-sitter le **compte** (run partiel) ; la porte Roslyn (`SourceFileGate`) le laisse tomber **silencieusement** (remédiation vague 2b, round 3 : « ignoré et compté » sans réserve était faux de la moitié Roslyn de l'étape code) |
| Encodage | UTF-8 (avec ou sans BOM) et UTF-16 avec BOM ; tout autre encodage ou toute séquence invalide → fichier ignoré et compté |
| Arbre tree-sitter contenant des nœuds `ERROR` | les symboles hors de la région en erreur sont conservés, la région est signalée ; le fichier compte comme **partiellement analysé** |
| Profondeur d'imbrication pathologique | limite de profondeur de parcours ; au-delà, fichier ignoré et compté |
| Symlink / point de reparse sortant du dépôt | jamais suivi — `Internal/ReparsePoints` porte déjà cette détection dans la bibliothèque |
| Fichier modifié pendant le run | lecture unique en snapshot ; le hash du contenu lu entre dans le manifeste (§6.3) |
| Code qui ne compile pas | normal, pas une erreur : tree-sitter reste tolérant, Roslyn dégrade vers `NameMatchResolver` |
| Run trop long | timeout global et `CancellationToken` ; une annulation rend le run **partiel** |

**La règle qui relie ce tableau au reste du design** : tout fichier ignoré, partiellement analysé ou annulé est **enregistré avec sa cause**, et restreint l'élagage en conséquence (§6.3). Le rapport de sortie liste les fichiers non analysés avec leur cause — c'est ce qui distingue « symbole supprimé » de « fichier non lu ».

> **Livré en remédiation vague 3** (`GenerateRun.Summarize`, préfixe `run: ` sur stderr, §9). Cette phrase décrivait une intention que rien n'implémentait : `RunStatus` n'avait qu'un seul consommateur de production, à l'intérieur du refus d'élaguer de `BundleWriter`, que `Reconcile` court-circuite dès qu'il n'y a pas de manifeste précédent ou pas de candidat. Un premier run, un run `--reset`, ou tout run dont l'ensemble d'ids n'a pas rétréci calculaient donc `TraversalComplete` et chaque `FileStatus` non-`Extracted` puis les jetaient. Mesuré sur cet hôte, exit 0 et rien d'imprimé au-delà de `Wrote N concept(s)` : un `--max-file-size` sous l'unique fichier source, et une jonction circulaire (avec un `*.sln` racine, sans quoi c'est le scanner qui échoue bruyamment d'abord) qui vide la liste de fichiers et n'écrit qu'`overview`.
>
> Le rapport nomme les fichiers **entièrement ignorés**, plafonné à dix avec un compte de reste — pas les `PartiallyExtracted`, qui sont l'état normal d'un dépôt C# moderne (voir l'encadré ci-dessous) et noieraient les précédents. Les compteurs par cause, eux, sont toujours donnés.

> **Corrigé à l'implémentation (mesure de la Task 4, ruling R21, appliqué en Task 11).** Le premier jet écrivait ici « un run partiel **n'élague rien** », et c'est trop grossier : cette formulation annule la règle 3 de §6.3 et rend l'élagage inatteignable.
>
> Mesuré : la grammaire tree-sitter vendorisée ne parse pas une expression de collection **vide** `[]` en position d'expression — idiome ordinaire du C# 12+, présent dans les sources de ce producer même — et 3 fichiers réels sur 6 échantillonnés reviennent `PartiallyExtracted` pour cette seule raison. `PartiallyExtracted` est donc l'**état normal** d'un dépôt C# moderne, pas un cas rare : la règle grossière aurait désactivé l'élagage pour toujours.
>
> La règle exacte sépare deux formes d'échec que ce paragraphe confondait. Une **traversée tronquée** (racine absente ou illisible, échec d'énumération, annulation ou timeout — `RunStatus.TraversalComplete` faux) interdit tout élagage, pour une raison sans rapport avec la qualité du parse : un symbole a pu se **déplacer** vers un fichier jamais visité, et supprimer son ancien concept le perdrait sans remplacement. Une traversée **complète** dont certains fichiers se sont mal extraits est au contraire exactement connue : chaque fichier visité a un `FileStatus` enregistré, donc l'élagage est sûr pour les ids possédés par les fichiers `Extracted` et interdit pour ceux des autres — c'est la règle 3 de §6.3, et rien de plus.

---

## 3. Schéma des ids de concepts

C'est la décision la plus lourde du design : un id instable fait churner tout le bundle à chaque commit, ce qui tue l'argument « déterministe et git-diffable ». Les sources de collision ont été **mesurées** sur ce repo (heuristique regex sur les déclarations C# de `src/`), pas supposées.

### 3.1 Forme

**`code/<langage>/<conteneur…>/<nom>`**

```
code/csharp/okf4net/link-scanner              ← le type
code/csharp/okf4net/link-scanner/scan         ← un membre
code/csharp/okf4net/yaml/yaml-value           ← type dans OKF4net.Yaml
code/typescript/web/src/lib/format/format-date
```

Le segment `<conteneur…>` est une **fonction du `LanguageProfile`** : segments de namespace en C#/Java, chemin du module (fichier) en TS/JS, chemin de package en Go/Python.

Le segment `<langage>` coûte un niveau mais évite qu'un `Foo` C# et un `Foo` TypeScript se marchent dessus dans un repo mixte, et rend `code/csharp/` navigable d'un bloc. **Décision prise en connaissance de son défaut** : il fragmente un repo mixte en sous-arbres parallèles.

### 3.2 Les surcharges sont fusionnées

**Un concept par (conteneur, nom).** Le corps liste chaque signature avec son propre span. Quatre raisons, par ordre de poids :

1. **Stabilité.** Un suffixe numérique (`validate-2`) est dépendant de l'ordre : ajouter une surcharge renumérote ses voisines et fait bouger des concepts qui n'ont pas changé. La fusion rend l'id insensible à l'ajout.
2. **Coût de résolution quasi nul.** Les 38,7 % d'arêtes ambiguës mesurées au spike sont une ambiguïté **inter-types**, pas inter-surcharges (`Equals` à 7 candidats = 7 types différents). C'est le conteneur que Roslyn tranche, et le conteneur reste dans l'id.
3. tree-sitter ne distingue pas deux surcharges par le nom de toute façon.
4. **C'est rare** : 16 paires (fichier, nom) sur 487 portent plus d'une déclaration (~3 %), dont une partie est du bruit de l'heuristique.

Effet de bord bienvenu : les `partial class` réparties sur plusieurs fichiers fusionnent naturellement — comportement correct.

### 3.3 Trois garde-fous

- **Segments réservés.** `BundleConceptWriter` rejette les concepts nommés `index` ou `log` (ils écraseraient les fichiers propres du bundle) — vérifié dans `src/OKF4net/BundleConceptWriter.cs`. Une propriété nommée `Index` est parfaitement plausible ; on **réutilise** `IsReservedSegment` de `ConceptGenerator.cs` au lieu d'en écrire un second.
- **Collision résiduelle** (casse seule — `Parse` vs `parse` — ou type imbriqué homonyme d'un membre) : départage déterministe par **ordre Ordinal du nom d'origine**, le premier garde le slug nu, les suivants prennent `-2`, `-3`. Ordinal **sur le nom** et non sur (fichier, ligne), pour que le départage survive à un déplacement de fichier ou à un décalage de lignes. Mesuré à **0 occurrence** sur ce repo ; la règle existe pour Go et JS, où c'est courant.
- **Profondeur.** Un type devient à la fois le fichier `link-scanner.md` et le dossier `link-scanner/`. C'est légal, et `IndexGenerator` les liste dans deux rubriques distinctes du parent (document / `Subdirectories`). Conséquence assumée : **un `index.md` par dossier de type** (~170 sur ce repo). Sur un projet Java profond (`com/example/…`), les chemins s'allongent — **à surveiller vis-à-vis de `MAX_PATH` sous Windows**.

### 3.4 Un registre d'ids unique

`UniqueConceptId` maintient aujourd'hui un `usedIds` **local à `Generate`**. Il doit désormais couvrir les **quatre familles** (`overview`, `packages/`, `docs/`, `code/`) dans un seul registre, sinon la règle de départage ci-dessus ne voit pas les collisions inter-familles.

---

## 4. Forme du concept généré

Quatre points de conformité ont été vérifiés dans le validateur avant d'arrêter cette forme ; trois l'ont changée.

### 4.1 Le concept type

```markdown
---
type: C# Method
title: LinkScanner.Scan
description: Scans a concept body for §6 markdown links, returning them in source order.
description_source: doc-comment
resource: https://github.com/jchable/okf4net/blob/main/src/OKF4net/Links.cs#L232-L268
tags: [csharp, method, public]
generated:
  by: okfgen/0.1.0
---

# LinkScanner.Scan

Scans a concept body for §6 markdown links, returning them in source order.

## Signatures

- `public static IReadOnlyList<ConceptLink> Scan(string body)` — `src/OKF4net/Links.cs#L232-L268`

## Calls

- [ConceptLink.Classify](/code/csharp/okf4net/concept-link/classify)
- [YamlMapping.Get](/code/csharp/okf4net/yaml/yaml-mapping/get)

## Calls (unresolved)

- `string.Substring`
- `Enumerable.Where`
```

### 4.2 `description` : une chaîne de sources, pas un LLM

tree-sitter capture le nœud de commentaire précédant la déclaration ; on en extrait le `<summary>` en C#, le JSDoc en TS, la docstring en Python, le doc comment en Go. **C'est l'avantage net sur okf-rs**, qui a besoin de `generate --enrich` et d'un endpoint OpenAI pour remplir ce champ.

Mais la couverture mesurée ici — **92 % sur les 454 déclarations `public` de `src/`** — est un artefact de ce repo (`TreatWarningsAsErrors` impose les commentaires XML). Sur un repo ordinaire c'est l'inverse, donc le repli compte vraiment :

```
IDescriptionSource  (ordonnée, premier qui répond gagne)
  1. DocCommentSource     ← ///, JSDoc, docstring, doc comment Go     [v1]
  2. SignatureSource      ← phrase dérivée de la signature            [v1]
  3. LlmDescriptionSource ← endpoint OpenAI-compatible                [hors v1]
```

**Le LLM est hors v1 parce que le mécanisme dont il dépend n'existe pas encore**, pas parce que c'est du travail. okf-rs ne réinterroge jamais une description existante — c'est ce qui l'empêche de churner et de recoûter de l'argent à chaque run. Or `WritePolicy.Update` préserve les **fichiers** que le run ne génère pas, pas les **champs** d'un fichier régénéré : une régénération écraserait une description enrichie.

**Cette préservation de champ est livrée en v1 pour elle-même**, LLM ou pas : sans elle, une description écrite à la main disparaît au `generate` suivant, et le bundle n'est qu'un artefact jetable au lieu d'une base de connaissance éditable. Elle s'appuie sur une clé d'extension `description_source` (les clés producteur inconnues survivent aux allers-retours et ne déclenchent aucun diagnostic) :

| `description_source` | Au `generate` suivant |
|---|---|
| `doc-comment` | re-dérivée — le code reste la source de vérité, un commentaire amélioré se propage |
| `generated` | re-dérivée ; c'est le créneau que le LLM viendra remplir |
| `manual` / `llm` | **jamais écrasée** |

Le LLM devient alors un pur ajout : une implémentation de plus dans la chaîne, zéro modification ailleurs.

### 4.3 `resource` : une URL, décidée par le validateur

Le validateur classe d'abord la valeur (`FrontmatterResourceClassifier.KindOf`), et **la forme relative nue n'est pas résolue contre la racine du bundle** : `Bundle.TryResolveResource` ne combine avec `Root` que la forme *bundle-relative* (`/…`) ; une valeur relative nue est résolue **contre le répertoire du concept** (`Bundle.cs:384`). Un chemin repo-relatif est donc encore moins viable que ne le suggérait le premier jet de ce design : `resource: src/OKF4net/Links.cs` porté par `code/csharp/okf4net/link-scanner/scan.md` serait cherché sous `<bundle>/code/csharp/okf4net/link-scanner/src/OKF4net/Links.cs`, et la profondeur du préfixe parasite **varie d'un concept à l'autre**. Résultat : ~470 warnings `FrontmatterPathMissing`, ce qui rend `validate` inutilisable. Omettre le champ coûte exactement autant de warnings, car `resource` est dans `RecommendedFields` (`Validate.cs:503`).

En revanche le classifieur **court-circuite sur une URL** (statut `Url`, `Bundle.cs:353`) : aucune vérification de chemin, et aucune branche de diagnostic ne traite ce statut (`Validate.cs:475-479`) — donc aucun warning, et on gagne un lien source cliquable avec le span. D'où deux options CLI : `--repo-url` et `--rev`.

`--rev` vaut par défaut le **nom de branche**, pas un sha : un sha ferait churner les 470 concepts à chaque commit. C'est un arbitrage assumé, et il faut l'appeler par son nom : une URL sur une branche mutable est un **lien source**, pas un permalien — elle peut cesser de correspondre au `revision` enregistré sur `overview` (§6.1). Passer `--rev <sha>` explicitement donne un vrai permalien, au prix du churn. L'URL est construite segment par segment avec encodage, jamais par concaténation brute.

Sans `--repo-url`, on retombe sur le chemin repo-relatif et on assume les warnings — documenté comme tel.

> **Corrigé à l'implémentation (2026-09-03).** La phrase ci-dessus est restée vraie pour `packages/` et `docs/`, mais **pas** pour `code/` : cette famille n'émet aucun champ plutôt qu'un chemin repo-relatif, parce qu'omettre coûte exactement le même nombre de warnings (`resource` est un champ recommandé) et que le corps du concept nomme déjà son fichier dans chaque libellé de span `## Signatures`. Les deux familles sans span n'ont pas cet autre pointeur, donc elles gardent le chemin. Et — le vrai défaut, resté invisible à 607 tests — `BuildPackageConcept`/`BuildDocConcept` ne recevaient pas `GenerateOptions` du tout : **avec** `--repo-url`, où chaque concept `code/` obtenait une URL qui résout, ces deux familles écrivaient encore un chemin nu qui manque. Mesuré sur ce dépôt avant correction : 20 des 55 warnings restants. Elles construisent désormais la même URL (sans span, le concept portant sur le fichier entier), via le même `BlobUrl`. Voir `producers/README.md`, « What `--repo-url` changes ».

**Note de conformité amont** : `ROADMAP.md` décrivait cette limitation en disant que le validateur résout `sources[].resource` « relative to the bundle root ». Imprécision corrigée ; l'entrée y a depuis été réécrite, le bloc `sources` en question n'étant plus émis (§4.5).

### 4.4 `generated` : `by` partout, `at` sur `overview` seul

Trois faits vérifiés ont arbitré :

- **La racine du bundle ne peut pas porter le stamp.** §12 : le frontmatter de l'`index.md` racine doit déclarer **uniquement** `okf_version` — toute autre clé est une **Error**. Ce n'était pas une option.
- **Omettre `at` ne coûte aucune capacité du validateur.** La péremption (`ConceptStale`) se calcule sur `stale_after`, une date absolue ; elle ne lit jamais `generated.at`.
- **`at` n'est pas une information par concept.** Les 470 concepts sont générés dans la même passe. Le mettre partout, c'est stocker un fait unique 470 fois — et **chaque régénération réécrirait les 470 fichiers**, si bien que le `git diff` du bundle montrerait 470 horodatages au lieu de ce qui a changé dans le code. C'est exactement la propriété qui rend le bundle utile en revue.

Donc : `overview` porte `generated: { by, at }` + `revision: <sha>` ; les concepts de code portent `by` seul. La valeur de `at` est la **date du commit HEAD** — voir §6.1, qui corrige ce point.

`AutoStampGenerated` est `internal` et désactivé par défaut : `BundleWriter` ne peut pas rajouter un stamp par accident.

### 4.5 Ce que le concept ne porte pas

- **Pas de section `## Called by`.** `Bundle.Backlinks(id)` est public et calculé au chargement ; matérialiser les liens retour dupliquerait une information dérivable et **doublerait le churn** — ajouter un appel dans un fichier réécrirait aussi le concept de l'appelé.
- **Les appels non résolus sont du texte, pas des liens.** Le spike a mesuré que **57,5 % des sites d'appel pointent hors du repo** (BCL, NuGet). Les lier produirait autant de `BrokenLink` — des `Info` seulement, mais assez pour noyer `validate`. En code span, ils restent lisibles et greppables sans polluer le graphe.
- **Pas de bloc `sources`** : il ferait doublon avec `resource` sur 470 concepts. *(2026-09-03 : la règle est désormais tenue par **toutes** les familles. `packages/` et `docs/` émettaient une entrée unique dont le `resource` était la même chaîne que le champ au-dessus — le cas exact que cette règle vise — pour un second warning par concept sur un fait déjà énoncé. Supprimée.)*
- **Les liens sont absolus** (`/code/...`), la forme recommandée par §6.1 — aucune arithmétique de chemin relatif dans le générateur.

---

## 5. Articulation avec les concepts existants

### 5.1 Un concept par namespace/module

On ne peut pas pointer vers un dossier : `index.md` est un fichier **réservé**, pas un concept, donc un lien vers `/code/csharp/okf4net/index` serait un `BrokenLink`. Il faut donc un **concept réel par namespace** — ce que fait aussi okf-rs (ses 16 « Module »). Sur ce repo : **10 namespaces**, coexistant avec leur dossier exactement comme les types (`okf4net.md` + `okf4net/`).

Le rattachement package → namespace est **déductible, mais pas depuis l'arborescence**. « Un `.csproj` possède les fichiers de son dossier » est faux en MSBuild : un projet peut ajouter ou retirer des sources (`Compile Include`/`Remove`), lier des fichiers situés hors de son dossier, hériter d'un `Directory.Build.props`, consommer des sources générées, et se décliner sur plusieurs TFM.

La liste des sources se tire donc du même endroit que les références (§7.2) : l'item MSBuild **`Compile`**, par `(projet, TFM, configuration)`. Deux règles qui en découlent et qu'il faut fixer maintenant :

- **Fichiers liés et sources partagées** : un fichier revendiqué par plusieurs projets est rattaché au premier dans l'ordre `Ordinal` du chemin du `.csproj`, et le concept mentionne les autres. Pas de duplication de concept.
- **Multi-TFM** : les symboles sont l'**union** des TFM, et un symbole absent de certains TFM le signale dans son corps. Un concept par TFM ferait exploser la volumétrie pour une information que personne ne cherche à ce niveau.

`RepositoryScanner` résout déjà les `.csproj` depuis le `.sln` — c'est le point d'entrée, pas la règle d'appartenance.

### 5.2 La colonne vertébrale : un seul niveau vers le bas

```
overview                               → packages/* (9) + docs/*
  packages/okf4net                     → code/csharp/okf4net              (namespace)
    code/csharp/okf4net                → ses types + ses sous-namespaces
      code/csharp/okf4net/link-scanner → ses membres
        …/link-scanner/scan            → (feuille)
```

La règle « un seul niveau » n'est pas cosmétique, c'est du **contrôle de churn** : si `overview` listait les 480 concepts, ajouter un type réécrirait `overview`. Avec un niveau, ajouter un type ne touche que le concept de son namespace. Même logique qu'écarter `## Called by`.

**Deux familles de liens, qui ne se mélangent pas** : la *containment* (cette colonne, descendante, une arête par parent) et les *appels* (`## Calls`, transverses, qui traversent l'arbre). `okf graph` voit les deux ; c'est la section du corps qui dit laquelle est laquelle.

### 5.3 Volumétrie (ce repo)

| Famille | Concepts |
|---|---|
| `overview` | 1 |
| `packages/` | 9 |
| `docs/` | 1 |
| `code/csharp/` — namespaces | 10 |
| `code/csharp/` — types | ~158 |
| `code/csharp/` — membres publics | ~300 |
| **Total concepts** | **~480** |
| + `index.md` générés | ~170 |

### 5.4 Portée par défaut

Plutôt que de coder en dur `src/` — une convention qui n'est que la nôtre : on scanne tout sauf `bin`/`obj`/`node_modules`/`.git`, **la visibilité fait le filtrage**, et **les tests sont exclus par convention** (projet référençant un SDK de test, ou dossier `test`/`tests`/`spec`). Sur ce repo cela retire les ~900 méthodes de `OKF4net.Tests` — sinon le bundle triplerait sans rien apprendre à un agent.

Trois drapeaux pour élargir : `--include-tests`, `--include-internal`, `--no-code`.

---

## 6. Déterminisme, élagage, cache

### 6.1 `generated.at` = horodatage du commit HEAD

Un `at` pris sur l'horloge murale **casse le contrôle CI** : régénérer et comparer octet à octet échouerait toujours, sur ce seul champ — or c'est exactement le contrôle qui empêche un bundle périmé d'être livré en silence.

Donc : **`at` = date du commit HEAD.** Source identique → sortie identique, horodatage compris. Et le stamp devient *plus* informatif : il ne dit plus « à quelle heure la commande a tourné » (que git enregistre déjà au commit du bundle) mais **quel état du code ce bundle reflète**. `revision: <sha>` sur `overview` donne l'exactitude.

**Valeur exacte** : la *committer date* du commit HEAD, normalisée en UTC et écrite en **datetime ISO 8601 avec offset explicite** — `2026-08-31T12:34:56Z`. La spec amont l'impose (§5 : « Every timestamp-valued key in OKF is an ISO 8601 datetime with an explicit UTC offset, for example `2026-06-30T14:00:00Z` »), vérifié le 2026-08-31 contre `okf/SPEC.md`. Le datetime reste parfaitement déterministe : même commit → même valeur.

> **Divergence de la bibliothèque, hors périmètre de ce lot mais à consigner.** `Validate.IsIso8601DateTime` (`Validate.cs:618`) coupe sur `T`/espace et ne valide que la partie date, donc il accepterait aussi bien une date nue — il est plus permissif que la spec. Plus grave dans l'autre sens : `Lifecycle.From` parse `stale_after` avec `DateOnly.TryParseExact(raw, "yyyy-MM-dd")` (`Lifecycle.cs:45`), si bien qu'un `stale_after` **conforme à la spec** (`2026-06-30T14:00:00Z`) est rejeté en `StaleAfterInvalid` (« stale_after is not `YYYY-MM-DD` ») et que la péremption n'est jamais calculée. La bibliothèque exige donc la forme non conforme. À ouvrir comme point distinct sur `src/OKF4net` ; ce lot n'écrit pas de `stale_after` et n'est pas bloqué par là.

Hors dépôt git : repli sur l'horloge. Voir §6.2 pour ce que `--check` fait alors — « comparaison octet à octet » et « champ ignoré » ne peuvent pas être vrais en même temps.

À ne pas confondre avec `--rev` (§4.3) : l'URL de `resource` porte un **nom de branche** (stable), `revision` porte le **sha** (précis). Deux rôles différents.

### 6.2 Les règles de déterminisme

| Source de dérive | Règle |
|---|---|
| Ordre des collections | tri `Ordinal` partout ; **jamais** itérer un `Dictionary`/`HashSet` vers la sortie |
| Séparateurs de chemin | normalisés en `/` |
| Fins de ligne | LF — déjà garanti par `YamlEmitter`, rien à faire |
| Culture | `Ordinal` ; jamais de `ToLower` culture-dépendant (`Slugify` passe déjà par `UnicodeCaseFold`) |
| Chemins absolus | jamais dans la sortie — tout est relatif au repo |
| Horodatage | date du commit HEAD (§6.1) |

Et un verbe pour l'automatisation : **`okfgen generate --check`**, sortie non nulle en cas de dérive.

**Sa définition demande de la précision, parce que la version naïve est contradictoire avec §4.2.** « Régénérer dans un dossier temporaire vide puis comparer les octets » ne peut pas fonctionner : une description `manual` ou `llm` n'existe que dans le bundle existant, donc une génération partie de zéro ne la contient pas, et `--check` serait **rouge en permanence** sur tout concept à description manuelle — exactement les concepts auxquels on tient le plus.

Définition retenue : `--check` **copie le bundle existant** dans un temporaire, y applique le chemin `--update` complet (donc la préservation de champ s'exerce normalement), puis compare **octet à octet** avec l'original. On compare bien la sortie d'une génération réelle, et la préservation fait partie de ce qui est vérifié.

**Champs exclus de la comparaison** — la liste est close et doit être énumérée dans l'aide de la commande, pas laissée à l'implémentation :

| Contexte | Exclusion |
|---|---|
| dépôt git | **aucune** — comparaison strictement octet à octet |
| hors dépôt git | `generated.at` et `revision` sur `overview` seulement (l'horloge murale les rend non reproductibles) |

Hors git, la comparaison n'est donc plus « octet à octet » mais « octet à octet sur une projection dont deux champs sont retirés ». C'est une propriété plus faible ; l'aide de la commande le dit.

**Versions d'outils.** Le déterminisme est garanti **à version d'extracteur fixée**, pas dans l'absolu : une montée de grammaire tree-sitter ou de Roslyn peut changer symboles, spans ou descriptions sur une source inchangée. Deux conséquences : les versions sont **verrouillées** (`packages.lock.json` activé sur les projets `CodeGraph.*`), et elles sont inscrites dans `generated.by` sur `overview` (`okfgen/0.1.0 tree-sitter/x.y.z roslyn/a.b.c`) pour qu'un écart soit interprétable. Une montée de version est une **migration volontaire** : on régénère, on relit le diff du golden, on le commite comme un changement — jamais une dérive subie.

### 6.3 Le vrai défaut à réparer : `Update` n'élague jamais

Vérifié : `WritePolicy.Update` préserve « les fichiers que ce run ne génère pas » et **ne supprime jamais rien**. Pour des concepts de code, c'est un défaut franc : **supprimer une méthode laisse son concept en place pour toujours**, pointant vers du code qui n'existe plus. Un agent qui interroge ce bundle reçoit une réponse *fausse* — pire que pas de réponse.

Correction : **le générateur possède le sous-arbre `code/`** et le réconcilie sous `--update`. Mais un élagage naïf est destructif, parce que « absent de ce run » a deux causes indiscernables : le symbole a disparu, ou **le fichier n'a pas pu être analysé**. Un parse en échec, un fichier illisible, un projet non restauré (§7.2 prévoit alors une dégradation) ou une interruption produisent le même ensemble incomplet — et supprimeraient du contenu valide.

Trois règles, qui tiennent ensemble :

1. **Transactionnel.** La génération va d'abord dans un staging ; le commit vers le bundle et l'élagage n'ont lieu qu'**après un run intégralement réussi**. Un run partiel ou dégradé écrit ses concepts mais **ne supprime rien** et le signale en sortie.

   > **Précision apportée à l'implémentation (Task 11).** « Intégralement réussi » se lit au niveau de la *porte* du run — traversée complète, au moins un fichier tenté, aucun concept en échec d'écriture, dépôt présent — et **pas** « chaque fichier parfaitement extrait » (voir la correction en §2.3 : ce serait `RunStatus.IsComplete`, faux sur du C# ordinaire). La qualité par fichier s'applique candidat par candidat, à la règle 3. Deux points que le texte ne disait pas et que l'implémentation fixe : le manifeste est écrit **en dernier**, après le déplacement du dernier fichier stagé, pour qu'une interruption laisse le manifeste *précédent* plutôt qu'un manifeste décrivant un état jamais atteint ; et le commit déplace les fichiers **un par un** au lieu de renommer le staging par-dessus le bundle, parce que ce renommage exige de supprimer le bundle vivant d'abord — un crash à cet instant détruit aussi les concepts écrits à la main hors du préfixe possédé, ce qu'aucun run suivant ne répare.
2. **Un manifeste, pas un préfixe.** Le run précédent laisse un manifeste des ids qu'il a produits (et des fichiers analysés). L'élagage ne porte **que sur les ids de ce manifeste** — jamais sur un fichier inconnu. Un concept écrit à la main sous `code/` n'est donc pas effacé, il est laissé en place, et un avertissement signale qu'il n'est pas possédé.
3. **Périmètre restreint aux propriétaires ayant réussi.** Si l'extraction a échoué sur un fichier, les ids que ce fichier possédait sont exclus de l'élagage de ce run, même si le reste a réussi.

Concrètement : un **paramètre de préfixe possédé plus un manifeste** sur `IBundleWriter.Write`, plutôt qu'une quatrième `WritePolicy`. Le rapport distingue explicitement « symbole supprimé » de « fichier non analysé ».

Interaction à énoncer franchement avec §4.2 : la préservation de champ protège une description manuelle sur un concept **qui survit**. Elle ne protège pas contre l'élagage — et c'est correct : une description manuelle attachée à un symbole qui n'existe plus est périmée par définition. L'élagage la supprime et le signale.

Effet de bord bienvenu : avec l'élagage, `--update` devient le mode normal de régénération et `--reset` n'a plus de raison d'être dans le flux courant.

### 6.4 Pas de cache incrémental en v1

Le spike mesure **1,2 ms par fichier** ; extrapolé (et donné comme extrapolation), un repo de 5 000 fichiers s'extrait en ~6 s. Ce n'est pas là qu'est le coût.

Le coût est dans **Roslyn**. Or un cache par fichier n'y aide pas : la **résolution est globale** par nature — elle a besoin de tous les symboles pour arbitrer un nom. Un cache par fichier n'économiserait que la moitié bon marché du travail.

Et la fonctionnalité qui a réellement besoin d'un cache — `watch` — n'est pas en v1. Le seam reste ouvert : `ILanguageExtractor` est **par fichier et pur** (contenu → faits), donc un cache s'y ajoutera comme décorateur, sans rien remettre en cause.

---

## 7. Packaging et distribution

Le spike avait tiré deux conclusions de ses mesures de poids. La vérification dans la documentation officielle puis une mesure sur ce repo les ont **toutes les deux infirmées**.

### 7.1 Packages `dotnet tool` RID-specific

Un tool **portable** embarque les natifs de **tous** les RIDs — confirmé, et on ne peut pas contourner en copiant les fichiers à la main : c'est le `deps.json` qui alimente `NATIVE_DLL_SEARCH_DIRECTORIES`, pas le contenu du dossier.

Mais la conclusion « donc pas de `dotnet tool` » tombe : **.NET 10 sait produire des packages tool RID-specific**, et `dotnet tool install` sélectionne le bon. Un package pointeur + un package par RID, **~69 Mo au lieu de 590**.

Trois faits à retenir au moment d'implémenter :

- **Piège de packaging.** `ToolPackageRuntimeIdentifiers` seul fait échouer `dotnet pack` (`NETSDK1047`). Il faut **aussi** `RuntimeIdentifiers`, et pousser tous les sous-packages, pas seulement le pointeur.
- **Coût de compatibilité nul ici.** Ce mode casse durement les consommateurs sur SDK ≤ 9, sans compatibilité descendante — mais `okfgen` n'est pas publié aujourd'hui, et le repo exige déjà le SDK 10+.
- **Ne pas l'appliquer à `okf-mcp`**, qui a de vrais utilisateurs. Vérifié le 2026-08-31 : `src/OKF4net.Mcp/OKF4net.Mcp.csproj` ne déclare **aucun** `RuntimeIdentifier(s)`, donc le changement de comportement .NET 10 ne le touche pas.

**Correction du chiffre du spike : ~69 Mo par RID, pas 12 Mo.** Le 12 Mo supposait de retirer les grammaires inutiles (`verilog` pèse 18 Mo, `razor` 11 Mo), or on ne peut pas retirer des fichiers natifs individuels d'un `PackageReference` sans chirurgie sur le `deps.json`. Le publish self-contained le permettrait — le dossier de l'app y est toujours sondé — mais il rajoute le runtime, donc le bilan est pire. **Réduction à ~40 Mo : suivi documenté, pas promesse v1.**

Pas de Native AOT : Roslyn, `System.CommandLine` et le Generic Host l'excluent, et un pack AOT devrait être construit par OS. **Framework-dependent RID-specific** est la bonne forme. (Le CLI `okf` reste AOT ; c'est sans rapport.)

### 7.2 L'étage Roslyn — question rouverte

> **✅ Section validée par mesure le 2026-08-31.** Le premier jet affirmait cette conclusion sur la foi d'une inférence non testée ; un audit l'a signalé ; le prototype a été écrit, exécuté, puis retiré de l'arbre de travail comme le jetable qu'il est.
>
> **Reproduire la mesure** : le prototype vit dans l'historique au commit `0db6e9a` (`producers/spikes/RoslynCompilationSpike/`). `git show 0db6e9a --stat` le liste ; `git checkout 0db6e9a -- producers/spikes` le restaure. Tout ce dont dépendent les conclusions ci-dessous — la commande MSBuild exacte, le protocole des trois projets, les compteurs d'erreurs — est reproduit dans cette section, de sorte qu'elle se lit sans lui.

**Verdict : oui, une `CSharpCompilation` correcte se construit depuis les requêtes MSBuild, sans `MSBuildWorkspace`.** Zéro erreur sur les trois projets sondés, choisis par difficulté croissante :

| Projet | Items `Compile` | Références | Erreurs |
|---|---|---|---|
| `OKF4net` (autonome) | 40 (2 générés) | 167 | **0** |
| `OKF4net.Mcp` (`PackageReference` + chaîne de `ProjectReference`) | 7 (2 générés) | 213 | **0** |
| `OKF4net.Agents` (`Microsoft.Agents.AI`) | 6 (2 générés) | 186 | **0** |

Coût de la requête MSBuild : 533 à 1194 ms par projet.

Donc `OkfProducer.CodeGraph.Roslyn` ne référence que **`Microsoft.CodeAnalysis.CSharp`** : pas de `Workspaces.MSBuild`, pas de `MSBuildLocator`, pas de BuildHost.

**Correction 1 — la liste de cibles du premier jet était incomplète.** `-t:ResolveReferences` seul renvoie les références mais **pas les sources générées**. Ce repo active `ImplicitUsings` (le défaut du SDK), donc sans `-t:GenerateGlobalUsings -t:GenerateAssemblyInfo` le jeu `Compile` n'a ni `*.GlobalUsings.g.cs` ni `*.AssemblyInfo.cs`, et tout fichier reposant sur un using implicite échoue. La requête exacte :

```sh
dotnet msbuild <proj> \
  -t:ResolveReferences -t:GenerateGlobalUsings -t:GenerateAssemblyInfo \
  -getItem:ReferencePath -getItem:Compile \
  -getProperty:DefineConstants -getProperty:LangVersion -getProperty:Nullable \
  -getProperty:AllowUnsafeBlocks -getProperty:TargetFramework -getProperty:OutputType
```

**Correction 2 — « restauré » ne suffit pas, il faut **construit**.** C'est la correction la plus importante, et elle contredit ce que cette section affirmait. Les `ProjectReference` se résolvent vers `bin/<config>/<tfm>/*.dll`, qui n'existe qu'après une build. Mesuré en simulant un dépôt restauré-mais-non-construit (`--drop-project-refs`) : `OKF4net.Mcp` passe de 0 à **4 erreurs** — `CS0234` sur le namespace `OKF4net.Agents`, `CS0246`/`CS0103` sur `OkfBundleTools`. Les symboles du projet référencé disparaissent entièrement.

Conséquence pour le design : la voie `CompilationReference` — compiler nous-mêmes les projets du dépôt et les lier entre eux depuis les sources — n'est plus une préférence, elle devient **obligatoire** si l'on veut fonctionner sur un dépôt seulement restauré. À défaut, dégradation propre vers `NameMatchResolver` seul. ~~Et l'élagage est désactivé pour ce run (§6.3).~~ **Faux, corrigé à l'implémentation — voir l'encadré en fin de §7.2 : la disponibilité du resolver ne conditionne pas l'élagage.**

**Correction 3 — le paquet Roslyn doit suivre la version de langage du SDK.** `Microsoft.CodeAnalysis.CSharp` 4.14.0 ne connaît pas `LangVersion 14` : `LanguageVersionFacts.TryParse("14", …)` échoue. Le prototype se rabat sur `Preview` et atteint quand même zéro erreur, mais ce repli **change silencieusement la sémantique d'analyse**. Le producer doit soit épingler un Roslyn qui connaît la version du SDK, soit échouer bruyamment — jamais dégrader en silence.

**Ce que le prototype n'établit pas**, et que le resolver de production devra traiter : projets multi-TFM, générateurs de source contribuant des items `Compile`, et des chaînes `Directory.Build.props` autres que celle de ce dépôt. Une seule machine, un seul SDK, une seule configuration (`Debug`).

#### Traité depuis, par mesure (Task 6)

Deux des trois angles morts ci-dessus ont été mesurés puis corrigés dans `RoslynResolver` :

- **Multi-TFM.** La *outer build* d'un projet multi-cible n'a **pas de cible `ResolveReferences` du tout** — MSBuild répond `MSB4057`, la requête échoue entièrement. `MsBuildProjectQuery` réessaie donc avec `-p:TargetFramework=<premier listé>` après un échec (optimiste d'abord : aucun aller-retour supplémentaire dans le cas mono-TFM courant ; un projet non restauré échoue aussi la sonde et c'est son erreur d'origine, plus utile, qui remonte). *Premier listé* plutôt que *le plus récent* : une règle lisible dans le fichier projet, là où « le plus récent » ferait silencieusement changer l'ensemble des symboles le jour où quelqu'un ajoute un TFM.
- **`LangVersion` absente.** `LanguageVersionFacts.TryParse("")` renvoie **false**. Confondre « absente » et « inconnue » ferait donc partir l'échec bruyant de la correction 3 sur n'importe quel projet qui n'épingle simplement pas de version. Or une propriété absente est exactement le cas où le SDK ne passe **aucun** `/langversion` à csc, et `LanguageVersion.Default` est par définition ce que csc fait alors. Seule une version *spécifiée* et non reconnue échoue — et cet échec est **scopé au projet** (`RoslynProjectAvailability.UnknownLanguageVersion`), pas au run : le danger que nomme la correction 3 est un repli *silencieux* vers `Preview`, et ne pas compiler ce projet-là y répond complètement ; faire tomber le run en plus sacrifierait la résolution exacte de tous les autres projets pour rien.

#### Limitation documentée : les générateurs de source Roslyn ne s'exécutent pas

La correction 1 récupère bien les fichiers générés **par le SDK** (`*.GlobalUsings.g.cs`, `*.AssemblyInfo.cs`) — ce sont des items `Compile`. Mais un **générateur de source Roslyn** s'exécute *dans le compilateur* et ne contribue **aucun** item `Compile` : ses membres sont donc purement et simplement absents d'une compilation construite depuis la requête MSBuild.

**Mesuré sur ce dépôt** : `src/OKF4net.Cli` utilise le générateur `System.Text.Json` et produit **6 erreurs** — `CS0534` ×2 (`CliJsonContext` n'implémente pas `JsonSerializerContext.GetTypeInfo(Type)` ni `GeneratedSerializerOptions`), `CS7036` (constructeur `JsonSerializerContext(JsonSerializerOptions?)`), `CS0117` ×3 (`CliJsonContext` ne contient pas de définition pour `Default`) — toutes sur les membres que le générateur aurait émis. Les **sept autres** projets `src/` compilent à zéro erreur.

**Décision : ne pas exécuter les générateurs.** Ce n'est pas un oubli ni un bug à corriger au passage. Les exécuter signifie charger et exécuter des assemblys d'analyseurs **choisis par le dépôt analysé** : du code arbitraire, issu exactement de l'entrée que §2.3 traite comme hostile, à l'intérieur d'un outil dont le métier est de lire des sources non fiables. Échanger la posture de sécurité du producer contre une meilleure résolution sur certains projets n'est pas un arbitrage à faire en silence.

> **Correction apportée à l'implémentation (remédiation vague 2b) : la posture à laquelle ce refus faisait appel n'existe pas, et n'a jamais existé.** `MsBuildProjectQuery.Run` lance `dotnet msbuild` sur chaque projet, `WorkingDirectory` positionné dans le répertoire du projet lui-même. Une **évaluation** MSBuild *est* l'exécution de logique écrite par le dépôt : `Directory.Build.props`/`.targets`, chaque `Import` qu'ils atteignent, toute cible accrochée à `BeforeTargets="ResolveReferences"`, et une tâche `RoslynCodeTaskFactory` `<Code>` en ligne — tout cela s'exécute sous l'identité de qui lance `okfgen`. C'est une porte **plus large** qu'un générateur de source, ouverte un fichier plus loin, avant que le resolver ne voie le moindre symbole. Ni le ledger, ni le plan, ni ce document ne traitaient MSBuild comme vecteur d'exécution.
>
> **La décision R22 ne change pas** — ne pas exécuter les générateurs reste juste ; c'est sa justification qui était fausse. Formulation honnête : viser `okfgen` sur un dépôt, c'est déjà accepter d'évaluer sa build, donc **ne scanner qu'un dépôt qu'on accepterait de builder**. Ajouter un hôte de générateurs par-dessus étendrait cette exécution de l'évaluation MSBuild jusqu'au processus du compilateur, sans aucun bac à sable — un pas de plus, pas une ligne préservée.
>
> **Levier ajouté : `okfgen generate --no-msbuild`.** Il saute entièrement l'étape Roslyn/MSBuild : **aucun `dotnet msbuild` lancé et aucune logique MSBuild de l'arbre scanné évaluée**, résolution des appels laissée à la baseline par correspondance de noms (une ambiguïté inter-types est alors laissée sans lien plutôt que devinée), et une note le dit dans le rapport du run. **Le défaut ne change pas** : le rendre opt-in dégraderait silencieusement la qualité de résolution de tous les runs existants. Un danger documenté avec un levier vaut mieux qu'une dégradation muette.
>
> **Correction (remédiation vague 2b, round 2) — deux affirmations de l'encadré ci-dessus étaient trop fortes, et une troisième manquait.**
>
> 1. **« aucun processus lancé, rien d'exécuté depuis l'arbre scanné » est faux**, et c'était un *élargissement* d'une phrase déjà correcte (la note du CLI dit « aucun `dotnet msbuild` lancé », qui est juste). `ConceptGenerator` lance `git show -s --format=%cI HEAD` puis `git rev-parse HEAD` à **chaque** run, `--no-msbuild` compris ; `GenerateRun.Execute` lance `git symbolic-ref` **sauf si `--rev` a déjà nommé la ref** (`request.Rev ?? GitRevision.CurrentBranch(...)` court-circuite) ; et `--check` lance un `git rev-parse HEAD` de plus (`BundleDrift.Check`) dans l'arbre scanné, avant la régénération à laquelle il compare. Tous via `GitRevision.RunGit`, `WorkingDirectory` positionné sur le dépôt scanné. **Deux à quatre invocations selon les drapeaux, pas trois** — le round 2 avait écrit « les trois … à chaque run », énumération transcrite depuis `GenerateRun` + `ConceptGenerator` au lieu des sites d'appel, que l'on obtient par `grep -rn "GitRevision\." producers/src --include=*.cs` — lancé, il rend les quatre sites (`GenerateRun.cs:114`, `BundleDrift.cs:288`, `ConceptGenerator.cs:150`, `ConceptGenerator.cs:151`) plus trois lignes de commentaire. Un grep de `RunGit` ne convient pas — la méthode est privée, les appelants passent par les publiques — et un grep de `HeadSha|CurrentBranch` non plus : il manque `ConceptGenerator.cs:150`, qui appelle `HeadCommitInstant`. L'exposition est bien moindre (aucune de ces commandes ne déclenche de hook, de fsmonitor, ni de pager avec stdout redirigé), mais une divulgation de sécurité doit être exactement vraie. Formulation retenue partout : « aucun `dotnet msbuild` lancé, aucune logique MSBuild du dépôt évaluée ».
>
> 2. **« les trois cibles de `Targets` bornent ce qui est *demandé* » est faux.** Mesuré sur cet hôte (SDK 10.0.204) : `dotnet msbuild` applique automatiquement un `Directory.Build.rsp` présent dans le répertoire du projet — le répertoire où la requête s'exécute délibérément — et ce fichier contient des **switches**. Un `-t:Pwn` injecté a fait tourner une cible que le producer n'a jamais demandée (fichier témoin écrit), à côté de son propre `-t:ResolveReferences`. Le dépôt ajoute donc à la requête elle-même, pas seulement à ce qui tourne au passage : il peut la transformer en `-t:Build`. Deux faits atténuants, mesurés eux aussi : un switch de ligne de commande l'emporte en cas de conflit (le `-nodeReuse:false` du producer n'est pas retournable depuis le rsp), et la **conclusion** — « ne scanner qu'un dépôt qu'on accepterait de builder » — reste la bonne atténuation. C'est la borne énumérée qui était fausse, pas la conclusion.
>
> 3. **`--no-msbuild` a un second coût, énoncé nulle part.** La carte de propriété des sources de §5.1 est construite à partir des ensembles d'items `Compile` que cette même requête MSBuild fournit. Sans l'étape, `ownership` reste `null` et `ConceptGenerator.AttributePackages` n'émet **aucun** lien `packages` → namespace : c'est un niveau manquant de la colonne vertébrale de containment, pas une arête manquante — et sous `--update`, les concepts `packages/` précédemment corrects sont écrasés par des versions sans lien. Trois endroits énuméraient le coût du flag comme « des arêtes » ; ils disent maintenant les deux.
>
> Une note distincte, non résolue ici : **`--v:diag` n'est pas un vecteur d'amplification.** Le registre d'échappement proposait d'inonder le `ReadToEnd` non borné via un `-v:diag` injecté par rsp. Mesuré : en mode `-getItem`/`-getProperty` le log console est entièrement supprimé — une requête avec `-v:diag` dans le rsp, et une avec un `Directory.Build.targets` émettant trois `<Message>` de 100 Ko, ont chacune imprimé **344 326 octets**, identiques au run propre. Ce qui amplifie vraiment, c'est le JSON lui-même : un `Directory.Build.targets` de quinze lignes déclarant 10 000 items `Compile` a fait passer la même requête de 344 326 à **10 457 323 octets en 1,1 s**. La lecture de stdout est donc désormais plafonnée (32 Mio, ~70× la plus grosse réponse réelle mesurée sur ce dépôt : 456 364 octets pour `src/OKF4net.Mcp`), en continuant à drainer le tube pour ne pas bloquer l'enfant.

> **Correction (remédiation vague 2b, round 3) — une correction du round 2 était fausse dans l'autre sens, et un rapport de projet affirmait quelque chose de faux.**
>
> 1. **Le décompte `git` du round 2 (« trois, à chaque run ») était lui-même faux**, corrigé en place dans le point 1 ci-dessus. Mécanisme : l'énumération avait été transcrite depuis les fichiers regardés (`GenerateRun` + `ConceptGenerator`) et non depuis les sites d'appel ; c'est `grep -rn "GitRevision\." producers/src --include=*.cs` qui les rend tous les quatre. Formulation retenue : conditionnelle et complète (README, `GenerateRun`), ou sans chiffre du tout (`MsBuildProjectQuery`, aide du CLI).
>
>    **Deux recettes fausses ont été écrites ici avant celle-là, et le mécanisme vaut plus que le résultat.** Le round 3 nommait `grep -rn RunGit producers/src`, qui ne trouve pas `BundleDrift.cs:288` : `RunGit` est privé et les appelants passent par les méthodes publiques. Le round 4 l'a remplacé par `grep -rn "HeadSha\|CurrentBranch" …`, qui n'en rend que **trois** — il manque `ConceptGenerator.cs:150`, qui appelle `HeadCommitInstant`. Cette seconde erreur est passée parce que le grep avait été **consigné avec un bloc élidé** (« déclarations/doc »), ce qui faisait ressembler une sortie à trois sites à une sortie à quatre. D'où la règle, plus forte que « consigner le grep » : **le grep consigné est l'artefact, et il doit l'être intégralement — l'élision est précisément où l'erreur se cache.**
>
> 2. **`--max-file-size` : la qualification n'avait été propagée qu'à un seul endroit.** Le round 2 avait corrigé la ligne du README ; le texte d'aide (`okfgen generate --help`), la doc du paramètre `MaxFileBytes` et les deux tableaux de ce document disaient toujours « ignoré et compté » sans réserve — faux de la moitié Roslyn de l'étape code, qui laisse tomber l'item **silencieusement**. Les quatre sont corrigés, et **la clause qualifiante est désormais épinglée par un test** (`CliTests.The_help_keeps_the_clauses_that_make_its_two_hazard_sentences_true`) : rien dans ce dépôt n'épinglait le texte d'aide, alors que trois phrases fausses de cette branche y vivaient. Précédent suivi : `BundleDrift.CheckDescription`.
>
> 3. **CORRIGÉ — un projet dont la porte a refusé tous les fichiers était rapporté `Compiled`.** `CSharpCompilation.Create` sans arbre de syntaxe, en `DynamicallyLinkedLibrary`, n'a aucun diagnostic (un `Exe` donnerait CS5001) : la compilation vide passe le seuil « zéro erreur », `RoslynProjectAvailability.Compiled` est rapporté, et `GenerateRun.ReportProjects` n'imprimait rien. Ce n'est pas un trou silencieux mais une **affirmation fausse** sur le run — le resolver ne possède aucun de ses fichiers, donc la baseline par correspondance de noms les porte exactement comme ceux d'un projet en échec. Le test « rapporté `Compiled` mais ne possède aucun de ses propres items `Compile` » est calculable dans `GenerateRun` seul (`RoslynResolver.QueriedProjects` porte les `CompileFiles`, `Owns` répond pour les fichiers), sans canal supplémentaire depuis `CompilationFactory`. Épinglé en données pures, sans MSBuild ni disque.
>
> 4. **ENREGISTRÉ, non corrigé — `CompilationFactory.cs:190-192`, hors périmètre de ce round.** `reference.AssemblyPath` est le `FullPath` de l'item `ReferencePath`, contrôlé par le dépôt, et le seul chemin issu du JSON MSBuild qui ne passe pas par `FullPath()`. `File.Exists` rend inoffensives toutes les formes *malformées*, mais pas un **non-assembly existant** : `<ReferencePath Include="$(MSBuildProjectFullPath)"/>` dans un `Directory.Build.targets` fait lever `BadImageFormatException` à `MetadataReference.CreateFromFile`. `RoslynResolver.Compile` n'attrape que `UnknownLanguageVersionException` et le filtre du CLI ne liste pas celle-là : c'est un **crash non rattrapé, avec pile d'appels**. Consigné dans la doc de `MsBuildProjectQuery.ReadReferences`, là où la valeur naît.
>
> 5. **ENREGISTRÉ — contrainte d'hôte désormais permanente.** Le Developer Mode ne peut pas être activé sur cette machine (confirmé par son propriétaire), donc `File.CreateSymbolicLink` n'y réussira jamais et les branches `if (overwriteShape)` ne s'y exécuteront jamais : ce n'est pas une mesure en attente, et les fermer exige une autre plateforme. À ne pas surestimer : les formes **jonction** *sont* mesurées ici (`mklink /J` ne demande aucun privilège) ; seul l'écrasement par lien symbolique de **fichier** reste non exécuté.

**Conséquence, assumée et visible plutôt que découverte** : tout projet utilisant un générateur de source (`System.Text.Json`, `GeneratedRegex`, `LoggerMessage`, l'essentiel d'ASP.NET) a une compilation en erreur, `RoslynResolver` se déclare indisponible pour lui (`CompilationHadErrors`), ne le possède pas (`Owns` → `false`), et `NameMatchResolver` le porte au niveau *baseline*. Le **rapport de sortie** le dit à l'opérateur, projet par projet (voir la correction vague 2b ci-dessous : ce n'est pas `IsComplete` qui l'alimente). C'est la dégradation propre — jamais une résolution depuis une table de symboles trouée, qui n'échouerait pas à résoudre les appels mais les attribuerait au mauvais symbole.

> **Correction apportée à l'implémentation (Task 11) : `RoslynResolver.IsComplete` ne conditionne pas l'élagage, et ce document affirmait le contraire à deux endroits (§7.2 correction 2, et cette phrase même).**
>
> L'élagage agit sur une **absence** : un id que le manifeste précédent revendique et que ce run n'a pas produit. Or aucun resolver ne contribue de symbole à `CodeGraph.Symbols` — `CodeGraphBuilder` les construit depuis `ILanguageExtractor` filtré par `FileEligibility.IsInScope`, et un resolver ne décide que du rendu d'un site d'appel (lien `## Calls` ou span de code `## Calls (unresolved)`). Un resolver dégradé ne peut donc **pas** rendre un concept absent, donc pas provoquer une suppression fautive.
>
> Et le conditionner dessus rendrait l'élagage **mort sur ce dépôt même** : `src/OKF4net.Cli` utilise un générateur de source et ne compile pas ici, donc `IsComplete` est faux sur un checkout ordinaire. C'est exactement le piège que `RunStatus.IsComplete` tend une propriété plus loin (voir la correction en §2.3), pour la même raison de forme.
>
> Ce qui conditionne l'élagage : `RunStatus.TraversalComplete` (porte du run) plus le `FileStatus` par fichier (périmètre, règle 3 de §6.3). `IsComplete` du resolver reste ce qu'il a toujours été utilement : la distinction « a tourné et n'a rien résolu » / « n'a pas pu tourner ».

> **Correction apportée à l'implémentation (remédiation vague 2b) : `IsComplete` et `IsAvailable` n'alimentent aucun rapport, et ce document l'affirmait deux fois** (la phrase « `IsComplete` passe à `false`, ce que le **rapport de sortie** doit dire à l'opérateur » ci-dessus, et « rapportée à l'opérateur » juste avant, dont la mention a été retirée).
>
> Vérifié par `grep` sur tout `producers/src` : aucun lecteur de production pour l'une ou l'autre propriété ; les seuls lecteurs sont les tests. Ce que l'opérateur voit vient de `GenerateRun.ReportProjects`, qui itère `RoslynResolver.Projects` et émet **une note par projet non compilé**, en nommant le projet, sa `RoslynProjectAvailability` et le détail. C'est strictement plus d'information qu'un booléen.
>
> Arbitrage retenu : **corriger le document et les commentaires plutôt que câbler une seconde voie**. Câbler `IsComplete` dans le rapport ajouterait une ligne agrégée à côté d'une voie par projet qui fonctionne déjà et qui dit davantage. Les deux propriétés restent : c'est la question qu'un hôte intégrant le resolver pose, et le fixture de `RoslynResolverTests` s'appuie sur `IsComplete` pour prouver que son dépôt scratch a réellement compilé.

**Rayon d'impact — il déborde du projet en échec.** C'est le point le moins intuitif et il doit être écrit noir sur blanc. Un projet qui ne compile pas ne coûte pas seulement la précision *sur ses propres fichiers* :

- **Dans le projet A en échec** : ses fichiers ne sont pas `Owns()`és, donc chacun de ses appels retombe sur la baseline. Attendu.
- **Dans les projets propres qui appellent A** : ils compilent, eux, mais contre le **`bin/`** de A — donc un appel B → A se lie à un symbole de *métadonnées*, pas de source, et n'est pas résoluble exactement. La précision se dégrade sur tout le **cône de dépendances inverses** de A, pas seulement dans A.

Et ce second cas était initialement traité *à l'envers* : un symbole de métadonnées était rendu `Unresolved`, ce qui est juste pour une cible réellement externe (BCL, NuGet — la supposition par nom de la baseline y est fausse et la rétracter est un gain) mais **destructeur** pour une cible qui est un projet du dépôt : le concept existe (l'extracteur a lu la source de A, que Roslyn ait su compiler A ou non) et l'arête `ByName` de la baseline était correcte. Un seul projet en échec faisait donc disparaître des arêtes justes dans tous les projets qui l'appellent.

Les deux cas sont désormais distingués par la **provenance MSBuild** — l'assembly de la cible est remontée à la `MetadataReference` dont elle vient, dont le chemin est cherché parmi les items `ReferencePath` que MSBuild a marqués `ReferenceSourceTarget=ProjectReference` avec un `MSBuildSourceProjectFile` sous la racine du dépôt. Jamais par nom de fichier ou d'assembly : un paquet NuGet peut porter le nom d'un projet du dépôt, et `OutputPath` peut rediriger une sortie n'importe où. Quand la cible est un projet du dépôt non compilé, le resolver **ne dit rien** et laisse la verdict de la baseline en place.

**Chemin futur possible** : un *hôte de générateurs en bac à sable*. C'est cela, et non un `CSharpGeneratorDriver` non gardé, qui lèverait la limitation.

La limitation est épinglée par un test (`A_project_completed_by_a_source_generator_degrades_rather_than_resolving_from_a_hole`, sur une fixture dédiée plutôt que sur `OKF4net.Cli` lui-même) pour qu'elle ne change pas silencieusement dans un sens ou dans l'autre.

**Et `MSBuildWorkspace` n'avait pas à être écarté aussi catégoriquement**, même si la question est désormais sans objet : [roslyn#80127](https://github.com/dotnet/roslyn/issues/80127) est toujours ouverte, mais la difficulté est conditionnée — un outil qui en dépend doit **livrer le BuildHost**, et depuis Roslyn 4.9 `MSBuildLocator` n'est en général plus nécessaire. La formulation « inutilisable pour un outil distribué sur nuget.org » était trop forte.

---

## 8. Tests

Le design fait **trois promesses**, et elles ne se prouvent pas avec le même outil.

| Promesse | Ce qui la prouve | Ce qui ne la prouve pas |
|---|---|---|
| L'extraction est correcte (spans, symboles, appels) | fixtures de source minimales, une par forme, spans assertés exactement | un e2e sur ce repo — trop gros pour localiser une régression |
| La sortie est déterministe (§6) | régénération **octet à octet** d'un bundle golden | des tris assertés à la main |
| Les ids sont stables sous mutation (§3, §5) | un test de **rayon d'explosion** | une assertion sur un id isolé |

### 8.1 Deux dettes à réparer d'abord

- **`BundleValidationRunner` n'a pas d'horloge.** Il appelle `BundleValidator.Validate(bundle)` sans le second paramètre, donc `SystemClock`. Or `IOkfClock`/`FixedClock` sont livrés publiquement pour exactement ça (`src/OKF4net/IOkfClock.cs`, consommés par `Validate.cs`, `Audit.cs`, `AttestationOrchestrator` et 5 resolvers du catalog). Inoffensif aujourd'hui — rien de généré ne porte `stale_after` — mais c'est une dépendance à la date du jour au cœur du seul test qui asserte la conformité de bout en bout. **Threader `IOkfClock` dans `IBundleValidationRunner`** : branchement d'un seam existant, pas un seam nouveau.
- **Le style de test actuel ne passe pas à l'échelle.** Les tests existants sont des assertions de forme sur des objets en mémoire, champ par champ. Personne n'écrit 480 assertions de champ, et surtout elles ne testent pas la propriété qui compte (le churn).

### 8.2 Le golden bundle du producer

Le déterminisme ne se teste honnêtement que par comparaison octet à octet, et `--check` (§6.2) *est* déjà cet outil. Le test se réduit à : un dépôt-fixture versionné, son bundle golden versionné à côté, `--check` en assertion.

**Le piège est culturel, pas technique.** Ce repo a une règle dure : ne jamais toucher `tests/fixtures/` pour faire passer un test. Un golden de producer est un animal différent — il capture **notre propre sortie**, pas celle d'une implémentation de référence, et il est régénérable par construction. Il *doit* être régénéré quand le générateur change intentionnellement : la discipline inverse.

Donc : golden sous **`producers/tests/OkfProducer.Tests/fixtures/`**, avec son propre README énonçant la discipline, **jamais** sous `tests/fixtures/`. Et il porte sur un **dépôt-fixture construit exprès** (~15-20 concepts, une occurrence de chaque forme), pas sur ce repo : un diff de 480 concepts n'est pas relisible, donc pas un test.

### 8.3 Le test de rayon d'explosion

Les décisions les plus lourdes — surcharges fusionnées (§3.2), containment d'un seul niveau (§5.2), `## Called by` écarté (§4.5) — existent **toutes** pour borner le churn. Le test : générer, hasher l'arborescence, muter la source, régénérer, asserter **l'ensemble des fichiers modifiés**.

La table porte sur les **concepts** ; les `index.md` correspondants suivent mécaniquement (`IndexGenerator` réécrit l'index du dossier parent d'un concept ajouté ou supprimé) et l'assertion doit les exclure explicitement plutôt que les compter comme du churn.

| Mutation de la source | Concepts qui doivent changer |
|---|---|
| ajout d'une surcharge | le concept du membre, et lui seul (§3.2) |
| ajout d'un type public | le concept de son namespace + le nouveau type (§5.2) |
| ajout d'un membre privé | **aucun** (§5.4) |
| suppression d'une méthode | son concept supprimé + le concept de son type (§6.3) |
| commit qui ne touche pas le code | **`overview` seul** (§6.1) |

**La dernière ligne mérite d'être énoncée exactement, parce qu'elle a l'air de dire « aucun » et ne le dit pas.** `overview` porte `revision: <sha>` et `generated.at` : un commit qui ne touche que la doc change quand même le sha, donc `overview` est réécrit. C'est le prix assumé de l'exactitude du stamp, et il est borné à **1 fichier sur ~480** — c'est précisément la propriété à asserter. Ce qui doit rester rigoureusement stable, ce sont les ~479 concepts de code. Avec l'horloge murale à la place de la date HEAD, cette même assertion tomberait sur 480 fichiers au lieu d'un.

### 8.4 Roslyn : tester l'ambiguïté, pas la résolution facile

Un test sur un fichier jouet de cinq lignes ne prouve ni le 38,7 % ni le 98,8 % : **l'ambiguïté est inter-types**. Le dépôt-fixture doit reproduire les formes qui la produisent — un même nom de membre porté par N types (`Equals`, `Get`), méthodes d'extension, dispatch d'interface, génériques, et `local_function_statement` (le trou de 1,2 % identifié par le spike).

Forme de l'assertion : **pas** « cet appel se résout bien », mais **les deux taux avec un plancher** (taux de raccrochage, taux d'arêtes résolues). Une montée de version de grammaire ou de Roslyn qui les dégrade échoue alors bruyamment, au lieu de déplacer silencieusement des appels vers `## Calls (unresolved)`. Même discipline que `tools/viewer-security-check/` : un taux avec un seuil, pas un grep de marqueur.

**Et un oracle plus fort que celui du spike.** Le spike comparait au nom, à ±6 lignes près — assez pour un ordre de grandeur, pas pour une assertion de non-régression : il accepte un raccrochage décalé. Le test doit comparer sur l'**offset normalisé** (§2.1), exactement l'identité que le resolver utilise.

**Cas de conversion d'unités obligatoires** (§2.1), chacun un test à part entière, parce qu'un raccrochage faux est pire qu'un raccrochage absent : `var café = Foo();` (non-ASCII avant l'appel), un littéral contenant un emoji, un caractère hors du plan de base, et le même fichier en CRLF.

### 8.5 Ce que xunit ne couvre pas ici

La règle du repo (« un run xunit vert ne dit rien du code que xunit ne peut pas exécuter ») s'applique, mais différemment :

- **À l'inverse du cas viewer**, xunit exécute *vraiment* les grammaires tree-sitter (P/Invoke natif). Les tests d'extraction sont réels, pas des smoke checks sur du texte source.
- **Mais seulement pour le RID de l'hôte de test.** Un run vert sur `win-x64` ne dit rien du chargement des natifs sur `linux-x64` ou `osx-arm64`.
- **Le packaging est entièrement hors de portée** : packages RID-specific, `NETSDK1047`, `dotnet tool install` qui choisit le bon RID — rien n'est atteignable depuis un projet de test.

### 8.6 La contrainte CI

**Décision actée le 2026-08-01 : `producers/` ne rentre pas dans la CI.** Deux conséquences honnêtes :

- La garantie doit être **locale et évidente** : une seule commande dans le README du producer, à lancer avant d'y toucher (`dotnet test producers/OkfProducer.sln`, qui embarque le `--check` sur le dépôt-fixture).
- Le smoke check de packaging par RID **ne peut pas être une garantie** sans CI. Il devient une **étape manuelle documentée au moment de publier**, annoncée comme telle — plutôt que de prétendre une couverture qui n'existe pas.

**Dérive de doc à corriger dans ce lot** : `ROADMAP.md` (section producer) enregistre encore « No CI coverage » comme un follow-up non traité avec deux options ouvertes. Il doit consigner **la décision**, pas la question — sinon chaque lecteur la rouvre.

### 8.7 Benchmark d'acceptation de la recherche — **exécuté, résultat négatif**

Le design n'avait jamais vérifié qu'un bundle de cette taille reste **exploitable**. Mesure faite le 2026-08-31 sur un corpus représentatif construit depuis les vrais symboles de `src/` (395 concepts : 10 curatés, 10 namespaces, 127 types, 248 membres — extraction conservatrice, donc **plus favorable** que les ~480 visés), chargé par `Bundle.Load` puis interrogé par `ConceptSearch.Search`.

| Mesure | Résultat |
|---|---|
| Places curatées dans le **top 5** (ce que l'agent injecte) | **1 / 55** |
| Places curatées dans le **top 20** (ce que `okf_search` renvoie) | **18 / 220** |
| Requêtes sans **aucun** concept curaté dans le top 20 | **5 / 11** |
| Rang du premier curaté sur « bundle » | **#74** |
| Rang du premier curaté sur « concept » | **#83** |
| Rang du premier curaté sur « validation » | #24 |

Sur « bundle », les dix premiers résultats sont des membres d'`OkfBundleTools`, tous à **score 6** (le maximum) : le score est un OU par sous-chaîne plafonné à 6 par terme (`ConceptSearch.cs:94-118`), donc les ex æquo sont massifs, et le départage `ThenBy(Concept.Id)` (`ConceptSearch.cs:50`, comparaison `Ordinal` segment par segment, `ConceptId.cs:317`) fait passer `code/…` avant `docs/…`, `overview` et `packages/…` **systématiquement**.

**Contrefactuel mesuré** — restreindre la portée aux types (147 concepts, membres retirés) : top 5 **1 / 55**, inchangé ; top 20 : 23 / 220 au lieu de 18. Réduire le volume **ne corrige pas** le problème, parce que la cause est l'ordre, pas le nombre.

**Décision prise, et implémentée le 2026-08-31 :** rotation par famille dans le sélecteur partagé (`ConceptSearch.TopDiversified`), branchée dans les points de troncature. Les deux autres issues envisagées — sortir les concepts de code dans un bundle séparé, ou assumer un bundle non cherchable — sont écartées : la première coûte la navigation d'un seul tenant, la seconde rend l'auto-injection de contexte inutilisable.

**Une hypothèse intermédiaire a été essayée et mesurée fausse**, et cela vaut d'être consigné parce que c'est contre-intuitif : diversifier *à l'intérieur* d'une bande de score ne suffit pas. Sur « bundle », un membre généré littéralement nommé `Bundle` matche le terme dans son titre, sa description et son corps et score le maximum (6), tandis que le concept curaté qui répond réellement à la question ne le mentionne que dans sa description (2). Ils sont dans des bandes différentes : aucune rotation intra-bande ne rend le curaté atteignable. La sélection doit donc **traverser les bandes**, en donnant son tour à chaque famille — au prix assumé qu'un concept mieux scoré soit déplacé par un moins bien scoré d'une famille non représentée.

**Résultat, même corpus, avant → après :**

| Mesure | Avant | Après |
|---|---|---|
| Places curatées dans le top 5 | 1 / 55 | **23 / 55** |
| Places curatées dans le top 20 | 18 / 220 | **34 / 220** |
| Requêtes sans aucun curaté dans le top 20 | 5 / 11 | **0 / 11** |
| Rang du premier curaté sur « bundle » | #74 | **#2** |
| Rang du premier curaté sur « concept » | #83 | **#2** |

Le critère d'acceptation est rempli. Le benchmark est devenu un **test permanent** (`tests/OKF4net.Tests/SearchScaleTests.cs`), assorti d'un test de contrôle sur le même corpus : sans diversification, `Take(5)` ne ramène aucun concept curaté — ce qui atteste que le corpus **discrimine** réellement les deux ordres, et donc que le vert des tests au-dessus veut dire quelque chose.

**Correction du 2026-09-01 — il n'y avait pas deux points de troncature, mais trois.** Cette section affirmait que la rotation était branchée dans « les deux **seuls** points de troncature ». C'était faux, et dans le sens le plus gênant : le troisième était le chemin *recommandé*.

`OkfContextProvider` a deux chemins de lecture. Le constructeur V1 (`OkfBundleTools`) tronque à `MaxConceptsInjected` — c'est celui qui avait été corrigé. Le constructeur V2 « scopé » (`IKnowledgeResolver` + `IMemoryStore`), lui, ne tronque pas à un nombre de slots : `ProvideScopedAsync` → `AppendPassages` rend un **préfixe contigu** de la liste de passages jusqu'à épuisement du budget de tokens. C'est une troncature, simplement bornée par un budget plutôt que par un compteur — et le chemin V1 porte un `[Obsolete]` sur `MemoryDirectory` qui pousse explicitement les hôtes vers V2. La correction avait donc atterri sur le chemin déprécié et manqué le chemin recommandé.

`KnowledgeQuery.FairnessQuota` ne couvre pas ce cas : il fait tourner les **sources** du catalogue, pas les familles d'ids à l'intérieur d'une source.

**Mesuré le 2026-09-01, pas supposé.** Corpus §8.7 avec les descriptions générées partageant le vocabulaire curaté (ce que produit réellement okfgen à partir des commentaires de doc), une source de catalogue, options par défaut (`TokenBudget` 2000, part connaissance 0,6) :

| Requête | passages rendus / disponibles | curatés rendus | rang du premier curaté |
|---|---|---|---|
| validation | 83 / 336 | **0** | #333 |
| bundle | 38 / 336 | **0** | #333 |
| concept | 38 / 334 | **0** | #333 |
| yaml | 40 / 335 | **0** | #333 |
| catalog | 38 / 334 | **0** | #47 |
| search | 38 / 336 | **0** | #333 |

Zéro concept curaté injecté sur 6 requêtes larges sur 7 (la septième, `index`, ne ramène que 2 hits en tout et tient entièrement dans le budget). Le chemin scopé est donc corrigé lui aussi.

Il l'est avec une **variante** du sélecteur, `ConceptSearch.TopDiversifiedBy`, et la différence n'est pas cosmétique : `TopDiversified` re-dérive l'ordre des familles depuis les scores, ce qui écraserait silencieusement l'entrelacement inter-sources que `FairnessQuota` vient d'appliquer en amont (vérifié : cela fait rougir `OkfContextProviderFusionTests.A_merged_ranking_with_a_fairness_quota_surfaces_both_sources`). `TopDiversifiedBy` visite les familles dans leur **ordre de première apparition**, donc les deux mécanismes se composent au lieu de se disputer. Sur une liste triée par score les deux ordres coïncident, `ConceptId` comparant segment par segment en ordinal.

La surface mémoire n'est **pas** diversifiée, et c'est délibéré : `FileMemoryStore` concatène une liste classée par tier dans son ordre de lecture, et préfixe les ids par le tier — une rotation par famille y entrelacerait les tiers et écraserait cette précédence au lieu de répartir des slots à l'intérieur.

Enfin, les tests d'acceptation passent désormais par les **types de production** (`OkfBundleTools.Search`, les deux constructeurs d'`OkfContextProvider`) et non plus seulement par `ConceptSearch.TopDiversified` appelé directement : la version précédente restait verte si l'on remettait un `.Take(...)` aux deux points de branchement.

**La revendication §11 doit être lue précisément** : `src/OKF4net` n'est pas modifié dans son comportement — `ConceptSearch.Search` est inchangé, `TopDiversified` est une méthode ajoutée que seuls les consommateurs appellent. Mais le comportement d'`OKF4net.Agents`, lui, change délibérément.

---

## 9. Surface CLI (v1)

Drapeaux ajoutés à `okfgen generate` par ce lot :

| Flag | Défaut | Rôle | § |
|---|---|---|---|
| `--repo-url <url>` | absent | base des URL `resource` de **toutes** les familles ; sans lui, `code/` n'émet rien et `packages/`/`docs/` retombent sur le chemin repo-relatif + warnings assumés | 4.3 |
| `--rev <ref>` | branche courante | ref utilisée dans l'URL des permaliens (jamais un sha par défaut) | 4.3 |
| `--check` | off | régénère dans un temporaire et compare octet à octet ; sortie non nulle si dérive | 6.2 |
| `--include-tests` | off | inclut les projets/dossiers de test | 5.4 |
| `--include-internal` | off | descend sous la visibilité publique | 5.4 |
| `--no-code` | off | désactive l'étage graphe de code (comportement actuel) | 5.4 |
| `--max-file-size <n>` | 2 Mo | plafond par fichier source, appliqué par les deux moteurs ; au-delà, l'extracteur ignore **et compte** (run partiel), la porte Roslyn laisse tomber l'item `Compile` **silencieusement** | 2.3 |

`--update` conserve son nom mais change de sémantique sur `code/` (élagage, §6.3).

**En HEAD détaché**, il n'y a pas de nom de branche à mettre dans l'URL : `--rev` devient alors obligatoire pour obtenir des permaliens. À défaut, `--repo-url` est ignoré et on retombe sur les chemins repo-relatifs et leurs warnings (§4.3) — jamais sur un sha implicite, qui ferait churner les 470 concepts au commit suivant.

### 9.1 Le rapport de complétude — ajouté en remédiation vague 3

Chaque `generate` **qui atteint l'étape de génération** imprime **une ligne** sur stderr, préfixée `run: ` : fichiers **visités** et, s'ils ne se sont pas tous extraits, la répartition par cause ; traversée complète ou non (§2.3) ; projets détectés, projets compilés dans la clôture, et fichiers **couverts** par le resolver exact (§7.2) ; et combien de concepts `code` sont atteignables depuis `overview` (§5.2). Suivent, le cas échéant, les fichiers **entièrement ignorés**, nommés avec leur cause, plafonnés à dix.

**Le caractère inconditionnel est le point** — mais la borne exacte n'est pas « chaque run ». Un run qui lève **avant** la génération imprime `error:` et sort en 1 sans avoir rien rapporté ; c'est atteignable et non théorique (jonction circulaire dans un dépôt sans `*.sln` racine : c'est le parcours récursif `.csproj` du scanner lui-même qui lève). L'invariant qui tient est : **tout run qui atteint l'étape de génération, et donc tout run qui sort en 0** — le rapport est émis *avant* l'écriture, si bien qu'un run dont l'écriture a échoué a tout de même dit ce que son analyse avait trouvé. Cela posé : toutes les autres déclarations de ce producer sur lui-même sont des `note:` conditionnées à leur propre déclencheur, et chaque déclencheur couvre un sous-ensemble différent de ce qu'un run peut laisser de côté ; un run sans note est donc indiscernable d'un mécanisme qui n'a pas tiré. Rien n'agrégeait. C'est le constat agrégé de la revue finale — *« le bundle sous-décrit silencieusement l'API, et le producer n'a aucune déclaration à l'exécution de ce qu'il a laissé de côté »* — et il ferme trois trous nommés là-bas : `RunStatus` calculé puis jeté (§2.3), l'étage Roslyn court-circuité sans `else` sur un dépôt sans `.csproj` (§7.2), et la colonne vertébrale §5.2 cassée à 100 % sans qu'aucune note ne tire.

**« Visités », pas « lus » — corrigé en remédiation round 2.** Le compte vient de `RunStatus.Skipped`, qui enregistre *l'issue de chaque fichier tenté, y compris ceux extraits proprement*, et cinq des sept valeurs de `FileStatus` signifient que le fichier n'a **pas** été lu. La clause disait « read » : sur un run `--max-file-size`, elle imprimait `1 source file(s) read: 1 skipped, over --max-file-size` — elle prétendait avoir lu le fichier qu'elle venait de dire n'avoir pas lu, sur précisément le run dégradé pour lequel la ligne existe, et dans le sens qui flatte le run.

**« Couvert », pas « résolu exactement » — même nature d'erreur.** `RoslynResolver.Owns` est un prédicat par *fichier* qui signifie seulement « appartient à un projet compilé sans erreur ». À l'intérieur d'un fichier couvert, un site d'appel que Roslyn ne parvient pas à *raccrocher* est omis de `Resolve` et le verdict par correspondance de nom subsiste : des arêtes name-matched vivent donc aussi dans les fichiers « couverts », et l'ancienne formulation (« resolved exactly for X … by name matching for the other N ») affirmait une partition qui n'existe pas. Le raccrochage est à 100 % sur ce dépôt aujourd'hui, donc l'erreur vive est quasi nulle ; la sur-affirmation était héritée de `RoslynProjectReport`.

Deux chiffres ne sont **pas** pris sur parole : la fraction de couverture est demandée au resolver fichier par fichier (`RoslynResolver.Owns`), donc un projet rapporté `Compiled` dont tous les items `Compile` ont été refusés avant la construction de la compilation compte zéro fichier couvert ; et l'atteignabilité est parcourue sur les corps réellement produits avec `LinkScanner` **et `ConceptLink.Resolve`** — le scanner *et* le resolver du validateur — plutôt que redérivée de la règle §5.1, donc un lien que ce producer a écrit et que le scanner ne voit pas compte comme absent ici exactement comme pour `okf validate`. Ce dernier compte porte sur l'**atteignabilité**, pas sur la containment : le parcours suit tout lien absolu, `## Calls` compris, donc la clause dégradée dit « nothing reaches the others from `overview` » et jamais « nothing links down to them ».

**`--no-code` a sa propre ligne, et elle n'affirme plus que le reste du bundle est intact** — elle le faisait, et c'était faux. Sans graphe, le générateur prend sa branche `CodeFamily.Empty`, donc `AttributePackages` (qui porte la seule note qui mentionnerait la perte) n'est jamais appelé, et chaque concept `packages/` est écrit sans lien de containment vers ses namespaces. Sous `--update` cela écrase les liens qu'un run précédent avait écrits, tandis que les concepts `code/` sont conservés — laissant exactement la famille `code` inatteignable que la dernière clause de la ligne ordinaire existe pour signaler. Même danger que `--no-msbuild` ; et `--check --no-code` est refusé alors que `--update --no-code` ne l'est pas.

**Ce que la ligne ne dit toujours pas.** Elle énonce ce que le run a *visité* et comment il a *résolu* — jamais ce qu'il a choisi de ne pas *émettre*. Un fichier lu intégralement, extrait proprement et couvert par le resolver exact ne produit toujours aucun concept pour ses indexeurs, ses opérateurs, ses opérateurs de conversion ou ses membres d'énumération : ce sont des déclarations que l'extracteur ne produit pas. Une ligne parfaitement saine est donc compatible avec un bundle qui sous-décrit l'API, et ce rapport ne ferme que **partiellement** le constat agrégé cité plus haut. Non clôturable ici : le producer ne peut pas compter ce qu'il n'a jamais extrait, donc la dimension par symbole exige d'abord que l'extracteur déclare ses propres omissions — inventer un nombre ici serait exactement le défaut que cette ligne existe pour clore. Dit à voix haute dans `producers/README.md`, à côté de la ligne elle-même.

Sur **stderr** et pas stdout : stdout porte le résultat du run, que lit une garde CI ; et le rapport ne doit rien changer à ce qui atterrit dans le bundle, ce qu'un flux ne peut pas faire (`--check` compare des octets de bundle).

---

## 10. Hors scope v1

- **Enrichissement LLM des descriptions** — le seam (`IDescriptionSource`) et son prérequis (préservation de champ) sont livrés ; l'implémentation ne l'est pas (§4.2).
- **Cache incrémental et `watch`** — le seam reste ouvert (§6.4).
- **Réduction du poids à ~40 Mo par RID** — suivi documenté (§7.1).
- **Historique de génération dans `log.md` (§9 OKF)** — pas de détection de changement à y consigner tant que l'élagage n'a pas tourné en vrai.
- **Résolveurs SCIP/LSIF** — écartés au profit de l'approche à deux seams, rebranchables plus tard comme `ISymbolResolver` supplémentaire.
- **Langages au-delà de C#** — l'architecture est multi-langage par construction (profils), mais seul le profil C# est en v1, avec Roslyn. Un second profil servirait de test de la généralité du seam, pas d'objectif de couverture.

---

## 11. Contraintes respectées

- **Zéro modification de `src/OKF4net`.** Les arêtes sont des liens markdown §6, la containment aussi ; aucun champ inventé dans la spec OKF. **Nuance à ne pas gommer** : la bibliothèque n'est pas modifiée, mais §8.7 montre que le comportement d'un *consommateur* (`okf_search`, `OkfContextProvider`, dans `OKF4net.Agents`) se dégrade nettement. « Zéro modification » ne vaut pas « zéro impact », et l'issue 1 de §8.7 consisterait précisément à modifier `OKF4net.Agents`.
- **`okf graph` fonctionne d'emblée — à l'exécution.** `CmdGraph` (`OkfCli.cs:641`) n'a aucune borne : un arc par lien, pour tous les concepts. À ~480 concepts, c'est ~480 arcs de containment plus ~900 arcs d'appel, sans filtre disponible. La commande tourne ; le rendu global n'est pas exploitable. Un filtre (par préfixe d'id, par profondeur) est un suivi à ouvrir sur le CLI, hors périmètre de ce lot.
- **Fidélité spec** : liens absolus (§6.1), `index.md` racine sans clé additionnelle (§12), `resource` conforme (§6.2), `description` champ recommandé (§11).
- **Règle de dépendances** : `producers/` est explicitement hors du périmètre zero-dependency ; `OkfProducer.Core` reste néanmoins sur `OKF4net` seul, et les dépendances lourdes vivent dans deux projets séparés (§2.2).
- **`producers/` reste hors `OKF4net.sln` et hors CI** (§8.6).
- Conventions de style héritées du `Directory.Build.props` racine ; en-tête SPDX LGPL-3.0-or-later sur chaque nouveau fichier.

---

## Annexe A — Résultats du spike

> **⚠ Provenance cassée — à réparer avant le plan.** Ce document citait « branche `worktree-spike-treesitter-dotnet`, commit `79ade6a` » comme source de ces chiffres. Vérifié le 2026-08-31 : `git ls-tree 79ade6a` ne contient **rien** du spike, et `git status` dans ce worktree donne `?? spike-roslyn/`, `?? spike-treesitter/`, `?? spike-errcheck/` — les trois répertoires sont **non suivis**. `79ade6a` n'est que le commit de base du worktree. Les chiffres ci-dessous ne vivent donc que dans un répertoire de travail non versionné, et **personne ne peut les reproduire** : une re-mesure indépendante a donné 38,0 % au lieu de 38,7 % et 1,46 ms/fichier au lieu de 1,2 ms, sans qu'on puisse arbitrer. Correction : commiter le spike (ou le rejouer et enregistrer la sortie) avant que le plan ne s'appuie dessus. Les chiffres sont conservés ici comme **ordres de grandeur**, pas comme mesures citables.

| Question | Ordre de grandeur | Solidité |
|---|---|---|
| `win-x64` disponible ? | Oui — `TreeSitter.DotNet` 1.3.0, 9 RIDs, ~30 grammaires précompilées | confirmé indépendamment |
| Poids du tool portable | ~590 Mo, 257 fichiers natifs pour 9 RIDs | confirmé indépendamment (616 923 604 octets) |
| Vitesse d'extraction | 1,2 à 1,5 ms par fichier C# | mesures divergentes, même ordre |
| Spans | exacts, vérifiés à la main sur `src/OKF4net/Links.cs` | vérification manuelle, non automatisée |
| Raccrochage Roslyn ↔ tree-sitter | ~98,8 % | reproduit, mais **oracle faible** (comparaison au nom à ±6 lignes) |
| Arêtes internes indécidables sans Roslyn | 38 à 39 % | reproduit comme calcul |
| Sites d'appel sans déclaration connue dans le dépôt | ~54 à 58 % | **la formulation « hors du dépôt » était abusive** : la mesure dit « sans déclaration connue », ce qui inclut ce que l'extraction a manqué |

**Trois conclusions du spike infirmées** (voir §7) : le poids cible de 12 Mo (irréaliste, ~69 Mo par RID) ; la nécessité d'une dépendance MSBuild (la question est rouverte, §7.2) ; et l'explication de ses erreurs de compilation, qui reste une hypothèse non testée puisque le spike n'a jamais utilisé les références résolues.
