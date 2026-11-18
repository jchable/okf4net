# Design — Recherche par passages (barreau 4)

Date : 2026-11-18
Statut : **conception seule, non planifiée.** Ce document fixe les réponses aux
risques identifiés et nomme les décisions encore ouvertes, pour que
l'implémentation parte d'une conception plutôt que d'un problème ouvert.
Prérequis bloquants : §6.
Design lié : `2026-11-18-okf-passages-design.md`, qui livre le découpeur
réutilisé ici.
Revue externe : Codex (`gpt-6-astra`), passes 1 et 2, intégrées. Plusieurs prémisses
de la première version étaient fausses et ont été corrigées (§7).

## 1. Le besoin

Dans l'échelle de l'article de David R Oliver
(« The Way Up Is a Library », 2026-08-01 : dossier → `index.md` → index de
recherche → property graph → bibliothèque fédérée), le barreau 4 est la
recherche **au niveau du passage**. On découpe chaque concept par heading, on
score les passages, et on renvoie un extrait avec son fil d'Ariane.

Le gain attendu porte sur le contexte plus que sur la pertinence, et il ne
touche pas les deux surfaces de la même façon :

- **`okf_search`** n'envoie déjà au modèle qu'une ligne de métadonnées et un
  extrait d'**une ligne** par concept (`OkfBundleTools`, rendu des résultats).
  Le gain est un extrait **mieux choisi** : la section qui répond, avec son fil
  d'Ariane, plutôt que la première ligne qui contient un terme.
- **`OkfContextProvider`** lit davantage, puisqu'il injecte du contenu de
  concept dans un budget. C'est là qu'un passage pertinent à la place d'un
  concept entier économise vraiment du contexte.

## 2. L'état actuel

`ConceptSearch.ScoreConcept` fait de la **présence binaire par champ** : titre
×3, tags et description réunis en **un seul champ** ×2, body ×1, sommé sur les
termes. Plusieurs occurrences d'un même terme dans le body ne comptent qu'une
fois, et il n'y a pas de normalisation par longueur.

`ScoredConcept(Concept, int)` est de l'API publique du cœur. `Search` est
appelée directement par `OkfBundleTools` (helper partagé
`ScoreConceptsForAsync`, utilisé aussi par le chemin du contexte),
`FileMemoryStore` et `OkfBundleKnowledgeSource`. `OkfContextProvider` passe
par les tools. `okf-mcp` est un consommateur indirect des tools. `Excerpt` est
la première ligne non vide, après pliage des caractères de contrôle et bidi et
trim, qui contient un terme en sous-chaîne. `OKF4net.Catalog.KnowledgePassage`
est un résultat à granularité concept (identité, titre, extrait, score, chemin,
signaux). Il ne contient ni body ni objet `Concept`.

## 3. Les risques et les réponses

### 3.1 Un changement de score subi par tout le monde → une API ajoutée

`ConceptSearch.SearchPassages(…)` est ajoutée et renvoie des `ScoredPassage`.
**`Search` reste inchangée**, et aucun consommateur ne bouge sans le choisir
explicitement. Le chemin le plus sensible est `OkfContextProvider`, qui
**dépense un budget de tokens** en descendant la liste classée : changer le
classement, c'est changer ce que l'agent voit. `okf_search` est exposé à des
agents via `okf-mcp`, sans relecture humaine garantie. Il n'est donc pas « sans
risque ».

**Piège** : `OkfBundleTools.ScoreConceptsForAsync` est partagé par
`okf_search` et le chemin du contexte. Le modifier ferait migrer les deux à la
fois. Chaque migration se fait donc à son point d'appel, pas dans ce helper.

Ordre de migration : `okf_search` d'abord, puis le fournisseur de contexte. Le
catalogue reste sur `Search` tant qu'on n'a pas décidé autrement. La règle
« un seul scorer » de `CLAUDE.md` est préservée : `SearchPassages` vit dans
`ConceptSearch` et n'est forkée dans aucun consommateur.

### 3.2 Les statistiques de corpus → **décision ouverte, bloquante**

BM25 a besoin des fréquences de documents et de la longueur moyenne : le score
cesse d'être une fonction pure d'un concept.

La première version proposait un cache par image
(`ConditionalWeakTable<Bundle, PassageIndex>`) « sans invalidation », au motif
qu'un `Bundle` serait figé après chargement. **C'est faux.** `Bundle.Concepts`
expose la `List<Concept>` réelle, et `Frontmatter.Set` comme `AsMapping()`
permettent de modifier le titre ou les tags d'un concept chargé. Seul
`BundleHandle` remplace l'image à chaque rechargement réussi (et garde
l'ancienne si le rechargement échoue). Un index mis en cache peut donc devenir
faux sans aucun rafraîchissement.

Options à trancher avant toute implémentation :

1. **Établir une vraie frontière immuable** sur l'image (collections en
   lecture seule, frontmatter figé après chargement). C'est la solution
   propre, mais elle touche l'API publique du cœur : rupture à arbitrer.
2. **Indexer une copie** des données nécessaires (texte des passages, titre,
   tags) prise à la construction. L'index reste juste vis-à-vis de lui-même,
   mais il peut diverger d'une image modifiée après coup. Ce comportement est
   alors documenté.
3. **Ne pas mettre en cache** : reconstruire l'index à chaque recherche. C'est
   O(bundle) par requête, à juger sur les baselines (§6).

Quelle que soit l'option, s'il y a un cache, deux propriétés sont exigées, et
le mot `Lazy` ne suffit à garantir ni l'une ni l'autre :

- **une seule construction réussie par clé, même sous appels concurrents** :
  la fabrique d'une `ConditionalWeakTable` peut s'exécuter plusieurs fois.
  `Lazy<T>` en mode `ExecutionAndPublication` sérialise bien la construction,
  mais il **conserve** l'exception d'un échec ;
- **un échec n'est pas conservé** : le mode `PublicationOnly`, qui ne garde pas
  l'exception, autorise en revanche plusieurs constructions concurrentes.

Le protocole est donc explicite : une entrée `Lazy` en `ExecutionAndPublication`,
et, en cas d'échec, son **remplacement conditionnel** (on ne retire l'entrée
que si c'est encore celle qui a échoué), pour que l'appel suivant recommence.
Les tests couvrent les courses entre échec, nouvelle tentative et publication
réussie.

**La clé** inclut la configuration de découpage : la valeur du budget, et
l'identité de la mesure. Une fermeture de delegate peut garder la même identité
tout en changeant de comportement. La recherche n'accepte donc en cache que la
mesure par défaut, ou une mesure que l'hôte déclare pure (documenté). Plusieurs
configurations sur une même image font plusieurs index : leur nombre est borné
(§6).

### 3.3 Le frontmatter hérité par chaque passage → une clé secondaire

Si chaque passage héritait du ×3 du titre, un H3 profond battrait le concept qui
traite vraiment du sujet. Un bonus additif « petit » ne suffit pas : il domine
n'importe quel score BM25 inférieur à lui. Proposition : le BM25 du passage
(avec un bonus pour ses propres headings) est la **clé primaire**, et la
correspondance du titre ou des tags du concept n'est qu'une **clé secondaire**
qui départage les égalités. Elle ne peut pas faire passer un passage sans
correspondance locale devant un passage qui en a une.

**Décisions ouvertes**, à trancher sur un jeu de requêtes de référence écrit
avant le code (requête, passage attendu en tête) :

- la tokenisation des termes (`customer_id`, `CustomerId`, `customer id`) ;
- la normalisation Unicode (accents composés ou décomposés) ;
- les termes répétés dans la requête ;
- les égalités et le corpus sur lequel portent les statistiques.

Le jeu de requêtes inclut aussi des réponses réparties sur plusieurs sections.

### 3.4 Remonter des passages aux concepts → le maximum, avec ses limites

Pour un appelant qui veut encore des concepts, le score d'un concept est le
**maximum** de ses passages. Avec la somme, un long document gagnerait par
simple volume. Le maximum a deux limites assumées : un concept qui a plus de
passages a plus de chances d'en avoir un qui score bien, et une réponse
répartie sur plusieurs sections n'additionne plus ses correspondances. Le jeu
de requêtes de référence (§3.3) mesure ces deux effets.

### 3.5 La famine des fenêtres de résultats → **décision ouverte**

`TopDiversified` est défini sur les concepts. Vingt passages d'un même concept,
c'est la famine qu'il existe pour empêcher, un niveau plus bas.

La première version proposait d'enchaîner deux appels à `TopDiversifiedBy`
(par concept, puis par famille d'id). **Cela ne tient pas.** `TopDiversifiedBy`
n'a pas de quota k : son troisième argument est le nombre total de résultats.
Et deux rotations successives ne garantissent pas les deux diversités à la
fois : sur `y/a:1, y/a:2, y/a:3, x/b:1, x/c:1, x/d:1`, la fenêtre de trois
après les deux passes contient deux passages de `y/a`, alors que deux autres
concepts attendent. Une troncature dès la première passe peut aussi éliminer
une famille.

À définir avant le code :

- la propriété voulue **sur la liste finale** (par exemple : aucun concept n'a
  un deuxième passage tant qu'un concept non représenté a un résultat) ;
- la priorité entre diversité par concept et par famille ;
- le moment de la troncature.

Cela demande probablement un algorithme dédié dans `ConceptSearch`, pas une
composition des deux existants.

**Prérequis** : issue #279. `RoundRobin` revisite les familles épuisées à chaque
tour et devient quadratique quand les familles ont des tailles très inégales.
C'est exactement le cas d'un concept à beaucoup de passages face à des concepts
à un seul. On corrige la brique partagée, pas une copie propre aux passages.

### 3.6 Le texte présenté au modèle → `ModelTextBuilder`

Un passage injecté passe par `Internal/ModelTextBuilder` comme les bodies
aujourd'hui. Trois usages sont à conserver :

- `Block` pour le texte du passage, multiligne, avec probablement un nouveau
  membre délimité plus long que toute séquence de backticks ou de tildes qu'il
  contient ;
- les interpolations pour les métadonnées : titre, id et headings du fil
  d'Ariane ;
- `ContextElement` pour l'enveloppe de contexte.

`ModelFacingBoundaryTests` s'étend aux passages, y compris aux titres, ids et
headings hostiles.

**Règle d'observation** (`CLAUDE.md`, `OkfBundleTools`) : un passage montré au
modèle n'est **jamais** une observation complète du concept. Il ne doit pas
satisfaire le contrôle qui autorise `okf_write_concept` ou
`okf_run_computation`. Une lecture partielle ne débloque jamais une écriture.

### 3.7 Zéro dépendance → BM25 écrit sur la BCL

SQLite FTS5 donnerait BM25 gratuitement, mais c'est une dépendance interdite
dans `src/OKF4net`. BM25 tient en quelques dizaines de lignes : on l'écrit sur
la BCL, en mémoire.

## 4. Réutilisation de l'export

Les passages sont ceux de `PassageSplitter` (`2026-11-18-okf-passages-design.md`,
§3), avec les mêmes règles d'omission (§4.2) et les mêmes ids (§4.3). La
jointure entre un résultat de recherche et un embedding se fait sur
**`sectionId`**, indépendant du budget. Elle est de un à plusieurs : une
section peut avoir plusieurs morceaux dans l'export. L'`id` d'un morceau (`~n`) dépend du
budget et de la mesure, donc un export avec `--max-chars` et une recherche sans
budget ne partagent que leurs `sectionId`. L'attribution des ids et les
omissions vivent dans une unité partagée par l'export et la recherche, pas
dans chacun.

## 5. Tests attendus

- Le jeu de requêtes de référence (§3.3, §3.4), exécuté comme un test : il fige
  le classement voulu.
- `Search` inchangée : tous les tests existants de `ConceptSearch` et de ses
  consommateurs restent verts sans modification.
- Le cache, selon l'option retenue en §3.2 : construction unique sous appels
  **concurrents** (on compte, on ne chronomètre pas), pas de mise en cache d'un
  échec, et le comportement face à une image modifiée.
- La propriété de diversification retenue (§3.5), vérifiée sur la liste finale.
- La frontière avec le modèle : `ModelFacingBoundaryTests` étendu, et un test
  qui prouve qu'un passage injecté ne débloque aucune écriture.

## 6. Prérequis bloquants

- **La décision §3.2** (immuabilité de l'image ou stratégie d'index).
- **Une baseline de l'index de passages, temps et mémoire.** La baseline de
  chargement existe déjà (`BundleLoadPerformanceTests`, 2 001 concepts, #6),
  mais elle exclut explicitement la recherche et la mémoire. Avant d'activer
  l'index par défaut où que ce soit, il faut mesurer sur un gros bundle :
  - le temps de construction et celui d'une recherche ;
  - la **mémoire retenue** par un index et le **pic** pendant sa construction.
    `SnapshotMemory` n'estime que l'image chargée, pas les vues dérivées ;
  - le **nombre d'index** qu'une image peut retenir (une clé par configuration,
    §3.2), et la politique de rétention. Une référence faible au bundle ne
    borne rien tant que le bundle reste vivant.

  Pour l'option « copie » de §3.2, il faut aussi définir sa cohérence avec les
  métadonnées utilisées au moment de restituer un résultat.
- **L'issue #279.**

## 7. Hors périmètre

- La persistance de l'index sur disque, à n'envisager que si la baseline la
  justifie.
- La recherche par passages dans le viewer statique : il n'a pas de processus
  pour exécuter `ConceptSearch` (voir `CLAUDE.md`). Elle viendra par
  l'extension VS Code (#163).
- La recherche sémantique (embeddings) : c'est le rôle du pipeline externe que
  l'export alimente.

## 8. Corrections apportées par la revue externe

| Prémisse de la première version | Réalité |
|---|---|
| Un `Bundle` est figé après chargement | faux : `Concepts` et `Frontmatter` sont modifiables (§3.2) |
| `okf_search` envoie des concepts entiers | faux : une ligne de métadonnées et un extrait (§1) |
| Un humain relit la liste de `okf_search` | faux comme garantie : exposé aux agents via `okf-mcp` (§3.1) |
| `KnowledgePassage` contient un concept entier | faux : résultat à granularité concept, sans body (§2) |
| `TopDiversifiedBy` a un quota k, deux passes suffisent | faux (§3.5) |
| Un petit bonus additif ne fait que départager | faux : il peut dominer, d'où une clé secondaire (§3.3) |
| La baseline de performance reste à créer | celle du chargement existe ; celle de l'index manque (§6) |
