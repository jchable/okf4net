# Provider de bundle embarqué dans un assembly — design

Date : 2026-11-12
Statut : **validé section par section par le mainteneur le 2026-11-12**
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

## 3. Conformité à la spécification

SPEC §3 : « A bundle is a directory tree of markdown files » et « A bundle MAY
be distributed as » un dépôt git, une archive, un sous-répertoire — liste
permissive, non exhaustive. Le provider présente exactement l'arbre du
répertoire source (mêmes chemins relatifs, mêmes octets), et le cœur le lit
par la même couture que le filesystem. Aucune divergence : **pas d'entrée dans
`docs/spec-conformance/`**. La parité (§6.2) est la preuve que l'arbre lu est
le même.

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
    /// Lève ArgumentException si aucune ressource ne porte « okf:<prefix>/ ».
    public EmbeddedResourceBundleProvider(Assembly assembly, string prefix);

    /// Les préfixes présents dans l'assembly, triés ordinalement.
    public static IReadOnlyList<string> GetBundlePrefixes(Assembly assembly);
}
```

Un préfixe inconnu lève à la construction : c'est presque toujours une faute
de frappe ou un `<OkfBundle>` oublié, qu'un bundle vide masquerait. Un préfixe
vide ou contenant `/` lève aussi (`ArgumentException`).

### 4.3 Contrat

| Membre | Comportement |
|---|---|
| `Capabilities` | `CanWrite = false`, `CompletesSynchronously = true`. |
| `Id` | `embedded:<nom complet de l'assembly>/<prefix>` — stable, non secret, distinct d'un assembly à l'autre et d'un préfixe à l'autre. |
| `Location` | `embedded:<nom simple de l'assembly>/<prefix>` — affichage ; la règle d'affichage (`BundleDisplayPath`) y ajoute séparateur et segments comme pour le filesystem. `Assembly.Location`, vide en single-file, n'est jamais utilisé. |
| `NativeConstraints` | `FileDirectoryConflict \| WellFormedUnicode`. Les noms sont stockés en UTF-8 dans les métadonnées (deux surrogates isolés y deviendraient le même nom) ; et un `LogicalName` écrit à la main peut définir à la fois `a` et `a/b`, qu'aucun arbre ne peut tenir : le cœur refuse alors ce bundle plutôt que de le charger. |
| `GetVersionAsync` | Jeton dérivé du `ModuleVersionId` (MVID) du module manifeste : le contenu ne peut pas changer pendant la vie du processus. |
| `EnumerateAsync` | La liste des noms est lue une fois (`GetManifestResourceNames()`) à la construction, filtrée sur `okf:<prefix>/`, préfixe retiré, triée ordinalement (ordre déterministe). Le filtrage par `prefix` de clé se fait à la frontière de segment. Le provider borne sa propre énumération par `MaxEntries`, `MaxDepth` et `MaxKeyBytes` (`BundleLimitExceededException`), comme le fait le filesystem. `Length` est fourni quand il est connu sans lecture. |
| `ReadAsync` | `GetManifestResourceStream` sur le nom exact (recherche ordinale). `Length` = longueur du flux. Absent → `BundleObjectNotFoundException`. Avec `expected` différent du jeton → `BundleVersionMismatchException`. |
| `ExistsAsync` | Appartenance exacte (ordinale) à la liste filtrée. |
| `WriteAsync` / `DeleteAsync` | Tâche déjà en faute avec `BundleReadOnlyException`, rien n'est tenté (même forme que `ReadOnlyBundleProvider`). |

Version d'objet : le même jeton MVID pour tous les objets. Il suffit aux
comparaisons `expected` (épinglage d'attestation, `Bundle.ExecutableResources`)
parce que le contenu est immuable pour le processus ; il change dès que
l'assembly est reconstruit avec un contenu différent.

Toutes les opérations rendent une tâche déjà achevée : le provider sert les
points d'entrée synchrones (`SyncBridge`) sans attente.

### 4.4 Hors périmètre (YAGNI)

Pas de rafraîchissement (`BundleRefreshPolicy.Manual` suffit, le contenu est
figé), pas d'`IProviderScope`, pas de chargement d'assembly par nom, pas de
fabrique de catalogue (#236), pas de lecture d'archive zip.

## 5. Côté build : le `.targets` du paquet

### 5.1 Approche

`build/OKF4net.Providers.EmbeddedResources.targets`, en **MSBuild pur**
(transformations d'items, fonctions de propriétés), sans assembly de tâche.
L'alternative — une tâche compilée — gérerait plus confortablement
l'échappement, mais imposerait de livrer une DLL compilée contre
`Microsoft.Build.Utilities.Core` et chargée tant par le MSBuild .NET Framework
de Visual Studio que par celui de `dotnet` : une dépendance et une matrice de
compatibilité pour peu de gain. Si un nom ne peut pas être reproduit fidèlement
en MSBuild pur, le build **échoue** en nommant le fichier (le ticket : « round-trip
every path … or reject it explicitly »).

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

### 5.3 Ce que fait la cible

Une cible exécutée avant `PrepareResourceNames`, par `OkfBundle` :

1. Prend **tous** les fichiers sous le répertoire, récursivement, sans filtre —
   comme le provider filesystem charge tout.
2. Ajoute pour chacun un `EmbeddedResource` avec
   `LogicalName="okf:<Prefix>/<chemin relatif, séparateur />"`,
   `WithCulture="false"` et `Type="Non-Resx"`. Deux pièges désarmés :
   sans `WithCulture="false"`, MSBuild déplacerait un `glossary.fr.md` dans un
   **assembly satellite** `fr` ; et un `.resx` du bundle serait compilé par
   ResGen au lieu d'être embarqué tel quel.

À l'évaluation, le `.targets` retire aussi le répertoire du bundle des globs
par défaut `Compile`, `EmbeddedResource`, `None` et `Content` : sinon un script
sanctionné `.cs` rangé dans le bundle (§10) serait **compilé dans l'hôte**, et
un `.resx` serait embarqué deux fois.

### 5.4 Erreurs de build

Codes `OKFEMB00x`, chacune nommant l'item ou le fichier en cause :

| Code | Cas |
|---|---|
| `OKFEMB001` | `Prefix` absent ou invalide (vide, ou contenant `/`). |
| `OKFEMB002` | Répertoire inexistant, ou sans aucun fichier. |
| `OKFEMB003` | Deux `OkfBundle` avec le même préfixe. |
| `OKFEMB004` | Un fichier dont le nom ne peut pas être reproduit fidèlement — réservé au cas où les tests en trouveraient un ; sinon le code n'est pas créé. |

### 5.5 Emballage

Le `.targets` va dans `build/` seulement, pas `buildTransitive/` : seul le
projet qui déclare `<OkfBundle>` en a besoin, et il référence le paquet
directement. Une bibliothèque qui embarque un bundle est lue depuis une
application par l'`Assembly` de la bibliothèque, sans `.targets` côté
application.

## 6. Tests

### 6.1 Tests unitaires

Dans `tests/OKF4net.Tests/Providers/EmbeddedResources/` :

- **Contrat** : chaque chemin d'écriture refuse — directement
  (`WriteAsync`, `DeleteAsync`) et par `BundleConceptWriter`, `IndexGenerator`
  et `OkfBundleTools` (dont les outils d'écriture ne doivent pas être
  proposés) ; toutes les tâches sont achevées (`CompletionTrackingProvider`) ;
  le préfixe filtre à la frontière de segment ; un préfixe inconnu, vide ou
  contenant `/` lève ; les bornes sont honorées ; la version est stable et un
  `expected` différent lève ; `GetBundlePrefixes` liste les préfixes.
- **Fidélité des noms** : un répertoire d'actifs aux noms délibérément
  pénibles — `a.b.c.md`, `glossary.fr.md`, espaces, `% ; @ $ # '`, non-ASCII
  en NFC, `.hidden`, un `.resx`, un `.cs`, des répertoires imbriqués. Assertion :
  le listing embarqué est égal, octet pour octet, au listing filesystem du même
  répertoire. Pas de nom NFD dans le dépôt (un checkout git sur macOS
  l'altérerait).

### 6.2 Parité (critère d'acceptation du ticket)

Le projet de tests importe le `.targets` directement depuis `src/` et embarque
les bundles de fixtures (`okf_v02`, `okf_v02_computation`, `okf_v02_reserved`,
`appendix_a`) et `bundles/acme_retail`. Pour chacun, chargé par le filesystem
puis par le provider embarqué, on compare :

- les concepts (clé, id, frontmatter, corps) ;
- les diagnostics de `BundleValidator` (code, sévérité, clé, message) ;
- `ConceptAudit`, le graphe, `ConceptSearch` sur quelques requêtes ;
- le relevé `ExecutableResources` ;
- le contenu des index générés (rendu sans écriture).

Le projet de tests gardant les globs par défaut, cette parité prouve en même
temps qu'un `.cs` ou un `.resx` d'un bundle n'est pas compilé. Les fixtures ne
sont pas modifiées : elles sont seulement embarquées (aucun arbitrage au sens
de `tests/fixtures/README.md`).

### 6.3 Job CI `embedded bundle AOT smoke test`

Même matrice de RID que `aot-publish`. Le job fait `dotnet pack` du provider
vers un flux local, puis publie `tests/EmbeddedBundleAotProbe/` en
`PublishAot`. Ce projet, hors de `OKF4net.sln`, consomme le **vrai paquet**
(donc le vrai `.targets`, l'élagage et l'AOT réels) et embarque
`bundles/acme_retail`. Le job l'exécute depuis un répertoire temporaire vide —
aucun bundle sur disque — et compare son résumé (nombre de concepts,
diagnostics par sévérité, code de sortie) à un fichier attendu versionné.

## 7. Livraison et documentation

- `src/OKF4net.Providers.EmbeddedResources/` : référence `OKF4net` seulement,
  zéro `PackageReference`, bloc d'emballage comme `OKF4net.Catalog`, README de
  paquet (recette, codes `OKFEMB00x`, fabrique de catalogue écrite par l'hôte,
  renvoi à #236).
- `release.yml` : une étape `dotnet pack` de plus.
- `CLAUDE.md` : le projet rejoint la liste BCL-only des règles dures ; une
  puce d'architecture.
- `README.md` (racine) et `CHANGELOG.md`.
- `graphify update .` après les modifications de code.
