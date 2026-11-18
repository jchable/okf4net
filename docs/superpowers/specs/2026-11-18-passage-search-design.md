# Design — Recherche par passages (barreau 4)

Date : 2026-11-18
Statut : **conception seule, non planifiée.** Ce document fixe les réponses aux
risques identifiés, pour que l'implémentation parte d'une conception plutôt que
d'un problème ouvert. Prérequis bloquant : §6.
Design lié : `2026-11-18-okf-passages-design.md`, qui livre le découpeur
réutilisé ici.

## 1. Le besoin

Dans l'échelle de l'article de David R Oliver
(« The Way Up Is a Library », 2026-08-01 : dossier → `index.md` → index de
recherche → property graph → bibliothèque fédérée), le barreau 4 est la
recherche **au niveau du passage**. On découpe chaque concept par heading, on
score les passages, et on renvoie un extrait avec son fil d'Ariane au lieu du
concept entier.

L'argument porte sur le contexte, pas sur la pertinence. Un agent qui reçoit
quatre fichiers complets pour répondre à une question sur une colonne a brûlé
son budget en listings. `okf_search` et `OkfContextProvider` raisonnent
aujourd'hui en concepts entiers.

## 2. L'état actuel et ses limites

`ConceptSearch.ScoreConcept` fait de la **présence binaire par champ** : titre
×3, tags/description ×2, body ×1, sommé sur les termes. Il n'y a ni fréquence ni
normalisation par longueur : un runbook de 400 lignes et un stub de 5 lignes qui
mentionnent tous deux le terme une fois obtiennent le même score.

`ScoredConcept(Concept, int)` est de l'API publique du cœur, consommée par
`OkfBundleTools` (`okf_search`), `OkfContextProvider`, `FileMemoryStore` et
`OkfBundleKnowledgeSource`. `Excerpt` (la première ligne qui contient un terme)
est consommé séparément. `OKF4net.Catalog.KnowledgePassage` s'appelle déjà
« passage », mais c'est un concept entier plus une ligne d'extrait.

## 3. Les risques et les réponses

### 3.1 Un changement de score subi par tout le monde → une API ajoutée

`ConceptSearch.SearchPassages(…)` est ajoutée et renvoie des `ScoredPassage`.
**`Search` reste inchangée**, et aucun consommateur ne bouge sans le choisir
explicitement. Le risque principal n'est pas `okf_search` (un humain relit la
liste) mais `OkfContextProvider`, qui **dépense un budget de tokens** en
descendant la liste classée : changer le classement, c'est changer ce que
l'agent voit.

Ordre de migration : `okf_search` d'abord, puis le fournisseur de contexte. Le
catalogue (`OkfBundleKnowledgeSource`, `FileMemoryStore`) reste sur `Search`
tant qu'on n'a pas décidé autrement. La règle « un seul scorer » de `CLAUDE.md`
est préservée : `SearchPassages` vit dans `ConceptSearch` et n'est forkée dans
aucun consommateur.

### 3.2 Les statistiques de corpus → un cache par image, sans invalidation

BM25 a besoin des fréquences de documents et de la longueur moyenne : le score
cesse d'être une fonction pure d'un concept. Mais un `Bundle` est une image
figée après chargement (le seul `private set`, `ExecutableResources`, est
rempli pendant le chargement), et un rafraîchissement de `BundleHandle` produit
une **nouvelle** image. L'index de passages se met donc en cache par image
(`ConditionalWeakTable<Bundle, PassageIndex>`), construit au premier usage et
libéré avec l'image. Il n'y a aucune invalidation à écrire, et le chargement ne
paie rien.

À vérifier à l'implémentation : qu'aucun chemin ne mute une image après
publication. Si l'un le fait, ce cache devient faux.

### 3.3 Le frontmatter hérité par chaque passage → un bonus borné

Si chaque passage héritait du ×3 du titre, un H3 profond battrait le concept qui
traite vraiment du sujet. Proposition : score = BM25 du passage (avec un bonus
pour ses propres headings) + un petit bonus de concept quand le titre ou les
tags correspondent. Ce bonus départage, il ne domine pas.

**Décision ouverte** : les poids exacts. Ils se tranchent sur un jeu de requêtes
de référence écrit avant le code (requête, passage attendu en tête), pas au
jugé.

### 3.4 Remonter des passages aux concepts → le maximum

Pour un appelant qui veut encore des concepts, le score d'un concept est le
**maximum** de ses passages. Avec la somme, un long document gagnerait par
simple volume.

### 3.5 La famine des fenêtres de résultats → `TopDiversifiedBy` réutilisé

`TopDiversified` est défini sur les concepts. Vingt passages d'un même concept,
c'est la famine qu'il existe pour empêcher, un niveau plus bas. Deux passes, sans
nouvel algorithme :

1. `TopDiversifiedBy` avec l'id du concept comme famille : au plus k passages
   d'un concept avant qu'un autre concept passe ;
2. la rotation actuelle par famille d'id (premier segment).

### 3.6 Le texte présenté au modèle → `ModelTextBuilder`

Un passage injecté passe par `Internal/ModelTextBuilder` comme les bodies
aujourd'hui, probablement par un nouveau membre de `Block`, délimité plus long
que toute séquence de backticks ou de tildes qu'il contient. Les preuves de
`ModelFacingBoundaryTests` s'étendent aux passages (bundle hostile, sorties lues
comme du CommonMark).

### 3.7 Zéro dépendance → BM25 écrit sur la BCL

SQLite FTS5 donnerait BM25 gratuitement, mais c'est une dépendance interdite
dans `src/OKF4net`. BM25 tient en quelques dizaines de lignes : on l'écrit sur
la BCL, en mémoire.

## 4. Réutilisation de l'export

Les passages sont ceux de `PassageSplitter` (`2026-11-18-okf-passages-design.md`,
§3), avec les mêmes ids (§4.3). Un passage remonté par la recherche porte donc
le même id que dans l'export JSONL : un hôte peut relier un résultat de recherche
à son embedding. Les règles d'omission (§4.2 de l'export) s'appliquent aussi.

## 5. Tests attendus

- Le jeu de requêtes de référence (§3.3), exécuté comme un test : il fige le
  classement voulu.
- `Search` inchangée : tous les tests existants de `ConceptSearch` et de ses
  consommateurs restent verts sans modification.
- Le cache par image : deux appels sur la même image construisent l'index une
  seule fois (on compte, on ne chronomètre pas) ; une nouvelle image en
  construit un nouveau.
- La diversification à deux passes.
- La frontière avec le modèle : `ModelFacingBoundaryTests` étendu.

## 6. Prérequis bloquant

Les **baselines de performance** du ROADMAP (« Performance baselines for large
bundle loads »). On mesure le coût de construction de l'index sur un gros bundle
avant de l'activer par défaut où que ce soit.

## 7. Hors périmètre

- La persistance de l'index sur disque, à n'envisager que si les baselines la
  justifient.
- La recherche par passages dans le viewer statique : il n'a pas de processus
  pour exécuter `ConceptSearch` (voir `CLAUDE.md`). Elle viendra par
  l'extension VS Code (#163).
- La recherche sémantique (embeddings) : c'est le rôle du pipeline externe que
  l'export alimente.
