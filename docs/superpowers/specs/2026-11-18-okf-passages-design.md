# Design — `okf passages` : export d'un bundle en passages pour un pipeline d'embedding

Date : 2026-11-18
Statut : design validé, non implémenté. Plan à venir :
`docs/superpowers/plans/2026-11-18-okf-passages.md`.
Design lié : `2026-11-18-passage-search-design.md` (barreau 4, conception
seule), qui réutilise le découpeur défini ici.

## 1. Le besoin

Une équipe qui fait du RAG sur sa connaissance interne pointe aujourd'hui sa
base vectorielle sur des documents bruts. Un bundle OKF est exactement la
vérité de terrain curée qu'il vaudrait mieux embedder : c'est le conseil
central de l'article
[« Google's Open Knowledge Format (OKF) »](https://pub.towardsai.net/googles-open-knowledge-format-okf-the-markdown-standard-every-ai-engineer-should-know-about-1d14b223f39e)
(Towards AI) : *« use OKF as ground truth before embedding »*.

Rien dans OKF4net ne remet aujourd'hui un bundle à un pipeline d'embedding.
Chaque consommateur réécrit donc son découpage, et il perd deux choses :

1. **La structure.** Un découpage à taille fixe coupe au milieu d'une section
   et sépare un paragraphe de son titre.
2. **Les signaux v0.2.** C'est le point décisif. Une fois le bundle découpé, le
   RAG ne sait plus qu'un concept est déprécié (§5.4), jamais relu (§5.3) ou
   périmé (§5.5). Il le remonte avec le même poids qu'un concept relu par un
   humain. C'est ce que v0.2 a été conçue pour dire, et c'est ce qu'aucun
   découpeur markdown générique ne restitue.

### 1.1 Ce que ce travail livre

- `okf passages <bundle>` : un JSONL sur stdout, qu'un pipeline Python consomme
  sans .NET (le découplage producteur/consommateur de l'article).
- Un id stable et une empreinte par passage, pour ne ré-embedder que ce qui a
  changé d'un export à l'autre.
- Le filtrage à la requête sur la confiance, le statut et l'échéance.
- `PassageSplitter`, brique publique et pure du cœur, réutilisée telle quelle
  par la recherche par passages (barreau 4).

### 1.2 Ce qu'il ne fait pas

Aucun embedding, aucune base vectorielle, aucun tokenizer embarqué, aucune
modification de `ConceptSearch`. Rien ne change au chargement d'un bundle.

## 2. Architecture

Deux unités pures dans `src/OKF4net/`, un verbe qui sérialise.

| Unité | Rôle | Dépend de |
|---|---|---|
| `PassageSplitter` | body (+ titre, budget) → sections ; aucune notion de bundle | `Internal/LfLines`, `Internal/MarkdownFences` |
| `PassageText` | composition de `embedText` et calcul du hash ; une seule implémentation | — |
| `PassageExport` | `Bundle` → `Passage` en ordre déterministe, projection v0.2 | `PassageSplitter`, `ConceptAudit` |
| `okf passages` | une `VerbSpec` ; JSONL via `CliJsonContext` | `PassageExport` |

### 2.1 Approches écartées

- **Tout dans la CLI.** Plus rapide, mais le barreau 4 devrait extraire et
  réécrire le découpeur.
- **`Concept.Passages` calculé au chargement.** Chaque chargement paierait le
  découpage et sa mémoire (recalibrage de `SnapshotMemory`, et même
  `okf validate` paierait). Le `Bundle` serait couplé à une vue dérivée, alors
  que ces vues doivent rester jetables.

### 2.2 Code partagé extrait

`ComputationExtractor` porte en privé `TryMatchFenceOpen` et `IsFenceCloseLine`
(ouverture ``` ou `~~~` de 3 caractères ou plus, fermeture du même caractère au
moins aussi longue). Ces deux méthodes passent dans
`Internal/MarkdownFences.cs`, utilisé par les deux. On ne crée pas de seconde
copie. Le comportement de `ComputationExtractor` ne change pas, et ses tests en
restent la preuve.

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
    string Text,                     // tranche verbatim du body, LF
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

### 3.1 Headings

- Seuls les headings **ATX** sont reconnus : 0 à 3 espaces d'indentation, 1 à 6
  `#`, puis un espace, une tabulation ou la fin de ligne. La séquence de `#` de
  fermeture (précédée d'un espace) est retirée, ainsi que les espaces aux deux
  bouts. `#hashtag` n'est pas un heading, ni un `#` indenté de 4 espaces.
- **Fence-aware** : une ligne à l'intérieur d'un bloc de code délimité n'est
  jamais un heading. Un bloc non fermé court jusqu'à la fin du body (CommonMark).
- **Setext non reconnu en v1** (texte souligné de `===`/`---`). C'est une
  limite documentée, sans perte : la section est simplement plus grosse.
- La pile des headings : un heading de niveau n dépile tout ce qui est de
  niveau ≥ n, puis s'empile. Un saut de niveau (H1 → H3) est toléré.

### 3.2 Partition exacte

Le body est normalisé en LF via `LfLines`. Les sections le **partitionnent** :
le préambule (tout ce qui précède le premier heading, éventuellement vide),
puis une section par heading, depuis sa ligne de heading jusqu'à la ligne qui
précède le heading suivant. **Invariant : la concaténation des `Text`, dans
l'ordre, redonne le body normalisé.** Le découpeur ne jette rien, même pas un
préambule vide. Omettre des sections est une politique d'export (§4.2).

### 3.3 Budget

Le budget s'applique à **`embedText`** (§4.1), puisque c'est lui que le modèle
d'embedding reçoit. Le découpeur appelle donc `PassageText.ComposeEmbedText`
pour mesurer. La taille mesurée est ainsi exactement celle qui sort.

Une section dont l'`embedText` dépasse `MaxSize` est redécoupée :

1. Elle est découpée en blocs aux lignes blanches **hors bloc de code**. Un
   bloc de code délimité forme un bloc atomique.
2. Les blocs sont regroupés de façon gloutonne, dans l'ordre, tant que
   l'`embedText` du morceau reste ≤ `MaxSize`.
3. Un bloc qui dépasse seul est émis tel quel avec `Oversize = true`. On ne
   coupe jamais au milieu d'un paragraphe ou d'un bloc de code.

Les morceaux portent `Part` 1..n, et l'invariant de partition tient toujours.
La ligne de heading reste dans le premier morceau. L'en-tête de `embedText`
redonne le contexte aux morceaux suivants.

`MaxSize` ≤ 0 lève `ArgumentOutOfRangeException`. Sans budget, rien n'est
redécoupé : `Part` vaut 0 et `Oversize` vaut `false` partout, quelle que soit la
taille de la section.

## 4. L'export — `PassageExport`

```csharp
public static class PassageExport
{
    public static IReadOnlyList<Passage> Build(Bundle bundle, PassageBudget? budget = null);
}
```

L'ordre est déterministe : les concepts par `ConceptId` en ordinal, puis les
passages dans l'ordre du document. Les signaux v0.2 viennent **d'un seul appel
à `ConceptAudit.Run(bundle, AuditQuery.All)`**, dont on lit l'`AuditFinding` de
chaque concept. On ne recalcule rien (règle « l'audit ne se forke pas »). Les
fichiers illisibles (`Bundle.ParseProblems`) ne sont pas des concepts : ils ne
produisent aucun passage, et la CLI les signale (§5.2).

### 4.1 Le modèle `Passage`

| Champ (JSON) | Contenu |
|---|---|
| `id` | identité stable (§4.3) |
| `conceptId` | `sales/orders` |
| `headings` | `["Schema", "Partitioning"]`, `[]` pour le préambule |
| `text` | la tranche verbatim du body |
| `embedText` | en-tête + ligne vide + `text` |
| `hash` | `sha256:` + hex minuscule du SHA-256 de `embedText` en UTF-8 |
| `oversize` | `true` si le passage dépasse le budget sans pouvoir être coupé |
| `type`, `title` | frontmatter, `null` si absent |
| `tags` | frontmatter, `[]` si absent |
| `trust` | `AuditVocabulary.Name(TrustTier)` (§5.3) |
| `status` | `AuditVocabulary.Name(ConceptStatus)` (§5.4) |
| `staleAfter` | `Lifecycle.StaleAfter` en UTC, format fixe `yyyy-MM-ddTHH:mm:ss.fffffffZ` (7 décimales toujours écrites), ou `null` |

Tous les champs sont toujours présents, dans cet ordre. Les noms sont en
camelCase, comme `validate`/`info`/`audit --json` (`CliJsonContext`).

**En-tête de `embedText`** : le titre du concept (ou son id s'il n'a pas de
titre), puis chaque heading de `headings`, joints par ` > `. Pour le préambule,
le titre seul. L'en-tête répète la ligne de heading déjà présente dans `text` :
c'est un doublon de quelques tokens qu'on accepte, parce que la règle reste la
même pour tous les passages. C'est ce qui permet à un morceau `Part ≥ 2` de
garder son contexte.

**Le hash porte sur `embedText`, pas sur `text`.** La question du pipeline est
« dois-je ré-embedder ? ». Un titre de concept renommé change l'entrée de
l'embedding, donc le hash doit changer.

### 4.2 Ce que l'export omet

Une section dont le `text`, privé de sa ligne de heading, ne contient que des
blancs est omise. Le préambule vide aussi. Embedder un heading nu ne sert à
rien.

### 4.3 Identité

`id = <conceptId>#<slug>/<slug>/…`, un slug par heading de la pile.

- **Slug** (règle GitHub) : minuscules en culture invariante. On garde lettres,
  chiffres, `-` et `_` (lettres Unicode comprises), on remplace espaces et
  tabulations par `-`, et on retire le reste. Un slug vide devient `section`.
- **Frères homonymes** (même slug sous le même parent) : le premier garde son
  slug, les suivants prennent `-1`, `-2`… Le calcul est linéaire (compteur par
  parent), jamais une recherche répétée.
- **Préambule** : `<conceptId>#`.
- **Morceaux** d'une section redécoupée : suffixe `~<Part>`. Ces suffixes sont
  positionnels dans la section, et c'est documenté.

Un passage dont seul le voisin change garde son id et son hash. Renommer un
heading change l'id, et c'est voulu.

### 4.4 Péremption : `staleAfter`, pas `isStale`

Un index construit aujourd'hui est interrogé dans trois mois. Un booléen
calculé à l'export serait faux le jour où il compte. Le pipeline filtre au
moment de la requête (`staleAfter > now`). Les dates ISO en UTC à largeur fixe
se comparent comme des chaînes, donc n'importe quelle base vectorielle sait
faire ce filtre. La largeur fixe est indispensable : avec des décimales
facultatives (`FFFFFFF`), `…:00.5Z` se classerait avant `…:00Z` (`.` < `Z`).
Les 7 décimales correspondent à la précision d'un `DateTimeOffset`, donc le
format ne perd rien. Le viewer fait le même choix pour la même raison. Effet
secondaire : la sortie ne dépend pas de l'horloge, et le golden n'a pas besoin
de `--as-of`.

### 4.5 Exclus volontairement

`description`, `lastVerifiedAt`, `lastChangedAt`, les liens sortants. Ils sont
disponibles, mais aucun ne filtre un RAG mieux que `trust` et `status`.
Ajouter un champ plus tard ne casse pas un consommateur JSONL ; en retirer un,
si.

## 5. Le verbe — `okf passages`

```text
okf passages <bundle> [--max-chars <n>]
    --max-chars <n>   Re-split sections whose embedText exceeds n characters
```

### 5.1 Sortie

JSONL sur stdout : UTF-8 sans BOM, `\n`, un objet `Passage` par ligne,
sérialisé par `CliJsonContext` (génération de source, AOT) sans indentation.
L'encodeur reste celui des autres sorties `--json`. Un bundle sans concept
donne une sortie vide et le code 0.

`--max-chars` construit un `PassageBudget(n)` avec la mesure par défaut (points
de code). Une valeur absente, nulle, négative ou non entière est une erreur
d'usage (code 1). La CLI ne propose aucune mesure par tokenizer.

### 5.2 Fichiers illisibles

- stdout contient tous les passages qu'on a pu produire ;
- chaque fichier de `ParseProblems` est signalé sur stderr :
  `warning: skipped <display path>: <error>` ;
- **le code de sortie vaut 1 si au moins un fichier a été ignoré.** En CI,
  `okf passages b > out.jsonl` échoue au lieu d'indexer un bundle amputé sans
  le dire. Le code 1 sert aussi aux erreurs d'usage, comme pour
  `okf validate` ; stderr distingue les deux cas.

**Changement transverse** : le délégué de `VerbSpec.Run` reçoit en plus
`stderr` : `Func<CliArgs, TextReader, TextWriter, TextWriter, int>`. Le
changement est mécanique sur tous les handlers, sans aucun changement de
sortie : tous les goldens existants restent identiques octet pour octet.

## 6. Tests

### 6.1 `PassageSplitterTests`

- **Propriété de partition** sur chaque concept de `bundles/` et de
  `tests/fixtures/`, et sur chaque cas construit.
- Headings : indentation 0–3 contre 4, `#x`, `#` seul, `#` de fermeture, saut
  de niveau, dépilement.
- Blocs de code : ``` et `~~~`, fermeture plus longue, fermeture d'un autre
  caractère ignorée, bloc non fermé. CRLF et CR isolés (`LfLines`).
- Budget : regroupement glouton, bloc de code atomique, `Oversize`, `Part`. Une
  mesure qui **enregistre ses arguments** prouve qu'on mesure `embedText`.
  Comptage en points de code (emoji, CJK) contre `string.Length`.

### 6.2 Entrées hostiles

Le découpeur lit du markdown non fiable. On exécute donc des entrées
construites pour l'attaquer, on ne se contente pas de relire :

- 100 000 headings ;
- 10 000 frères homonymes (suffixes de slug) ;
- une ligne de 10 Mo ;
- des blocs de code non fermés ou alternés ;
- des titres pleins de caractères C0/C1, bidi et de surrogates isolés.

La linéarité se prouve **en comptant le travail** (appels de mesure, lignes
visitées), jamais en chronométrant. Le JSON produit doit se reparser.

### 6.3 `PassageExportTests`

`trust`, `status` et `staleAfter` égaux à l'`AuditFinding` du même concept ;
normalisation UTC ; ordre ; omissions (§4.2) ; hash recalculé indépendamment ;
ids (préambule, homonymes, morceaux).

### 6.4 CLI

- **Golden** `passages.jsonl`, nouveau groupe `GoldenParityTests`,
  `okf passages tests/fixtures/okf_v02`. Le bundle d'entrée n'est pas modifié.
  On ajoute une ligne au tableau de `tests/fixtures/README.md`.
- **`MachineOutputTests`** : `passages` devient la cinquième sortie machine,
  figée indépendamment sur un bundle construit dans le test, avec des attentes
  dérivées à la main. Chaque ligne se parse, avec l'ordre et les types des
  champs, et le `text` égal à la tranche source.
- Fichier illisible : stdout complet, avertissement sur stderr, code 1.
- `--max-chars` invalide : code 1 ; `okf passages --help` : code 0.
- L'ajout de `stderr` aux handlers ne change aucun golden existant.
- **CI** : un appel `okf passages` dans le smoke test Native AOT de `ci.yml`.
  Le JSON doit survivre au trimming, ce que les tests en processus ne prouvent
  pas.

## 7. Documentation

- README : une section `okf passages`, avec un exemple de filtre côté pipeline
  (`status != "deprecated" and (staleAfter is None or staleAfter > now)`).
- CHANGELOG `[Unreleased]`.
- `CLAUDE.md` : `PassageSplitter` comme découpeur unique et
  `Internal/MarkdownFences` comme règles de blocs de code uniques, sur le
  modèle de `LfLines`.
- Rien dans `docs/spec-conformance/` : la spec ne dit rien de l'export, donc il
  n'y a aucune divergence à documenter.
