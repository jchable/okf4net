# Design — `okf passages` : export d'un bundle en passages pour un pipeline d'embedding

Date : 2026-11-18
Statut : design validé, non implémenté. Plan à venir :
`docs/superpowers/plans/2026-11-18-okf-passages.md`.
Design lié : `2026-11-18-passage-search-design.md` (barreau 4, conception
seule), qui réutilise le découpeur défini ici.
Revue externe : Codex (`gpt-6-astra`), passe 1, intégrée (voir §9).

## 1. Le besoin

Une équipe qui fait du RAG sur sa connaissance interne pointe aujourd'hui sa
base vectorielle sur des documents bruts. Un bundle OKF est exactement la
vérité de terrain curée qu'il vaudrait mieux embedder. C'est le conseil
central de l'article
[« Google's Open Knowledge Format (OKF) »](https://pub.towardsai.net/googles-open-knowledge-format-okf-the-markdown-standard-every-ai-engineer-should-know-about-1d14b223f39e)
(Towards AI) : *« use OKF as ground truth before embedding »*.

Rien dans OKF4net ne remet aujourd'hui un bundle à un pipeline d'embedding.
Chaque consommateur réécrit donc son découpage, et il perd deux choses :

1. **La structure.** Un découpage à taille fixe coupe au milieu d'une section
   et sépare un paragraphe de son titre.
2. **Les signaux v0.2.** C'est le point décisif. Une fois le bundle découpé, le
   RAG ne sait plus qu'un concept est déprécié (§5.4), jamais relu (§5.3),
   modifié depuis sa dernière relecture (§5.2) ou périmé (§5.5). Il le remonte
   avec le même poids qu'un concept relu par un humain. C'est ce que v0.2 a été
   conçue pour dire, et c'est ce qu'aucun découpeur markdown générique ne
   restitue.

### 1.1 Ce que ce travail livre

- `okf passages <bundle>` : un JSONL sur stdout, qu'un pipeline Python consomme
  sans .NET (le découplage producteur/consommateur de l'article).
- Un id stable et une empreinte de l'entrée d'embedding par passage, pour ne
  ré-embedder que ce qui a changé d'un export à l'autre.
- Le filtrage à la requête sur la confiance, le statut, l'obsolescence de la
  relecture et l'échéance.
- `PassageSplitter`, brique publique et pure du cœur, réutilisée telle quelle
  par la recherche par passages (barreau 4).

### 1.2 Ce qu'il ne fait pas

Aucun embedding, aucune base vectorielle, aucun tokenizer embarqué, aucune
modification de `ConceptSearch`. Rien ne change au chargement d'un bundle.

## 2. Architecture

Deux unités pures dans `src/OKF4net/`, un verbe qui sérialise.

| Unité | Rôle | Dépend de |
|---|---|---|
| `PassageSplitter` | body (+ titre, budget) → sections ; aucune notion de bundle | la passe de blocs de `LinkScanner` (§2.2), `LfLines.SplitSpans` |
| `PassageText` | composition de `embedText`, troncature de l'en-tête, hash ; une seule implémentation | — |
| `PassageExport` | `Bundle` → `Passage`, au fil de l'eau, en ordre déterministe, projection v0.2 | `PassageSplitter`, `ConceptAudit` |
| `okf passages` | une `VerbSpec` ; JSONL via `CliJsonContext`, écrit ligne à ligne | `PassageExport` |

### 2.1 Approches écartées

- **Tout dans la CLI.** Plus rapide, mais le barreau 4 devrait extraire et
  réécrire le découpeur.
- **`Concept.Passages` calculé au chargement.** Chaque chargement paierait le
  découpage et sa mémoire (recalibrage de `SnapshotMemory`, et même
  `okf validate` paierait). Le `Bundle` serait couplé à une vue dérivée, alors
  que ces vues doivent rester jetables.

### 2.2 La grammaire markdown : la passe de blocs de `LinkScanner`

Le découpeur ne réimplémente **aucune** règle de blocs. `LinkScanner` contient
déjà « l'implémentation unique de skip code » (`CodeFreeLinePairs`, dans
`Links.cs`). C'est une passe ligne à ligne qui suit la stratégie de parsing de
blocs de CommonMark (§5) : citations, éléments de liste et leurs colonnes,
blocs de code délimités et indentés, blocs HTML (§4.6), soulignements setext,
définitions de références. Elle a été vérifiée contre commonmark.js 0.31.2 sur
300 000 bodies aléatoires. Elle relève **déjà** la position de chaque heading
ATX et son texte visible.

On expose une couture interne, sans dupliquer la passe : pour chaque heading
ATX, son index de ligne, son niveau, son texte visible, et le nombre de
conteneurs ouverts à cette ligne. On n'ajoute **aucune** seconde passe.

**Écarté** : extraire les règles privées de blocs de code de
`ComputationExtractor`. Elles ne sont pas conformes à CommonMark : `TrimStart`
sans limite d'indentation, info string d'une fence à backticks non contrôlée.
Codex l'a vérifié sur trois entrées, et je l'ai confirmé dans le code. Leur
écart concerne la détection de `# Computation` (§10). C'est un autre sujet, à
traiter à part. Ce design n'y touche pas.

## 3. Le découpeur — `PassageSplitter`

```csharp
public static class PassageSplitter
{
    public static IReadOnlyList<PassageSection> Split(
        string body, string contextTitle, PassageBudget? budget = null);
}

public sealed record PassageBudget(int MaxSize, Func<string, int>? Measure = null);

public sealed record PassageSection(
    IReadOnlyList<string> Headings,  // pile des headings, vide pour le préambule
    string Text,                     // tranche verbatim du body
    int Part,                        // 0 si la section n'a pas été redécoupée, sinon 1..n
    bool Oversize);
```

`Measure` vaut par défaut le nombre de points de code Unicode (`Rune`), et non
`string.Length`. C'est ce qu'un utilisateur appelle un caractère, et c'est ce
que compte `len()` en Python : un pipeline qui revérifie la taille obtient le
même nombre que nous. Un hôte branche un vrai tokenizer en une ligne
(`Measure: t => tokenizer.CountTokens(t)`, p. ex. avec
`Microsoft.ML.Tokenizers`). La dépendance reste alors chez celui qui connaît
son modèle. Le cœur et `okf` restent zéro-dépendance.

### 3.1 Points de coupure

- On coupe **uniquement sur les headings ATX de premier niveau de document**,
  c'est-à-dire ceux que la passe de blocs (§2.2) relève avec zéro conteneur
  ouvert. Un heading dans une citation ou un élément de liste ne coupe pas, et
  un `#` dans un bloc de code ou un bloc HTML n'est pas un heading. Ces règles
  viennent de la passe : le découpeur n'en a aucune à lui.
- **Les headings setext ne coupent pas** en v1, bien que la passe les
  reconnaisse. C'est une limite documentée, sans perte : la section est
  simplement plus grosse. On l'étendra s'il y a une demande.
- Le texte d'un heading dans `headings` est son **texte visible** tel que la
  passe le relève (contenu ATX, `#` de fermeture et espaces/tabulations
  retirés, CommonMark §4.2).
- La pile des headings : un heading de niveau n dépile tout ce qui est de
  niveau ≥ n, puis s'empile. Un saut de niveau (H1 → H3) est toléré.

### 3.2 Partition exacte

L'entrée est `Concept.Document.Body`, la chaîne produite par
`OkfDocument.Parse`. Ce n'est **pas** une tranche d'octets du fichier : le
parse joint déjà les lignes et retire une première ligne vide. Le découpage
utilise le même modèle de lignes que `LinkScanner` (`LfLines` : `\n` sépare, un
`\r` qui précède est retiré, un `\r` isolé reste dans la ligne), via
`LfLines.SplitSpans`, qui conserve la présence ou l'absence du `\n` final.

Les sections **partitionnent** le body, CRLF réécrits en LF : le préambule
(tout ce qui précède le premier point de coupure, éventuellement vide), puis
une section par point de coupure, depuis sa ligne de heading jusqu'à la ligne
qui précède le point de coupure suivant. **Invariant : la concaténation des
`Text`, dans l'ordre, redonne le body, CRLF réécrits en LF.** Le découpeur ne
jette rien. L'omission de sections est une politique d'export (§4.2), et
l'invariant ne s'applique pas à la sortie filtrée.

**Chaînes mal formées.** Un surrogate UTF-16 isolé est remplacé par U+FFFD
avant tout découpage, mesure ou hash. Le texte émis et le hash portent donc sur
la même chaîne. Seule l'API peut fournir une telle chaîne : le chargement
refuse l'UTF-8 invalide, et le parseur YAML écarte les échappements de
surrogates.

### 3.3 Budget

Le budget s'applique à **`embedText`** (§4.1), puisque c'est lui que le modèle
d'embedding reçoit.

**Blocs.** Une section se découpe en blocs aux lignes blanches **hors bloc de
code et hors bloc HTML**, selon la passe de blocs. Un bloc de code ou un bloc
HTML est atomique. Une ligne blanche s'attache au bloc qui la précède. La ligne
de heading s'attache au premier bloc qui la suit : aucun morceau ne contient un
heading seul.

**Regroupement, en travail linéaire.**

1. On mesure chaque bloc **une seule fois**. On mesure l'en-tête (§4.1) une
   seule fois.
2. On regroupe les blocs de façon gloutonne, dans l'ordre, tant que
   `mesure(en-tête) + Σ mesure(bloc)` reste ≤ `MaxSize`.
3. On **vérifie** chaque morceau émis par une mesure exacte de son
   `embedText`. Si le morceau dépasse et contient plusieurs blocs, on le coupe
   en deux à une frontière de bloc, et on revérifie chaque moitié (dichotomie).
   Avec la mesure par défaut, qui est additive, cette vérification ne coupe
   jamais rien. Avec un tokenizer, qui est presque additif aux frontières de
   paragraphes, elle corrige les rares écarts.
4. Un bloc qui dépasse seul est émis tel quel avec `Oversize = true`. On ne
   coupe jamais au milieu d'un bloc.

Les morceaux portent `Part` 1..n, et l'invariant de partition tient toujours.

**Coût garanti.** Le nombre total de caractères passés à `Measure` est
O(taille du body + taille de la sortie × log k), où k est le nombre de blocs
d'un morceau. Aucun préfixe grossissant n'est jamais remesuré. Ce coût
s'exprime en **caractères mesurés**, pas en appels. C'est ce que comptent les
tests (§6.2). Pour une `Measure` fournie par l'hôte, ce design ne garantit que
le volume de texte qui lui est soumis, pas son propre coût.

`MaxSize` ≤ 0 lève `ArgumentOutOfRangeException`. Sans budget, rien n'est
redécoupé : `Part` vaut 0 et `Oversize` vaut `false` partout, quelle que soit la
taille de la section.

## 4. L'export — `PassageExport`

```csharp
public static class PassageExport
{
    public static IEnumerable<Passage> Enumerate(Bundle bundle, PassageBudget? budget = null);
}
```

L'export est **au fil de l'eau**. On découpe un concept, on émet ses passages,
puis on passe au suivant. Aucune liste de tous les passages n'est matérialisée,
et la CLI écrit chaque ligne dès qu'elle est produite. L'ordre est
déterministe : les concepts par `ConceptId` en ordinal, puis les passages dans
l'ordre du document.

Les signaux v0.2 viennent **d'un seul appel à
`ConceptAudit.Run(bundle, AuditQuery.All)`**, qui couvre tous les concepts
chargés. On lit l'`AuditFinding` de chaque concept et on ne recalcule rien
(règle « l'audit ne se forke pas »). On ne lit jamais `IsStale` ni
`EvaluatedAt`, qui dépendent de l'horloge.

### 4.1 Le modèle `Passage`

| Champ (JSON) | Contenu |
|---|---|
| `id` | identité stable du passage (§4.3) |
| `sectionId` | `id` sans le suffixe de morceau `~n` |
| `conceptId` | `sales/orders` |
| `ordinal` | position du passage parmi ceux **émis** pour ce concept, à partir de 0 |
| `headings` | `["Schema", "Partitioning"]`, `[]` pour le préambule |
| `text` | la tranche verbatim du body |
| `embedText` | en-tête + ligne vide + `text` |
| `embedHash` | `sha256:` + hex minuscule du SHA-256 de `embedText` en UTF-8 |
| `oversize` | `true` si le passage dépasse le budget sans pouvoir être coupé |
| `type`, `title` | frontmatter, `null` si absent |
| `tags` | frontmatter, `[]` si absent |
| `trust` | `AuditVocabulary.Name(TrustTier)` (§5.3) |
| `reviewOutdated` | `AuditFinding.IsReviewOutdated` : contenu modifié (`generated.at`) après la dernière relecture (§5.2) |
| `status` | `AuditVocabulary.Name(ConceptStatus)` (§5.4) |
| `statusKnown` | `false` si le `status` du frontmatter n'est pas reconnu (lu alors comme `stable`) |
| `staleAfter` | `Lifecycle.StaleAfter` en UTC, format fixe `yyyy-MM-ddTHH:mm:ss.fffffffZ`, ou `null` |
| `staleAfterEpochMs` | la même échéance en millisecondes Unix, **arrondie au supérieur**, ou `null` |
| `staleAfterMalformed` | `true` si `stale_after` est présent mais illisible |

Tous les champs sont toujours présents, dans cet ordre. Les noms sont en
camelCase, comme `validate`/`info`/`audit --json` (`CliJsonContext`).

**En-tête de `embedText`** : le titre du concept (ou son id s'il n'a pas de
titre), puis chaque heading de `headings`, joints par ` > `. Pour le préambule,
le titre seul. **Chaque élément est tronqué à 200 points de code** (suivis de
`…`), comme les descriptions du site index du viewer. `headings` et `text`
restent intacts. Sans cette troncature, un heading d'1 Mo suivi de 10 000
sous-sections ferait générer environ 10 milliards de caractères à partir d'un
body d'1 Mo. Avec elle, l'en-tête est borné par la profondeur (au plus 7
éléments, soit ≤ 1 400 points de code et 6 séparateurs). L'en-tête répète la
ligne de heading déjà présente dans `text`, et ce doublon est accepté : la
règle reste la même pour tous les passages, et c'est ce qui permet à un
morceau `Part ≥ 2` de garder son contexte.

### 4.2 Ce que l'export omet

L'omission se décide sur les **sections d'origine, avant le budget** : une
section dont le `Text`, privé de sa ligne de heading, ne contient que des
blancs est omise entière, et le préambule vide aussi. Embedder un heading nu ne
sert à rien. Une section non omise qui est redécoupée émet tous ses morceaux :
d'après §3.3, aucun ne peut être un heading seul.

### 4.3 Identité

`id = <conceptId>#<s1>/<s2>/…[~<Part>]`, un segment par heading de la pile.

- **Slug** (règle GitHub) : minuscules en culture invariante. On garde lettres,
  chiffres, `-` et `_` (lettres Unicode comprises), on remplace espaces et
  tabulations par `-`, et on retire le reste. Un slug vide devient `section`.
  L'alphabet d'un slug ne contient donc **jamais** `.`, `~`, `/` ni `#`.
- **Occurrences** : sous un même parent, la k-ième section (k ≥ 2) dont le slug
  est `s` reçoit le segment `s.k`. La première garde `s`. Comme `.` n'apparaît
  jamais dans un slug, un segment d'occurrence ne peut pas coïncider avec le
  slug naturel d'un autre heading : `# A`, `# A`, `# A-1` donnent `a`, `a.2`,
  `a-1`. Le calcul est linéaire, avec un compteur par (parent, slug).
- **Préambule** : `<conceptId>#`.
- **Morceaux** : le suffixe `~<Part>` est ajouté au dernier segment. `sectionId`
  est l'`id` sans ce suffixe.

**Ce que garantit la stabilité, précisément.** L'`id` d'une section ne dépend
que de son `conceptId`, de la suite des slugs de ses ancêtres et du nombre de
frères précédents qui portent son slug. Les conséquences :

- modifier le **contenu** d'une section ne change aucun id ;
- ajouter, retirer ou renommer une section change seulement les ids des
  sections qui suivent un frère homonyme dont le rang d'occurrence a bougé ;
- un renommage qui ne change pas le slug (`A!` → `A?`) garde l'id ;
- l'`id` d'un morceau (`~n`) dépend en plus du budget et de la mesure. Seul
  `sectionId` est indépendant du budget, et c'est lui qui sert à joindre un
  export et une recherche faits avec des budgets différents.

L'`id` est unique **dans un bundle**. Plusieurs bundles peuvent produire le
même `id` : un consommateur qui en mélange plusieurs doit préfixer lui-même
(nom de bundle ou de source). C'est documenté, sans champ dédié.

### 4.4 Péremption : une échéance, pas un booléen

Un index construit aujourd'hui est interrogé dans trois mois. Un booléen
`isStale` calculé à l'export serait faux le jour où il compte. Le pipeline
filtre donc au moment de la requête, de deux façons :

- **`staleAfterEpochMs`** (nombre) se compare à `now` en millisecondes. C'est
  le seul filtre portable : certaines bases (Pinecone, par exemple) ne
  comparent que des nombres. L'arrondi au supérieur fait qu'une échéance à
  `…:00.0000001Z` n'est jamais vue comme déjà passée à `…:00Z`. C'est le même
  choix que le viewer (`staleAfterMs`).
- **`staleAfter`** (chaîne ISO UTC à largeur fixe, 7 décimales, sans perte)
  sert aux bases qui ont un type date, et à la lecture humaine. Comparée comme
  une chaîne, elle n'est correcte que si `now` est formaté exactement pareil.
  La documentation le dit et donne la construction de `now`.

Dans les deux cas, `null` veut dire « pas d'échéance » et le concept n'est
jamais périmé. Si `staleAfterMalformed` vaut `true`, l'échéance est aussi
`null` mais pour une autre raison : le pipeline peut alors exclure ce concept.
La sortie ne dépend pas de l'horloge, et le golden n'a pas besoin de `--as-of`.

### 4.5 Ce que `embedHash` permet, et ce qu'il ne permet pas

`embedHash` répond à une seule question : **dois-je ré-embedder ce passage ?**
Il change si et seulement si `embedText` change. Il ne couvre pas les
métadonnées. Un concept qui passe à `deprecated` garde le même `embedHash`.
D'où trois règles documentées pour le consommateur :

1. **Mettre à jour les métadonnées à chaque export**, même quand `embedHash`
   n'a pas changé. Sauter la ligne servirait un concept déprécié.
2. **Réconcilier les suppressions** après un export réussi : un `id` absent du
   nouvel export doit être supprimé de l'index.
3. La clé de réutilisation d'un vecteur est `(embedHash, modèle d'embedding,
   configuration)`. L'export ne connaît pas le modèle.

### 4.6 Exclus volontairement

`description`, `lastVerifiedAt`, `lastChangedAt`, `sources`, les liens
sortants. Ils ne sont **pas redondants** avec ce qui est exporté. Ils sont
exclus parce qu'ils servent à d'autres politiques que le filtrage courant d'un
RAG. Une politique comme « relu dans les six derniers mois » ou une citation
qui résout `sources[].id` exige une **jointure avec le bundle** par
`conceptId`, et c'est documenté. Ajouter un champ ne casse pas un consommateur
qui ignore les clés inconnues. Il peut casser un consommateur à schéma fermé :
toute évolution du format est donc notée au CHANGELOG.

## 5. Le verbe — `okf passages`

```text
okf passages <bundle> [--max-chars <n>]
    --max-chars <n>   Re-split sections whose embedText exceeds n characters
```

### 5.1 Sortie

JSONL sur stdout : UTF-8 sans BOM, `\n`, un objet `Passage` par ligne,
sérialisé par `CliJsonContext` (génération de source, AOT) sans indentation, et
écrit ligne à ligne. L'encodeur reste celui des autres sorties `--json`. Un
bundle sans concept et sans fichier ignoré donne une sortie vide et le code 0.

`--max-chars` construit un `PassageBudget(n)` avec la mesure par défaut (points
de code). Une valeur absente, nulle, négative ou non entière est une erreur
d'usage (code 1). La CLI ne propose aucune mesure par tokenizer.

### 5.2 Erreurs

Trois cas, dans cet ordre de priorité :

1. **Erreur d'usage** (verbe, flag, `--max-chars`) : rien sur stdout, message
   sur stderr, code 1.
2. **Échec du chargement** : une erreur d'I/O, un accès refusé ou un UTF-8
   invalide sur un objet font échouer le chargement **entier** (une
   `BundleLoadException`, comportement actuel de `Bundle`, inchangé ici). Rien
   sur stdout, message sur stderr, code 1.
3. **Problèmes récupérables** : les entrées de `Bundle.ParseProblems` (frontmatter
   illisible, objet trop gros). stdout contient tous les passages qu'on a pu
   produire, chaque fichier ignoré est signalé sur stderr
   (`warning: skipped <display path>: <error>`), et **le code vaut 1 si au moins
   un fichier a été ignoré**, y compris quand aucun concept n'a pu être lu.

Le code 1 ne distingue pas ces trois cas. C'est stderr qui les distingue, comme
pour `okf validate`. **Une sortie partielle reste un JSONL valide**, et
`> out.jsonl` laisse le fichier en place malgré le code 1. La documentation
recommande donc d'écrire dans un fichier temporaire et de ne le publier qu'en
cas de succès :

```sh
okf passages bundle > out.jsonl.tmp && mv out.jsonl.tmp out.jsonl
```

**Changement transverse** : le délégué de `VerbSpec.Run` reçoit en plus
`stderr` : `Func<CliArgs, TextReader, TextWriter, TextWriter, int>`. Le
changement est mécanique sur tous les handlers (un délégué central et huit
handlers privés), et aucun handler existant n'écrit sur ce nouveau paramètre.
La preuve que rien ne change à l'affichage, ce sont les goldens existants, qui
doivent rester inchangés.

## 6. Tests

### 6.1 `PassageSplitterTests`

- **Propriété de partition** sur chaque concept de `bundles/` et de
  `tests/fixtures/`, et sur chaque cas construit, y compris avec et sans `\n`
  final et avec un `\r` isolé.
- **Points de coupure**, avec pour chaque cas l'entrée exacte et le découpage
  attendu :
  - indentation 0 à 3 contre 4 ;
  - `#x`, `#` seul, fermeture `#` après une tabulation ;
  - heading dans une citation ou un élément de liste (pas de coupure) ;
  - `#` dans un bloc HTML `<pre>` (pas de coupure) ;
  - fence ouverte dans un élément de liste ;
  - fence indentée de 4, fermeture indentée de 4 ;
  - info string contenant un backtick ;
  - bloc non fermé ;
  - setext (pas de coupure) ;
  - saut de niveau et dépilement.

  Les trois contre-exemples de la revue Codex sur `ComputationExtractor` sont
  repris tels quels.
- **Budget** : regroupement glouton, blocs de code et HTML atomiques, heading
  attaché au premier bloc, `Oversize`, `Part`. Une mesure qui **enregistre ses
  arguments** prouve que l'en-tête est compté, que chaque bloc est mesuré une
  fois et que chaque morceau est vérifié. Une mesure non additive (test double)
  exerce la dichotomie. Comptage en points de code (emoji, CJK) contre
  `string.Length`.

### 6.2 Entrées hostiles

Le découpeur lit du markdown non fiable. On exécute donc des entrées
construites pour l'attaquer, on ne se contente pas de relire :

- 100 000 headings ;
- 10 000 frères homonymes ;
- les collisions `# A`/`# A`/`# A-1` et `!!!`/`???`/`section-1`, avec des ids
  qui doivent rester tous distincts ;
- une ligne de 10 Mo ;
- un heading d'1 Mo suivi de 10 000 sous-sections ;
- le cas glouton de la revue (N blocs `x`, budget `3N/2 + 3`) ;
- des blocs de code non fermés ou alternés ;
- des titres pleins de caractères C0/C1, bidi et de surrogates isolés.

La linéarité se prouve **en comptant les caractères passés à `Measure` et les
lignes visitées**, à plusieurs tailles d'entrée, en vérifiant que doubler
l'entrée double au plus le travail. On ne chronomètre jamais. Pour le JSON, on
reparse chaque ligne, on compare **les valeurs** aux attentes, et on recalcule
`embedHash` depuis l'`embedText` reparsé. Vérifier seulement que le JSON se
parse ne suffit pas.

### 6.3 `PassageExportTests`

- `trust`, `reviewOutdated`, `status`, `statusKnown`, `staleAfter`,
  `staleAfterEpochMs` et `staleAfterMalformed` correspondent à l'`AuditFinding`
  et au `Lifecycle` du même concept ;
- le concept `status: retired` / `stale_after: not-a-date` de la revue ;
- la normalisation UTC et l'arrondi au supérieur de l'epoch ;
- l'ordre de sortie ;
- les omissions (§4.2), décidées avant le budget ;
- `ordinal` et `sectionId` ;
- l'énumération au fil de l'eau : le premier passage est obtenu avant que le
  dernier concept soit découpé, ce qui se prouve par un compteur, pas par une
  mesure de mémoire.

### 6.4 CLI

- **Golden** `passages.jsonl`, nouveau groupe `GoldenParityTests`,
  `okf passages tests/fixtures/okf_v02`. Le bundle d'entrée n'est pas modifié.
  On ajoute une ligne au tableau de `tests/fixtures/README.md`.
- **`MachineOutputTests`** : `passages` devient la cinquième sortie machine,
  figée indépendamment sur un bundle construit dans le test, avec des attentes
  dérivées à la main. Chaque ligne se parse, avec l'ordre et les types des
  champs, et le `text` égal à la tranche source.
- Les trois cas de §5.2, chacun avec son stdout, son stderr et son code.
- `--max-chars` invalide : code 1 ; `okf passages --help` : code 0.
- L'ajout de `stderr` aux handlers ne change aucun golden existant.
- **CI** : un appel `okf passages` dans le smoke test Native AOT de `ci.yml`,
  qui vérifie que la sortie se parse et contient les champs attendus. Le JSON
  doit survivre au trimming, ce que les tests en processus ne prouvent pas.

## 7. Documentation

- README : une section `okf passages`, avec un exemple de filtre côté pipeline
  (`status != "deprecated" and statusKnown and not reviewOutdated and
  (staleAfterEpochMs is None or staleAfterEpochMs > now_ms)`). La section
  reprend aussi les règles du consommateur de §4.5, le préfixage multi-bundle
  (§4.3), la jointure pour les champs exclus (§4.6) et la publication
  conditionnelle (§5.2).
- CHANGELOG `[Unreleased]`.
- `CLAUDE.md` : `PassageSplitter` comme découpeur unique, et l'usage de la passe
  de blocs de `LinkScanner` comme seule grammaire markdown, sur le modèle de
  `LfLines`.
- Rien dans `docs/spec-conformance/` : la spec ne dit rien de l'export, donc il
  n'y a aucune divergence à documenter.

## 8. Hors périmètre

- La conformité CommonMark des règles de fences de `ComputationExtractor`
  (§2.2), qui relève de §10.
- `ConceptSearch.RoundRobin`, quadratique sur des familles de tailles très
  inégales : issue #279. L'export ne l'utilise pas.

## 9. Revue externe — passe 1

La revue Codex (`gpt-6-astra`, effort high) a remonté 22 constats. Leur
intégration :

| Constat | Décision |
|---|---|
| Collision d'ids par suffixe d'occurrence | séparateur `.` hors alphabet (§4.3) |
| Règles de fences de `ComputationExtractor` non conformes | passe de blocs de `LinkScanner` (§2.2) |
| Promesse de stabilité trop large | stabilité qualifiée précisément (§4.3) |
| Ids de recherche et d'export divergents avec le budget | `sectionId` indépendant du budget (§4.1, §4.3) |
| Linéarité non prouvée par le comptage d'appels | mesure par bloc + vérification, comptage en caractères (§3.3, §6.2) |
| Amplification mémoire par les en-têtes | troncature à 200 points de code + émission au fil de l'eau (§4, §4.1) |
| `ParseProblems` ne couvre pas tous les fichiers illisibles | trois cas d'erreur explicites (§5.2) |
| Filtre `staleAfter` non portable | `staleAfterEpochMs` (§4.4) |
| Le hash n'est pas un protocole de synchronisation | renommé `embedHash`, règles du consommateur (§4.5) |
| Champs exclus présentés comme redondants | `reviewOutdated` ajouté, jointure documentée (§4.6) |
| Métadonnées invalides indiscernables | `statusKnown`, `staleAfterMalformed` (§4.1) |
| Moment de l'omission non défini | avant le budget, heading jamais seul (§3.3, §4.2) |
| Modèle de lignes et sens de « verbatim » | `Document.Body`, `SplitSpans` (§3.2) |
| Grammaire ATX hors fences simples | couverte par la passe de blocs (§3.1) |
| Surrogates isolés | remplacés par U+FFFD avant tout traitement (§3.2) |
| Ordre, version et namespace pour un consommateur | `ordinal` ajouté, namespace documenté (§4.1, §4.3) |
| Sortie partielle publiée malgré le code 1 | publication conditionnelle documentée (§5.2) |
| `RoundRobin` quadratique | issue #279, hors périmètre (§8) |
| Constats propres au barreau 4 | intégrés dans `2026-11-18-passage-search-design.md` |
