# Design — `okf audit` : requête corpus-level sur les signaux de confiance et de fraîcheur

Date : 2026-08-21
Statut : validé, prêt pour le plan d'implémentation

## 1. Objectif

Répondre, sur un bundle entier et en une commande, à la question que l'article
[« OKF v0.2 Quietly Admits the Folder Has a Ceiling »](https://medium.com/@davidroliver/okf-v0-2-quietly-admits-the-folder-has-a-ceiling-the-way-up-is-a-library-25fa54e872f9)
(David R Oliver, 2026-08-01) met au centre :

> *which of my concepts are past their `stale_after` date and have never been verified by a human?*

L'argument : v0.2 a livré le **schéma** d'une base de connaissances (provenance,
trust, lifecycle) en déclarant l'infrastructure de requête hors périmètre. Les
champs `generated`/`verified`/`status`/`stale_after` ne rapportent rien tant
qu'on ne peut pas les interroger **à l'échelle du corpus** ; les ouvrir un par un
marche à 40 concepts, coûte cher à 4 000, et devient une requête de base de
données déguisée en YAML à 40 000.

OKF4net a déjà tout le socle sémantique — `Trust.DeriveTier` (§5.3),
`Lifecycle`/`IsStale` (§5.4/§5.5), `Frontmatter.TrustTier` — mais **aucune
surface de requête** : ni les 7 verbes CLI ni les 11 tools agents ne posent de
question corpus-level. `okf audit` est cette surface.

### 1.1 Pourquoi pas `okf validate`

`BundleValidator` émet déjà un `ConceptStale` (warning) par concept périmé
([Validate.cs:404](../../../src/OKF4net/Validate.cs#L404)). La séparation est
délibérée et doit le rester :

| | `validate` | `audit` |
|---|---|---|
| Question | ce bundle est-il conforme §11 ? | quels concepts demandent une action ? |
| Sortie | diagnostics par fichier, sévérités | compteurs corpus + worklist filtrable |
| Filtrable | non | oui (`--stale`, `--trust`, `--status`, `--type`) |
| Code retour | 1 si non conforme | **toujours 0** (un audit rapporte, il ne juge pas) |
| Sorties figées | goldens byte-exact v0.1 | nouveaux goldens (§7.3) |

Corollaire dur : **aucune sortie existante de `validate`/`info` ne change**. Les
goldens v0.1 restent intacts, ce qui exclut d'emblée l'alternative « ajouter des
diagnostics `Info` au validateur » (§10, alternative B).

## 2. Périmètre

**Dans le périmètre** — trois unités, une par couche :

1. `ConceptAudit` : le calcul, dans le cœur, partagé.
2. Le verbe CLI `okf audit`, texte + `--json`.
3. Le tool agent/MCP `okf_audit`, lecture seule.

**Hors périmètre** (au backlog, mémorisé, non traité ici) : audit fédéré
multi-bundles via `OKF4net.Catalog` ; liens typés `links:` et export
property-graph ; `--fail-on` pour gater la CI ; recherche par passages/BM25 ;
tout champ frontmatter hors v0.2.

## 3. Unité 1 — `ConceptAudit` (cœur)

Nouveau fichier `src/OKF4net/Audit.cs`, zéro dépendance, calqué sur le doublet
existant `BundleValidator.Validate(bundle, clock) → ValidationReport`.

### 3.1 API publique

```csharp
/// <summary>Les prédicats de sélection d'un audit (§5.3–§5.5). Combinés en ET ; `default` ne filtre rien.</summary>
/// <remarks>
/// L'égalité générée compare <c>Trust</c> par référence (c'est le comportement de
/// <c>EqualityComparer&lt;IReadOnlySet&lt;T&gt;&gt;.Default</c>) : deux requêtes logiquement
/// identiques peuvent être inégales. Ne pas s'appuyer dessus, ni utiliser une
/// <c>AuditQuery</c> comme clé de dictionnaire. Le type reste un record struct pour
/// `with` et `ToString`, pas pour son égalité.
/// </remarks>
public readonly record struct AuditQuery(
    bool StaleOnly = false,
    IReadOnlySet<TrustTier>? Trust = null,
    ConceptStatus? Status = null,
    string? Type = null)
{
    /// <summary>La requête qui retient tous les concepts.</summary>
    public static AuditQuery All => default;

    /// <summary>Vrai dès qu'au moins un prédicat est posé.</summary>
    public bool IsFiltered => StaleOnly || Trust is not null || Status is not null || Type is not null;
}

/// <summary>Un concept retenu par un audit, avec ses signaux déjà dérivés.</summary>
public readonly record struct AuditFinding(
    ConceptId Id,
    string Path,
    string? Type,
    string? Title,
    TrustTier Trust,
    Lifecycle Lifecycle,
    bool IsStale);

/// <summary>Le résultat d'un audit : compteurs sur tout le bundle + les concepts sélectionnés.</summary>
public sealed class AuditReport
{
    public DateOnly AsOf { get; }
    public int ConceptCount { get; }
    public IReadOnlyDictionary<TrustTier, int> TrustCounts { get; }      // les 3 clés toujours présentes
    public IReadOnlyDictionary<ConceptStatus, int> StatusCounts { get; } // les 3 clés toujours présentes
    public int StaleCount { get; }
    public IReadOnlyList<AuditFinding> Findings { get; }                 // trié par Id, ordinal
}

/// <summary>Interroge un bundle sur ses signaux §5.3–§5.5. Ne lit rien sur disque, n'écrit rien, ne lève rien.</summary>
public static class ConceptAudit
{
    public static AuditReport Run(Bundle bundle, AuditQuery query = default, IOkfClock? clock = null);
}
```

### 3.2 Sémantique, point par point

- **Horloge.** `clock ?? new SystemClock()`, `AsOf = clock.Today`, exactement
  comme `BundleValidator.Validate`. Aucun `DateTime.UtcNow` enfoui : c'est ce qui
  rend les tests et les goldens déterministes (`FixedClock` existe déjà côté
  tests).
- **Périmètre des compteurs.** `TrustCounts`, `StatusCounts`, `StaleCount` et
  `ConceptCount` portent **toujours sur le bundle entier**, jamais sur la
  sélection. Le dénominateur reste stable quand on filtre ; `Findings` seul
  bouge. Les trois clés de chaque dictionnaire sont toujours présentes (valeur 0
  incluse) pour que la sortie ait une forme fixe.
- **Périmètre des concepts.** `bundle.Concepts` uniquement. Les `index.md` et
  `log.md` (§8/§9) sont exposés séparément par `Bundle` et ne sont pas des
  concepts. Les documents **non parsables** — frontmatter invalide
  (`DocumentParseException`) ou clés requises manquantes (`ConceptIdException`) —
  sont collectés dans `bundle.ParseErrors` par le chargement permissif (§11) et
  n'entrent dans aucun compteur. À ne pas confondre avec un fichier réellement
  **illisible** (I/O, droits, contenu non-UTF-8) : `Bundle.Load` lève alors
  `BundleLoadException` et abandonne le chargement entier ; le CLI rend cela en
  `error:` + code 1 (§4.3), et l'audit ne voit jamais ce cas.
- **Tier de confiance.** `concept.Document.Frontmatter.TrustTier`, donc
  `Trust.DeriveTier` (§5.3) : un `human:` ⇒ `HumanReviewed`, sinon tout
  vérificateur ⇒ `MachineConfirmed`, liste vide ⇒ `Unverified`.
- **Staleness.** `Lifecycle.IsStale(AsOf)`, donc §5.5 : `AsOf >= stale_after`.
  Une borne exacte (`AsOf == stale_after`) est **périmée**.
- **`stale_after` malformé** ⇒ `IsStale` faux, jamais dans la worklist. C'est
  `validate` qui possède le diagnostic `StaleAfterInvalid` ; l'audit ne le
  redouble pas.
- **Statut inconnu** (`status: retired` dans les fixtures) ⇒ compté comme
  `Stable`, conformément à §5.4 (« absent ou inconnu ⇒ stable »). `Lifecycle`
  transporte `StatusIsKnown`, mais l'audit ne l'expose pas : signaler la valeur
  inconnue est le travail de `validate`.
- **`Type`** : comparaison **ordinale exacte** sur `Frontmatter.Type`. Pas de
  repli de casse — un concept sans `type` n'est jamais retenu par `--type`. La
  question « `BigQuery Table` et `bigquery-table` sont-ils le même label ? »
  appartient au futur lint de vocabulaire (backlog), pas ici.
- **Tri.** `Findings` trié par `ConceptId` ordinal croissant. Déterministe, et
  indépendant de l'ordre de parcours du système de fichiers.
- **Robustesse.** Aucune exception : errors-as-data comme le reste du cœur. Un
  bundle vide donne des compteurs à zéro et `Findings` vide.
- **Pourquoi pas `StalePolicy`.** Le cœur a déjà `StalePolicy`
  (`Use`/`Tolerate(grace)`/`Strict`), que les Agents et le Catalog appliquent à
  la *restitution*. Elle répond à « dois-je exposer ce concept à un
  consommateur ? ». `AuditQuery.StaleOnly` répond à « ce concept est-il sur ma
  liste de travail ? » — la question inverse, où un concept périmé est
  précisément ce qu'on veut voir, pas ce qu'on veut filtrer. Les deux mécanismes
  coexistent donc volontairement ; ne pas les fusionner.

## 4. Unité 2 — verbe CLI `okf audit`

Dans `src/OKF4net.Cli/OkfCli.cs` : `CmdAudit(string[] args, TextWriter stdout)`,
branché dans le `switch` de `Run`, plus `JsonOutput.WriteAudit`.

### 4.1 Grammaire

```
okf audit <bundle> [--stale] [--trust <tiers>] [--status <s>] [--type <t>]
                   [--as-of <YYYY-MM-DD>] [--json]
```

| Flag | Valeur | Effet |
|---|---|---|
| `--stale` | — | ne retient que les concepts périmés à la date d'observation |
| `--trust` | liste séparée par `,` parmi `unverified`, `machine-confirmed`, `human-reviewed` | ne retient que ces tiers |
| `--status` | `draft` \| `stable` \| `deprecated` | ne retient que ce statut |
| `--type` | chaîne | ne retient que ce `type` (exact, ordinal) |
| `--as-of` | `YYYY-MM-DD` | fixe la date d'observation (défaut : aujourd'hui, UTC) |
| `--json` | — | document JSON unique, ligne terminée |

Les prédicats se combinent en **ET**. La question de l'article s'écrit :

```sh
okf audit bundles/acme_retail --stale --trust unverified,machine-confirmed
```

**Un seul vocabulaire** partout (entrée CLI, texte, JSON, tool agent) :
`unverified` / `machine-confirmed` / `human-reviewed` et
`draft` / `stable` / `deprecated`. Pas d'alias, pas de raccourci `--unverified` —
un booléen mentirait sur le cas `machine-confirmed`, qui est aussi « jamais
relu par un humain ».

Règles de parsing des valeurs, pour lever toute ambiguïté :

- `--trust` est le **seul flag à valeurs multiples** : liste séparée par `,`,
  chaque entrée trimée puis validée contre le vocabulaire, doublons absorbés
  (`IReadOnlySet`), entrée vide (`--trust "a,,b"` ou `--trust ""`) rejetée avec
  le message « unknown trust tier » et la valeur fautive citée.
- `--status` prend **une seule valeur**, trimée puis validée contre le
  vocabulaire §5.4. Pas de liste : `AuditQuery.Status`, le champ JSON `status` et
  le paramètre du tool agent sont tous des scalaires (`ConceptStatus?` /
  `string?`). Un besoin multi-statuts se traiterait par un changement de type
  cohérent sur les trois surfaces, pas par une tolérance du parseur.
- `--type` est une **valeur libre** : prise verbatim, sans trim ni repli de
  casse, puisque le spec ne contraint pas le vocabulaire de `type`.
- Flag répété : la **première occurrence gagne**, comportement hérité de
  `FlagValue` (`Array.IndexOf`) et commun à tous les verbes existants.
- **Le parsing de l'entrée est strict, celui du frontmatter reste permissif.**
  `Lifecycle.From` résout un `status` inconnu en `stable` (§5.4 : le chargement
  ne rejette rien, §11), alors que `--status retired` doit échouer. Ce n'est pas
  une incohérence : un producteur ne contrôle pas ce qu'il lit, un utilisateur
  contrôle ce qu'il tape, et une faute de frappe silencieusement absorbée en
  `stable` rendrait une worklist fausse. Les deux parsers restent donc distincts
  — ne pas « harmoniser » le strict vers le permissif.
- **Ordre de validation.** Les valeurs des flags sont validées **avant** la
  résolution du positionnel. Sinon `okf audit --as-of` (le flag comme unique
  argument) rendrait `missing <bundle>` : `Positional` saute le créneau d'un flag
  valué sans vérifier qu'il a une valeur, et le vrai défaut serait masqué par un
  diagnostic moins précis.

**Piège de parsing à ne pas rater.** `--trust`, `--status`, `--type` et `--as-of`
consomment le token suivant : ils doivent être déclarés dans les `valuedFlags` de
`Positional(args, "<bundle>", ...)`, sinon `okf audit --as-of 2099-06-01 mon/bundle`
prendrait `2099-06-01` pour le chemin du bundle.

C'est exactement ce que `--out` a résolu pour `render`. Note de base : ces deux
helpers (`Positional(…, valuedFlags)` et `FlagValue`) sont arrivés avec le
travail viewer ; ils sont présents depuis le merge d'`origin/dev` dans la branche
d'implémentation. Sur une base antérieure, il faudrait les ajouter d'abord.

### 4.2 Deux modes de présentation, une seule sélection

**Sans aucun flag de filtre**, `okf audit <bundle>` sélectionne le **même
ensemble** que `--stale` ; seule la présentation change. Cette équivalence est
une règle testable, pas un effet de bord.

Concrètement, le CLI passe `new AuditQuery(StaleOnly: true)` dans les deux cas :
`AuditQuery.All` (aucun prédicat) n'est jamais utilisé par le verbe — il ne sert
qu'aux appelants qui veulent la totalité du corpus, dont le tool agent invoqué
avec `stale: false` et aucun autre filtre.

**Les flags de filtre sont exactement `--stale`, `--trust`, `--status` et
`--type`.** `--as-of` et `--json` n'en font pas partie et ne changent jamais de
mode : `okf audit <bundle> --as-of 2099-06-01` reste en mode rapport — c'est
précisément l'invocation du golden (§7.3).

**Conséquence à assumer : `audit` ne sait pas sélectionner tout le corpus en une
option.** Sans filtre il rend la worklist des périmés, et `--json` porte alors
des `findings` limités à ceux-ci — les compteurs, eux, restent corpus-larges. Qui
veut l'inventaire concept par concept énumère les trois tiers
(`--trust unverified,machine-confirmed,human-reviewed`). Aucun `--all` n'est
ajouté : `audit` est une worklist, l'inventaire est déjà le métier de
`okf info --json` et de `okf_browse`. Ce point doit apparaître tel quel dans la
documentation du verbe, faute de quoi un consommateur du JSON prendra `findings`
pour le corpus.

**Mode rapport** (aucun flag de filtre) — synthèse + worklist :

```
bundle:     tests/fixtures/okf_v02
as of:      2099-06-01
concepts:   2

trust:
     1  human-reviewed
     0  machine-confirmed
     1  unverified

status:
     0  draft
     2  stable
     0  deprecated

stale:      1 of 2 past stale_after

needs attention (1):
  metrics/dau  stale 2099-01-01  human-reviewed  stable
```

Conventions d'alignement reprises telles quelles de `info` : libellé + padding
jusqu'à la colonne 13 (`bundle:` suivi de 5 espaces), compteurs en `  {n,4}  {label}`.
Ordre des tiers : du plus fort au plus faible. Ordre des statuts : l'ordre du
cycle de vie §5.4 (`draft`, `stable`, `deprecated`), pas l'ordre alphabétique.
Worklist vide ⇒ la dernière section devient la ligne `needs attention: none`.
Aucun plafond : c'est un CLI, la sortie se pipe.

**Mode requête** (au moins un flag de filtre) — une ligne par concept, rien
d'autre, pour rester pipe-friendly (`| wc -l`, `| xargs`) :

```
metrics/dau  stale 2099-01-01  human-reviewed  stable
```

Sélection vide ⇒ **aucune sortie**, code 0.

**Format d'une ligne de concept** (identique dans les deux modes, à l'indentation
de deux espaces près en mode rapport) — quatre champs séparés par deux espaces :

1. l'id du concept (`ConceptId`, donc toujours normalisé avec `/`) ;
2. la fraîcheur : `stale <YYYY-MM-DD>`, `fresh <YYYY-MM-DD>`, ou
   `no-stale-after` si le champ est absent **ou malformé** ;
3. le tier de confiance ;
4. le statut résolu.

Asymétrie assumée entre texte et JSON sur le cas malformé : le texte affiche
`no-stale-after` (un `stale_after` illisible ne dit rien de la fraîcheur, et
c'est `validate` qui signale la valeur fautive), tandis que le JSON conserve la
valeur brute dans `staleAfter` avec `stale: false` — un outil qui consomme le
JSON doit pouvoir distinguer « champ absent » de « champ présent mais illisible »
sans relire les fichiers.

Imprimer l'id plutôt que le chemin n'est pas cosmétique : `ConceptId.FromPath`
normalise toujours en `/`, donc les goldens de `audit` sont comparables
byte-for-byte sur les trois OS **sans** la normalisation `Replace('\\','/')` que
`validate.out` doit subir ([GoldenParityTests.cs:74-85](../../../tests/OKF4net.Tests/GoldenParityTests.cs#L74-L85)).

### 4.3 Codes de retour et erreurs

- **0** : succès, y compris avec des findings. `audit` rapporte, il ne juge pas
  la conformité. Pas de `--fail-on` tant qu'un utilisateur ne le demande pas.
- **1** : erreur d'invocation ou bundle illisible, via `CliOperationException`,
  rendue par `Run` en `error: {message}` sur stderr.

Messages exacts (nouveaux) :

| Cas | stderr |
|---|---|
| `--as-of` invalide | `error: --as-of is not a valid YYYY-MM-DD date: "2026-13-01"` |
| `--trust` inconnu | `error: unknown trust tier "foo"; expected unverified, machine-confirmed or human-reviewed` |
| `--status` inconnu | `error: unknown status "foo"; expected draft, stable or deprecated` |

Réutilisés tels quels : `error: missing <bundle>` (`Positional`) et
`error: --as-of requires a value` (`FlagValue`).

`--as-of` est parsé exactement comme `Lifecycle.From` le fait pour `stale_after`
— même contrat, et compatible `InvariantGlobalization` (AOT) :

```csharp
DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var asOf)
```

Les cinq arguments sont obligatoires : `DateOnly` n'offre **pas** de surcharge
`(s, format, provider, out)` — seulement `(s, format, out)` et la forme complète
ci-dessus. Omettre `DateTimeStyles.None` ne compile pas.

### 4.4 Texte d'aide

Ajouter à `Usage` la ligne du verbe **juste après `validate`** (l'ordre de la
liste est vérifié par le test 24 : conformité d'abord, santé du corpus ensuite),
alignée sur les autres — le verbe occupe 8 colonnes, d'où quatre espaces après
`audit` :

```
    audit    <bundle>    Report trust, freshness and lifecycle across the bundle
```

et étendre la ligne d'option existante en
`--json           Machine-readable output for validate/info/audit`. Le commentaire
de classe de `OkfCli` énumère les sous-commandes et annonce leur nombre :
l'incrémenter en comptant les verbes réellement présents sur la base (sept
depuis le merge du viewer, donc huit avec `audit`) et citer `audit`.

## 5. Unité 3 — tool agent `okf_audit`

Dans `OkfBundleTools`, sur le modèle de `Search` :

```csharp
[Description("Audit the bundle's trust, freshness and lifecycle signals: counts by trust tier and status, plus the concepts needing attention. Filter with stale/trust/status/type.")]
public string Audit(
    [Description("Only concepts past their stale_after date. Defaults to true.")] bool stale = true,
    [Description("Comma-separated trust tiers to include: unverified, machine-confirmed, human-reviewed.")] string? trust = null,
    [Description("Only concepts with this lifecycle status: draft, stable or deprecated.")] string? status = null,
    [Description("Only concepts with this frontmatter type (exact match).")] string? type = null)
```

Enregistré dans `GetTools()` via `AIFunctionFactory.Create(Audit, "okf_audit")`.
**Lecture seule**, donc absent de `WriteToolNames` : il reste disponible quand
`okf-mcp` tourne en mode read-only.

**La date d'observation vient de la couture existante, pas d'une horloge neuve.**
Le tool n'expose délibérément pas de paramètre `asOf` (un agent n'a pas à choisir
sa notion d'aujourd'hui), mais il ne doit pas non plus laisser `ConceptAudit`
retomber sur `SystemClock` : `OkfBundleTools` possède déjà le seam interne
`Func<DateTime> UtcNow` et la propriété `Today` qui en dérive — « the shared seam
behind `ReadConcept`'s and `Search`'s staleness checks »
([OkfBundleTools.cs:136](../../../src/OKF4net.Agents/OkfBundleTools.cs#L136)).
`Audit` passe donc `Today` à `ConceptAudit`, via un adaptateur privé de quatre
lignes :

```csharp
private sealed class PinnedClock(DateOnly today) : IOkfClock
{
    public DateOnly Today { get; } = today;
}
```

Sans cela, la sortie du tool dépendrait du jour d'exécution et le cas limite
`today == stale_after` serait intestable. L'alternative — une surcharge publique
`ConceptAudit.Run(bundle, query, DateOnly asOf)` — est écartée pour garder une
seule forme canonique dans le cœur ; l'adaptateur reste local aux Agents et
n'ajoute aucune surface publique.

Différences assumées avec le CLI :

- rend **toujours** la forme rapport (synthèse + liste), même filtré : la
  synthèse est du contexte utile pour un agent ;
- **plafonne à 20 findings**, suivis de `… and N more (narrow with stale/trust/status/type)`
  — même plafond que `okf_search`, et c'est précisément l'économie de contexte
  que l'article défend ;
- omet la ligne `bundle:` (le tool est lié à un seul bundle) ;
- valeurs invalides de `trust`/`status` ⇒ message d'usage rendu comme chaîne (pas
  d'exception), sur le modèle de `SearchUsageMessage` ;
- **tout ce qui peut toucher le disque passe par `RunTool`**, la garde partagée
  par tous les tools qui chargent le bundle : elle convertit `OkfException`
  (donc `BundleLoadException`), `ArgumentException`, `IOException`,
  `UnauthorizedAccessException` et `DecoderFallbackException` en une chaîne
  `Error: …`. Un tool de fonction **rend** une erreur, il n'en lève pas : un
  répertoire supprimé après la construction du tool remonterait sinon en
  exception jusqu'au runtime de l'agent. Le rendu du vocabulaire suit la même
  règle que le CLI — les libellés viennent d'`AuditVocabulary`, jamais de
  littéraux recopiés.

**Le rendu texte n'est pas partagé entre CLI et Agents** : seul le calcul
(`ConceptAudit`) l'est. Raison : les octets du CLI sont verrouillés par des
goldens et ne doivent jamais bouger parce qu'une chaîne destinée à un agent a
changé ; les deux rendus font une quinzaine de lignes chacun. C'est exactement le
partage retenu pour `ConceptSearch` (scorer commun, présentations distinctes).

## 6. Sortie `--json`

`System.Text.Json` **source-generated** — obligatoire, le CLI est publié Native
AOT. Nouveaux records internes dans `JsonOutput.cs`, plus
`[JsonSerializable(typeof(AuditJsonResult))]` sur `CliJsonContext` (le générateur
couvre le graphe atteignable ; seul le type racine s'annote). Nommage camelCase
via la policy déjà en place.

```csharp
internal sealed record AuditQueryJson(bool Stale, IReadOnlyList<string>? Trust, string? Status, string? Type);
internal sealed record TrustCountsJson(int HumanReviewed, int MachineConfirmed, int Unverified);
internal sealed record StatusCountsJson(int Draft, int Stable, int Deprecated);
internal sealed record AuditFindingJson(string ConceptId, string Path, string? Type, string? Title, string Trust, string Status, string? StaleAfter, bool Stale);
internal sealed record AuditJsonResult(
    string Bundle, string AsOf, int ConceptCount, AuditQueryJson Query,
    TrustCountsJson Trust, StatusCountsJson Status, int StaleCount,
    IReadOnlyList<AuditFindingJson> Findings);
```

Exemple (`okf audit tests/fixtures/okf_v02 --as-of 2099-06-01 --json`, ici
ré-indenté ; la sortie réelle est une seule ligne suivie de `\n`) :

```json
{
  "bundle": "tests/fixtures/okf_v02",
  "asOf": "2099-06-01",
  "conceptCount": 2,
  "query": { "stale": true, "trust": null, "status": null, "type": null },
  "trust": { "humanReviewed": 1, "machineConfirmed": 0, "unverified": 1 },
  "status": { "draft": 0, "stable": 2, "deprecated": 0 },
  "staleCount": 1,
  "findings": [
    {
      "conceptId": "metrics/dau",
      "path": "tests/fixtures/okf_v02/metrics/dau.md",
      "type": "Metric",
      "title": "Daily Active Users",
      "trust": "human-reviewed",
      "status": "stable",
      "staleAfter": "2099-01-01",
      "stale": true
    }
  ]
}
```

Décisions de schéma :

- `--json` rend **toujours** le document complet, dans les deux modes : les
  machines voient une seule forme, quelle que soit la présentation texte.
- `query` **rejoue la requête appliquée**, ce qui rend le document
  auto-descriptif et lève l'ambiguïté du mode par défaut (`stale: true` sans
  qu'aucun flag n'ait été passé).
- `query.trust` est sérialisé dans **l'ordre du ladder** (`unverified`,
  `machine-confirmed`, `human-reviewed`), jamais dans l'ordre de saisie :
  `IReadOnlySet` n'a pas d'ordre garanti, et sans cette règle le JSON ne serait
  pas reproductible dès qu'on passe plusieurs tiers. `null` quand `--trust` est
  absent.
- `staleAfter` porte la valeur **brute** du frontmatter ; elle vaut `null` si le
  champ est absent, et la valeur brute non parsable s'il est malformé (auquel cas
  `stale` est `false`).
- Pas de champ `statusKnown` : le statut inconnu est le domaine de `validate`
  (§3.2). Schéma minimal, donc stable.
- `path` est le chemin réel du concept, donc porteur du séparateur natif de l'OS
  — c'est le seul champ à normaliser côté sortie C# dans le test golden (§7.3).

## 7. Tests

### 7.1 Unitaires — `tests/OKF4net.Tests/AuditTests.cs` (nouveau)

Bundles synthétiques + `FixedClock` (déjà présent).

1. Les trois tiers sont comptés distinctement (`human:` ⇒ human-reviewed ;
   vérificateur non-humain seul ⇒ machine-confirmed ; absence ⇒ unverified).
2. Statut inconnu compté comme `stable`.
3. Borne de staleness §5.5 : `AsOf == stale_after` ⇒ périmé ; `AsOf == stale_after - 1j` ⇒ non.
4. `stale_after` malformé ⇒ non périmé, absent de la worklist.
5. `stale_after` absent ⇒ jamais périmé.
6. Les prédicats se combinent en ET (`--stale` + `--trust` ne retient que
   l'intersection).
7. `Findings` trié par id ordinal, indépendamment de l'ordre de chargement.
8. Les compteurs portent sur tout le bundle même quand la requête filtre.
9. Bundle vide ⇒ compteurs à zéro, `Findings` vide, aucune exception.
10. Documents non parsables exclus des compteurs, sans exception : un fichier au
    frontmatter invalide atterrit dans `ParseErrors`, et `ConceptCount` ne le
    compte pas. (Pas de test « fichier illisible » : ce cas lève
    `BundleLoadException` au chargement et n'atteint jamais `ConceptAudit`.)
11. `clock: null` ⇒ `AsOf` = date UTC du jour.
12. `--type` : match ordinal exact ; casse différente ⇒ pas de match ; concept
    sans `type` ⇒ jamais retenu.

### 7.2 CLI — `tests/OKF4net.Tests/CliTests.cs` (existant)

13. Mode rapport : sections et alignements attendus — **y compris avec `--as-of`
    seul**, qui ne doit pas basculer en mode requête (§4.2).
14. Mode requête : uniquement des lignes de concepts, pas de synthèse ; et
    l'idiome des trois tiers (`--trust unverified,machine-confirmed,human-reviewed`)
    retourne bien la totalité des concepts du bundle.
15. **Équivalence** : `audit <b>` et `audit <b> --stale` sélectionnent le même
    ensemble (comparaison sur les ids).
16. `--json` : document parsable, champs et valeurs attendus, `query` rejoué.
17. `--as-of` invalide ⇒ code 1 + stderr exact.
18. `--trust` inconnu ⇒ code 1 + stderr exact.
19. `--status` inconnu ⇒ code 1 + stderr exact.
20. `--trust` avec une entrée vide (`unverified,,human-reviewed`) ⇒ code 1 ;
    avec un doublon (`unverified,unverified`) ⇒ code 0 et même résultat qu'une
    seule occurrence.
21. **Régression de parsing** : `okf audit --as-of <date> <bundle>` (flags avant
    le positionnel) résout le bon bundle ; idem pour `--trust`, `--status`, `--type`.
22. Code 0 malgré des findings.
23. Sélection vide ⇒ sortie vide, code 0.
24. `--help` liste `audit`.

### 7.3 Goldens — `tests/OKF4net.Tests/GoldenParityTests.cs`

Bundle réutilisé : `tests/fixtures/okf_v02` (2 concepts, déjà porteur des champs
v0.2), avec `--as-of 2099-06-01` **figé** — cette date rend `metrics/dau`
(`stale_after: 2099-01-01`) périmé sans toucher au bundle. Formulation exacte de
l'engagement : **aucun bundle de fixtures n'est créé ni modifié**, et **aucun
golden existant n'est modifié** ; les seuls ajouts sont des goldens neufs,
vérifiés à la main (voir plus bas).

Nouveaux fichiers dans `tests/fixtures/golden/` : `audit-v02.out` et
`audit-v02.json`. **Pas de `audit-v02.exitcode`** : le code de retour d'`audit`
est constamment 0 (§4.3), un golden pour une constante n'apporte rien et grossit
la surface de fixtures. Le test l'assure en ligne (`Assert.Equal(0, r.Code)`),
exactement comme `Info_output_matches_golden`, qui n'a pas non plus de golden de
code retour ; les `*.exitcode` de `validate` n'existent que parce que son code
varie.

- `audit-v02.out` : comparé **byte-for-byte sans normalisation** (la sortie ne
  contient que des ids de concepts, toujours en `/`) — sauf la ligne `bundle:`,
  qui reprend l'argument tel que passé, d'où le recours à `WithRepoRootAsCwd`
  comme pour `validate`/`info`.
- `audit-v02.json` : comparé après `Replace('\\','/')` **sur la sortie C#,
  jamais sur le golden**, à cause du champ `path` (§6) — même traitement, et même
  justification, que `validate.out`.

Provenance à consigner explicitement, la règle du repo l'exige : ces goldens sont
**écrits à la main** et vérifiés contre le texte du spec (§5.3 tiers, §5.4
statuts, §5.5 staleness), **pas** capturés depuis le CLI de référence — `audit`
n'existe pas en amont. À documenter dans `tests/fixtures/README.md` et dans le
commentaire de classe de `GoldenParityTests`, au même titre que
`validate-v02.out`.

### 7.4 Non-régression

Aucun golden existant ne change. `dotnet test OKF4net.sln` doit rester vert sans
qu'un seul fichier de `tests/fixtures/` préexistant soit touché.

### 7.5 Agents — `tests/OKF4net.Tests/Agents/`

25. `okf_audit` est enregistré dans `GetTools()`.
26. Il est **absent** de `WriteToolNames` (donc exposé en mode read-only).
27. Plafond à 20 findings + ligne `… and N more`.
28. `trust`/`status` invalides ⇒ message d'usage rendu, pas d'exception.
29. **Date pinnée par le seam `UtcNow`** : avec `UtcNow` figé, la sortie est
    déterministe, et la borne `today == stale_after` classe bien le concept comme
    périmé. Sans ce test, le comportement clé dépendrait du jour d'exécution.

## 8. Documentation à mettre à jour

- `README.md` : liste des verbes CLI, et la table §-du-spec → type (ajouter
  `ConceptAudit` en regard de §5.3–§5.5).
- `CHANGELOG.md` : entrée sous `Unreleased`.
- `ROADMAP.md` : inscrire l'échelle de l'article (index → recherche → graphe →
  bibliothèque fédérée) et marquer ce premier barreau.
- `CLAUDE.md` : une ligne sur `ConceptAudit` comme surface de requête unique
  partagée CLI/Agents — même statut que la note « ne pas forker `ConceptSearch` ».

**Coordination.** Au moment d'écrire cette spec, `CLAUDE.md` et `ROADMAP.md`
étaient déjà modifiés, non commités, dans le worktree principal par une autre
session (travail viewer/CI). Les toucher ici provoquera un conflit au rebase :
ces deux fichiers sont à traiter en fin de branche, une fois l'autre session
mergée, et non au fil de l'implémentation.

## 9. Contraintes respectées

- **Zéro dépendance tierce** : `Audit.cs` n'utilise que la BCL ; aucun
  `PackageReference` ajouté nulle part.
- **Native AOT** : JSON source-generated obligatoire, parsing de date en
  `InvariantCulture` (compatible `InvariantGlobalization`).
- **Fidélité au spec** : aucun champ frontmatter nouveau, aucune extension. Tout
  se dérive de champs v0.2 existants (§5.3/§5.4/§5.5). `audit` est un
  **consommateur** du spec, pas une extension de celui-ci.
- **Fixtures** : aucune fixture existante modifiée ; les nouvelles relèvent de
  l'exception documentée « comportement non couvert par une capture existante »,
  vérifiées à la main contre le texte du spec.
- **Conventions de fichier** : en-tête `// SPDX-License-Identifier: LGPL-3.0-or-later`,
  namespace file-scoped, XML doc sur toute l'API publique, nullable activé,
  warnings = erreurs.

## 10. Alternatives écartées

**A. Calculer dans le CLI, dupliquer côté Agents plus tard.** Écartée : elle
forke la logique dans deux couches, ce que le repo interdit explicitement pour le
scorer de recherche. Une seule implémentation, deux présentations.

**B. Étendre `BundleValidator` avec des diagnostics `Info`, `audit` devenant une
vue filtrée de `validate`.** Écartée pour deux raisons cumulatives : elle mélange
la conformité §11 avec de l'hygiène éditoriale, et elle modifierait la sortie de
`validate`, donc les goldens byte-exact — interdit.

**C. Un flag `--unverified` plutôt que `--trust <liste>`.** Écartée : « jamais
relu par un humain » recouvre deux tiers (`unverified` **et**
`machine-confirmed`) ; un booléen serait faux sur le second, et il ferait un
second vocabulaire en plus des noms de tiers.

**D. Code de retour non nul quand il y a des findings.** Reportée : un concept
périmé n'est pas une erreur de conformité, et l'usage décrit par l'article est
une worklist hebdomadaire routée par équipe, pas un gate de CI. Si le besoin de
gate apparaît, il se traitera par un `--fail-on` explicite.
