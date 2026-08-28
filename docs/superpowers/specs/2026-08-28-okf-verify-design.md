# Design — `okf verify` : enregistrer une relecture, refermer la boucle d'audit

Date : 2026-08-28
Statut : validé en brainstorming (design approuvé section par section, second avis
indépendant intégré), prêt pour le plan d'implémentation

## 1. Objectif

`okf audit` (spec du 2026-08-21) a donné au bundle sa première question
corpus-level : *« quels concepts ont dépassé leur `stale_after` sans qu'un humain
les ait jamais relus ? »*. Il rend une worklist — mais cette worklist n'a pas de
sortie. Le champ `verified` (§5.2), dont §5.3 dérive mécaniquement le tier de
confiance et dont `okf audit` dérive toute sa sélection, n'a **aucun chemin
d'écriture gouverné** : la seule façon d'enregistrer une relecture est d'éditer
le YAML à la main ou de réécrire un frontmatter entier via `okf_write_concept`.
Résultat : personne ne le fait, rien n'est jamais vérifié, et chaque finding
d'audit est un constat sans remède.

`okf verify` est le geste qui referme la boucle : constat → relecture →
estampille → le concept change de tier au passage d'audit suivant.

```sh
okf audit b --stale --trust unverified,machine-confirmed \
  | cut -d' ' -f1 \
  | okf verify b --by human:julien -
```

— la question de l'article et sa réponse, jointes en une ligne. La friction que
cette feature supprime est celle d'*enregistrer* la relecture ; la *relecture*
elle-même n'a jamais été automatisable, et tout design qui automatise
l'enregistrement sans la relecture produit la promotion de masse qui vide la
worklist (voir §2 et §10).

### 1.1 Trois faits du code qui conditionnent le design

Vérifiés dans la base, pas supposés :

- **Une estampille exprime deux choses et rien d'autre.**
  `Stamp(Actor? By, string? At)` ([Trust.cs:7](../../../src/OKF4net/Trust.cs#L7)) :
  pas de sujet, pas de portée, pas de liaison au contenu relu.
- **Le tier ignore la date.** `Trust.DeriveTier` est `Any(IsHuman)`
  ([Trust.cs:38-46](../../../src/OKF4net/Trust.cs#L38-L46)) — une estampille
  humaine de 2019 vaut `human-reviewed` pour toujours. Notre propre golden le
  montre : `metrics/dau` y est à la fois `human-reviewed` et dans la worklist.
- **Rien ne dit quand un contenu a bougé.** `MaybeStampGenerated` n'écrit
  `generated` que s'il est **absent**
  ([BundleConceptWriter.cs:565-574](../../../src/OKF4net/BundleConceptWriter.cs#L565-L574)) ;
  toute mise à jour le reporte inchangé.

Conséquence assumée partout dans cette spec : une estampille atteste **un moment,
pas une version** du contenu. La question « le contenu a-t-il bougé depuis la
relecture ? » se répond hors bibliothèque, par
`git log -1 --format=%cI -- <chemin>` comparé à `max(verified[].at)` — le dossier
est canonique et son historique est celui de git, pas du YAML. **Aucune extension
de schéma** (`digest`, `scope`, `note` dans l'estampille) ne sera acceptée pour
recréer cette information dans le bundle ; c'est la réponse sanctionnée, à
documenter pour qu'elle ne soit pas re-proposée.

## 2. Le modèle de confiance — décisions actées

Ces décisions ont été prises explicitement par l'utilisateur pendant le
brainstorming ; elles priment sur toute intuition contraire d'un implémenteur.

**Une estampille est une déclaration datée et signée, pas une preuve.** Aucun
outil zéro-dépendance ne peut authentifier qui l'a écrite : pas de crypto, pas
de fournisseur d'identité. `--by human:quelquun-dautre` est possible et le
restera. Ce qui rend une estampille crédible, c'est **où elle atterrit** — dans
un diff relu sous protection de branche — pas l'outil qui l'a produite. Le
mécanisme recommandé (documentation, pas code) : le relecteur ou l'auteur lance
`okf verify` localement, la PR contient la ligne
`+ - { by: human:alice, at: … }`, le relecteur voit l'affirmation et peut la
refuser. **Jamais** l'inverse — inférer l'estampille d'une approbation GitHub
transforme « un humain a approuvé ce diff » en « un humain se porte garant de
cette connaissance », deux choses différentes chaque fois qu'une PR touche un
fichier pour une autre raison que le relire (c'est-à-dire presque toujours).

**Pas de garde sur `okf_write_concept`** (décision utilisateur, 2026-08-28).
`okf_write_concept` écrit un frontmatter complet, estampilles comprises : c'est
son contrat — importer un bundle, corriger un concept, reporter une relecture
existante en font partie. Le brider sur `verified` le casserait pour ces usages
légitimes, et ce n'est pas à un outil d'écriture générique de porter une
politique de confiance. En contrepartie, la documentation dit sans détour que ce
chemin existe.

**Le tool agent `okf_verify` est symétrique au CLI** (décision utilisateur,
2026-08-28) : mêmes acteurs acceptés, `human:` compris. Cohérence du modèle —
si l'estampille est une déclaration, un agent n'a pas moins le droit de la
transcrire qu'un shell. La conséquence est documentée, pas cachée : un modèle
*peut* écrire une estampille `human:` ; c'est le diff relu qui fait foi.
Symétrie oblige, `okf_verify` est un tool **mutateur** : il entre dans
`WriteToolNames` et disparaît d'un déploiement MCP read-only.

**Ce que la v1 garantit, et rien de plus** : toute estampille écrite par cette
chaîne d'outils est bien formée (§7), datée en UTC, porte exactement sur les
concepts nommés par l'appelant, et atterrit dans un fichier que git versionne.
Ce qu'elle ne garantit pas — l'identité réelle du signataire, le fait qu'il ait
lu quoi que ce soit — est annoncé comme hors de portée, dans le README et dans
l'aide du verbe.

## 3. Périmètre

**Dans le périmètre** — quatre unités, la première étant un prérequis
d'infrastructure que la relecture de cette spec a révélé nécessaire :

0. **Deux extensions du CLI, sans lesquelles la grammaire de §5 est
   inexprimable** (voir §3.1).
1. `BundleConceptWriter.RecordVerification` + le primitif atomique de
   read-modify-write sur le **frontmatter** (il n'existe aujourd'hui que pour le
   corps).
2. Le verbe CLI `okf verify`.
3. Le tool agent `okf_verify` (mutateur).

### 3.1 Unité 0 — les deux prérequis du CLI

**`CliArgs` ne porte qu'un seul positionnel.** `_positional` est un `string?`
([OkfCli.cs:151](../../../src/OKF4net.Cli/OkfCli.cs#L151)) et `Positional(what)`
le rend seul ; les huit verbes existants prennent tous exactement un positionnel
(`<bundle>` ou `<file>`). `verify` est le premier à en vouloir N
(`<bundle> <id>…`). Le scanner doit donc exposer, en plus, la **liste ordonnée**
des positionnels suivants — `Rest()` ou équivalent, tokens dans l'ordre, ceux
d'après `--` inclus.

Note d'honnêteté, parce qu'un futur relecteur la posera : cette liste **a
existé**, et a été réduite à un champ unique le 2026-08-22 lors d'une passe
`/simplify`, au motif exact que rien ne lisait jamais au-delà du premier
élément. C'était vrai à ce moment-là. La restaurer n'annule pas cette
simplification, elle répond à un besoin qui n'existait pas encore — et le champ
unique redevient ce qu'il aurait dû rester : un cas particulier de la liste, pas
son remplaçant.

**`OkfCli.Run` ne reçoit pas stdin.** Sa signature est
`Run(string[] args, TextWriter stdout, TextWriter stderr)`
([Program.cs:17](../../../src/OKF4net.Cli/Program.cs#L17)) et rien dans le CLI
ne lit `Console.In` aujourd'hui. Or la forme `-` de §5.1 — la ligne qui referme
la boucle — en dépend, et les tests pilotent le CLI **en processus** via
`TestPaths.Run` : sans seam, le chemin stdin ne serait testable qu'en lançant un
sous-processus, ce que la suite ne fait nulle part.

Décision : ajouter un paramètre `TextReader stdin` à `OkfCli.Run`, câblé à
`Console.In` par `Program.Main` et à un `StringReader` par les tests. C'est un
changement de signature d'une API publique (`OkfCli.Run` est le point d'entrée
unique, documenté comme tel) : il casse tout appelant externe, doit figurer au
CHANGELOG comme rupture, et `TestPaths.Run` gagne une surcharge pour que les
~60 appels existants restent inchangés.

Alternative écartée : lire `Console.In` directement dans `CmdVerify`. Moins de
surface remuée, mais le chemin le plus important de la feature deviendrait le
seul non couvert par la suite — exactement le trou que la spec d'audit a payé
cher ailleurs.

**Hors périmètre, consigné au ROADMAP** (voir §10 pour les raisons) : l'audit
conscient du temps (exposer les estampilles dans `AuditFinding` pour demander
« human-reviewed, mais depuis quand ? ») ; toute GitHub Action ; `--remove` ;
`--json` ; `--stale-after` ; toute amélioration de l'émetteur YAML.

## 4. Unité 1 — le cœur

### 4.1 API publique

Méthode ajoutée à `BundleConceptWriter` (classe existante) :

```csharp
    /// <summary>
    /// Enregistre une relecture : ajoute ou remplace l'entrée `verified` de
    /// l'acteur <paramref name="by"/> sur le concept, en préservant tout le
    /// reste du frontmatter et le corps. Erreurs rendues en chaîne (errors-as-
    /// data), null en cas de succès — même contrat que WriteConcept.
    /// </summary>
    /// <param name="conceptId">L'id du concept (chemin sans .md).</param>
    /// <param name="by">L'acteur §7, requis, bien formé.</param>
    /// <param name="at">Horodatage ISO-8601 UTC ; null ⇒ UtcNow formaté.</param>
    public string? RecordVerification(string conceptId, string by, string? at = null);
```

Un seul écrivain gouverné, appelé par le CLI et par le tool — le partage retenu
pour `ConceptAudit` (calcul commun, présentations distinctes) s'applique ici à
l'écriture.

### 4.2 Sémantique, point par point

- **Dernière estampille par acteur — ni journal, ni état.** Si `verified`
  contient déjà une entrée dont `by` est **textuellement identique** (comparaison
  ordinale du `Raw`) à l'acteur donné, cette entrée est réécrite **à sa
  position** ; sinon l'entrée `{ by, at }` est ajoutée en fin de liste.
  Mécaniquement : `YamlSequence` est immuable (`Items` est un
  `IReadOnlyList<YamlValue>` fixé au constructeur,
  [YamlValue.cs:255-266](../../../src/OKF4net/Yaml/YamlValue.cs#L255-L266)), donc
  on reconstruit une séquence en recopiant les items dans l'ordre et en
  substituant celui qui correspond, puis on la repose sous la clé `verified` via
  `YamlMapping.Insert` — qui remplace en place et **préserve la position de la
  clé** dans le frontmatter
  ([YamlMapping.cs:59-74](../../../src/OKF4net/Yaml/YamlMapping.cs#L59-L74)).
  Deux niveaux, deux mécanismes : ne pas confondre la position de l'estampille
  dans la séquence avec celle de `verified` dans le mapping.
  L'écrivain ne touche **jamais** l'entrée d'un autre acteur : un `process:`
  ne peut pas dégrader une relecture humaine en la remplaçant. Pourquoi pas un
  journal : l'émetteur YAML coûte trois lignes par estampille et le frontmatter
  part dans le contexte des agents à chaque lecture — un vérificateur
  `process:nightly` quotidien produirait ~1100 lignes par concept et par an.
  Pourquoi pas un état (liste remplacée) : le modèle est pluriel par
  construction (`DeriveTier` est un `Any`) et remplacer effacerait le jugement
  des autres acteurs. Ce qui est perdu — la cadence des relectures — a déjà sa
  place : `log.md` (§9).
- **Convention d'écrivain, pas règle de lecteur.** §5.2 décrit une liste et ne
  dit rien de l'unicité par acteur. `ParseVerified` continue d'accepter les
  doublons de tout autre producteur — même asymétrie strict-en-entrée /
  permissif-en-lecture que la spec d'audit §4.1.
- **Validation à l'écriture : conformité §11, pas mode producteur.**
  `RecordVerification` appelle `ValidateConformance()` (type non vide,
  [OkfDocument.cs:158](../../../src/OKF4net/OkfDocument.cs#L158)), **pas**
  `Validate()`. Divergence délibérée avec `WriteConcept` : `verify` ne produit
  pas de contenu, il enregistre la relecture d'un contenu qu'il n'a pas écrit ;
  refuser d'enregistrer parce qu'un tiers a omis une `description` substituerait
  une politique éditoriale au jugement du relecteur — et rendrait inestampillable
  précisément les concepts que la worklist remonte.
- **`by` : requis, bien formé.** `Actor.Parse(by).IsWellFormed` doit être vrai —
  la chaîne `human:` nue (qui promeut pourtant le tier, `IsHuman` étant
  insensible à la bonne formation) est rejetée à l'écriture. Strict en entrée,
  permissif en lecture, comme partout.
- **`at` : toujours écrit.** Fourni ⇒ validé par
  `BundleValidator.IsIso8601DateTime` (public,
  [Validate.cs:618](../../../src/OKF4net/Validate.cs#L618) — le prédicat du
  validateur lui-même, pour que `verify` ne puisse jamais écrire ce que
  `validate` avertirait) ; absent ⇒ `OkfTimestamp.FormatUtc(UtcNow())` via le
  seam d'horloge existant du writer
  ([BundleConceptWriter.cs:81](../../../src/OKF4net/BundleConceptWriter.cs#L81)),
  donc épinglable en test.
  **Élargissement de contrat à acter** : la doc de ce seam dit aujourd'hui
  « consulté uniquement quand `AutoStampGenerated` est vrai ». `RecordVerification`
  le consultera indépendamment de ce flag — c'est voulu (une seule horloge dans
  le writer, épinglée une seule fois en test), mais le commentaire XML doit être
  corrigé dans le même changement, sinon il ment.
- **`generated` n'est jamais touché.** Ni écrit, ni rafraîchi : une relecture
  n'est pas une génération, et la rafraîchir maquillerait la question « le
  contenu a-t-il bougé depuis ? » (§1.1).
- **Atomicité.** Nouveau primitif privé de read-modify-write sur le
  frontmatter, calqué sur `AppendToConceptAtomic`
  ([BundleConceptWriter.cs:347](../../../src/OKF4net/BundleConceptWriter.cs#L347)) :
  lecture, transformation, écriture sous une même détention du verrou par
  chemin. Deux `verify` concurrents sur le même concept ne peuvent pas se
  perdre une estampille. Les clés inconnues survivent (le `YamlMapping` ordonné
  garantit déjà le round-trip).
- **Erreurs-as-data.** Concept introuvable, id malformé, document non conforme,
  `by`/`at` invalides ⇒ chaîne d'erreur, jamais d'exception pour un cas attendu.

## 5. Unité 2 — le verbe CLI

### 5.1 Grammaire

```
okf verify <bundle> <concept-id>… --by <acteur> [--at <YYYY-MM-DDTHH:MM:SSZ>] [--dry-run]
okf verify <bundle> - --by <acteur>            # ids lus sur stdin, un par ligne
```

| Élément | Règle |
|---|---|
| `<concept-id>…` | Un ou plusieurs ids **explicites**. Aucune forme « tout le bundle ». |
| `-` | Seul id positionnel : les ids arrivent de stdin, un par ligne, lignes vides ignorées, chaque ligne trimée. Pas de mélange `-` + ids explicites. |
| `--by` | Requis, valué, acteur §7 bien formé. Aucun défaut, aucune variable d'environnement, aucune lecture de git config : l'outil n'invente jamais un auteur. |
| `--at` | Optionnel, valué, ISO-8601 ; défaut : UTC maintenant. Sert la transcription différée (CI future) et les goldens déterministes. |
| `--dry-run` | Affiche ce qui serait écrit, n'écrit rien, code 0. |

Le parsing passe par `CliArgs.Scan(args, "--by", "--at")` — les flags valués
déclarés au scan, le séparateur `--` honoré, comme pour les huit verbes
existants. **Tout-ou-rien** : les ids sont tous validés (existence, bonne forme)
avant la première écriture ; un id inconnu fait échouer la commande entière sans
rien écrire.

**Pourquoi pas de forme groupée** : `verify` et `validate` partagent leur
préfixe, s'autocomplètent l'un vers l'autre et signifient l'inverse (conformité
machine / endossement humain), alors que les deux prennent un bundle en premier
argument. Un `okf verify monbundle` — frappe erronée de `validate`, ou complétion
malheureuse — doit donc échouer bruyamment (`error: missing <concept-id>`)
plutôt que faire quelque chose de plausible sur tout le corpus. Et une forme `--all`
est le geste exact de la promotion de masse : lancée une fois à l'onboarding,
elle vide la worklist pour toujours et ressemble à un succès.

**Pourquoi pas `--stale-after`** : une commande qui à la fois affirme la
relecture et fait taire le détecteur est un bouton de renouvellement. La doc du
verbe répond explicitement à « comment sortir ce concept de la worklist de
péremption ? » : mettez à jour le contenu, puis son `stale_after` — deux gestes,
volontairement.

### 5.2 Sortie

Une ligne par concept, dans l'ordre donné, formulée comme un **enregistrement**
(« recorded »), pas comme une vérification — le verbe s'appelle `verify` par
fidélité au vocabulaire du champ (`verified`) et du tier (`human-reviewed`),
mais sa sortie ne surjoue pas ce qu'il fait :

```
recorded metrics/revenue  human:julien  2026-08-28T09:14:00Z
```

En `--dry-run`, `would record` remplace `recorded`. Aucune autre sortie sur
stdout. Quand l'acteur avait déjà une entrée, la ligne porte le suffixe
`  (replaces 2026-07-01T00:00:00Z)` — le remplacement est visible, pas
silencieux.

### 5.3 Codes de retour et messages exacts

- **0** : succès (y compris `--dry-run`) ; **1** : erreur d'invocation, id
  inconnu, bundle illisible — via `CliOperationException`, rendue `error: …`.

| Cas | stderr |
|---|---|
| aucun id | `error: missing <concept-id>` |
| `--by` absent | `error: verify requires --by <actor>` |
| `--by` sans valeur | `error: --by requires a value` (contrat `CliArgs`) |
| `--by` mal formé | `error: --by is not a well-formed §7 actor: "human:"` |
| `--at` invalide | `error: --at is not ISO-8601: "hier"` |
| id inconnu | `error: unknown concept "metrics/nope"` (et rien n'est écrit) |
| `-` mélangé à des ids | `error: "-" (stdin) cannot be combined with explicit concept ids` |

### 5.4 Le reflow, assumé et documenté

Écrire via le modèle ré-émet tout le frontmatter : sur un bundle écrit à la
main, le premier `verify` produit un diff de fichier entier pour un changement
d'une ligne (styles flow → block, commentaires supprimés — le parseur les
ignore). Décision v1 : **accepter et documenter** — passer `okf fmt -w` sur le
bundle une fois, en PR dédiée, après quoi les diffs de `verify` sont minimaux.
Rejeté explicitement : un patcheur textuel chirurgical de `verified`, qui
recréerait le second chemin d'écriture divergent que `BundleConceptWriter`
existe pour éliminer, en contournant verrous, gardes de reparse et validation.
L'amélioration de l'émetteur (séquences inline) est une piste séparée, au
ROADMAP.

## 6. Unité 3 — le tool agent `okf_verify`

```csharp
[Description("Record a review of one or more concepts: adds or replaces the caller's { by, at } entry in each concept's `verified` list. The stamp is a dated declaration, not a proof — same rules as the okf verify CLI verb.")]
public string Verify(
    [Description("Comma-separated concept ids (paths without .md). Explicit ids only — there is no whole-bundle form.")] string conceptIds,
    [Description("The §7 actor recording the review, e.g. human:alice, agent:assistant/1.0, process:nightly. Required, well-formed.")] string by,
    [Description("ISO-8601 UTC timestamp; omit for now.")] string? at = null)
```

- Symétrique au CLI : mêmes règles (`by` requis bien formé, ids explicites,
  tout-ou-rien, `at` validé), mêmes limites documentées (déclaration, pas
  preuve).
- **Dans `WriteToolNames`** : c'est un mutateur ; il disparaît en déploiement
  read-only, et le test existant qui épingle ce set passe de trois à quatre
  entrées.
- Corps sous `RunTool` ; entrées invalides ⇒ message d'usage rendu en chaîne
  (modèle `SearchUsageMessage`), jamais d'exception.
- La date vient du seam `UtcNow`/`Today` de `OkfBundleTools` via le writer —
  aucune horloge nouvelle.
- Rendu : les mêmes lignes `recorded …` que le CLI, une par concept. Rendu non
  partagé avec le CLI (dont les octets seront verrouillés par goldens), règle
  établie par la spec d'audit §5.

## 7. Tests

### 7.1 Cœur (`RecordVerificationTests`, nouveau)

1. Première estampille sur un concept sans `verified` : liste créée, `{by, at}`
   exacts, tout le reste du frontmatter et le corps byte-identiques par
   ailleurs ; clés inconnues préservées.
2. Même acteur re-vérifie : entrée remplacée **en place** (position dans la
   liste inchangée), pas d'ajout.
3. Acteur différent : ajout en fin, entrées existantes intactes. Cas des
   doublons pré-existants du même acteur (écrits par un autre producteur, que le
   lecteur permissif accepte) : **seule la première occurrence textuelle est
   remplacée**, les suivantes sont préservées — l'écrivain ne supprime jamais
   une entrée qu'il ne remplace pas, même redondante. Épinglé par ce test.
4. `by` mal formé (`human:`) rejeté ; `at` non ISO rejeté ; concept inconnu
   rejeté — tous en erreurs-chaîne.
5. Concept non conforme §11 (type vide) : rejeté ; concept conforme mais sans
   `description` : **accepté** (la divergence §4.2, épinglée).
6. `at` absent ⇒ `UtcNow` du writer, épinglé par le seam.
7. Le tier observé par `ConceptAudit` bascule : unverified → machine-confirmed
   (acteur `process:`) → human-reviewed (acteur `human:`) après estampille.
8. Concurrence : deux `RecordVerification` en parallèle sur le même concept,
   acteurs distincts ⇒ les deux estampilles présentes.
9. `generated` absent avant ⇒ toujours absent après ; présent avant ⇒
   byte-identique après.

### 7.2 CLI (`CliTests` + goldens)

10. Cas nominal multi-ids, `--at` épinglé : lignes `recorded` exactes, code 0.
11. Golden : `verify` sur une copie de `tests/fixtures/okf_v02` (TempDir — on ne
    modifie jamais une fixture), `--at` figé, sortie et fichier résultant
    comparés à un golden neuf écrit à la main, LF, provenance documentée dans
    `tests/fixtures/README.md` (pas de binaire de référence : verbe OKF4net).
12. stdin : `printf "a\nb\n" | okf verify b - --by …` estampille les deux ;
    lignes vides ignorées ; `-` + id explicite ⇒ erreur exacte.
13. Chaque message d'erreur de §5.3, byte-exact.
14. Tout-ou-rien : deux ids dont un inconnu ⇒ code 1, **aucun** des deux
    fichiers modifié.
15. `--dry-run` : sortie `would record`, fichiers byte-identiques.
16. Enchaînement de la boucle : `audit` (worklist non vide) → `verify` →
    `audit` (worklist réduite) — le test raconte la feature.
17. `--help` liste `verify` ; parsing : flags valués avant le positionnel ;
    `--` honoré.

### 7.3 Agents/MCP

18. `okf_verify` enregistré dans `GetTools()` ; **présent** dans
    `WriteToolNames` (le test des trois mutateurs passe à quatre) ; absent du
    toolset read-only.
19. Estampille réellement écrite via le pipeline AIFunction (liaison des
    arguments), et via une session MCP `CallToolAsync`.
20. `by` mal formé / ids vides ⇒ message d'usage, pas d'exception ; bundle
    supprimé après construction ⇒ `Error: …` via `RunTool`.
21. Schéma : `conceptIds` et `by` requis, `at` optionnel — épinglé comme pour
    `okf_audit`.

### 7.4 Unité 0 — les prérequis CLI

Numérotés à la suite bien que la tâche vienne en premier : la numérotation sert
à référencer un cas depuis le plan, pas à ordonner le travail.

22. `CliArgs` : plusieurs positionnels rendus **dans l'ordre** ; un seul ⇒ la
    liste a un élément et `Positional(what)` continue de rendre le premier
    (aucun des huit verbes existants ne change de comportement) ; aucun ⇒
    `Positional` lève toujours `missing <what>`.
23. `CliArgs` : les tokens après `--` entrent dans la liste des positionnels et
    **jamais** dans les flags — la règle établie le 2026-08-22 vaut aussi pour
    les positionnels au-delà du premier. Cas : `verify b -- --by` traite
    `--by` comme un id, pas comme un flag.
24. `OkfCli.Run` : le `TextReader` injecté est bien la source de la forme `-`
    (un `StringReader` en test produit les mêmes estampilles qu'une liste d'ids
    explicites), et un verbe qui ne lit pas stdin n'y touche jamais — aucune
    lecture bloquante introduite sur les huit verbes existants.

## 8. Documentation

- README : le verbe (avec l'enchaînement `audit | verify` en exemple), la ligne
  du tableau §5.2-§5.3 → `RecordVerification`, et l'encadré « déclaration, pas
  preuve » : ce que l'estampille garantit, ce qu'elle ne garantit pas, le fait
  qu'`okf_write_concept` peut aussi en écrire une, et le mécanisme recommandé
  (l'estampille dans le diff relu, jamais inférée d'une approbation).
- CHANGELOG sous `Unreleased`, avec **une entrée de rupture** pour la signature
  de `OkfCli.Run` (§3.1) : `OKF4net.Cli` est publié, et le point d'entrée gagne
  un paramètre.
- Site (`web/`) : ligne dans les deux tables de verbes + chapitre docs/Cli, avec
  sortie réelle capturée ; tables de tools (12e tool) dans les README Agents et
  Mcp + pages du site.
- `CLAUDE.md` : une ligne — `RecordVerification` est l'écrivain gouverné unique
  de `verified` ; ne pas en forker un second.
- ROADMAP : l'audit conscient du temps (le follow-up à plus forte valeur : il
  transforme les estampilles d'alibi permanent en signal qui décroît, et ne
  demande aucun chemin d'écriture) ; GitHub Action éventuelle ; émetteur YAML.

## 9. Contraintes respectées

Zéro dépendance (BCL seul, aucun `PackageReference`) ; SPDX + file-scoped
namespaces + XML doc + nullable + warnings-as-errors ; Native AOT sans reflexion
nouvelle ; aucune fixture existante modifiée, goldens neufs manuscrits et
documentés ; aucune sortie existante ne bouge (aucun golden actuel ne couvre
`verify`) ; spec v0.2 : aucun champ nouveau, aucune clé nouvelle dans
l'estampille — la seule convention ajoutée (unicité par acteur à l'écriture) est
côté écrivain et documentée comme telle.

## 10. Alternatives écartées

**A. GitHub Action qui estampille les fichiers touchés par une PR approuvée.**
L'idée d'origine de l'article — écartée comme mécanisme : « touché par un commit
approuvé » n'est pas « lu et endossé par une personne ». Un `okf fmt -w`
bundle-wide, une régénération d'index ou un bump de `stale_after` promouvrait
tout le corpus au tier maximal, et la worklist vide ressemblerait à un succès.
Le lieu (la review) était le bon ; le mécanisme retenu est d'inverser le sens :
l'estampille est *dans* le diff qu'on approuve.

**B. Garde sur `okf_write_concept`** (refuser d'introduire une estampille
`human:` absente du disque). Proposée par le second avis, écartée par décision
utilisateur : le tool réécrit des frontmatters entiers par contrat, et une
politique de confiance n'appartient pas à un écrivain générique. Compensation :
documentation explicite, et le modèle « déclaration, pas preuve » assumé
jusqu'au bout.

**C. Tool agent interdit de `human:`** (ou pas de tool du tout). Écartée par la
même décision : symétrie complète avec le CLI. L'asymétrie aurait été une
demi-mesure — la voie `okf_write_concept` restant ouverte à côté.

**D. `verified` comme journal (append toujours) ou comme état (remplacement
total).** Écartées toutes deux — §4.2. Retenu : dernière estampille par acteur.

**E. `--stale-after` dans la v1.** Le point que le second avis donnait lui-même
comme le plus contestable de sa proposition. Écarté : affirmer la relecture et
faire taire le détecteur dans le même geste fabrique un bouton de renouvellement.
Coupé **en le disant** : la réponse à « comment sortir de la worklist » est dans
la doc, pas dans un flag.

**F. Renommer le verbe (`review`, `attest`, `sign`).** Plus honnêtes en
apparence, écartés : `attest` collisionne avec le vocabulaire §10, `sign`
surpromet (pas de crypto), et s'éloigner du nom du champ (`verified`) et du tier
(`human-reviewed`) violerait la règle « un seul vocabulaire partout ». Le nom
reste `verify` ; l'honnêteté est payée dans la sortie (« recorded ») et la doc.

**G. Patcheur textuel du frontmatter pour des diffs minimaux.** Écarté — §5.4 :
second chemin d'écriture divergent, exactement ce que `BundleConceptWriter`
existe pour empêcher.

**H. Faire l'audit conscient du temps d'abord.** Défendable (aucun chemin
d'écriture requis, et il corrige l'alibi permanent), mais il raffine le constat
sans donner de sortie à la worklist. Ordonné juste derrière, au ROADMAP.
