# Couture « provider de bundle » sous `Bundle` — design

Date : 2026-10-07 (révisions 2 à 6, du 2026-10-08 au 2026-10-12)
Statut : proposé
Ticket : [#139](https://github.com/jchable/okf4net/issues/139). Chaque décision
datée citée ici y est consignée en commentaire : du 2026-10-05 au 2026-10-07
pour le cadrage, puis les arbitrages des revues du 2026-10-08, du 2026-10-09 et
du 2026-10-10.

**Révision 4 (seconde passe de revue externe, 2026-10-10).** La seconde passe,
sur la révision 3, confirme que les correctifs précédents tiennent. Elle trouve
deux constats bloquants et six importants dans les raccords, tous vérifiés sur le
code et intégrés.

- **Bloquants.**
  - Une lecture **interne** (`OkfContextProvider` appelle `ReadConcept` avant de
    tronquer ou d'abandonner son rendu) serait devenue une observation. Et
    `okf_get_computation`, qui ne montre pas le corps entier, aurait autorisé une
    réécriture complète. Les observations sont désormais enregistrées à la
    frontière de présentation, et chacune a une **finalité** (§5.4).
  - Revérifier la racine d'une source de catalogue au seul chargement laissait
    les accès différés sans protection : la remontée des ancêtres s'arrête avant
    la racine. La frontière de confinement est maintenant revérifiée avant
    **chaque** accès au stockage (§8).
- **Importants.**
  - Une computation en fichier pouvait s'exécuter avec un contrat modifié depuis
    l'observation (§5.4).
  - La réutilisation d'un handle ignorait `path` (§9).
  - Une écriture pouvait créer un conflit d'arborescence (§2).
  - Le relevé des ressources exécutables au chargement n'avait pas d'états
    d'échec (§5.1).
  - Le séparateur de `relativeDir` changeait sans être annoncé (§9).
  - Le registre ne savait pas enregistrer une absence, et sa borne était fausse
    (§5.4).

Arbitrage du mainteneur (2026-10-10), consigné sur #139 : **D5**, un concept
injecté par `OkfContextProvider` n'est jamais une observation, même s'il est
complet.

**Révision 5 (troisième passe de revue externe, 2026-10-10).** Verdict « prêt
pour un plan après deux précisions », toutes deux vérifiées sur le code et
intégrées (§5.1, §5.4) :

- la révision 4 affirmait qu'un attester en URL est déjà refusé ; c'est faux.
  Sans `attester.resource`, ou avec une URL, l'orchestrateur laisse aujourd'hui
  l'`IAttester` de l'hôte attester avec un texte source nul. Arbitrage **D6** :
  ces deux cas sont désormais **refusés**, changement de comportement annoncé
  (§10) ;
- la reprise après un refus d'épinglage avait disparu entre les révisions 3 et
  4 : le refus **invalide l'image**, et la tentative suivante repart d'un relevé
  à jour.

**Révision 6 (quatrième passe de revue externe, 2026-10-12).** Verdict « prêt
pour un plan après trois corrections ciblées » ; D6 et la reprise sont
vérifiées. Intégré :

- **l'acquittement de l'invalidation** : un refus crée une obligation de
  rechargement, acquittée seulement par un chargement réussi commencé après le
  refus ; un chargement en cours ou un chargement échoué la laissent pendante
  (§5.1, §5.2) ;
- **la priorité des erreurs** : « ne fait jamais échouer le chargement » devient
  une règle locale aux ressources ; un échec global du §6 ou du plafond #151
  interrompt toujours le chargement (§5.1) ;
- **`BundleWriteReport` et les résultats existants** : `VerificationOutcome`
  enrichi, `MemoryDeleteResult` remplacé, choix comparé type par type (§1,
  §10) ;
- les propres écritures et les conflits sont nommés comme les deux autres
  exceptions à la politique de rafraîchissement, et `Undeclared`/`Url` sous
  `Manual` demandent un `Refresh()` de l'hôte (§5.1) ;
- la portée réelle de D6 dans les tests : le helper `InlineComputation` et ses
  seize usages, pas un seul test (§5.1).

**Révision 3 (revue externe, 2026-10-09).** Une revue externe adversariale de la
version 2, qui a exécuté des programmes jetables sur le code actuel, concluait
« à reprendre ». Elle a trouvé deux constats bloquants, huit importants et un
mineur ; tous ont été vérifiés sur le code et sont intégrés.

- **Bloquants.**
  - Un rafraîchissement effaçait la version d'un script montrée au modèle, si
    bien qu'un script remplacé entre-temps pouvait être exécuté.
  - `ParseErrors`, typé par une clé validée, ne pouvait pas contenir un nom
    justement rejeté par la validation.
- **Importants.**
  - `Strict` ne contrôlait pas l'arborescence : collisions de casse au niveau des
    dossiers, conflit entre un fichier et un dossier de même nom, noms réservés
    `COM¹`…`LPT³` manquants.
  - Les dossiers vides, référencés aujourd'hui par l'index, disparaissaient sans
    que la spec le dise.
  - « L'image servie » ne désignait pas la version qu'un agent avait réellement
    lue avant d'écrire.
  - L'encodage mémoire de la v2 reposait sur un constat faux de la revue
    interne : `KnowledgeAccessScope` refuse déjà `:` et `/`. Le seul vrai cas est
    un nom de périphérique.
  - La découverte de bundle d'`okf-mcp` lisait `index.md` sans limite.
  - Les resolvers du catalogue reconstruisent leurs sources à chaque requête, ce
    qui rendait le défaut `OnAccess` de la v2 sans effet.
  - L'image n'est pas réellement immuable.
  - Plusieurs membres publics n'avaient pas de décision.
- **Mineur.** Quatre affirmations factuelles étaient inexactes.

Arbitrages du mainteneur (2026-10-09), consignés sur #139 :

- **D1** : un agent n'agit que sur ce qu'il a vu, d'où un registre
  d'observations par session (§5.4, §4).
- **D2** : la rupture sur les dossiers vides est acceptée (§2, §10).
- **D3** : l'immuabilité devient une garantie affaiblie et documentée ; le vrai
  modèle figé est le ticket #181 (§5.1).
- **D4** : la protection des clés mémoire se limite aux noms de périphériques
  (§9).

**Révision 2 (revue interne, 2026-10-08).** Une revue de la version 1, faite en
vérifiant ses affirmations sur le code, a ajouté plusieurs éléments :

- les chemins d'écriture oubliés (`log.md`, suppression d'un scope mémoire,
  dossiers parents, `parse`/`fmt` hors couture) ;
- la règle d'affichage des chemins sous Windows ;
- les surfaces publiques manquantes ;
- la correspondance exacte en casse et en forme Unicode ;
- le préfixe d'énumération limité à une frontière de segment ;
- `File.Move` pour la création atomique ;
- les arbitrages `OnAccess` pour le catalogue et le périmètre de l'énumération.

## Contexte et motivation

Aujourd'hui un bundle **est** un répertoire local, et cette hypothèse n'est pas
derrière une couture : elle traverse l'API publique et ses consommateurs.

- `Bundle.Load(string root)` (`src/OKF4net/Bundle.cs`) parcourt le répertoire
  avec `Directory`/`File`, et lit l'`index.md` racine dès le chargement pour en
  tirer `okf_version`.
- `Concept.Path`, `Bundle.IndexFiles`, `Bundle.LogFiles`, `Bundle.ParseErrors`,
  `Diagnostic.Path` et `AuditFinding.Path` exposent des chemins du système de
  fichiers.
- `Bundle.TryResolveResource(..., out string? absolutePath, ...)` et
  `Bundle.ReadResourceText(string absolutePath)` remettent un chemin hôte absolu
  à l'appelant ; `AttestationOrchestrator` lit par ce biais le script sanctionné
  et l'attester qu'il fait ensuite exécuter.
- `BundleConceptWriter(string bundleRoot, ...)` écrit avec `File.*`, en place
  (`File.WriteAllText`), sous un registre de verrous **propre au processus**,
  indexé sur la racine canonique, **lié au thread** (`[ThreadStatic]`).
- `IndexGenerator.RegenerateIndexes(string bundleRoot)` parcourt et écrit le
  répertoire ; `BundleValidator.Validate` relit `index.md`/`log.md` et teste
  l'existence de ressources directement sur disque.
- `OkfBundleTools` garde une chaîne `BundleRoot`, met en cache
  `Bundle.Load(BundleRoot)` jusqu'à ses propres écritures, écrit `log.md`
  lui-même, et fait une dizaine d'appels `File.*`/`Directory.*` de son côté.
- Côté catalogue, `GroupedKnowledgeResolver` et `FusedResolverEngine`
  résolvent à nouveau le chemin de chaque source et créent un
  `OkfBundleKnowledgeSource` **à chaque requête**. Celui-ci recharge le bundle à
  chaque recherche. Deux politiques de fraîcheur opposées coexistent donc : le
  cache permanent des outils, le rechargement systématique du catalogue.
- La découverte de bundle d'`okf-mcp` lit `index.md` en entier
  (`OkfBundleDiscovery.ReadRootIndexOrNull`).
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
- la concurrence optimiste, l'atomicité par objet et les observations par
  session ;
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
| Modèle de documents réellement figé | #181 |

Explicitement hors périmètre :

- un provider mémoire distant (`FileMemoryStore` reste adossé au filesystem,
  via le provider) ;
- le choix d'un provider autre que filesystem pour `okf-mcp` et `okf` ;
- les transactions multi-objets ;
- un niveau de confiance par source ;
- un serveur live.

## Design

### 1. Le contrat `IBundleProvider`

Un provider expose un espace de **clés** et des **objets** (des octets) sous ces
clés. Il ne connaît ni concepts, ni liens, ni frontmatter : tout le sens OKF
reste dans le cœur. **Il n'y a pas de dossiers** : un « dossier » est un préfixe
de clés, qui n'existe que s'il contient au moins un objet (§2, D2).

Forme (noms provisoires, synchrone jusqu'à #152) :

```csharp
public interface IBundleProvider
{
    /// Identité stable et non secrète : diagnostics, déduplication des sources
    /// du catalogue, et clé des verrous en processus pour les providers autres
    /// que filesystem (§4).
    string Id { get; }

    /// Emplacement affichable et non secret (filesystem : la racine telle que donnée).
    string Location { get; }

    BundleProviderCapabilities Capabilities { get; }   // CanWrite, au minimum

    /// Contraintes d'alias propres au stockage (§2) — jamais désactivables.
    BundleKeyConstraints NativeConstraints { get; }

    /// Jeton de version du bundle entier, bon marché ; sert au rafraîchissement.
    /// Son parcours interne respecte les bornes reçues (§6).
    BundleVersion GetVersion(BundleLimits limits);

    /// Noms bruts relatifs à la racine, éventuellement sous un préfixe.
    /// Le préfixe s'arrête à une frontière de segment : "a" couvre "a/…",
    /// jamais "ab/…" (même confusion que celle interdite pour la racine, §2).
    /// Le cœur les valide en BundleKey (§2) : le provider ne valide rien lui-même.
    IEnumerable<BundleListing> Enumerate(BundleKey? prefix, BundleLimits limits);

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

- `BundleListing` : le nom brut (`string`), la taille si connue, et la version de
  l'objet si le backend la donne sans surcoût (S3 la donne avec le listing ; le
  filesystem non).
- `BundleObject` : un flux de contenu et sa `BundleObjectVersion`. Le cœur lit ce
  flux **en comptant les octets** (§6).
- `BundleWriteCondition` : `None` (écriture inconditionnelle), `MustNotExist`,
  `IfMatch(version)`.
- Exceptions du contrat : `BundleReadOnlyException` (écriture sur un provider
  sans `CanWrite`), `BundleWriteConflictException` (condition non satisfaite),
  `BundleObjectNotFoundException`.
- `BundleVersion` et `BundleObjectVersion` sont des jetons **opaques**, comparés
  en ordinal ; seul le provider en connaît la construction.
- `BundleLimits` porte les bornes du §6. Le cœur les applique à ce qu'il reçoit
  ; le provider les applique à ses propres parcours (listing interne,
  `GetVersion`), que le cœur ne voit pas.

**Une seule interface, capacité déclarée.** Un provider sait toujours lire ; il
déclare `CanWrite`. L'hôte peut **rétrograder** un provider en lecture seule
(décorateur `provider.AsReadOnly()`), jamais l'inverse.

**Le flux de responsabilités est le suivant :** le provider liste des noms bruts
et lit/écrit des octets sous des clés déjà validées ; le cœur valide les clés et
l'arborescence, applique les bornes, construit l'image, résout les liens et les
chemins §6.2, fait les relectures-réessais, tient les observations et garde la
capacité d'écriture.

**Rapport des opérations multi-objets.** Un type commun, `BundleWriteReport`,
liste les objets écrits (ou supprimés) et les objets en échec avec leur raison.
`RecordVerifications`, la surcharge provider de `RegenerateIndexes` et la
suppression d'un scope mémoire l'exposent, de sorte que le format n'est pas
réinventé par chaque implémentation (§4, §9). Son intégration aux types de
résultat existants n'est pas uniforme, et le choix est fait par type (révision
6) :

- **`VerificationOutcome` est enrichi**, jamais remplacé : il gagne une
  propriété `Report`. `Recorded`/`Records` et `Report` répondent à deux
  questions différentes (« le tampon est-il enregistré » et « l'écriture
  a-t-elle atterri »), `Message` alimente la sortie golden d'`okf verify`, et
  `VerificationRecord.ReplacedAt` n'a pas de place dans un rapport générique.
  Aucun appelant ne change.
- **`MemoryDeleteResult` est remplacé.** Son `TiersDeleted` compte aujourd'hui
  des répertoires supprimés. Après la couture, un tier est un préfixe dont les
  clés sont supprimées une à une : le compteur n'a plus d'unité, et « un tier
  compte seulement si toutes ses clés sont parties » rendrait 0 après avoir
  supprimé quarante objets. Le type devient
  `MemoryDeleteResult(IReadOnlyList<MemoryTierDeletion> Tiers, string? Error)`,
  chaque tier portant son `BundleWriteReport`. C'est un changement cassant de
  `IMemoryStore` (§10).

### 2. Les clés : `BundleKey`, en trois couches

Le cœur possède le type `BundleKey` et le **seul** point de validation. Un
provider ne reçoit jamais qu'une clé déjà validée ; il doit la faire correspondre
**de façon injective** à son stockage, sous une racine ou un préfixe **terminé
par un séparateur** (`bundle/`, jamais `bundle`, pour que `bundle-evil/` ne soit
jamais inclus).

**Nom brut et clé validée sont deux choses distinctes.** Un nom brut (`string`)
sort du listing d'un provider ; une `BundleKey` est ce que la validation en tire.
Tout ce qui doit signaler un nom **rejeté** porte le nom brut : un
`BundleParseError(string RawName, BundleKey? Key, string Error)`, où `Key` est
`null` quand le nom n'a pas passé la validation. À l'affichage, un nom valide
suit la règle du §8, si bien que la sortie ne change pas pour les cas
d'aujourd'hui. Un nom brut rejeté, qui est un cas nouveau, passe par
`DebugQuote.Quote`, qui neutralise les caractères de contrôle.

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
| Filesystem sous Windows | pas de `.` ni d'espace final dans un segment ; pas de `<>:"\|?*` ; pas de nom réservé (`CON`, `PRN`, `AUX`, `NUL`, `COM1`–`COM9`, `COM¹`, `COM²`, `COM³`, `LPT1`–`LPT9`, `LPT¹`, `LPT²`, `LPT³`, avec ou sans extension, quelle que soit la casse) ; collisions de casse **à chaque niveau** de l'arborescence ; conflit fichier/dossier |
| Filesystem sous macOS | collisions de casse et de forme Unicode à chaque niveau ; NFC exigé ; conflit fichier/dossier ; Unicode bien formé |
| Filesystem sous Linux | conflit fichier/dossier ; Unicode bien formé |
| S3 | Unicode bien formé |

> **Note du 2026-10-19 (arbitrage du mainteneur).** Les lignes Windows et macOS
> perdent leurs collisions entre frères, et la ligne macOS perd « NFC exigé » :
> Windows = pas de `.` ni d'espace final, pas de `<>:"\|?*`, pas de nom réservé,
> conflit fichier/dossier ; macOS = conflit fichier/dossier, Unicode bien formé.
> Le provider filesystem compare toujours le nom stocké exactement : aucune clé
> n'ouvre l'objet d'une autre ; sur un volume qui replie la casse ou la forme
> Unicode, deux tels noms ne peuvent pas coexister, et sur un volume qui ne la
> replie pas, ce sont deux fichiers légitimes que `dev` chargeait. La détection
> des collisions entre frères est donc une affaire de portabilité : elle passe au
> profil `Strict` (`Windows | MacOS | Linux | CaseInsensitiveCollisions |
> UnicodeFormCollisions | RequireNfc`), qui n'a rien perdu, et à tout provider qui
> la déclare (`additionalConstraints` compris). Par défaut, de tels frères se
> chargent, chacun avec un avertissement `KeyNotPortable`.

> **Note du 2026-10-19 (second arbitrage du mainteneur).** La ligne Windows perd
> aussi « pas de nom réservé » (`WindowsReservedNames`) : Windows 11 crée et
> ouvre `con.md`, `COM1.md`, `aux.txt.md` ou un dossier `con` par les API
> ordinaires (aucun chemin `\\?\` n'est nécessaire), `dev` les chargeait, et
> seuls des outils comme `git` les refusent. C'est une règle de portabilité : elle
> reste dans `Strict` (liste explicite `Windows | MacOS | Linux |
> WindowsReservedNames | CaseInsensitiveCollisions | UnicodeFormCollisions |
> RequireNfc`, qui n'a rien perdu), et par défaut ces noms se chargent avec un
> avertissement `KeyNotPortable`. Windows natif = pas de `.` ni d'espace final
> (Win32 les retire : le nom ouvre l'objet d'un autre), pas de `<>:"|?*` (`:`
> ouvre aussi un flux NTFS), conflit fichier/dossier.

**Unicode bien formé** (`WellFormedUnicode`) : un segment ne contient aucun
surrogate isolé. Un stockage qui écrit ses noms en UTF-8 ne peut pas le
représenter : l'encodeur remplace chaque surrogate isolé par U+FFFD, si bien que
`\uD800.sql` et `\uD801.sql`, deux clés distinctes, désigneraient le même
`\uFFFD.sql`. C'est une contrainte de stockage, pas une règle du socle : NTFS
stocke ses noms en UTF-16 et tient les deux, d'où son absence de la ligne
Windows.

Le filesystem déduit ses contraintes de l'OS par défaut. **La vraie frontière est
le volume, pas l'OS** (dossier rendu sensible à la casse sous Windows, APFS
sensible à la casse, montage vfat/NTFS/exFAT/CIFS ou dossier ext4 insensible à
la casse sous Linux). Deux garanties distinctes en découlent. La **correspondance
exacte** (ci-dessous) est celle du provider lui-même : il la tient sur tout OS et
tout volume, sans dépendre de la politique ni des contraintes déclarées. La
**portabilité des collisions entre frères** relève de `Strict` : un hôte sous
Linux dont le bundle est sur un volume qui ignore la casse reste en `Strict`, ou
ajoute `CaseInsensitiveCollisions` aux contraintes natives du provider
(`additionalConstraints`), pour que le chargement écarte aussi les frères qui y
entrent en collision.

**Couche 3 — politique de portabilité (choisie par l'hôte, jamais par le
contenu).**

- `Strict` (**défaut**) : l'union de toutes les contraintes natives connues. Un
  bundle valide sur un provider est valide sur tous, **arborescence comprise**.
- `Native` : les contraintes du provider courant seulement.
- Des validateurs supplémentaires peuvent être ajoutés, mais **uniquement pour
  restreindre** ; aucune configuration ne descend sous `Native`.

> **Note du 2026-10-19 (arbitrage du mainteneur).** Le défaut devient `Native`,
> parce que l'OKF §11 n'impose aucune contrainte de nommage ni de taille : aucun
> bundle chargé par `dev` ne doit casser par défaut. `Native` refuse seulement ce
> que le stockage du provider ne peut pas contenir ou confondrait ; `Strict` est
> un profil portable **sur option**. Ce que `Strict` refuserait en plus devient un
> avertissement (`Bundle.PortabilityIssues`, `DiagnosticCode.KeyNotPortable`). Le
> jeu natif macOS perd `RequireNfc` (HFS+ stocke les noms décomposés, APFS garde
> la forme écrite), que `Strict` garde. Le texte ci-dessus est conservé tel qu'il
> a été validé.

**L'arborescence se valide comme un tout, pas clé par clé.** Comparer des clés
complètes ne suffit pas. Exemples :

- `A/x.md` et `a/y.md` sont deux clés distinctes même en ignorant la casse,
  mais leurs dossiers parents fusionnent sous Windows.
- `a.md` et `a.md/b.md` peuvent coexister dans un stockage à clés, pas sur un
  filesystem.

La validation porte donc sur l'ensemble des clés énumérées.

- **Collisions à chaque niveau.** Parmi les enfants d'un même préfixe, deux noms
  égaux une fois la casse repliée (et, sous `Strict`, après normalisation NFC)
  sont en collision. **Tous** les noms en collision sont écartés, avec leurs
  sous-arbres : le cœur ne peut pas savoir lequel est le bon.
- **Conflit fichier/dossier.** Un objet dont la clé est aussi un préfixe d'autres
  clés est en conflit. L'objet et tout ce qui est sous lui sont écartés.
- Le diagnostic suit la règle du périmètre de l'énumération, ci-dessous.

**Les écritures ne doivent pas créer ces conflits.** Valider le listing ne
suffit pas, puisqu'une création peut introduire une collision. Sur un
filesystem sensible à la casse dont l'image contient `A/x.md`, créer `a/y.md`
passe la validation clé par clé et la condition `MustNotExist`, mais le
chargement suivant écarterait les deux sous-arbres. Avant toute **création**
(une clé qui n'existe pas encore), le cœur vérifie donc l'arborescence qui
résulterait de l'écriture, sous la politique active, et sous le verrou en
processus :

- pour chaque niveau de la nouvelle clé, il énumère les enfants du préfixe
  parent **chez le provider**, et non dans l'image, qui peut être périmée. Il
  cherche un nom égal une fois la casse repliée (et normalisé en NFC sous
  `Strict`) ;
- il vérifie qu'aucun préfixe de la nouvelle clé n'est un objet existant, et
  qu'aucun objet n'existe déjà sous la nouvelle clé prise comme préfixe ;
- en cas de conflit, l'écriture est **refusée** avant toute tentative, avec un
  message qui nomme le nom en conflit.

**Ce qui n'est pas garanti**, et que la documentation dit. Cette vérification
et l'écriture ne forment pas une opération atomique sur l'arborescence. La
condition par objet ne porte que sur la clé écrite. Deux écritures concurrentes
sur des clés différentes, venant de deux processus ou d'un processus et d'un
outil externe, peuvent donc encore produire un conflit. Le verrou en processus
écarte ce cas à l'intérieur d'un processus. Le chargement suivant écarte les
noms en conflit avec un diagnostic, comme pour tout conflit venu de
l'extérieur. Aucune transaction multi-objets n'est introduite pour autant.

**Énumération lenient, lecture/écriture stricte**, sur le modèle des deux
prédicats de `ReparsePoints` : un nom brut invalide trouvé en énumérant est
écarté ; une clé invalide demandée en lecture ou en écriture lève une exception.

**Périmètre de l'énumération** (arbitrage du 2026-10-08). Aujourd'hui
`CollectMarkdown` ne retient que les `.md` et ne regarde pas les autres noms.
Le comportement devient :

- **La borne d'entrées (§6) compte toutes les entrées énumérées**, quelle que
  soit leur extension : c'est le coût réel d'un listing. Aucun dossier n'est
  écarté par son nom (`.git` et `node_modules` comptent), comme aujourd'hui où le
  parcours y descend aussi.
- **Les diagnostics de clé ne concernent que deux cas.**
  - Un `.md` écarté (nom invalide, collision, conflit) : une entrée dans
    `Bundle.ParseErrors`, comme aujourd'hui un `.md` dont le nom n'est pas un
    identifiant de concept valide (`ConceptIdException` → `ParseErrors`).
  - Une ressource référencée par un champ §6.2 dont la clé est invalide ou
    écartée : le statut `Unsafe`.
- **Tout autre nom écarté l'est sans bruit.** Un fichier non référencé qui n'est
  pas un concept n'a jamais produit de diagnostic, et n'en produit toujours pas.
- **Dossiers vides** (D2). Un dossier qui ne contient aucun fichier, à quelque
  profondeur que ce soit, n'existe pas dans l'espace de clés. L'index parent ne
  le référence donc plus. C'est une rupture assumée (§10). git ne sait pas
  stocker un dossier vide, si bien qu'aucune fixture n'en contient et que les
  goldens ne bougent pas.

**Correspondance exacte, y compris hors du listing.** Les collisions sont
traitées à l'énumération, mais une clé peut aussi être demandée sans y figurer :
une ressource du §6.2, une cible d'écriture, une cible de `verify`. Sur un volume
qui ignore la casse ou la forme Unicode (Windows, macOS), une ouverture naïve de
`References/A.sql` lirait `references/a.sql` : deux clés, un seul objet, ce qui
viole l'injectivité exigée plus haut. Un provider doit donc garantir que `Read`,
`Exists`, `Write` et `Delete` ne touchent un objet que si son nom correspond
**exactement** à la clé, segment par segment, en casse comme en forme Unicode.
Sinon, l'objet est traité comme absent. Le writer applique déjà cette règle pour
`verify` (`ExistsWithExactCase` dans `BundleConceptWriter`) ; elle devient une
exigence du contrat.

**Résolution des champs de chemin du §6.2, dans le cœur, lexicalement sur les
clés** (elle ne dépend plus d'aucune sémantique de chemin de l'OS) :

| Valeur brute | Résultat |
|---|---|
| URL `scheme://…` | statut `Url`, **jamais déréférencée** |
| `/x` | depuis la racine |
| `./x`, `../x` | depuis le dossier du concept |
| `x` (chemin nu) | depuis la racine — lecture locale du §6.2, consignée dans `docs/spec-conformance/` (S6.2-1) |
| remonte au-dessus de la racine | `Unsafe` |
| désigne la racine elle-même (`/`, `.` depuis un concept de premier niveau, `sub/..`) | `Missing`, sans clé (comme `File.Exists` sur un dossier avant la couture) |
| finit par `/` (`references/a.sql/`) | un préfixe, jamais un objet : la clé passe la politique et les gardes du provider (`Unsafe` si refusée), puis `Missing` |
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
- **La création des dossiers parents est implicite**, puisqu'il n'y a pas de
  dossier dans un espace de clés. Un provider qui en a besoin (le filesystem)
  les crée lui-même lors d'un `Write`. C'est alors à lui d'y appliquer la garde
  STRICT des ancêtres que `BundleConceptWriter` applique aujourd'hui avant son
  `Directory.CreateDirectory`.
- **Hors couture, explicitement** : `okf parse` et `okf fmt` lisent et écrivent
  **un fichier isolé** désigné en ligne de commande, qui n'appartient à aucun
  bundle (`ReadFileStrict`/`WriteFileStrict` dans `OkfCli`). Ils restent sur
  `System.IO` et ne sont pas concernés par la règle « aucune E/S hors couture ».
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

  > **Note du 2026-10-19 (lot B, plan d'implémentation, B17 à B22) :** chaque
  > primitive a été mesurée avant d'être employée (sondes du plan). Sous Unix,
  > `File.Move` sans écrasement est un `lstat` puis un `rename` : un autre
  > **processus** qui crée le nom entre les deux est remplacé ; dans un processus,
  > le verrou de commit ferme la fenêtre (B20, #178). Sous Windows,
  > `File.Replace` est atomique pour l'état stocké, pas pour les lecteurs :
  > pendant le remplacement, un lecteur concurrent peut ne trouver aucun objet
  > sous le nom ou rencontrer une violation de partage, jamais un objet tronqué
  > (B17). Le temporaire d'un remplacement est créé en 0600 et prend le mode de
  > la cible par son handle ; celui d'une création garde le mode par défaut,
  > faute d'API pour lire l'umask (B18). Un nom que le volume confondrait avec
  > une entrée stockée est refusé d'après le volume lui-même, pas d'après un
  > repli de casse (B19) ; une cible que le processus ne peut pas écrire est
  > refusée comme avant (B22) ; un échec du commit est classé d'après le disque,
  > et une issue inconnue garde le temporaire (B21).

  > **Note du 2026-10-20 (lot B, revues externes exécutées, B42 à B48) :**
  > `ReplaceFile` peut aussi échouer **sans effet** (erreur 1175, mesurée deux
  > fois en 1 500 commits, lecteurs ouverts) : le commit est refait, au plus
  > trois fois en tout, depuis la re-vérification de la condition sur les octets stockés,
  > puis échoue avec `BundleWriteNotCommittedException`, la cible intacte (B43).
  > Une lecture réessaie la fenêtre de `ReplaceFile` (violation de partage, ou
  > entrée listée un instant plus tôt puis introuvable), sous Windows seulement,
  > ce qui la rétrécit sans la fermer (B45). Le classement d'un commit échoué
  > renouvelle la frontière et les gardes STRICT avant toute lecture (B44). Une
  > écriture inconditionnelle n'exige que le droit d'écrire la cible, comme
  > `File.WriteAllText` ; seul `IfMatch` la lit (B42). Une notification
  > `onWriteCommitted` qui échoue après le commit ne fait pas passer l'écriture
  > pour un échec (B46) : elle s'exécute une seule fois, l'écriture devenue
  > définitive, hors de toute nouvelle tentative, et rien de ce qu'elle lève
  > n'est pris pour un refus de l'écriture (B48). La politique de clés couvre
  > aussi les clés `index.md` que produit `IndexGenerator` (B47).

  > **Note du 2026-10-22 (lot B, revue complète, vague de correctifs) :**
  > corrige ce que les notes précédentes affirmaient. (1) Une écriture n'exige
  > pas seulement le droit d'écrire la cible (B42, note du 2026-10-20) : son
  > temporaire `.okf-write-*.tmp` est créé à côté de la cible et le commit est
  > un renommage, il faut donc le droit de créer des fichiers dans le dossier de
  > la cible et d'y renommer ; `File.WriteAllText` n'exigeait que le droit
  > d'écrire la cible. Mesuré (Linux, utilisateur non privilégié, base contre
  > tête) : `okf verify B t/orders` avec `t` en 0555 et un fichier inscriptible
  > sortait 0, sort maintenant 1 avec `error: Access to the path
  > '…/t/.okf-write-<hex>.tmp' is denied.`, fichier inchangé ; même chose pour
  > `okf index`, `WriteConcept`, `AppendToConceptAtomic` et `AppendLog` ; sous
  > Windows, un dossier dont l'ACL refuse à l'utilisateur la création de
  > fichiers refuse de même ; sous Unix, dans un dossier collant partagé (comme
  > `/tmp`), un fichier appartenant à un autre utilisateur ne peut pas non plus
  > être remplacé. Décision du mainteneur : pas de repli en place, qui
  > rouvrirait les écritures tronquées de #157 ; un bundle dans un dossier non
  > inscriptible ou collant partagé n'est pas un déploiement supporté pour
  > l'écriture. (2) La condition d'une écriture n'est vérifiée atomiquement avec
  > le remplacement qu'**à l'intérieur d'un processus** : le verrou de commit est
  > un verrou en processus (B20 ne fermait la fenêtre que pour un processus), et
  > un autre processus peut écrire le même objet entre la dernière vérification
  > et le commit (#178). Mesuré par la revue, avec deux processus faisant chacun
  > 150 `AppendLog` / `AppendToConceptAtomic` sur un bundle : sous Linux jusqu'à
  > 100 ajouts sur 297 déclarés validés étaient perdus ; sous Windows 17 entrées
  > déclarées validées étaient perdues, avec des erreurs `ReplaceFile`
  > 1176/1177 (issue inconnue), un log absent un instant et des temporaires
  > `.okf-write-*.tmp` laissés par une simple contention, pas seulement par un
  > crash (l'ancien écrivain en place perdait plus d'entrées dans le même test
  > mais ne laissait jamais de temporaire). Décision du mainteneur : pas de
  > fichier verrou dans cette branche ; #178 reste le suivi, sans promesse.
  > (3) Une écriture `IfMatch` dont la racine ou un dossier parent n'existe pas
  > ne peut pas tenir : elle est refusée en conflit avant de créer quoi que ce
  > soit ; un objet qui disparaît entre sa résolution et le commit est, sous
  > `IfMatch`, un conflit, et sous `None` une nouvelle préparation (bornée par
  > les trois préparations) ou une création, jamais un « introuvable » brut ;
  > un commit échoué dont le temporaire est encore en place, avec un répertoire
  > ou une entrée dont on ne peut montrer qu'elle est la version lue (illisible,
  > par exemple) sous la clé, est un conflit et son temporaire est retiré, pour
  > une écriture conditionnelle ou une création (un remplacement inconditionnel
  > garde l'erreur brute) ; `Delete` sous une racine absente
  > est sans effet quand la condition tient ; les outils de `OkfBundleTools`
  > oublient leur bundle en cache dès qu'une écriture se termine en `Error:`,
  > car la notification de commit ne s'exécute pas pour une issue inconnue (B46,
  > B48).

- **Version d'objet sur le filesystem : SHA-256 du contenu**, pas la date de
  modification, dont la résolution est d'1 à 2 s sur certains systèmes de
  fichiers.
- Conséquence : le remplacement atomique est une **exigence du contrat**, et
  l'item du ROADMAP « Atomic write-then-rename in `BundleConceptWriter` » est
  absorbé par #140, avec ses tests (sémantique de `File.Replace`, garde de
  chemin sur le nom temporaire, verrou).
- Le verrou en processus reste, comme optimisation contre les conflits inutiles
  et comme garantie à l'intérieur d'un processus. Sur le filesystem, sa logique
  actuelle (registre lexical + résolu) est conservée telle quelle ; ailleurs, il
  est indexé par `IBundleProvider.Id`.

**Fenêtre inter-processus sur le filesystem.** Entre la revérification et
`File.Replace`, un autre processus peut écrire. Cette fenêtre est **acceptée et
documentée** ; le verrou OS qui la ferme est #178. Elle est distincte du
problème des observations, ci-dessous, que ce design règle.

**Réécriture complète d'un concept.**

- API : `WriteConcept` accepte une version attendue **optionnelle** ; sans elle,
  l'écriture est inconditionnelle, comme aujourd'hui.

  > **Note du 2026-10-19 (lot B, B29) :** la version attendue est une
  > `BundleWriteCondition` (`IfMatch(v)`, `MustNotExist` ou `None`), pas une
  > `BundleObjectVersion?` : l'observation « absent » (`MustNotExist`) doit
  > pouvoir atteindre le writer. Les surcharges à trois arguments restent
  > inconditionnelles.

- Outils agents (D1) : l'écriture est conditionnée à ce que **cette session** a
  observé, d'après son registre (§5.4), et non à l'image courante du handle
  partagé, qu'un autre appel a pu rafraîchir entre-temps. Seule une observation
  de finalité **concept complet** (`okf_read_concept`) autorise une réécriture.
  Une observation **code** (`okf_get_computation`) ne l'autorise pas, puisque cet
  outil ne montre pas le corps entier.
  - Observation « concept complet », version v : condition `IfMatch(v)`.
  - Observation « absent », c'est-à-dire que `okf_read_concept` a répondu
    « introuvable » à cette session : création, condition `MustNotExist`.
  - Pas d'observation utilisable, parce que le concept n'a jamais été lu
    entièrement, que seul son code a été montré, ou que l'entrée a été évincée :
    **refus** avec un message de la bibliothèque (« lisez-le d'abord avec
    `okf_read_concept` »), que le concept existe ou non.
  - En cas de conflit, le modèle reçoit un message rédigé par la bibliothèque
    (le concept a changé depuis votre lecture, relisez-le), et l'image est
    rafraîchie. Le schéma des outils ne change pas.
  - Une suppression observée se gère sans effacer de protection : la session lit
    `a` en v1 ; `a` est supprimé ailleurs ; sa réécriture `IfMatch(v1)` échoue en
    conflit ; elle relit, obtient « introuvable », ce qui remplace son
    observation par « absent » ; elle peut alors recréer `a` en `MustNotExist`.

**Opérations sur plusieurs objets** (`RecordVerifications`,
`RegenerateIndexes`, `FileMemoryStore.DeleteScopeAsync`) : **pas de
transaction**. Chaque objet est écrit atomiquement ; l'opération rend un
`BundleWriteReport` (§1) qui dit exactement ce qui a été écrit et ce qui a
échoué, avec la raison. Elle est relançable sans risque : pas de tampon de
vérification dupliqué, index recalculés en entier.

**Nouveaux essais.**

- Les opérations lire-modifier-écrire (append, verify, log, régénération
  d'index) réessaient sur conflit : **3 tentatives, délai aléatoire croissant**,
  en relisant l'état courant à chaque fois. Tant que le contrat est synchrone,
  ce délai est un `Thread.Sleep` dans la bibliothèque, sur le thread de
  l'appelant (souvent un thread d'agent). C'est acceptable pour trois essais
  courts ; il devient un `Task.Delay` avec #152.
- **Conséquence pour `AppendToConceptAtomic`** : son callback `buildBody`,
  documenté aujourd'hui comme « invoked exactly once », est désormais appelé
  **une fois par tentative** (au plus 3), toujours sur le corps relu. Il doit
  donc être une fonction pure du corps reçu. Son seul appelant du dépôt, la
  capture de mémoire d'`OkfContextProvider`, l'est déjà. Le contrat documenté
  change, et c'est consigné au §10.
- Les écritures conditionnées à une observation **ne réessaient jamais
  automatiquement** : réessayer écraserait ce que l'agent n'a pas vu.
- Après épuisement des essais, `BundleConceptWriter` rend le conflit comme ses
  autres erreurs attendues (`Error: …`), fidèle à sa convention.

### 5. Image, version, observations et rafraîchissement

**5.1 L'image.** Un `Bundle` reste une image **complète** du bundle, parce que
la validation, le graphe, les backlinks et `ConceptSearch` ont besoin du corpus
entier. Elle porte :

- la `BundleVersion` du provider au moment du chargement ;
- la `BundleObjectVersion` de chaque objet chargé ;
- l'état de chaque **ressource exécutable référencée par un concept §10**
  (fichier de computation, attester), relevé au chargement (§5.4).

**Un problème propre à une ressource ne fait pas échouer le chargement.** Le
chargement reste permissif (§11) : un état négatif d'une ressource ne retire pas
le concept de l'image, et n'empêche ni la lecture, ni la validation, ni l'audit.
La règle est **locale** : elle ne couvre pas les échecs globaux du §6 (délai
réseau, borne d'entrées, profondeur) ni le plafond mémoire de #151, qui
interrompent le chargement entier, relevé compris, comme ils le feraient sans
lui. Un délai dépassé pendant le relevé d'un attester est donc un échec de
chargement, pas un état `Unreadable` ; l'image précédente reste servie, marquée
périmée (§5.2). Chaque ressource reçoit l'un de ces états :

| État | Cas | Conséquence |
|---|---|---|
| `Pinned(version)` | lue et hachée | exécutable |
| `Undeclared` | le concept ne déclare pas d'`attester.resource` | exécution refusée (**changement de comportement**, D6, voir ci-dessous) |
| `Url` | valeur `scheme://…` | jamais lue ; exécution refusée (**changement de comportement**, D6) |
| `Unsafe` | résolution §6.2 refusée (§2) | exécution refusée |
| `Missing` | aucun objet sous la clé | exécution refusée |
| `Unreadable` | erreur de lecture, UTF-8 invalide | exécution refusée |
| `TooLarge` | au-delà de 16 Mio (§6) | exécution refusée, sans lire au-delà de la borne |

Une exécution refusée pour l'une de ces raisons reçoit un message de la
bibliothèque qui nomme la ressource et l'état. La validation continue de
signaler les ressources manquantes comme aujourd'hui (`BundleValidator`).

**Fin de l'attestation fournie par l'hôte** (D6, arbitrage du 2026-10-10).
Aujourd'hui, `AttestationOrchestrator.TryResolveAttesterSource` ne lit rien
quand le concept ne déclare pas d'`attester.resource`, ou en déclare une sous
forme d'URL ; il laisse alors l'`IAttester` enregistré par l'hôte attester avec
`AttesterSourceText = null`. Ces deux cas sont désormais **refusés avant tout
appel au runtime** : il n'y a rien à épingler, donc rien que le modèle ou
l'hôte puissent tenir pour le code d'attestation effectivement exécuté. C'est un
changement de sens du §10.5 pour cette implémentation, à consigner au §10 et
dans `docs/spec-conformance/`. Son périmètre réel est étroit : les six
computations des bundles du dépôt déclarent toutes un attester en fichier, et
`ContainerAttester` refuse déjà un `AttesterSourceText` nul. Côté tests, le
changement de sens touche plus que le seul
`Absent_attester_declaration_yields_null_source_text_not_a_failure` : le
helper `InlineComputation` d'`AttestationOrchestratorTests` construit une
computation **sans** attester et sert seize tests, dont `Happy_path_is_displayable`.
Il doit recevoir un attester (sur le modèle d'`InlineComputationWithAttesterFile`)
pour que ces tests continuent de tester leur objet initial au lieu de rougir
sur D6.

**Reprise après un refus d'épinglage.** Un refus pour version changée (`Pinned`
dont la relecture avec `expected` échoue) ou pour un état négatif (`Missing`,
`Unreadable`, `TooLarge`, `Unsafe`) crée une **obligation de rechargement** sur
le handle. Elle n'est **acquittée que par un chargement réussi commencé après
le refus** : un chargement déjà en cours au moment du refus, même s'il termine
ensuite, ne l'acquitte pas, puisqu'il a pu relever l'ancien état ; un chargement
qui échoue (réseau, plafond #151) la laisse pendante, et l'image précédente
reste servie, périmée. Un compteur de génération suffit ; sa forme relève du
plan. La garantie est donc : **la tentative suivante provoque un rechargement,
et utilise le nouveau relevé si ce rechargement réussit**. Elle vaut sous les
quatre politiques, l'obligation primant sur l'intervalle de `Interval` et sur
l'absence de signal de `Notify`. Aucune réexécution automatique, aucune
modification des observations de la session : si la computation ou le concept
ont changé aussi, la relecture par le modèle reste nécessaire. Une ressource
créée ou corrigée après le chargement est ainsi reprise, sans intervention de
l'hôte. `Undeclared` et `Url` ne créent pas d'obligation : seul un changement du
concept peut les faire évoluer, et sous `Manual` une modification externe de ce
concept demande toujours un `Refresh()` de l'hôte — les observations ne
déclenchent pas de rafraîchissement.

Ce n'est pas la seule situation où le cœur recharge sans que la politique le
demande : les propres écritures de la session et un conflit d'écriture
(§4, §5.2) invalident l'image de la même manière. Ces trois cas sont les seules
exceptions à la politique, et le message de refus le dit.

`OkfDocument.Body` reste une `string` synchrone (le passage à un accès
asynchrone éventuel relève de #153).

**Immuabilité : garantie affaiblie et documentée** (D3). L'image est remplacée
d'un bloc au rafraîchissement, mais son **contenu n'est pas figé** :
`Frontmatter.Set` modifie en place, et `Frontmatter.AsMapping()` renvoie le
dictionnaire interne. L'image étant **partagée** entre consommateurs, la
modifier n'est **pas supporté**, et la documentation le dit. #139 inclut un audit
des endroits du dépôt qui modifient un document chargé ; le writer travaille déjà
sur une copie superficielle (`WriteConcept(string, Frontmatter, string)`). Le
vrai modèle figé, qui est un changement cassant de `Frontmatter`/`YamlMapping`,
est #181.

**5.2 Le détenteur d'image : `BundleHandle`.** Un objet par source réunit
provider, options (politique de rafraîchissement, bornes, portabilité) et image
courante.

- Il est **partagé** par `OkfBundleTools`, `OkfContextProvider` et les sources
  du catalogue. Pour le catalogue, c'est le catalogue lui-même qui crée et
  conserve les handles (§9), si bien qu'un handle survit d'une requête à
  l'autre.
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
  précédente continue d'être servie, marquée périmée. Une obligation de
  rechargement née d'un refus d'épinglage (§5.1) reste alors pendante.
- **Après ses propres écritures** : invalidation, puis rechargement à la
  prochaine utilisation (comportement actuel). La mise à jour incrémentale est
  #179.

**5.3 Les politiques de rafraîchissement.** Le provider fournit `GetVersion()`
(obligatoire) et, en option, `IBundleChangeNotifier` (un flux de signaux de
changement). Le cœur implémente une fois pour tous les providers :

| Politique | Comportement |
|---|---|
| `Manual` (**défaut général**) | l'hôte appelle `Refresh()` |
| `OnAccess` (**défaut des sources de `catalog.json`**) | comparaison du jeton avant chaque utilisation de l'image |
| `Interval(N)` | comparaison au plus toutes les N secondes, à l'accès |
| `Notify` | un signal marque l'image « à vérifier » ; comparaison à l'accès suivant |

- **Pourquoi deux défauts** (arbitrage du 2026-10-08). Aujourd'hui, une source
  de catalogue est rechargée à chaque requête : elle est toujours fraîche. En
  `Manual`, elle ne verrait plus jamais un changement externe sans action de
  l'hôte, ce qui serait une régression silencieuse. Les sources de
  `catalog.json` sont donc en `OnAccess` par défaut. Elles restent toujours
  fraîches, mais ne rechargent que si le jeton a changé. Ce défaut n'a d'effet
  que parce que les handles du catalogue survivent d'une requête à l'autre
  (§9). Un hôte peut choisir une autre politique par source. Le défaut général
  reste `Manual`, conformément à la décision du 2026-10-05.
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

**5.4 Observations et épinglage du code exécuté** (D1 : « n'agir que sur ce
qu'on a vu » ; D5).

**Le registre d'observations.** Chaque instance d'`OkfBundleTools` (une session
d'agent, une connexion MCP) tient un registre. Pour une clé, il garde **ce qui a
été présenté au modèle et à quelle fin**.

| Finalité | Enregistrée par | Autorise |
|---|---|---|
| **concept complet**, version v | l'outil `okf_read_concept`, quand il rend le concept entier | la réécriture du concept (§4) ; l'exécution de sa computation en ligne, dont le code et le contrat sont dans ce qu'il rend |
| **code**, version v | l'outil `okf_get_computation`, pour le concept (contrat + code) et, s'il y en a un, pour le fichier de computation | l'exécution seulement, jamais une réécriture |
| **absent** | l'outil `okf_read_concept`, quand il répond « introuvable » | la création du concept en `MustNotExist` (§4) |

Une clé peut porter à la fois une observation « concept complet » et une
observation « code ». Une nouvelle observation de même finalité, par la même
session, remplace la précédente. Les règles suivantes fixent ce qui compte comme
observation.

- **À la frontière de présentation, jamais à l'appel d'une méthode.**
  L'enregistrement est fait par la fonction exposée au modèle, après qu'elle a
  produit le résultat complet. Il n'est **pas** fait par la méthode publique
  `ReadConcept` ou `GetComputation` qu'un appelant interne utilise. Une méthode
  publique appelée directement par un hôte n'enregistre donc rien : un hôte qui
  présente lui-même le résultat au modèle n'obtient pas de droit d'agir. C'est le
  défaut sûr.
- **Une injection de contexte n'est jamais une observation** (D5).
  `OkfContextProvider` appelle `ReadConcept`, puis tronque ou abandonne le bloc
  selon son budget (`RenderBlock` peut renvoyer `null`). Même un bloc injecté en
  entier ne compte pas : l'injection est automatique, le modèle ne l'a pas
  demandée.
- **Une recherche ou une navigation n'en sont pas** non plus : elles ne montrent
  que des extraits.

La durée de vie et la portée du registre sont les suivantes.

- Il **survit aux rafraîchissements** de l'image.
- Il n'est **pas partagé** : ni par le `BundleHandle`, ni avec un autre jeu
  d'outils, ni avec `OkfContextProvider`, qui n'en a pas besoin puisqu'il
  n'enregistre rien. Une lecture faite par un autre appelant ne peut donc pas
  remplacer la version vue par celui-ci.
- **Borne propre** : 10 000 clés, en évinçant les moins récemment utilisées. Le
  nombre de clés du bundle ne le borne pas, puisque les clés observées peuvent
  disparaître et d'autres apparaître. Une entrée évincée est perdue : l'action
  qu'elle aurait autorisée est **refusée** jusqu'à une nouvelle lecture, jamais
  autorisée par défaut.
- La garantie vaut par instance d'`OkfBundleTools`. Un hôte qui sert plusieurs
  conversations avec une seule instance partage le registre entre elles ; la
  documentation recommande une instance par conversation.

**Ce que `okf_run_computation` exécute.** Au début de l'appel, l'outil capture
**en une seule fois** tout ce que l'exécution utilisera, et l'exécution ne relit
plus rien hors de cette capture.

1. **Le concept : contrat et runtime compris, dans les deux modes.** Il faut une
   observation « concept complet » ou « code » de version v, et la version du
   concept dans l'image courante doit être égale à v. Sinon, l'exécution est
   refusée (« elle a changé depuis votre lecture »). L'orchestrateur prend le
   contrat et le runtime dans le concept (`AttestationOrchestrator.RunAsync`).
   Un frontmatter modifié avec un fichier de code inchangé est donc refusé lui
   aussi.
2. **Le code.**
   - En ligne : c'est le bloc de code du **corps** du concept, sous
     `# Computation` (§10.3). Il est couvert par le point 1.
   - En fichier : il faut en plus une observation « code » de ce fichier. Il est
     relu avec `expected` = la version observée.
3. **L'attester.** Son code n'est jamais montré au modèle. Il est épinglé à
   **l'image dans laquelle l'exécution a lieu** : son état doit être
   `Pinned(version)` (§5.1), et il est relu avec `expected` = cette version.
   Tout autre état, `Undeclared` et `Url` compris (D6), ou une version qui a
   changé depuis le chargement, donne un refus.

Le cœur passe ensuite à l'orchestrateur le concept et les textes capturés ;
`AttesterSourceText` n'est donc plus jamais nul à l'entrée d'un runtime. Sans
observation, quand une version a changé ou quand un état n'est pas `Pinned`,
l'exécution est **refusée** avec un message de la bibliothèque (« lisez-la
d'abord avec `okf_get_computation` », « elle a changé depuis votre lecture », ou
la ressource et son état). Un rafraîchissement intermédiaire n'y change rien,
puisque le registre lui survit. Un refus pour version changée ou pour un état
négatif invalide l'image, comme le §5.1 le décrit, pour que la tentative
suivante parte d'un relevé à jour.

La liste blanche d'empreintes (#154) reste le contrôle qui ne dépend pas de qui
peut écrire dans la source.

### 6. Bornes par défaut

Finies **pour tous les providers, filesystem compris**.

> **Note du 2026-10-19 (arbitrage du mainteneur).** Défaut `Native`, parce que
> l'OKF §11 n'impose aucune contrainte de nommage ni de taille ; `Strict` est un
> profil sur option. Les bornes sont relevées à 256 Mio par objet, 10 000 000
> entrées et une profondeur de 1 024 : finies, pour qu'un provider hostile ou
> emballé ne puisse pas épuiser la mémoire, mais hors de portée d'un bundle réel.
> La profondeur, fixée d'abord à 256, compte chaque nom listé (`node_modules`,
> `.git` et fichiers non-`.md` compris) : un arbre valide de 257 niveaux, ou un
> `node_modules` de 300 sans aucun `.md`, que `dev` chargeait, faisait échouer
> le chargement ; 1 024 est hors de portée, une clé `.md` ne pouvant dépasser
> 512 segments (1 024 octets).
> La longueur de clé reste à 1 024 octets (règle de base de la couche 1). Un hôte
> qui lit un stockage non fiable les abaisse. Le tableau ci-dessous garde les
> valeurs validées à l'origine.

| Borne | Défaut | Dépassement |
|---|---|---|
| Taille d'un concept | 16 Mio | entrée dans `Bundle.ParseErrors`, le chargement continue (**comportement nouveau** : aujourd'hui un concept illisible fait échouer tout le chargement en `BundleLoadException`, ce qui reste le cas pour un octet UTF-8 invalide) |
| Taille d'une ressource | 16 Mio | exception à la lecture |
| Taille de `index.md` / `log.md` | 16 Mio | l'`index.md` racine, lu **pendant le chargement** pour `okf_version`, est traité comme illisible : pas de version déclarée, comme aujourd'hui pour une erreur de lecture. Les autres, lus plus tard par la validation et les outils, lèvent une exception à la lecture, que `BundleValidator` et les outils rapportent déjà comme un fichier illisible |
| Entrées par bundle (toutes, §2) | 1 000 000 | chargement interrompu (`BundleLoadException`) |
| Profondeur | 64 | chargement interrompu |
| Longueur de clé | 1 024 octets UTF-8 | entrée écartée ; diagnostic selon le périmètre de l'énumération (§2) |
| Délai par requête (providers réseau) | 30 s | échec de la requête et du chargement |
| Requêtes simultanées (providers réseau) | 16 | file d'attente |

- Les tailles, le nombre d'entrées, la profondeur et la longueur de clé sont
  appliqués **dans le cœur** : il lit les flux en comptant les octets et compte
  les entrées et les segments. Un provider défaillant ne peut donc pas les
  contourner.
- Le provider applique en plus les mêmes bornes (entrées et profondeur) à ses
  **parcours internes**, que le cœur ne voit pas, `GetVersion()` compris : c'est
  pourquoi `BundleLimits` lui est passé (§1).
- Le délai et le parallélisme sont de la responsabilité du provider.
- Le plafond mémoire de #151 s'y ajoute, sans défaut.

Une exception de dépassement de taille dérive d'`IOException`, pour que les
gestionnaires existants, qui traitent `IOException` comme « illisible », la
classent correctement sans modification.

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
  distante, c'est faire confiance à tous ceux qui peuvent y écrire. Observations
  et épinglage : §5.4. Liste blanche d'empreintes optionnelle : #154.
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
  **correspondance exacte** (§2) sur tout OS : il compare le nom réel de chaque
  segment, comme `ExistsWithExactCase` le fait aujourd'hui, avant de lire,
  d'écrire ou de supprimer. Il le fait même là où l'OS ne replie pas la casse,
  car c'est le volume qui en décide (un montage vfat sous Linux ignore la casse) ;
  un dossier dont le listing est refusé ou échoue est donc « aucune entrée »
  partout. Cette garantie ne dépend ni de la politique — `Strict` gouverne la
  portabilité des collisions entre frères — ni des contraintes ajoutées.
- Il crée lui-même les dossiers parents lors d'un `Write`, après la garde STRICT
  des ancêtres (§3). Une suppression laisse les dossiers vides en place : un
  dossier vide ne contient aucune clé, donc il n'existe pas pour la couture.
- **Frontière de confinement, revérifiée avant chaque accès.**
  - **Le problème.** Aujourd'hui, la résolution du chemin à chaque requête
    revérifie que la racine d'une source de catalogue est toujours sous la racine
    du catalogue et qu'elle n'est pas un point de reparse. Les gardes de lecture,
    elles, remontent les ancêtres **jusqu'à la racine du bundle, sans
    l'inclure** (`ReparsePoints.AnyEntryUpTo`). Une racine remplacée par une
    jonction après le chargement passerait donc inaperçue lors d'une lecture
    différée. La revue externe l'a reproduit sur les primitives actuelles : une
    ressource lue depuis l'image conservée sortait du catalogue.
  - **La règle.** Un provider filesystem peut recevoir une **frontière de
    confinement**. Le catalogue lui passe sa propre racine ; c'est le cas de
    toute source de catalogue. S'il en a une, il revérifie en mode **STRICT**,
    **avant chaque accès effectif au stockage**, la chaîne qui va de la frontière
    jusqu'à sa racine **incluse** : pas de point de reparse ni d'entrée
    impossible à inspecter, et la racine toujours sous la frontière. Cela vaut
    pour `GetVersion`, `Enumerate`, chaque `Read` (ressources différées
    comprises), `Exists`, `Write` et `Delete`, et pas seulement au chargement.
    La garde existante, des ancêtres jusqu'à l'objet, continue de s'appliquer
    en plus.
  - **Sans frontière**, c'est-à-dire avec une racine donnée directement par
    l'hôte (`Bundle.Load(string root)`, `OkfBundleTools(string)`), la racine est
    de la configuration fiable, comme aujourd'hui.
  - **Le coût** est de quelques appels système par accès, sur une chaîne courte.
- **Règle d'affichage d'un chemin** (pour la CLI, le Viewer, `Diagnostic` et
  `AuditFinding`) : `Location`, puis les segments de la clé joints par le
  **séparateur de l'OS**, c'est-à-dire exactement ce que `Path.Combine(root, …)`
  produit aujourd'hui. Sous Windows, `okf validate` affiche donc toujours des
  `\`. Les tests golden normalisent les `\` (`GoldenParityTests`) et ne peuvent
  pas garantir cette règle : un test **non normalisé** sous Windows la protège
  (§11).
- `GetVersion()` : une empreinte du listing (clé, taille, date de modification),
  bornée (§6). Ce jeton est **faible** : deux modifications de même taille dans la
  même seconde peuvent lui échapper. L'effet est une image périmée jusqu'au
  changement suivant, **jamais une perte de données**, puisque les écritures
  sont protégées par la version d'objet (le hash du contenu). Il **ne sert pas** à
  l'épinglage du code exécuté (§5.4).
- Les surcharges `string root` restent les points d'entrée commodes :
  `Bundle.Load(string root)`, `BundleConceptWriter(string bundleRoot, …)`,
  `IndexGenerator.RegenerateIndexes(string)`, `OkfBundleTools(string, …)`,
  `OkfBundleKnowledgeSource(string, string)`. Chacune construit le provider
  filesystem.

### 9. Les consommateurs

| Consommateur | Changement |
|---|---|
| `Bundle` | `Load(IBundleProvider, BundleLoadOptions?)` ; `Concept.Path` → `Concept.Key` ; `IndexFiles`/`LogFiles` → `BundleKey` ; `ParseErrors` → `BundleParseError(RawName, Key?, Error)` (§2) ; `Root` → `Provider` + `Location` ; `TryResolveResource(..., out BundleKey? key, ...)` ; `ReadResourceText(BundleKey)` ; versions des ressources exécutables relevées au chargement (§5.1) ; lecture bornée de l'`index.md` racine (§6) |
| `ConceptId` | ajoute `ToKey()` et `FromKey(BundleKey)`, que le cœur utilise désormais. `ToPath(string)` et `FromPath(string, string)` restent, comme utilitaires du filesystem : purs calculs de chaînes, rien ne les oblige à disparaître |
| `BundleValidator` | relit `index.md`/`log.md` et teste les ressources via le provider (avec les bornes), plus via `File.*`. `Diagnostic.Path` (aujourd'hui une chaîne remplie avec `concept.Path`) devient `Diagnostic.Key` (`BundleKey?`) ; le chemin affiché suit la règle du §8 |
| `ConceptAudit` | `AuditFinding.Path` devient `AuditFinding.Key` ; le chemin affiché par `okf audit` et `okf_audit` suit la règle du §8, si bien que leurs octets ne changent pas |
| `BundleConceptWriter` | constructeur sur `BundleHandle` (ou provider) ; version attendue optionnelle ; garde de capacité ; relectures-réessais ; atomicité via le provider ; **reprend l'écriture de `log.md`** (§3) ; `RecordVerifications` expose un `BundleWriteReport` ; `AppendToConceptAtomic` appelle `buildBody` une fois par tentative (§4). `BundleRoot` : voir ci-dessous |
| `IndexGenerator` | surcharges sur provider ; garde de capacité ; écritures conditionnelles. Le callback `Synthesize` garde sa forme. Pour la surcharge `string`, `relativeDir` garde aussi sa **valeur historique**, avec le séparateur de l'OS (aujourd'hui `Path.GetRelativePath`, donc `a\b` sous Windows) : rien ne change pour ses appelants. La surcharge provider, qui est nouvelle, passe une chaîne avec `/` pour séparateur, ce qui est documenté. La surcharge provider renvoie un `BundleWriteReport` (clés écrites, échecs) ; la surcharge `string` renvoie toujours la liste des chemins écrits. Un dossier vide n'est plus référencé (§2) |
| `OkfBundleTools` | constructeur sur `BundleHandle` ; plus aucune E/S directe. `AppendLog` délègue au writer ; la lecture d'`index.md` passe par le provider en UTF-8 **strict** (aujourd'hui `File.ReadAllText`, qui remplace les octets invalides par U+FFFD) ; filtrage des outils d'écriture selon la permission effective ; **registre d'observations**, enregistré par les fonctions exposées au modèle et jamais par les méthodes publiques `ReadConcept`/`GetComputation` (§5.4) ; écritures et exécutions conditionnées aux observations (§4, §5.4). `BundleRoot` : voir ci-dessous |
| `OkfContextProvider` | partage le `BundleHandle` de ses outils. Il appelle `ReadConcept` sans rien enregistrer : une injection n'est jamais une observation (D5, §5.4). Ses chargements passent par la couture, mais restent **bloquants** dans ses méthodes asynchrones tant que le contrat est synchrone ; c'est #152 qui les rend asynchrones |
| `OkfBundleKnowledgeSource` | constructeur sur `BundleHandle` ; ne recharge plus à chaque requête, il suit la politique de la source |
| Catalog | voir « Catalog : qui crée et conserve les handles » ci-dessous |
| `FileMemoryStore` | reste adossé au filesystem, via le provider ; recharge le scope à chaque lecture, comme aujourd'hui. `ReadAsync`, `WriteAsync` et `EnumerateAsync` ne changent pas ; `DeleteScopeAsync` rend le nouveau `MemoryDeleteResult` (§1), ce qui est le seul changement d'`IMemoryStore`. Voir ci-dessous pour la suppression d'un scope et les clés mémoire |
| `AttestationOrchestrator` | lit le code sanctionné via `ReadResourceText(BundleKey)`, épinglé (§5.4) |
| Viewer (`SiteModel`, `HtmlWriter`) | `SiteModel` lit aujourd'hui `bundle.Root` et le chemin de `ParseErrors` : il passe à `Location` et au chemin affiché du §8, si bien que le HTML produit par `okf-render` ne change pas |
| `okf` / `okf-render` | restent synchrones, sur le provider filesystem ; leurs octets de sortie ne changent pas, sous Linux comme sous Windows (règle d'affichage du §8). `parse` et `fmt` restent hors couture (§3) |
| `okf-mcp` | reste filesystem (`OKF_BUNDLE_ROOT`). Sa découverte de bundle (`OkfBundleDiscovery.ReadRootIndexOrNull`) lit l'`index.md` racine **avec la borne de 16 Mio** : au-delà, l'index est traité comme illisible, donc « aucune version déclarée », comme aujourd'hui pour une erreur de lecture |
| `producers/` | une recherche n'y trouve aucun usage des membres qui changent, mais une recherche ne prouve rien : la vérification est la compilation et les tests de `producers/OkfProducer.sln` en local |

> **Note du 2026-10-19 (lot B, plan d'implémentation) :** `BundleConceptWriter`
> rend tout refus d'un chemin par le provider avec le message de son ancienne
> garde de point de reparse : ses deux messages (le chemin, l'entrée cible)
> n'en font plus qu'un, puisque le provider ne les distingue pas (B32), et ses
> essais lire-modifier-écrire tournent sous son verrou (B30). `IndexGenerator`
> référence un sous-dossier dès qu'un objet, de n'importe quelle extension, se
> trouve sous lui (§11) ; la création d'un `index.md` passe le contrôle
> d'arborescence comme toute création du cœur ; la surcharge `string` saute en
> silence un `index.md` que le provider refuse (un lien sur son chemin) et un
> `index.md` racine illisible, et lève toute autre erreur, dont
> `BundleWriteConflictException` (`NameUnavailable`) pour un nom que le
> stockage ne peut pas porter : un dossier nommé `index.md` qui contient des
> fichiers et, sur un volume qui plie la casse, un fichier dont le nom ne
> diffère de `index.md` que par la casse (décision du mainteneur, 2026-10-21 :
> échec fermé, ce fichier n'est jamais écrit à travers ; une omission
> silencieuse laisserait l'`index.md` du dossier non écrit sans signal). Les
> remarques « Failures » de `RegenerateIndexesWith` donnent le reste.

**`BundleRoot` d'`OkfBundleTools` et de `BundleConceptWriter`.** Ces propriétés
publiques renvoient une chaîne de chemin, ce qui n'a pas de sens pour un provider
distant.

- Elles sont gardées une version, marquées `[Obsolete]`, comme
  `RunComputation` l'a été.
- Elles renvoient la racine pour un provider filesystem, et lèvent
  `NotSupportedException` pour tout autre provider. Renvoyer `Location` sous un
  nom de « racine » serait un piège.
- Les nouveaux membres sont `Handle` (le `BundleHandle`) et `Location`.

**Catalog : qui crée et conserve les handles.** Aujourd'hui,
`GroupedKnowledgeResolver` et `FusedResolverEngine` appellent
`CatalogPathResolver.TryResolve` et construisent un `OkfBundleKnowledgeSource`
**à chaque requête** (`FusedResolverEngine` déduplique en plus par répertoire
résolu). `MemoryServiceCollectionExtensions` résout aussi les chemins des
sources mémoire. Désormais :

- **L'instantané du catalogue possède les handles.** À chaque
  `ReloadAsync`, le catalogue crée un `BundleHandle` par source de
  connaissance activée, avec la fabrique de son type de provider. Les resolvers
  prennent les handles dans l'instantané au lieu de résoudre un chemin à chaque
  requête.
- **Réutilisation d'un handle : sur la configuration effective seulement.** Le
  catalogue ne réutilise le handle d'une source, pour ne pas jeter son image,
  que si sa **configuration effective** est identique en tout point :
  - type de provider ;
  - pour le filesystem, chemin **résolu**, et non la valeur brute de `path` ;
  - réglages du provider ;
  - politique de rafraîchissement ;
  - bornes ;
  - politique de portabilité ;
  - frontière de confinement.

  Toute différence, par exemple `path: old` devenu `path: new` avec le même type
  et les mêmes réglages, fait **reconstruire** le handle. L'ancien n'est plus
  servi par le nouvel instantané.
- **La déduplication se fait par `IBundleProvider.Id`**, et non plus par
  répertoire résolu. Pour le filesystem, l'identifiant est la racine canonique ;
  le résultat est donc le même qu'aujourd'hui.
- **Les contrôles de chemin** (`CatalogPathResolver` : pas de chemin absolu,
  confinement sous la racine du catalogue, pas de point de reparse) ne valent
  que pour les sources de type `filesystem`. Ils s'appliquent à la création du
  handle. Ensuite, la frontière de confinement (la racine du catalogue) est
  revérifiée par le provider **avant chaque accès au stockage** (§8), et plus
  seulement au chargement.
- **Les sources mémoire** restent de type `filesystem` et sont résolues comme
  aujourd'hui. Une source mémoire d'un autre type est rejetée avec un
  diagnostic, puisque la mémoire distante est hors périmètre.

**Catalog : nommer un provider dans `catalog.json`.** Une source peut porter
`"provider": "<type>"` et un objet `"settings"`. Le type `filesystem` reste le
défaut, avec `path` comme aujourd'hui. L'hôte enregistre des fabriques de
providers par type (options de `FileKnowledgeCatalog`, et une extension
d'enregistrement dans `OKF4net.Catalog.Hosting`). Chaque fabrique **déclare la
liste des réglages qu'elle accepte** (pour S3 : bucket, préfixe, région,
endpoint) ; tout autre réglage est rejeté par un diagnostic. Ainsi aucun champ de
credential ne peut entrer par `catalog.json`, puisque les credentials passent par
le client injecté. `KnowledgeCatalogSource` gagne le type de provider et ses
réglages ; son `Path` ne vaut plus que pour le type `filesystem`. `catalog.json`
lui-même reste un fichier local lu par `FileKnowledgeCatalog`.

**`FileMemoryStore` : suppression d'un scope.** Aujourd'hui `DeleteScopeAsync`
fait un `Directory.Delete(subDir, recursive: true)`, et `Directory.Exists(subDir)`
sert à savoir si un scope existe. Avec la couture :

- la suppression énumère les clés sous le préfixe du scope, puis supprime chaque
  objet ; le résultat porte un `BundleWriteReport` par tier (§1), qu'il y ait
  eu échec partiel ou non ;
- un scope « existe » s'il contient au moins une clé, et non plus si son dossier
  existe. Les dossiers vides laissés sur le filesystem (§8) n'ont donc aucun
  effet.

**`FileMemoryStore` : clés mémoire et noms réservés** (D4, qui remplace
l'arbitrage du 2026-10-08).

- **Ce qui est déjà garanti.** `KnowledgeAccessScope` n'admet que des
  identifiants qui sont des segments `ConceptId` valides (`[A-Za-z0-9_]` puis
  `[A-Za-z0-9_.-]`). `:`, `/` et les caractères non-ASCII n'apparaissent donc
  jamais. Le segment final est construit par `MemoryPath.Encode` sous la forme
  `<identifiant en minuscules>-<hash de 16 caractères>`. Il ne finit jamais par
  `.` ni par une espace, et deux identifiants qui ne diffèrent que par la casse
  ont des hashs distincts.
- **Le seul cas réel.** Un identifiant dont la base, c'est-à-dire la partie
  avant le premier `.`, est un nom réservé de Windows : `con.foo` devient
  `con.foo-<hash>`, qui reste réservé puisque l'extension n'y change rien.
- **La règle.** Le cœur valide le **segment final complet** sous `Strict`. Si sa
  base est un nom réservé, la partie lisible est préfixée par `_`
  (`_con.foo-<hash>`). Le hash, calculé sur l'identifiant brut, ne change pas, et
  garantit toujours l'unicité.
- **La migration.** Seuls ces identifiants changent de clé. Ils n'ont jamais
  fonctionné sous Windows ; sous Linux, une note de migration, dans le
  CHANGELOG et le README de `OKF4net.Catalog`, indique comment renommer leurs
  dossiers.
- **Le domaine.** Étendre le domaine des identifiants admis par
  `KnowledgeAccessScope` serait une décision séparée, explicite, qui devrait
  revoir cette règle.

### 10. Changements cassants et changements de comportement

À consigner au CHANGELOG, dans une même version :

- **Cassants (API)**, détaillés au §9 :
  - `Concept.Path`, `Bundle.Root`, `IndexFiles`, `LogFiles`, `ParseErrors`
    (devient `BundleParseError`), `TryResolveResource`, `ReadResourceText` ;
  - `Diagnostic.Path`, `AuditFinding.Path` ;
  - `KnowledgeCatalogSource`, qui gagne le type de provider et ses réglages ;
  - `BundleRoot` d'`OkfBundleTools` et de `BundleConceptWriter` (`[Obsolete]`
    pendant une version) ;
  - le contrat documenté de `AppendToConceptAtomic` : `buildBody` est appelé une
    fois par tentative et doit être pur ;
  - les constructeurs qui prennent désormais un `BundleHandle` (les surcharges
    `string` restent) ;
  - `MemoryDeleteResult` : `TiersDeleted` disparaît au profit d'une liste de
    tiers portant chacun son `BundleWriteReport` (§1) ; `IMemoryStore` change
    donc de signature sur `DeleteScopeAsync`. `VerificationOutcome`, lui, est
    seulement enrichi.

  La migration asynchrone (#152) sera un second changement cassant, accepté.
- **Comportement, filesystem compris** :
  - bornes finies par défaut (§6). La borne d'entrées compte **toutes** les
    entrées, `.git` et `node_modules` compris (§2) : un très gros dossier peut
    désormais faire échouer un chargement qui réussissait. Un concept de plus de
    16 Mio devient une entrée de `ParseErrors` au lieu d'être chargé ;
  - politique de clés `Strict`, arborescence comprise (§2) : un `.md` écarté
    (nom invalide, collision, conflit fichier/dossier) devient une entrée de
    `ParseErrors`, et une ressource référencée écartée reçoit le statut
    `Unsafe`. Les autres fichiers n'ont jamais été examinés et ne le sont
    toujours pas ;

    > **Note du 2026-10-19 (arbitrage du mainteneur) :** défaut `Native`, parce
    > que l'OKF §11 n'impose aucune contrainte de nommage ni de taille ; `Strict`
    > est un profil sur option ; bornes relevées à 256 Mio / 10 000 000 / 1 024.
    > Seul ce que le stockage ne peut pas contenir ou confondrait est écarté (sous
    > Windows : un point ou une espace final, un caractère interdit, un conflit
    > fichier/dossier ; pas `con.md`, que Windows 11 crée et ouvre par les API
    > ordinaires et que `dev` chargeait : `git` le refuse, d'où l'avertissement
    > `KeyNotPortable` par défaut et le refus sous `Strict`) ;
    > le reste se charge comme sur `dev`, avec un avertissement
    > `KeyNotPortable` quand `Strict` l'aurait refusé ;
  - **dossiers vides** : un dossier sans aucun fichier n'est plus référencé par
    l'index de son parent (§2, D2) ;
  - correspondance exacte en casse et en forme Unicode (§2) : sous Windows ou
    macOS, une ressource référencée avec une autre casse que le fichier réel
    devient `Missing` au lieu d'être résolue ;
  - un `\` dans un chemin §6.2 donne `Unsafe` (§2) ;
  - écritures atomiques et conditionnelles (§4) ;
  - **outils agents** (§4, §5.4) :
    - `okf_run_computation` n'exécute que ce qui a été montré à la session, et
      seulement si le concept (contrat compris) n'a pas changé depuis ;
    - `okf_write_concept` refuse de réécrire un concept que la session n'a pas
      lu en entier avec `okf_read_concept`. Une injection de contexte ne compte
      pas (D5), et `okf_get_computation` non plus ;
    - une entrée évincée du registre (au-delà de 10 000 clés) fait refuser
      l'action jusqu'à une nouvelle lecture ;
    - une computation dont une ressource n'est pas `Pinned` (§5.1) est refusée
      en nommant la ressource et son état ; un refus pour version changée ou
      état négatif invalide l'image ;
    - **D6, changement de sens du §10.5 pour cette implémentation** : une
      computation sans `attester.resource`, ou dont l'attester est une URL,
      n'est plus attestée par l'`IAttester` de l'hôte avec un texte nul ; elle
      est refusée avant tout appel au runtime. À consigner dans
      `docs/spec-conformance/`. Le test
      `Absent_attester_declaration_yields_null_source_text_not_a_failure`
      change de sens avec elle ;
  - une **création** qui produirait une collision ou un conflit fichier/dossier
    est refusée avant toute écriture (§2) ;
  - les sources du catalogue ne rechargent plus à chaque requête, mais restent
    toujours fraîches grâce à `OnAccess` et aux handles conservés (§5.3, §9) ;
  - lecture d'`index.md` par les outils agents : UTF-8 strict (§9) ;
  - découverte d'`okf-mcp` : `index.md` racine borné (§9) ;
  - clés mémoire : préfixe `_` pour les noms réservés, avec note de migration
    (§9).
- **Inchangé** :
  - les octets de sortie de `okf` et `okf-render`, hors dossiers vides dans un
    index régénéré ;

    > **Note du 2026-10-19 (lot B, complétée le 2026-10-21) :** et hors les
    > autres cas consignés au CHANGELOG, dont : un dossier dont les seules
    > entrées sont des liens symboliques ou des jonctions n'est plus référencé
    > non plus (aucun listing ne suit un lien) ; un dossier nommé `index.md` qui
    > contient des fichiers fait échouer `okf index` avec
    > `BundleWriteConflictException` au lieu de `UnauthorizedAccessException`
    > (message d'erreur différent, même code de sortie) ; et, sur un volume qui
    > plie la casse (NTFS, le volume macOS par défaut), un fichier dont le nom ne
    > diffère de `index.md` que par la casse (`Index.md` à la racine ou dans un
    > sous-dossier) le fait échouer de la même façon, code de sortie 1, là où
    > l'ancienne écriture réussissait en l'écrasant (après l'avoir listé comme
    > document dans son propre index). C'est voulu : échec fermé, ce fichier
    > n'est jamais écrit à travers. Les remarques « Failures » de
    > `RegenerateIndexesWith` donnent le reste de ce que lève la surcharge
    > `string`.

  - aucun nom de fichier de `tests/fixtures/` ni de `bundles/` n'est écarté par
    `Strict` (vérifié le 2026-10-06 sur 146 entrées, soit 99 fichiers et 47
    répertoires : ni `:`, ni `.` ou espace final, ni nom réservé, ni caractère
    non-ASCII, ni collision de casse) ;
  - un fichier versionné par git ne peut pas être un dossier vide, donc aucune
    fixture n'est touchée par D2.

### 11. Vérification

- `dotnet test OKF4net.sln` vert, **sans toucher à aucune fixture** ; format et
  publication AOT verts ; ensemble de dépendances de `okf` inchangé.
- **Pas d'E/S résiduelle hors de la couture** : prouvé en remontant les points
  d'entrée publics (surcharges et valeurs par défaut comprises), pas par un grep
  de symboles. La découverte d'`okf-mcp` est incluse.
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
- **Arborescence** : sous `Strict`, `A/x.md` + `a/y.md`, `a.md` + `a.md/b.md`,
  `COM¹.md` et `con.md` sont tous écartés, avec une entrée de `ParseErrors` qui
  porte le nom brut.
- **Noms bruts** : un `.md` rejeté au nom contenant un caractère de contrôle
  apparaît dans `ParseErrors` avec `Key = null`, et s'affiche neutralisé.
- **Correspondance exacte** : sous Windows et macOS, une ressource référencée
  avec une autre casse, ou une autre forme Unicode, que le fichier réel n'est
  ni lue ni écrite.
- **Observations (D1)**. Chaque cas suivant donne un **refus** :
  - le script A est montré, puis remplacé par B (y compris avec la même taille,
    dans la même seconde), puis le handle est rafraîchi et la computation
    exécutée ;
  - une computation est exécutée sans avoir été montrée ;
  - un concept est réécrit sans avoir été lu ;
  - une session a lu v1, une autre fait passer l'image en v2, et la première
    écrit, ce qui donne un conflit au lieu d'un écrasement ;
  - l'attester est modifié après le chargement de l'image ;
  - **(révision 4)** `OkfContextProvider` injecte un concept, entier ou tronqué,
    puis la session tente de le réécrire ou d'exécuter son code inline ;
  - **(révision 4)** la session n'a vu le concept que par `okf_get_computation`,
    puis tente une réécriture complète ;
  - **(révision 4)** le fichier `q.sql` est observé, puis le runtime ou le
    contrat du concept change, le handle est rafraîchi et `q.sql` reste
    identique ;
  - **(révision 4)** une observation est évincée par plus de 10 000 lectures
    d'autres clés, puis la session agit sur la clé évincée ;
  - **(révision 4)** un hôte appelle directement la méthode publique
    `ReadConcept`, puis l'outil d'écriture.

  À l'inverse, ces cas doivent réussir :
  - une lecture par un second jeu d'outils ne remplace pas l'observation du
    premier ;
  - **(révision 4)** la session lit `a`, `a` est supprimé ailleurs, la
    réécriture échoue en conflit, la relecture répond « introuvable », puis la
    recréation en `MustNotExist` réussit.
- **Attestation (D6)** : une computation sans `attester.resource`, ou avec une
  URL, est refusée avant que le runtime soit appelé ; un `IAttester` de test
  enregistré pour ce runtime n'est jamais invoqué. `AttesterSourceText` n'est
  jamais nul à l'entrée d'un runtime.
- **Reprise après refus**, sous chacune des quatre politiques : l'attester passe
  de v1 à v2 après le chargement ; la première exécution est refusée ; la
  seconde tentative, sans aucun `Refresh()` de l'hôte, s'exécute sur v2. Même
  parcours pour une ressource `Missing` créée après le chargement. Les
  observations de la session sont inchangées par ce rafraîchissement.
- **Reprise dégradée** : un chargement déjà en cours au moment du refus
  n'acquitte pas l'obligation (la tentative suivante recharge encore) ; un
  rechargement qui échoue (réseau simulé, plafond #151) laisse l'obligation
  pendante et l'image v1 servie périmée, puis un rechargement réussi
  l'acquitte.
- **Relevé des ressources exécutables** : un concept dont l'attester est absent,
  `Unsafe`, illisible, de plus de 16 Mio ou une URL est **chargé**, validé et
  lisible. Son exécution est refusée en nommant l'état. Aucune lecture n'est
  tentée pour une URL.
- **Frontière de confinement** : la racine d'une source de catalogue est
  remplacée par une jonction **après** le chargement, sans rafraîchissement
  (politique `Manual`). Une lecture de ressource différée, un `GetVersion` et
  une écriture sont alors tous refusés.
- **Réutilisation des handles** : une source dont seul `path` change obtient un
  nouveau handle, qui sert le nouveau contenu ; une source inchangée garde le
  sien.
- **Arborescence à l'écriture** : sur un filesystem sensible à la casse dont
  l'image contient `A/x.md`, la création de `a/y.md` est refusée sous `Strict`.
  La création de `a.md/b.md` quand `a.md` existe l'est aussi.
- **`Synthesize`** : sous Windows, la surcharge `string` reçoit toujours
  `relativeDir = a\b`, et la surcharge provider `a/b`.
- **Dossiers vides** : un dossier vide n'apparaît plus dans l'index régénéré ; un
  dossier qui ne contient que des fichiers non-`.md` y apparaît toujours.
- **Bornes** : un `index.md` racine de plus de 16 Mio donne `okf_version` nul,
  au chargement comme dans la découverte d'`okf-mcp`. Un concept de plus de
  16 Mio va dans `ParseErrors`.
- **Mémoire** : `con.foo` est écrit, lu et supprimé sous `_con.foo-<hash>` ; un
  scope vidé n'« existe » plus ; une suppression partielle rend un
  `BundleWriteReport`.
- **Catalogue** : deux requêtes successives sur une source inchangée réutilisent
  la même image ; une modification du bundle est vue à la requête suivante
  (`OnAccess`) ; une racine de source remplacée par une jonction après la
  création du handle est refusée **au prochain accès**, quel qu'il soit (voir
  « Frontière de confinement » plus haut).
- **Immuabilité** : l'audit des sites qui modifient un document chargé est
  livré ; aucun d'eux ne modifie une image partagée.
- **Concurrence** : le test concurrent de #131 reste vert de façon répétée (s'il
  rougit, c'est une régression, pas un aléa) ; réessais bornés des opérations
  lire-modifier-écrire, avec un `buildBody` appelé une fois par tentative.
- **Batterie hostile exécutée**, commune à tous les providers et lancée sous
  `Native` (le plancher) : traversée, séparateurs encodés, `..` littéral contre
  normalisation côté client, confusion de préfixe, alias de casse et NFC/NFD,
  `.`/espace final et noms réservés (exposants compris), conflits fichier/dossier,
  objets et listings surdimensionnés, redirections. Elle inclut le cas d'un
  segment de `ConceptId` qui se termine par `.`, accepté aujourd'hui et
  résolu comme alias sous Windows (constaté par la revue externe du
  2026-10-09 : une écriture `a./b` aboutit à `a/b`).
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
6. **Un dépassement de taille lève une exception dérivée d'`IOException`**, pour
   que les gestionnaires existants la classent comme « illisible » sans
   modification.
7. **Un `\` dans une valeur de chemin §6.2 donne `Unsafe`.** Aujourd'hui, sous
   Windows, `references\a.sql` est résolu, alors que sous Linux c'est un nom de
   fichier contenant un `\`. Le design choisit un résultat portable et qui échoue
   fermé ; c'est un changement de comportement pour les bundles écrits sous
   Windows avec des `\`.
8. **Collisions et conflits : tous les noms concernés sont écartés**, avec leurs
   sous-arbres, et non seulement le second.
9. **Aucune minuterie d'arrière-plan** : `Interval` et `Notify` se vérifient au
   prochain accès.
10. **`Bundle.Root` remplacé par `Provider` + `Location`**, plutôt qu'un `Root`
    nullable qui ne vaudrait que pour le filesystem.
11. **`ConceptId.ToPath`/`FromPath` sont conservés** comme utilitaires du
    filesystem, à côté des nouveaux `ToKey`/`FromKey`, plutôt que supprimés.
12. **`Diagnostic.Path` devient `Diagnostic.Key`, et `AuditFinding.Path` devient
    `AuditFinding.Key`.** Le chemin affiché est calculé par l'appelant (§8),
    plutôt que de garder dans la bibliothèque une chaîne déjà composée.
13. **Un `index.md` ou `log.md` trop gros** est traité comme un fichier
    illisible au moment où on le lit, plutôt qu'écarté dès l'énumération.
14. **Un scope mémoire existe s'il contient au moins une clé**, plutôt que si
    son dossier existe.

Ajoutés par la révision 3 :

15. **Un concept de plus de 16 Mio va dans `ParseErrors`** (comportement
    nouveau et permissif), alors qu'un concept en UTF-8 invalide continue de
    faire échouer tout le chargement comme aujourd'hui. Les deux ne sont pas
    alignés, pour ne pas élargir le changement.
16. **Le périmètre d'une observation est l'instance d'`OkfBundleTools`**, et les
    observations sont `okf_read_concept` et `okf_get_computation` seulement :
    une recherche ou une navigation, qui ne montrent que des extraits, n'en sont
    pas.
17. **L'attester est épinglé à l'image courante**, par une version relevée au
    chargement, puisque son code n'est jamais montré au modèle. Cela coûte une
    lecture par ressource exécutable référencée, à chaque chargement.
18. **`BundleRoot` gardé une version en `[Obsolete]`**, levant
    `NotSupportedException` hors filesystem, plutôt que supprimé tout de suite ou
    renvoyant `Location`.
19. **Le catalogue réutilise le handle d'une source inchangée** à travers un
    `ReloadAsync`, et refait les contrôles de racine d'une source filesystem à
    chaque chargement.
20. **Un rapport commun `BundleWriteReport`** pour les opérations multi-objets.
    Les types de résultat existants (`VerificationOutcome`,
    `MemoryDeleteResult`) l'exposent, plutôt que d'être remplacés.
21. **Préfixe `_` pour un nom réservé dans une clé mémoire**, plutôt qu'un autre
    échappement.

Ajoutés par la révision 4 :

22. **Trois finalités d'observation** (concept complet, code, absent), avec ce
    que chacune autorise, plutôt qu'une observation unique.
23. **Les méthodes publiques `ReadConcept`/`GetComputation` n'enregistrent
    rien** : seules les fonctions exposées au modèle le font. Un hôte qui
    présente lui-même un résultat n'obtient donc aucun droit d'agir.
24. **Borne du registre : 10 000 clés**, avec éviction des moins récemment
    utilisées, et refus de l'action après éviction.
25. **Frontière de confinement seulement quand elle est fournie** : une racine
    passée directement par l'hôte reste de la configuration fiable, sans
    revérification à chaque accès.
26. **Vérification de l'arborescence avant création seulement**, en énumérant
    les préfixes parents chez le provider, et non avant chaque écriture d'un
    objet existant, qui ne change pas l'arborescence.
27. **Les états du relevé des ressources exécutables** (`Pinned`, `Undeclared`,
    `Url`, `Unsafe`, `Missing`, `Unreadable`, `TooLarge`), avec un concept
    toujours chargé et une exécution refusée hors `Pinned`.

Ajoutés par la révision 5 :

28. **Le refus d'épinglage crée une obligation de rechargement même sous
    `Manual`**, plutôt que d'exiger un `Refresh()` de l'hôte ; avec les propres
    écritures et les conflits d'écriture, c'est l'une des trois exceptions à la
    politique.
29. **`Undeclared` et `Url` ne créent pas d'obligation**, puisque seul un
    changement du concept peut les faire évoluer ; sous `Manual`, ce changement
    externe demande donc un `Refresh()` de l'hôte.

Ajoutés par la révision 6 :

30. **L'obligation de rechargement n'est acquittée que par un chargement réussi
    commencé après le refus**, plutôt que par toute tentative ou par la fin d'un
    chargement déjà en cours.
31. **Un échec global pendant le relevé (délai réseau, bornes, plafond) fait
    échouer le chargement**, comme il le ferait sans relevé, plutôt que de
    devenir un état `Unreadable` local.
32. **`VerificationOutcome` enrichi, `MemoryDeleteResult` remplacé** (§1) :
    le premier porte des faits que le rapport générique n'a pas et alimente une
    sortie golden ; le compteur du second perd son unité avec la couture.

## Risques et limites

- **Taille du changement.** Il touche l'API publique du cœur et tous ses
  consommateurs. Le plan doit le découper en PR relisibles : contrat et
  extraction du filesystem d'abord, consommateurs ensuite.
- **Le registre de verrous du filesystem** est subtil (registres lexical et
  résolu, réentrance explicite, comportement sur alias). Le déplacer derrière la
  couture sans changer sa sémantique est le passage le plus risqué ; le test
  concurrent de #131 et les tests de verrou existants en sont la barrière.
- **« Lire avant d'agir » change l'usage des outils** : un modèle qui réécrivait
  un concept ou exécutait une computation sans les lire d'abord se voit refuser
  l'action, et doit faire un appel de plus. Les messages de refus disent
  lequel. C'est aussi vrai pour un concept que `OkfContextProvider` avait déjà
  injecté en entier (D5), et après l'éviction d'une entrée du registre.
- **Jeton de version faible sur le filesystem** : une modification peut échapper
  au rafraîchissement jusqu'à la suivante (§8). C'est sans perte de données ;
  l'épinglage du code exécuté n'en dépend plus.
- **Fenêtre inter-processus sur le filesystem** : acceptée (§4, #178).
- **Attestation fournie par l'hôte (D6)** : un hôte qui s'appuyait sur un
  `IAttester` attestant sans source dans le bundle voit ses computations
  refusées jusqu'à ce que le bundle déclare un attester en fichier. Aucun bundle
  du dépôt n'est dans ce cas ; des hôtes externes peuvent l'être, d'où l'entrée
  de conformance et le CHANGELOG.
- **Immuabilité** : tant que #181 n'est pas fait, un appelant qui modifie un
  document d'une image partagée la corrompt pour tous ses consommateurs. C'est
  documenté comme non supporté, sans être empêché.
- **Bundles existants sous Linux** dont des noms tombent sous `Strict` :
  diagnostics nouveaux. C'est l'hôte qui choisit de passer en `Native`.
- **Borne d'entrées et gros dossiers** : comme elle compte toutes les entrées,
  un bundle placé à la racine d'un dépôt avec un énorme `node_modules` peut ne
  plus se charger. L'erreur est explicite, et la borne se règle par source.
- **Dossiers vides** : un utilisateur qui préparait l'arborescence d'un bundle
  avec des dossiers vides ne les voit plus dans l'index régénéré.
- **Migration des clés mémoire** : sous Linux, les scopes dont l'identifiant a
  pour base un nom réservé changent de clé ; sans renommage, leur mémoire
  existante n'est plus lue. La note de migration le dit.
- **Correspondance exacte** : sous Windows et macOS, un bundle qui référençait
  une ressource avec la mauvaise casse et « fonctionnait » verra cette ressource
  passer à `Missing`.

## Travaux futurs

Tickets #140, #141, #142, #143, #151, #152, #153, #154, #178, #179, #181 (voir
Périmètre).
