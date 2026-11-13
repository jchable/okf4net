# Provider de bundle embarqué dans un assembly — design

Date : 2026-11-12
Statut : **révision 3**, à relire par le mainteneur (la révision 1 a été
validée section par section le 2026-11-12, puis soumise à deux revues
externes ; la révision 2 les intègre, avec les arbitrages D6 et D7 ; la
révision 3 intègre la relecture de la révision 2 : parcours des liens sans
descente, position de l'import dans les tests, `None`, `PortabilityIssues`)
Ticket : [#141](https://github.com/jchable/okf4net/issues/141). Suite hors lot :
[#236](https://github.com/jchable/okf4net/issues/236) (fabrique de catalogue).

Prérequis de lecture : la couture provider
([`2026-10-07-bundle-provider-seam-design.md`](2026-10-07-bundle-provider-seam-design.md))
et son contrat asynchrone
([`2026-11-03-async-bundle-provider-design.md`](2026-11-03-async-bundle-provider-design.md)),
dont ce document reprend le vocabulaire (clé, contraintes natives, bornes,
image, handle).

## 1. But

Permettre à un hôte de livrer son bundle OKF **dans son propre binaire** — un
agent single-file ou Native AOT, un outil `dotnet tool`, un assembly de tests —
sans répertoire à déployer à côté, et sans que le bundle puisse diverger du
binaire avec lequel il a été testé. C'est aussi le deuxième backend du contrat
`IBundleProvider` : BCL seule, en lecture seule par nature, sans réseau, il
exerce la couture sur un espace de clés qui n'est pas un système de fichiers.

## 2. Décisions

| # | Question | Décision |
|---|---|---|
| D1 | Format d'embarquement | **Une ressource de manifeste par fichier**, `LogicalName` = `okf:<prefix>/<chemin relatif>`. Pas d'archive zip (décompression à chaque lecture, horodatages non reproductibles, séparateurs dépendants du MSBuild). |
| D2 | Déclaration côté build | **Un `.targets` livré dans le paquet**, en MSBuild pur, item `<OkfBundle Include="dir/" Prefix="kb" />`. |
| D3 | Intégration au catalogue | **Hors de ce lot** ; ticket [#236](https://github.com/jchable/okf4net/issues/236). Le README du paquet montre la fabrique d'une dizaine de lignes qu'un hôte écrit lui-même. |
| D4 | Preuve Native AOT | **Un job CI** qui publie en AOT une sonde consommant le **vrai paquet** et l'exécute. |
| D5 | Nommage | Paquet et projet `OKF4net.Providers.EmbeddedResources`, classe `EmbeddedResourceBundleProvider`, item MSBuild `OkfBundle`. `Resources` seul était ambigu avec les ressources §6.2 du cœur (`ExecutableResources`, `ResourceKeyResolver`). |
| D6 | Lien (symlink, jonction) sous le répertoire du bundle, au build | **Erreur de build** `OKFEMB005` nommant le lien (échec fermé). Le glob `**` de MSBuild suit les liens : sans garde, un lien vers `~/.aws/credentials` ou un répertoire extérieur partirait dans un exécutable publié — vérifié par essai sous Windows (jonction) et Linux (symlinks de fichier et de répertoire). |
| D7 | Membres `[Obsolete]` qui lisent par `System.IO` depuis `Location` | **Refus hors filesystem**, dans ce lot : `Bundle.TryResolveResource(…)` et `Bundle.ReadResourceText(string)` lèvent `NotSupportedException` quand le provider n'est pas un `FileSystemBundleProvider` (§4.4). |

## 3. Conformité à la spécification

SPEC §3 : « A bundle is a directory tree of markdown files » et « A bundle MAY
be distributed as » un dépôt git, une archive, un sous-répertoire — liste
permissive, non exhaustive. Le provider présente l'arbre du répertoire source
(mêmes chemins relatifs, mêmes octets) tel que le provider filesystem le
liste — liens exclus, puisque le build les refuse (D6) — et le cœur le lit par
la même couture. La parité (§6.3) en est la preuve.

Le stockage embarqué ne peut pas tenir quelques noms qu'un système de fichiers
Linux tient : des jumeaux de casse (`Case.md` et `case.md`, que `csc` refuse
comme doublons, CS1508) et des noms contenant `\`, `"`, `|` ou un caractère de
contrôle (CS1566). Le build les **refuse en les nommant** (`OKFEMB004`), il ne
les perd jamais en silence. Ce n'est pas une divergence de la spec, qui
n'impose aucun nom ; ces noms sont déjà non portables (`Strict`). **Pas
d'entrée dans `docs/spec-conformance/`.**

## 4. Le provider à l'exécution

### 4.1 Convention de nommage

Un fichier de chemin relatif `<rel>` (séparateur `/`, nom exact : casse, points,
Unicode tels que le système de fichiers source les donne) devient la ressource
de manifeste `okf:<prefix>/<rel>`. Le préfixe est une chaîne non vide sans `/` ;
plusieurs bundles peuvent cohabiter dans un même assembly sous des préfixes
distincts. Un préfixe en couvre un autre seulement à la frontière de segment :
`kb` ne voit jamais `okf:kb2/…`.

Le provider ne lit que la convention, pas la façon dont les ressources ont été
produites : une ressource déclarée à la main avec le bon `LogicalName` est lue
de la même manière.

### 4.2 API publique

```csharp
namespace OKF4net.Providers.EmbeddedResources;

public sealed class EmbeddedResourceBundleProvider : IBundleProvider
{
    /// Lève ArgumentException si l'assembly est dynamique, si le préfixe est
    /// vide ou contient « / », ou si aucune ressource ne porte « okf:<prefix>/ ».
    public EmbeddedResourceBundleProvider(Assembly assembly, string prefix);

    /// Les préfixes présents dans l'assembly, distincts, triés ordinalement.
    public static IReadOnlyList<string> GetBundlePrefixes(Assembly assembly);
}
```

Un préfixe inconnu lève à la construction : c'est presque toujours une faute
de frappe ou un `<OkfBundle>` oublié, qu'un bundle vide masquerait.
`GetBundlePrefixes` ignore un nom `okf:` sans `/` ou au préfixe vide
(`okf:/x`) ; un assembly dynamique y lève la même `ArgumentException`.

**Domaine.** Le provider sert les assemblies d'exécution — chargés depuis un
fichier, un bundle single-file, une image Native AOT ou des octets
(`Assembly.Load(byte[])`, qui fonctionne et est testé) — et les ressources
**incorporées** dans leur module manifeste. Un assembly dynamique
(`Assembly.IsDynamic` : `GetManifestResourceNames` y lève
`NotSupportedException`) est refusé par une `ArgumentException`. Une ressource
liée (`/linkresource`) ou située dans un autre module n'est pas prise en
charge : Native AOT ne la recopie pas. Pas de support multi-module.

### 4.3 Contrat

| Membre | Comportement |
|---|---|
| `Capabilities` | `CanWrite = false`, `CompletesSynchronously = true`. |
| `Id` | `embedded:<nom complet de l'assembly>/<MVID>/<prefix>` — stable, non secret. Le nom complet seul ne suffit pas : deux constructions de contenus différents sous la même version d'assembly, chargées par `Assembly.Load(byte[])`, ont le même nom complet et des MVID distincts (vérifié par essai), et le catalogue déduplique sur `Id` (`FusedResolverEngine`). Deux chargements des mêmes octets (deux `AssemblyLoadContext` par exemple) gardent le même `Id` : même contenu, dédoublonnage correct. |
| `Location` | `embedded:<nom simple de l'assembly>/<prefix>` — **affichage seulement**. La règle d'affichage (`BundleDisplayPath`) y ajoute séparateur de l'OS et segments comme pour le filesystem ; sous Windows l'affichage mélange donc les séparateurs (`embedded:App/kb\sub\x.md`), ce qui est assumé. `Concept.Path`, `Diagnostic.Path`, `Bundle.Root`, `IndexFiles`, `LogFiles` en sont des projections d'affichage, jamais des chemins à ouvrir : un appelant lit par `BundleKey` (`TryResolveResourceKey`, `ReadResourceText(BundleKey)`). Les deux membres `[Obsolete]` qui feraient de l'E/S depuis cette chaîne la refusent (D7, §4.4). `Assembly.Location`, vide en single-file, n'est jamais utilisé. |
| `NativeConstraints` | `WellFormedUnicode` seul. Les noms sont stockés en UTF-8 dans le tas `#Strings` (ECMA-335 §II.24.2.3) : deux surrogates isolés y deviendraient le même nom. Un assembly produit par le `.targets` n'en contient jamais (`csc` échoue avant, CS1566) ; le drapeau couvre les assemblies écrits autrement. Rien d'autre : la recherche est ordinale et sensible à la casse, sans normalisation (vérifié en JIT, single-file élagué et AOT), et le manifeste peut tenir à la fois `okf:kb/a` et `okf:kb/a/b` sans alias. Exiger un arbre est une portabilité, pas une contrainte native (`BundleKeyConstraints`, remarques de classe) : sous la politique `Native`, un tel couple — possible seulement par un `LogicalName` écrit à la main — est signalé comme non portable (`KeyNotPortable`, `Strict` portant `FileDirectoryConflict`) ; sous `Strict`, le cœur rejette la clé en conflit et ce qui est dessous, et le chargement continue. |
| `GetVersionAsync` | Jeton dérivé du `ModuleVersionId` (MVID) du module manifeste : le contenu ne peut pas changer pendant la vie du processus. En build déterministe, le MVID dépend du contenu des ressources, pour le build IL comme pour la publication élaguée (vérifié par essai : build identique, même MVID ; un octet modifié, autre MVID ; retour arrière, MVID d'origine), et Native AOT le conserve. |
| `EnumerateAsync` | La liste des noms est lue une fois (`GetManifestResourceNames()`) à la construction, filtrée sur `okf:<prefix>/`, préfixe retiré, triée ordinalement (ordre déterministe). Le filtrage par `prefix` de clé se fait à la frontière de segment. **Le provider n'applique aucune borne** : il n'a pas de parcours interne (sa liste est déjà calculée), et le cœur applique les trois bornes dans `Bundle.ListNamesAsync` — `MaxEntries` et `MaxDepth` en levant, `MaxKeyBytes` en mettant le nom de côté comme un problème local qui ne fait pas échouer le chargement. Un provider qui lèverait sur `MaxKeyBytes` changerait cette sémantique. (Le filesystem compte aussi les répertoires dans son parcours interne ; les deux ne coïncident donc pas exactement à la marge de `MaxEntries`, ce qui est sans effet sur la parité aux bornes par défaut.) `Length` est fourni quand il est connu sans lecture. |
| `ReadAsync` | `GetManifestResourceStream` sur le nom exact (recherche ordinale). `Length` = longueur du flux (`UnmanagedMemoryStream`, longueur connue). Absent → `BundleObjectNotFoundException`. Avec `expected` différent du jeton → `BundleVersionMismatchException`. |
| `ExistsAsync` | Appartenance exacte (ordinale) à la liste filtrée. |
| `WriteAsync` / `DeleteAsync` | `ValueTask.FromException(new BundleReadOnlyException(Id))`, rien n'est tenté — la forme exacte de `ReadOnlyBundleProvider`. |

Version d'objet : le même jeton MVID pour tous les objets. Il suffit aux
comparaisons `expected` parce que le contenu est immuable pour le processus.
Aucun mécanisme du cœur n'exige des jetons distincts pour deux objets :
`ExecutableResources` et `ObservationRegistry` travaillent par clé, `expected`
compare le jeton de cette clé, `BundleHandle` ne compare que le jeton du
bundle, et les jetons ne sont ni persistés ni montrés au modèle.

Toutes les opérations rendent une tâche déjà achevée : le provider sert les
points d'entrée synchrones (`SyncBridge`) sans attente.

### 4.4 Membres `[Obsolete]` à E/S hors filesystem (D7)

`Bundle.TryResolveResource(Concept, string, out string?, out ResourceResolutionStatus)`
et `Bundle.ReadResourceText(string)` résolvent et lisent par `System.IO` depuis
`Root`, hors de la couture. Pour un provider qui n'est pas un
`FileSystemBundleProvider`, `Root` est une chaîne d'affichage qui peut désigner
un répertoire relatif ordinaire : sous Linux, `GetFullPath("embedded:App/kb")`
donne `<cwd>/embedded:App/kb`, et un fichier planté là serait lu (mesuré). Le
défaut est préexistant (`memory://x` de `InMemoryBundleProvider`). Les deux
membres lèvent désormais `NotSupportedException` dans ce cas, avec un message
qui renvoie à `TryResolveResourceKey` / `ReadResourceText(BundleKey)` — la
même réponse que `BundleRoot` donne déjà hors filesystem
(`BundleConceptWriter`, `OkfBundleTools`). Changement du cœur, dans ce lot,
couvert par un test par membre (provider embarqué et `InMemoryBundleProvider`) ;
le comportement filesystem est inchangé. `ObsoleteMembersTests` reste
l'inventaire fermé : aucun membre n'est ajouté ni retiré. `NoResidualFileIoTests` repose sur un
`InMemoryBundleProvider` **inscriptible** (ses cas d'écriture n'ont pas de sens
ici) : le provider embarqué a donc son propre test frère, dans la même
collection `CurrentDirectoryCollection`, qui fait passer chaque point d'entrée
de **lecture** (chargement, validation, audit, recherche, outils de lecture
d'`OkfBundleTools`, résolution et lecture par clé) avec le répertoire courant
réglé sur un dossier vide, et vérifie que ce dossier reste vide.

### 4.5 Hors périmètre (YAGNI)

Pas de rafraîchissement (`BundleRefreshPolicy.Manual` suffit, le contenu est
figé), pas de chargement d'assembly par nom, pas de fabrique de catalogue
(#236), pas de lecture d'archive zip, pas de support multi-module.

## 5. Côté build : le `.targets` du paquet

### 5.1 Approche

`build/OKF4net.Providers.EmbeddedResources.targets`, en **MSBuild pur**
(transformations d'items, fonctions de propriétés), sans assembly de tâche.
L'alternative — une tâche compilée — imposerait de livrer une DLL compilée
contre `Microsoft.Build.Utilities.Core` et chargée tant par le MSBuild .NET
Framework de Visual Studio que par celui de `dotnet` : une dépendance et une
matrice de compatibilité pour peu de gain. Les essais des revues montrent que
le MSBuild pur suffit pour le nommage fidèle et les doublons de casse
(`Distinct()` contre `DistinctWithCase()`), et que `File.GetAttributes` rend
`ReparsePoint` sur les liens. Ce qu'il ne peut pas reproduire, le build le
**refuse en le nommant** (le ticket : « round-trip every path … or reject it
explicitly »).

**Point ouvert, tranché par le prototype (§8).** Le contrôle des liens doit
inspecter chaque répertoire **avant d'y descendre** (§5.4, étape 2), ce que ni
le glob `**` ni `Directory.GetDirectories(root, '*', AllDirectories)` ne font :
tous deux traversent une jonction avant que l'on puisse en contrôler le
résultat (vérifié : `knowledge\link\deep` est énuméré), et une cible
inaccessible ou cyclique peut empêcher d'atteindre le diagnostic. Candidat en
MSBuild pur : un parcours récursif par la tâche `MSBuild` sur le `.targets`
lui-même, un niveau par appel, avec `Directory.GetDirectories(dir)` non
récursif et `File.GetAttributes` sur chaque enfant avant l'appel suivant. Si le
prototype ne démontre pas cette propriété sur les trois OS, le plan s'arrête et
D2 est rouvert avec le mainteneur (candidats : tâche inline
`RoslynCodeTaskFactory`, fournie par MSBuild sans DLL livrée ; tâche
compilée).

### 5.2 Usage

```xml
<ItemGroup>
  <OkfBundle Include="knowledge/" Prefix="kb" />
</ItemGroup>
```

```csharp
var provider = new EmbeddedResourceBundleProvider(typeof(App).Assembly, "kb");
var bundle = await Bundle.LoadAsync(provider, ct: ct);
```

Le `.targets` est importé dans **tout** projet qui référence le paquet, y
compris une bibliothèque qui ne fait que lire un bundle : sans item
`OkfBundle`, il ne fait **rien**. Chaque cible et chaque `Remove` porte
`Condition="'@(OkfBundle)' != ''"` — sans elle, zéro item donne un seul lot
vide, donc `Include="**"`, et tout le répertoire du projet (y compris `obj/`,
et d'éventuels secrets) partirait en ressources (vérifié par essai).

### 5.3 Retrait des items par défaut

Sous la condition ci-dessus, le `.targets` retire chaque fichier de bundle des
items par défaut `Compile`, `EmbeddedResource` et `Content` : sinon un script
sanctionné `.cs` rangé dans le bundle (§10) serait **compilé dans l'hôte**, un
`.resx` serait compilé par ResGen. `None` est laissé tel quel, pour garder les
fichiers visibles dans l'IDE : un item `None` par défaut ne porte aucune
métadonnée de copie et n'affecte pas le build ; si l'hôte pose lui-même
`CopyToOutputDirectory` ou `CopyToPublishDirectory` sur des fichiers du bundle,
ils sont copiés à côté du binaire comme il l'a demandé — le `.targets` ne
retire ni n'ajoute ces métadonnées, et le README le signale (une copie sur
disque va contre le but du paquet sans rien changer à ce que sert le provider).

**Ce retrait est une cible, pas un `Remove` d'évaluation (révision du
2026-11-13).** La première conception posait à l'évaluation
`Remove="@(OkfBundle->'%(FullPath)/**')"`, qui marchait sur le prototype. Elle
est fausse pour un vrai projet : ce `Remove` cesse silencieusement de retirer
quoi que ce soit dès que le projet compte **100** items `Compile` (mesuré : 98
retire, 100 ne retire pas ; `OKF4net.Tests` en a plus de 700), de sorte que le
`x.cs` du bundle était compilé — c'est `OKFEMB006` qui l'a révélé en construisant
`OKF4net.Tests`. Un `Remove="%(_OkfRoot.OkfDir)**"` par lot, dans une cible,
ne retirait rien non plus. Ce qui marche, à toute taille : construire par une
comparaison de chemins la liste des items situés sous le répertoire, puis les
retirer **par identité**, sans glob.

La cible `_OkfDetachDefaultItems` (un lot par bundle, `_OkfDetachBundle`) tourne
`BeforeTargets="PrepareForBuild;AssignTargetPaths;CoreCompile"` : avant tout
consommateur des listes — `AssignTargetPaths` précède le traitement des
ressources, dont `SplitResourcesByCulture` copierait un `.resx` ou un `.licx`
dans des listes qu'un retrait ultérieur n'atteint plus. Elle voit tout
`OkfBundle` déclaré au moment où le build tourne, où que ce soit — corps du
projet, `Directory.Build.targets`, cible exécutée plus tôt. `OKFEMB006` (§5.4,
étape 5) ne couvre plus que ce qu'une cible **remet** dans ces listes après le
retrait.

### 5.4 Exécution : la cible

Une cible `BeforeTargets="PrepareResourceNames"`, sous la condition de §5.2,
qui fait dans l'ordre, pour chaque `OkfBundle` :

1. **Valide l'item** : `Prefix` présent, non vide, sans `/` (`OKFEMB001`) ;
   répertoire existant et contenant au moins un fichier (`OKFEMB002`) ; aucun
   autre `OkfBundle` au même préfixe, **comparé sans tenir compte de la
   casse**, puisque `csc` compare ainsi les noms de ressources (`OKFEMB003`).
2. **Refuse tout lien** (D6) : le répertoire du bundle lui-même, chacun de ses
   sous-répertoires et chacun de ses fichiers, dont `File.GetAttributes`
   contient `ReparsePoint` (.NET le renseigne aussi pour un symlink Unix) →
   `OKFEMB005` nommant le lien. **Chaque répertoire est inspecté avant toute
   descente** : le parcours ne liste jamais le contenu d'un lien, si bien
   qu'une jonction vers l'extérieur, une cible inaccessible ou un cycle
   aboutissent au diagnostic au lieu de parcourir la cible (§5.1, point
   ouvert). Le glob des fichiers n'est évalué qu'après ce parcours, quand plus
   aucun répertoire n'est un lien ; les fichiers qu'il rend sont contrôlés à
   leur tour.
3. **Calcule les noms** à partir de `%(FullPath)` moins le chemin complet du
   répertoire du bundle (`Substring`), séparateur `\` remplacé par `/` sous
   Windows. Pas de `%(RecursiveDir)%(Filename)%(Extension)` : sous Linux,
   `%(Extension)` perd le point final de `trailingdot.` (vérifié). La variante
   `Substring` (comme `$([MSBuild]::MakeRelative(…))`) reproduit les 41 noms
   Linux et les 27 noms Windows des essais, octets compris, sans décodage `%xx`
   supplémentaire.
4. **Refuse ce qu'il ne peut pas reproduire** (`OKFEMB004`, en nommant le
   fichier) : un nom relatif contenant `\` (sous un OS où c'est un caractère de
   nom), `"`, `|` ou un caractère de contrôle (U+0000–U+001F, U+007F) ; deux
   noms du même bundle égaux sans tenir compte de la casse. Les caractères
   sont contrôlés **pendant le parcours, sur les noms bruts** que rendent
   `Directory.GetFiles` et `Directory.GetDirectories`, avant qu'aucun ne
   devienne un item : hors Windows, MSBuild change le `\` d'une spécification
   d'item en `/`, si bien que `back\slash.md` atteindrait `File.GetAttributes`
   sous le nom `back/slash.md` et échouerait sur `MSB4184` au lieu du refus
   nommé (revue du plan, vérifié sous Linux). (Un nom à
   surrogate isolé n'est pas détectable en MSBuild : `csc` échoue sur CS1566,
   message moins lisible mais build en échec, rien de perdu.)
5. **Vérifie le retrait** : un item `Compile`, `Content` ou
   `EmbeddedResource` — hormis ceux que la cible ajoute — encore situé sous un
   répertoire de bundle → `OKFEMB006` (un item remis par une cible après le
   retrait, §5.3).
6. **Ajoute** pour chaque fichier un `EmbeddedResource` avec
   `LogicalName="okf:<Prefix>/<nom>"`, `WithCulture="false"` et
   `Type="Non-Resx"`.

**Pourquoi `BeforeTargets="PrepareResourceNames"`, et pourquoi ces deux
métadonnées sont obligatoires.** `BeforeTargets` s'exécute après les
dépendances de la cible (`AssignTargetPaths`, `SplitResourcesByCulture`,
`CreateManifestResourceNames`) : les items ajoutés échappent donc à
`AssignCulture` et aux traitements spécialisés, et arrivent complets. Mais ces
deux métadonnées sont alors le seul filtre de `_CoreCompileResourceInputs`
(`Microsoft.Common.CurrentVersion.targets`) : sans elles, le build réussit avec
**zéro ressource** (vérifié). Avancer la cible avant `AssignTargetPaths`
marcherait pour la culture (sans `WithCulture="false"`, `glossary.fr.md`
partirait dans un satellite `fr/`, vérifié) mais enverrait un `.licx` du bundle
à la tâche LC (`MSB4803`, vérifié). Modifier `PrepareResourceNamesDependsOn`
est écarté : écrasé en silence par `CurrentVersion.targets` quand le fichier
est importé dans le corps du projet. Le `.targets` porte ces raisons en
commentaire.

### 5.5 Erreurs de build

| Code | Cas |
|---|---|
| `OKFEMB001` | `Prefix` absent ou invalide (vide, ou contenant `/`). |
| `OKFEMB002` | Répertoire inexistant, ou sans aucun fichier. |
| `OKFEMB003` | Deux `OkfBundle` au même préfixe, sans tenir compte de la casse. |
| `OKFEMB004` | Un nom que le stockage embarqué ne peut pas tenir : `\`, `"`, `|`, caractère de contrôle, ou jumeau de casse dans le même bundle. |
| `OKFEMB005` | Un lien symbolique ou une jonction : le répertoire du bundle, un sous-répertoire ou un fichier (D6). |
| `OKFEMB006` | Un item `Compile`, `Content` ou `EmbeddedResource` remis, par une cible, sous un répertoire de bundle après le retrait de §5.3. |

### 5.6 Emballage

Le `.targets` va dans `build/` seulement, pas `buildTransitive/` : seul le
projet qui déclare `<OkfBundle>` en a besoin, et il référence le paquet
directement (`PrivateAssets` contient `build` par défaut). Une bibliothèque
qui embarque un bundle est lue depuis une application par l'`Assembly` de la
bibliothèque, sans `.targets` côté application.

## 6. Tests

Toutes les comparaisons qui passent par une validation ou un audit utilisent
la même horloge fixe, `FixedClock` à **2026-11-01T00:00:00Z**, des deux côtés
et dans la sonde AOT : `ConceptAudit` enregistre l'instant d'évaluation, et les
`stale_after: 2026-12-31` d'`acme_retail` changeraient sinon le résultat avec
le calendrier, sans changement de code.

### 6.1 Tests du provider

Dans `tests/OKF4net.Tests/Providers/EmbeddedResources/` :

- **Contrat** : chaque chemin d'écriture refuse — directement (`WriteAsync`,
  `DeleteAsync`) et par `BundleConceptWriter`, `IndexGenerator` et
  `OkfBundleTools` (dont les outils d'écriture ne doivent pas être proposés) ;
  toutes les tâches sont achevées (`CompletionTrackingProvider`) ; le préfixe
  filtre à la frontière de segment ; un préfixe inconnu, vide ou contenant `/`
  lève ; un assembly dynamique est refusé ; un assembly chargé par
  `Assembly.Load(byte[])` est servi ; deux assemblies de même nom complet et de
  contenus différents ont des `Id` distincts ; `MaxKeyBytes` abaissé laisse le
  chargement aboutir avec un problème local, comme le filesystem ; la version
  est stable et un `expected` différent lève ; `GetBundlePrefixes` liste les
  préfixes et ignore `okf:/x`.
- **D7** : `TryResolveResource` et `ReadResourceText(string)` lèvent
  `NotSupportedException` sur le provider embarqué et sur
  `InMemoryBundleProvider` ; le test frère de `NoResidualFileIoTests`
  décrit en §4.4.

### 6.2 Tests du `.targets`

- **Répertoire d'actifs dans le projet de tests**
  (`tests/OKF4net.Tests/Providers/EmbeddedResources/Assets/names/`, donc sous
  les globs par défaut du projet) : `a.b.c.md`, `glossary.fr.md`,
  `x.en-US.md`, espaces, `% ; @ $ # ' ( ) ,`, `$(Property).md`,
  `%(Metadata).md`, `@(Items).md`, `pct%25.md`, `pct%3B.md`, non-ASCII en NFC,
  `.hidden`, `.dir/`, des répertoires imbriqués, un `.licx`, un `.restext`, un
  `.resx` brut et un `.cs` **volontairement invalides**. Assertion : les clés
  et les octets servis par le provider embarqué sont égaux à ceux que sert
  `FileSystemBundleProvider` (son `EnumerateAsync`, pas un listing brut du
  disque) sur le même répertoire. Le build qui réussit prouve à lui seul que le
  `.cs` n'est pas compilé ni le `.resx` passé à ResGen. Rien de NFD, de point
  final ni de caractère réservé à Windows n'est versionné (un checkout les
  altérerait ou échouerait).
- **Builds en processus enfant** (`TargetsBuildTests`) : chaque test génère
  dans un répertoire temporaire un petit projet qui importe le `.targets`
  **exactement à la place de l'import NuGet**, lance `dotnet build`, puis lit
  l'assembly produit dans un `AssemblyLoadContext` collectible. Un import
  « après `Sdk.targets` » ne convient pas : il passe après
  `Directory.Build.targets`, que l'import NuGet précède, et verrait donc un
  `OkfBundle` tardif que le vrai paquet ne voit pas (vérifié :
  `SEEN_AT_IMPORT=knowledge/` contre `SEEN_AT_IMPORT=` sous l'import NuGet).
  Moyen retenu : un fichier `obj/<projet>.csproj.okf.targets` qui importe le
  `.targets`, apparié par le même import générique que `nuget.g.targets`
  (`$(MSBuildProjectExtensionsPath)$(MSBuildProjectFile).*.targets`, dans
  `Microsoft.Common.targets`) et que la restauration ne réécrit pas ; le
  prototype (§8) vérifie par la même sonde `SEEN_AT_IMPORT` qu'il se place
  comme l'import NuGet, et à défaut les tests consomment le vrai paquet,
  emballé une fois par exécution vers un flux local (avec `OKF4net`) et
  restauré dans un `NUGET_PACKAGES` isolé. Cas :
  - un arbre de noms générés, légaux sur l'OS courant — sous Linux et macOS en
    plus : point final, espaces en tête et en fin, NFD, `* ? : < >` — comparé
    au provider filesystem ;
  - un projet qui référence le `.targets` **sans** `OkfBundle` n'embarque
    aucune ressource ;
  - `Include="knowledge"` sans `/` final fonctionne ;
  - chaque code `OKFEMB001` à `OKFEMB006` est levé sur son cas : préfixe
    invalide ; répertoire vide ; préfixes ne différant que par la casse ; sous
    Linux, jumeaux de casse, `"`, `|`, `\` ; un lien (jonction sous Windows,
    qui ne demande pas de privilège, symlink de fichier et de répertoire
    ailleurs) ; un `OkfBundle` déclaré dans `Directory.Build.targets`.
  - **confinement du parcours** : une jonction (ou un symlink de répertoire)
    vers un répertoire extérieur contenant un sous-répertoire, une cible
    inaccessible, et un cycle (lien vers un ancêtre) donnent tous `OKFEMB005`
    sans que la cible ait été listée — vérifié par une cible extérieure
    illisible ou par l'absence de son contenu dans la sortie détaillée du
    build — et le build se termine.

### 6.3 Parité (critère d'acceptation du ticket)

Le projet de tests importe le `.targets` depuis `src/`, à la fin de son corps,
après ses items `OkfBundle`, et embarque les bundles de fixtures (`okf_v02`,
`okf_v02_computation`, `okf_v02_reserved`, `appendix_a`) et
`bundles/acme_retail`. Pour chacun, chargé par `FileSystemBundleProvider` puis
par le provider embarqué :

- **objets** : mêmes clés, mêmes octets, exhaustivement ;
- **chargement** : `OkfVersion`, `IndexKeys`, `LogKeys`, `ParseProblems`
  (sans chemin d'affichage), `PortabilityIssues` (par `Key` et `Reason`) ;
- **concepts** : clé, id, frontmatter, corps ;
- **diagnostics** de `BundleValidator` : code, sévérité, clé, `Concept`,
  `Field`, message — jamais `Path`, projection d'affichage qui diffère
  nécessairement ;
- `ConceptAudit` (horloge fixe), le graphe, `ConceptSearch` sur quelques
  requêtes, par clé et sans chemin d'affichage ;
- **`ExecutableResources`** : par clé et état, **sans** les jetons de version
  (SHA-256 d'un côté, MVID de l'autre), puis une relecture épinglée au jeton de
  chaque côté dont les octets sont comparés ;
- **index générés** : `IndexGenerator` n'a pas d'API publique de rendu sans
  écriture ; chaque provider est donc enveloppé dans un double de test
  inscriptible qui délègue les lectures et **capture** les écritures en
  mémoire, et l'on compare les écritures capturées. Aucune fixture n'est
  écrite.

Les fixtures ne sont pas modifiées : elles sont seulement embarquées (aucun
arbitrage au sens de `tests/fixtures/README.md`). La preuve que les globs ne
compilent pas un `.cs` de bundle est celle de §6.2, pas celle-ci : ces bundles
sont hors du répertoire du projet et n'ont ni `.cs` ni `.resx`.

### 6.4 Job CI `embedded bundle AOT smoke test`

Même matrice de RID que `aot-publish` (`linux-x64`, `osx-arm64`, `win-x64`).

1. `dotnet pack` de **`OKF4net` et du provider** vers un flux local, avec une
   version propre au run : `<Version de Directory.Build.props>-ci.<run_id>.<run_attempt>`.
   Le `ProjectReference` vers `OKF4net` devient une dépendance de paquet à la
   même version : le cœur doit être dans le même flux, sinon la restauration
   échoue ou prend un cœur publié.
2. `NUGET_PACKAGES` isolé ; un `nuget.config` généré listant le flux local
   **et** nuget.org (`Microsoft.DotNet.ILCompiler` et les packs runtime en
   viennent ; un `--source` seul remplacerait les sources).
3. Restauration de `tests/EmbeddedBundleAotProbe/` avec le RID et
   `PublishAot=true`, la version épinglée ; contrôle dans
   `project.assets.json` que `OKF4net` et le provider sont résolus à la version
   du run ; puis `dotnet publish --no-restore` avec les mêmes propriétés.
4. La sonde (hors de `OKF4net.sln`, elle hérite du `Directory.Build.props` du
   dépôt, `TreatWarningsAsErrors` compris) embarque `bundles/acme_retail` et
   imprime un résumé déterministe — clés, nombre de concepts, diagnostics
   (code, sévérité, clé) et décompte d'audit, horloge fixe. Elle est exécutée
   **deux fois** : depuis un répertoire temporaire vide, sur le bundle
   embarqué ; puis avec `--filesystem <chemin du checkout>`, sur le même
   bundle lu par `FileSystemBundleProvider`, dans le même binaire AOT. Le job
   compare les deux sorties. Aucun fichier attendu versionné : un snapshot
   d'`acme_retail` (copie amont, dérive connue) n'aurait pas de régime dans
   `tests/fixtures/README.md`.

## 7. Livraison et documentation

- `src/OKF4net.Providers.EmbeddedResources/` : référence `OKF4net` seulement,
  zéro `PackageReference`, bloc d'emballage comme `OKF4net.Catalog`, le
  `.targets` sous `build/` dans le paquet, README de paquet (recette, domaine
  des assemblies, codes `OKFEMB00x`, `Location` d'affichage, fabrique de
  catalogue écrite par l'hôte, renvoi à #236).
- `release.yml` : une étape `dotnet pack` de plus.
- `ci.yml` : le job de §6.4.
- `CLAUDE.md` : le projet rejoint la liste BCL-only des règles dures ; une
  puce d'architecture (dont le `.targets`, code non-.NET dont les gardes
  exécutables sont `TargetsBuildTests` et le job AOT).
- `README.md` (racine) et `CHANGELOG.md` (dont D7, changement de comportement
  de deux membres `[Obsolete]`).
- `graphify update .` après les modifications de code.

## 8. Non vérifié à ce jour

Par les revues : AOT sur `win-x64` et `osx-arm64` (le job de §6.4 le
couvrira), macOS en général, le MSBuild .NET Framework de Visual Studio,
l'interaction avec `dotnet format`.

**Prototype Windows du 2026-11-12** (SDK 10, hors dépôt) : le parcours récursif
par la tâche `MSBuild` signale une jonction vers l'extérieur et un cycle sans
lister leur cible, et traverse un répertoire `sub;semi` (enfants joints sur
NUL par `Char.ConvertFromUtf32(0)`, puis `Escape(…).Split(NUL)`) ; 20 noms
pénibles embarqués octet pour octet, sans satellite ; `OKFEMB001`, `003`,
`005`, `006` levés sur leur cas ; inerte sans `OkfBundle`. Restent Linux et
macOS, couverts par la porte de la tâche 1 du plan.

**Première étape obligatoire du plan : un prototype du `.targets`**, avant
toute autre tâche, qui démontre :

1. le parcours des liens qui inspecte chaque répertoire avant d'y descendre,
   en MSBuild pur, sur une jonction vers l'extérieur, une cible inaccessible
   et un cycle (§5.1, point ouvert) — sinon arrêt et D2 rouvert avec le
   mainteneur ;
2. que l'import par `obj/<projet>.csproj.okf.targets` se place comme l'import
   NuGet (`SEEN_AT_IMPORT` vide pour un `OkfBundle` de
   `Directory.Build.targets`) — sinon les tests du `.targets` consomment le
   vrai paquet (§6.2) ;
3. que le retrait de §5.3 ne dépend pas du nombre d'items `Compile` du projet (cas à 120 sources) et n'énumère pas le disque sous le
   répertoire du bundle (une comparaison de chemins sur les items, pas un parcours) ;
4. le nommage, les codes `OKFEMB001` à `OKFEMB006` et la condition
   `'@(OkfBundle)' != ''` sur les cas de §6.2.
