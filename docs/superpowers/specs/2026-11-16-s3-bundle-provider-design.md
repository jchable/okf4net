# Provider de bundle S3 — design

Date : 2026-11-16
Statut : **révision 2**, à relire par le mainteneur. La révision 1 (design validé
section par section le 2026-11-15) a reçu une revue interne et une revue externe
le 2026-11-16 ; la révision 2 les intègre, avec cinq arbitrages du mainteneur du
même jour (D15, D5, D1, D8, et la consignation des décisions sur les tickets).
Ticket : [#142](https://github.com/jchable/okf4net/issues/142). Suites hors lot :
[#238](https://github.com/jchable/okf4net/issues/238) (`okf-mcp` sur S3),
[#239](https://github.com/jchable/okf4net/issues/239) (fabrique de catalogue S3,
emplacement à trancher avec [#236](https://github.com/jchable/okf4net/issues/236)).

Prérequis de lecture : la couture provider
([`2026-10-07-bundle-provider-seam-design.md`](2026-10-07-bundle-provider-seam-design.md)),
son contrat asynchrone
([`2026-11-03-async-bundle-provider-design.md`](2026-11-03-async-bundle-provider-design.md))
et le provider embarqué
([`2026-11-12-embedded-resource-bundle-provider-design.md`](2026-11-12-embedded-resource-bundle-provider-design.md)),
dont ce document reprend le vocabulaire (clé, contraintes natives, bornes,
image, handle) et la forme.

**Sources des décisions.** Le cadrage de sécurité (#139, 2026-10-06), l'atomicité
et le rafraîchissement (#139, 2026-10-07), et la règle de priorité des erreurs
(#139, 2026-10-12) s'appliquent sans être rejoués. Les arbitrages de ce lot
(D1 réarbitré, `Location`, changements du cœur, politique de version,
épinglage) et l'inventaire des écarts au commentaire de #142 du 2026-10-06 sont
consignés en commentaire de #142 ; les sept points que #141 a tranchés le
2026-11-15 pour tout provider de paquet, en commentaire de #141 (§8).

## 1. But

Servir un bundle OKF stocké dans **Amazon S3 ou un store compatible S3** — un
bucket et un préfixe — à un hôte (agent, catalogue écrit à la main, et plus tard
`okf-mcp` #238), partagé entre plusieurs machines et géré hors du dépôt. C'est le
premier backend réellement distant du contrat `IBundleProvider`, le premier
réellement asynchrone (`CompletesSynchronously = false`) et le premier qui exige
une dépendance tierce.

**Lecture seule.** Déclarer un jour `CanWrite` est une décision à part (§7.3).

## 2. Décisions

| # | Question | Décision |
|---|---|---|
| D1 | Client S3 | **SDK AWS pour .NET, `AWSSDK.S3`**, plutôt qu'un client `HttpClient` + SigV4 écrit à la main. Arbitré par le mainteneur le 2026-11-15 sur deux prototypes mesurés (annexe A) ; il remplace la décision du 2026-10-05, prise sans mesure. Exception de dépendance propre au projet, comme `Catalog.Hosting` : rien d'autre dans `src/` ne référence ce projet. **Politique de version (2026-11-16)** : le paquet déclare un **minimum** (`AWSSDK.S3 >= 4.1.0`, qui tire `AWSSDK.Core [4.1.0, 5.0.0)`), sans épinglage exact qui créerait des conflits chez un hôte utilisant d'autres paquets `AWSSDK.*`. La sécurité ne dépend d'aucune version : elle est vérifiée à l'exécution (D2, D3) et par des tests de sensibilité ; T3 compile la sonde contre les versions minimales **et** contre les dernières publiées. |
| D2 | Qui construit le client, et comment | **Le provider**, à partir d'un `AWSCredentials` et d'une `S3BundleSource` fournis par l'hôte ; aucune surcharge n'accepte un `IAmazonS3`. Le provider ne lit jamais l'environnement, un fichier ou la chaîne d'identifiants par défaut (#139, §3) et **fixe explicitement tout réglage que le SDK irait sinon chercher dans l'environnement ou `~/.aws/config`** : endpoints configurés ignorés, mode et nombre de réessais, FIPS, dualstack, mode de configuration par défaut, checksums, proxy désactivé, un `HttpClient` propre à chaque provider (§4.4). Le trafic des fournisseurs d'identifiants (STS, IMDS, SSO) appartient à l'hôte : D3 ne le couvre pas. |
| D3 | Sorties réseau | **Garde d'hôte obligatoire, non désactivable**, sur un ensemble d'**hôtes exacts** (jamais un suffixe) calculé depuis `S3BundleSource` seule : toute requête S3 vers une autre origine est refusée avant l'envoi, avec une marque identifiable. Mesuré nécessaire : le SDK suit une 307 vers tout hôte `*.amazonaws.com`, la re-signe et y envoie le jeton de session (annexe A). **La détection de région du SDK, qui envoie un `HEAD` par un transport à part (`BucketRegionDetector`, `AmazonS3HttpUtil`, présents dans le binaire 4.1.0), doit être empêchée ou passer par le transport gardé** : c'est le premier point de la porte §9, et le plan s'arrête s'il ne tient pas. |
| D4 | Écriture | **Lecture seule** : `WriteAsync`/`DeleteAsync` rendent `BundleReadOnlyException(Id)` sans rien tenter. |
| D5 | `Location` et `Id` | **Le nom du bucket n'atteint jamais le modèle** (#139, §3, sans amendement ; arbitrage du 2026-11-16). `Location = "s3:<nom>/<prefix>"`, où `<nom>` est le `DisplayName` donné par l'hôte, ou à défaut les 8 premiers hex de l'`Id`. `Id = "s3:" + 32 hex` d'un SHA-256 de la configuration effective (§4.3) : stable, non vide, opaque. |
| D6 | Domaine de la configuration | `Prefix` : vide (tout le bucket) ou une suite de segments du socle `BundleKey`, **en Unicode bien formé**. `DisplayName` : un segment du socle en Unicode bien formé, sans `:`. `Bucket` : un nom de bucket S3 d'usage général (3 à 63 caractères, minuscules, chiffres, `-`, `.`) ; ARN, points d'accès, alias et buckets de répertoire (`--x-s3`, `--ol-s3`, `-s3alias`) refusés. `ServiceUrl` : une **origine** seule (schéma, hôte, port), sans userinfo, chemin, query ni fragment. Clé S3 = `<prefix>/<key>`, ou `<key>` sans préfixe : injective, à frontière de segment. |
| D7 | Erreurs | `S3RequestException : BundleProviderException` pour tout échec du transport ou du serveur : le cœur en fait un **échec de chargement**, jamais un état local (§4.6, règle de priorité de #139 du 2026-10-12). Les exceptions typées du contrat pour un objet absent, une version différente, un refus propre à une clé. Message écrit par la bibliothèque, à vocabulaire fermé ; aucun texte venu du serveur. |
| D8 | Versions | **Version d'objet = `sha256:` + SHA-256 des octets lus** (arbitrage du 2026-11-16) : l'épinglage ne dépend ni d'`If-Match` ni d'un ETag MD5, et a la même force que sur le filesystem. **Version du bundle = `s3v1:`** + SHA-256 d'un encodage canonique du listing, avec les ETag : elle ne sert qu'à la fraîcheur. Pas d'`IBundleChangeNotifier`. |
| D9 | Listing | `EncodingType=url` toujours, décodé une seule fois ; corps de réponse borné **avant** désérialisation ; **échec fermé** sur toute incohérence (clé hors `<prefix>/`, ordre UTF-8 non strictement croissant, jeton répété ou démesuré, page tronquée sans jeton, page plus grande que demandée). |
| D10 | Internes du cœur | **Aucun.** Le listing est un itérateur `async` ; le cœur n'accorde aucun `InternalsVisibleTo` à ce projet (garde de test). Ce projet accorde les siens au seul projet de tests. |
| D11 | Transport | `https://` exigé ; `http://` seulement avec `AllowInsecureHttp = true`. |
| D12 | Checksums | Réglage explicite dans `S3BundleSource` (par défaut : ceux du SDK, mesurés acceptés par RustFS) ; jamais lu de l'environnement. |
| D13 | Périmètre | Bibliothèque seule. Pas de fabrique de catalogue (#239), pas d'option `okf-mcp` (#238), pas de notification S3, pas d'écriture. |
| D14 | Tests sans CI | Catégorie **`S3Integration`**, distincte de `ContainerIntegration`, contre `docker.io/rustfs/rustfs:1.0.3` (MinIO n'est plus disponible sur Docker Hub). `ci.yml` et `release.yml` excluent les deux catégories. |
| D15 | Changements du cœur | **Deux, en tête du plan, en TDD, dans cette branche** (arbitrage du 2026-11-16) : (a) `LoadCoreAsync` lit la version du bundle **avant** de lister ; (b) `ResolveResourceKeyAsync` ne rend `Unsafe` que sur un refus d'accès et laisse remonter toute autre défaillance du provider (§4.7). |
| D16 | Concurrence | Le provider borne ses requêtes en vol (`MaxConcurrentRequests`, 16 par défaut), flux de corps compris : c'est sa responsabilité (spec de la couture, l. 1021 ; spec asynchrone §3.6 ; #139 §4). |

## 3. Conformité à la spécification

SPEC §3 : un bundle « MAY be distributed as » un dépôt, une archive, un
sous-répertoire — liste permissive. Le provider présente le même arbre de noms
et les mêmes octets qu'un répertoire téléversé tel quel ; la parité (T2) en est
la preuve. Le stockage S3 tient des noms qu'un système de fichiers ne tient pas
(NFC et NFD distincts, `\`, `?`), et en refuse d'autres côté serveur : ce sont
des questions de clés, réglées par les trois couches de `BundleKey`, pas de la
spec OKF. **Pas d'entrée dans `docs/spec-conformance/`** : les deux changements
du cœur (D15) corrigent l'implémentation du contrat de la couture, ils ne
s'écartent pas de la spec.

## 4. Le provider

### 4.1 Projet et paquet

`src/OKF4net.Providers.S3/`, paquet `OKF4net.Providers.S3`, namespace
`OKF4net.Providers.S3`. Références : `OKF4net` (`ProjectReference`) et
**exclusivement** `AWSSDK.S3` (`PackageReference`, minimum 4.1.0 ;
`AWSSDK.Core`, `AWSSDK.Extensions.Numerics` et `System.IO.Hashing` en
transitifs). `IsAotCompatible`, bloc d'emballage comme le provider embarqué,
README de paquet. Dans `OKF4net.sln` ; étape `dotnet pack` dans `release.yml`.

### 4.2 API publique

```csharp
namespace OKF4net.Providers.S3;

public sealed record S3BundleSource
{
    public required string Bucket { get; init; }
    public string Prefix { get; init; } = "";
    public string? DisplayName { get; init; }
    public RegionEndpoint? Region { get; init; }     // AWS ; exclusif de ServiceUrl
    public Uri? ServiceUrl { get; init; }            // origine d'un store compatible ; exclusif de Region
    public bool ForcePathStyle { get; init; }
    public bool AllowInsecureHttp { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public int MaxConcurrentRequests { get; init; } = 16;
    public long MaxObjectBytes { get; init; } = BundleLimits.Default.MaxObjectBytes;
    public S3ChecksumMode Checksums { get; init; } = S3ChecksumMode.WhenSupported;
}

public enum S3ChecksumMode { WhenSupported, WhenRequired }

public sealed class S3BundleProvider : IBundleProvider, IDisposable
{
    /// Aucune E/S. ArgumentException sur toute valeur hors du domaine de D6, ni
    /// ou les deux de Region/ServiceUrl, http:// sans AllowInsecureHttp, un délai
    /// ou une borne <= 0.
    public S3BundleProvider(AWSCredentials credentials, S3BundleSource source);

    /// Libère le client S3 et le HttpClient que le provider a construits.
    public void Dispose();
}

/// Une requête S3 a échoué. Message écrit par la bibliothèque, à vocabulaire
/// fermé ; aucun texte venu du serveur.
public sealed class S3RequestException : BundleProviderException
{
    public S3Operation Operation { get; }     // List, Read
    public S3FailureKind Kind { get; }        // Timeout, Throttled, ServerError, ClientError, Transport, RefusedHost, InvalidResponse, Credentials, Integrity
    public int? StatusCode { get; }
}
```

Le provider n'a **qu'un chemin de construction** du client et de son
`HttpClient` ; un constructeur **interne**, pour les tests, ne fait qu'y injecter
le `HttpMessageHandler` intérieur, sous le garde D3.

L'hôte possède les identifiants (tout `AWSCredentials`) et le provider ; le
provider possède son client. Un `BundleHandle` ne libère pas son provider. Un
appel sur un provider libéré échoue en `S3RequestException` (`Kind =
Transport`), jamais en `ObjectDisposedException` brute.

### 4.3 Contrat

| Membre | Comportement |
|---|---|
| `Capabilities` | `CanWrite = false`, `CompletesSynchronously = false`. |
| `Id` | `"s3:" + hex(SHA-256(UTF-8("s3v1\n" + endpoint + "\n" + bucket + "\n" + prefix)))[..32]`, où `endpoint` vaut `aws:<nom système de la région>` ou l'origine de `ServiceUrl` (schéma et hôte en minuscules, port explicite) suivie de `|path` ou `|vhost` selon `ForcePathStyle`. Deux sources de même configuration effective ont le même `Id` (ce que veut la déduplication du catalogue, `FusedResolverEngine.cs:141`), deux sources différentes jamais. |
| `Location` | `s3:<DisplayName ou 8 hex de l'Id>` suivi de `/<prefix>` quand le préfixe n'est pas vide. Affichage seulement ; jamais le bucket ni l'endpoint (D5). |
| `NativeConstraints` | `BundleKeyConstraints.WellFormedUnicode`. |
| `EnumerateAsync` | Itérateur `async` sur `ListObjectsV2` (`Prefix = <prefix>/` plus le préfixe de clé demandé suivi de `/`, `MaxKeys = 1000`, `EncodingType=url`), une page à la fois : la borne d'entrées du cœur l'arrête par `DisposeAsync`. Nom brut = clé décodée moins `<prefix>/` ; `Length = Size` ; `Version` = `null` (la version d'objet est celle de la lecture, D8). Aucune validation de nom (le cœur la fait) ; D9 sur toute incohérence. |
| `GetVersionAsync` | Listing complet par le même itérateur, borné par `limits.MaxEntries` et `limits.MaxDepth` (comptés sur **tous** les noms, invalides et non Markdown compris ; `BundleLimitExceededException`), haché **en flux, dans l'ordre du listing** (§4.5). |
| `ReadAsync` | `GetObject` sans condition. Un `Content-Length` supérieur à `MaxObjectBytes` est refusé avant la lecture du corps (`BundleObjectTooLargeException`) ; le corps est ensuite lu **en entier** sous le délai de l'opération, en comptant les octets contre la même borne, puis haché : version = `sha256:<hex>`. Avec `expected` différent : `BundleVersionMismatchException`. Le `BundleObject` rendu porte un `MemoryStream` sur ces octets et `Length` exact. 404 : `BundleObjectNotFoundException`. 403 : `BundleAccessRefusedException(key, …)`. |
| `ExistsAsync` | `ListObjectsV2(Prefix = <clé S3>, MaxKeys = 1, EncodingType=url)` : `true` si la première clé rendue est **exactement** la clé (elle trie avant toute clé qu'elle préfixe), `false` sinon. Même droit que le listing, même sémantique que le contrat : un refus n'est jamais `false` (`S3RequestException`). |
| `WriteAsync` / `DeleteAsync` | `ValueTask.FromException(new BundleReadOnlyException(Id))`. |

Le cœur, pas le provider, applique `MaxKeyBytes`, la validation des clés, sa
propre concurrence de `LoadAsync` et D7 de #141 (§8). Un chargement coûte la
version du bundle (un listing), le listing des noms (un second) et un `GET` par
objet lu ; aucun cache ne les fusionne (YAGNI, à rouvrir sur mesure).

**Pas de transaction entre objets.** Un objet supprimé entre le listing et sa
lecture fait échouer le chargement (`BundleObjectNotFoundException` →
`BundleLoadException`) ; l'image précédente reste servie, marquée périmée. Un
objet modifié entre deux lectures donne une image qui mêle deux états du bucket,
comme sur le filesystem ; la version du bundle, lue en premier (D15 a), garantit
qu'un tel mélange est rechargé à l'accès suivant sous `OnAccess` et `Every`.

**Mémoire.** Pas de lecture en flux (D8) : chaque objet est entier en mémoire
dans le provider avant que le cœur ne le décode, soit au pire
`MaxConcurrentRequests × MaxObjectBytes` en vol. Le README conseille d'abaisser
`MaxObjectBytes` (et la borne du cœur avec) pour un bucket non fiable.

### 4.4 Garde d'hôte et configuration du SDK (D2, D3)

**Configuration hermétique.** `AmazonS3Config` reçoit explicitement :
`IgnoreConfiguredEndpointUrls = true` ; `RetryMode = Standard` et
`MaxErrorRetry = 2` (3 tentatives) ; `UseFIPSEndpoint = false`,
`UseDualstackEndpoint = false` ; `DefaultConfigurationMode = Standard` ; la
région (`Region`, ou `us-east-1` d'authentification pour `ServiceUrl`) ;
`ForcePathStyle` ; `RequestChecksumCalculation` et `ResponseChecksumValidation`
depuis `Checksums` ; `Timeout` infini (le délai est celui de l'opération,
§4.5) ; un `HttpClientFactory` propre au provider qui **désactive le cache
partagé du SDK** (`UseSDKHttpClientCaching = false`) et crée un `HttpClient` sur
`PinningHandler(SocketsHttpHandler { AllowAutoRedirect = false, UseProxy =
false, MaxConnectionsPerServer = MaxConcurrentRequests })`. Les réglages S3
propres à l'environnement (point de terminaison régional de `us-east-1`, points
d'accès multirégions) sont fixés ou rendus sans effet ; la porte §9 en dresse la
liste exhaustive sur le binaire et la vérifie en processus enfant.

**Garde.** À la construction, le provider calcule l'ensemble **exact** des hôtes
admis depuis `S3BundleSource` : l'hôte de `ServiceUrl` (et `<bucket>.<hôte>`
sans `ForcePathStyle`), ou pour `Region` `s3.<région>.amazonaws.com` et
`<bucket>.s3.<région>.amazonaws.com`, selon la table que la porte §9 relève.
Le `PinningHandler` compare schéma, hôte et port de `RequestUri` et lève, sans
envoyer, une exception marquée (`Kind = RefusedHost`). Une région mal configurée
se manifeste donc par un refus du garde, pas par une correction silencieuse : le
README le dit.

**Ce que le garde ne couvre pas, et ce qui le couvre alors.**

- La **détection de région** du SDK (`BucketRegionDetector`) passe par un
  transport à part, hors du `HttpClientFactory`, sans le jeton d'annulation, et
  peut construire une URL `http://`. Elle doit être **empêchée** (configuration,
  ou réglage qui ne la déclenche jamais) ou **routée** par le transport gardé.
  La porte §9 mesure toutes les sorties au niveau socket ; si ni l'un ni l'autre
  n'est possible, le plan s'arrête et D1 est rouvert.
- Le trafic des **fournisseurs d'identifiants** appartient à l'hôte (D2). Le
  délai de l'opération (§4.5) s'applique à l'appel du provider qui l'attend ; un
  fournisseur qui bloque ou échoue donne `S3RequestException` (`Kind =
  Credentials` ou `Timeout`).

**Sensibilité.** Chaque test du garde (T1, sonde T3) a sa contre-épreuve : le
même scénario sans garde fait échouer l'assertion, ce qui prouve que le test
voit le garde et non une erreur de transport.

### 4.5 Délais et version du bundle

**Délai par opération.** Chaque opération du provider (une page de listing, une
lecture complète corps compris, un `ExistsAsync`) s'exécute sous un jeton lié au
jeton de l'appelant et à un délai : `source.RequestTimeout`, ou pour
`EnumerateAsync`/`GetVersionAsync` le plus court de `source.RequestTimeout` et
`limits.RequestTimeout` (seules ces opérations reçoivent des `BundleLimits`). Le
délai couvre les réessais du SDK, leur attente et l'acquisition des
identifiants. Un délai dépassé devient `S3RequestException` (`Kind = Timeout`),
jamais une `OperationCanceledException`, que le cœur prendrait pour une
annulation de l'appelant ; une annulation de l'appelant reste une annulation, et
une borne déjà dépassée reste l'exception de la borne.

**Encodage de `s3v1`.** Le jeton est `"s3v1:" + hex(SHA-256(flux))`, où le flux
est, pour chaque entrée du listing **dans l'ordre rendu**, la concaténation de
trois champs préfixés chacun par leur longueur en octets (entier non signé sur 4
octets, gros-boutiste) : le nom brut en UTF-8, l'ETag tel que rendu en UTF-8,
la taille en décimal ASCII. L'ordre doit être strictement croissant en octets
UTF-8 (l'ordre de S3) : un doublon, un recul ou une boucle de pagination fait
échouer le listing (D9). Un ETag absent ou vide, une taille absente ou négative :
échec du listing. Le jeton ne dépend ni de la culture ni du découpage en pages.

### 4.6 Traduction des erreurs (D7)

| Origine | Devient |
|---|---|
| 404 sur `GetObject` | `BundleObjectNotFoundException(key)` |
| 403 sur `GetObject` | `BundleAccessRefusedException(key, "access denied")` |
| octets lus différents d'`expected` | `BundleVersionMismatchException(key)` |
| `Content-Length` ou octets lus au-delà de `MaxObjectBytes` | `BundleObjectTooLargeException` |
| tout autre échec, **avant les en-têtes, pendant le corps ou à la libération** : `AmazonServiceException` (403 sur un listing compris), `AmazonClientException` (identifiants, checksum, démarshalling), `HttpRequestException`, `IOException`, `XmlException`, `InvalidOperationException`, `ArgumentException` du gestionnaire de redirection, refus du garde, délai dépassé, provider libéré | `S3RequestException` avec `Kind` ; l'original en `InnerException` (ces types acceptent une cause ; les exceptions typées du contrat ci-dessus n'en ont pas, et c'est assumé) |
| annulation par le jeton de l'appelant ; `OutOfMemoryException` | propagée telle quelle |

**Message à vocabulaire fermé.** `"S3 <operation> failed: <kind>"`, plus
`" (HTTP <code>)"` et la clé du bundle quand il y en a une. Le code d'erreur S3
(`<Code>`) n'entre jamais dans le message : c'est un texte du serveur, que D9
traite comme hostile. Il n'est pas non plus exposé en propriété ; un hôte qui
veut le diagnostic brut le lit dans `InnerException`, canal du journal de l'hôte
(#139, §3).

### 4.7 Les deux changements du cœur (D15)

**(a) Version avant listing.** `LoadCoreAsync` lit `GetVersionAsync` avant
`ListNamesAsync` (aujourd'hui `Bundle.cs:261` puis `:291`). Un changement entre
les deux donne une image plus récente que son jeton : l'accès suivant voit un
jeton différent et recharge. L'ordre inverse donnait une image plus ancienne que
son jeton, crue fraîche pour toujours. Sans effet pour le filesystem (sa portée
de provider rend les deux appels cohérents) ni pour l'embarqué (contenu figé).
Test : un `GatedProvider` qui ajoute un objet entre les deux appels, dans les
deux ordres ; avec le nouveau, l'accès suivant sous `OnAccess` recharge et voit
l'objet.

**(b) Refus et pannes pendant la résolution.** `ResolveResourceKeyAsync`
(`Bundle.cs:1244`) rend `Unsafe` pour `BundleAccessRefusedException` seulement ;
toute autre `BundleProviderException`, et les `IOException` /
`UnauthorizedAccessException` d'un provider qui n'en a pas d'autres, remontent.
Une panne réseau pendant la validation fait échouer la validation, au lieu
d'émettre « escapes the bundle » (`Validate.cs:709`). Test : un provider de test
dont `ExistsAsync` lève chacune de ces familles. Les goldens n'en sont pas
affectés (aucun provider de fixture ne lève) ; le plan le vérifie.

## 5. Sécurité, récapitulatif

| Menace | Réponse |
|---|---|
| Redirection vers un hôte arbitraire, fuite du jeton de session | garde D3 sur hôtes exacts, mesuré (M3c) ; contre-épreuve de sensibilité |
| Détection de région par un transport à part | empêchée ou routée (D3) ; porte §9, arrêt du plan sinon |
| Configuration ambiante (endpoints, proxy, FIPS, réessais) | configuration hermétique (D2), sonde en environnement hostile |
| Trafic des fournisseurs d'identifiants | hors garde, à l'hôte ; borné par le délai de l'opération |
| Normalisation du chemin par le client | le SDK envoie la clé telle quelle (M4) ; représentation sur le fil vérifiée (T1) |
| Clé hors du bundle | préfixe à frontière de segment (D6), échec fermé sur clé rendue hors préfixe (D9). Pour `GET`, le confinement suppose un store qui décode le chemin une fois sans normaliser les points : hypothèse énoncée au README, mesurée sur RustFS (T2) |
| Alias de préfixes (surrogates isolés → U+FFFD) | `WellFormedUnicode` sur le préfixe (D6) |
| Alias de clés (`/` initial, NFC/NFD, casse) | `/` initial impossible dans une `BundleKey` ; NFC/NFD et casse distincts côté S3, signalés comme non portables sous `Strict` |
| Listing hostile (page démesurée, jeton géant, XML invalide, caractère de contrôle) | `EncodingType=url`, corps borné avant désérialisation, invariants de page (D9) |
| Objets et listings démesurés | `Content-Length` et comptage (D8) ; bornes du cœur ; `MaxEntries`/`MaxDepth` dans `GetVersionAsync` |
| Code sanctionné remplacé après le chargement, collision MD5, store qui ignore `If-Match` | version = SHA-256 des octets (D8), indépendante du store |
| Texte du serveur dans un message lu par le modèle | vocabulaire fermé (§4.6) |
| Bucket ou endpoint visibles du modèle | `Location` sans bucket (D5), messages sans bucket ni endpoint |
| Transport en clair | D11 ; une URL `http://` de la détection de région est couverte par D3 |

## 6. Tests

Toute comparaison qui passe par une validation ou un audit utilise `FixedClock`
à 2026-11-01T00:00:00Z, comme pour l'embarqué.

### T0 — les deux changements du cœur (D15)

Dans `tests/OKF4net.Tests/Providers/`, sur `GatedProvider` et un provider de
test : (a) l'insertion entre version et listing, recharge à l'accès suivant ;
(b) chaque famille d'exception d'`ExistsAsync` pendant la validation, `Unsafe`
pour le seul refus. Écrits rouges avant le changement. Suite complète et
goldens inchangés ensuite.

### T1 — unitaires, en CI, sans service

Dans `tests/OKF4net.Tests/Providers/S3/`. Le provider tourne avec le **vrai
SDK** contre un `HttpMessageHandler` qui rend des réponses S3 scriptées ; le
garde D3 est posé entre le SDK et ce handler, par le chemin de construction
unique (§4.2).

| Test | Ce qu'il prouve |
|---|---|
| Domaine de la configuration | `ArgumentException` pour chaque valeur hors D6 (préfixe `/x`, `x/`, `a//b`, `..`, surrogate isolé ; `DisplayName` avec `/` ou `:` ; bucket ARN, `--x-s3`, majuscules ; `ServiceUrl` avec userinfo, chemin, query) ; aucune requête émise |
| Injectivité | deux préfixes différents, U+FFFD réel compris, donnent des clés S3 différentes sur le fil ; `kb` ne demande jamais `kb2/…` ; préfixe vide = tout le bucket |
| Représentation sur le fil | pour chaque nom de la batterie, la cible de la requête est exactement l'encodage attendu de la clé (pas de `..` réduit, pas de double encodage) |
| Listing hostile (D9) | clé hors préfixe, ordre non croissant, doublon, jeton répété, jeton géant, page tronquée sans jeton, page plus grande que `MaxKeys`, corps démesuré, XML invalide, U+0001 encodé : l'énumération échoue, le chargement aussi, rien n'est servi, et le corps démesuré n'est pas désérialisé |
| Pagination et borne | plus de 1 000 objets sur plusieurs pages ; une borne d'entrées atteinte arrête le listing et le libère |
| `ExistsAsync` | correspondance exacte (`a.md` contre `a.md.bak`), `false` sur absence, `S3RequestException` sur 403 |
| Lecture | version = SHA-256 des octets ; `expected` différent → `VersionMismatch` ; un serveur qui sert d'autres octets sous le même ETag → `VersionMismatch` ; `Content-Length` trop grand refusé sans lire le corps ; corps plus long que son `Content-Length` refusé à la borne |
| Erreurs | une famille par ligne de §4.6, injectée avant les en-têtes, pendant le corps et à la libération ; annulation et borne gardent leur priorité |
| Messages | des valeurs sentinelles (bucket, endpoint, id de requête, URL, `\n`, U+2028) placées dans **chaque champ distant** (`<Code>`, `<Message>`, `<RequestId>`, `<HostId>`, `<Endpoint>`, en-têtes) n'apparaissent dans aucun `Message` d'exception, ni dans les `Path` des diagnostics de validation, ni dans les diagnostics du catalogue |
| Délais | un corps qui cale, une suite de réessais, un fournisseur d'identifiants qui bloque : `S3RequestException` (`Timeout`) dans le délai ; le jeton de l'appelant est transmis et reste une annulation (modèle : `AsyncProviderContractTests`) |
| Garde | 301, 307 et `PermanentRedirect` vers un autre hôte, **y compris `<autre-bucket>.s3.<région>.amazonaws.com`** : refus marqué, rien d'envoyé, pas de réessai ; la table des hôtes admis par configuration, relevée par un handler qui refuse tout ; **contre-épreuve sans garde** |
| Isolement | deux providers d'origines différentes dans un même processus n'échangent ni `HttpClient` ni garde ; `Dispose` libère le sien |
| Environnement hostile | une sonde en **processus enfant** (`AWS_ENDPOINT_URL_S3`, `AWS_ENDPOINT_URL`, `AWS_USE_FIPS_ENDPOINT`, `AWS_USE_DUALSTACK_ENDPOINT`, `AWS_RETRY_MODE`, `AWS_MAX_ATTEMPTS`, `HTTPS_PROXY`, `HTTP_PROXY`, un `~/.aws/config` hostile via `AWS_CONFIG_FILE`) : mêmes requêtes, mêmes hôtes qu'en environnement vide |
| `Id`, `Location` | `Id` stable, opaque, égal pour deux configurations effectives égales, différent sinon ; `Location` sans bucket, avec `DisplayName` ou les 8 hex |
| Version `s3v1` | ajout, suppression, renommage, changement de taille ou d'ETag changent le jeton ; découpage en pages et culture ne le changent pas ; deux listings identiques donnent le même jeton |
| Écritures | `WriteAsync`/`DeleteAsync`, `BundleConceptWriter`, `IndexGenerator` : `BundleReadOnlyException`, rien n'est envoyé ; `OkfBundleTools` n'offre aucun outil d'écriture |
| Pont synchrone | avec un handler **réellement suspendu**, une lecture synchrone lève `InvalidOperationException` (`SyncBridge.IncompleteMessage`) et annule la requête ; une écriture synchrone est refusée avant de commencer |
| D7 de #141, frère de `NoResidualFileIoTests` | les membres `[Obsolete]` à chemin refusent ce provider ; les points d'entrée de lecture, répertoire courant sur un dossier vide, n'y écrivent rien ; un segment `c:x.md` sous Windows : `Path` documenté, `Key` exact |
| Gardes de dépendance | le cœur n'accorde aucun `InternalsVisibleTo` à `OKF4net.Providers.S3` ; l'assembly ne référence qu'`OKF4net`, `AWSSDK.*` et la BCL |

### T2 — `[Trait("Category", "S3Integration")]`, hors CI

Un fixture choisit le moteur explicitement (`podman` s'il répond, sinon
`docker`), vérifie l'accès au moteur, démarre `docker.io/rustfs/rustfs:1.0.3`
sur un port libre avec des identifiants générés, crée un bucket, téléverse, et
arrête le conteneur à la fin. Moteur absent : chaque test est **rapporté
ignoré** avec la raison. Moteur présent mais conteneur en échec : les tests
**échouent**. Garantie : `dotnet test tests/OKF4net.Tests --filter
Category=S3Integration` avec **0 skipped**, en tête du README du paquet et dans
CLAUDE.md ; exécutée sous Podman sans compatibilité Docker et sous Docker.

| Test | Ce qu'il prouve |
|---|---|
| Parité (`okf_v02`, `okf_v02_computation`, `okf_v02_reserved`, `appendix_a`, `bundles/acme_retail`) | comme les `ParityTests` de l'embarqué, par `LoadAsync` et les relectures asynchrones : mêmes clés et octets ; mêmes faits de chargement ; diagnostics (sans `Path`), audit, graphe, recherche ; `ExecutableResources` par clé et état (les versions sont des SHA-256 des deux côtés : égales), puis relecture épinglée ; index générés capturés par un double inscriptible. Aucune fixture n'est écrite ni modifiée |
| Refus du serveur | `a/../b`, `a//b`, `a/./b` : téléversement refusé, 400 attendu, rien de listé |
| Noms acceptés, oracle indépendant | `%2F`, `%2e%2e`, `%25`, `%252e`, `+`, espace, `\`, `?`, `#`, NFC et NFD de `café.md`, `dir/`, `/kb/lead.md` (normalisé par RustFS), `kb2/…` à côté de `kb/…` : chaque objet a un **contenu distinctif** ; le listing est comparé à celui de `mc` (client indépendant) ; chaque objet servi par le provider a le contenu de sa clé ; ce que le cœur rejette suit son socle sous `Native` |
| Longueur | `MaxKeyBytes` abaissé, plutôt qu'une clé de plus de 1 024 octets que RustFS refuse |
| Lecture épinglée | après réécriture d'un objet, `ReadResourceText(BundleKey, expected, …)` refuse ; `AttestationOrchestrator.CaptureAsync` rend le refus exact (version différente) et aucune étape d'exécution ne démarre |
| Fraîcheur | `OnAccess` voit une modification ; `Every` seulement après l'intervalle |
| Bornes | un objet au-delà d'un `MaxObjectBytes` abaissé : problème local ; un listing au-delà d'un `MaxEntries` abaissé : échec du chargement |
| Panne | conteneur arrêté pendant un rechargement : l'image précédente reste servie, marquée périmée, `LastLoadError` renseigné |

### T3 — job CI `S3 provider AOT smoke test`

Sur le modèle d'`embedded-aot`, matrice `linux-x64`, `osx-arm64`, `win-x64`,
**deux fois** : versions minimales (`AWSSDK.S3` 4.1.0, `AWSSDK.Core` 4.1.0) et
dernières versions publiées au moment du run. `dotnet pack` d'`OKF4net` et du
provider vers un flux local à une version propre au run ; contrôle du `.nuspec`
(minimum `AWSSDK.S3`) et de `project.assets.json` (versions S3 **et** Core
résolues, affichées dans le journal) ; publication Native AOT de
`tests/S3ProviderAotProbe/` (hors `OKF4net.sln`). La sonde sert elle-même un
**faux S3 en boucle locale** sur un `TcpListener` (HTTP/1.1 minimal, sans
`HttpListener`, dont le comportement varie selon l'OS), charge un petit bundle,
compare son résumé au provider filesystem, et vérifie sous AOT que le garde
refuse une 307 vers `*.amazonaws.com` avec sa marque — et, en contre-épreuve,
qu'un client sans garde la suit vers un second écouteur local. Aucun conteneur.

## 7. Livraison et documentation

### 7.1 Fichiers

- `src/OKF4net.Providers.S3/` et son README de paquet : **en tête**, la commande
  `S3Integration` et le contrôle « 0 skipped » ; la recette d'hôte
  (identifiants, `S3BundleSource`, `BundleHandle` avec `Every`), le coût de
  `OnAccess`, la mémoire (§4.3), les checksums, la région mal configurée, les
  hypothèses sur le store (décodage unique du chemin), ce que le garde ne couvre
  pas (§4.4), la plage de versions testée, la fabrique de catalogue écrite par
  l'hôte (renvoi à #239).
- `OKF4net.sln`, `release.yml` (pack ; filtre de tests étendu), `ci.yml` (filtre
  étendu, job T3).
- `CLAUDE.md` : la règle des dépendances gagne l'exception `AWSSDK.S3` (ce projet
  seul, minimum déclaré, rien ne le référence) ; une puce d'architecture ; la
  garde « toute montée d'`AWSSDK.S3` **ou** d'`AWSSDK.Core` exige la commande
  `S3Integration` à 0 skipped et le job T3 vert », au même rang que
  `ContainerIntegration` ; les deux changements du cœur dans la puce
  `Providers/`.
- `README.md` racine et `CHANGELOG.md` (dont les deux changements du cœur).
- `graphify update .` après les modifications de code.

### 7.2 Branche et conflits

Branche `feat/s3-bundle-provider`, empilée sur `feat/embedded-resource-provider`
(PR #237), rebasée sur `dev` après la fusion de #237. **`src/OKF4net/` n'est
touché que par les deux commits de D15**, en tête du plan, limités à
`LoadCoreAsync` et `ResolveResourceKeyAsync` dans `Bundle.cs` et à leurs tests.
Ailleurs (`OKF4net.sln`, `README.md`, `CHANGELOG.md`, `CLAUDE.md`, `ci.yml`,
`release.yml`), seulement des ajouts séparés.

### 7.3 Si `CanWrite` est déclaré un jour

Décision à part, non prise ici : correspondance de `BundleWriteCondition` sur
`If-None-Match: *` et `If-Match` de `PutObject` (409
`ConditionalRequestConflict` compris), alors que la version d'objet est un
SHA-256 et non un ETag (D8) ; sémantique d'une écriture dont la réponse se perd
(`BundleWriteOutcomeUnknownException`) ; suppression conditionnelle ; envoi
`aws-chunked` ; support réel de ces en-têtes par chaque store compatible.

## 8. Décisions de #141 appliquées (2026-11-15, consignées sur #141)

1. Pas d'interne du cœur (D10), garde de test.
2. Bornes, validation des clés et D7 de #141 viennent du cœur ; le provider
   déclare `NativeConstraints`, `Id`, `Location`, `Capabilities` et les erreurs
   typées du contrat, et n'applique que ce qui lui revient (D16, `RequestTimeout`,
   le parcours de `GetVersionAsync`, `MaxObjectBytes` avant de bufferiser).
3. Pas de secret dans `catalog.json` : fabrique hors lot (#239).
4. Dépendance validée par le mainteneur (D1).
5. Tests du contrat sur les doubles existants ; hors CI seulement par une Trait,
   commande locale en tête du README.
6. Conflits : §7.2.
7. Gardes exécutables pour ce que xunit n'exécute pas : T3 et la porte §9.

**Écarts au commentaire de #142 du 2026-10-06**, consignés sur #142 : les bornes
par défaut sont celles du cœur d'aujourd'hui (256 Mio, 10 M d'entrées,
profondeur 1 024) et non 16 Mio, 1 M et 64 ; la politique de clés par défaut
est `Native` (le défaut du cœur, `BundleLoadOptions.KeyPolicy`), `Strict` restant
disponible ; la version d'objet est un SHA-256 et non l'ETag ou `VersionId`
(D8).

## 9. Non vérifié à ce jour — porte de la tâche 1 du plan

Les prototypes de l'annexe A ont été faits sous Windows, contre RustFS, en
path-style. **Première tâche du plan, avant tout code de production** (y compris
les commits de D15, qui n'en dépendent pas mais suivent la porte), un prototype
qui mesure :

1. **toutes les sorties réseau**, au niveau socket (un écouteur pour chaque hôte
   possible, résolution de noms détournée), sur des réponses 301, 307,
   `PermanentRedirect`, 400 `AuthorizationHeaderMalformed` avec et sans
   `<Region>`, et `x-amz-bucket-region` : la détection de région se
   déclenche-t-elle, par quel transport, vers quel hôte, en `http://` ? Peut-on
   l'empêcher par configuration, ou la faire passer par le transport gardé ?
   **Si non, arrêt du plan et D1 rouvert** ;
2. les hôtes visés pour `Region` (*virtual-hosted*, path-style, buckets avec
   points) et pour `ServiceUrl` avec et sans `ForcePathStyle` — ils fixent la
   table de §4.4 ;
3. la liste exhaustive des réglages lus de l'environnement par `AWSSDK.Core` et
   `AWSSDK.S3` 4.1.0, et que la configuration de §4.4 les neutralise tous, en
   processus enfant ;
4. que le cache de `HttpClient` du SDK est bien désactivé par la fabrique, et
   que deux providers ne partagent rien ;
5. que l'exception du garde n'est pas réessayée par le SDK, pour le type
   retenu ;
6. qu'un délai par jeton lié interrompt une lecture de corps qui cale, des
   réessais en attente et un fournisseur d'identifiants qui bloque ;
7. qu'un corps dont le checksum ne correspond pas est détecté, et par quelle
   exception, pour la traduction de §4.6 ;
8. qu'un corps de listing peut être borné avant désérialisation dans le
   handler ;
9. le cycle de vie du conteneur RustFS piloté depuis xunit, sous Podman sans
   compatibilité Docker et sous Docker sous Linux ;
10. qu'une page `ListObjectsV2` aux noms hostiles en `EncodingType=url` est
    décodée à l'identique du listing `mc`, et que RustFS rend les clés en ordre
    UTF-8 strictement croissant.

Si l'un de ces points contredit le design, le plan s'arrête et la section
concernée est rouverte avec le mainteneur.

## Annexe A — mesures de D1 (2026-11-14 et 2026-11-15)

Deux prototypes jetables, hors dépôt, contre `rustfs/rustfs:1.0.3`, win-x64,
.NET 10 ; données : `bundles/acme_retail` (18 objets) et un bundle généré de
5 001 fichiers (pagination au-delà de 1 000). Les deux chargent les deux bundles
exactement comme le provider filesystem (9 et 5 000 concepts, mêmes clés et
identifiants, 0 problème), en JIT et en Native AOT. Journaux bruts conservés
dans le scratchpad de la session ; chiffres vérifiés à la main sur ces journaux.
Ces mesures portent le choix entre les deux clients ; elles ne prouvent ni la
sûreté de tous les stores compatibles ni celle de tous les chemins du SDK, que
la porte §9 et les tests T1 à T3 couvrent.

| # | Mesure | SDK (`AWSSDK.S3` 4.1.0) | `HttpClient` + SigV4 à la main |
|---|---|---|---|
| M1 | Exe AOT (base 3,39 Mo) | 11,70 Mo (+8,31) ; 11,83 avec le garde | 8,22 Mo (+4,83) |
| M1 | Paquets | 4 (S3, Core, Extensions.Numerics, System.IO.Hashing), 0 vulnérable | 0 |
| M2 | Avertissements AOT et trimming | 0 (témoin fautif : détecté) | 0 (témoin fautif : détecté) |
| M3 | 301, 307, `PermanentRedirect` vers un hôte quelconque | non suivis | non suivis |
| M3b | 307 vers `*.amazonaws.com` | **suivie, re-signée, bucket changé, succès rendu** ; `AllowAutoRedirect` (déjà `false` par défaut) n'y change rien | non suivie |
| M3c | idem avec jeton de session | **jeton envoyé à la cible (3/3)** | — |
| M3c | idem avec garde d'hôte | 0/3 parvenue, pas de réessai ; 0 refus sur 15 149 requêtes normales | — |
| M3c | désactivation par configuration | aucune option ; le registre de pipeline est global ; retirer le handler rend la 307 comme un succès vide | — |
| M4 | Clés sur le fil | `..`, `.`, `//` envoyés tels quels | `System.Uri` réécrit `..` sans `DangerousDisablePathAndQueryCanonicalization` |
| M4 | RustFS | refuse `a/../b`, `a//b`, `a/./b` (400), > 1 024 octets (400) ; normalise un `/` initial ; NFC et NFD coexistent ; préfixe `kb/` ne rend jamais `kb2/` | idem |
| M5 | ETag | entre guillemets, identique PUT/GET/HEAD/LIST ; 412 sur ETag périmé (sans valeur de preuve pour l'épinglage, qui ne repose plus sur l'ETag, D8) | idem |
| M6 | Checksums | CRC32 par défaut, accepté | GET signé accepté |
| M7 | Code | adaptateur 72 lignes | 603 lignes mesurées + ≈ 400 estimées pour la production |
| M7 | Cadence | ≈ 2 versions par semaine (54 en 6 mois) | — |
