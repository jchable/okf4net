# Provider de bundle S3 — design

Date : 2026-11-16
Statut : **révision 5** (2026-11-19, #287). La révision 1 (design validé
section par section le 2026-11-15) a reçu une revue interne et une revue externe ;
la révision 2 les intégrait avec cinq arbitrages du mainteneur (D15, D5, D1, D8,
consignation sur les tickets) ; la révision 3 intégrait une seconde revue externe
et un arbitrage de plus (portée de D15, ticket #247) ; la révision 4 intégrait une
troisième revue externe (garde lié au bucket et au chemin, instantané
d'identifiants, décodage strict, limites de fraîcheur). **La révision 5 replie
dans le corps les écarts constatés à l'exécution et les décisions du mainteneur
du 2026-11-19** (ancienne Annexe C), ainsi que les ajouts d'API faits depuis
(`SigningRegion`, #284 ; `Anonymous`, #291) et l'abandon des ETags non MD5
(#286) : le corps dit ce que fait le code. L'Annexe C ne garde que les points
encore ouverts et la table de repli.
Ticket : [#142](https://github.com/jchable/okf4net/issues/142). Suites hors lot :
[#238](https://github.com/jchable/okf4net/issues/238) (`okf-mcp` sur S3),
[#239](https://github.com/jchable/okf4net/issues/239) (fabrique de catalogue S3,
emplacement à trancher avec [#236](https://github.com/jchable/okf4net/issues/236)),
[#247](https://github.com/jchable/okf4net/issues/247) (panne globale pendant la
lecture d'`index.md`/`log.md` par la validation ; réglé depuis, dans 0.8.0,
§4.7), [#292](https://github.com/jchable/okf4net/issues/292) (mesures hors
RustFS : MinIO fait, Amazon S3 réel non mesuré).

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
| D1 | Client S3 | **SDK AWS pour .NET, `AWSSDK.S3`**, plutôt qu'un client `HttpClient` + SigV4 écrit à la main. Arbitré le 2026-11-15 sur deux prototypes mesurés (annexe A) ; remplace la décision du 2026-10-05, prise sans mesure. Exception de dépendance propre au projet, comme `Catalog.Hosting` : rien d'autre dans `src/` ne référence ce projet. **Politique de version (2026-11-16)** : un **minimum** déclaré (`AWSSDK.S3 >= 4.1.0`, qui admet `AWSSDK.Core [4.1.0, 5.0.0)`), pas d'épinglage exact. Les garanties sont vérifiées à l'exécution et par des tests de sensibilité, **sur chaque combinaison testée** : la porte §9 et T3 portent sur la version minimale et sur la dernière publiée, et un second job CI fait passer toute la tranche de tests T1 du provider à la dernière publiée (§6, T3, #288) ; toute montée de S3 ou de Core les rejoue. |
| D2 | Qui construit le client, et comment | **Le provider**, à partir d'une `S3BundleSource` et d'un `AWSCredentials` fournis par l'hôte, ou, pour une source déclarée `Anonymous` (#291), sans identifiants, par un constructeur à part (§4.2) ; aucune surcharge n'accepte un `IAmazonS3`. Le provider ne lit jamais l'environnement, un fichier ou la chaîne d'identifiants par défaut (#139, §3), et **aucun réglage ambiant n'influence ce qu'il envoie, ni où** : il fixe explicitement chaque réglage que le SDK irait chercher dans l'environnement ou `~/.aws/config` (§4.4), et la sonde d'environnement hostile compare URI, en-têtes et signatures. Le trafic des fournisseurs d'identifiants (STS, IMDS, SSO) appartient à l'hôte : D3 ne le couvre pas. |
| D3 | Sorties réseau | **Garde obligatoire, non désactivable**, sur **une seule autorité** (schéma, hôte, port) **et un préfixe de chemin**, calculés depuis `S3BundleSource` et le mode d'adressage effectif : toute requête S3 qui ne vise pas exactement ce bucket sous ce préfixe est refusée avant l'envoi, avec une marque identifiable (§4.4). Deux hôtes admis à la fois permettraient à une redirection de changer de bucket sans changer de chemin. Mesuré nécessaire : le SDK suit une 307 vers tout hôte `*.amazonaws.com`, la re-signe et y envoie le jeton de session (annexe A). **Deux sorties du SDK échappent au `HttpClientFactory` et doivent être empêchées pour ce client, sans effet sur les autres clients AWS de l'hôte** : la détection de région (`BucketRegionDetector`, `HEAD` par un transport à part) et la télémétrie CSM (UDP, `AWS_CSM_*`). C'est la porte §9 ; si l'une ne peut pas l'être, le plan s'arrête et D1 est rouvert. |
| D4 | Écriture | **Lecture seule** : `WriteAsync`/`DeleteAsync` rendent `BundleReadOnlyException(Id)` sans rien tenter. |
| D5 | `Location` et `Id` | **Le nom du bucket n'atteint jamais le modèle** (#139, §3, sans amendement). `Location = "s3:<nom>/<prefix>"`, où `<nom>` est le `DisplayName` donné par l'hôte, ou à défaut les 8 premiers hex de l'`Id`. `Id = "s3:" + 32 hex` d'un SHA-256 de la configuration effective (§4.3), région de signature (`SigningRegion`) et accès anonyme (`Anonymous`) compris quand ils sont posés : stable, non vide, opaque. |
| D6 | Domaine de la configuration | `Prefix` : vide (tout le bucket) ou une suite de segments du socle `BundleKey`, **en Unicode bien formé**. `DisplayName` : un segment du socle en Unicode bien formé, sans `:`. `Bucket` : un nom de bucket S3 d'usage général (3 à 63 caractères, minuscules, chiffres, `-`, `.`, sans `..` ni forme d'adresse IPv4) ; ARN, points d'accès, alias, buckets de répertoire et de tables (suffixes `--x-s3`, `--ol-s3`, `-s3alias`, `--table-s3`) et préfixes réservés (`xn--`, `sthree-`, `amzn-s3-demo-`) refusés. Sans `ForcePathStyle`, un bucket qui contient `.` et un `ServiceUrl` dont l'hôte est une adresse IP sont refusés (le SDK passerait de lui-même en path-style, Annexe B, G3). `Region` et `ServiceUrl` : exactement un des deux ; une `Region` hors de la partition DNS `amazonaws.com` (Chine, `amazonaws.com.cn`) est refusée, ce cas passe par un `ServiceUrl`. `ServiceUrl` : une **origine** `http(s)` seule, sans userinfo, chemin, query ni fragment. `SigningRegion` : source `ServiceUrl` seulement, 2 à 32 caractères `[a-z][a-z0-9]*(-[a-z0-9]+)*`. `MaxObjectBytes` : de 1 à `Array.MaxLength`. Clé S3 = `<prefix>/<key>`, ou `<key>` sans préfixe : injective, à frontière de segment. |
| D7 | Erreurs | `S3RequestException : BundleProviderException` pour tout échec qui n'est pas propre à un objet : le cœur en fait un **échec de chargement**, jamais un état local (règle de priorité de #139). Les exceptions typées du contrat pour un objet absent, un refus propre à l'objet, une version différente, un objet trop grand ; la distinction se fait par une **table fermée** (statut et code reconnu, §4.6). Message à vocabulaire fermé ; aucun texte venu du serveur. |
| D8 | Versions | **Version d'objet = SHA-256 des octets lus, en hex minuscule**, au même format que le provider filesystem : l'épinglage ne dépend ni d'`If-Match` ni d'un ETag MD5. **Version du bundle = `s3v1:`** + SHA-256 d'un encodage canonique du listing, ETag compris : elle ne sert qu'à la fraîcheur. Pas d'`IBundleChangeNotifier` : la politique `Notify` est refusée à la construction du `BundleHandle` (`ArgumentException`), S3 n'envoyant aucun signal à un client qui interroge. |
| D9 | Listing | `EncodingType=url` toujours, chaque clé listée décodée une seule fois **strictement** (pourcent vers octets, un `+` brut lu comme une espace, puis UTF-8 sans remplacement, §4.3) ; corps de réponse borné **avant** désérialisation (§4.6) ; une page tronquée **sans clé** est suivie, et comptée dans un **budget de pages** (`MaxEntries + 1` pour un listing, 1 000 pour `ExistsAsync`) ; chaque page tronquée doit porter un jeton de continuation **nouveau** ; **échec fermé** sur toute incohérence de transport (§4.3). Un nom bien transporté mais invalide n'est **pas** une incohérence : il est rendu brut au cœur, qui le juge. |
| D10 | Internes du cœur | **Aucun.** Le listing est un itérateur `async` ; le cœur n'accorde aucun `InternalsVisibleTo` à ce projet (garde de test). Ce projet accorde les siens au seul projet de tests. |
| D11 | Transport | `https://` exigé ; `http://` seulement avec `AllowInsecureHttp = true`. |
| D12 | Checksums | Réglage explicite dans `S3BundleSource` (par défaut : ceux du SDK, mesurés acceptés par RustFS) ; jamais lu de l'environnement. |
| D13 | Périmètre | Bibliothèque seule. Pas de fabrique de catalogue (#239), pas d'option `okf-mcp` (#238), pas de notification S3, pas d'écriture. |
| D14 | Tests sans CI | Catégorie **`S3Integration`**, distincte de `ContainerIntegration`, contre `docker.io/rustfs/rustfs:1.0.3@sha256:9ca9559533b537d10482fe923288b676ad08f92a7069d9a17327fbae3cab688b` par défaut, et contre MinIO avec `OKF_S3IT_STORE=minio` (`cgr.dev/chainguard/minio@sha256:dc5e098902c102b848a72b96671c235f1f8464ccad60a62d1f205f8c221ce12b`, `RELEASE.2026-11-16T00-11-40Z` : MinIO Inc. ne publie plus son image communautaire, #292). Chaque image est épinglée par le condensé de son index (l'étiquette seule pourrait être déplacée, le condensé ne le peut pas). `ci.yml` et `release.yml` excluent les deux catégories. |
| D15 | Changements du cœur | **Deux, en tête du plan, en TDD, dans cette branche** (arbitrages du 2026-11-16) : (a) `LoadCoreAsync` lit la version du bundle **avant** de lister ; (b) `ResolveResourceKeyAsync` ne rend `Unsafe` que sur un refus d'accès, et toute autre défaillance du provider remonte **traduite par le cœur** (§4.7). (b) vaut **pour tous les providers**, filesystem compris, et s'étend au relevé des ressources exécutables, qui passe par la même résolution (confirmé par le mainteneur le 2026-11-19). Rien d'autre dans ce lot : la même confusion dans la lecture d'`index.md`/`log.md` par la validation, commune à tous les providers, est partie dans #247, réglé depuis (0.8.0, §4.7). |
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
    public string? SigningRegion { get; init; }      // portée SigV4 d'une source ServiceUrl ; null : us-east-1 (#284)
    public bool Anonymous { get; init; }             // bucket public lu sans signature (#291)
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
    /// ou une borne <= 0, et sur une source Anonymous.
    public S3BundleProvider(AWSCredentials credentials, S3BundleSource source);

    /// Bucket public (#291) : aucune E/S, aucune signature, aucun appel d'identifiants.
    /// ArgumentException hors du domaine de D6 et sur une source qui n'est pas Anonymous.
    public S3BundleProvider(S3BundleSource source);

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
`HttpClient` ; des constructeurs **internes**, pour les tests, ne font qu'y
injecter le `HttpMessageHandler` intérieur, sous le garde D3. Le handler réseau
n'est construit qu'une fois la source validée : une source refusée ne laisse
aucun objet réseau à libérer.

**Région de signature (#284).** Une source `ServiceUrl` signe avec la portée
SigV4 `SigningRegion ?? "us-east-1"` ; une source `Region` signe avec sa région
(`SigningRegion` y est refusé). Un store compatible qui vérifie la région de la
portée répond `400 AuthorizationHeaderMalformed` à une portée fausse, classé
`BucketUnavailable` (§4.6) : le README dit de soupçonner la région de signature
avant le bucket quand un store connu répond ainsi.

**Accès anonyme (#291).** Une source `Anonymous` se construit par
`S3BundleProvider(S3BundleSource)` : le client reçoit des identifiants anonymes
du SDK, aucune requête n'est signée et aucun identifiant de l'hôte n'est jamais
demandé. Les deux constructeurs ne se croisent jamais (celui qui prend des
identifiants refuse une source `Anonymous`, l'autre toute source qui ne l'est
pas) : une requête non signée est toujours une décision écrite dans la source,
jamais un identifiant manquant. Le garde est inchangé. Un bucket qui n'est pas
public répond 403, rendu `ClientError` (une lecture d'une clé :
`BundleAccessRefusedException`).

L'hôte possède les identifiants (tout `AWSCredentials`) et le provider ; le
provider possède son client et son `HttpClient`. Un `BundleHandle` ne libère pas
son provider. Un appel sur un provider libéré échoue en `S3RequestException`
(`Kind = Disposed`), jamais en `ObjectDisposedException` brute ; une opération
**en cours** quand `Dispose` est appelé est interrompue (attente du permis, des
identifiants, requête, corps) par un jeton de libération lié à chaque opération,
et échoue de même en `Disposed`.

### 4.3 Contrat

Toutes les opérations reposent sur un **itérateur privé d'entrées** S3 (nom
décodé, ETag, taille) ; `EnumerateAsync`, `GetVersionAsync` et `ExistsAsync` en
sont trois projections.

| Membre | Comportement |
|---|---|
| `Capabilities` | `CanWrite = false`, `CompletesSynchronously = false`. |
| `Id` | `"s3:" + hex(SHA-256(UTF-8("s3v1\n" + endpoint + "\n" + bucket + "\n" + prefix)))[..32]`, hex minuscule, où `endpoint` vaut `aws:<nom système de la région>` ou l'origine de `ServiceUrl` (schéma et hôte en minuscules, port explicite en décimal), suivie de `\|path` ou `\|vhost` selon `ForcePathStyle`, puis de `\|sign:<SigningRegion>` **seulement** quand `SigningRegion` est posé et de `\|anon` **seulement** quand `Anonymous` est vrai (dans cet ordre) : tout `Id` calculé avant ces options est inchangé (vecteurs connus dans `SourceTests`, calculés hors du code). Deux sources de même configuration effective ont le même `Id` (déduplication du catalogue, `FusedResolverEngine.cs:141`), deux sources différentes jamais. |
| `Location` | `s3:<DisplayName ou 8 hex de l'Id>` suivi de `/<prefix>` quand le préfixe n'est pas vide. Affichage seulement ; jamais le bucket ni l'endpoint (D5). |
| `NativeConstraints` | `BundleKeyConstraints.WellFormedUnicode`. |
| `EnumerateAsync` | Itérateur `async` sur `ListObjectsV2` (`Prefix = <prefix>/` plus le préfixe de clé demandé suivi de `/`, `MaxKeys = 1000`, `EncodingType=url`), une page à la fois, au plus `limits.MaxEntries + 1` pages : la borne d'entrées du cœur l'arrête par `DisposeAsync`, et aucun permis n'est tenu pendant qu'une entrée est rendue. Nom brut = clé décodée moins `<prefix>/` ; `Length = Size` ; `Version = null` (comme le filesystem ; le cœur ne garde que le nom brut). Aucune validation de nom. |
| `GetVersionAsync` | Listing complet par l'itérateur privé, borné par `limits.MaxEntries` et `limits.MaxDepth` comptés sur **tous** les noms (invalides et non Markdown compris ; `BundleLimitExceededException`), haché **en flux, dans l'ordre du listing** (§4.5). |
| `ReadAsync` | `GetObject` sans condition. Un `Content-Length` supérieur à `MaxObjectBytes` est refusé avant la lecture du corps (`BundleObjectTooLargeException`) ; le corps est lu **en entier**, sous le délai et le permis de l'opération, dans **un seul tampon** dimensionné par `Content-Length` (agrandi en comptant contre la même borne s'il est absent ; jamais plus de `MaxObjectBytes + 1` octets lus), puis haché : version = hex minuscule du SHA-256. Un corps plus court que son `Content-Length`, que le SDK ne détecte pas, est refusé par le provider (`Transport`). Avec `expected` différent : `BundleVersionMismatchException` ; aucun en-tête conditionnel n'est envoyé. Le `BundleObject` rendu porte un `MemoryStream` en lecture seule sur ce tampon, sans copie, et `Length` exact. |
| `ExistsAsync` | `ListObjectsV2(Prefix = <clé S3>, MaxKeys = 1, EncodingType=url)` : `true` si la première clé rendue est **exactement** la clé (elle trie avant toute clé qu'elle préfixe), `false` sinon. Les pages tronquées **sans clé** qui la précèdent sont suivies, au plus `ExistsMaxPages = 1000` pages en tout, chacune une opération avec son propre délai et son propre plafond d'envois ; au-delà, `InvalidResponse`, jamais `false`. Même droit que le listing ; un refus n'est jamais `false` (`S3RequestException`). |
| `WriteAsync` / `DeleteAsync` | `ValueTask.FromException(new BundleReadOnlyException(Id))`. |

**Incohérences de transport (D9), toutes en `S3RequestException` (`Kind =
InvalidResponse`)** : clé rendue hors du préfixe listé ; ordre des clés non
strictement croissant en octets UTF-8 (doublon et recul compris, **d'une page à
l'autre aussi**, pages vides comprises) ; jeton de continuation répété (mémorisé
par son SHA-256, 32 octets par page), absent ou vide quand la page est tronquée,
ou plus long que la borne de §4.6 ; page plus grande que `MaxKeys` (refusée dès
l'entrée de trop) ; **budget de pages dépassé** : plus de `MaxEntries + 1` pages dans
un parcours de listing, plus de 1 000 dans un `ExistsAsync`, pages vides
comprises ; ETag absent ou vide, taille absente, signée ou non décimale ; XML
indécodable, DTD comprise ; racine autre que `ListBucketResult` ; un champ de
page ou d'entrée (`Contents`, `EncodingType`, `IsTruncated`,
`NextContinuationToken`, `MaxKeys`, `Key`, `ETag`, `Size`) répété, ou dans un
autre espace de noms que celui de la racine — l'espace de noms S3
(`http://s3.amazonaws.com/doc/2006-03-01/`) ou aucun —, refusé plutôt que lu ou
ignoré (un store qui en emploie un autre, comme l'interopérabilité S3 de Google
Cloud Storage, est donc refusé : limite au README) ; `EncodingType` absent ou
différent de `url`, `IsTruncated` autre que `true`/`false` ; une clé dont le
décodage échoue. Les autres éléments (`Prefix`, `CommonPrefixes`, `KeyCount`…)
sont ignorés avec leur sous-arbre ; seul `Key` est décodé.

**Page tronquée sans clé (décision du mainteneur, 2026-11-19).** Elle n'est
**pas** une incohérence : Amazon S3 peut en rendre sur un bucket versionné, sous
une longue suite de marqueurs de suppression (non mesuré). Elle est suivie, à
condition que son jeton soit nouveau, et **comptée** dans le budget de pages.
C'est le budget, avec le refus d'un jeton répété, qui arrête un store qui ne
finit jamais, en pages et en mémoire. Ce budget est partagé avec la borne
d'entrées : sous un `MaxEntries` très bas, des pages vides l'épuisent et l'échec
est un `InvalidResponse`, pas une borne d'entrées ; des pages plus petites que
`MaxKeys`, que S3 autorise, ne sont jamais refusées tant que le budget tient. Un
store hostile peut donc coûter jusqu'à `MaxEntries + 1` requêtes, ce que le
README dit en conseillant d'abaisser `MaxEntries` pour un bucket non fiable.

**Décodage d'une clé listée.** Chaque `%XX` devient un octet, un `+` **brut**
devient une espace (0x20), tout autre caractère ses octets UTF-8 ; la suite
d'octets est décodée en UTF-8 **strict** (sans remplacement :
`new UTF8Encoding(false, throwOnInvalidBytes: true)`). C'est le décodage en
formulaire (`S3KeyCodec.TryDecodeListed`), qu'emploient Amazon S3 (cru, non
mesuré, #292) et MinIO (mesuré le 2026-11-19 : une espace en `+` brut, un `+` en
`%2B`) ; RustFS, qui écrit l'espace en `%20`, se décode de même (décision du
mainteneur du 2026-11-19). Un `%` non suivi de deux chiffres hexadécimaux, une
séquence UTF-8 invalide (`%FF`, la forme trop longue `%C0%AF`, le surrogate
encodé `%ED%A0%80`), un surrogate isolé : `InvalidResponse`. Jamais
`Uri.UnescapeDataString`, qui laisse `%FF` tel quel et confond `%FF` avec
`%25FF`. Ce qui est **envoyé** ou vérifié par le garde (chemin, valeurs de
requête) se décode strictement **sans** cette règle (`TryDecode` : un `+` y
reste un `+`), et le garde **refuse un `+` brut** dans toute valeur de requête
admise, qu'un store lirait comme une espace (§4.4). Une erreur de convention
échoue fermé dans les deux lectures possibles (ordre D9, ou 404 à la lecture).
Un nom correctement transporté mais invalide (`bad%01.md`, `a//b.md`) est rendu
brut : le cœur en fait un problème local et charge le reste.

Le cœur, pas le provider, applique `MaxKeyBytes`, la validation des clés, sa
propre concurrence de `LoadAsync` et D7 de #141 (§8). Un chargement coûte la
version du bundle (un listing), le listing des noms (un second) et un `GET` par
objet lu ; aucun cache ne les fusionne (YAGNI, à rouvrir sur mesure).

**Cohérence d'un chargement.** Pas de transaction entre objets. Un objet qui
disparaît entre le listing et sa lecture a l'effet que le cœur donne déjà à une
absence, selon ce qu'il est :

| Objet disparu | Effet (code du cœur, inchangé) |
|---|---|
| un concept | le chargement échoue (`BundleObjectNotFoundException` → `BundleLoadException`) ; l'image précédente reste servie, marquée périmée |
| une ressource du relevé (`computation`, attester) | état `Missing` (`Bundle.cs:598`) ; le chargement réussit, l'exécution est refusée |
| l'`index.md` racine | `okf_version` absent (`Bundle.cs:975`) ; le chargement réussit |

**Fraîcheur.** Avec la version lue en premier (D15 a), un changement survenu
pendant le chargement est rechargé à la **prochaine comparaison que la politique
autorise** (à l'accès suivant sous `OnAccess`, après l'intervalle sous `Every`)
**s'il change le jeton**, c'est-à-dire un nom, un ETag ou une taille du listing.
Deux limites, assumées, documentées au README et fixées chacune par un test :

1. **A → B → A** : un changement défait avant la comparaison peut laisser une
   image qui mêle des octets intermédiaires sous un jeton redevenu identique (la
   même limite que le filesystem) ;
2. **ETag constant** : un remplacement **durable** par d'autres octets de même
   taille et de même ETag (collision, ou store dont l'ETag ne suit pas le
   contenu) ne change pas le jeton et n'est **jamais** rechargé par la
   politique. Le code sanctionné reste protégé, puisque sa relecture épinglée
   compare le SHA-256 (D8) et refuse ; le reste de l'image (textes, liens)
   reste périmé jusqu'au prochain changement visible ou à un `RefreshAsync` de
   l'hôte.

**Mémoire.** Pas de lecture en flux (D8). Une lecture en vol tient au pire, pour
un objet de `B` octets : `B` dans le tampon du provider, `B` dans la copie que le
cœur fait de ce flux (`Bundle.cs:753`) et jusqu'à `2B` pour la chaîne UTF-16
décodée, soit **`4B`** avant que le provider ne soit libéré ; le relevé des
ressources exécutables passe par le même chemin. Sans `Content-Length`, le
tampon du provider grandit par doublement (au pire `2B` transitoires de plus).
Aux valeurs par défaut (16 lectures en vol, 256 Mio), cela fait jusqu'à 16 Gio,
plus les textes retenus par le préchargement (`Bundle.cs:463`) ; le plafond de
#151 reste la réponse à la mémoire d'un bundle entier. Le README conseille
d'abaisser `MaxObjectBytes`, la borne du cœur et `MaxConcurrentRequests` pour un
bucket non fiable ; la porte §9 mesure ce pic sur plusieurs objets proches de la
borne, avec et sans `Content-Length`.

### 4.4 Configuration du SDK, garde et identifiants (D2, D3)

**Configuration hermétique.** `AmazonS3Config` reçoit explicitement
(`ClientBuilder`) : `IgnoreConfiguredEndpointUrls = true` ;
`RetryMode = Standard`, `MaxErrorRetry = 2` (3 tentatives) ;
`UseFIPSEndpoint = false`, `UseDualstackEndpoint = false`,
`UseAccelerateEndpoint = false`, `UseArnRegion = false`,
`DisableMultiregionAccessPoints = true` ;
`USEast1RegionalEndpointValue = Regional` (sous
`AWS_S3_US_EAST_1_REGIONAL_ENDPOINT=legacy`, variable ou profil, une source
`us-east-1` visait sinon `s3.amazonaws.com` et toute opération était refusée par
le garde) ; `AccountIdEndpointMode = PREFERRED` (`AWS_ACCOUNT_ID_ENDPOINT_MODE`
changeait sinon la métrique du `User-Agent`) ;
`DefaultConfigurationMode = Standard` (défense en profondeur : mesuré, le
provider envoie les mêmes requêtes sans ce réglage sous `AWS_DEFAULTS_MODE`
`auto`, `mobile` ou `in-region` et ne contacte aucun IMDS ; la ligne est gardée,
décision du mainteneur) ; `DisableClockSkewCorrection = false` ;
`AllowAutoRedirect = false`, `CacheHttpClient = false`,
`Timeout = InfiniteTimeSpan` ; `ClientAppId = ""` (un `ClientAppId` nul retombe
sur `AWS_SDK_UA_APP_ID` et `sdk_ua_app_id`, `" "` laisse `app/` : Annexe B, G4) ;
la région (`Region`, ou la région d'authentification
`SigningRegion ?? "us-east-1"` pour `ServiceUrl`, §4.2) ; `ForcePathStyle` ;
`RequestChecksumCalculation` et `ResponseChecksumValidation` depuis
`Checksums`. **CSM est désactivé et la détection de région empêchée pour ce
client** (sous-classe `ProviderS3Client`, Annexe B, G1). `AuthSchemePreference`
**n'est pas fixé** : mesuré, `AWS_AUTH_SCHEME_PREFERENCE=sigv4a` (avec
`AWS_SIGV4A_SIGNING_REGION_SET`) ne change rien, le SDK n'offrant que SigV4 pour
un bucket ordinaire (ARN, points d'accès multirégions et buckets de répertoire
sont refusés à la construction, D6). Toutes les chaînes `AWS_*` et toutes les clés
de profil d'`AWSSDK.Core` et d'`AWSSDK.S3` 4.1.0 ont été relevées sur les
binaires et mesurées une à une (relevé systématique du 2026-11-19) : chaque
réglage ambiant trouvé qui change où va une requête ou ce qu'elle porte est
surchargé, et épinglé avec un **témoin positif** (un client ordinaire change sous
lui) par `HostileEnvironmentTests` (processus enfant) ou `AmbientEndpointTests`
(en processus, sources `Region`), à une exception près : l'orthographe *profil*
du réglage us-east-1 (`s3_us_east_1_regional_endpoint`, lue une fois par
processus) n'a été mesurée qu'en processus frais, sans test permanent (décision
du mainteneur). Ce qu'aucun réglage ne retire, le garde le retire (plus bas).

**Un `HttpClient` possédé par le provider.** Le `HttpClientFactory` du provider
rend toujours **le même** `HttpClient`, créé à la construction :
`UseSDKHttpClientCaching = false`, `DisposeHttpClientsAfterUse = false`,
`HttpClient.Timeout = InfiniteTimeSpan` (le délai est celui de l'opération,
§4.5), sur la chaîne `PinningHandler` (le garde) → `BoundingHandler` (bornes de
transport, §4.6) → `SocketsHttpHandler { AllowAutoRedirect = false,
UseProxy = false, UseCookies = false, PooledConnectionLifetime = 2 min }` (pas de
cookie rejoué que le garde ne verrait pas ; une durée de connexion finie, pour
qu'un hôte de longue durée résolve à nouveau le nom du store). Le provider le
libère une fois, dans `Dispose`.

**Garde : une autorité et un préfixe de chemin.** Le mode d'adressage effectif
est fixé à la construction, jamais laissé au SDK : path-style quand
`ForcePathStyle` est vrai, *virtual-hosted* sinon (et la porte §9 vérifie que le
SDK n'en change pas, buckets à points compris ; un bucket qu'il ne sait pas
adresser ainsi est refusé à la construction). Le provider en tire **une seule**
autorité admise et le préfixe de chemin qui désigne le bucket :

| Configuration | Autorité admise | Le chemin doit commencer par |
|---|---|---|
| `ServiceUrl`, `ForcePathStyle` | l'origine de `ServiceUrl` | `/<bucket>/` |
| `ServiceUrl`, *virtual-hosted* | `<bucket>.<hôte de ServiceUrl>` (même schéma et port) | `/` |
| `Region`, `ForcePathStyle` | `s3.<région>.amazonaws.com` (HTTPS, 443) | `/<bucket>/` |
| `Region`, *virtual-hosted* | `<bucket>.s3.<région>.amazonaws.com` (HTTPS, 443) | `/` |

Le `PinningHandler` refuse, sans rien envoyer, toute requête faite hors d'une
opération du provider, toute méthode autre que `GET` (comparée ordinalement :
chaque opération est un `GET`, aucun `HEAD` n'est jamais envoyé), et compare
schéma, hôte et port de `RequestUri` — et d'un en-tête `Host` explicite, lu
brut — à l'autorité admise. Il vérifie ensuite que le chemin brut
(`AbsolutePath`, sans décodage) est, pour une lecture, exactement le préfixe de
la table suivi de l'encodage attendu de `<prefix>/<key>` (`S3KeyCodec.EncodePath`,
l'encodage que le SDK envoie, Annexe B, G3), sans aucune query ; pour un listing,
exactement le préfixe de la table, avec une query tirée de la liste fermée
`list-type`, `prefix`, `encoding-type`, `continuation-token`, `max-keys` (chacun
au plus une fois, `list-type=2` et `encoding-type=url` présents, aucun `+` brut
dans une valeur, chaque valeur décodable strictement) et un `prefix` égal, une
fois décodé, au préfixe attendu. Tout écart lève une exception marquée
(`Kind = RefusedHost`). Aucune redirection ne peut ainsi faire lire un autre
bucket ou une autre clé, même vers une autorité qui serait admise pour une autre
configuration (`demo.s3.<région>…/kb/code.py` contre `s3.<région>…/kb/code.py`).
Les autorités des régions de la partition `amazonaws.com` sont celles de la
table ; les autres partitions sont refusées à la construction (D6).

**Une région mal configurée** n'est jamais corrigée en silence (la détection de
région est empêchée), mais elle ne se manifeste pas toujours par un refus du
garde : une 307 vers un autre hôte est refusée par le garde (`RefusedHost`) ;
un 301 `PermanentRedirect` (ou 301 sans corps) et un 400
`AuthorizationHeaderMalformed` sont des réponses du store, classées
`BucketUnavailable` (§4.6). Aucun de ces cas n'a été mesuré contre Amazon S3
réel (#292) ; le README dit les deux.

**Plafond d'envois.** Le garde refuse tout envoi au-delà de **3 par opération**
(`PinningHandler.MaxSendsPerOperation`, les 3 tentatives de
`MaxErrorRetry = 2`), réessais et redirections compris (`Kind = InvalidResponse`).
Une 307 répondue indéfiniment vers une URL que le garde **admet** (même autorité,
même chemin) faisait boucler le SDK sans borne (Annexe B, conséquence 4 :
3,6 Go en quelques minutes) ; elle est refusée au quatrième envoi, après trois
envois. Aucun chemin légitime n'a besoin de plus ; plafond confirmé par le
mainteneur le 2026-11-19. Pour un listing, l'opération est une page : le plafond
ne s'étend jamais sur plusieurs pages.

**Ce que le garde retire.** Ni le `User-Agent` ni `x-amzn-trace-id` ne sont
signés (mesuré : `SignedHeaders` = `host`, `x-amz-content-sha256`, `x-amz-date`
et, pour une lecture, `x-amz-checksum-mode`), si bien que le garde les modifie
sans toucher à la signature (décision du mainteneur du 2026-11-19) :

- l'en-tête `x-amzn-trace-id`, que le SDK ajoute dans AWS Lambda
  (`AWS_LAMBDA_FUNCTION_NAME` et `_X_AMZN_TRACE_ID` posés) avec la valeur
  ambiante, est retiré, quelle que soit sa casse ;
- le `User-Agent`, et `x-amz-user-agent` où le SDK met son texte quand l'hôte
  pose `AWSConfigs.UseAlternateUserAgentHeader` (non signé non plus, mesuré), est
  **reconstruit** depuis une liste fermée d'emplacements relevée sur `AWSSDK`
  4.1.0 : `aws-sdk-dotnet-coreclr/…`, `ua/…`, `os/<nom>#<version>`,
  `md/ARCH#…`, `lang/<nom>#<version>`, `md/aws-sdk-dotnet-core#…`, `ft/aot`,
  `api/S3#…`, `md/ClientAsync|ClientSync`, `cfg/init-coll#…`, `m/<codes>`,
  écrits dans cet ordre. Chaque valeur est un **nombre** (un à quatre groupes de un
  à cinq chiffres) ou un élément d'un **ensemble fermé** (architectures, noms de
  système et de langage, codes de métrique `b e E F N P R`), jamais du texte
  libre. Tout autre jeton est retiré (échec fermé) — `exec-env/`
  (`AWS_EXECUTION_ENV`), `app/` (`AWS_SDK_UA_APP_ID`) et le texte de
  `AWS_INTERNAL_ENV` n'atteignent donc jamais le fil — ; un emplacement vu deux
  fois avec des valeurs différentes (texte ambiant qui imite un jeton) est retiré
  entier ; sans aucun jeton reconnu, aucun `User-Agent` n'est envoyé. Un SDK plus
  récent, ou une plateforme hors des ensembles fermés (mesurés sous Windows
  seulement), perd de l'information, jamais une requête.

**Sensibilité.** Le seul cas que le SDK suit sans garde est la **307** avec
`Location` (son gestionnaire de redirection transforme les autres 3xx en
erreurs) : c'est sur elle que portent les contre-épreuves (sans garde, la
requête atteint la cible ; avec, elle est refusée avec sa marque), vers un autre
hôte, **et entre les deux autorités d'un même bucket, dans les deux sens**. Le
chemin inchangé ne désigne un autre bucket que **dans le sens *virtual-hosted* →
régional** (`/kb/a.md` de `demo.s3…` vers `s3…` lit le bucket `kb`) ; dans le
sens *path-style* → *virtual-hosted*, le SDK retire le segment du bucket et garde
le chemin de la clé, en ignorant le chemin du `Location` : la contre-épreuve de
ce sens le constate, et le garde ferme les deux sens de la même façon (il compare
l'autorité et le chemin brut). Les 301 et `PermanentRedirect` sont testés comme
refus natifs du SDK, traduits par §4.6.

**Identifiants : un instantané, jamais le fournisseur de l'hôte dans le
pipeline.** `AWSCredentials.GetCredentialsAsync` ne prend pas de jeton et peut
bloquer de façon synchrone, et le SDK rappelle les identifiants à chaque
signature, réessai compris — y compris par `GetCredentials()` synchrone dans le
chemin de redirection. Le client S3 ne reçoit donc **pas** l'`AWSCredentials` de
l'hôte, mais un `AWSCredentials` interne au provider, dont
`GetCredentials`/`GetCredentialsAsync` rendent sans attendre l'instantané
(`ImmutableCredentials`) courant et ne touchent jamais le fournisseur de
l'hôte. `ImmutableCredentials` ne portant pas d'expiration, le provider résout
un instantané **par opération**, en appelant le fournisseur de l'hôte
**isolé** (`Task.Run`) sous une attente bornée par le délai de l'opération (un
fournisseur rafraîchissant de l'hôte met lui-même en cache). À
l'expiration : `S3RequestException` (`Kind = Credentials`), le résultat tardif
est ignoré, et la tâche abandonnée occupe un permis de concurrence jusqu'à sa
fin réelle : au plus `MaxConcurrentRequests` fils bloqués par un fournisseur
défaillant, jamais une accumulation sans borne. Un résultat nul est un échec
`Credentials`. Rien n'est envoyé à S3 sans instantané : hors d'une opération,
l'`AWSCredentials` interne lève (`Credentials`). Les identifiants sont résolus
**avant** que le contexte de l'opération ne soit posé : `Task.Run` fait suivre
l'`ExecutionContext`, et un contexte posé d'abord atteindrait le code de l'hôte.
Une source `Anonymous` (§4.2) ne résout rien : le client porte des identifiants
anonymes et rien n'est signé.

### 4.5 Délais, priorité des échecs et version du bundle

**Délai par opération.** Chaque opération du provider (une page de listing, une
lecture complète corps compris, un `ExistsAsync`) s'exécute sous un jeton lié au
jeton de l'appelant, à un jeton de libération du provider et à un délai :
`source.RequestTimeout`, ou pour `EnumerateAsync`/`GetVersionAsync`
`limits.RequestTimeout` quand il est positif et plus court (une valeur infinie,
nulle ou négative ne l'emporte jamais), plafonné dans tous les cas à
`int.MaxValue` millisecondes (environ 24,8 jours). Le délai court dès l'appel :
il couvre l'attente du permis, l'acquisition des identifiants, les réessais du
SDK et leur attente, et la lecture du corps.

**Le délai est par page, non par parcours** (statué le 2026-11-19 : gardé tel
quel). Un store qui répond chaque page juste avant le délai tient un parcours
jusqu'au produit du budget de pages (§4.3 : `MaxEntries + 1` pour un listing,
1 000 pour `ExistsAsync`) par le délai ; c'est le budget de pages qui borne un
parcours, et le README le dit.

**Priorité des échecs.** Une opération retient **un seul échec principal**, le
premier constaté, et le rend :

1. si le jeton de l'appelant était annulé avant cet échec, l'annulation
   (`OperationCanceledException`, qui porte **le jeton de l'appelant lui-même**) ;
2. sinon une borne du cœur ou du provider (`BundleObjectTooLargeException`,
   `BundleLimitExceededException`) si c'est elle ;
3. sinon l'échec traduit par §4.6.

Une `OperationCanceledException` qui ne vient pas du jeton de l'appelant (délai,
fournisseur d'identifiants, SDK) est un échec (`Kind = Timeout`, `Credentials`
ou, si le provider a été libéré, `Disposed`), jamais une annulation. Un échec pendant le nettoyage (libération
du flux, de la réponse) ne masque jamais l'échec principal ; **seul**, il est
classé comme tout autre échec par §4.6 (un checksum faux constaté à la fermeture
est donc `Integrity`).

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

**Classement, par une table fermée** (statut HTTP et code S3 reconnu, **par
couples** : un code ne compte qu'avec le statut sous lequel S3 l'envoie ; tout
autre couple tombe dans la ligne de son statut) :

| Origine | Devient |
|---|---|
| 404 `NoSuchKey` sur `GetObject` | `BundleObjectNotFoundException(key)` |
| 403 `AccessDenied` sur `GetObject` | `BundleAccessRefusedException(key, "access denied")` |
| octets lus différents d'`expected` | `BundleVersionMismatchException(key)` |
| `Content-Length` ou octets lus au-delà de `MaxObjectBytes` | `BundleObjectTooLargeException` |
| 403 `InvalidAccessKeyId`, `SignatureDoesNotMatch` ; 403 **ou 400** `ExpiredToken`, `InvalidToken`, `TokenRefreshRequired` (S3 a envoyé les deux statuts ; précision de la tâche 8 du plan) ; échec du fournisseur d'identifiants | `S3RequestException` (`Credentials`) |
| 404 `NoSuchBucket` ; 301 `PermanentRedirect`, ou 301 sans corps (le SDK nomme alors l'erreur d'après le statut, `MovedPermanently`, mesuré sur 4.1.0) ; 400 `AuthorizationHeaderMalformed`, `IllegalLocationConstraintException` | `S3RequestException` (`BucketUnavailable`) |
| 503 `SlowDown`, 429 | `S3RequestException` (`Throttled`) |
| autre 5xx | `S3RequestException` (`ServerError`) |
| autre 4xx (403 et 404 aux codes non reconnus compris) | `S3RequestException` (`ClientError`) |
| autre 3xx (302, 308, 307 sans `Location`…) | `S3RequestException` (`InvalidResponse`) |
| **règle terminale** : toute `AmazonServiceException` (dont `AmazonS3Exception`) qu'aucune ligne ne classe, quel que soit son statut | `S3RequestException` (`InvalidResponse`) |
| délai dépassé | `S3RequestException` (`Timeout`) |
| refus du garde | `S3RequestException` (`RefusedHost`) |
| incohérence de §4.3, XML indécodable, réponse au-delà d'une borne de transport | `S3RequestException` (`InvalidResponse`) |
| checksum de réponse faux ; ETag MD5 véritable (32 chiffres hexadécimaux, sans `-<parts>`) qui ne correspond pas aux octets — le SDK le vérifie lui-même à la lecture (mesuré sur 4.1.0) | `S3RequestException` (`Integrity`) |
| `FormatException` levée en lisant une réponse, nue ou portée dans la chaîne d'`InnerException` | `S3RequestException` (`InvalidResponse`) |
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

**ETags que le SDK ne peut pas vérifier (#286).** Le SDK contrôle le corps d'un
`GetObject` réussi contre un ETag simple (sans `-`) comme un MD5 : il en décode
l'hexadécimal et compare. Un ETag qui n'est pas 32 chiffres hexadécimaux n'est
pas un MD5, et ce contrôle ne pouvait qu'échouer à tort (mesuré sur 4.1.0 : un
ETag non hexadécimal levait `FormatException`, un condensé SHA-1 ou SHA-256
hexadécimal échouait en faux `Integrity`), si bien qu'un store compatible aux
ETags opaques échouait sur toute lecture. Le `BoundingHandler` **retire donc
l'en-tête `ETag` d'une réponse de lecture réussie** quand il n'est ni un MD5
véritable (32 chiffres hexadécimaux) ni de forme multipart (qui contient `-`, que
le SDK ne vérifie pas) : le SDK n'a plus rien à contrôler. Un vrai MD5 qui ne
correspond pas reste `Integrity`. L'intégrité du provider ne dépend jamais de
l'ETag : la version est toujours le SHA-256 des octets lus (D8). Une
`FormatException` qui atteindrait encore le classement est `InvalidResponse`
(décision du mainteneur du 2026-11-19) : la réponse du store est malformée, pas
le transport.

**`Integrity` repose sur le texte du SDK.** `AWSSDK.Core` 4.1.0 n'a pas
d'exception typée pour un condensé faux : le classement reconnaît le message du
SDK (« Expected hash not equal to calculated hash »), cherché le long de la
chaîne d'`InnerException`, `AggregateException` comprise (profondeur 16,
64 exceptions examinées au plus). Un SDK plus récent qui changerait ce texte
donnerait `Transport` : échec fermé, gardé par deux tests sur le vrai pipeline,
que le job CI à la dernière version du SDK rejoue (§6, T3).

**Message à vocabulaire fermé.** `"S3 <opération> failed: <kind>"`, plus
`" (HTTP <code>)"` et la clé du bundle quand il y en a une. Le code d'erreur S3
n'entre jamais dans le message, ni dans une propriété : c'est un texte du
serveur. Le diagnostic brut reste dans `InnerException`, canal du journal de
l'hôte (#139, §3).

**Bornes de transport, appliquées avant désérialisation.** Corps d'une page de
listing (et d'un `ExistsAsync`) **8 Mio** ; corps d'une réponse d'erreur (tout
statut ≥ 300, `GetObject` compris) **64 Kio** ; jeton de continuation **1 024
octets**. Le handler lit le corps d'origine jusqu'à `B + 1` octets au plus (un
octet de détection, pour distinguer la fin d'un excédent, avec ou sans
`Content-Length`), n'en garde que `B`, et **ne rend une réponse au SDK qu'après**
: une réponse égale à la borne est rendue sur un contenu neuf en mémoire ; au-delà,
l'opération échoue (`InvalidResponse`) et le SDK ne reçoit aucune réponse. Le SDK
ne voit donc jamais le flux d'origine : il ne peut pas commencer à désérialiser
avant la validation, par construction. Le corps d'un `GetObject` réussi n'est
pas concerné (borné par `MaxObjectBytes`, §4.3).

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
provider — remonte **traduite par le cœur** : une `BundleLoadException` de
message `provider failure resolving '<clé>' (<Type>)`, qui nomme la clé et le
type de l'exception, jamais son texte, avec l'original en cause. Une panne
pendant la validation la fait donc échouer au lieu d'émettre « escapes the
bundle », et aucun texte de provider n'atteint le modèle par `OkfBundleTools`.
**Portée** (confirmée par le mainteneur le 2026-11-19) : le changement vaut pour
**tous les providers**, filesystem compris, et pour le relevé des ressources
exécutables (`Bundle.SurveyResourceAsync`, qui passe par
`ResolveResourceKeyAsync`) : toute défaillance du provider autre qu'un refus,
y compris une `IOException` ou une `UnauthorizedAccessException` brutes, y fait
désormais **échouer le chargement** (image précédente conservée, périmée) là où
elle consignait l'état `Unsafe`. Aucun golden n'a bougé. Tests : un provider dont `ExistsAsync` lève chacune de ces
familles, dont une `IOException` portant une URL et un secret sentinelles ; le
résultat réel de `ValidateBundleAsync` ne contient ni l'un ni l'autre. Les
goldens n'en sont pas affectés (aucun provider de fixture ne lève) ; le plan le
vérifie.

**Hors D15, dans #247** : au moment de ce lot, `ReadResourceTextAsync`
traduisait toute `BundleProviderException` en `IOException`, que la validation
absorbait en `UnparseableIndex`/`UnparseableLog` : une panne réseau pendant la
lecture d'`index.md` ou de `log.md` par la validation donnait un diagnostic
local au lieu d'un échec, pour tout provider. **#247 l'a réglé depuis (0.8.0)** :
une défaillance du provider pendant la lecture d'une image chargée lève
`BundleLoadException` (`provider failure reading '<clé>' (<Type>)`), et la
validation n'absorbe plus que les problèmes propres au fichier
(`IOException`, `UnauthorizedAccessException`, `DecoderFallbackException`).

## 5. Sécurité, récapitulatif

| Menace | Réponse |
|---|---|
| Redirection vers un hôte arbitraire, fuite du jeton de session | garde D3 sur une autorité, mesuré (M3c) ; contre-épreuve sur la 307 ; au plus 3 envois par opération, une 307 admise répondue sans fin n'étant jamais envoyée une quatrième fois (§4.4) |
| Redirection entre autorités admissibles d'un même bucket (dans le sens *virtual-hosted* → régional, le chemin inchangé désigne un autre bucket) | une seule autorité et un préfixe de chemin vérifiés par requête (§4.4) ; contre-épreuves dans les deux sens |
| Requête d'une autre nature (écriture, `HEAD`, paramètre de listing inattendu, `+` brut lu comme une espace par le store) | garde : `GET` seul, query de lecture vide, liste fermée de paramètres de listing, `+` brut refusé (§4.4) |
| Fournisseur d'identifiants rappelé par le SDK hors de tout délai (signatures, réessais, redirection synchrone) | instantané interne au provider (§4.4) ; le fournisseur de l'hôte n'est appelé qu'isolé |
| Alias de clés par un décodage laxiste (`%FF` contre `%25FF`) | décodage strict (§4.3) |
| Détection de région, télémétrie CSM (transports hors `HttpClientFactory`) | empêchées pour ce client (D3) ; porte §9, arrêt du plan sinon |
| Configuration ambiante (endpoints, proxy, FIPS, double pile, point de terminaison us-east-1, mode de point de terminaison par compte, mode par défaut, réessais, checksums, CSM, User-Agent, en-tête de trace Lambda) | configuration hermétique (D2, §4.4) relevée sur les binaires ; `User-Agent` reconstruit depuis une liste fermée et `x-amzn-trace-id` retiré par le garde ; sonde en environnement hostile sur URI, en-têtes et signatures, avec témoin positif |
| Requête non signée par accident | une source `Anonymous` est explicite, ses deux constructeurs ne se croisent jamais (§4.2) |
| Trafic des fournisseurs d'identifiants | hors garde, à l'hôte ; isolé et borné par le délai de l'opération (§4.4) |
| Normalisation du chemin par le client | le SDK envoie la clé telle quelle (M4) ; représentation sur le fil vérifiée (T1) |
| Clé hors du bundle | préfixe à frontière de segment (D6), échec fermé sur clé rendue hors préfixe (D9). Pour `GET`, le confinement suppose un store qui décode le chemin une fois sans normaliser les points : hypothèse énoncée au README, mesurée sur RustFS (T2) |
| Alias de préfixes (surrogates isolés → U+FFFD) | `WellFormedUnicode` sur le préfixe (D6) |
| Alias de clés (`/` initial, NFC/NFD, casse) | `/` initial impossible dans une `BundleKey` ; NFC/NFD et casse distincts côté S3, signalés comme non portables sous `Strict` |
| Listing hostile (page démesurée, jeton géant, pages vides sans fin, jeton répété, XML invalide ou d'un autre espace de noms) | bornes de transport avant désérialisation, budget de pages comptant les pages vides, refus d'un jeton répété, invariants de page (§4.3, §4.6) ; le délai étant par page, un parcours lent est borné par ce budget (§4.5) |
| ETag opaque (non MD5) faisant échouer le contrôle du SDK | en-tête retiré d'une lecture réussie ; version = SHA-256 des octets (§4.6, #286) |
| Réponse d'erreur démesurée | borne de 64 Kio avant désérialisation (§4.6) |
| Objets démesurés | `Content-Length` et comptage (D8) ; bornes du cœur |
| Code sanctionné remplacé, collision MD5, store qui ignore `If-Match` | version = SHA-256 des octets (D8) |
| Panne globale déguisée en état local | table fermée (§4.6), D15 (b) pour tous les providers ; la lecture d'`index.md`/`log.md` par la validation, réglée par #247 (§4.7) |
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
| Domaine de la configuration | `ArgumentException` pour chaque valeur hors D6 (préfixe `/x`, `x/`, `a//b`, `..`, surrogate isolé ; `DisplayName` avec `/` ou `:` ; bucket ARN, `--x-s3`, majuscules, formes réservées ; `ServiceUrl` avec userinfo, chemin, query ; `SigningRegion` mal formée ou sur une source `Region` ; `MaxObjectBytes` hors domaine) ; aucune requête émise ; une source refusée ne laisse aucun objet réseau vivant (`SourceTests`) |
| Région de signature (#284) | un store scripté qui impose la région de la portée lue dans l'en-tête `Authorization` refuse la source par défaut (400, `BucketUnavailable`) et accepte la source déclarée ; la portée par défaut reste `us-east-1` (`SigningRegionTests`) |
| Accès anonyme (#291) | une source `Anonymous` liste et lit sans aucun en-tête `Authorization` ni appel d'identifiants, contre-épreuve signée ; les deux constructeurs ne se croisent jamais ; `\|anon` dans l'`Id` seulement quand il est posé (`AnonymousTests`) |
| Injectivité | deux préfixes différents, U+FFFD réel compris, donnent des clés S3 différentes sur le fil ; `kb` ne demande jamais `kb2/…` ; préfixe vide = tout le bucket |
| Représentation sur le fil | pour chaque nom de la batterie, la cible de la requête est exactement l'encodage attendu de la clé |
| Incohérences de transport | une par ligne de §4.3 : l'énumération échoue (`InvalidResponse`), le chargement aussi, rien n'est servi ; un document hostile passé par le vrai SDK est `InvalidResponse` |
| Pages tronquées vides | suivies, et une page suivante porteuse de clés se charge ; comptées dans le budget de pages, qui arrête un store sans fin ; l'ordre des clés tient d'une page à l'autre à travers une page vide ; `ExistsAsync` les suit, s'arrête à son budget de 1 000 et refuse un jeton répété (`ListingWalkerTests`) |
| Décodage strict | vecteurs `%FF`, `%C0%AF`, `%ED%A0%80`, `%`, `%4` : `InvalidResponse` ; `%25FF`, `%25C0%25AF` : noms littéraux `%FF`, `%C0%AF` ; deux noms qui ne diffèrent que par `%FF` contre `%25FF` ne se confondent jamais ; une espace et un `+` reviennent sous leur nom, qu'un store écrive l'espace en `+` brut (Amazon S3, MinIO) ou en `%20` (RustFS) ; `EncodingType` absent ou autre dans la réponse : `InvalidResponse` (`CodecTests`, `ListPageParserTests`, `ListingWalkerTests`) |
| Noms invalides bien transportés | `bad%01.md`, `a//b.md` : rendus bruts, problème local du cœur, le reste du bundle chargé |
| Bornes de transport | lecture bornée : une page de 8 Mio passe, 8 Mio + 1 échoue, et le handler n'a jamais lu plus de `B + 1` octets du corps d'origine, avec et sans `Content-Length` ; désérialisation non commencée : le corps d'origine est un flux témoin qui enregistre ses lecteurs, et le SDK n'en reçoit jamais la référence (une réponse rejetée ne parvient pas au SDK : aucun appel de son démarshalleur, observé par un compteur dans le handler de test placé sous le SDK) ; une erreur `GetObject` de 64 Kio + 1 et un jeton de 1 025 octets échouent de même |
| Pagination et borne | plus de 1 000 objets sur plusieurs pages ; **beaucoup de petites pages valides** (pages de 1 à 100 clés, `MaxEntries = 1000`) sont acceptées tant que la borne d'entrées ne l'est pas ; une borne d'entrées atteinte arrête le listing et le libère |
| `ExistsAsync` | correspondance exacte (`a.md` contre `a.md.bak`), `false` sur absence, `S3RequestException` sur refus |
| Lecture | version = SHA-256 hex minuscule des octets (égale à celle du filesystem pour les mêmes octets) ; `expected` différent → `VersionMismatch` ; d'autres octets sous le même ETag → `VersionMismatch` (ETag multipart ; un ETag MD5 véritable est refusé avant par le SDK, `Integrity`, §4.6) ; un ETag simple opaque (non hexadécimal, ou SHA-1/SHA-256 hexadécimal) n'est plus contrôlé par le SDK et la lecture réussit, tandis qu'une lecture épinglée compare toujours les octets (`ReadTests.An_opaque_single_part_ETag_is_not_the_SDKs_to_check_and_the_read_succeeds` ; mutation : retirer aussi les vrais MD5 fait échouer le test MD5) ; `Content-Length` trop grand refusé sans lire le corps ; corps plus long que son `Content-Length` refusé à la borne ; flux rendu en lecture seule, sans copie |
| Classement des erreurs | une ligne de la table de §4.6 par test, **par le vrai pipeline du SDK**, injectée avant les en-têtes, pendant le corps et à la libération, dont 302, 308, 307 sans `Location` et un statut 4xx/5xx inconnu (règle terminale) ; une `FormatException` est `InvalidResponse` où qu'elle soit dans la chaîne, une défaillance de transport reste `Transport` (`ErrorTests.A_format_failure_is_InvalidResponse_wherever_it_sits_and_a_transport_failure_is_not`) ; une panne d'identifiants pendant le relevé des ressources exécutables fait échouer le chargement (jamais `Unsafe`) |
| Priorité des échecs | matrice de doubles échecs (borne puis annulation, annulation puis échec de libération, checksum faux puis délai, `OperationCanceledException` venue des identifiants sans annulation de l'appelant) : type, `Kind` et cause retenus selon §4.5 ; un checksum faux constaté **seul** à la fermeture : `Integrity` |
| Messages | des valeurs sentinelles (bucket, endpoint, id de requête, URL, `\n`, U+2028) placées dans **chaque champ distant** (`<Code>`, `<Message>`, `<RequestId>`, `<HostId>`, `<Endpoint>`, `<Region>`, en-têtes) n'apparaissent dans aucun `Message` d'exception, ni dans les `Path` des diagnostics de validation, ni dans les diagnostics du catalogue |
| Délais | un corps qui cale, une suite de réessais, une attente de permis : `Timeout` dans le délai ; le jeton de l'appelant est transmis et reste une annulation (modèle : `AsyncProviderContractTests`) |
| Identifiants | un fournisseur qui bloque de façon synchrone, une tâche jamais terminée, une fin tardive : `Credentials` dans le délai, rien d'envoyé, permis tenu jusqu'à la fin réelle, résultat tardif ignoré ; un fournisseur qui **réussit une fois puis bloque** : les signatures suivantes, réessais et chemin de redirection compris, utilisent l'instantané sans jamais rappeler le fournisseur hors de l'isolement (compteur d'appels du fournisseur) |
| Concurrence (D16) | plus de `N` appels mixtes (listing, lecture, `ExistsAsync`) : jamais plus de `N` opérations en vol, corps suspendus compris ; une annulation en file libère sa place ; un échec de nettoyage rend le permis |
| `HttpClient` | même instance sur des appels successifs et concurrents ; libérée une seule fois par `Dispose` ; deux providers d'origines différentes n'échangent ni `HttpClient` ni garde |
| Garde | la 307 vers `<autre-bucket>.s3.<région>.amazonaws.com` **et** la 307 entre les deux autorités d'un même bucket, dans les deux sens (`demo.s3.<région>…/kb/x` ↔ `s3.<région>…/kb/x`) : refus marqué, rien d'envoyé, pas de réessai, **et contre-épreuve sans garde** pour chacune (la requête atteint la cible) ; un chemin de lecture ou un `prefix` de listing altérés sur la bonne autorité, une autre méthode que `GET`, un en-tête `Host` explicite étranger, un paramètre de listing hors liste ou répété, un `+` brut : refusés ; une 307 répondue indéfiniment sur une URL admise s'arrête à 3 envois (`GuardTests.A_307_answered_forever_stops_at_three_sends`) ; 301 et `PermanentRedirect` : refus natifs du SDK, traduits ; l'autorité et le préfixe de chemin de chaque ligne de la table de §4.4, relevés par un handler qui refuse tout ; la reconstruction du `User-Agent` ligne par ligne (emplacement vide, ensemble fermé, emplacement ambigu, en-tête alternatif `x-amz-user-agent`) |
| Environnement hostile | une sonde en **processus enfant** (`HostileEnvironmentTests`) sous les variables et clés de profil du relevé systématique (§4.4), dont `AWS_ENDPOINT_URL_S3`, `AWS_ENDPOINT_URL`, `AWS_USE_FIPS_ENDPOINT`, `AWS_USE_DUALSTACK_ENDPOINT`, `AWS_RETRY_MODE`, `AWS_MAX_ATTEMPTS`, `AWS_SDK_UA_APP_ID` et `AWS_EXECUTION_ENV` avec un marqueur, `AWS_CSM_*` vers un écouteur UDP, les modes de checksum, `AWS_AUTH_SCHEME_PREFERENCE`, `AWS_ACCOUNT_ID_ENDPOINT_MODE`, `AWS_S3_US_EAST_1_REGIONAL_ENDPOINT=legacy`, `AWS_DEFAULTS_MODE`, le cas Lambda (`AWS_LAMBDA_FUNCTION_NAME`, `_X_AMZN_TRACE_ID`, `AWS_INTERNAL_ENV`), `HTTPS_PROXY`, `HTTP_PROXY`, et un `AWS_CONFIG_FILE` hostile : mêmes URI, en-têtes (hors date et identifiant d'invocation) et signatures qu'en environnement vide, à horloge fixée, **et un client ordinaire perturbé dans le même cas** (témoin positif) ; aucun marqueur ni en-tête de trace sur le fil ; aucun datagramme reçu. `AmbientEndpointTests` (en processus, sources `Region`) épingle de même les points de terminaison ambiants et la variable us-east-1 |
| `Id`, `Location` | `Id` stable, opaque, égal pour deux configurations effectives égales, différent sinon ; `Location` sans bucket, avec `DisplayName` ou les 8 hex |
| Version `s3v1` | vecteurs attendus (dont U+E000 / U+10000) ; ajout, suppression, renommage, taille ou ETag changent le jeton ; découpage en pages et culture ne le changent pas |
| Fraîcheur, triplet constant | un faux S3 remplace durablement des octets à nom, ETag et taille constants : le jeton ne change pas et l'image n'est pas rechargée (limite 2 de §4.3), tandis que la relecture épinglée du code changé est refusée (`ReadTests.A_persistent_change_with_a_constant_triplet_is_not_reloaded_but_pinned_code_is_refused`) ; un store réel ne peut pas produire ce triplet, d'où sa place en T1 |
| Écritures | `WriteAsync`/`DeleteAsync`, `BundleConceptWriter`, `IndexGenerator` : `BundleReadOnlyException`, rien n'est envoyé ; `OkfBundleTools` n'offre aucun outil d'écriture |
| Pont synchrone | avec un handler **réellement suspendu**, une lecture synchrone lève `InvalidOperationException` (`SyncBridge.IncompleteMessage`) et annule la requête ; une écriture synchrone est refusée avant de commencer, en `BundleReadOnlyException` (le contrôle `CanWrite` précède `RequireSynchronousProvider`) |
| D7 de #141, frère de `NoResidualFileIoTests` | les membres `[Obsolete]` à chemin refusent ce provider ; les points d'entrée de lecture, répertoire courant sur un dossier vide, n'y écrivent rien ; un segment `c:x.md` sous Windows (jamais un concept : `:` est interdit, spec OKF §2) : la **clé exacte** sur le problème d'analyse et sur le problème de portabilité |
| Gardes de dépendance | le cœur n'accorde aucun `InternalsVisibleTo` à `OKF4net.Providers.S3` ; l'assembly ne référence qu'`OKF4net`, `AWSSDK.*` et la BCL |

### T2 — `[Trait("Category", "S3Integration")]`, hors CI

Un fixture choisit le moteur explicitement (`podman` s'il répond, sinon
`docker`), vérifie l'accès au moteur, démarre le store que nomme
`OKF_S3IT_STORE` — `rustfs` (défaut, `docker.io/rustfs/rustfs:1.0.3`) ou
`minio` (`cgr.dev/chainguard/minio`, `RELEASE.2026-11-16T00-11-40Z`), chacun
épinglé par le condensé de son index (D14), une autre valeur faisant échouer le
run — sur un port libre avec des identifiants générés, crée un bucket,
téléverse, et arrête le conteneur à la fin. Moteur absent : chaque test est
**rapporté ignoré** avec la raison. Moteur présent mais conteneur en échec (un
condensé élagué par Chainguard compris) : les tests **échouent**. Garantie :
`dotnet test tests/OKF4net.Tests --filter Category=S3Integration` avec
**0 skipped**, une fois par store (**37 tests** : 12 scénarios et 25 cas de
parité, cinq théories sur cinq bundles), en tête du README du paquet et dans
CLAUDE.md. RustFS : Podman sans compatibilité Docker et Docker ; MinIO : Podman
(l'image démarre aussi sous Docker). **Amazon S3 réel n'a jamais été mesuré**
(#292, Annexe C).

**Ce que chaque store ne peut pas prouver** (README, section Testing) : RustFS
envoie une espace en `%20` et un `+` en `%2B`, jamais un `+` brut ; il **ne
discrimine donc pas** le `+` brut d'un nom listé. MinIO écrit une espace en `+`
brut : le test des noms épingle la forme propre à chaque store, et c'est la passe
MinIO qui exerce le décodage en formulaire (mesuré : avec la règle du `+` brut
inversée, la passe MinIO échoue, la passe RustFS non) ; en CI, seuls les tests T1
(le vrai SDK contre des pages scriptées) le gardent. MinIO n'a envoyé aucune page
tronquée vide, et aucun des deux stores ne couvre les ETags simples non MD5, la
limitation de débit (`SlowDown`), un jeton expiré, une région fausse ni une
vérification de la région de signature. Le SDK ne peut pas `PUT` une clé à `/`
initial vers RustFS : la fixture signe cette requête elle-même (SigV4,
`UNSIGNED-PAYLOAD`), avec l'horloge de l'hôte, qui doit s'accorder avec celle du
moteur à moins de 15 minutes près.

| Test | Ce qu'il prouve |
|---|---|
| Parité (`okf_v02`, `okf_v02_computation`, `okf_v02_reserved`, `appendix_a`, `bundles/acme_retail`) | comme les `ParityTests` de l'embarqué : mêmes clés et octets ; mêmes faits de chargement ; diagnostics (sans `Path`), audit, graphe, recherche ; `ExecutableResources` par clé, état **et version** (même format des deux côtés) ; relecture épinglée par `ReadResourceTextAsync` ; index générés capturés par un double inscriptible. Aucune fixture n'est écrite ni modifiée |
| Refus du serveur | `a/../b`, `a//b`, `a/./b` : téléversement refusé, 400 attendu, rien de listé |
| Noms acceptés, oracle indépendant | `%2F`, `%2e%2e`, `%25`, `%252e`, `+`, espace, `\`, `?`, `#`, NFC et NFD de `café.md`, `dir/`, `/kb/lead.md` (normalisé par RustFS), `kb2/…` à côté de `kb/…` : chaque objet a un **contenu distinctif** ; le listing est comparé à celui d'un client d'administration distinct, sans `EncodingType` (chemin de décodage indépendant : texte XML), `mc` n'étant plus garanti disponible (image MinIO retirée de Docker Hub) ; chaque objet servi par le provider a le contenu de sa clé ; ce que le cœur rejette suit son socle sous `Native` |
| Longueur | `MaxKeyBytes` abaissé, plutôt qu'une clé de plus de 1 024 octets que RustFS refuse |
| Lecture épinglée | après réécriture d'un objet, `ReadResourceTextAsync(key, expected, …)` lève `BundleVersionMismatchException` ; `AttestationOrchestrator.CaptureAsync` rend le refus exact et aucune étape d'exécution ne démarre |
| Fraîcheur | `OnAccess` voit une modification qui change le jeton ; `Every` seulement après l'intervalle. Le remplacement durable à triplet constant (limite 2 de §4.3) n'est **pas** un test T2 : un store réel ne peut pas le produire, il est en T1 (« Fraîcheur, triplet constant ») |
| Erreurs réelles (#292) | bucket absent (404 `NoSuchBucket` → `BucketUnavailable`), clé d'accès inconnue (403 `InvalidAccessKeyId` → `Credentials`), secret faux (403 `SignatureDoesNotMatch` → `Credentials`), clé absente (`BundleObjectNotFoundException`) : le statut et le code lus en `InnerException`, jamais dans le message (`Real_store_errors_land_in_the_documented_kinds`) |
| Accès anonyme (#291) | une politique de bucket rend un préfixe public : une source `Anonymous` le charge sans signature, le préfixe privé voisin est refusé par le store (`An_anonymous_source_reads_a_public_prefix_and_is_refused_on_a_private_one`) |
| Courses | un concept, une ressource du relevé et l'`index.md` racine supprimés entre le listing et leur lecture : échec du chargement avec image précédente servie et périmée, état `Missing`, `okf_version` absent (table de §4.3) |
| Bornes | un objet au-delà d'un `MaxObjectBytes` abaissé : problème local ; un listing au-delà d'un `MaxEntries` abaissé : échec du chargement |
| Panne | conteneur arrêté pendant un rechargement : l'image précédente reste servie, marquée périmée, `LastLoadError` renseigné |
| Ordre | le store rend les clés de la batterie en ordre UTF-8 strictement croissant (précondition de §4.5) ; assertion portée par le test des noms acceptés, pas un test à part |

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
garde **par l'API publique du paquet** : une 307 vers un autre hôte et une 307
entre autorités du même bucket sont refusées avec leur marque (job `S3 provider
AOT smoke test (<rid>, AWSSDK <minimum|latest>)` de `ci.yml`). Pour la
**contre-épreuve**, la sonde n'utilise aucun interne du provider (D10) : elle
construit **directement un `AmazonS3Client` témoin**, sans garde, dont le
`HttpClientFactory` rend un `HttpClient` dont le `SocketsHttpHandler` a pour
proxy explicite un second `TcpListener` local ; la même 307 y arrive (compteur
du second écouteur à 1). Rien ne touche le fichier hosts ni TLS (la sonde est en
`http://` avec `AllowInsecureHttp`). Aucun conteneur.

**Second job : `S3 provider tests at the latest AWSSDK`** (`ci.yml`, job
`s3-latest-sdk`, #288). Sur `ubuntu-latest`, il fait monter les deux projets de
test (`OKF4net.Tests` et la sonde `S3HostileEnvProbe`) à la dernière version
publiée d'`AWSSDK.S3` et d'`AWSSDK.Core`, vérifie que les assemblies chargées
sont bien celles-là (un run resté au minimum échoue), puis exécute toute la
tranche T1 du provider (`FullyQualifiedName~Providers.S3&Category!=S3Integration`),
sonde d'environnement hostile comprise. Le job AOT ne vérifie que le garde ; ce
job rejoue les témoins qui dépendent du comportement du SDK (texte d'`Integrity`,
réglages ambiants, pipeline) sur la dernière version.

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
3. les autorités et chemins visés pour `Region` (*virtual-hosted*, path-style,
   buckets avec points, partitions hors `aws`) et pour `ServiceUrl` avec et sans
   `ForcePathStyle` — ils fixent la table de §4.4 — et que le SDK ne change
   jamais de mode d'adressage de lui-même ; qu'une 307 entre les deux autorités
   d'un même bucket change bien de bucket sans changer de chemin (le défaut que
   le préfixe de chemin du garde ferme) ;
4. que le SDK rappelle les identifiants à chaque signature, et par
   `GetCredentials()` synchrone dans le chemin de redirection ; qu'un
   `AWSCredentials` interne rendant un instantané suffit à ce qu'il ne rappelle
   jamais celui de l'hôte ; la valeur de `ClientAppId` qui neutralise le repli
   sur `AWS_SDK_UA_APP_ID` et `sdk_ua_app_id` ;
5. qu'une fabrique rendant toujours le même `HttpClient`, avec
   `UseSDKHttpClientCaching = false` et `DisposeHttpClientsAfterUse = false`,
   n'est ni partagée ni libérée par le SDK ;
6. que l'exception du garde n'est pas réessayée par le SDK, pour le type
   retenu ;
7. qu'un délai par jeton lié interrompt une lecture de corps qui cale et des
   réessais en attente, et que l'isolement des identifiants de §4.4 borne un
   fournisseur qui bloque ;
8. qu'un corps dont le checksum ne correspond pas est détecté, et par quelle
   exception, pour la traduction de §4.6 ;
9. que les corps de listing et d'erreur peuvent être bornés dans le handler
   **avant** que le SDK ne les désérialise, avec et sans `Content-Length` ;
10. la mémoire d'un chargement de plusieurs objets proches de `MaxObjectBytes`
   (§4.3) ;
11. le cycle de vie du conteneur RustFS piloté depuis xunit, sous Podman sans
    compatibilité Docker et sous Docker sous Linux ;
12. qu'une page `ListObjectsV2` aux noms hostiles en `EncodingType=url` est
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

## Annexe B — résultats de la porte (tâche 1 du plan, 2026-11-16)

Deux prototypes jetables (hors dépôt) : `AWSSDK.S3` 4.1.0 + `AWSSDK.Core` 4.1.0
(minimum) et `AWSSDK.S3` 4.1.0 + `AWSSDK.Core` 4.1.0.1 (dernières publiées à
cette date). Windows 11, .NET 10 ; Podman 5.6.1 ; Docker 28.3.3 sous Ubuntu 24.04
(WSL, harnais compilé en .NET 9 pour ce seul point). Journaux bruts dans le
scratchpad de la session. Sauf mention, **résultat identique sur les deux
versions**.

| # | Point de §9 | Observé | Verdict |
|---|---|---|---|
| G1 | Sorties réseau hors `HttpClientFactory` | **CSM** : un client ordinaire envoie 2 datagrammes UDP par appel sous `AWS_CSM_ENABLED` ; une sous-classe d'`AmazonS3Client` qui retire `CSMCallAttemptHandler` et `CSMCallEventHandler` dans `CustomizeRuntimePipeline` (point d'extension **par client**, appelé après l'ajout des gestionnaires CSM) en envoie 0. **Détection de région** : l'inventaire statique de l'IL (appelants transitifs) donne un seul chemin vers le transport à part, `AmazonS3RetryPolicy.SharedRetryForExceptionAsync` → `BucketRegionDetector.DetectMismatchWithHeadBucketFallbackAsync` → `GetBucketRegionNoPipelineAsync` → `AmazonS3HttpUtil.GetHeadAsync` ; remplacer, dans la même sous-classe, le `RetryHandler` par `new RetryHandler(new StandardRetryPolicy(Config))` le rend inatteignable, et les 503 restent réessayés (2 envois). Aucun scénario (301, 400 avec ou sans `<Region>`, 403 avec ou sans `x-amz-bucket-region`, 307 ; `Region` et `ServiceUrl` ; avec et sans identifiants ambiants) n'a déclenché le `HEAD` sur un client ordinaire : pas de témoin dynamique. | **conforme** (neutralisation par client, structurelle) |
| G2 | Réglages ambiants | Variables lues : `AWS_ENDPOINT_URL[_*]`, `AWS_IGNORE_CONFIGURED_ENDPOINT_URLS`, `AWS_USE_FIPS_ENDPOINT`, `AWS_USE_DUALSTACK_ENDPOINT`, `AWS_MAX_ATTEMPTS`, `AWS_SDK_UA_APP_ID`, `AWS_REQUEST_CHECKSUM_CALCULATION`, `AWS_AUTH_SCHEME_PREFERENCE`, `AWS_DISABLE_CLOCK_SKEW_CORRECTION`, `AWS_EXECUTION_ENV`, `AWS_ACCOUNT_ID`, `AWS_REGION`, `AWS_DEFAULT_REGION`, `AWS_CONFIG_FILE`, `AWS_PROFILE`, `AWS_S3_US_EAST_1_REGIONAL_ENDPOINT`, `AWS_S3_DISABLE_MULTIREGION_ACCESS_POINTS`, `AWS_S3_DISABLE_EXPRESS_SESSION_AUTH`, `AWS_CSM_*`, et les variables d'identifiants. Sous toutes ces variables et un profil hostile, le client durci (configuration de §4.4, `ClientAppId = ""`, sous-classe de G1) envoie des requêtes identiques à l'environnement vide (ligne, chemin, requête, `SignedHeaders`, valeurs signées, portée), **sauf** le jeton `exec-env/<valeur>` que `AWS_EXECUTION_ENV` ajoute au `User-Agent` et qu'aucun réglage du client ne retire. Le `User-Agent` n'est pas signé (`SignedHeaders=host;x-amz-checksum-mode;x-amz-content-sha256;x-amz-date`). Témoin : le client ordinaire est perturbé (échecs). | **conforme**, avec retrait du jeton par le handler du provider (conséquence 2) |
| G3 | Autorités, chemins, échange de bucket, encodage | `us-east-1` → `s3.us-east-1.amazonaws.com` ; `cn-north-1` → partition `amazonaws.com.cn`. **Le SDK passe de lui-même en path-style** pour un bucket contenant `.` (région et `ServiceUrl`) et pour un `ServiceUrl` dont l'hôte est une adresse IP. Échange confirmé : une 307 entre les deux autorités garde le chemin (`/kb/a.md` de `demo.s3…` vers `s3…`, bucket `kb`). L'encodage du chemin envoyé est celui de `S3KeyCodec.EncodePath` pour les 26 noms de la batterie. | **conforme** : §4.4 prévoit de refuser à la construction ce que le SDK ne sait pas adresser comme demandé (conséquence 3) |
| G4 | Appels d'identifiants ; `ClientAppId` | Un `AWSCredentials` hôte passé au client est appelé à chaque opération **par `GetCredentials()` synchrone et deux fois par l'asynchrone**, réessais compris ; derrière un `AWSCredentials` qui rend un instantané, l'hôte n'est jamais appelé (0, réessai et redirection compris). `ClientAppId = ""` neutralise `AWS_SDK_UA_APP_ID` (aucun `app/` dans le `User-Agent`) ; `" "` laisse `app/` ; `null` laisse `app/AMBIENTMARK`. Une 307 vers la même URL sur un `ServiceUrl` IP lève `ArgumentException` (le gestionnaire de redirection S3 refuse un hôte non S3). | **conforme** |
| G5 | Durée de vie du `HttpClient` | Avec `UseSDKHttpClientCaching=false` et `DisposeHttpClientsAfterUse=false`, chaque client n'utilise que son `HttpClient` ; le SDK n'en libère aucun, ni pendant l'usage ni à la libération du client. | **conforme** (le provider le libère lui-même) |
| G6 | Exception du garde non réessayée | Une exception dérivant d'`Exception`, levée par un handler : 1 appel, remontée telle quelle (`MaxErrorRetry = 2`), client ordinaire comme durci. | **conforme** |
| G7 | Délais | Corps bloqué qui honore le jeton : annulé à 1 s par le jeton seul. Corps « sourd » au jeton : bloqué au-delà de 6 s par le jeton seul, débloqué à 1 s par la libération de la réponse (`ObjectDisposedException`). Tempête de 503 : coupée à 1 s (`TaskCanceledException`). Identifiants bloquants sous `Task.Run(...).WaitAsync(1 s)` : rendus à 1 s. | **conforme** (les deux mécanismes de §4.5 sont nécessaires) |
| G8 | Intégrité | `WHEN_SUPPORTED` : `AmazonClientException` « Expected hash not equal to calculated hash », levée **pendant la lecture**. `WHEN_REQUIRED` : non détecté. | **conforme** ; le README dit que `WhenRequired` ne vérifie pas les réponses |
| G9 | Bornage avant désérialisation ; `AsyncLocal` | Un handler qui rend une **nouvelle** réponse (`ByteArrayContent`) est désérialisé normalement ; au-delà de la borne, l'exception du handler remonte et le flux d'origine n'a été lu que par lui (1 ouverture). La valeur `AsyncLocal` posée avant l'appel est vue par le handler à la première tentative et à la tentative réessayée. | **conforme** |
| G10 | Mémoire (16 × 64 Mio, Markdown UTF-8 valide) | Avec `Content-Length` : pic de 4 069 Mio au-dessus de la base (borne 4B × 16 = 4 096 Mio). Sans : 2 363 Mio (borne 6B × 16 = 6 144 Mio). Les 16 concepts chargés en entier. | **conforme** |
| G11 | RustFS piloté par un processus de test | Podman sans aucun `docker` sur le `PATH` : prêt en 3,4 s, retiré, rien ne reste. Docker 28.3.3 sous Linux (WSL) : prêt en 29 s (tirage de l'image), retiré, rien ne reste. | **conforme** |
| G12 | Décodage et ordre du listing | `ListObjectsV2Request` n'a pas de propriété `EncodingType` : c'est **`Encoding = EncodingType.Url`**. La réponse porte `EncodingType=url` et le SDK **ne décode pas** les clés (`S3Object.Key` encodé) ; le décodage strict coïncide avec le listing non encodé pour les 26 noms ; ordre strictement croissant par point de code. RustFS refuse `a/../b`, `a//b`, `a/./b` (400). | **conforme** |

**Conséquences pour l'implémentation** (consignées comme décisions d'exécution
dans le plan ; aucune ne contredit une décision D1–D16) :

1. **Client** (G1) : le provider construit une sous-classe interne
   d'`AmazonS3Client` dont `CustomizeRuntimePipeline` retire les deux
   gestionnaires CSM s'ils sont présents et remplace le `RetryHandler` par une
   `StandardRetryPolicy`. Le test de non-régression est structurel : le pipeline
   du client du provider ne contient aucun gestionnaire CSM ni
   `AmazonS3RetryPolicy`, alors que celui d'un `AmazonS3Client` ordinaire en
   contient une (contre-épreuve) ; le test CSM dynamique (datagrammes) reste.
2. **`User-Agent`** (G2) : le handler du provider retire du `User-Agent` tout
   jeton `exec-env/…` et `app/…` (non signés) ; `ClientAppId = ""` (G4).
3. **Adressage** (G3) : refusés à la construction, sans `ForcePathStyle`, un
   bucket contenant `.` (quel que soit l'endpoint) et un `ServiceUrl` dont
   l'hôte est une adresse IP. Le SDK ne change alors jamais de mode pour une
   configuration admise.
4. **Nombre d'envois par opération** (constat hors des douze points) : une 307
   répondue indéfiniment vers une URL que le garde admet fait boucler le SDK
   sans borne (mesuré : 3,6 Go en quelques minutes, arrêt forcé). Le garde
   refuse donc tout envoi au-delà de **3 par opération** (les 3 tentatives de
   `MaxErrorRetry = 2`), redirections comprises (`Kind = InvalidResponse`).
5. **API** (G12) : `ListObjectsV2Request.Encoding = EncodingType.Url` (la
   propriété `EncodingType` n'existe pas).
6. **Test de 307 sur la même autorité** (G4) : il se fait sur une source
   `Region` ; sur un `ServiceUrl` IP, le SDK refuse la redirection par
   `ArgumentException`.

## Annexe C — Points ouverts et écarts repliés (révision 5, 2026-11-19)

Jusqu'à la révision 4, cette annexe consignait les écarts constatés à
l'exécution (tâches 1 à 12, 2026-11-18) sans réécrire le corps, puis les
décisions du mainteneur du 2026-11-19. La révision 5 (#287) les replie dans le
corps, qui dit désormais ce que fait le code. Ne restent ici que les points
encore ouverts, avec leur ticket, les arbitrages qui ferment un point sans
changer le code, et la table de repli. Les mesures historiques sont dans les
annexes A et B.

### C.1 Points ouverts

- **Amazon S3 réel jamais mesuré (#292).** Toutes les mesures portent sur
  RustFS 1.0.3 et MinIO `RELEASE.2026-11-16T00-11-40Z`. Restent à mesurer sur
  Amazon S3 : le décodage en formulaire des clés listées (§4.3, cru, non
  mesuré), une région fausse (307, 301 `PermanentRedirect`, 400
  `AuthorizationHeaderMalformed`, §4.4), la limitation de débit (`SlowDown`), un
  jeton expiré, les pages tronquées vides d'un bucket versionné sous des
  marqueurs de suppression (D9, §4.3) et les ETags SSE-KMS. Même ticket : la
  vérification de la région de signature par un store compatible
  (`SigningRegion`, §4.2 : MinIO avec une région de site, Wasabi, Scaleway,
  Backblaze B2, OVH, Garage, aucun mesuré ; la passe MinIO a tourné avec la
  région de site par défaut).

### C.2 Arbitrages sans changement de code (2026-11-19)

- **Délai par page, non par parcours** : gardé ; un parcours est borné par le
  budget de pages, propriété documentée (§4.5, README).
- **`DefaultConfigurationMode = Standard`** : gardé comme défense en profondeur
  inerte (mesuré sans effet ; §4.4).
- **Orthographe *profil* du réglage us-east-1** (`s3_us_east_1_regional_endpoint`) :
  mesurée en processus frais seulement, sans test permanent (§4.4).
- **`Notify`** : refusé à la construction du `BundleHandle` (`ArgumentException`),
  le provider n'étant pas un `IBundleChangeNotifier` (D8).
- **Espace de noms XML du listing** : seul celui de S3 ou aucun ; limite gardée,
  à rouvrir sur la demande d'un store réel (§4.3).

### C.3 Table de repli

| Entrée | Titre | Où elle vit désormais |
|---|---|---|
| C1 | Décodage d'une clé listée (`+` brut → espace), `+` brut refusé par le garde | §4.3 « Décodage d'une clé listée », §4.4 (garde), D9 ; mesure Amazon S3 : C.1 (#292) |
| C2 | 307 entre les deux autorités d'un même bucket : autre bucket dans le seul sens *virtual-hosted* → régional | §4.4 « Sensibilité », §5 |
| C3 | Au plus 3 envois par opération | §4.4 « Plafond d'envois », §5 |
| C4 | D15 (b) pour tous les providers et pour le relevé des ressources exécutables | D15, §4.7 (b) |
| C5 | Mesures de l'exécution sur §4.6 (a ETag MD5 → `Integrity` ; b 301 sans corps → `BucketUnavailable` ; c `FormatException` → `InvalidResponse` et ETags non MD5 retirés, #286 ; d corps court → `Transport` ; e identifiants résolus avant le contexte ; f `Disposed` en cours d'opération ; g jeton propre de l'appelant ; h `Integrity` sur le texte du SDK) | §4.6 (a, b, c, h), §4.3 `ReadAsync` (d), §4.4 identifiants (e), §4.2 (f), §4.5 (g) |
| C6 | Lignes des tableaux de tests (triplet constant en T1, `c:x.md`, pont synchrone, `PUT` à `/` initial) | §6 T1 et T2 |
| C7 | Espace de noms du XML de listing | §4.3 « Incohérences de transport » ; arbitrage en C.2 |
| C8 | Régions hors de la partition `amazonaws.com` refusées | D6, §4.4 |
| C9 | « Une région mal configurée se manifeste par un refus du garde » contredisait §4.6 | §4.4 « Une région mal configurée » ; mesure Amazon S3 : C.1 (#292) |
| C10 | Page tronquée sans clé acceptée et comptée dans le budget de pages ; délai par page (#283) | D9, §4.3, §4.5 ; arbitrage en C.2 |
| C11 | Région de signature d'une source `ServiceUrl` (`SigningRegion`, #284) | §4.2, D5, D6, §4.3 (`Id`), §4.4 ; mesure sur un store qui la vérifie : C.1 (#292) |
| C12 | Relevé systématique des réglages ambiants ; `x-amzn-trace-id` retiré et `User-Agent` reconstruit (#285) | §4.4 « Configuration hermétique » et « Ce que le garde retire », §5, §6 T1 ; arbitrages en C.2 |
