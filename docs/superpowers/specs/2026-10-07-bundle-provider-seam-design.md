# Couture « provider de bundle » sous `Bundle` — design

Date : 2026-10-07
Statut : proposé
Ticket : [#139](https://github.com/jchable/okf4net/issues/139) (les décisions
datées ci-dessous y sont consignées en commentaires, du 2026-10-05 au 2026-10-07)

**Révision (revue interne, 2026-10-08)** : une revue de la version 1, faite en
vérifiant ses affirmations sur le code, a trouvé six constats importants et dix
modérés. Tous sont intégrés.

Les constats importants :

- **Chemins d'écriture oubliés** : `log.md` (écrit par `OkfBundleTools.AppendLog`
  hors du writer), la suppression d'un scope mémoire, la création des dossiers
  parents, et les verbes `parse`/`fmt` qui opèrent sur un fichier isolé.
- **Affichage des chemins sous Windows** : les tests golden normalisent les `\`
  et ne peuvent donc pas garantir des octets inchangés. La règle d'affichage est
  maintenant écrite, et un test non normalisé est exigé.
- **Surfaces publiques manquantes** dans la liste des changements cassants :
  `Diagnostic.Path`, `KnowledgeCatalogSource.Path`, le Viewer.
- **Fraîcheur du catalogue** : une source créée depuis `catalog.json` est
  maintenant en `OnAccess` par défaut (arbitrage du 2026-10-08).
- **Périmètre de l'énumération** : il est maintenant défini (arbitrage du
  2026-10-08).
- **Alias de casse sur une clé lue hors du listing** : la correspondance doit
  être exacte en casse et en forme Unicode.
- **Clés mémoire sous `Strict`** : la partie lisible des identifiants est
  désormais encodée, avec une note de migration (arbitrage du 2026-10-08).

Parmi les modérés, le plus notable : l'épinglage du code exécuté porte
maintenant sur la version que l'outil a **montrée**.

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
    /// Identité stable et non secrète : diagnostics, et clé des verrous en
    /// processus pour les providers autres que filesystem (§4).
    string Id { get; }

    /// Emplacement affichable et non secret (filesystem : la racine telle que donnée).
    string Location { get; }

    BundleProviderCapabilities Capabilities { get; }   // CanWrite, au minimum

    /// Contraintes d'alias propres au stockage (§2) — jamais désactivables.
    BundleKeyConstraints NativeConstraints { get; }

    /// Jeton de version du bundle entier, bon marché ; sert au rafraîchissement.
    BundleVersion GetVersion();

    /// Noms bruts relatifs à la racine, éventuellement sous un préfixe.
    /// Le préfixe s'arrête à une frontière de segment : "a" couvre "a/…",
    /// jamais "ab/…" (même confusion que celle interdite pour la racine, §2).
    /// Le cœur les valide en BundleKey (§2) : le provider ne valide rien lui-même.
    IEnumerable<BundleListing> Enumerate(BundleKey? prefix = null);

    /// Contenu + version de l'objet. Condition optionnelle (épinglage, §5.4).
    BundleObject Read(BundleKey key, BundleObjectVersion? expected = null);

    bool Exists(BundleKey key);

    /// Écriture et suppression conditionnelles (§4). Interdites si !CanWrite.
    /// ReadOnlyMemory et non ReadOnlySpan : la forme doit survivre au passage
    /// en ValueTask (#152), qui ne peut pas capturer un Span.
    BundleObjectVersion Write(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition);
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
écartées : le cœur ne peut pas savoir laquelle est la bonne. Le diagnostic suit
la règle du périmètre de l'énumération, plus bas.

**Énumération lenient, lecture/écriture stricte**, sur le modèle des deux
prédicats de `ReparsePoints` : un nom brut invalide trouvé en énumérant est
écarté ; une clé invalide demandée en lecture ou en écriture lève une exception.

**Périmètre de l'énumération** (arbitrage du 2026-10-08). Aujourd'hui
`CollectMarkdown` ne retient que les `.md` et ne regarde pas les autres noms.
Le comportement devient :

- **La borne d'entrées (§6) compte toutes les entrées énumérées**, quelle que
  soit leur extension : c'est le coût réel d'un listing. Aucun dossier n'est
  écarté par son nom (`.git`, `node_modules` comptent), comme aujourd'hui où le
  parcours y descend aussi.
- **Les diagnostics de clé ne concernent que deux cas.**
  - Un nom en `.md` invalide : une entrée dans `Bundle.ParseErrors`, comme un
    fichier illisible aujourd'hui.
  - Une ressource référencée par un champ §6.2 dont la clé est invalide : le
    statut `Unsafe`.
- **Tout autre nom invalide est écarté sans bruit.** Un fichier non référencé
  qui n'est pas un concept n'a jamais produit de diagnostic, et n'en produit
  toujours pas. La sortie de `okf validate` ne change donc pas pour lui.

**Correspondance exacte, y compris hors du listing.** Les collisions de casse
sont traitées à l'énumération, mais une clé peut aussi être demandée sans y
figurer : une ressource du §6.2, une cible d'écriture, une cible de `verify`.
Sur un volume qui ignore la casse ou la forme Unicode (Windows, macOS), une
ouverture naïve de `References/A.sql` lirait `references/a.sql` : deux clés,
un seul objet, ce qui viole l'injectivité exigée plus haut. Un provider doit
donc garantir que `Read`, `Exists`, `Write` et `Delete` ne touchent un objet que
si son nom correspond **exactement** à la clé, en casse comme en forme Unicode.
Sinon, l'objet est traité comme absent. Le writer applique déjà cette règle
pour `verify` (`ExistsWithExactCase` dans `BundleConceptWriter`) ; elle devient
une exigence du contrat.

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
- Cette garde vit dans le cœur, à chaque point d'entrée d'écriture, et le
  provider la répète en dernier rempart. Les points d'entrée, recensés dans le
  code au 2026-10-08 :
  - `BundleConceptWriter` : écriture et append de concept, `RecordVerifications`.
  - `IndexGenerator` : `index.md`.
  - **`log.md`**, aujourd'hui écrit par `OkfBundleTools.AppendLog` lui-même
    (`File.WriteAllText`), hors du writer. Il **passe dans
    `BundleConceptWriter`**, qui redevient l'unique chemin d'écriture, avec le
    nouvel essai des opérations lire-modifier-écrire (§4).
  - `FileMemoryStore` : écriture d'une entrée, et **suppression d'un scope**,
    aujourd'hui un `Directory.Delete(subDir, recursive: true)`. Le contrat n'a
    pas de suppression de dossier : la suppression devient « énumérer le
    préfixe, puis supprimer objet par objet » (§9).
- **La création des dossiers parents est implicite** : il n'y a pas de dossier
  dans un espace de clés. Un provider qui en a besoin (le filesystem) les crée
  lui-même lors d'un `Write`, et c'est à lui d'y appliquer la garde STRICT des
  ancêtres que `BundleConceptWriter` applique aujourd'hui avant son
  `Directory.CreateDirectory`.
- **Hors couture, explicitement** : `okf parse` et `okf fmt` lisent et écrivent
  **un fichier isolé** désigné en ligne de commande, qui n'appartient à aucun
  bundle (`ReadFileStrict`/`WriteFileStrict` dans `OkfCli`). Ils restent sur
  `System.IO` et ne sont pas concernés par la règle « aucune E/S hors
  couture ».
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
  même dossier → revérification de la version → remplacement, sous le verrou en
  processus. Le remplacement dépend de la condition :
  - `IfMatch` ou `None` sur un objet existant : `File.Replace`, qui exige que la
    destination existe ;
  - `MustNotExist`, ou création : `File.Move(temp, dest, overwrite: false)`,
    qui échoue de façon atomique si la destination est apparue entre-temps.
    Cet échec est rendu comme un conflit.
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
  en relisant l'état courant à chaque fois. Tant que le contrat est synchrone,
  ce délai est un `Thread.Sleep` dans la bibliothèque, sur le thread de
  l'appelant (souvent un thread d'agent). C'est acceptable pour trois essais
  courts ; il devient un `Task.Delay` avec #152.
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

- **Le partage est explicite.** Un constructeur commode qui prend une chaîne
  (`OkfBundleTools(string)`, `OkfBundleKnowledgeSource(string, string)`, …)
  crée **son propre** handle. Deux consommateurs construits ainsi sur la même
  racine ont donc deux images. « Une image par source » ne vaut que si l'hôte
  leur passe le même `BundleHandle`. La documentation le dit.
- `OkfContextProvider` construit sur des `OkfBundleTools` utilise le handle de
  ces outils.

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
| `Manual` (**défaut général**) | l'hôte appelle `Refresh()` |
| `OnAccess` (**défaut des sources de `catalog.json`**) | comparaison du jeton avant chaque utilisation de l'image |
| `Interval(N)` | comparaison au plus toutes les N secondes, à l'accès |
| `Notify` | un signal marque l'image « à vérifier » ; comparaison à l'accès suivant |

- **Pourquoi deux défauts** (arbitrage du 2026-10-08). Aujourd'hui,
  `OkfBundleKnowledgeSource.SearchAsync` recharge le bundle à chaque requête :
  une source de catalogue est toujours fraîche. En `Manual`, elle ne verrait
  plus jamais un changement externe sans action de l'hôte, ce qui serait une
  régression silencieuse. Les sources créées depuis `catalog.json` sont donc en
  `OnAccess` par défaut : elles restent toujours fraîches, et ne rechargent que
  si le jeton a changé, au lieu de recharger à chaque requête. Un hôte peut
  choisir une autre politique par source. Le défaut général reste `Manual`,
  conformément à la décision du 2026-10-05.
- `FileMemoryStore` ne passe pas par un `BundleHandle` : il continue de charger
  le scope demandé à chaque lecture, comme aujourd'hui (§9).
- Toutes les politiques sont **paresseuses** : aucune minuterie ni aucun thread
  d'arrière-plan dans le cœur.
- `Notify` n'est disponible que si le provider implémente
  `IBundleChangeNotifier` ; la demander sinon est une erreur de configuration
  explicite.
- **Une notification est un indice, jamais une vérité** : elle déclenche une
  comparaison de jeton, jamais un rechargement aveugle (`FileSystemWatcher`
  perd des événements).
- Aucun outil de rafraîchissement n'est exposé au modèle.

**5.4 Épinglage du code exécuté.** L'objectif est que le code exécuté soit
exactement celui que le modèle, et l'humain qui relit ses appels, ont pu voir.
L'orchestrateur lit le code sanctionné ainsi, par ordre de préférence :

1. **Un objet chargé dans l'image** (le concept, donc une computation écrite
   dans le frontmatter) est déjà figé.
2. **La version montrée.** Quand `okf_get_computation` affiche un fichier de
   computation ou un attester, il enregistre dans le `BundleHandle` la
   `BundleObjectVersion` du texte affiché (sur le filesystem, son SHA-256).
   `okf_run_computation` relit ce fichier avec `expected` = cette version, et
   refuse si elle a changé. Cette règle est forte sur **tous** les providers, le
   filesystem compris, et ne coûte qu'un hash du texte déjà lu.
3. **À défaut de version montrée** (exécution sans lecture préalable dans la
   même image), la version donnée par le listing de l'image quand le provider la
   connaît (S3). Sinon, le cœur compare la `BundleVersion` avant et après la
   lecture et refuse s'il y a eu changement.

Le cas 3 hérite de la faiblesse du jeton de bundle sur le filesystem (§8) : sous
notre modèle de menace, quiconque écrit dans le bundle peut remplacer un script
par un autre de même taille dans la même seconde. Le cas 2 ferme ce trou chaque
fois que le code a été montré. La liste blanche d'empreintes (#154) reste le
contrôle qui ne dépend pas de qui peut écrire dans la source.

Les versions montrées sont propres à une image : un rafraîchissement les
efface, et un fichier montré dans une image précédente retombe donc dans le
cas 3.

### 6. Bornes par défaut

Finies **pour tous les providers, filesystem compris**.

| Borne | Défaut | Dépassement |
|---|---|---|
| Taille d'un fichier (concept ou ressource) | 16 Mio | concept : entrée dans `Bundle.ParseErrors`, le chargement continue (§11) ; ressource : exception à la lecture |
| Taille de `index.md` / `log.md` | 16 Mio | ces fichiers sont listés mais lus plus tard (validation, outils) : exception à la lecture, que `BundleValidator` et les outils rapportent comme un fichier illisible, ce qu'ils font déjà aujourd'hui pour une erreur d'E/S |
| Entrées par bundle (toutes, §2) | 1 000 000 | chargement interrompu (`BundleLoadException`) |
| Profondeur | 64 | chargement interrompu |
| Longueur de clé | 1 024 octets UTF-8 | entrée écartée ; diagnostic selon le périmètre de l'énumération (§2) |
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
- Il déclare ses contraintes natives d'après l'OS (§2), et il garantit la
  **correspondance exacte** (§2) : sur un volume qui ignore la casse ou la forme
  Unicode, il compare le nom réel de chaque segment, comme
  `ExistsWithExactCase` le fait aujourd'hui, avant de lire, d'écrire ou de
  supprimer.
- Il crée lui-même les dossiers parents lors d'un `Write`, après la garde STRICT
  des ancêtres (§3). Une suppression laisse les dossiers vides en place : un
  dossier vide ne contient aucune clé, donc il n'existe pas pour la couture.
- **Règle d'affichage d'un chemin** (pour la CLI, le Viewer et
  `Diagnostic.Path`) : `Location`, puis les segments de la clé joints par le
  **séparateur de l'OS**, c'est-à-dire exactement ce que `Path.Combine(root, …)`
  produit aujourd'hui. Sous Windows, `okf validate` affiche donc toujours des
  `\`. Les tests golden normalisent les `\` (`GoldenParityTests`) et ne peuvent
  pas garantir cette règle : un test **non normalisé** sous Windows la protège
  (§11).
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
| `ConceptId` | ajoute `ToKey()` et `FromKey(BundleKey)`, que le cœur utilise désormais. `ToPath(string)` et `FromPath(string, string)` restent, comme utilitaires du filesystem : purs calculs de chaînes, rien ne les oblige à disparaître |
| `BundleValidator` | relit `index.md`/`log.md` et teste les ressources via le provider (avec les bornes), plus via `File.*`. `Diagnostic.Path` (aujourd'hui une chaîne remplie avec `concept.Path`) devient `Diagnostic.Key` (`BundleKey?`) ; le chemin affiché suit la règle du §8 |
| `BundleConceptWriter` | constructeur sur `BundleHandle` (ou provider) ; version attendue optionnelle ; garde de capacité ; relectures-réessais ; atomicité via le provider ; **reprend l'écriture de `log.md`** (§3). `BundleRoot` est remplacé par le handle |
| `IndexGenerator` | surcharges sur provider ; garde de capacité ; écritures conditionnelles |
| `OkfBundleTools` | constructeur sur `BundleHandle` ; plus aucune E/S directe. `AppendLog` délègue au writer ; la lecture d'`index.md` passe par le provider en UTF-8 **strict** (aujourd'hui `File.ReadAllText`, qui remplace les octets invalides par U+FFFD) ; filtrage des outils d'écriture selon la permission effective ; écritures conditionnées à l'image ; enregistrement des versions montrées (§5.4) |
| `OkfContextProvider` | partage le `BundleHandle` de ses outils. Ses chargements passent par la couture, mais restent **bloquants** dans ses méthodes asynchrones tant que le contrat est synchrone ; c'est #152 qui les rend asynchrones |
| `OkfBundleKnowledgeSource` | constructeur sur `BundleHandle` ; ne recharge plus à chaque requête, il suit la politique de la source (`OnAccess` par défaut pour une source de `catalog.json`, §5.3) |
| `FileMemoryStore` | API publique inchangée ; reste adossé au filesystem, via le provider ; recharge le scope à chaque lecture, comme aujourd'hui. Voir ci-dessous pour la suppression d'un scope et l'encodage des clés |
| Catalog (`catalog.json`) | `catalog.json` lui-même reste un fichier local lu par `FileKnowledgeCatalog`. `KnowledgeCatalogSource` gagne le type de provider et ses réglages ; son `Path` ne vaut plus que pour le type `filesystem` (voir ci-dessous) |
| `AttestationOrchestrator` | lit le code sanctionné via `ReadResourceText(BundleKey)`, épinglé (§5.4) |
| Viewer (`SiteModel`, `HtmlWriter`) | `SiteModel` lit aujourd'hui `bundle.Root` et le chemin de `ParseErrors` : il passe à `Location` et au chemin affiché du §8, si bien que le HTML produit par `okf-render` ne change pas |
| `okf` / `okf-render` | restent synchrones, sur le provider filesystem ; leurs octets de sortie ne changent pas, sous Linux comme sous Windows (règle d'affichage du §8). `parse` et `fmt` restent hors couture (§3) |
| `okf-mcp` | inchangé (`OKF_BUNDLE_ROOT`, filesystem) |
| `producers/` | une recherche n'y trouve aucun usage des membres qui changent, mais une recherche ne prouve rien : la vérification est la compilation et les tests de `producers/OkfProducer.sln` en local |

**`FileMemoryStore` : suppression d'un scope.** Aujourd'hui `DeleteScopeAsync`
fait un `Directory.Delete(subDir, recursive: true)`, et `Directory.Exists(subDir)`
sert à savoir si un scope existe. Avec la couture :

- la suppression énumère les clés sous le préfixe du scope, puis supprime chaque
  objet, avec un rapport exact en cas d'échec partiel (§4) ;
- un scope « existe » s'il contient au moins une clé, et non plus si son dossier
  existe. Les dossiers vides laissés sur le filesystem (§8) n'ont donc aucun
  effet.

**`FileMemoryStore` : encodage des clés mémoire** (arbitrage du 2026-10-08).
`MemoryPath.Encode` garde aujourd'hui l'identifiant brut en minuscules, suivi
d'un hash de 16 caractères. Deux problèmes :

- un identifiant contenant un caractère refusé par `Strict` (par exemple
  `acme:eu`) produit un segment invalide. Sous Linux, la mémoire existante de ce
  scope deviendrait inaccessible après la migration ;
- un identifiant contenant `/` ajoute des segments. Le hash évite déjà les
  collisions, mais la partie lisible n'est pas injective. Ce problème existe
  déjà aujourd'hui.

La partie lisible est désormais encodée : tout caractère qui n'est pas valide
dans un segment sous `Strict` (`/` compris) est remplacé par `-`. Le hash reste
calculé sur l'identifiant brut, inchangé, et c'est toujours lui qui garantit
l'unicité. Seuls les scopes dont l'identifiant contenait un caractère remplacé
changent de clé. Leurs anciens dossiers, invalides sous `Strict`, sont ignorés.
Une note de migration, dans le CHANGELOG et le README de `OKF4net.Catalog`,
indique comment les renommer. Sous Windows, ces identifiants n'ont jamais
fonctionné, puisque `:` y est interdit dans un nom de dossier : aucune
migration n'y est nécessaire.

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
  `ParseErrors`, `TryResolveResource`, `ReadResourceText`, `Diagnostic.Path`,
  `BundleConceptWriter.BundleRoot`, `KnowledgeCatalogSource` (gagne le type de
  provider et ses réglages), et les constructeurs qui prennent désormais un
  `BundleHandle` (les surcharges `string` restent) (§9). La migration
  asynchrone (#152) sera un second changement cassant, accepté.
- **Comportement, filesystem compris** :
  - bornes finies par défaut (§6), et la borne d'entrées compte **toutes** les
    entrées, `.git` et `node_modules` compris (§2) : un très gros dossier peut
    désormais faire échouer un chargement qui réussissait ;
  - politique de clés `Strict` (§2) : un `.md` au nom invalide devient une entrée
    de `ParseErrors`, et une ressource référencée au nom invalide reçoit le
    statut `Unsafe`. Les autres fichiers n'ont jamais été examinés et ne le sont
    toujours pas ;
  - correspondance exacte en casse et en forme Unicode (§2) : sous Windows ou
    macOS, une ressource référencée avec une autre casse que le fichier réel
    devient `Missing` au lieu d'être résolue ;
  - un `\` dans un chemin §6.2 donne `Unsafe` (§2) ;
  - écritures atomiques et conditionnelles (§4) ;
  - `OkfBundleKnowledgeSource` ne recharge plus à chaque requête, mais reste
    toujours frais grâce à `OnAccess` (§5.3) ;
  - lecture d'`index.md` par les outils agents : UTF-8 strict (§9) ;
  - clés mémoire : encodage de la partie lisible, avec note de migration (§9).
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
  échouer **chaque** point d'entrée d'écriture du §3 avec
  `BundleReadOnlyException`, sans rien modifier sur disque : écriture et append
  de concept, `RecordVerifications`, `index.md`, `log.md` (via `AppendLog`),
  écriture mémoire et suppression d'un scope. Les outils d'écriture ne sont pas
  proposés.
- **Affichage** : un test **non normalisé**, exécuté sous Windows, vérifie que
  `okf validate`, `okf info` et `okf audit` affichent les mêmes chemins (avec des
  `\`) avant et après la migration. Les tests golden, qui normalisent les `\`,
  ne le peuvent pas.
- **Correspondance exacte** : sous Windows et macOS, une ressource référencée
  avec une autre casse, ou une autre forme Unicode, que le fichier réel n'est
  ni lue ni écrite.
- **Mémoire** : un scope dont l'identifiant contient `:` ou `/` est écrit, lu
  et supprimé sous une clé valide en `Strict` ; deux identifiants qui ne
  diffèrent que par un caractère remplacé obtiennent deux clés distinctes ; un
  scope vidé n'« existe » plus.
- **Épinglage** : un fichier de computation modifié entre `okf_get_computation`
  et `okf_run_computation`, y compris par un remplacement de même taille dans la
  même seconde, fait refuser l'exécution.
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

Ajoutés par la révision du 2026-10-08 :

12. **`ConceptId.ToPath`/`FromPath` sont conservés** comme utilitaires du
    filesystem, à côté des nouveaux `ToKey`/`FromKey`, plutôt que supprimés.
13. **`Diagnostic.Path` devient `Diagnostic.Key`**, et le chemin affiché est
    calculé par l'appelant (§8), plutôt que de garder dans la bibliothèque une
    chaîne de chemin déjà composée.
14. **Un `index.md` ou `log.md` trop gros** est traité comme un fichier
    illisible au moment où on le lit, plutôt qu'écarté dès l'énumération.
15. **Un scope mémoire existe s'il contient au moins une clé**, plutôt que si
    son dossier existe.
16. **Le caractère de remplacement de l'encodage mémoire est `-`.**
17. **Les versions montrées (§5.4) sont propres à une image** et sont effacées
    au rafraîchissement, plutôt que conservées d'une image à l'autre.

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
- **Borne d'entrées et gros dossiers** : comme elle compte toutes les entrées,
  un bundle placé à la racine d'un dépôt avec un énorme `node_modules` peut ne
  plus se charger. L'erreur est explicite, et la borne se règle par source.
- **Migration des clés mémoire** : sous Linux, les scopes dont l'identifiant
  contenait un caractère désormais remplacé changent de clé. Sans renommage, leur
  mémoire existante n'est plus lue. La note de migration le dit.
- **Correspondance exacte** : sous Windows et macOS, un bundle qui référençait
  une ressource avec la mauvaise casse et « fonctionnait » verra cette ressource
  passer à `Missing`.

## Travaux futurs

Tickets #140, #141, #142, #143, #151, #152, #153, #154, #178, #179 (voir Périmètre).
