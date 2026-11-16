# Provider de bundle S3 — design

Date : 2026-11-16
Statut : **révision 3**, à relire par le mainteneur. La révision 1 (design validé
section par section le 2026-11-15) a reçu une revue interne et une revue externe ;
la révision 2 les intégrait avec cinq arbitrages du mainteneur (D15, D5, D1, D8,
consignation sur les tickets) ; la révision 3 intègre une seconde revue externe
et un arbitrage de plus (portée de D15, ticket #247).
Ticket : [#142](https://github.com/jchable/okf4net/issues/142). Suites hors lot :
[#238](https://github.com/jchable/okf4net/issues/238) (`okf-mcp` sur S3),
[#239](https://github.com/jchable/okf4net/issues/239) (fabrique de catalogue S3,
emplacement à trancher avec [#236](https://github.com/jchable/okf4net/issues/236)),
[#247](https://github.com/jchable/okf4net/issues/247) (panne globale pendant la
lecture d'`index.md`/`log.md` par la validation).

Prérequis de lecture : la couture provider
([`2026-10-07-bundle-provider-seam-design.md`](2026-10-07-bundle-provider-seam-design.md)),
son contrat asynchrone
([`2026-11-03-async-bundle-provider-design.md`](2026-11-03-async-bundle-provider-design.md))
et le provider embarqué
([`2026-11-12-embedded-resource-bundle-provider-design.md`](2026-11-12-embedded-resource-bundle-provider-design.md)),
dont ce document reprend le vocabulaire (clé, contraintes natives, bornes,
image, handle) et la forme.

**Sources des décisions.** Le cadrage de sécurité (#139, 2026-10-06),
l'atomicité et le rafraîchissement (#139, 2026-10-07) et la règle de priorité
des erreurs (#139, 2026-10-12) s'appliquent sans être rejoués. Les arbitrages de
ce lot et l'inventaire des écarts au commentaire de #142 du 2026-10-06 sont
consignés sur
[#142](https://github.com/jchable/okf4net/issues/142#issuecomment-6913532450) ;
les sept règles communes aux providers de paquet, sur
[#141](https://github.com/jchable/okf4net/issues/141#issuecomment-6913533206)
(§8). L'arbitrage du 2026-11-16 sur la portée de D15 est consigné par #247.

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
| D1 | Client S3 | **SDK AWS pour .NET, `AWSSDK.S3`**, plutôt qu'un client `HttpClient` + SigV4 écrit à la main. Arbitré le 2026-11-15 sur deux prototypes mesurés (annexe A) ; remplace la décision du 2026-10-05, prise sans mesure. Exception de dépendance propre au projet, comme `Catalog.Hosting` : rien d'autre dans `src/` ne référence ce projet. **Politique de version (2026-11-16)** : un **minimum** déclaré (`AWSSDK.S3 >= 4.1.0`, qui admet `AWSSDK.Core [4.1.0, 5.0.0)`), pas d'épinglage exact. Les garanties sont vérifiées à l'exécution et par des tests de sensibilité, **sur chaque combinaison testée** : la porte §9 et T3 portent sur la version minimale et sur la dernière publiée ; toute montée de S3 ou de Core les rejoue. |
| D2 | Qui construit le client, et comment | **Le provider**, à partir d'un `AWSCredentials` et d'une `S3BundleSource` fournis par l'hôte ; aucune surcharge n'accepte un `IAmazonS3`. Le provider ne lit jamais l'environnement, un fichier ou la chaîne d'identifiants par défaut (#139, §3), et **aucun réglage ambiant n'influence ce qu'il envoie, ni où** : il fixe explicitement chaque réglage que le SDK irait chercher dans l'environnement ou `~/.aws/config` (§4.4), et la sonde d'environnement hostile compare URI, en-têtes et signatures. Le trafic des fournisseurs d'identifiants (STS, IMDS, SSO) appartient à l'hôte : D3 ne le couvre pas. |
| D3 | Sorties réseau | **Garde d'hôte obligatoire, non désactivable**, sur un ensemble d'**hôtes exacts** calculé depuis `S3BundleSource` seule : toute requête S3 vers une autre origine est refusée avant l'envoi, avec une marque identifiable. Mesuré nécessaire : le SDK suit une 307 vers tout hôte `*.amazonaws.com`, la re-signe et y envoie le jeton de session (annexe A). **Deux sorties du SDK échappent au `HttpClientFactory` et doivent être empêchées pour ce client, sans effet sur les autres clients AWS de l'hôte** : la détection de région (`BucketRegionDetector`, `HEAD` par un transport à part) et la télémétrie CSM (UDP, `AWS_CSM_*`). C'est la porte §9 ; si l'une ne peut pas l'être, le plan s'arrête et D1 est rouvert. |
| D4 | Écriture | **Lecture seule** : `WriteAsync`/`DeleteAsync` rendent `BundleReadOnlyException(Id)` sans rien tenter. |
| D5 | `Location` et `Id` | **Le nom du bucket n'atteint jamais le modèle** (#139, §3, sans amendement). `Location = "s3:<nom>/<prefix>"`, où `<nom>` est le `DisplayName` donné par l'hôte, ou à défaut les 8 premiers hex de l'`Id`. `Id = "s3:" + 32 hex` d'un SHA-256 de la configuration effective (§4.3) : stable, non vide, opaque. |
| D6 | Domaine de la configuration | `Prefix` : vide (tout le bucket) ou une suite de segments du socle `BundleKey`, **en Unicode bien formé**. `DisplayName` : un segment du socle en Unicode bien formé, sans `:`. `Bucket` : un nom de bucket S3 d'usage général (3 à 63 caractères, minuscules, chiffres, `-`, `.`) ; ARN, points d'accès, alias et buckets de répertoire (`--x-s3`, `--ol-s3`, `-s3alias`) refusés. `ServiceUrl` : une **origine** seule, sans userinfo, chemin, query ni fragment. `MaxObjectBytes` : de 1 à `Array.MaxLength`. Clé S3 = `<prefix>/<key>`, ou `<key>` sans préfixe : injective, à frontière de segment. |
| D7 | Erreurs | `S3RequestException : BundleProviderException` pour tout échec qui n'est pas propre à un objet : le cœur en fait un **échec de chargement**, jamais un état local (règle de priorité de #139). Les exceptions typées du contrat pour un objet absent, un refus propre à l'objet, une version différente, un objet trop grand ; la distinction se fait par une **table fermée** (statut et code reconnu, §4.6). Message à vocabulaire fermé ; aucun texte venu du serveur. |
| D8 | Versions | **Version d'objet = SHA-256 des octets lus, en hex minuscule**, au même format que le provider filesystem : l'épinglage ne dépend ni d'`If-Match` ni d'un ETag MD5. **Version du bundle = `s3v1:`** + SHA-256 d'un encodage canonique du listing, ETag compris : elle ne sert qu'à la fraîcheur. Pas d'`IBundleChangeNotifier`. |
| D9 | Listing | `EncodingType=url` toujours, décodé une seule fois ; corps de réponse borné **avant** désérialisation (§4.6) ; budget de pages ; **échec fermé** sur toute incohérence de transport (§4.3). Un nom bien transporté mais invalide n'est **pas** une incohérence : il est rendu brut au cœur, qui le juge. |
| D10 | Internes du cœur | **Aucun.** Le listing est un itérateur `async` ; le cœur n'accorde aucun `InternalsVisibleTo` à ce projet (garde de test). Ce projet accorde les siens au seul projet de tests. |
| D11 | Transport | `https://` exigé ; `http://` seulement avec `AllowInsecureHttp = true`. |
| D12 | Checksums | Réglage explicite dans `S3BundleSource` (par défaut : ceux du SDK, mesurés acceptés par RustFS) ; jamais lu de l'environnement. |
| D13 | Périmètre | Bibliothèque seule. Pas de fabrique de catalogue (#239), pas d'option `okf-mcp` (#238), pas de notification S3, pas d'écriture. |
| D14 | Tests sans CI | Catégorie **`S3Integration`**, distincte de `ContainerIntegration`, contre `docker.io/rustfs/rustfs:1.0.3` (MinIO n'est plus disponible sur Docker Hub). `ci.yml` et `release.yml` excluent les deux catégories. |
| D15 | Changements du cœur | **Deux, en tête du plan, en TDD, dans cette branche** (arbitrages du 2026-11-16) : (a) `LoadCoreAsync` lit la version du bundle **avant** de lister ; (b) `ResolveResourceKeyAsync` ne rend `Unsafe` que sur un refus d'accès, et toute autre défaillance du provider remonte **traduite par le cœur** (§4.7). Rien d'autre : la même confusion dans la lecture d'`index.md`/`log.md` par la validation est commune à tous les providers et part dans #247. |
| D16 | Concurrence | Le provider borne ses **opérations** en vol par un permis (`MaxConcurrentRequests`, 16 par défaut), tenu de l'appel jusqu'à la fin du corps et du nettoyage, acquisition des identifiants comprise : c'est sa responsabilité (spec de la couture ; spec asynchrone §3.6 ; #139 §4). |

## 3. Conformité à la spécification

SPEC §3 : un bundle « MAY be distributed as » un dépôt, une archive, un
sous-répertoire — liste permissive. Le provider présente le même arbre de noms
et les mêmes octets qu'un répertoire téléversé tel quel ; la parité (T2) en est
la preuve. Le stockage S3 tient des noms qu'un système de fichiers ne tient pas
(NFC et NFD distincts, `\`, `?`), et en refuse d'autres côté serveur : ce sont
des questions de clés, réglées par les trois couches de `BundleKey`, pas de la
spec OKF. **Pas d'entrée dans `docs/spec-conformance/`** : D15 corrige
l'implémentation du contrat de la couture, sans s'écarter de la spec.

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

    /// Libère, une seule fois, le client S3 et le HttpClient que le provider a construits.
    public void Dispose();
}

/// Une requête S3 a échoué. Message écrit par la bibliothèque, à vocabulaire
/// fermé ; aucun texte venu du serveur.
public sealed class S3RequestException : BundleProviderException
{
    public S3Operation Operation { get; }   // List, Read, Exists
    public S3FailureKind Kind { get; }      // Timeout, Throttled, ServerError, ClientError, Transport,
                                            // RefusedHost, InvalidResponse, Credentials, BucketUnavailable, Integrity, Disposed
    public int? StatusCode { get; }
}
```

Le provider n'a **qu'un chemin de construction** du client et de son
`HttpClient` ; un constructeur **interne**, pour les tests, ne fait qu'y injecter
le `HttpMessageHandler` intérieur, sous le garde D3.

L'hôte possède les identifiants (tout `AWSCredentials`) et le provider ; le
provider possède son client et son `HttpClient`. Un `BundleHandle` ne libère pas
son provider. Un appel sur un provider libéré échoue en `S3RequestException`
(`Kind = Disposed`), jamais en `ObjectDisposedException` brute.

### 4.3 Contrat

Toutes les opérations reposent sur un **itérateur privé d'entrées** S3 (nom
décodé, ETag, taille) ; `EnumerateAsync`, `GetVersionAsync` et `ExistsAsync` en
sont trois projections.

| Membre | Comportement |
|---|---|
| `Capabilities` | `CanWrite = false`, `CompletesSynchronously = false`. |
| `Id` | `"s3:" + hex(SHA-256(UTF-8("s3v1\n" + endpoint + "\n" + bucket + "\n" + prefix)))[..32]`, hex minuscule, où `endpoint` vaut `aws:<nom système de la région>` ou l'origine de `ServiceUrl` (schéma et hôte en minuscules, port explicite en décimal), suivie de `\|path` ou `\|vhost` selon `ForcePathStyle`. Deux sources de même configuration effective ont le même `Id` (déduplication du catalogue, `FusedResolverEngine.cs:141`), deux sources différentes jamais. |
| `Location` | `s3:<DisplayName ou 8 hex de l'Id>` suivi de `/<prefix>` quand le préfixe n'est pas vide. Affichage seulement ; jamais le bucket ni l'endpoint (D5). |
| `NativeConstraints` | `BundleKeyConstraints.WellFormedUnicode`. |
| `EnumerateAsync` | Itérateur `async` sur `ListObjectsV2` (`Prefix = <prefix>/` plus le préfixe de clé demandé suivi de `/`, `MaxKeys = 1000`, `EncodingType=url`), une page à la fois : la borne d'entrées du cœur l'arrête par `DisposeAsync`. Nom brut = clé décodée moins `<prefix>/` ; `Length = Size` ; `Version = null` (comme le filesystem ; le cœur ne garde que le nom brut). Aucune validation de nom. |
| `GetVersionAsync` | Listing complet par l'itérateur privé, borné par `limits.MaxEntries` et `limits.MaxDepth` comptés sur **tous** les noms (invalides et non Markdown compris ; `BundleLimitExceededException`), haché **en flux, dans l'ordre du listing** (§4.5). |
| `ReadAsync` | `GetObject` sans condition. Un `Content-Length` supérieur à `MaxObjectBytes` est refusé avant la lecture du corps (`BundleObjectTooLargeException`) ; le corps est lu **en entier**, sous le délai et le permis de l'opération, dans **un seul tampon** dimensionné par `Content-Length` (agrandi en comptant contre la même borne s'il est absent), puis haché : version = hex minuscule du SHA-256. Avec `expected` différent : `BundleVersionMismatchException`. Le `BundleObject` rendu porte un `MemoryStream` en lecture seule sur ce tampon, sans copie, et `Length` exact. |
| `ExistsAsync` | `ListObjectsV2(Prefix = <clé S3>, MaxKeys = 1, EncodingType=url)` : `true` si la première clé rendue est **exactement** la clé (elle trie avant toute clé qu'elle préfixe), `false` sinon. Même droit que le listing ; un refus n'est jamais `false` (`S3RequestException`). |
| `WriteAsync` / `DeleteAsync` | `ValueTask.FromException(new BundleReadOnlyException(Id))`. |

**Incohérences de transport (D9), toutes en `S3RequestException` (`Kind =
InvalidResponse`)** : clé rendue hors de `<prefix>/` ; ordre des clés non
strictement croissant en octets UTF-8 (doublon et recul compris) ; jeton de
continuation répété, vide quand la page est tronquée, ou plus long que la borne
de §4.6 ; page plus grande que `MaxKeys` ; **page tronquée sans progrès** (aucune
clé) ; budget de pages épuisé (`ceil(MaxEntries / 1000) + 1` pages par parcours) ;
ETag absent ou vide, taille absente ou négative ; XML indécodable ; `%`
mal formé dans une clé encodée. Un nom correctement transporté mais invalide
(`bad%01.md`, `a//b.md`) est rendu brut : le cœur en fait un problème local et
charge le reste.

Le cœur, pas le provider, applique `MaxKeyBytes`, la validation des clés, sa
propre concurrence de `LoadAsync` et D7 de #141 (§8). Un chargement coûte la
version du bundle (un listing), le listing des noms (un second) et un `GET` par
objet lu ; aucun cache ne les fusionne (YAGNI, à rouvrir sur mesure).

**Cohérence d'un chargement.** Pas de transaction entre objets. Un objet
supprimé entre le listing et sa lecture fait échouer le chargement
(`BundleObjectNotFoundException` → `BundleLoadException`), et l'image précédente
reste servie, marquée périmée. Avec la version lue en premier (D15 a), un
changement **persistant** survenu pendant le chargement se voit dans le jeton à
la **prochaine comparaison que la politique autorise** (à l'accès suivant sous
`OnAccess`, après l'intervalle sous `Every`), et l'image est rechargée. Un
changement **défait avant cette comparaison** (A → B → A) peut laisser une image
qui mêle des octets intermédiaires sous un jeton redevenu identique : limite
assumée, la même que celle du filesystem, documentée au README et fixée par un
test.

**Mémoire.** Pas de lecture en flux (D8). En vol dans le provider : au plus
`MaxConcurrentRequests × MaxObjectBytes` octets de corps (4 Gio aux valeurs par
défaut). Le chargement total est plus lourd : le cœur décode chaque objet en
chaîne UTF-16 (jusqu'à deux fois sa taille) et garde les textes du préchargement
(`Bundle.cs:463`) ; le plafond de #151 reste la réponse à la mémoire d'un bundle
entier. Le README conseille d'abaisser `MaxObjectBytes`, la borne du cœur et
`MaxConcurrentRequests` pour un bucket non fiable ; la porte §9 mesure un
chargement de plusieurs objets proches de la borne.

### 4.4 Configuration du SDK, garde et identifiants (D2, D3)

**Configuration hermétique.** `AmazonS3Config` reçoit explicitement :
`IgnoreConfiguredEndpointUrls = true` ; `RetryMode = Standard`,
`MaxErrorRetry = 2` (3 tentatives) ; `UseFIPSEndpoint = false`,
`UseDualstackEndpoint = false` ; `DefaultConfigurationMode = Standard` ;
`DisableClockSkewCorrection = false` ; `ClientAppId = null` effectif (aucun
identifiant d'application ambiant dans le User-Agent) ; `AuthSchemePreference`
fixé à SigV4 ; la région (`Region`, ou `us-east-1` d'authentification pour
`ServiceUrl`) ; `ForcePathStyle` ; `RequestChecksumCalculation` et
`ResponseChecksumValidation` depuis `Checksums` ; le point de terminaison
régional de `us-east-1` et les points d'accès multirégions fixés ; **CSM
désactivé pour ce client** ; la détection de région empêchée. La liste exacte et
le moyen de chaque neutralisation sont relevés sur le binaire par la porte §9,
pour chaque version testée : là où ce texte nomme un réglage que le SDK
n'expose pas sous ce nom, c'est la porte qui fait foi.

**Un `HttpClient` possédé par le provider.** Le `HttpClientFactory` du provider
rend toujours **le même** `HttpClient`, créé à la construction :
`UseSDKHttpClientCaching = false`, `DisposeHttpClientsAfterUse = false`,
`HttpClient.Timeout = InfiniteTimeSpan` (le délai est celui de l'opération,
§4.5), sur `PinningHandler(SocketsHttpHandler { AllowAutoRedirect = false,
UseProxy = false })`. Le provider le libère une fois, dans `Dispose`.

**Garde.** À la construction, le provider calcule l'ensemble **exact** des hôtes
admis depuis `S3BundleSource` : l'hôte de `ServiceUrl` (et `<bucket>.<hôte>`
sans `ForcePathStyle`), ou pour `Region` `s3.<région>.amazonaws.com` et
`<bucket>.s3.<région>.amazonaws.com`, selon la table que la porte §9 relève. Le
`PinningHandler` compare schéma, hôte et port de `RequestUri` et lève, sans
envoyer, une exception marquée (`Kind = RefusedHost`). Une région mal configurée
se manifeste par un refus du garde, pas par une correction silencieuse : le
README le dit.

**Sensibilité.** Le seul cas que le SDK suit sans garde est la **307** avec
`Location` (son gestionnaire de redirection transforme les autres 3xx en
erreurs) : c'est sur elle que porte la contre-épreuve (sans garde, la requête
atteint la cible ; avec, elle est refusée avec sa marque). Les 301 et
`PermanentRedirect` sont testés comme refus natifs du SDK, traduits par §4.6.

**Identifiants.** `AWSCredentials.GetCredentialsAsync` ne prend pas de jeton et
peut bloquer de façon synchrone. Le provider résout donc les identifiants de
chaque opération **isolés** (`Task.Run`) sous une attente bornée par le délai de
l'opération. À l'expiration : `S3RequestException` (`Kind = Credentials`), et le
résultat tardif est ignoré. Le **permis de concurrence reste tenu** jusqu'à la
fin réelle de la tâche abandonnée : au plus `MaxConcurrentRequests` fils bloqués
par un fournisseur défaillant, jamais une accumulation sans borne. Rien n'est
envoyé à S3 sans identifiants résolus.

### 4.5 Délais, priorité des échecs et version du bundle

**Délai par opération.** Chaque opération du provider (une page de listing, une
lecture complète corps compris, un `ExistsAsync`) s'exécute sous un jeton lié au
jeton de l'appelant et à un délai : `source.RequestTimeout`, ou pour
`EnumerateAsync`/`GetVersionAsync` le plus court de `source.RequestTimeout` et
`limits.RequestTimeout`. Le délai court dès l'appel : il couvre l'attente du
permis, l'acquisition des identifiants, les réessais du SDK et leur attente, et
la lecture du corps.

**Priorité des échecs.** Une opération retient **un seul échec principal**, le
premier constaté, et le rend :

1. si le jeton de l'appelant était annulé avant cet échec, l'annulation
   (`OperationCanceledException`) ;
2. sinon une borne du cœur ou du provider (`BundleObjectTooLargeException`,
   `BundleLimitExceededException`) si c'est elle ;
3. sinon l'échec traduit par §4.6.

Une `OperationCanceledException` qui ne vient pas du jeton de l'appelant (délai,
fournisseur d'identifiants, SDK) est un échec (`Kind = Timeout` ou
`Credentials`), jamais une annulation. Un échec pendant le nettoyage (libération
du flux, de la réponse) ne masque jamais l'échec principal ; seul, il devient
`S3RequestException` (`Kind = Transport`).

**Encodage de `s3v1`.** Le jeton est `"s3v1:"` suivi du SHA-256 en hex minuscule
d'un flux qui contient, pour chaque entrée du listing **dans l'ordre rendu**,
trois champs, chacun précédé de sa longueur en octets (entier non signé sur 4
octets, gros-boutiste) : le nom décodé en UTF-8 (sans le préfixe du bundle),
l'ETag tel que rendu en UTF-8 (guillemets compris), la taille en décimal ASCII
sans signe ni zéro initial. L'ordre est exigé strictement croissant en octets
UTF-8 : c'est l'ordre des buckets S3 d'usage général, et une **précondition
déclarée** pour un store compatible (le README le dit ; RustFS est mesuré en §9).
Le jeton ne dépend ni de la culture ni du découpage en pages. Les vecteurs de T1
fixent l'encodage, dont un cas U+E000 contre U+10000 (ordre UTF-8 contre
UTF-16).

### 4.6 Traduction des erreurs (D7) et bornes de transport

**Classement, par une table fermée** (statut HTTP et code S3 reconnu ; tout
autre couple tombe dans la ligne de son statut) :

| Origine | Devient |
|---|---|
| 404 `NoSuchKey` sur `GetObject` | `BundleObjectNotFoundException(key)` |
| 403 `AccessDenied` sur `GetObject` | `BundleAccessRefusedException(key, "access denied")` |
| octets lus différents d'`expected` | `BundleVersionMismatchException(key)` |
| `Content-Length` ou octets lus au-delà de `MaxObjectBytes` | `BundleObjectTooLargeException` |
| 403 `InvalidAccessKeyId`, `SignatureDoesNotMatch`, `ExpiredToken`, `InvalidToken`, `TokenRefreshRequired` ; échec du fournisseur d'identifiants | `S3RequestException` (`Credentials`) |
| 404 `NoSuchBucket`, 301/400 de région, `PermanentRedirect` | `S3RequestException` (`BucketUnavailable`) |
| 503 `SlowDown`, 429 | `S3RequestException` (`Throttled`) |
| autre 5xx | `S3RequestException` (`ServerError`) |
| autre 4xx (403 et 404 aux codes non reconnus compris) | `S3RequestException` (`ClientError`) |
| délai dépassé | `S3RequestException` (`Timeout`) |
| refus du garde | `S3RequestException` (`RefusedHost`) |
| incohérence de §4.3, XML indécodable, réponse au-delà d'une borne de transport | `S3RequestException` (`InvalidResponse`) |
| checksum de réponse faux | `S3RequestException` (`Integrity`) |
| `HttpRequestException`, `IOException`, `AmazonClientException` d'une autre nature, `InvalidOperationException`, `ArgumentException` du gestionnaire de redirection — **avant les en-têtes, pendant le corps ou à la libération** | `S3RequestException` (`Transport`) |
| provider libéré | `S3RequestException` (`Disposed`) |
| annulation par le jeton de l'appelant ; `OutOfMemoryException` | propagées telles quelles |

Les deux refus locaux (objet absent, objet refusé) restent locaux là où le cœur
les traite ainsi (relevé des ressources exécutables) ; tous les autres font
échouer le chargement. Une panne d'identifiants entre la lecture des concepts et
le relevé n'est donc jamais rendue en `Unsafe`.

`S3RequestException` porte l'original en `InnerException` ; les exceptions
typées du contrat n'acceptent pas de cause (constructeurs scellés à la clé),
c'est assumé.

**Message à vocabulaire fermé.** `"S3 <opération> failed: <kind>"`, plus
`" (HTTP <code>)"` et la clé du bundle quand il y en a une. Le code d'erreur S3
n'entre jamais dans le message, ni dans une propriété : c'est un texte du
serveur. Le diagnostic brut reste dans `InnerException`, canal du journal de
l'hôte (#139, §3).

**Bornes de transport, appliquées avant désérialisation**, dans le handler, sur
les octets reçus (avec ou sans `Content-Length`) : corps d'une page de listing
**8 Mio** ; corps d'une réponse d'erreur (tout statut ≥ 300, `GetObject`
compris) **64 Kio** ; jeton de continuation **1 024 octets**. Une réponse égale
à la borne passe ; au-delà, l'opération échoue (`InvalidResponse`) sans que le
SDK ait désérialisé quoi que ce soit.

### 4.7 Les deux changements du cœur (D15)

**(a) Version avant listing.** `LoadCoreAsync` lit `GetVersionAsync` avant
`ListNamesAsync` (aujourd'hui `Bundle.cs:261` puis `:291`). Un changement entre
les deux donne une image plus récente que son jeton, rechargée à la prochaine
comparaison ; l'ordre actuel donnait une image plus ancienne que son jeton,
crue fraîche. Sans effet pour l'embarqué (contenu figé) ; pour le filesystem, la
portée de provider rend déjà les deux appels cohérents sur les noms. Tests : un
`GatedProvider` qui ajoute un objet entre les deux appels (l'accès suivant sous
`OnAccess` recharge et voit l'objet), et le cas A → B → A qui fixe la limite de
§4.3.

**(b) Refus et pannes pendant la résolution.** `ResolveResourceKeyAsync`
(`Bundle.cs:1244`) rend `Unsafe` pour `BundleAccessRefusedException` seulement.
Toute autre défaillance d'`ExistsAsync` — une autre `BundleProviderException`,
une `IOException` ou une `UnauthorizedAccessException` brutes d'un autre
provider — remonte **traduite par le cœur** : une `BundleLoadException` dont le
message nomme la clé et le type de l'exception, jamais son texte, avec
l'original en cause. Une panne pendant la validation la fait donc échouer au
lieu d'émettre « escapes the bundle » (`Validate.cs:709`), et aucun texte de
provider n'atteint le modèle par `OkfBundleTools` (`OkfBundleTools.cs:2758`
affiche `ex.Message`). Tests : un provider dont `ExistsAsync` lève chacune de ces
familles, dont une `IOException` portant une URL et un secret sentinelles ; le
résultat réel de `ValidateBundleAsync` ne contient ni l'un ni l'autre. Les
goldens n'en sont pas affectés (aucun provider de fixture ne lève) ; le plan le
vérifie.

**Hors D15, dans #247** : `ReadResourceTextAsync` traduit toute
`BundleProviderException` en `IOException` (`Bundle.cs:1321`), que la
validation absorbe en `UnparseableIndex`/`UnparseableLog` (`Validate.cs:971`,
`:1050`). Une panne réseau pendant la lecture d'`index.md` ou de `log.md` par la
validation donne donc un diagnostic local au lieu d'un échec, pour tout
provider. Le README du provider S3 documente cette limite jusqu'à #247.

## 5. Sécurité, récapitulatif

| Menace | Réponse |
|---|---|
| Redirection vers un hôte arbitraire, fuite du jeton de session | garde D3 sur hôtes exacts, mesuré (M3c) ; contre-épreuve sur la 307 |
| Détection de région, télémétrie CSM (transports hors `HttpClientFactory`) | empêchées pour ce client (D3) ; porte §9, arrêt du plan sinon |
| Configuration ambiante (endpoints, proxy, FIPS, réessais, User-Agent, schéma d'authentification) | configuration hermétique (D2) ; sonde en environnement hostile sur URI, en-têtes et signatures |
| Trafic des fournisseurs d'identifiants | hors garde, à l'hôte ; isolé et borné par le délai de l'opération (§4.4) |
| Normalisation du chemin par le client | le SDK envoie la clé telle quelle (M4) ; représentation sur le fil vérifiée (T1) |
| Clé hors du bundle | préfixe à frontière de segment (D6), échec fermé sur clé rendue hors préfixe (D9). Pour `GET`, le confinement suppose un store qui décode le chemin une fois sans normaliser les points : hypothèse énoncée au README, mesurée sur RustFS (T2) |
| Alias de préfixes (surrogates isolés → U+FFFD) | `WellFormedUnicode` sur le préfixe (D6) |
| Alias de clés (`/` initial, NFC/NFD, casse) | `/` initial impossible dans une `BundleKey` ; NFC/NFD et casse distincts côté S3, signalés comme non portables sous `Strict` |
| Listing hostile (page démesurée, jeton géant, pages vides sans fin, XML invalide) | bornes de transport avant désérialisation, budget de pages, invariants de page (§4.3, §4.6) |
| Réponse d'erreur démesurée | borne de 64 Kio avant désérialisation (§4.6) |
| Objets démesurés | `Content-Length` et comptage (D8) ; bornes du cœur |
| Code sanctionné remplacé, collision MD5, store qui ignore `If-Match` | version = SHA-256 des octets (D8) |
| Panne globale déguisée en état local | table fermée (§4.6), D15 (b) ; reste #247 |
| Texte du serveur ou d'un provider dans un message lu par le modèle | vocabulaire fermé (§4.6) ; traduction par le cœur (D15 b) |
| Bucket ou endpoint visibles du modèle | `Location` sans bucket (D5), messages sans bucket ni endpoint |
| Transport en clair | D11 ; la détection de région, qui peut viser `http://`, est empêchée (D3) |

## 6. Tests

Toute comparaison qui passe par une validation ou un audit utilise `FixedClock`
à 2026-11-01T00:00:00Z, comme pour l'embarqué. Les tests passent par les
spellings **asynchrones** (`LoadAsync`, `ReadResourceTextAsync`,
`CaptureAsync`) ; seuls les tests du pont synchrone appellent les autres.

### T0 — les deux changements du cœur (D15)

Dans `tests/OKF4net.Tests/Providers/`, sur `GatedProvider` et un provider de
test : (a) l'insertion entre version et listing recharge à l'accès suivant ; le
cas A → B → A est fixé tel que §4.3 le décrit ; (b) chaque famille d'exception
d'`ExistsAsync` pendant la validation : `Unsafe` pour le seul refus, échec
traduit sinon, et une URL et un secret sentinelles absents du résultat de
`ValidateBundleAsync`. Écrits rouges avant le changement ; suite complète et
goldens inchangés ensuite.

### T1 — unitaires, en CI, sans service

Dans `tests/OKF4net.Tests/Providers/S3/`. Le provider tourne avec le **vrai
SDK** contre un `HttpMessageHandler` qui rend des réponses S3 scriptées ; le
garde D3 est posé entre le SDK et ce handler, par le chemin de construction
unique (§4.2).

| Test | Ce qu'il prouve |
|---|---|
| Domaine de la configuration | `ArgumentException` pour chaque valeur hors D6 (préfixe `/x`, `x/`, `a//b`, `..`, surrogate isolé ; `DisplayName` avec `/` ou `:` ; bucket ARN, `--x-s3`, majuscules ; `ServiceUrl` avec userinfo, chemin, query ; `MaxObjectBytes` hors domaine) ; aucune requête émise |
| Injectivité | deux préfixes différents, U+FFFD réel compris, donnent des clés S3 différentes sur le fil ; `kb` ne demande jamais `kb2/…` ; préfixe vide = tout le bucket |
| Représentation sur le fil | pour chaque nom de la batterie, la cible de la requête est exactement l'encodage attendu de la clé |
| Incohérences de transport | une par ligne de §4.3 (dont les pages tronquées vides enchaînées : nombre de requêtes borné par le budget, réponses libérées) : l'énumération échoue (`InvalidResponse`), le chargement aussi, rien n'est servi |
| Noms invalides bien transportés | `bad%01.md`, `a//b.md` : rendus bruts, problème local du cœur, le reste du bundle chargé |
| Bornes de transport | une page de 8 Mio passe, 8 Mio + 1 échoue ; une erreur `GetObject` de 64 Kio + 1, avec et sans `Content-Length`, échoue **sans désérialisation** (le handler compte les octets lus, le test vérifie qu'aucun octet au-delà de la borne n'a été consommé) ; un jeton de 1 025 octets échoue |
| Pagination et borne | plus de 1 000 objets sur plusieurs pages ; une borne d'entrées atteinte arrête le listing et le libère |
| `ExistsAsync` | correspondance exacte (`a.md` contre `a.md.bak`), `false` sur absence, `S3RequestException` sur refus |
| Lecture | version = SHA-256 hex minuscule des octets (égale à celle du filesystem pour les mêmes octets) ; `expected` différent → `VersionMismatch` ; d'autres octets sous le même ETag → `VersionMismatch` ; `Content-Length` trop grand refusé sans lire le corps ; corps plus long que son `Content-Length` refusé à la borne ; flux rendu en lecture seule, sans copie |
| Classement des erreurs | une ligne de la table de §4.6 par test, injectée avant les en-têtes, pendant le corps et à la libération ; une panne d'identifiants pendant le relevé des ressources exécutables fait échouer le chargement (jamais `Unsafe`) |
| Priorité des échecs | matrice de doubles échecs (borne puis annulation, annulation puis échec de libération, checksum faux puis délai, `OperationCanceledException` venue des identifiants sans annulation de l'appelant) : type, `Kind` et cause retenus selon §4.5 |
| Messages | des valeurs sentinelles (bucket, endpoint, id de requête, URL, `\n`, U+2028) placées dans **chaque champ distant** (`<Code>`, `<Message>`, `<RequestId>`, `<HostId>`, `<Endpoint>`, `<Region>`, en-têtes) n'apparaissent dans aucun `Message` d'exception, ni dans les `Path` des diagnostics de validation, ni dans les diagnostics du catalogue |
| Délais | un corps qui cale, une suite de réessais, une attente de permis : `Timeout` dans le délai ; le jeton de l'appelant est transmis et reste une annulation (modèle : `AsyncProviderContractTests`) |
| Identifiants | un fournisseur qui bloque de façon synchrone, une tâche jamais terminée, une fin tardive : `Credentials` dans le délai, rien d'envoyé, permis tenu jusqu'à la fin réelle, résultat tardif ignoré |
| Concurrence (D16) | plus de `N` appels mixtes (listing, lecture, `ExistsAsync`) : jamais plus de `N` opérations en vol, corps suspendus compris ; une annulation en file libère sa place ; un échec de nettoyage rend le permis |
| `HttpClient` | même instance sur des appels successifs et concurrents ; libérée une seule fois par `Dispose` ; deux providers d'origines différentes n'échangent ni `HttpClient` ni garde |
| Garde | la 307 vers `<autre-bucket>.s3.<région>.amazonaws.com` : refus marqué, rien d'envoyé, pas de réessai, **et contre-épreuve sans garde** (la requête atteint la cible) ; 301 et `PermanentRedirect` : refus natifs du SDK, traduits ; la table des hôtes admis par configuration, relevée par un handler qui refuse tout |
| Environnement hostile | une sonde en **processus enfant** (`AWS_ENDPOINT_URL_S3`, `AWS_ENDPOINT_URL`, `AWS_USE_FIPS_ENDPOINT`, `AWS_USE_DUALSTACK_ENDPOINT`, `AWS_RETRY_MODE`, `AWS_MAX_ATTEMPTS`, `AWS_SDK_UA_APP_ID`, `AWS_CSM_ENABLED`/`AWS_CSM_PORT` vers un écouteur UDP, `HTTPS_PROXY`, `HTTP_PROXY`, un `AWS_CONFIG_FILE` hostile, et les variables que la porte §9 ajoute) : mêmes URI, en-têtes (hors date et identifiant d'invocation) et signatures qu'en environnement vide, à horloge fixée ; aucun datagramme reçu |
| `Id`, `Location` | `Id` stable, opaque, égal pour deux configurations effectives égales, différent sinon ; `Location` sans bucket, avec `DisplayName` ou les 8 hex |
| Version `s3v1` | vecteurs attendus (dont U+E000 / U+10000) ; ajout, suppression, renommage, taille ou ETag changent le jeton ; découpage en pages et culture ne le changent pas |
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
| Parité (`okf_v02`, `okf_v02_computation`, `okf_v02_reserved`, `appendix_a`, `bundles/acme_retail`) | comme les `ParityTests` de l'embarqué : mêmes clés et octets ; mêmes faits de chargement ; diagnostics (sans `Path`), audit, graphe, recherche ; `ExecutableResources` par clé, état **et version** (même format des deux côtés) ; relecture épinglée par `ReadResourceTextAsync` ; index générés capturés par un double inscriptible. Aucune fixture n'est écrite ni modifiée |
| Refus du serveur | `a/../b`, `a//b`, `a/./b` : téléversement refusé, 400 attendu, rien de listé |
| Noms acceptés, oracle indépendant | `%2F`, `%2e%2e`, `%25`, `%252e`, `+`, espace, `\`, `?`, `#`, NFC et NFD de `café.md`, `dir/`, `/kb/lead.md` (normalisé par RustFS), `kb2/…` à côté de `kb/…` : chaque objet a un **contenu distinctif** ; le listing est comparé à celui de `mc` (client indépendant) ; chaque objet servi par le provider a le contenu de sa clé ; ce que le cœur rejette suit son socle sous `Native` |
| Longueur | `MaxKeyBytes` abaissé, plutôt qu'une clé de plus de 1 024 octets que RustFS refuse |
| Lecture épinglée | après réécriture d'un objet, `ReadResourceTextAsync(key, expected, …)` lève `BundleVersionMismatchException` ; `AttestationOrchestrator.CaptureAsync` rend le refus exact et aucune étape d'exécution ne démarre |
| Fraîcheur | `OnAccess` voit une modification ; `Every` seulement après l'intervalle |
| Bornes | un objet au-delà d'un `MaxObjectBytes` abaissé : problème local ; un listing au-delà d'un `MaxEntries` abaissé : échec du chargement |
| Panne | conteneur arrêté pendant un rechargement : l'image précédente reste servie, marquée périmée, `LastLoadError` renseigné |
| Ordre | RustFS rend les clés de la batterie en ordre UTF-8 strictement croissant (précondition de §4.5) |

### T3 — job CI `S3 provider AOT smoke test`

Sur le modèle d'`embedded-aot`, matrice `linux-x64`, `osx-arm64`, `win-x64`,
**deux fois** : versions minimales (`AWSSDK.S3` 4.1.0, `AWSSDK.Core` 4.1.0) et
dernières versions publiées au moment du run. `dotnet pack` d'`OKF4net` et du
provider vers un flux local à une version propre au run ; contrôle du `.nuspec`
(minimum `AWSSDK.S3`) et de `project.assets.json` (versions S3 **et** Core
résolues, affichées dans le journal) ; publication Native AOT de
`tests/S3ProviderAotProbe/` (hors `OKF4net.sln`). La sonde sert elle-même un
**faux S3 en boucle locale** sur un `TcpListener` (HTTP/1.1 minimal), charge un
petit bundle, compare son résumé au provider filesystem, et vérifie sous AOT le
garde : une 307 vers `<autre-bucket>.s3.<région>.amazonaws.com` est refusée
avec sa marque. Pour la **contre-épreuve**, la sonde construit, par le
constructeur interne, un client **sans garde** dont le `SocketsHttpHandler` a
pour proxy explicite un second `TcpListener` local : la 307 y arrive (compteur
du second écouteur à 1), sans toucher au fichier hosts ni à TLS (la sonde est en
`http://` avec `AllowInsecureHttp`). Aucun conteneur.

## 7. Livraison et documentation

### 7.1 Fichiers

- `src/OKF4net.Providers.S3/` et son README de paquet : **en tête**, la commande
  `S3Integration` et le contrôle « 0 skipped » ; la recette d'hôte
  (identifiants, `S3BundleSource`, `BundleHandle` avec `Every`), le coût de
  `OnAccess`, la mémoire (§4.3), les checksums, la région mal configurée, les
  préconditions sur le store (décodage unique du chemin, ordre UTF-8), ce que le
  garde ne couvre pas (§4.4), la limite A → B → A, la limite de #247, la plage de
  versions testée, la fabrique de catalogue écrite par l'hôte (renvoi à #239).
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
touché que par les deux commits de D15**, limités à `LoadCoreAsync` et
`ResolveResourceKeyAsync` dans `Bundle.cs` et à leurs tests. Ailleurs
(`OKF4net.sln`, `README.md`, `CHANGELOG.md`, `CLAUDE.md`, `ci.yml`,
`release.yml`), seulement des ajouts séparés.

### 7.3 Si `CanWrite` est déclaré un jour

Décision à part, non prise ici : correspondance de `BundleWriteCondition` sur
`If-None-Match: *` et `If-Match` de `PutObject` (409
`ConditionalRequestConflict` compris), alors que la version d'objet est un
SHA-256 et non un ETag (D8) ; sémantique d'une écriture dont la réponse se perd
(`BundleWriteOutcomeUnknownException`) ; suppression conditionnelle ; envoi
`aws-chunked` ; support réel de ces en-têtes par chaque store compatible.

## 8. Décisions de #141 appliquées (2026-11-15)

Consignées sur
[#141](https://github.com/jchable/okf4net/issues/141#issuecomment-6913533206).

1. Pas d'interne du cœur (D10), garde de test.
2. Bornes, validation des clés et D7 de #141 viennent du cœur ; le provider
   déclare `NativeConstraints`, `Id`, `Location`, `Capabilities` et les erreurs
   typées du contrat, et n'applique que ce qui lui revient (D16, délais, le
   parcours de `GetVersionAsync`, `MaxObjectBytes` avant de bufferiser, bornes de
   transport).
3. Pas de secret dans `catalog.json` : fabrique hors lot (#239).
4. Dépendance validée par le mainteneur (D1).
5. Tests du contrat sur les doubles existants ; hors CI seulement par une Trait,
   commande locale en tête du README.
6. Conflits : §7.2.
7. Gardes exécutables pour ce que xunit n'exécute pas : T3 et la porte §9.

**Écarts au commentaire de #142 du 2026-10-06**, consignés sur #142 : bornes par
défaut du cœur d'aujourd'hui (256 Mio, 10 M d'entrées, profondeur 1 024) au lieu
de 16 Mio, 1 M et 64 ; politique de clés par défaut `Native` (le défaut du cœur,
`BundleLoadOptions.KeyPolicy`), `Strict` restant disponible ; version d'objet
SHA-256 au lieu de l'ETag ou de `VersionId` (D8).

## 9. Non vérifié à ce jour — porte de la tâche 1 du plan

Les prototypes de l'annexe A ont été faits sous Windows, contre RustFS, en
path-style, avec de petits objets. **Première tâche du plan, avant tout code de
production** (y compris les commits de D15), un prototype qui mesure, **sur la
version minimale et sur la dernière version publiée** d'`AWSSDK.S3`/`AWSSDK.Core` :

1. **toutes les sorties réseau**, au niveau socket (un écouteur TCP pour chaque
   hôte possible par résolution détournée, un écouteur UDP pour CSM), sur des
   réponses 301, 307, `PermanentRedirect`, 400 `AuthorizationHeaderMalformed`
   avec et sans `<Region>`, et `x-amz-bucket-region`, avec et sans `AWS_CSM_*` :
   la détection de région et CSM se déclenchent-ils, par quel transport, vers
   quel hôte, en `http://` ? Peut-on les **empêcher pour ce client seul**, sans
   état global ? **Si non pour l'un d'eux, arrêt du plan et D1 rouvert** ;
2. la liste exhaustive des réglages lus de l'environnement et de
   `~/.aws/config` par les deux assemblies, et le moyen de neutraliser chacun
   (§4.4) ; vérification en processus enfant sur URI, en-têtes et signatures ;
3. les hôtes visés pour `Region` (*virtual-hosted*, path-style, buckets avec
   points) et pour `ServiceUrl` avec et sans `ForcePathStyle` — ils fixent la
   table de §4.4 ;
4. qu'une fabrique rendant toujours le même `HttpClient`, avec
   `UseSDKHttpClientCaching = false` et `DisposeHttpClientsAfterUse = false`,
   n'est ni partagée ni libérée par le SDK ;
5. que l'exception du garde n'est pas réessayée par le SDK, pour le type
   retenu ;
6. qu'un délai par jeton lié interrompt une lecture de corps qui cale et des
   réessais en attente, et que l'isolement des identifiants de §4.4 borne un
   fournisseur qui bloque ;
7. qu'un corps dont le checksum ne correspond pas est détecté, et par quelle
   exception, pour la traduction de §4.6 ;
8. que les corps de listing et d'erreur peuvent être bornés dans le handler
   **avant** que le SDK ne les désérialise, avec et sans `Content-Length` ;
9. la mémoire d'un chargement de plusieurs objets proches de `MaxObjectBytes`
   (§4.3) ;
10. le cycle de vie du conteneur RustFS piloté depuis xunit, sous Podman sans
    compatibilité Docker et sous Docker sous Linux ;
11. qu'une page `ListObjectsV2` aux noms hostiles en `EncodingType=url` est
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
sûreté de tous les stores compatibles ni celle de tous les chemins du SDK (la
détection de région et CSM n'y sont pas exercés), que la porte §9 et les tests
T1 à T3 couvrent.

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
