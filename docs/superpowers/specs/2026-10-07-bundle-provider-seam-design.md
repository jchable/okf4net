# Couture « provider de bundle » sous `Bundle` — design

Date : 2026-10-07
Statut : proposé
Ticket : [#139](https://github.com/jchable/okf4net/issues/139) (les décisions
datées ci-dessous y sont consignées en commentaires, du 2026-10-05 au 2026-10-07)

## Contexte et motivation

Aujourd'hui un bundle **est** un répertoire local, et cette hypothèse n'est pas
derrière une couture : elle traverse l'API publique et ses consommateurs.

- `Bundle.Load(string root)` (`src/OKF4net/Bundle.cs`) parcourt le répertoire
  avec `Directory`/`File`.
- `Concept.Path`, `Bundle.IndexFiles`, `Bundle.LogFiles` et
  `Bundle.ParseErrors` exposent des chemins du système de fichiers.
- `Bundle.TryResolveResource(..., out string? absolutePath, ...)` et
  `Bundle.ReadResourceText(string absolutePath)` remettent un chemin hôte
  absolu à l'appelant ; `AttestationOrchestrator` lit par ce biais le script
  sanctionné et l'attester qu'il fait ensuite exécuter.
- `BundleConceptWriter(string bundleRoot, ...)` écrit avec `File.*`, en place
  (`File.WriteAllText`), sous un registre de verrous **propre au processus**,
  indexé sur la racine canonique, **lié au thread** (`[ThreadStatic]`).
- `IndexGenerator.RegenerateIndexes(string bundleRoot)` parcourt et écrit le
  répertoire ; `BundleValidator.Validate` relit `index.md`/`log.md` et teste
  l'existence de ressources directement sur disque.
- `OkfBundleTools` garde une chaîne `BundleRoot`, met en cache
  `Bundle.Load(BundleRoot)` jusqu'à ses propres écritures, et fait une dizaine
  d'appels `File.*`/`Directory.*` de son côté ;
  `OkfBundleKnowledgeSource.SearchAsync` recharge le bundle **à chaque
  requête**. Deux politiques de fraîcheur opposées coexistent donc.
- Le README de `OKF4net.Catalog` l'écrit : « Local filesystem bundles only — no
  remote/HTTP sources ».

`IKnowledgeSource`/`IMemoryStore` abstraient déjà la recherche et la mémoire un
niveau au-dessus, mais cela ne suffit pas : les outils agents (validation,
audit, vérification, graphe, ressources, `okf_run_computation`) opèrent sur un
`Bundle`. Un bundle stocké ailleurs ne peut être servi par eux que si la
couture passe **sous** `Bundle`.

## Objectif

Les agents (et l'hôte MCP) peuvent travailler sur un bundle qui ne vit pas sur
le disque local. **La lecture est l'objectif ; l'écriture est une capacité
optionnelle.** Ce document fixe la couture et son contrat ; le premier — et ici
le seul — backend est le système de fichiers, qui sert à revalider l'API de bout
en bout avant tout autre backend.

## Périmètre

Dans ce design (#139, puis #140 pour le provider filesystem) :

- le contrat `IBundleProvider`, **synchrone** dans cette première étape ;
- les clés (`BundleKey`) et leur validation en trois couches ;
- la capacité d'écriture, la lecture seule et sa garde ;
- la concurrence optimiste et l'atomicité par objet ;
- l'image chargée, sa version et les politiques de rafraîchissement ;
- les bornes par défaut ;
- les règles de sécurité transverses (credentials, erreurs, URL, exécution) ;
- le provider filesystem, par défaut, dans `OKF4net` ;
- la migration de tous les consommateurs vers la couture.

Hors de ce design, chacun dans son ticket :

| Sujet | Ticket |
|---|---|
| Revalidation de bout en bout sur le filesystem | #140 |
| Provider ressources embarquées dans un assembly | #141 |
| Provider S3 (SDK AWS) — dépend du contrat asynchrone | #142 |
| Documentation | #143 |
| Estimation mémoire et plafond par source (refus) | #151 |
| Contrat asynchrone (`ValueTask`, `IAsyncEnumerable`) | #152 |
| Compaction, corps évictables, accès asynchrone aux corps | #153 |
| Liste blanche d'empreintes du code sanctionné | #154 |
| Verrou OS inter-processus sur le filesystem (reporté) | #178 |
| Mise à jour incrémentale de l'image après écriture | #179 |

Explicitement hors périmètre : un provider mémoire distant (`FileMemoryStore`
reste adossé au filesystem, via le provider) ; le choix d'un provider autre que
filesystem pour `okf-mcp` et `okf` ; les transactions multi-objets ; un niveau
de confiance par source ; un serveur live.

## Design

### 1. Le contrat `IBundleProvider`

Un provider expose un espace de **clés** et des **objets** (des octets) sous ces
clés. Il ne connaît ni concepts, ni liens, ni frontmatter : tout le sens OKF
reste dans le cœur.

Forme (noms provisoires, synchrone jusqu'à #152) :

```csharp
public interface IBundleProvider
{
    /// Identité stable et non secrète : clé des verrous en processus, diagnostics.
    string Id { get; }

    /// Emplacement affichable et non secret (filesystem : la racine telle que donnée).
    string Location { get; }

    BundleProviderCapabilities Capabilities { get; }   // CanWrite, au minimum

    /// Contraintes d'alias propres au stockage (§2) — jamais désactivables.
    BundleKeyConstraints NativeConstraints { get; }

    /// Jeton de version du bundle entier, bon marché ; sert au rafraîchissement.
    BundleVersion GetVersion();

    /// Noms bruts relatifs à la racine, éventuellement sous un préfixe.
    /// Le cœur les valide en BundleKey (§2) : le provider ne valide rien lui-même.
    IEnumerable<BundleListing> Enumerate(BundleKey? prefix = null);

    /// Contenu + version de l'objet. Condition optionnelle (épinglage, §5.4).
    BundleObject Read(BundleKey key, BundleObjectVersion? expected = null);

    bool Exists(BundleKey key);

    /// Écriture et suppression conditionnelles (§4). Interdites si !CanWrite.
    BundleObjectVersion Write(BundleKey key, ReadOnlySpan<byte> content, BundleWriteCondition condition);
    void Delete(BundleKey key, BundleWriteCondition condition);
}
```

- `BundleListing` : le nom brut, la taille si connue, et la version de l'objet
  si le backend la donne sans surcoût (S3 la donne avec le listing ; le
  filesystem non).
- `BundleObject` : un flux de contenu et sa `BundleObjectVersion`. Le cœur lit ce
  flux **en comptant les octets** (§6).
- `BundleWriteCondition` : `None` (écriture inconditionnelle),
  `MustNotExist`, `IfMatch(version)`.
- Exceptions du contrat : `BundleReadOnlyException` (écriture sur un provider
  sans `CanWrite`), `BundleWriteConflictException` (condition non satisfaite),
  `BundleObjectNotFoundException`.
- `BundleVersion` et `BundleObjectVersion` sont des jetons **opaques**, comparés
  en ordinal ; seul le provider en connaît la construction.

**Une seule interface, capacité déclarée.** Un provider sait toujours lire ; il
déclare `CanWrite`. L'hôte peut **rétrograder** un provider en lecture seule
(décorateur `provider.AsReadOnly()`), jamais l'inverse.

**Le flux de responsabilités est le suivant :** le provider liste des noms bruts
et lit/écrit des octets sous des clés déjà validées ; le cœur valide les clés,
applique les bornes, construit l'image, résout les liens et les chemins §6.2,
fait les relectures-réessais, et garde la capacité d'écriture.

### 2. Les clés : `BundleKey`, en trois couches

Le cœur possède le type `BundleKey` et le **seul** point de validation. Un
provider ne reçoit jamais qu'une clé déjà validée ; il doit la faire correspondre
**de façon injective** à son stockage, sous une racine ou un préfixe **terminé
par un séparateur** (`bundle/`, jamais `bundle`, pour que `bundle-evil/` ne soit
jamais inclus).

**Couche 1 — socle (cœur, jamais désactivable).**

- Segments séparés par `/` ; pas de `/` en tête ni en fin ; aucun segment vide,
  `.` ou `..`.
- Aucun `\`, NUL ni caractère de contrôle (U+0000–U+001F, U+007F–U+009F).
- Au plus 1 024 octets en UTF-8.
- Comparaison **ordinale, sensible à la casse** (comme `Bundle` aujourd'hui).
- Le cœur ne **décode jamais** l'encodage pourcent : `%2e%2e` est un nom
  littéral. Encoder la clé dans ses requêtes est l'affaire du provider.

Une clé de concept se termine par `.md` ; l'identifiant de concept qu'on en
dérive doit en plus passer `ConceptId.ValidateSegment`, inchangé.

**Couche 2 — contraintes natives (déclarées par chaque provider, jamais
désactivables).** Ce sont les règles d'alias de son stockage, c'est-à-dire le
plancher de sécurité : deux clés distinctes ne doivent jamais désigner le même
objet.

| Provider | Contraintes natives |
|---|---|
| Filesystem sous Windows | pas de `.` ni d'espace final dans un segment ; pas de `<>:"\|?*` ; pas de nom de périphérique (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `LPT1`–`LPT9`, avec ou sans extension, quelle que soit la casse) ; collisions de casse |
| Filesystem sous macOS | collisions de casse ; NFC exigé |
| Filesystem sous Linux | aucune au-delà du socle |
| S3 | aucune au-delà du socle |

Le filesystem déduit ses contraintes de l'OS par défaut. **La vraie frontière est
le volume, pas l'OS** (dossier rendu sensible à la casse sous Windows, APFS
sensible à la casse, montage vfat/NTFS sous Linux) ; la documentation dit qu'un
tel hôte doit rester en `Strict`.

**Couche 3 — politique de portabilité (choisie par l'hôte, jamais par le
contenu).**

- `Strict` (**défaut**) : l'union de toutes les contraintes natives connues. Un
  bundle valide sur un provider est valide sur tous.
- `Native` : les contraintes du provider courant seulement.
- Des validateurs supplémentaires peuvent être ajoutés, mais **uniquement pour
  restreindre** ; aucune configuration ne descend sous `Native`.

**Collisions de casse.** Quand la politique les interdit et que deux clés d'un
même bundle ne diffèrent que par la casse, **toutes** les clés en collision sont
écartées avec un diagnostic : le cœur ne peut pas savoir laquelle est la bonne.

**Énumération lenient, lecture/écriture stricte**, sur le modèle des deux
prédicats de `ReparsePoints` : un nom brut invalide trouvé en énumérant est
ignoré avec un diagnostic ; une clé invalide demandée en lecture ou en écriture
lève une exception.

**Résolution des champs de chemin du §6.2, dans le cœur, lexicalement sur les
clés** (elle ne dépend plus d'aucune sémantique de chemin de l'OS) :

| Valeur brute | Résultat |
|---|---|
| URL `scheme://…` | statut `Url`, **jamais déréférencée** |
| `/x` | depuis la racine |
| `./x`, `../x` | depuis le dossier du concept |
| `x` (chemin nu) | depuis la racine — lecture locale du §6.2, consignée dans `docs/spec-conformance/` (S6.2-1) |
| remonte au-dessus de la racine | `Unsafe` |
| forme à lettre de lecteur (`C:\x`, `e:q.sql`) | `Unsafe` |
| contient `\` | `Unsafe` (voir les points à valider) |
| segments vides ou `.` | ignorés (`a//./b` → `a/b`), comme `GetFullPath` aujourd'hui |

Le résultat est une `BundleKey` ; la clé est ensuite soumise aux couches 1 à 3
comme n'importe quelle autre.

### 3. Capacité d'écriture et lecture seule

- **Toute écriture est vérifiée contre la capacité du provider avant d'être
  tentée.** Sur un provider sans `CanWrite`, elle lève `BundleReadOnlyException`
  sans aucun effet de bord : ni écriture partielle, ni no-op silencieux.
- Cette garde vit dans le cœur, à chaque point d'entrée d'écriture
  (`BundleConceptWriter`, `IndexGenerator`, `FileMemoryStore`), et le provider
  la répète en dernier rempart.
- Outils agents : la permission d'écrire effective est
  `provider.CanWrite && mode != OkfToolMode.ReadOnly`. Quand elle est fausse, les
  outils d'écriture (`OkfBundleTools.WriteToolNames`) ne sont **pas proposés**
  au modèle, par le même filtrage que `OkfToolMode.ReadOnly` ; l'exception de la
  bibliothèque reste le dernier rempart.
- `okf_run_computation` n'est pas une écriture : il reste hors de
  `WriteToolNames` et hors du circuit d'approbation.

### 4. Concurrence et atomicité

**Modèle : concurrence optimiste par objet.**

- Une lecture renvoie le contenu et sa `BundleObjectVersion` ; une écriture
  fournit une `BundleWriteCondition`. Le provider remplace l'objet
  **atomiquement si et seulement si** la condition tient, sinon il lève
  `BundleWriteConflictException`.
- S3 : `If-Match` / `If-None-Match: *`. Filesystem : fichier temporaire dans le
  même dossier → revérification de la version → `File.Replace`, sous le verrou
  en processus.
- **Version d'objet sur le filesystem : SHA-256 du contenu**, pas la date de
  modification, dont la résolution est d'1 à 2 s sur certains systèmes de
  fichiers.
- Conséquence : le remplacement atomique est une **exigence du contrat**, et
  l'item du ROADMAP « Atomic write-then-rename in `BundleConceptWriter` » est
  absorbé par #140, avec ses tests (sémantique de `File.Replace`, garde de
  chemin sur le nom temporaire, verrou).
- Le verrou en processus reste, comme optimisation contre les conflits inutiles
  et comme garantie à l'intérieur d'un processus. Sur le filesystem, sa logique
  actuelle (registre lexical + résolu) est conservée telle quelle.

**Fenêtre inter-processus sur le filesystem.** Entre la revérification et
`File.Replace`, un autre processus peut écrire. Cette fenêtre est **acceptée et
documentée** ; le verrou OS qui la ferme est #178.

**Réécriture complète d'un concept.**

- API : `WriteConcept` accepte une version attendue **optionnelle** ; sans elle,
  l'écriture est inconditionnelle, comme aujourd'hui.
- Outils agents : l'écriture est **implicitement conditionnée à la version du
  concept dans l'image servie** (`MustNotExist` s'il en est absent). En cas de
  conflit, le modèle reçoit un message rédigé par la bibliothèque (le concept a
  changé depuis sa lecture, le relire avec `okf_read_concept`) et l'image est
  rafraîchie. Le schéma des outils ne change pas.

**Opérations sur plusieurs objets** (`RecordVerifications`,
`RegenerateIndexes`, `FileMemoryStore.DeleteScopeAsync`) : **pas de
transaction**. Chaque objet est écrit atomiquement ; l'opération rapporte
exactement ce qui a été écrit et ce qui a échoué, avec la raison ; elle est
relançable sans risque (pas de tampon de vérification dupliqué, index recalculés
en entier).

**Nouveaux essais.**

- Les opérations lire-modifier-écrire (append, verify, log, régénération
  d'index) réessaient sur conflit : **3 tentatives, délai aléatoire croissant**,
  en relisant l'état courant à chaque fois.
- Les écritures conditionnées à l'image **ne réessaient jamais
  automatiquement** : réessayer écraserait ce que l'agent n'a pas vu.
- Après épuisement des essais, `BundleConceptWriter` rend le conflit comme ses
  autres erreurs attendues (`Error: …`), fidèle à sa convention.

### 5. Image, version et rafraîchissement

**5.1 L'image.** Un `Bundle` reste une image **complète et immuable** du bundle
(validation, graphe, backlinks et `ConceptSearch` ont besoin du corpus entier).
Elle porte la `BundleVersion` du provider au moment du chargement et la
`BundleObjectVersion` de chaque objet chargé. `OkfDocument.Body` reste une
`string` synchrone (le passage à un accès asynchrone éventuel relève de #153).

**5.2 Le détenteur d'image : `BundleHandle`.** Un objet par source réunit
provider, options (politique de rafraîchissement, bornes, portabilité) et image
courante. Il est **partagé** par `OkfBundleTools`, `OkfContextProvider` et
`OkfBundleKnowledgeSource`, ce qui remplace les deux politiques opposées
d'aujourd'hui.

- **Un seul chargement à la fois** : des appelants concurrents attendent le
  chargement en cours au lieu d'en lancer chacun un.
- Le remplacement de l'image est atomique ; un appel ne voit jamais qu'une
  seule version.
- **Échec d'un rafraîchissement** (réseau, plafond de #151) : l'image
  précédente continue d'être servie, marquée périmée.
- **Après ses propres écritures** : invalidation, puis rechargement à la
  prochaine utilisation (comportement actuel). La mise à jour incrémentale est
  #179.

**5.3 Les politiques.** Le provider fournit `GetVersion()` (obligatoire) et, en
option, `IBundleChangeNotifier` (un flux de signaux de changement). Le cœur
implémente une fois pour tous les providers :

| Politique | Comportement |
|---|---|
| `Manual` (**défaut**) | l'hôte appelle `Refresh()` |
| `OnAccess` | comparaison du jeton avant chaque utilisation de l'image |
| `Interval(N)` | comparaison au plus toutes les N secondes, à l'accès |
| `Notify` | un signal marque l'image « à vérifier » ; comparaison à l'accès suivant |

- Toutes les politiques sont **paresseuses** : aucune minuterie ni aucun thread
  d'arrière-plan dans le cœur.
- `Notify` n'est disponible que si le provider implémente
  `IBundleChangeNotifier` ; la demander sinon est une erreur de configuration
  explicite.
- **Une notification est un indice, jamais une vérité** : elle déclenche une
  comparaison de jeton, jamais un rechargement aveugle (`FileSystemWatcher`
  perd des événements).
- Aucun outil de rafraîchissement n'est exposé au modèle.

**5.4 Épinglage du code exécuté.** L'orchestrateur lit le code sanctionné **dans
la version de l'image chargée** :

- un objet chargé dans l'image (concept, donc computation en ligne) est déjà
  figé ;
- une ressource lue à l'exécution (fichier de computation, attester) est lue
  avec `expected` = la version donnée par le listing de l'image quand le
  provider la connaît (S3) ; sinon, le cœur compare la `BundleVersion` avant et
  après la lecture et refuse s'il y a eu changement (filesystem).

### 6. Bornes par défaut

Finies **pour tous les providers, filesystem compris**.

| Borne | Défaut | Dépassement |
|---|---|---|
| Taille d'un fichier (concept ou ressource) | 16 Mio | concept : entrée dans `Bundle.ParseErrors`, le chargement continue (§11) ; ressource : exception à la lecture |
| Entrées par bundle | 1 000 000 | chargement interrompu (`BundleLoadException`) |
| Profondeur | 64 | chargement interrompu |
| Longueur de clé | 1 024 octets UTF-8 | entrée ignorée avec diagnostic |
| Délai par requête (providers réseau) | 30 s | échec de la requête et du chargement |
| Requêtes simultanées (providers réseau) | 16 | file d'attente |

Les quatre premières sont appliquées **dans le cœur** (lecture des flux en
comptant les octets, comptage des entrées et des segments), si bien qu'un
provider défaillant ne peut pas les contourner. Les deux dernières sont de la
responsabilité du provider. Le plafond mémoire de #151 s'y ajoute, sans défaut.

### 7. Sécurité transverse

**Modèle de menace.** Le contenu du bundle n'est pas fiable ; la configuration de
la source l'est. `catalog.json` est de la configuration d'opérateur.

- **Jamais d'URL suivie depuis le contenu.** Les cibles réseau ne viennent que de
  la configuration de l'hôte.
- **Credentials injectés uniquement.** Un provider reçoit un client ou des
  credentials déjà configurés et n'en construit jamais à partir de sources
  ambiantes. Un exécutable final (`okf-mcp`) peut choisir la chaîne par défaut
  d'un SDK dans sa propre racine de composition, et le documente.
- **Aucun secret dans `catalog.json`** (mécanisme : §9, Catalog).
- **Erreurs traduites.** Une erreur de provider devient un message rédigé par la
  bibliothèque avant d'atteindre un résultat d'outil ou le modèle (ni bucket, ni
  identifiant de requête, ni URL signée, ni chemin hôte) ; le détail brut ne va
  qu'au journal de l'hôte. Même classe de bug que #70, même frontière que #122.
- **Exécution de code (§10).** Brancher un `AttestationOrchestrator` reste
  l'accord explicite de l'hôte ; la documentation dit que, sur une source
  distante, c'est faire confiance à tous ceux qui peuvent y écrire. Épinglage :
  §5.4. Liste blanche d'empreintes optionnelle : #154.
- **Aucun niveau de confiance par source.** Toutes les sources sont non fiables ;
  l'hôte exclut une source par `SourceVisibilityPolicy`/`PermittedSourceIds`, et
  chaque passage porte déjà `KnowledgePassage.SourceId`.

### 8. Le provider filesystem

- Il vit dans `OKF4net` et c'est le **provider par défaut** quand aucun n'est
  fourni : `okf` (BCL seule, Native AOT) et toutes les captures golden en
  dépendent.
- Il conserve tous les gardes actuels, déplacés derrière la couture : prédicats
  STRICT/lenient de `ReparsePoints` (lenient pour l'énumération, STRICT pour la
  lecture et l'écriture), confinement sous la racine, revérification tardive
  avant écriture, registre de verrous.
- Il déclare ses contraintes natives d'après l'OS (§2).
- `GetVersion()` : une empreinte du listing (clé, taille, date de modification).
  Ce jeton est **faible** : deux modifications de même taille dans la même
  seconde peuvent lui échapper. L'effet est une image périmée jusqu'au
  changement suivant, **jamais une perte de données**, puisque les écritures
  sont protégées par la version d'objet (le hash du contenu).
- Les surcharges `string root` restent les points d'entrée commodes :
  `Bundle.Load(string root)`, `BundleConceptWriter(string bundleRoot, …)`,
  `IndexGenerator.RegenerateIndexes(string)`, `OkfBundleTools(string, …)`,
  `OkfBundleKnowledgeSource(string, string)`. Chacune construit le provider
  filesystem.

### 9. Les consommateurs

| Consommateur | Changement |
|---|---|
| `Bundle` | `Load(IBundleProvider, BundleLoadOptions?)` ; `Concept.Path` → `Concept.Key` ; `IndexFiles`/`LogFiles` → `BundleKey` ; `ParseErrors` → `(BundleKey Key, string Error)` ; `Root` → `Provider` + `Location` ; `TryResolveResource(..., out BundleKey? key, ...)` ; `ReadResourceText(BundleKey)` |
| `BundleValidator` | relit `index.md`/`log.md` et teste les ressources via le provider (avec les bornes), plus via `File.*` |
| `BundleConceptWriter` | constructeur sur `BundleHandle` (ou provider) ; version attendue optionnelle ; garde de capacité ; relectures-réessais ; atomicité via le provider |
| `IndexGenerator` | surcharges sur provider ; garde de capacité ; écritures conditionnelles |
| `OkfBundleTools` | constructeur sur `BundleHandle` ; plus aucune E/S directe ; filtrage des outils d'écriture selon la permission effective ; écritures conditionnées à l'image |
| `OkfContextProvider` | partage le `BundleHandle` de ses outils ; plus d'E/S bloquante hors de la couture |
| `OkfBundleKnowledgeSource` | constructeur sur `BundleHandle` ; ne recharge plus à chaque requête, il suit la politique de la source |
| `FileMemoryStore` | API publique inchangée ; passe par le provider filesystem ; reste filesystem |
| Catalog (`catalog.json`) | voir ci-dessous |
| `AttestationOrchestrator` | lit le code sanctionné via `ReadResourceText(BundleKey)`, épinglé (§5.4) |
| `okf` / `okf-render` | restent synchrones, sur le provider filesystem ; leurs octets de sortie ne changent pas (c'est la CLI qui compose le chemin affiché à partir de `Location` et de la clé) |
| `okf-mcp` | inchangé (`OKF_BUNDLE_ROOT`, filesystem) |
| `producers/` | une recherche n'y trouve aucun usage des membres qui changent, mais une recherche ne prouve rien : la vérification est la compilation et les tests de `producers/OkfProducer.sln` en local |

**Catalog : nommer un provider dans `catalog.json`.** Une source peut porter
`"provider": "<type>"` et un objet `"settings"`. Le type `filesystem` reste le
défaut, avec `path` comme aujourd'hui. L'hôte enregistre des fabriques de
providers par type (options de `FileKnowledgeCatalog`, et une extension
d'enregistrement dans `OKF4net.Catalog.Hosting`). Chaque fabrique **déclare la
liste des réglages qu'elle accepte** (pour S3 : bucket, préfixe, région,
endpoint) ; tout autre réglage est rejeté par un diagnostic. Ainsi aucun champ de
credential ne peut entrer par `catalog.json`, puisque les credentials passent par
le client injecté.

### 10. Changements cassants et changements de comportement

À consigner au CHANGELOG, dans une même version :

- **Cassants (API)** : `Concept.Path`, `Bundle.Root`, `IndexFiles`, `LogFiles`,
  `ParseErrors`, `TryResolveResource`, `ReadResourceText` (§9). La migration
  asynchrone (#152) sera un second changement cassant, accepté.
- **Comportement, filesystem compris** :
  - bornes finies par défaut (§6) ;
  - politique de clés `Strict` (§2) : certains noms légaux sous Linux sont
    écartés, avec un diagnostic ou le statut `Unsafe`, jamais silencieusement ;
  - écritures atomiques et conditionnelles (§4) ;
  - `OkfBundleKnowledgeSource` ne recharge plus à chaque requête (§5).
- **Inchangé** : les octets de sortie de `okf` et `okf-render`. Aucun nom de
  fichier de `tests/fixtures/` ni de `bundles/` n'est écarté par `Strict`
  (vérifié le 2026-10-06 sur les 146 entrées : ni `:`, ni `.` ou espace final, ni
  nom de périphérique, ni caractère non-ASCII, ni collision de casse).

### 11. Vérification

- `dotnet test OKF4net.sln` vert, **sans toucher à aucune fixture** ; format et
  publication AOT verts ; ensemble de dépendances de `okf` inchangé.
- **Pas d'E/S résiduelle hors de la couture** : prouvé en remontant les points
  d'entrée publics (surcharges et valeurs par défaut comprises), pas par un grep
  de symboles.
- **Lecture seule** : un provider filesystem rétrogradé en lecture seule fait
  échouer chaque chemin d'écriture avec `BundleReadOnlyException`, sans rien
  modifier sur disque ; les outils d'écriture ne sont pas proposés.
- **Concurrence** : le test concurrent de #131 reste vert de façon répétée (s'il
  rougit, c'est une régression, pas un aléa) ; conflit d'une écriture
  conditionnée à l'image → message au modèle et rafraîchissement ; réessais
  bornés des opérations lire-modifier-écrire.
- **Batterie hostile exécutée**, commune à tous les providers et lancée sous
  `Native` (le plancher) : traversée, séparateurs encodés, `..` littéral contre
  normalisation côté client, confusion de préfixe, alias de casse et NFC/NFD,
  `.`/espace final et noms de périphériques, objets et listings surdimensionnés,
  redirections. Elle inclut le cas d'un segment de `ConceptId` qui se termine par
  `.`, accepté aujourd'hui et peut-être déjà source d'alias sous Windows (non
  vérifié à ce jour).
- `dotnet test producers/OkfProducer.sln` vert en local ;
  `dotnet test tests/OKF4net.Tests --filter Category=ContainerIntegration` en
  local avec **0 test ignoré**, puisque l'attestation lit désormais via la
  couture.

## Points à valider à la relecture

Ces choix n'ont pas été discutés explicitement ; ils découlent des décisions
prises, mais l'auteur les a tranchés seul.

1. **Exception contre chaîne d'erreur.** `BundleConceptWriter` ne lève jamais
   d'exception pour une erreur attendue (il rend `Error: …`). Ce design lui fait
   lever `BundleReadOnlyException`, puisque la décision prise est « émettre une
   exception » et qu'écrire sur une source en lecture seule est une erreur de
   configuration, pas un événement attendu. Un conflit après épuisement des
   essais, lui, est attendu et reste une chaîne `Error: …`.
2. **Les outils d'écriture sont retirés**, et non proposés puis refusés, quand
   la permission effective est fausse (même mécanisme que
   `OkfToolMode.ReadOnly`).
3. **Une seule interface avec un drapeau `CanWrite`**, et un décorateur
   `AsReadOnly()`, plutôt qu'une interface d'écriture séparée. C'est plus simple
   pour les consommateurs ; la garde passe au moment de l'exécution plutôt qu'à la
   compilation.
4. **Le provider renvoie des noms bruts et le cœur valide**, pour garder un seul
   point de validation.
5. **`catalog.json` : liste blanche des réglages déclarée par chaque fabrique**,
   ce qui affine la décision « un champ ressemblant à un credential est rejeté » :
   une liste blanche ne dépend d'aucune heuristique sur les noms.
6. **Une ressource trop grosse lève une exception dérivée d'`IOException`**, pour
   que les gestionnaires existants (qui traitent `IOException` comme « illisible »)
   la classent correctement sans modification.
7. **Un `\` dans une valeur de chemin §6.2 donne `Unsafe`.** Aujourd'hui, sous
   Windows, `references\a.sql` est résolu, alors que sous Linux c'est un nom de
   fichier contenant un `\`. Le design choisit un résultat portable et qui échoue
   fermé ; c'est un changement de comportement pour les bundles écrits sous
   Windows avec des `\`.
8. **Collisions de casse : toutes les clés en collision sont écartées**, et non
   seulement la seconde.
9. **Aucune minuterie d'arrière-plan** : `Interval` et `Notify` se vérifient au
   prochain accès.
10. **Épinglage des ressources sur le filesystem** par comparaison du jeton de
    bundle, qui est faible (§8). La fenêtre résiduelle est celle de ce jeton.
11. **`Bundle.Root` remplacé par `Provider` + `Location`**, plutôt qu'un `Root`
    nullable qui ne vaudrait que pour le filesystem.

## Risques et limites

- **Taille du changement.** Il touche l'API publique du cœur et tous ses
  consommateurs. Le plan doit le découper en PR relisibles : contrat et
  extraction du filesystem d'abord, consommateurs ensuite.
- **Le registre de verrous du filesystem** est subtil (registres lexical et
  résolu, réentrance explicite, comportement sur alias). Le déplacer derrière la
  couture sans changer sa sémantique est le passage le plus risqué ; le test
  concurrent de #131 et les tests de verrou existants en sont la barrière.
- **Jeton de version faible sur le filesystem** : une modification peut échapper
  au rafraîchissement jusqu'à la suivante (§8). C'est sans perte de données, et
  documenté.
- **Fenêtre inter-processus sur le filesystem** : acceptée (§4, #178).
- **Bundles existants sous Linux** dont des noms tombent sous `Strict` :
  diagnostics nouveaux. C'est l'hôte qui choisit de passer en `Native`.

## Travaux futurs

Tickets #140, #141, #142, #143, #151, #152, #153, #154, #178, #179 (voir Périmètre).
