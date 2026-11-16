# Provider de bundle S3 — design

Date : 2026-11-16
Statut : **révision 1**, à relire par le mainteneur (design validé section par
section le 2026-11-15 ; décision D1 arbitrée le 2026-11-15 sur mesures)
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
image, handle) et la forme. Les décisions de cadrage déjà prises sur les tickets
s'appliquent sans être rejouées : sécurité (#139, 2026-10-06), atomicité et
rafraîchissement (#139, 2026-10-07), et les sept points tranchés par #141 le
2026-11-15 (§8).

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
| D1 | Client S3 | **SDK AWS pour .NET, `AWSSDK.S3`, version exacte épinglée** (4.1.0 au jour des mesures), plutôt qu'un client `HttpClient` + SigV4 écrit à la main. Arbitré par le mainteneur le 2026-11-15 sur deux prototypes mesurés (annexe A). Exception de dépendance propre au projet, comme `Catalog.Hosting` : rien d'autre dans `src/` ne référence ce projet. |
| D2 | Qui construit le client | **Le provider**, à partir d'un `AWSCredentials` et d'une `S3BundleSource` fournis par l'hôte. Aucune surcharge n'accepte un `IAmazonS3` : le garde D3 ne se garantit que sur un client qu'on a construit. Le provider ne lit jamais l'environnement, un fichier ou la chaîne d'identifiants par défaut du SDK (#139, §3). |
| D3 | Redirections | **Garde d'hôte obligatoire, non désactivable** : un `HttpClientFactory` dont le handler refuse, avant tout envoi, une requête dont l'origine (schéma, hôte, port) n'est pas dans l'ensemble calculé à la construction. Mesuré nécessaire : le SDK suit une 307 vers tout hôte `*.amazonaws.com`, la re-signe et y envoie le jeton de session (annexe A, M3b, M3c). |
| D4 | Écriture | **Lecture seule** : `WriteAsync`/`DeleteAsync` rendent `BundleReadOnlyException(Id)` sans rien tenter. |
| D5 | `Location` et `Id` | `Location = "s3://<bucket>/<prefix>"` (affichage seulement ; le bucket est une configuration d'opérateur, non secrète). `Id = "s3:" + 32 hex` d'un SHA-256 de la configuration effective (§4.3) : stable, non vide, opaque. Aucun message d'erreur ne nomme le bucket ni l'endpoint. |
| D6 | Préfixe | Vide = tout le bucket ; sinon une suite de segments valides au sens du socle `BundleKey` (pas de `/` en tête ni en fin, pas de segment vide, `.` ou `..`). Clé S3 = `<prefix>/<key>`, ou `<key>` sans préfixe : injective, à frontière de segment. |
| D7 | Erreurs | Traduites vers les familles que le chargeur comprend (§4.6) : `S3RequestException : IOException` avec un message écrit par la bibliothèque, `UnauthorizedAccessException` pour un refus d'accès, les exceptions typées du contrat pour 404 et 412. L'original en `InnerException`, pour le journal de l'hôte seulement. |
| D8 | Versions | Version d'objet = **ETag** tel que rendu (entre guillemets). Version du bundle = `"s3v1:"` + SHA-256 des triplets (nom brut, ETag, taille) d'un listing complet trié. Pas d'`IBundleChangeNotifier`. |
| D9 | Listing incohérent | **Échec fermé** : une clé rendue hors de `<prefix>/`, un jeton de continuation répété, une page annoncée tronquée sans jeton ou vide font échouer l'énumération (`S3RequestException`). |
| D10 | Internes du cœur | **Aucun.** Le listing est un itérateur `async` ; le cœur n'accorde aucun `InternalsVisibleTo` à ce projet (garde de test, comme l'embarqué). Ce projet accorde les siens au seul projet de tests. |
| D11 | Transport | `https://` exigé ; `http://` seulement avec `AllowInsecureHttp = true` (stores compatibles locaux, tests). |
| D12 | Checksums | Défauts du SDK conservés (mesuré : acceptés par RustFS). Le README dit comment les assouplir pour un store qui les refuse. |
| D13 | Périmètre | Bibliothèque seule. Pas de fabrique de catalogue (#239), pas d'option `okf-mcp` (#238), pas de notification S3, pas d'écriture. |
| D14 | Tests sans CI | Catégorie **`S3Integration`**, distincte de `ContainerIntegration`, contre `rustfs/rustfs:1.0.3` épinglé (MinIO n'est plus disponible sur Docker Hub). `ci.yml` et `release.yml` excluent les deux catégories. |

## 3. Conformité à la spécification

SPEC §3 : un bundle « MAY be distributed as » un dépôt, une archive, un
sous-répertoire — liste permissive. Le provider présente le même arbre de noms
et les mêmes octets qu'un répertoire téléversé tel quel ; la parité (§6, T2) en
est la preuve. Le stockage S3 tient des noms qu'un système de fichiers ne tient
pas (NFC et NFD distincts, `\`, `?`), et en refuse d'autres côté serveur (RustFS
refuse `a/../b`, `a//b`, `a/./b`, mesuré M4) : ce sont des questions de clés,
réglées par les trois couches de `BundleKey`, pas de la spec OKF. **Pas d'entrée
dans `docs/spec-conformance/`.**

## 4. Le provider

### 4.1 Projet et paquet

`src/OKF4net.Providers.S3/`, paquet `OKF4net.Providers.S3`, namespace
`OKF4net.Providers.S3`. Références : `OKF4net` (`ProjectReference`) et
**exclusivement** `AWSSDK.S3` (`PackageReference`, version exacte ; `AWSSDK.Core`
et ses deux transitifs mesurés, `AWSSDK.Extensions.Numerics` et
`System.IO.Hashing`, viennent avec). `IsAotCompatible`, bloc d'emballage comme
le provider embarqué, README de paquet. Dans `OKF4net.sln` ; étape
`dotnet pack` dans `release.yml`.

### 4.2 API publique

```csharp
namespace OKF4net.Providers.S3;

public sealed record S3BundleSource
{
    public required string Bucket { get; init; }
    public string Prefix { get; init; } = "";
    public RegionEndpoint? Region { get; init; }     // AWS ; exclusif de ServiceUrl
    public Uri? ServiceUrl { get; init; }            // store compatible ; exclusif de Region
    public bool ForcePathStyle { get; init; }
    public bool AllowInsecureHttp { get; init; }
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);
}

public sealed class S3BundleProvider : IBundleProvider, IDisposable
{
    /// Aucune E/S. ArgumentException : bucket vide, préfixe invalide (D6), ni ou
    /// les deux de Region/ServiceUrl, http:// sans AllowInsecureHttp, délai <= 0.
    public S3BundleProvider(AWSCredentials credentials, S3BundleSource source);

    /// Libère le client S3 que le provider a construit.
    public void Dispose();
}

/// Une requête S3 a échoué ; le message ne nomme ni bucket, ni endpoint, ni
/// requête. Opération, code HTTP et code d'erreur S3 en propriétés.
public sealed class S3RequestException : IOException { … }
```

Un constructeur **interne** prend en plus le `HttpMessageHandler` intérieur, pour
les tests (T1) ; le garde D3 est toujours posé **au-dessus** de lui.

L'hôte possède les identifiants (statiques, de session, AssumeRole, SSO : tout
`AWSCredentials`) et le provider ; le provider possède son client. Un
`BundleHandle` ne libère pas son provider : c'est à l'hôte de le faire.

### 4.3 Contrat

| Membre | Comportement |
|---|---|
| `Capabilities` | `CanWrite = false`, `CompletesSynchronously = false` : les points d'entrée synchrones le refusent (`SyncBridge`), par conception. |
| `Id` | `"s3:" + hex(SHA-256("s3v1\n" + endpoint + "\n" + bucket + "\n" + prefix))[..32]`, où `endpoint` est `aws:<région>` ou l'origine normalisée de `ServiceUrl` (schéma, hôte en minuscules, port explicite). Deux sources de même configuration effective ont le même `Id`, ce que veut la déduplication du catalogue. |
| `Location` | `s3://<bucket>/<prefix>` (sans `/<prefix>` quand il est vide). Projection d'affichage seulement ; les deux membres `[Obsolete]` à chemin le refusent déjà (D7 de #141). |
| `NativeConstraints` | `BundleKeyConstraints.WellFormedUnicode` (#139, mesuré : RustFS garde NFC et NFD comme deux objets). |
| `EnumerateAsync` | Itérateur `async` sur `ListObjectsV2` (`Prefix = <prefix>/` plus le préfixe de clé demandé suivi de `/`, pages de 1 000), une page à la fois : la borne d'entrées du cœur l'arrête par `DisposeAsync`. Nom brut = clé S3 moins `<prefix>/` ; `Length = Size` ; `Version = ETag`. Aucune validation de nom (le cœur la fait). D9 sur toute incohérence. |
| `GetVersionAsync` | Listing complet par le même itérateur, borné par `limits.MaxEntries` et `limits.MaxDepth` (`BundleLimitExceededException`), haché en D8. Coût : une requête par 1 000 objets à chaque comparaison. |
| `ReadAsync` | `GetObject`, avec `EtagToMatch` quand `expected` est donné. Version = ETag de la réponse ; `Length = ContentLength`. 404 → `BundleObjectNotFoundException` ; 412 → `BundleVersionMismatchException`. Le flux rendu possède la réponse et applique le délai à la lecture du corps (§4.5). |
| `ExistsAsync` | `HeadObject` : 200 → `true` ; 404 → `false` ; 403 → `UnauthorizedAccessException`, jamais `false` (sans le droit `ListBucket`, S3 répond 403 pour une clé absente : un refus n'est pas une absence). |
| `WriteAsync` / `DeleteAsync` | `ValueTask.FromException(new BundleReadOnlyException(Id))`. |

Le cœur, pas le provider, applique `MaxObjectBytes`, `MaxKeyBytes`, la validation
des clés, la concurrence de `LoadAsync` (`MaxConcurrentRequests`) et D7 de #141
(§8, point 2). Un chargement coûte deux listings complets (`ListNames` puis
`GetVersionAsync`) et un `GET` par objet lu ; aucun cache ne les fusionne
(YAGNI, à rouvrir sur mesure).

ETag : opaque pour le cœur. Pour un objet multipart ou chiffré SSE-KMS, ce n'est
pas un hachage du contenu ; un réenvoi des mêmes octets peut donc changer de
version, ce qui coûte un rechargement, jamais une incohérence.

### 4.4 Garde d'hôte (D3)

À la construction, le provider calcule l'ensemble des origines admises : celle de
`ServiceUrl`, ou celles que le SDK vise pour `Region` selon le style
d'adressage. Le `HttpClientFactory` du client crée un `HttpClient` sur
`PinningHandler(SocketsHttpHandler { AllowAutoRedirect = false })` ; le
`PinningHandler` compare `RequestUri` (schéma, hôte, port) à cet ensemble et
lève, sans envoyer, une exception qui devient `S3RequestException` (« request to
an unexpected host refused »).

Mesuré (annexe A, M3c) : 0 requête sur 3 parvenue à la cible d'une 307 ; erreur
remontée sans réessai ; 0 refus sur 15 149 requêtes normales ; +136 Ko en AOT.
**La règle exacte pour AWS (*virtual-hosted*, FIPS, dualstack) et pour un
`ServiceUrl` sans `ForcePathStyle` (le SDK peut alors viser `<bucket>.<hôte>`)
n'est pas mesurée** : c'est la porte de la tâche 1 du plan (§9).

### 4.5 Délais

Chaque requête porte un jeton lié au jeton de l'appelant et à un délai :
`source.RequestTimeout`, ou pour `EnumerateAsync`/`GetVersionAsync` le plus
court de `source.RequestTimeout` et `limits.RequestTimeout` (seules ces deux
opérations reçoivent des `BundleLimits`). Le délai couvre la requête **et la
lecture du corps** : le flux rendu par `ReadAsync` lit sous ce jeton. Un délai
dépassé devient `S3RequestException` (« timed out »), jamais une
`OperationCanceledException`, que le cœur prendrait pour une annulation de
l'appelant ; une annulation de l'appelant reste une annulation. Réessais : mode
standard du SDK (3 tentatives). Pas de sémaphore propre : la concurrence est
celle du cœur.

### 4.6 Traduction des erreurs (D7)

| Origine | Devient |
|---|---|
| 404 sur `GetObject` | `BundleObjectNotFoundException(key)` |
| 412 sur `GetObject` | `BundleVersionMismatchException(key)` |
| 404 sur `HeadObject` | `false` |
| 403 (`AccessDenied`, `InvalidAccessKeyId`, `SignatureDoesNotMatch`…) | `UnauthorizedAccessException` (message de la bibliothèque) |
| autre `AmazonServiceException`, `HttpRequestException`, refus du garde, `ArgumentException` du gestionnaire de redirection du SDK (mesurée M3), délai dépassé | `S3RequestException : IOException` |
| annulation par le jeton de l'appelant | propagée telle quelle |

Message : opération (`list`, `read`, `head`), code HTTP, code d'erreur S3 et la
clé du bundle s'il y en a une (elle est déjà dans les messages du cœur) ; jamais
le bucket, l'endpoint, un id de requête ni une URL (#139, §3). Le chargeur en
fait un `BundleLoadException` qui nomme le type.

## 5. Sécurité, récapitulatif

| Menace | Réponse |
|---|---|
| Redirection vers un hôte arbitraire, fuite du jeton de session | D3, mesuré (M3c) |
| Normalisation du chemin par le client (`System.Uri` réécrit `..`) | le SDK envoie la clé telle quelle (mesuré M4) ; T2 l'exécute |
| Clé hors du bundle | préfixe à frontière de segment (D6) et échec fermé sur une clé rendue hors préfixe (D9) |
| Alias de clés (`/` initial normalisé par RustFS, NFC/NFD, casse) | `/` initial impossible dans une `BundleKey` et refusé dans le préfixe ; NFC/NFD et casse distincts côté S3, signalés par la politique `Strict` comme non portables |
| Objets et listings démesurés | bornes du cœur ; `MaxEntries`/`MaxDepth` dans le parcours de `GetVersionAsync` |
| Secret dans la configuration ou les messages | identifiants de l'hôte seulement (D2) ; messages sans bucket, endpoint, requête ni URL (D7) |
| Code sanctionné remplacé après le chargement | lecture épinglée par `EtagToMatch` (412 → refus), mesurée M5 |
| Transport en clair | D11 |

## 6. Tests

Toute comparaison qui passe par une validation ou un audit utilise `FixedClock`
à 2026-11-01T00:00:00Z, comme pour l'embarqué.

### T1 — unitaires, en CI, sans service

Dans `tests/OKF4net.Tests/Providers/S3/`. Le provider tourne avec le **vrai SDK**
contre un `HttpMessageHandler` qui rend des réponses S3 scriptées (XML de
`ListObjectsV2`, en-têtes, codes) ; le garde D3 est posé entre le SDK et ce
handler. Ces tests prouvent le parsing du SDK, la traduction des erreurs et le
garde, pas seulement notre logique.

| Test | Ce qu'il prouve |
|---|---|
| Construction | `ArgumentException` pour un bucket vide, un préfixe invalide (`/x`, `x/`, `a//b`, `..`), ni ou les deux de `Region`/`ServiceUrl`, `http://` sans opt-in, un délai ≤ 0 ; aucune requête n'est émise |
| Correspondance | clé S3 = `<prefix>/<key>` ; un listing sous `kb` ne demande jamais `kb2/…` ; préfixe vide = tout le bucket |
| Serveur hostile (D9) | une clé hors préfixe, un jeton répété, une page tronquée sans jeton : l'énumération échoue, le chargement aussi, rien n'est servi |
| Pagination | plus de 1 000 objets sur plusieurs pages ; une borne d'entrées atteinte arrête le listing et le libère |
| 404, 412, 403 | `NotFound`, `VersionMismatch`, `UnauthorizedAccessException` ; `ExistsAsync` ne rend jamais `false` sur 403 |
| Messages | aucun ne contient le bucket, l'endpoint, un id de requête ou une URL ; l'original est en `InnerException` |
| Délais | un corps qui cale donne `S3RequestException` après le délai ; une annulation de l'appelant reste `OperationCanceledException` ; le jeton est transmis à chaque requête (modèle : `AsyncProviderContractTests`) |
| Garde | 301, 307 et `PermanentRedirect` vers un autre hôte, **y compris `*.amazonaws.com`** : rien n'est envoyé, pas de réessai ; la table des origines admises par configuration, relevée par un handler qui refuse tout et consigne les URI |
| `Id`, `Location` | stables, `Id` opaque et non vide, égal pour deux configurations effectives égales, différent sinon |
| Version | change avec un ETag ; `MaxEntries`/`MaxDepth` respectés par `GetVersionAsync` |
| Écritures | `WriteAsync`/`DeleteAsync`, `BundleConceptWriter`, `IndexGenerator` : `BundleReadOnlyException`, rien n'est envoyé ; `OkfBundleTools` n'offre aucun outil d'écriture |
| Synchrone | les points d'entrée synchrones refusent le provider |
| D7 de #141, frère de `NoResidualFileIoTests` | les membres `[Obsolete]` à chemin refusent ce provider ; les points d'entrée de lecture, répertoire courant sur un dossier vide, n'y écrivent rien |
| Gardes de dépendance | le cœur n'accorde aucun `InternalsVisibleTo` à `OKF4net.Providers.S3` ; l'assembly ne référence qu'`OKF4net`, `AWSSDK.*` et la BCL |

### T2 — `[Trait("Category", "S3Integration")]`, hors CI

Un fixture démarre `rustfs/rustfs:1.0.3` par la CLI `docker` (Docker ou Podman)
sur un port libre, avec des identifiants générés, crée un bucket, téléverse par le
SDK, et arrête le conteneur à la fin. Sans moteur, chaque test est **rapporté
ignoré** avec la raison, jamais absent. Garantie : la commande
`dotnet test tests/OKF4net.Tests --filter Category=S3Integration` avec **0
skipped**, en tête du README du paquet et dans CLAUDE.md.

| Test | Ce qu'il prouve |
|---|---|
| Parité (`okf_v02`, `okf_v02_computation`, `okf_v02_reserved`, `appendix_a`, `bundles/acme_retail`) | comme les `ParityTests` de l'embarqué : mêmes clés et octets ; mêmes faits de chargement ; diagnostics (sans `Path`), audit, graphe, recherche ; `ExecutableResources` par clé et état, puis relecture épinglée ; index générés capturés par un double inscriptible. Aucune fixture n'est écrite ni modifiée |
| Batterie de clés hostiles, **exécutée**, politique `Native` | `a/../b`, `a//b`, `a/./b`, `%2F`, `%2e%2e`, `+`, espace, `\`, `?`, `#`, `/` initial, NFC et NFD de `café.md`, `dir/`, 1 025 octets, `kb2/…` à côté de `kb/…` : chaque clé envoyée telle quelle par le SDK, puis chargement ; rien hors du préfixe, chaque objet servi est celui de sa clé, chaque rejet suit le socle du cœur |
| Lecture épinglée | après réécriture d'un objet, l'ancien ETag est refusé ; `AttestationOrchestrator.Capture` refuse le code changé |
| Fraîcheur | `OnAccess` voit une modification ; `Every` seulement après l'intervalle |
| Bornes | un objet au-delà d'un `MaxObjectBytes` abaissé : problème local ; un listing au-delà d'un `MaxEntries` abaissé : échec du chargement |

### T3 — job CI `S3 provider AOT smoke test`

Sur le modèle d'`embedded-aot`, matrice `linux-x64`, `osx-arm64`, `win-x64` :
`dotnet pack` d'`OKF4net` et du provider vers un flux local à une version propre
au run ; contrôle du `.nuspec` (dépendance `AWSSDK.S3` à la version épinglée) et
de `project.assets.json` ; publication Native AOT de `tests/S3ProviderAotProbe/`
(hors `OKF4net.sln`). La sonde sert elle-même un **faux S3 en boucle locale**
(`HttpListener` ou `TcpListener`, `http://` avec `AllowInsecureHttp`), charge un
petit bundle, compare son résumé au provider filesystem et vérifie que le garde
refuse une 307 vers `*.amazonaws.com` **sous AOT**. Garde exécutable du pack, de
l'AOT et du trimming du vrai paquet ; aucun conteneur.

## 7. Livraison et documentation

### 7.1 Fichiers

- `src/OKF4net.Providers.S3/` et son README de paquet : **en tête**, la commande
  `S3Integration` et le contrôle « 0 skipped » ; la recette d'hôte (identifiants,
  `S3BundleSource`, `BundleHandle` avec `Every`), le coût de `OnAccess`, les
  checksums, la fabrique de catalogue écrite par l'hôte (renvoi à #239), le
  `Location` d'affichage.
- `OKF4net.sln`, `release.yml` (pack ; filtre de tests étendu), `ci.yml` (filtre
  étendu, job T3).
- `CLAUDE.md` : la règle des dépendances gagne l'exception `AWSSDK.S3` (projet
  seul, version exacte, rien ne le référence) ; une puce d'architecture ; la
  garde « une montée d'`AWSSDK.S3` exige la commande `S3Integration` à 0 skipped
  et le job T3 vert », au même rang que `ContainerIntegration`.
- `README.md` racine et `CHANGELOG.md`.
- `graphify update .` après les modifications de code.

### 7.2 Branche et conflits

Branche `feat/s3-bundle-provider`, empilée sur `feat/embedded-resource-provider`
(PR #237), rebasée sur `dev` après la fusion de #237. Dans les fichiers que #237
modifie aussi (`OKF4net.sln`, `README.md`, `CHANGELOG.md`, `CLAUDE.md`,
`ci.yml`, `release.yml`), seulement des ajouts séparés ; `src/OKF4net/` n'est pas
touché.

### 7.3 Si `CanWrite` est déclaré un jour

Décision à part, non prise ici : correspondance de `BundleWriteCondition` sur
`If-None-Match: *` et `If-Match` de `PutObject` (409 `ConditionalRequestConflict`
compris), sémantique d'une écriture dont la réponse se perd
(`BundleWriteOutcomeUnknownException`), suppression conditionnelle, envoi
`aws-chunked`, et le support réel de ces en-têtes par chaque store compatible.

## 8. Décisions de #141 appliquées (2026-11-15)

1. Pas d'interne du cœur (D10), garde de test.
2. Bornes, validation des clés et D7 de #141 viennent du cœur ; le provider
   déclare `NativeConstraints`, `Id`, `Location`, `Capabilities` et les erreurs
   typées du contrat.
3. Pas de secret dans `catalog.json` : fabrique hors lot (#239).
4. Dépendance validée par le mainteneur (D1).
5. Tests du contrat sur les doubles existants ; hors CI seulement par une Trait,
   commande locale en tête du README.
6. Conflits : §7.2.
7. Gardes exécutables pour ce que xunit n'exécute pas : T3 et la porte §9.

## 9. Non vérifié à ce jour — porte de la tâche 1 du plan

Le prototype SDK de l'annexe A a été fait sur Windows, contre RustFS, en
adressage path-style. **Première tâche du plan, avant tout code de
production**, un prototype qui mesure :

1. les origines visées par le SDK pour `Region` (*virtual-hosted*, et avec FIPS
   et dualstack s'ils sont exposés), et pour `ServiceUrl` avec et sans
   `ForcePathStyle`, relevées par un garde qui refuse tout (aucun compte AWS
   nécessaire) — elles fixent la règle de §4.4 ;
2. que le type d'exception du garde n'est pas réessayé par le SDK, quel que soit
   le type retenu (une dérivée d'`IOException` ou d'`HttpRequestException`
   pourrait l'être ; mesuré seulement pour une dérivée d'`Exception`) ;
3. qu'un délai par jeton lié interrompt bien une lecture de corps qui cale ;
4. qu'un corps dont le checksum ne correspond pas est détecté (ou ne l'est pas :
   le README le dira) ;
5. le cycle de vie du conteneur RustFS piloté depuis xunit, sous Podman et sous
   Docker sous Linux ;
6. qu'une page `ListObjectsV2` aux noms hostiles (encodage d'URL) est décodée à
   l'identique du listing `mc`.

Si l'un de ces points contredit le design, le plan s'arrête et la section
concernée est rouverte avec le mainteneur.

## Annexe A — mesures de D1 (2026-11-14 et 2026-11-15)

Deux prototypes jetables, hors dépôt, contre `rustfs/rustfs:1.0.3`, win-x64,
.NET 10 ; données : `bundles/acme_retail` (18 objets) et un bundle généré de
5 001 fichiers (pagination au-delà de 1 000). Les deux chargent les deux bundles
exactement comme le provider filesystem (9 et 5 000 concepts, mêmes clés et
identifiants, 0 problème), en JIT et en Native AOT. Journaux bruts conservés
dans le scratchpad de la session ; chiffres vérifiés à la main sur ces journaux.

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
| M5 | ETag | entre guillemets, identique PUT/GET/HEAD/LIST ; 412 sur ETag périmé | idem |
| M6 | Checksums | CRC32 par défaut, accepté | GET signé accepté |
| M7 | Code | adaptateur 72 lignes | 603 lignes mesurées + ≈ 400 estimées pour la production |
| M7 | Cadence | ≈ 2 versions par semaine (54 en 6 mois) | — |
