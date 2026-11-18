# Design — `okf passages` : export d'un bundle en passages pour un pipeline d'embedding

Date : 2026-11-18
Statut : design validé, non implémenté. Plan à venir :
`docs/superpowers/plans/2026-11-18-okf-passages.md`.
Design lié : `2026-11-18-passage-search-design.md` (barreau 4, conception
seule), qui réutilise le découpeur défini ici.
Revue externe : Codex (`gpt-6-astra`, effort high), passes 1 et 2, intégrées
(voir §9).

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
modification de `ConceptSearch`. L'export n'ajoute aucun calcul au chargement
d'un bundle. Trois corrections de briques partagées sont des prérequis (§2.3).
L'une d'elles (A) corrige les liens relevés au chargement sur les entrées
qu'elle concerne : c'est voulu, et ses tests le vérifient.

## 2. Architecture

Deux unités pures dans `src/OKF4net/`, un verbe qui sérialise.

| Unité | Rôle | Dépend de |
|---|---|---|
| `PassageSplitter` | body (+ titre, budget) → sections ; aucune notion de bundle | la passe de blocs de `LinkScanner` (§2.2), `LfLines.SplitSpans` |
| `PassageText` | normalisation `N`, composition et troncature de l'en-tête, slugs, hash ; une seule implémentation | — |
| `PassageExport` | `Bundle` → `Passage`, au fil de l'eau, en ordre déterministe, ids et omissions, projection v0.2 | `PassageSplitter`, `PassageText`, `ConceptAudit` |
| `okf passages` | une `VerbSpec` ; JSONL via `CliJsonContext`, écrit ligne à ligne | `PassageExport` |

L'attribution des ids et les omissions vivent dans une seule unité, réutilisée
par la recherche (barreau 4). Elles ne sont dupliquées nulle part.

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
définitions de références. Elle relève **déjà** chaque heading ATX. Son
commentaire revendique une équivalence avec commonmark.js sur 300 000 bodies,
mais **seulement pour la visibilité des références de notes**. Ce n'est pas
une preuve de conformité générale, et la passe a au moins un écart connu
(§2.3).

On en tire, **depuis la même passe**, sans seconde grammaire :

- **les headings** : pour chaque heading ATX, son index de ligne, son niveau
  et le nombre de conteneurs ouverts. Ce nombre est relevé **après** fermeture
  des conteneurs non continués et ouverture des nouveaux, là où la passe classe
  la ligne comme heading. Le texte du heading ne vient pas de la passe (§3.1).
- **les intervalles de blocs atomiques** (§2.3, tâche B) : les plages de lignes
  qui appartiennent à un bloc de code (délimité ou indenté) ou à un bloc HTML,
  **lignes blanches internes comprises**.

L'index de ligne suffit à retrouver la tranche via `LfLines.SplitSpans`.
L'offset existant de la passe est relatif à la ligne, pas au body.

**Écarté** : extraire les règles privées de blocs de code de
`ComputationExtractor`. Elles ne sont pas conformes à CommonMark (`TrimStart`
sans limite d'indentation, info string d'une fence à backticks non contrôlée).
Leur écart concerne la détection de `# Computation` (§10), un autre sujet que
ce design ne touche pas.

### 2.3 Prérequis : trois corrections de briques partagées

Trois tâches précèdent le découpeur dans le plan. Chacune a ses propres tests
et sa propre entrée CHANGELOG. Les tests existants de leurs consommateurs
(liens, index, audit, validateur) sont la preuve que rien d'autre ne bouge. Si
un golden change, le diff est arbitré selon `tests/fixtures/README.md`. Comme
ces tâches touchent l'API publique du cœur, `dotnet test producers/OkfProducer.sln`
est lancé à la fin de chacune (`CLAUDE.md`).

- **A — Prédicat d'espaces de `LinkScanner`.** Après une fence fermante
  (`Links.cs:763`), après un soulignement setext (`:1358`) et après une balise
  HTML seule sur sa ligne (`:1442`), la passe accepte n'importe quel espace
  Unicode (`IsWhiteSpace`). CommonMark n'admet que l'espace et la tabulation.
  Exemple : `"~~~\n~~~ \n# H\nx\n~~~\n"` ferme la fence à la deuxième
  ligne, et `# H` devient un heading. On corrige ces trois sites seulement
  (U+0020 et U+0009). Les autres usages d'espaces de la passe, dans les
  labels par exemple, ne sont pas concernés et ne changent pas. Chaque site a
  son test de non-régression.

  **Effet voulu sur le chargement** : `LinkScanner` construit aussi les liens
  du `Bundle`. Dans `"~~~\n~~~ \n[x](missing.md)\n~~~\n"`, le lien est
  aujourd'hui relevé après la fermeture erronée ; après A, il reste dans le code
  et disparaît du graphe. Les liens cassés, `info`, les diagnostics de
  validation et `graph` changent donc **pour ces entrées**. Les tests des
  consommateurs (liens, références de notes, headings conventionnels, entrées
  d'index) vérifient ces changements attendus. Aucune entrée de
  `tests/fixtures` ne contient ces espaces : aucun golden ne devrait bouger, et
  tout diff reste soumis au régime de `tests/fixtures/README.md`.
- **B — Intervalles de blocs.** La passe vide les lignes de code sans dire à
  quel bloc appartient une ligne blanche. Dans `"    a\n\n    b\n"`, la ligne
  blanche fait partie du **même** bloc de code indenté (CommonMark §4.4), mais
  la passe la traite comme une ligne blanche ordinaire. La passe expose donc les
  intervalles de blocs, enregistrés à part, aux branches de classification
  existantes. Pour le code indenté, elle garde le début du bloc et une plage de
  lignes blanches en attente : la plage entre dans le bloc s'il continue, elle
  en sort s'il se termine. Un changement de conteneur ferme le bloc, car la
  profondeur seule ne distingue pas deux éléments de liste successifs. Sa
  sortie existante (paires `Raw`/`Blanked`) ne change pas. Cas figés :
  `"    a\n\n    b\n\np\n"` (un bloc avec son blanc interne, sans son blanc
  final) et `"-     a\n\n-     b\n"` (deux blocs distincts).
- **C — Valeurs de cycle de vie non scalaires.** `Frontmatter.Lifecycle` passe
  `status` et `stale_after` par `AsDisplayString()`, qui rend `null` pour une
  liste ou un mapping. `status: [deprecated]` est alors lu comme absent, donc
  `stable` et connu. On corrige la brique partagée **sans changer le sens des
  membres existants** :
  - `Lifecycle` gagne deux propriétés en `init`, `StatusNotScalar` et
    `StaleAfterNotScalar`, posées par `Frontmatter.Lifecycle`, qui voit la
    valeur YAML. Ses quatre paramètres positionnels ne changent pas, ce qui
    préserve son constructeur et sa déconstruction. `StatusIsKnown`,
    `StaleAfterRaw` et `StaleAfterMalformed` gardent leur sens. Il n'y a
    **aucune valeur sentinelle** dans `StaleAfterRaw`, qui fuirait dans
    `audit --json`.
  - Les nouveaux drapeaux ne signalent **que les séquences et les mappings**
    (même vides). `YamlNull` (`null`, `~`, valeur vide non citée) garde son
    traitement actuel, celui de l'absence, comme le fait déjà le validateur pour
    `StatusNotScalar`. Une chaîne vide citée (`""`) est un scalaire : elle reste
    un statut inconnu ou une échéance illisible, comme aujourd'hui. Résultat
    attendu, chaque colonne pour son propre champ :

    | Valeur | `statusKnown` | `staleAfterMalformed` |
    |---|---|---|
    | absente ou `YamlNull` | `true` | `false` |
    | scalaire reconnu / échéance lisible | `true` | `false` |
    | scalaire inconnu / échéance illisible, dont `""` | `false` | `true` |
    | séquence ou mapping, même vide | `false` | `true` |

  - Côté validateur, `status` non scalaire est déjà signalé (`StatusNotScalar`),
    et rien ne change : modifier `StatusIsKnown` aurait ajouté un second
    diagnostic parasite (`StatusUnknown` avec un texte vide). `stale_after` non
    scalaire n'est signalé par rien aujourd'hui, puisque `CheckTemporal` ne
    s'exécute que si `StaleAfterRaw` est non nul. On ajoute le warning
    symétrique `StaleAfterNotScalar`, **à la fin** de l'enum `DiagnosticCode`
    (après `KeyNotPortable`), ce qui préserve les valeurs numériques existantes.
    Ses tests fixent la multiplicité exacte des diagnostics pour chaque cas,
    pas seulement leur présence.
  - L'audit transporte les nouvelles propriétés sans changer ses rendus.

  C'est un changement sémantique de `okf validate` pour ces bundles malformés
  (un warning de plus), noté au CHANGELOG.

## 3. Le découpeur — `PassageSplitter`

```csharp
public static class PassageSplitter
{
    public static IReadOnlyList<PassageSection> Split(
        string body, string contextTitle, PassageBudget? budget = null);
}

public sealed record PassageBudget(int MaxSize, Func<string, int>? Measure = null);

public sealed record PassageSection(
    IReadOnlyList<string> Headings,  // pile des textes de headings, vide pour le préambule
    string Text,                     // tranche de N(body)
    int Part,                        // 0 si la section tient entière, sinon 1..n
    bool Oversize);
```

`Measure` vaut par défaut le nombre de points de code Unicode (`Rune`), et non
`string.Length`. C'est ce qu'un utilisateur appelle un caractère, et c'est ce
que compte `len()` en Python : un pipeline qui revérifie la taille obtient le
même nombre que nous. Un hôte branche un vrai tokenizer en une ligne
(`Measure: t => tokenizer.CountTokens(t)`, p. ex. avec
`Microsoft.ML.Tokenizers`). La dépendance reste alors chez celui qui connaît
son modèle. Le cœur et `okf` restent zéro-dépendance.

**Préconditions d'une `Measure` fournie** : elle est pure (même texte, même
résultat), elle rend un entier ≥ 0, et elle ne lève pas d'exception. Le
découpeur additionne ses résultats en `long`. Une violation de ces
préconditions est une erreur de l'hôte : le découpeur ne boucle pas pour
autant (§3.3), mais le résultat n'est plus garanti.

### 3.1 Points de coupure

- On coupe **uniquement sur les headings ATX de premier niveau de document**,
  c'est-à-dire ceux que la passe de blocs (§2.2) relève avec zéro conteneur
  ouvert. Un heading dans une citation ou un élément de liste ne coupe pas, et
  un `#` dans un bloc de code ou un bloc HTML n'est pas un heading. Ces règles
  viennent de la passe : le découpeur n'en a aucune à lui.
- **Les headings setext ne coupent pas** en v1, bien que la passe les
  reconnaisse. C'est une limite documentée, sans perte : la section est
  simplement plus grosse. On l'étendra s'il y a une demande.
- **Le texte d'un heading** est son **contenu ATX brut**, tiré de sa seule
  ligne : sans l'indentation, les `#` d'ouverture, la séquence de `#` de
  fermeture (précédée d'un espace ou d'une tabulation) et les espaces ou
  tabulations aux bords (CommonMark §4.2). Ce n'est **pas** le texte rendu : le
  markdown inline reste tel quel. `# **A** &amp; B` donne `**A** &amp; B`, et
  `# [A](x)` donne `[A](x)`. La projection « texte visible » de `LinkScanner`
  est écartée parce qu'elle dépend de tout le body : elle masque `[r]` dès
  qu'une définition `[r]: /x` apparaît ailleurs, si bien que l'id de
  `# [A][r]` changerait quand une **autre** section change. Le contenu brut ne
  dépend que de la ligne.

  **Une seule règle** : ce contenu est calculé par le helper privé existant de
  `LinkScanner`, `AtxHeadingContentAt` (`Links.cs`), qui extrait déjà le
  contenu brut séparément de la projection visible. Il est partagé (visibilité
  `internal`), pas réécrit. La fermeture n'est retirée que si elle est
  terminale, non échappée, précédée d'un espace ou d'une tabulation et suivie
  seulement d'espaces ou de tabulations (CommonMark §4.2). Cas figés dans les
  tests :

  | Ligne | Contenu brut |
  |---|---|
  | `# foo ### b` | `foo ### b` |
  | `# foo \###` | `foo \###` |
  | `# foo#` | `foo#` |
  | `# foo<TAB>###<TAB>` | `foo` |
  | `### ###` | chaîne vide |
  | `# A  B` | `A  B` (deux espaces conservés) |

- La pile des headings : un heading de niveau n dépile tout ce qui est de
  niveau ≥ n, puis s'empile. Un saut de niveau (H1 → H3) est toléré.

### 3.2 Normalisation et partition exacte

L'entrée est `Concept.Document.Body`, la chaîne produite par
`OkfDocument.Parse`. Ce n'est **pas** une tranche d'octets du fichier : le
parse joint déjà les lignes et retire une première ligne vide.

**Une seule normalisation, `N`**, appliquée une fois, avant tout découpage,
mesure ou hash :

1. `\r\n` devient `\n`. Un `\r` isolé reste dans sa ligne : c'est le modèle de
   lignes de `LfLines`, donc celui de la passe de blocs (CommonMark, lui, y
   voit une fin de ligne : limite documentée, partagée avec `LinkScanner`).
2. Un surrogate UTF-16 isolé devient U+FFFD. Seule l'API peut fournir une
   telle chaîne : le chargement refuse l'UTF-8 invalide, et le parseur YAML
   écarte les échappements de surrogates.

La présence ou l'absence du `\n` final est conservée (`LfLines.SplitSpans`).
« Verbatim » veut dire, partout dans ce document : **une tranche de `N(body)`**.

Les sections **partitionnent** `N(body)` : le préambule (tout ce qui précède le
premier point de coupure, éventuellement vide), puis une section par point de
coupure, depuis sa ligne de heading jusqu'à la ligne qui précède le point de
coupure suivant. **Invariant : la concaténation des `Text`, dans l'ordre, est
égale à `N(body)`.** Le découpeur ne jette rien. Une section qui n'a que sa
ligne de heading (`"# A\n"` suivie de `"# B\n…"`) existe dans sa sortie.
L'omission est une politique d'export (§4.2), et l'invariant ne s'applique pas
à la sortie filtrée.

### 3.3 Budget

Le budget s'applique à **`embedText`** (§4.1), puisque c'est lui que le modèle
d'embedding reçoit. Une section dont l'`embedText` complet tient dans le budget
n'est **pas** redécoupée : `Part = 0`, même quand un budget est fourni.

Une section **sans bloc de contenu** (vide, ou faite seulement de sa ligne de
heading et de lignes blanches) n'est jamais redécoupée non plus : `Part = 0`,
et `Oversize = true` si son `embedText` dépasse le budget. C'est le cas de
`Split("", "T", new PassageBudget(1))`, dont le préambule vaut `"T\n\n"`.
L'export omet de toute façon ces sections (§4.2).

**Blocs.** Une section qui dépasse se découpe en blocs aux lignes blanches qui
ne sont dans aucun intervalle atomique (§2.2). Un bloc de code ou un bloc HTML,
lignes blanches internes comprises, est atomique. Les règles de rattachement :

- une ligne blanche s'attache au bloc qui la précède ;
- les lignes blanches en tête de section, s'il y en a, s'attachent au premier
  bloc ;
- la ligne de heading, et les lignes blanches qui la suivent, s'attachent au
  premier bloc qui a un contenu. Pour une section non omise (§4.2), aucun
  morceau ne contient donc seulement un heading.

**Regroupement.**

1. On mesure **le préfixe** une seule fois : l'en-tête suivi de sa ligne vide
   (`en-tête + "\n\n"`), exactement comme il apparaît dans `embedText`. Puis on
   mesure chaque bloc une seule fois.
2. On regroupe les blocs de façon gloutonne, dans l'ordre, tant que
   `mesure(préfixe) + Σ mesure(bloc)` reste ≤ `MaxSize`. Un regroupement
   contient toujours au moins un bloc.
3. On **vérifie** chaque regroupement par une mesure exacte de son
   `embedText`. S'il dépasse et contient k ≥ 2 blocs, on le coupe à l'indice
   ⌊k/2⌋, puis on vérifie chaque moitié de la même façon. Chaque récursion
   reçoit strictement moins de blocs, et un regroupement d'un seul bloc est
   terminal. Il n'y a ni boucle ni regroupement vide.
4. `Oversize = true` si et seulement si l'`embedText` exact d'un regroupement
   d'un seul bloc dépasse `MaxSize`. On ne coupe jamais au milieu d'un bloc.

Les morceaux portent `Part` 1..n dans l'ordre, et l'invariant de partition
tient toujours.

**Coût garanti, en caractères soumis à `Measure`** (c'est ce que comptent les
tests, §6.2, pas des appels ni du temps) :

Le travail d'une section comprend : la mesure de son `embedText` complet (pour
savoir si elle tient), puis, si elle dépasse, la mesure du préfixe, celle de
chaque bloc et la vérification de chaque regroupement.

- **avec la mesure par défaut**, qui est exactement additive sur une
  concaténation, la vérification de l'étape 3 ne coupe jamais rien. Le total
  est majoré explicitement : **caractères mesurés ≤ 2 × |N(body)| + 3 × |sortie|**,
  où |sortie| est la somme des `embedText` de **toutes** les sections produites
  par le découpeur, préfixes compris, **avant** les omissions de l'export, et où
  toutes les longueurs sont comptées dans l'unité de la mesure (points de code).
  Compter seulement les lignes JSONL conservées rendrait la borne fausse : un
  body vide coûte une mesure et ne donne aucune ligne. La borne est
  linéaire, mais ce n'est **pas** un ratio fixe entre deux tailles : franchir le
  seuil « la section tient » ajoute les phases de découpage, et peut faire plus
  que doubler le travail pour une entrée deux fois plus grande.
- **avec une `Measure` quelconque**, chaque niveau de dichotomie peut remesurer
  les mêmes caractères. Le coût est O((|N(body)| + |sortie|) × (1 + log₂ k)),
  où k est le nombre de blocs du plus grand regroupement initial (le `1 +`
  couvre le cas où tous les regroupements sont des singletons). Aucun préfixe
  grossissant n'est remesuré. Ce design garantit le volume de texte soumis à la
  mesure, pas le coût propre de celle-ci.

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

L'ordre est déterministe : les concepts par `ConceptId` en ordinal, puis les
passages dans l'ordre du document.

Les signaux v0.2 viennent **d'un seul appel à
`ConceptAudit.Run(bundle, AuditQuery.All)`**, qui couvre tous les concepts
chargés. On lit l'`AuditFinding` de chaque concept et on ne recalcule rien
(règle « l'audit ne se forke pas »). `ConceptAudit.Run` lit l'horloge en
interne, mais l'export n'utilise ni `IsStale` ni `EvaluatedAt` : rien de ce qui
sort ne dépend de l'heure.

**Mémoire.** L'audit se fait au premier `MoveNext`. Il parcourt tout le bundle
et matérialise la liste des findings, soit une mémoire en O(nombre de
concepts). Ensuite seulement, l'export découpe un concept, émet ses passages et
passe au suivant. La mémoire des **passages** est donc celle du concept en
cours. Aucune liste de tous les passages n'est matérialisée, et la CLI écrit
chaque ligne dès qu'elle est produite.

**Précondition** : le `Bundle` n'est pas modifié pendant l'énumération. Un
`Bundle` chargé est modifiable (`Frontmatter.Set`), et une modification entre
deux `MoveNext` mélangerait des signaux de l'audit avec des métadonnées lues
plus tard. C'est la même règle que pour une collection énumérée. La CLI la
respecte trivialement.

### 4.1 Le modèle `Passage`

| Champ (JSON) | Contenu |
|---|---|
| `format` | `okf-passages/1` : version du format de la ligne (§4.7) |
| `id` | identité stable du passage (§4.3) |
| `sectionId` | `id` sans le suffixe de morceau `~n` |
| `conceptId` | `sales/orders` |
| `ordinal` | position du passage parmi ceux **émis** pour ce concept, à partir de 0 |
| `headings` | textes des headings de la pile (§3.1), chacun tronqué (voir plus bas), `[]` pour le préambule |
| `text` | la tranche de `N(body)` |
| `embedText` | préfixe (en-tête + `"\n\n"`) + `text` |
| `embedHash` | `sha256:` + hex minuscule du SHA-256 de `embedText` en UTF-8 |
| `oversize` | `true` si le passage dépasse le budget sans pouvoir être coupé |
| `type`, `title` | frontmatter, `null` si absent |
| `tags` | frontmatter, `[]` si absent |
| `trust` | `AuditVocabulary.Name(TrustTier)` (§5.3) |
| `reviewOutdated` | `AuditFinding.IsReviewOutdated` (§5.2, voir plus bas) |
| `status` | `AuditVocabulary.Name(ConceptStatus)` (§5.4) |
| `statusKnown` | `Lifecycle.StatusIsKnown && !Lifecycle.StatusNotScalar` : `false` si `status` est présent mais non reconnu, ou non scalaire (§2.3 C) ; il est alors lu comme `stable` |
| `staleAfter` | `Lifecycle.StaleAfter` en UTC, format fixe `yyyy-MM-ddTHH:mm:ss.fffffffZ`, ou `null` |
| `staleAfterEpochMs` | la même échéance en millisecondes Unix entières, **arrondie au supérieur**, ou `null` |
| `staleAfterMalformed` | `Lifecycle.StaleAfterMalformed \|\| Lifecycle.StaleAfterNotScalar` : `true` si `stale_after` est présent mais illisible, ou non scalaire (§2.3 C) |

Tous les champs sont toujours présents, dans cet ordre. Les noms sont en
camelCase, comme `validate`/`info`/`audit --json` (`CliJsonContext`).

**`reviewOutdated`** vaut `true` quand le concept a une date de dernière
modification (`LastChangedAt` : `generated.at`, ou à défaut le `timestamp`
historique, §13.1) **et** une date de dernière relecture lisible (le plus
récent `verified[].at` qui se lit comme un horodatage §5), et que la première
est strictement postérieure à la seconde. Le choix entre `generated.at` et
`timestamp` se fait **avant** la lecture de la date : un `generated.at` présent
mais illisible ne retombe pas sur `timestamp`, et `reviewOutdated` vaut alors
`false`. Un concept jamais relu vaut `false` : c'est son `trust`
(`unverified`) qui le signale.

**Troncature.** Un « élément tronqué » fait **au plus 200 points de code,
ellipse comprise** : au-delà, on garde les 199 premiers, on retire les espaces
finaux et on ajoute `…`. C'est la coupe des descriptions du site index du
viewer, **extraite dans le cœur et partagée** (le viewer y ajoute, avant, son
propre repli des espaces, que l'export n'applique pas). Elle s'applique à chaque
élément de `headings`, et à la **copie** du titre (ou de l'id) dans l'en-tête
de `embedText`. Le champ JSON `title` reste **intégral**, comme les autres
métadonnées du concept (§4.3, §8). `text` n'est jamais tronqué.

**En-tête de `embedText`** : le titre du concept (ou son id s'il n'a pas de
titre), puis chaque élément de `headings`, tronqués, joints par ` > `. Pour le
préambule, le titre seul. Avec au plus 7 éléments (titre + 6 niveaux), l'en-tête
fait au plus 7 × 200 + 6 × 3 = 1 418 points de code, et le préfixe
1 420 avec sa ligne vide (un id utilisé à la place du titre est tronqué de la
même façon). L'en-tête répète la ligne de heading déjà présente
dans `text`. Ce doublon est accepté : la règle reste la même pour tous les
passages, et c'est ce qui permet à un morceau `Part ≥ 2` de garder son
contexte.

### 4.2 Ce que l'export omet

L'omission se décide sur les **sections d'origine, avant le budget** : une
section dont le `Text`, privé de sa ligne de heading, ne contient que des
blancs est omise entière, et le préambule vide aussi. Embedder un heading nu ne
sert à rien. Une section non omise qui est redécoupée émet tous ses morceaux :
d'après §3.3, aucun n'est un heading seul. Les sections omises **comptent**
quand même dans le calcul des ids (§4.3) : qu'un frère devienne vide ne change
pas l'id des autres.

### 4.3 Identité

**Slug** (règle GitHub, appliquée au contenu ATX brut du heading, §3.1) : minuscules en
culture invariante. On garde lettres, chiffres, `-` et `_` (lettres Unicode
comprises), on remplace espaces et tabulations par `-`, et on retire le reste.
Le résultat est **coupé à 64 points de code**. Un slug vide devient `section`.
L'alphabet d'un slug ne contient donc **jamais** `.`, `~`, `/` ni `#`. Il n'y a
pas de normalisation Unicode : deux graphies équivalentes (accent composé ou
décomposé) donnent deux slugs différents, sans casser l'unicité.

**Définition récursive.** Sur la sortie **complète** du découpeur (avant
omission) :

- le **segment** d'une section est son slug si c'est la première, parmi les
  sections ayant le même parent, à porter ce slug ; c'est `slug.k` si c'est la
  k-ième (k ≥ 2) ;
- le **chemin** d'une section est le chemin de son parent, suivi de `/` et de
  son segment (le chemin d'une section sans parent est son seul segment) ;
- `sectionId = <conceptId>#<chemin>`. Pour le préambule, `<conceptId>#` ;
- `id = sectionId` si `Part = 0`, sinon `sectionId~<Part>`. Le préambule
  redécoupé donne `<conceptId>#~1`, `<conceptId>#~2`…

Le parent d'une section est le heading précédent de niveau strictement
inférieur (§3.1), et son chemin est déjà désambiguïsé. Un segment d'occurrence
ne peut pas coïncider avec un slug naturel, puisque `.` n'apparaît jamais dans
un slug : `# A`, `# A`, `# A-1` donnent `a`, `a.2`, `a-1`. Deux slugs tronqués à
64 qui coïncident sont départagés par `.k` de la même façon. Le calcul est
linéaire, avec un compteur par (chemin du parent, slug).

**Ce que garantit la stabilité, précisément.** Le `sectionId` d'une section ne
dépend que du `conceptId` et, pour elle-même et chacun de ses ancêtres, de son
slug et de son rang d'occurrence sous son parent. Mais **quelles lignes sont des
headings, et qui est le parent de qui, dépend de la structure de blocs** du
body. Une modification qui ne touche aucune ligne de heading peut donc changer
des ids si elle change cet arbre : insérer une fence `~~~` qui englobe `# B`
fait de `# B` du code, et son enfant `## C` passe sous `A`, de `c#b/c` à
`c#a/c`. Les garanties qui suivent valent pour les modifications qui
**préservent les headings reconnus, leurs parents et leurs rangs
d'occurrence**. D'où :

- modifier le **contenu** d'une section (hors lignes de heading) ne change
  aucun `sectionId`, ni le sien ni celui d'une autre section : un slug ne
  dépend que de la ligne de son heading (§3.1) ;
- renommer une section change son propre `sectionId` et ceux de tous ses
  descendants, sauf si son slug ne change pas (`A!` → `A?`) ;
- insérer, retirer ou renommer une section change le rang d'occurrence de ses
  frères homonymes suivants, donc leurs `sectionId` **et ceux de tous leurs
  descendants** : avant `# A`/`## X`/`# A`/`## X`, le second `X` est `a.2/x` ;
  insérer un `# A` entre les deux le fait passer à `a.3/x`, alors que son propre
  rang sous son parent n'a pas bougé.

L'`id` d'un morceau (`~n`) dépend en plus du budget, de la mesure et du
contenu de sa section : modifier le contenu peut changer le nombre de morceaux.
Seul `sectionId` est indépendant du budget, et c'est lui qui sert à joindre un
export et une recherche faits avec des budgets différents. Cette jointure est
de un à plusieurs : une section, plusieurs morceaux.

L'`id` est unique **dans un bundle**. Plusieurs bundles peuvent produire le
même `id` : un consommateur qui en mélange plusieurs doit préfixer lui-même
(nom de bundle ou de source). C'est documenté, sans champ dédié.

**Borne de taille.** Avec les slugs coupés à 64 et les `headings` tronqués à
200, la **contribution des headings** à une ligne (dans `headings`, `id`,
`sectionId` et l'en-tête de `embedText`) est bornée par la profondeur, quelle
que soit leur longueur dans le body. Un heading d'1 Mo suivi de 10 000
sous-sections ne génère donc plus 30 milliards de caractères. Le reste de la
ligne n'est **pas** borné par la profondeur : `text`, les métadonnées du
concept répétées sur chacun de ses passages (`conceptId`, `type`, `title`,
`tags`) et les chiffres des compteurs d'occurrence (`a.10000`). La taille d'une
ligne est donc O(|text| + |métadonnées du concept| + profondeur ×
(264 + chiffres d'occurrence)). Ce design ne tronque pas les métadonnées : un
concept aux tags géants les répète sur chaque passage.

### 4.4 Péremption : une échéance, pas un booléen

Un index construit aujourd'hui est interrogé dans trois mois. Un booléen
`isStale` calculé à l'export serait faux le jour où il compte. Le pipeline
filtre donc au moment de la requête, de deux façons :

- **`staleAfterEpochMs`** (nombre entier) se compare à `now_ms`, l'heure
  courante en **millisecondes Unix entières, arrondie à l'inférieur**
  (`time.time_ns() // 1_000_000` en Python, sans passer par un flottant). C'est le seul filtre portable :
  certaines bases (Pinecone, par exemple) ne comparent que des nombres. Le
  filtre est quantifié à la milliseconde : une échéance à `…:00.0000001Z` est
  arrondie à la milliseconde suivante, et le concept passe pour périmé au plus
  une milliseconde après l'échéance exacte, jamais avant. C'est le même choix
  que le viewer (`staleAfterMs`), qui compare lui aussi à une horloge en
  millisecondes entières.
- **`staleAfter`** (chaîne ISO UTC à largeur fixe, 7 décimales, sans perte)
  donne la pleine précision. Elle sert aux bases qui ont un type date, et à la
  lecture humaine. Comparée comme une chaîne, elle n'est correcte que si `now`
  est formaté exactement pareil (`yyyy-MM-ddTHH:mm:ss.fffffffZ`). La
  documentation le dit et donne la construction de `now`.

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
`conceptId`, et c'est documenté. Un ajout de champ suit la règle de §4.7.

### 4.7 Version du format

Chaque ligne commence par `"format": "okf-passages/<n>"`. Le JSONL n'a pas
d'en-tête de fichier, donc la ligne est le seul endroit où mettre la version.

**Règle de lecture, qui fait partie du contrat** : un lecteur d'une version
`n` **doit ignorer les propriétés qu'il ne connaît pas**, et refuser une
version qu'il ne connaît pas. Sur cette base :

- **`n` ne change pas** pour un champ ajouté. L'ajout est noté au CHANGELOG.
  Un consommateur qui valide avec un schéma fermé doit le déclarer ouvert
  (`additionalProperties: true` en JSON Schema, par exemple). La documentation
  le dit.
- **`n` augmente** pour tout le reste : un champ retiré ou renommé, un type ou
  un sens changé, et toute modification d'une règle qui change les ids, les
  `embedText` ou le découpage d'un contenu inchangé (points de coupure §3.1,
  normalisation §3.2, budget §3.3, en-tête et troncature §4.1, omissions §4.2,
  ids §4.3).

La première version publiée est `okf-passages/1`.

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

Trois cas, dans cet ordre de priorité. L'ordre est garanti par construction :
les arguments, `--max-chars` compris, sont entièrement validés **avant** le
chargement, et le chargement est terminé **avant** la première ligne écrite.

1. **Erreur d'usage** (verbe, flag, `--max-chars`) : rien sur stdout, message
   sur stderr, code 1. Le bundle n'est pas lu.
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

- **Propriété de partition** (`Concat(Text) == N(body)`) sur chaque concept de
  `bundles/` et de `tests/fixtures/`, et sur chaque cas construit, y compris
  avec et sans `\n` final, avec un `\r` isolé et avec un surrogate isolé.
- **Points de coupure**, avec pour chaque cas l'entrée exacte et le découpage
  attendu :
  - indentation 0 à 3 contre 4 ;
  - `#x`, `#` seul, fermeture `#` après une tabulation ;
  - heading dans une citation ou un élément de liste (pas de coupure) ;
  - `#` dans un bloc HTML `<pre>` (pas de coupure) ;
  - fence ouverte dans un élément de liste ;
  - fence indentée de 4, fermeture indentée de 4 ;
  - info string contenant un backtick ;
  - fence fermante suivie d'un U+00A0 (pas de fermeture, §2.3 A) ;
  - bloc non fermé ;
  - setext (pas de coupure) ;
  - saut de niveau et dépilement.

  Les trois contre-exemples de la revue Codex sur `ComputationExtractor` sont
  repris tels quels.
- **Texte des headings** : `# **A** &amp; B`, `# [A](x)`, fermeture après une
  tabulation, figés ; et `"# [A][r]\nx\n# Z\ny\n"` contre la même chaîne suivie
  de `"\n[r]: /target\n"` : même `sectionId` pour la première section.
- **Section sans bloc sous budget** : `Split("", "T", new PassageBudget(1))`
  (`Part = 0`, `Oversize = true`), et une section faite seulement de lignes
  blanches.
- **Budget** :
  - section qui tient entière avec un budget (`Part = 0`) ;
  - regroupement glouton ;
  - le cas du séparateur oublié (titre `T`, body `"a\n\nb"`, `MaxSize = 5`) ;
  - blocs de code délimités, indentés (`"    a\n\n    b\n"`, jamais coupé) et
    HTML atomiques ;
  - rattachement des lignes blanches en tête, après le heading, entre blocs ;
  - section qui n'a que sa ligne de heading ;
  - préambule redécoupé ;
  - `Oversize`, `Part`.

  Une mesure qui **enregistre ses arguments** prouve que le préfixe est mesuré
  une fois avec sa ligne vide, que chaque bloc est mesuré une fois et que
  chaque regroupement est vérifié. Une mesure non additive (test double)
  exerce la dichotomie et sa terminaison. Comptage en points de code (emoji,
  CJK) contre `string.Length`.

### 6.2 Entrées hostiles

Le découpeur lit du markdown non fiable. On exécute donc des entrées
construites pour l'attaquer, on ne se contente pas de relire :

- 100 000 headings ;
- 10 000 frères homonymes ;
- les collisions `# A`/`# A`/`# A-1` et `!!!`/`???`/`section-1`, et deux
  headings de plus de 64 points de code qui partagent leurs 64 premiers, avec
  des ids qui doivent rester tous distincts ;
- une ligne de 10 Mo ;
- un heading d'1 Mo suivi de 10 000 sous-sections : on vérifie la borne de
  §4.3 sur chaque ligne ;
- le cas glouton de la passe 1 (N blocs `x`, budget `3N/2 + 3`) ;
- le cas dichotomique de la passe 2 (N blocs `x`, mesure non additive qui force
  la coupure jusqu'au singleton) ;
- des blocs de code non fermés ou alternés ;
- des titres pleins de caractères C0/C1, bidi et de surrogates isolés.

**La linéarité se prouve en comptant** les caractères soumis à `Measure` et
les lignes visitées, à plusieurs tailles d'entrée. On ne chronomètre jamais, et
on ne teste pas un ratio entre deux tailles (§3.3). Avec la mesure par défaut,
on vérifie pour chaque entrée la majoration explicite
`mesurés ≤ 2 × |N(body)| + 3 × |sortie|`. Avec la mesure non additive, on
vérifie `mesurés ≤ c × (|N(body)| + |sortie|) × (1 + log₂ k)` pour une
constante `c` fixée dans le test. Pour le JSON, on reparse
chaque ligne, on compare **les valeurs** aux attentes, et on recalcule
`embedHash` depuis l'`embedText` reparsé. Vérifier seulement que le JSON se
parse ne suffit pas.

### 6.3 `PassageExportTests`

- `trust`, `reviewOutdated`, `status`, `statusKnown`, `staleAfter`,
  `staleAfterEpochMs` et `staleAfterMalformed` correspondent à l'`AuditFinding`
  et au `Lifecycle` du même concept ;
- les concepts malformés : `status: retired` / `stale_after: not-a-date`, et
  `status: [deprecated]` / `stale_after: { bad: value }` (après §2.3 C), avec
  dans ce dernier cas la liste exacte des diagnostics de `okf validate` :
  `StatusNotScalar` et `StaleAfterNotScalar`, une fois chacun, et pas de
  `StatusUnknown` ; plus les cas `null`, `~`, valeur vide non citée et `""`
  cité, pour chaque champ, contre le tableau de §2.3 C ;
- la stabilité structurelle : l'exemple de la fence qui change le parent de
  `## C` (§4.3) ;
- `reviewOutdated` avec `generated.at`, avec seulement le `timestamp`
  historique, sans relecture, et avec une relecture illisible ;
- la normalisation UTC et l'arrondi au supérieur de l'epoch ;
- l'ordre de sortie ;
- les omissions (§4.2), décidées avant le budget, et le fait qu'une section
  omise compte dans les rangs d'occurrence ;
- `ordinal`, `sectionId`, les ids récursifs (exemple `a.2/x` → `a.3/x`), les
  morceaux du préambule ;
- la troncature des `headings` et du titre (200 points de code, ellipse
  comprise) ;
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
  champs, et le `text` égal à la tranche de `N(body)`.
- Les trois cas de §5.2, chacun avec son stdout, son stderr et son code, **plus
  leurs combinaisons** : erreur d'usage sur un bundle illisible (le bundle
  n'est pas lu), échec de chargement avec des `ParseProblems` (rien sur
  stdout).
- `--max-chars` invalide : code 1 ; `okf passages --help` : code 0.
- L'ajout de `stderr` aux handlers ne change aucun golden existant.
- **CI** : un appel `okf passages` dans le smoke test Native AOT de `ci.yml`,
  qui vérifie que la sortie se parse et contient les champs attendus. Le JSON
  doit survivre au trimming, ce que les tests en processus ne prouvent pas.

## 7. Documentation

- README : une section `okf passages`, avec un exemple de filtre côté pipeline
  (`status != "deprecated" and statusKnown and not staleAfterMalformed and not
  reviewOutdated and (staleAfterEpochMs is None or staleAfterEpochMs > now_ms)`,
  avec `now_ms = time.time_ns() // 1_000_000`). La section reprend aussi les règles
  du consommateur de §4.5, la règle de lecture de §4.7, le préfixage
  multi-bundle (§4.3), la jointure pour les champs exclus (§4.6) et la
  publication conditionnelle (§5.2).
- CHANGELOG `[Unreleased]` : le verbe, et les trois corrections de §2.3 (dont
  le changement sémantique de C).
- `CLAUDE.md` : `PassageSplitter` comme découpeur unique, et l'usage de la
  passe de blocs de `LinkScanner` (headings et intervalles de blocs) comme
  seule grammaire markdown, sur le modèle de `LfLines`.
- Rien dans `docs/spec-conformance/` : la spec ne dit rien de l'export, donc il
  n'y a aucune divergence à documenter.

## 8. Hors périmètre

- La conformité CommonMark des règles de fences de `ComputationExtractor`
  (§2.2), qui relève de §10.
- `ConceptSearch.RoundRobin`, quadratique sur des familles de tailles très
  inégales : issue #279. L'export ne l'utilise pas.
- Le `\r` isolé comme fin de ligne (CommonMark §2.1) : limite partagée avec
  `LinkScanner` et `LfLines` (§3.2).
- Un vrai texte rendu pour les headings (§3.1).
- La troncature des métadonnées répétées sur chaque passage (`tags`, `title`,
  `type`) : leur taille n'est pas bornée (§4.3).

## 9. Revue externe

### 9.1 Passe 1

La revue Codex (`gpt-6-astra`, effort high) a remonté 22 constats. Leur
intégration :

| Constat | Décision |
|---|---|
| Collision d'ids par suffixe d'occurrence | séparateur `.` hors alphabet (§4.3) |
| Règles de fences de `ComputationExtractor` non conformes | passe de blocs de `LinkScanner` (§2.2) |
| Promesse de stabilité trop large | stabilité qualifiée (§4.3), reprise en passe 2 |
| Ids de recherche et d'export divergents avec le budget | `sectionId` indépendant du budget (§4.1, §4.3) |
| Linéarité non prouvée par le comptage d'appels | comptage en caractères (§3.3, §6.2), repris en passe 2 |
| Amplification mémoire par les en-têtes | troncature + émission au fil de l'eau (§4, §4.1), reprise en passe 2 |
| `ParseProblems` ne couvre pas tous les fichiers illisibles | trois cas d'erreur explicites (§5.2) |
| Filtre `staleAfter` non portable | `staleAfterEpochMs` (§4.4) |
| Le hash n'est pas un protocole de synchronisation | renommé `embedHash`, règles du consommateur (§4.5) |
| Champs exclus présentés comme redondants | `reviewOutdated` ajouté, jointure documentée (§4.6) |
| Métadonnées invalides indiscernables | `statusKnown`, `staleAfterMalformed` (§4.1), repris en passe 2 |
| Moment de l'omission non défini | avant le budget (§4.2) |
| Modèle de lignes et sens de « verbatim » | `Document.Body`, `SplitSpans`, `N` (§3.2) |
| Grammaire ATX hors fences simples | couverte par la passe de blocs (§3.1) |
| Surrogates isolés | remplacés par U+FFFD dans `N` (§3.2) |
| Ordre, version et namespace pour un consommateur | `ordinal` et `format` ajoutés, namespace documenté (§4.1, §4.3, §4.7) |
| Sortie partielle publiée malgré le code 1 | publication conditionnelle documentée (§5.2) |
| `RoundRobin` quadratique | issue #279, hors périmètre (§8) |
| Constats propres au barreau 4 | intégrés dans `2026-11-18-passage-search-design.md` |

### 9.2 Passe 2

Aucun nouveau bloquant. Les constats et leur décision :

| Constat | Décision |
|---|---|
| La passe de `LinkScanner` ferme une fence suivie d'un espace Unicode | prérequis A, trois sites corrigés (§2.3) |
| La couture des headings ne donne pas les blocs atomiques (code indenté) | prérequis B, intervalles de blocs (§2.2, §2.3) |
| Les drapeaux ignorent les valeurs non scalaires | prérequis C, `Lifecycle` corrigé (§2.3) |
| Amplification restante dans `headings`, `id`, `sectionId` | slugs coupés à 64, `headings` tronqués à 200, borne (§4.1, §4.3) |
| Stabilité des ids encore mal décrite, `Part` ambigu | définition récursive, `Part = 0` si la section tient (§3.3, §4.3) |
| La somme du budget oublie le séparateur | préfixe mesuré avec sa ligne vide (§3.3) |
| Test de linéarité trop strict pour une mesure quelconque | deux garanties distinctes, médiane explicite, terminaison (§3.3, §6.2) |
| « Texte visible » n'est pas le texte rendu | projection définie et figée par des exemples (§3.1) |
| Rattachement des blancs et des headings incomplet | règles et exceptions écrites (§3.2, §3.3) |
| Surrogates contre l'invariant | normalisation unique `N`, invariant sur `N(body)` (§3.2) |
| Ellipse hors des 200 points de code | ellipse comprise, bornes recalculées (§4.1) |
| Règle de version contradictoire | lecteurs tenus d'ignorer l'inconnu (§4.7) |
| `now_ms` non entier, `reviewOutdated` sans `timestamp` | `now_ms` entier arrondi à l'inférieur, définition complète (§4.1, §4.4) |
| Audit matérialisé, mutation pendant l'énumération | mémoire annoncée, précondition (§4) |
| Priorité des erreurs non testée en combinaison | ordre garanti par construction, tests combinés (§5.2, §6.4) |

### 9.3 Passe 3

Aucun bloquant. Les constats et leur décision :

| Constat | Décision |
|---|---|
| La tâche C produirait un diagnostic parasite et ne signalerait pas `stale_after` non scalaire | nouveaux drapeaux sans changer les membres existants, warning `StaleAfterNotScalar` ajouté (§2.3 C) |
| Le slug dépend des définitions de liens de tout le body | contenu ATX brut de la ligne (§3.1, §4.3) |
| La borne de taille couvre plus que les headings | garantie limitée à leur contribution, reste annoncé (§4.3, §8) |
| Section sans bloc sous budget non définie | `Part = 0`, `Oversize` selon l'`embedText` (§3.3) |
| Le test de doublement contredit la borne | majorations explicites, `1 + log₂ k` (§3.3, §6.2) |
| « Rien ne change au chargement » trop absolu | effet voulu de A sur les liens annoncé et testé (§1.2, §2.3 A) |
| Construction unique du cache face à l'éviction | garantie par période de résidence (second document, §3.2) |

### 9.4 Passe 4

Aucun bloquant ni majeur. Verdict de Codex : **prêt pour un plan
d'implémentation, avec réserves**. Les réserves et leur décision :

| Réserve | Décision |
|---|---|
| Deux contrats pour le champ `title` | champ intégral, seule sa copie dans l'en-tête est tronquée (§4.1) |
| Stabilité des ids face aux changements de structure | garantie qualifiée par la préservation de l'arbre des headings (§4.3) |
| `YamlNull` et les nouveaux drapeaux | séquences et mappings seulement, tableau attendu (§2.3 C) |
| Helper ATX à nommer | `AtxHeadingContentAt` partagé, cas figés (§3.1) |
| Unité et périmètre de la majoration | sortie du découpeur avant omissions, en points de code (§3.3) |
