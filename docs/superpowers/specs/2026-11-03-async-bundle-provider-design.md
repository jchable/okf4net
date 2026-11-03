# Contrat asynchrone du provider de bundle — design

Date : 2026-11-03
Statut : proposé, à arbitrer
Ticket : [#152](https://github.com/jchable/okf4net/issues/152). Le ticket
consigne déjà un « design décidé » (2026-10-06) et un commentaire du 2026-10-07
sur le verrou d'écriture. Ce document les confronte au code de `dev`
(`d460c62`, lots A à D de #139 mergés) : il garde ce qui tient, corrige les deux
prémisses que le code dément (§1.3), et liste ce que seul le mainteneur peut
trancher.

Prérequis de lecture : la couture synchrone,
[`2026-10-07-bundle-provider-seam-design.md`](2026-10-07-bundle-provider-seam-design.md)
(révision 6 et notes des lots), dont ce document reprend le vocabulaire (image,
handle, obligation de rechargement, observations, bornes §6).

## 1. Le problème, tel que le code le montre

### 1.1 Le contrat synchrone et qui l'appelle

`IBundleProvider` (`src/OKF4net/Providers/IBundleProvider.cs:26-110`) est
entièrement synchrone : `GetVersion`, `Enumerate` (un `IEnumerable`), `Read`,
`Exists`, `Write`, `Delete`. Ses appels traversent le cœur à ces endroits :

| Appelant | Fichier:ligne | E/S faite |
|---|---|---|
| `Bundle.Load(IBundleProvider, …)` | `Bundle.cs:229-396` | listing (`ListNames`, `:552-580`), jeton (`:275`), lecture de chaque `.md` (`:326`, lecteur borné `:597-618`), `okf_version` (`:380`), relevé des ressources exécutables (`:394`, `:456-483`) |
| `Bundle.Load(string)` | `Bundle.cs:210-218` | `Directory.Exists` puis la surcharge provider |
| `Bundle.TryResolveResourceKey` | `Bundle.cs:1030-1082` | `Provider.Exists` (`:1064`) — **public, sync, fait de l'E/S** |
| `Bundle.ReadResourceText(BundleKey, …)` | `Bundle.cs:1095-1150` | `Provider.Read` |
| `BundleValidator.Validate` | `Validate.cs:353` | relit `index.md`/`log.md` (`:954`, `:1033`), `Exists` par ressource (`:684`, `:786`) |
| `BundleConceptWriter` (toutes les écritures) | `BundleConceptWriter.cs:657-1521`, `:2000` | lectures bornées + `Write`/`Delete` conditionnels |
| `IndexGenerator.Regenerate*` | `IndexGenerator.cs:204-490` | listing (`:267`), lectures (`:467`, `:490`), écritures |
| `BundleHandle.Current` / `Refresh` | `BundleHandle.cs:94-131`, `:207-258` | jeton (`:211`) et chargement (`:224`) |
| `AttestationOrchestrator.Capture` | `AttestationOrchestrator.cs:140`, `:188`, `:240` | lecture épinglée du code sanctionné — **public, sync, nouveau (non publié)** |

### 1.2 Où un pont sync-sur-async serait nécessaire aujourd'hui

Un provider réellement asynchrone (S3, #142) ne pourrait être servi qu'en
bloquant. Les surfaces asynchrones qui font déjà de l'E/S synchrone dessous :

- **Agents.** `OkfContextProvider.ProvideAIContextAsync`
  (`OkfContextProvider.cs:221-262`) appelle `_tools.GetBundle()` en synchrone.
  `GetBundle` (`OkfBundleTools.cs:289-295`) prend le **verrou d'écriture** puis
  `_handle.Current`, qui prend `_loadLock` et charge sous lui. **Le verrou
  d'écriture est donc tenu pendant tout un chargement** (une marche disque
  complète). Seul `okf_run_computation` est asynchrone
  (`RunComputationToolAsync`, `:1659`). Les douze autres outils rendent un
  `string` synchrone (`:401-412`). Le seul `GetAwaiter().GetResult()` de `src/`
  est la surcharge `RunComputation`, `[Obsolete]` depuis 0.7.0
  (`OkfBundleTools.cs:1600`).
- **Catalog.** `OkfBundleKnowledgeSource.SearchAsync`
  (`OkfBundleKnowledgeSource.cs:120-133`) lit `Handle.Current` en synchrone.
  `FileMemoryStore.ReadAsync`/`EnumerateAsync` appellent `Bundle.Load` en
  synchrone (`FileMemoryStore.cs:130`, `:310`). `FileKnowledgeCatalog.ReloadAsync`
  (`FileKnowledgeCatalog.cs:189-213`) lit le manifeste en synchrone sous
  `lock (_gate)` : c'est un fichier local de l'hôte, et aucun bundle n'y est
  chargé (les handles sont paresseux). `IBundleProviderFactory.Create`
  (`IBundleProviderFactory.cs:42`) est appelé sous ce même verrou et ne doit
  rien bloquer.
- **Attestation.** `RunAsync`/`RunCapturedAsync` sont asynchrones, mais
  `Capture` lit le script et l'attester en synchrone (`:188`, `:240`).
- **MCP.** `okf-mcp` (`Program.cs`) est déjà un hôte asynchrone. Il construit
  `OkfBundleTools(bundleRoot)` (`OkfMcpToolset.cs:28`) et sert ses
  `AIFunction` : un outil `Task<string>` passe sans changement. La découverte
  (`OkfBundleDiscovery.cs:101-111`) lit `index.md` hors couture, au démarrage :
  elle n'est pas concernée.
- **CLI, `okf-render`.** `Main` synchrone (`OKF4net.Cli/Program.cs:9`,
  `OKF4net.Render/Program.cs:9`). Les points d'entrée utilisés :
  `Bundle.Load(string)` (`OkfCli.cs:325-329`, `OkfRenderCli.cs:189-193`),
  `BundleValidator.Validate` (`OkfCli.cs:444`),
  `new BundleConceptWriter(path)` (`:713`), `IndexGenerator.RegenerateIndexes(path)`
  (`:902`). Le viewer (`OKF4net.Viewer`) ne fait **aucune** E/S provider : il
  projette une image chargée et écrit sa sortie locale avec `File.*`.
- **Producteur.** `producers/` n'utilise que les points d'entrée `string` :
  `BundleConceptWriter(staging)` (`BundleWriter.cs:100`),
  `IndexGenerator.RegenerateIndexes(outPath)` (`:314`), `Bundle.Load` puis
  `BundleValidator.Validate` (`BundleValidationRunner.cs:28-29`).

### 1.3 Deux prémisses du ticket que le code dément

1. **« La validation d'un bundle chargé reste synchrone (CPU seulement). »**
   Faux aujourd'hui. `BundleValidator.Validate` relit `index.md` et `log.md` par
   le provider et appelle `Exists` pour chaque ressource §6.2. Il ouvre même un
   scope provider (`Validate.cs:358`). Sur S3, valider coûte une requête par
   ressource. Il faut soit rendre la validation asynchrone, soit précharger au
   chargement (question Q4).
2. **« Le pont synchrone affirme que la `ValueTask` est déjà terminée ; le
   provider filesystem termine en synchrone. »** C'est juste pour les E/S, mais
   quatre attentes ne seraient plus synchrones si elles devenaient des
   `Task.Delay` comme la spec de la couture l'annonce (§4, « il devient un
   `Task.Delay` avec #152 ») :
   - les réessais de l'écrivain (`BundleConceptWriter.cs:2382`, `:2415`) ;
   - ceux de l'index (`IndexGenerator.cs:434`) ;
   - la fenêtre `ReplaceFile` de la lecture sous Windows
     (`FileSystemBundleProvider.cs:273`) ;
   - les nouveaux essais de commit (`:966`).

   Avec l'affirmation, `okf verify` lèverait alors une exception sous un conflit
   réel. Le pont doit donc distinguer un **mode d'exécution** pour les attentes
   (§3.3).

### 1.4 Le verrou d'écriture

`EnterWriteLock` (`BundleConceptWriter.cs:415-512`) combine plusieurs
mécanismes :

- une porte lexicale et un moniteur résolu à travers les points de reparse
  (#86) ;
- la réentrance par `[ThreadStatic] t_heldLocks` (`:215-216`) ;
- une règle anti-cycle : un thread qui tient déjà un verrou ne fait qu'essayer
  la porte.

Tout repose sur `Monitor`, lié au thread : il ne survit pas à un `await`.
Inventaire vérifié des acquisitions imbriquées :

- `RecordVerifications` (`:1157`) appelle `CheckVerificationTargets`, qui
  reprend le verrou (`:1192`, `:2000`) ;
- `OkfBundleTools.GetBundle`, `InvalidateBundle` et `RegenerateIndexes` prennent
  le verrou de l'écrivain (`:291`, `:303`, `:1207`) depuis l'extérieur de
  celui-ci.

Rien dans `src/` ne tient deux bundles à la fois. Les verrous **internes** du
provider filesystem (`CommitLocks`, 64 `Lock` en bandes, `:590` ;
`ListingMemo._gate`) n'entourent que du code synchrone : ils restent tels quels.

### 1.5 Ce qui est publié, et ce que cela libère

La dernière version est **v0.7.0 (2026-10-14)**. Son arbre ne contient pas
`src/OKF4net/Providers/` : `IBundleProvider`, `BundleHandle`, `BundleKey`,
`TryResolveResourceKey`, `ReadResourceText(BundleKey)`, `Capture`, les
fabriques du catalogue n'existent que dans `dev`, sous `[Unreleased]` du
CHANGELOG. **Aucun hôte ne peut avoir implémenté le contrat synchrone.** Tant
qu'aucune version ne le publie, on peut le remplacer sans dépréciation, sans
adaptateur public et sans second changement cassant.

En revanche, ont été publiés en 0.7.0, sans avertissement à l'époque :

- `Bundle.Root`, `IndexFiles`, `LogFiles`, `ParseErrors`, `TryResolveResource`,
  `ReadResourceText(string)` ;
- les deux `BundleRoot` ;
- les méthodes outils publiques `string` d'`OkfBundleTools` ;
- `BundleValidator.Validate`.

Le lot D a marqué les premiers `[Obsolete]` avec « removed with … (#152) »
(`Bundle.cs:621`, `:744-752`, `:881`, `:992` ; `BundleConceptWriter.cs:334` ;
`OkfBundleTools.cs:249`), et le CHANGELOG promet qu'ils sont « kept one
version with a warning ». **Aucun d'eux ne gêne le contrat asynchrone** :

- quatre sont des projections pures de l'image ;
- deux lisent le système de fichiers hors couture ;
- deux sont des propriétés.

Leur lien avec #152 n'était qu'un repère de version (Q2).

## 2. Les options

Les trois options partagent les invariants suivants :

- les clés, la validation en trois couches, le comptage des bornes et la
  résolution §6.2 restent **dans le cœur** (`ListNames`, `ReadTextAt`,
  `BundleKeyValidator`, `ResourceKeyResolver`), que le provider soit sync ou
  async ;
- le provider ne reçoit que des clés validées ;
- les goldens restent byte-identiques.

### (A) Interface jumelle `IAsyncBundleProvider`, l'interface sync gardée

```csharp
public interface IAsyncBundleProvider { /* mêmes membres, en ValueTask/IAsyncEnumerable */ }
// le cœur accepte l'une ou l'autre ; adaptateur sync → async trivial,
// async → sync = blocage
```

- **Pour.** Aucune rupture pour un implémenteur sync, et le CLI ne bouge pas.
- **Contre.**
  - L'avantage est nul ici : aucun implémenteur sync n'existe hors du dépôt
    (§1.5).
  - Chaque chemin du cœur (chargement, écrivain, index, validation, handle)
    existe en deux exemplaires, ou passe par un adaptateur. Dans le sens
    async → sync, c'est exactement le sync-sur-async que le ticket exclut.
  - Deux suites de conformance, deux batteries hostiles, deux jeux de
    garanties d'atomicité à documenter.
  - Le « fork » que les règles du dépôt interdisent pour le scorer ou le
    rendu d'audit réapparaît au niveau du contrat.

### (B) Tout asynchrone, requêtes comprises

`IBundleProvider` est remplacé, et l'API publique devient async de bout en bout,
requêtes sur l'image comprises (`GetAsync`, `SearchAsync`, `AuditAsync`). La CLI
passe à un `Main` async.

- **Pour.** Un seul modèle, sans pont.
- **Contre.**
  - Les requêtes sur l'image sont du pur CPU sur un objet en mémoire :
    `ValueTask` partout, c'est de l'allocation et de la viralité pour rien.
  - Cela casse `ConceptAudit`, `SiteModel` et le viewer, qui n'ont aucune E/S.
  - Le ticket a déjà tranché contre, et `OkfDocument.Body` reste une `string`
    jusqu'à #153.

### (C) Asynchrone aux bords d'E/S, requêtes synchrones sur l'image (titre du ticket)

`IBundleProvider` est **remplacé** par sa forme async. Seuls les points d'E/S
deviennent async. L'image (`Bundle`) garde ses requêtes synchrones. Un pont
synchrone **non bloquant** sert le seul provider filesystem.

```csharp
public interface IBundleProvider
{
    string Id { get; }                                   // inchangés : sans E/S
    string Location { get; }
    BundleProviderCapabilities Capabilities { get; }
    BundleKeyConstraints NativeConstraints { get; }

    ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct);
    IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct);
    ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct);
    ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct);
    ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct);
    ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct);
}
// BundleObject : Stream lu par le cœur avec ReadAsync(Memory<byte>, ct) ; gagne IAsyncDisposable.
// IBundleChangeNotifier.Subscribe(Action) : inchangé (le rappel ne fait que lever un drapeau).
// IBundleProviderFactory.Create : reste sync ; contrat explicite « aucune E/S à la construction ».
```

**Choix de forme**, valables pour (C) :

- **`IAsyncEnumerable` plutôt qu'un listing paginé explicite**
  (`ListAsync(prefix, continuation)`). L'énumération asynchrone suit la
  pagination S3 et laisse la borne d'entrées (et le plafond #151) arrêter le
  listing en cours de route, par `DisposeAsync` de l'énumérateur. C'est la
  forme idiomatique, avec `[EnumeratorCancellation]`. La pagination explicite
  serait plus simple à implémenter sans itérateur async, mais elle force chaque
  consommateur à réécrire la boucle de bornes. Le chargeur continue de
  matérialiser le listing avant le tri par composants (`Bundle.cs:302`) :
  l'énumération borne l'abandon, pas la mémoire du listing.
- **`ValueTask` pour le provider et les accès rapides** : le provider
  filesystem termine en synchrone, sans allocation. **`Task` quand une tâche est
  attendue plusieurs fois** : le chargement partagé du handle. **`Task<string>`
  pour les outils**, parce qu'`AIFunctionFactory` le lie nativement.
- **`CancellationToken` obligatoire** (pas de valeur par défaut) dans le
  contrat provider, pour qu'un décorateur ne l'oublie pas. Il reste optionnel
  (`= default`) sur l'API publique du cœur.

**Points d'entrée du cœur (C) :**

| Aujourd'hui (sync) | Après |
|---|---|
| `Bundle.Load(IBundleProvider, options)` *(non publié)* | `Bundle.LoadAsync(provider, options, ct)` — la surcharge sync disparaît |
| `Bundle.Load(string root)` | **gardé sync**, pont non bloquant (§3.3) |
| `Bundle.TryResolveResourceKey` *(non publié)* | `ResolveResourceKeyAsync(concept, raw, ct)` → `(BundleKey?, ResourceResolutionStatus)` |
| `Bundle.ReadResourceText(BundleKey, …)` *(non publié)* | `ReadResourceTextAsync(key, expected, ct)` → `(string, BundleObjectVersion)` |
| `BundleValidator.Validate(bundle)` *(publié)* | `ValidateAsync(bundle, clock, ct)` ; la version sync est gardée, avec un pont non bloquant (Q4) |
| `BundleConceptWriter` (ctor provider / handle) | méthodes `…Async` ; ctor `string` gardé, ses méthodes sync passent par le pont |
| `IndexGenerator.RegenerateIndexes(provider, …)` *(non publié)* | `RegenerateIndexesAsync` ; les surcharges `string` gardées sync |
| `BundleHandle.Current` / `Refresh()` *(non publiés)* | `GetCurrentAsync(ct)` / `RefreshAsync(ct)` ; plus de `Current` bloquant |
| `AttestationOrchestrator.Capture` *(non publié)* | `CaptureAsync(bundle, concept, expected, ct)` |
| `OkfBundleTools.ReadConcept` & co *(publiés, `string`)* | `…Async` → `Task<string>`, noms d'outils inchangés (Q3) |

## 3. Recommandation : (C), le contrat remplacé avant sa première publication

### 3.1 Pourquoi

- (A) paie le coût d'un double contrat pour protéger des implémenteurs qui
  n'existent pas (§1.5).
- (B) rend asynchrone du code qui ne fait pas d'E/S.
- (C) ne touche que les bords réellement concernés. Il garde le cœur unique
  (une seule boucle de bornes, un seul lecteur borné, une seule résolution
  §6.2), et laisse la CLI, `okf-render`, le producteur et les goldens sur un
  chemin synchrone qui **ne bloque jamais sur de l'asynchrone**.

C'est le design du ticket, avec les deux corrections du §1.3.

### 3.2 Ce qui reste dans le cœur

C'est inchangé, et c'est la règle : `ListNames` devient `ListNamesAsync` et
compte entrées, profondeur et longueur de clé sur chaque nom brut reçu, quel que
soit le provider. `ReadTextAt` devient `ReadTextAtAsync` et compte les octets.
La validation des clés et de l'arborescence, la résolution lexicale §6.2, la
traduction des erreurs (§7, `Translate`) et le relevé des ressources
exécutables restent dans le cœur. Le provider filesystem garde ses prédicats
STRICT/lenient tels quels : leur implémentation est synchrone, et elle le reste
(§3.4).

### 3.3 Le pont synchrone : affirmé, plus un mode d'attente

- **Affirmation (décision du ticket, gardée).** Une surcharge sync appelle le
  cœur async. Si la `ValueTask` rendue n'est pas terminée, elle lève
  `InvalidOperationException` (« provider completed asynchronously; use the
  async overload »), **sans jamais attendre**. Elle ne peut donc ni bloquer ni
  provoquer d'interblocage, quel que soit le `SynchronizationContext`. Elle ne
  sert que le provider filesystem construit par la surcharge `string`, ou un
  `Bundle` dont le provider termine en synchrone (`Validate`).
- **Aucun effet de bord avant d'avoir la garantie de synchronisme** (retour de
  relecture). Vérifier la complétion *après* le démarrage serait trop tard : une
  écriture sync sur un provider async lèverait, puis committerait quand
  l'opération abandonnée reprendrait. Donc : (1) une surcharge sync qui écrit
  ou supprime refuse **avant** tout appel d'E/S, sur une capacité déclarée par
  le provider (`CompletesSynchronously`, vraie pour le filesystem seul) ; un
  provider qui ne la déclare pas voit `InvalidOperationException` sans qu'aucune
  opération n'ait démarré. (2) Les lectures, sans effet de bord, gardent le
  contrôle de complétion a posteriori ; une opération incomplète est abandonnée
  sous un jeton annulé et son résultat jeté. §3.11 ajoute le test : provider
  async-contrôlé, appel sync d'écriture rejeté, puis complétion de l'opération
  en attente, et vérification qu'**aucun** commit n'a eu lieu.
- **Mode d'exécution interne** (`ExecutionMode.Synchronous | Asynchronous`, non
  public), transmis par les ponts. Il ne gouverne que deux choses :
  - les **attentes du cœur** (réessais de l'écrivain et de l'index) :
    `Thread.Sleep` en synchrone, `await Task.Delay(…, ct)` en asynchrone, par le
    point d'injection `Sleep` déjà présent (`BundleConceptWriter.cs:2382`),
    généralisé ;
  - le **degré de parallélisme** des lectures (§3.6) : 1 en synchrone.

  Ce n'est pas le patron `bool async` du BCL appliqué à chaque appel d'E/S :
  les E/S restent une seule voie (async), et seules les attentes et le
  parallélisme bifurquent.
- **Les attentes internes du provider filesystem** (lecture : au plus 3 essais
  de 5 à 20 ms, `:273` ; commit : 3 essais, `:966`, `:1083`) restent des
  `Thread.Sleep` bornés, **dans ses méthodes async aussi**. Elles ne valent que
  sous Windows et en cas de contention. Un hôte async paie au pire environ
  60 ms de thread bloqué, contre la garantie que le provider filesystem
  termine toujours en synchrone. C'est un choix à dire, pas à cacher (risque
  R4).
- **Pas de `ConfigureAwait` oublié.** Aujourd'hui, `ConfigureAwait(false)` est
  appliqué à la main, par endroits (Attestation, Catalog, Agents), et aucun
  analyseur ne l'exige. Proposition : l'appliquer partout dans les
  bibliothèques, et l'imposer avec la règle `CA2007` du SDK, activée dans
  `.editorconfig` pour `src/` hors exécutables. Aucun paquet n'est nécessaire.

### 3.4 Le provider filesystem : honnêteté sur ce que .NET donne

- **L'énumération de répertoires n'a aucune API asynchrone en .NET**
  (`Directory.Enumerate*`, `FileInfo`).
- **Les lectures de fichiers ont une vraie E/S asynchrone sous Windows**
  (`FileOptions.Asynchronous`, ports de complétion). Sous Linux et macOS, .NET
  exécute les appels système synchrones sur le pool de threads : c'est, en
  substance, un `Task.Run`.
- Pour des fichiers markdown de quelques Kio, la lecture synchrone est plus
  rapide que les deux.

**Recommandation : le provider filesystem reste synchrone dedans, et ses
méthodes rendent des `ValueTask` terminées** (`ValueTask.FromResult`). Son
`EnumerateAsync` rend un `IAsyncEnumerable` écrit à la main sur la liste qu'il
matérialise déjà (`FileSystemBundleProvider.cs:200-222`). Un itérateur `async`
sans `await` déclencherait CS1998, que `TreatWarningsAsErrors` rend bloquant.

Conséquence assumée : un hôte async qui charge un gros bundle disque bloque son
thread pendant la marche, **comme aujourd'hui**. S'il le refuse, il fait
`await Task.Run(() => handle.GetCurrentAsync(ct).AsTask())`. On ne fait pas de
`Task.Run` caché dans la bibliothèque.

Le scope de listing (`IProviderScope`, `AsyncLocal`, `:35`, `:128-138`) suit
déjà le flux d'exécution à travers les `await`. Un flux forké partage le memo,
qui est verrouillé pour ce cas (`:104-108`). Il reste compatible, mais un test
doit le montrer (§3.9).

### 3.5 `BundleHandle` : chargement unique sans verrou tenu à travers un `await`

Aujourd'hui, `_loadLock` (`BundleHandle.cs:32`) est tenu pendant le chargement
**et** pendant la comparaison de jetons (`:104-113`, `:121-130`). Une `Lock` ne
peut pas contenir d'`await`.

Remplacement **à vol unique** (*single-flight*) :

- **La tâche partagée.** Un champ `Task<Image>? _inflight` est publié sous une
  `Lock` brève (sans E/S ni `await` dedans). Tout appelant qui trouve une
  opération en cours fait `await _inflight.WaitAsync(ct)`.
- **Ce qu'elle contient.** L'opération « comparer le jeton, puis recharger si
  besoin » est **une seule** tâche. Il y a donc au plus une marche (jeton ou
  chargement) par handle à la fois : c'est la garantie « one load at a time »,
  inchangée.
- **Annulation.** Le chargement partagé ne s'exécute sous **aucun** jeton
  d'appelant. `Dispose` reste ce qu'il est aujourd'hui (`BundleHandle.cs:149`) :
  il ferme l'abonnement et n'annule rien, et un chargement ultérieur reste
  permis. Raison (retour de relecture) : `FileKnowledgeCatalog` dispose
  aussitôt les handles remplacés alors que des requêtes sur l'ancien instantané
  peuvent encore avoir besoin de leur premier chargement
  (`A_catalog_reload_during_a_search_leaves_the_search_on_its_snapshots_handles`).
  Un appelant qui annule cesse d'attendre et reçoit
  `OperationCanceledException` ; les autres reçoivent l'image. Si tous les
  appelants ont renoncé, le chargement va à son terme (borné par le provider)
  et son image sert le suivant. Une annulation venue du provider reste un échec
  de chargement, enveloppé comme aujourd'hui (`:232-257`).
- **L'obligation de rechargement** (compteur de générations, `:19-23`, `:221`)
  est inchangée, et **la jonction en tient compte** : un appelant ne rejoint
  une opération en cours que si elle a démarré à une génération ≥ celle qu'il
  exige. Un accès postérieur à `Invalidate()` mais antérieur à la fin de
  l'ancienne opération attend celle-ci, puis démarre ou rejoint une opération
  couvrant sa génération (comme le recontrôle sous le verrou de
  `BundleHandle.Current` aujourd'hui). Test dédié à cet ordre en §3.11.
- **`RefreshAsync`.** Si une opération est en cours, il l'attend, puis en lance
  une nouvelle, comme le `lock` sérialisé d'aujourd'hui.
- **Changement observable (bénin).** Sous `OnAccess`, N appelants concurrents
  partagent **une** comparaison au lieu d'en faire N l'une après l'autre. Les
  tests qui comptent les appels à `GetVersion` peuvent changer de chiffre, pas
  de sens.
- `IsStale`, `LastLoadError`, le réarmement `Notify` et l'image précédente
  servie en cas d'échec sont inchangés.

### 3.6 Chargement : parallélisme borné et déterminisme

En mode asynchrone, le cœur lit les `.md` (et les ressources du relevé) avec un
parallélisme borné par `BundleLimits.MaxConcurrentRequests`, avec un tampon par
lecteur (`ArrayPool`) au lieu du `chunk` partagé (`Bundle.cs:309`). Le provider
garde sa propre borne de requêtes.

**Déterminisme exigé**, sinon le texte d'erreur dépendrait de l'ordonnanceur :

- chaque résultat va dans la case de sa clé ;
- les concepts gardent l'ordre de `mdKeys` ;
- un échec global rapporte **la clé de plus petit rang** qui a échoué (on
  attend les rangs inférieurs, et on annule les supérieurs).

Les `ParseProblems` sont déjà triés après coup (`:387`). En mode synchrone, le
degré vaut 1 et le chemin est celui d'aujourd'hui.

### 3.7 Le verrou d'écriture : bail explicite, `SemaphoreSlim`

Le `[ThreadStatic]` réentrant ne survit pas à un `await`. Un `AsyncLocal`
« verrous tenus » serait **faux** : il est hérité par les tâches forkées, et
deux enfants concurrents se croiraient tous deux propriétaires.

Proposition :

- **Le registre.** On garde la porte lexicale et la clé résolue (#86), chacune
  en `SemaphoreSlim(1,1)`, prises toujours dans l'ordre porte puis résolue,
  avec `WaitAsync(ct)`. La garantie de #86 (deux alias qui se résolvent vers le
  même répertoire partagent le verrou) est conservée.
- **Le bail.** L'acquisition rend un **bail** (`WriteLease`, `IAsyncDisposable`).
- **Pas de réentrance ambiante.** Les points d'entrée publics acquièrent une
  fois. Les corps internes prennent `WriteLease` **en paramètre** (`…Locked(lease,
  …)`), ce qui couvre les deux imbrications inventoriées (§1.4).
  `OkfBundleTools` reçoit une API interne `RunUnderWriteLockAsync(Func<WriteLease,
  …>)`.
- **La règle anti-cycle** (« qui tient un verrou n'attend jamais une porte »)
  devient structurelle : rien ne tient deux bundles (§1.4), et une seconde
  acquisition sur un bail tenu est une erreur de programmation, détectée en
  debug.
- **Le rappel `onWriteCommitted`** (retour de relecture, absent de l'inventaire
  §1.4) s'exécute aujourd'hui sous le verrou (`BundleConceptWriter.cs:296-299`)
  et un hôte peut y appeler `OkfBundleTools.ReadConcept` sur la même racine : le
  moniteur réentrant l'admet, un sémaphore attendrait sur lui-même. Les
  paramètres de bail internes ne protègent pas du code hôte. Règle proposée :
  le rappel est **notifié après la libération du bail**, jamais dessous (la
  durée de ce verrou n'est plus liée au code de l'hôte). Cela change le
  moment du rappel (après le verrou, avant le retour de l'appel d'écriture) ;
  c'est un point à arbitrer, car un hôte qui compterait sur l'exclusion
  pendant le rappel la perd. À défaut, une politique de non-réentrance
  explicite en version Release : lever `InvalidOperationException` plutôt que
  se bloquer.
- **Les ponts synchrones** prennent le sémaphore par `Wait()`. C'est une attente
  de verrou, pas du sync-sur-async : le détenteur async n'a pas besoin du
  thread bloqué pour avancer, à condition que la bibliothèque utilise partout
  `ConfigureAwait(false)` (§3.3).
- **Le test #131** (`RecordVerificationTests.cs:549`) est la porte d'entrée.
  Les tests d'alias par jonction (#86) le sont aussi.

### 3.8 Sorties synchrones restantes, et AOT

- **Restent sync** : `Bundle.Load(string)`, `BundleConceptWriter(string)` et ses
  méthodes, `IndexGenerator.RegenerateIndexes(With)(string)`,
  `BundleValidator.Validate` (pont), toutes les requêtes de `Bundle`,
  `ConceptSearch`, `ConceptAudit`, le viewer, `OkfCli.Run`, `OkfRenderCli.Run`.
  La CLI passe à `RunAsync` seulement avec le premier provider non filesystem
  de `okf` (déclencheur du ticket, inchangé).
- **AOT.** Les machines à états async entrent dans `okf` par le cœur, même sur
  le chemin sync. L'impact sur la taille n'est **pas mesuré**. Il faut le
  mesurer à l'étape 2 et le consigner dans le CHANGELOG, comme les +164 Ko du
  viewer l'avaient été. `IAsyncEnumerable` et `ValueTask` sont compatibles AOT,
  sans réflexion.

### 3.9 Migration en étapes relisibles

| # | Étape | Taille | Porte |
|---|---|---|---|
| 1 | **Verrou d'écriture → bail + `SemaphoreSlim`**, encore appelé en synchrone (`Wait()`). Inventaire des imbrications, `…Locked(lease)`. Isole les régressions de verrou des régressions async. | M | #131 en boucle, tests d'alias #86, suite verte |
| 2 | **Contrat flippé** : `IBundleProvider` async ; provider filesystem et décorateur lecture seule à terminaison synchrone ; `LoadAsync`, `ListNamesAsync`, `ReadTextAtAsync` ; `Bundle.Load(string)` par le pont ; la quinzaine de fausses implémentations de test migrent via un adaptateur **de test** (sync → `ValueTask` terminées). Taille AOT mesurée. | L | goldens intouchés, smoke AOT, test « le pont lève sur un provider asynchrone » |
| 3 | **Écrivain et `IndexGenerator` async**, mode d'exécution pour les réessais, annulation avant commit et entre objets. | M | #131, tests d'annulation |
| 4 | **`BundleHandle` à vol unique**, `ValidateAsync`, `ResolveResourceKeyAsync`, `ReadResourceTextAsync`, `CaptureAsync`. | M | tests de vol unique et d'obligation, `ContainerIntegration` en local avec **0 ignoré** |
| 5 | **Consommateurs** : outils `Task<string>`, `OkfContextProvider` réellement async, sources du catalogue et `FileMemoryStore` sur `LoadAsync`, MCP vérifié ; retrait de `RunComputation` sync (obsolète depuis 0.7.0, « kept for one version »). | M | tests Agents/Catalog/Mcp, `producers/OkfProducer.sln` en local |
| 6 | **Lectures parallèles bornées** (async seulement), déterminisme des erreurs ; documentation (README, spec de la couture, CHANGELOG, `docs/spec-conformance/` si besoin). | S | tests de déterminisme |

Les membres `[Obsolete]` du lot D ne font partie d'aucune étape : leur retrait
est une décision de version (Q2).

### 3.10 Risques

- **R1. Sync-sur-async et interblocage.** Le pont n'attend jamais, ce qui
  l'exclut par construction. Le risque résiduel est un `Wait()` de sémaphore
  sous un contexte mono-thread dont le détenteur async aurait besoin. Il est
  couvert par `ConfigureAwait(false)` partout, une fois `CA2007` activée (§3.3).
- **R2. « One load at a time ».** La garantie est conservée par le vol unique,
  mais deux comportements bougent :
  - les comparisons concurrentes sont fusionnées ;
  - l'annulation d'un appelant n'interrompt plus le chargement des autres.

  Les deux sont à annoncer dans le CHANGELOG.
- **R3. Sémantique d'annulation.**
  - Une lecture ou un chargement annulés lèvent `OperationCanceledException`,
    jamais `BundleLoadException`.
  - Une écriture n'est annulable qu'**avant** son commit. Après, le résultat est
    rendu et la notification (`onWriteCommitted`, invalidation du handle)
    s'exécute quand même.
  - Une opération multi-objets annulée entre deux objets : Q5.
- **R4. Thread bloqué dans le provider filesystem.** Il y a les `Thread.Sleep`
  bornés sous Windows (§3.3), et la marche complète sur le thread appelant.
  Sous `OnAccess` dans un hôte ASP.NET, chaque appel d'outil fait une marche
  synchrone, **comme aujourd'hui**. Ce n'est pas une régression, mais ce n'est
  pas « async » non plus.
- **R5. Goldens.** Tout le chemin CLI passe par le pont. Un écart de texte
  vient d'un changement d'ordre (parallélisme : degré 1 en synchrone) ou d'un
  message d'exception (`OperationCanceledException` enveloppé par erreur). Les
  goldens ne se régénèrent jamais pour ça : c'est une régression côté C#.
- **R6. Changement cassant pour 0.7.0.** Les outils `string` publics, et
  `Validate` si on supprimait la version sync : Q3, Q4.
- **R7. Taille AOT de `okf`.** Elle est inconnue avant l'étape 2.

### 3.11 Les tests qui le prouvent (aucune borne d'horloge)

Toute concurrence est pilotée par des `TaskCompletionSource` et des compteurs,
jamais par un délai.

- **Le pont lève.** Un faux provider dont chaque `ValueTask` termine plus tard
  (`TaskCompletionSource` non résolue) : `Bundle.Load` par un adaptateur de test,
  `Validate`, `BundleConceptWriter` sync lèvent `InvalidOperationException`. Ils
  rendent la main immédiatement : le test vérifie que la `TaskCompletionSource`
  n'est **pas** résolue au retour.
- **Le provider filesystem termine en synchrone.** Un décorateur de test
  enregistre `IsCompletedSuccessfully` de chaque `ValueTask` rendue, sur toute
  la suite d'un chargement, d'une écriture, d'une régénération et d'un conflit
  provoqué. Les réessais sont exercés par le crochet `Sleep`, sans dormir.
- **Vol unique.** N appels concurrents de `GetCurrentAsync` sur un provider
  bloqué par une `TaskCompletionSource` : exactement **un** `EnumerateAsync`
  compté. L'annulation d'un appelant n'annule pas la `TaskCompletionSource`
  partagée, et les autres reçoivent l'image. Un `Invalidate()` pendant le
  chargement fait recharger l'accès suivant.
- **Annulation en plein chargement.** Le faux provider annule la
  `CancellationTokenSource` de l'appelant au k-ième nom listé ou à la k-ième
  lecture. Résultat : `OperationCanceledException`, l'image précédente reste
  servie, et l'énumérateur a reçu `DisposeAsync`.
- **Annulation en pleine écriture.** Annulée avant le commit : l'objet est
  inchangé. Annulée entre deux vérifications d'un lot : le rapport dit ce qui a
  atterri (Q5).
- **Abandon du listing.** Un listing infini s'arrête à `MaxEntries + 1`
  éléments produits, puis l'énumérateur est libéré.
- **Déterminisme des erreurs parallèles.** Les clés de rang 3 et 7 échouent,
  celle de rang 7 en premier : le message nomme la clé de rang 3.
- **Verrou.** #131 en boucle. Les tests d'alias #86. Un test structurel : une
  seconde acquisition attend tant que le bail est tenu (sa tâche n'est pas
  terminée), puis se termine après la libération.
- **Écriture sync sur provider async (retour de relecture).** Un provider dont
  les opérations sont pilotées par le test : l'appel sync d'écriture est
  rejeté, on complète ensuite l'opération en attente, et on vérifie qu'aucun
  commit n'a eu lieu.
- **Jonction consciente de la génération.** `Invalidate()` pendant un
  chargement, puis un accès avant sa fin : l'accès attend, puis obtient une
  image qui couvre sa génération (ordre forcé par événements).
- **`Dispose` sans annulation.** Un handle disposé, dont un chargement est en
  cours pour une requête sur l'ancien instantané du catalogue, termine et sert
  cette requête.
- **Rappel `onWriteCommitted` réentrant.** Le rappel appelle `ReadConcept` sur
  la même racine : il ne se bloque pas (selon la règle retenue en Q7).
- **Liaison des outils.** `GetTools` reste lié aux wrappers `*Tool` : une
  écriture sans observation est refusée, un appel public `WriteConcept` ne
  l'est pas.
- **Scope de listing à travers `await`.** Un chargement async sur le provider
  filesystem liste chaque répertoire une fois (`DirectoryListings`, `:81`),
  comme en synchrone.
- **Acceptation du ticket** : suite complète verte, aucun fichier de fixture
  touché, smoke AOT, `ContainerIntegration` en local avec 0 ignoré,
  `producers/OkfProducer.sln` vert en local.

## 4. Questions ouvertes (au mainteneur)

1. **Ordre avec la publication.** Faut-il sortir 0.8.0 (lots A–D) **avant**
   #152, avec un contrat sync qui sera cassé, ou après ?
   *Recommandation :* atterrir #152 avant de taguer 0.8.0, pour que le contrat
   provider ne soit publié qu'une fois, en async. Si une version est nécessaire
   avant, marquer `IBundleProvider`, `BundleHandle` et `Bundle.Load(IBundleProvider)`
   en `[Experimental("OKF0001")]` (BCL, .NET 8+). La portée exacte du
   diagnostic sur les signatures qui les mentionnent reste à vérifier.
2. **Retrait des membres `[Obsolete] … (#152)`.** Ils ont été publiés sans
   avertissement en 0.7.0, le CHANGELOG promet « kept one version with a
   warning », et aucun ne gêne le contrat async (§1.5).
   *Recommandation :* découpler. Ils restent obsolètes pendant toute la version
   qui embarque #152, et sont retirés dans la suivante (`Bundle.Root`,
   `IndexFiles`, `LogFiles`, `ParseErrors`, `TryResolveResource`,
   `ReadResourceText(string)`, `OkfBundleTools.BundleRoot`,
   `BundleConceptWriter.BundleRoot`). Leurs messages disent alors « removed in
   the release after 0.8.0 », et `ObsoleteMembersTests` cesse d'exiger
   « (#152) ». `RunComputation` sync, obsolète depuis 0.7.0, part avec #152.
3. **Méthodes outils publiques d'`OkfBundleTools`** (`string`, publiées en
   0.7.0). Faut-il les changer en place (rupture), ou ajouter des `…Async` ?
   *Recommandation :* ajouter `ReadConceptAsync` & co (`Task<string>`) pour les
   hôtes, **sans** y brancher `GetTools` : celui-ci continue de lier les
   wrappers internes `*Tool` (`ReadConceptTool`, `GetComputationTool`,
   `WriteConceptTool`, `RunComputationToolAsync`), qui portent les observations
   et la porte de §5.4. Les méthodes publiques n'enregistrent ni n'exigent rien,
   par construction (une injection de `OkfContextProvider` n'est pas une
   observation) ; les lier ou y mettre le contrôle casserait l'une des deux
   règles. Ces wrappers deviennent asynchrones (`Task<string>`), noms d'outils
   inchangés (explicites dans `AIFunctionFactory.Create`). Garder les versions `string`
   `[Obsolete]` une version, servies par le pont non bloquant : elles marchent
   sur le filesystem et lèvent sur un provider async.
4. **Validation.** `ValidateAsync`, ou préchargement au chargement
   (`index.md`/`log.md` lus, existence des ressources tirée du listing) ?
   *Recommandation :* `ValidateAsync`, et `Validate` sync gardé par le pont.
   Tirer l'existence du listing changerait `Unsafe` en `Missing` derrière un
   lien symbolique : le listing lenient saute les liens, alors qu'`Exists` STRICT
   les refuse. C'est un risque de diff sémantique sur `okf validate`. Et
   précharger coûterait une requête par ressource à chaque chargement, pour
   tous les consommateurs.
5. **Annulation d'une opération multi-objets après un premier commit**
   (`RecordVerifications`, régénération d'index, suppression d'un scope
   mémoire). Faut-il lever `OperationCanceledException`, ou rendre le rapport ?
   *Recommandation :* lever seulement si rien n'a été commité. Sinon, s'arrêter
   entre deux objets et rendre le `BundleWriteReport` / `VerificationOutcome`
   avec les objets restants « non tentés (annulé) », et invalider le handle.
   C'est la règle « l'opération dit exactement ce qui a atterri » de #139.
6. **`GetBundle` sous le verrou d'écriture.** Faut-il garder la sérialisation
   des lectures d'outils avec les écritures du même jeu d'outils (bail
   asynchrone tenu pendant un chargement, donc des secondes sur S3, qui
   bloquent les écritures), ou s'en remettre à l'obligation de rechargement ?
   *Recommandation :* la garder dans #152, pour ne pas changer de comportement :
   aujourd'hui, aucune image ne mélange une régénération d'index à moitié faite
   par ce même jeu d'outils. La relâcher avec #179 (mise à jour incrémentale),
   qui redéfinit de toute façon ce qu'une écriture fait à l'image.
7. **Rappel `onWriteCommitted` (retour de relecture).** Notifier après la
   libération du bail (§3.7), ou exiger la non-réentrance en Release ?
   *Recommandation :* notifier après la libération. C'est un changement de
   moment observable pour un hôte qui comptait sur l'exclusion pendant le
   rappel ; le CHANGELOG le dira.
