# Orchestrateur d'attestation par conteneurs — design

Date : 2026-09-07
Statut : proposé

**Révision (round 1)** : ce document intègre les corrections issues d'une
relecture adversariale indépendante (voir
`docs/review-briefing-attestation-containers-design.md`). Changement
principal : le binder de paramètres n'est plus une substitution textuelle
générique unique (défaut trouvé en revue — cassait la vérification de
provenance SQL et rouvrait un vecteur d'injection), mais deux binders qui ne
touchent jamais au texte de la computation.

**Révision (round 2)** : un second audit externe (utilisant le briefing
round 2 du même fichier) a trouvé quatre points Important supplémentaires.
Trois sont corrigés inline ici, marqués « post-revue » : image dédiée pour
l'attester (#2), sémantique d'annulation alignée sur le comportement actuel
de `AttestationOrchestrator` — qui a changé depuis la rédaction de ce
document (#5), filtrage des paramètres non déclarés (#7), drainage
stdout/stderr borné et concurrent (#6), exigence de limites de ressources
non encore chiffrées (#8), contrat de sortie de l'attester (#9). **Deux
points restent ouverts, en attente d'arbitrage** (voir la fin du document
après « Travaux futurs ») : (#1) `AttestationContext` ne porte pas de quoi
résoudre `attester.resource` en sécurité — touche le projet
`OKF4net.Attestation` déjà livré, hors périmètre initial de ce document ;
(#3/#4) la CLI `sqlcmd`/`psql` ne peut pas honorer à la fois le binding
natif et la garantie « pas de shell » — remet en cause le choix « CLI
officielle + wrapper minimal » pour `SqlClient`.

## Contexte et motivation

`OKF4net.Attestation` (§10, cf.
[2026-07-29-okf-attested-computation-design.md](2026-07-29-okf-attested-computation-design.md))
définit les contrats host-plugged (`IParameterBinder`, `IComputationExecutor`,
`IAttester`, groupés en `IAttestationRuntime`, résolus par nom de `runtime` via
`IAttestationRuntimeRegistry`) et l'orchestrateur `AttestationOrchestrator`, mais
**aucune implémentation concrète** de ces contrats n'existe dans le repo — c'est
un point d'extension délibérément laissé au host.

Le bundle `bundles/acme_retail` illustre le besoin : `runtime: bigquery`, un
executor qui doit lancer du SQL sanctionné, et un attester Python
(`attesters/sql_equality.py`) qui vérifie déterministiquement un receipt. La
règle déjà actée pour ce sous-système (voir mémoire
`feedback-attestation-no-porting-sanctioned-scripts`) est de ne **jamais**
réimplémenter la logique d'un script sanctionné en C# — le trust model de §10
repose sur l'exécution du script réel, pas d'une réinterprétation. Ce document
conçoit la pièce manquante : un host qui exécute réellement ces scripts
(Python, clients SQL Postgres/SQL Server, etc.) dans des conteneurs, en
respectant cette contrainte.

## Périmètre

Nouveau projet réutilisable **`src/OKF4net.Attestation.Containers/`**, et non
une démo ad hoc. Il fournit une implémentation générique et configurable des
contrats `OKF4net.Attestation`, que n'importe quel host peut enregistrer dans
`AttestationRuntimeRegistry` pour n'importe quel bundle.

Hors périmètre v1 (voir « Travaux futurs ») : moteurs d'exécution cloud/K8s,
images conteneur publiées par OKF4net, bootstraps d'attester dans un langage
autre que Python, gestion de secrets au-delà des variables d'environnement.

## Architecture & dépendances

`src/OKF4net.Attestation.Containers/` référence uniquement
`OKF4net.Attestation` (qui ne référence que `OKF4net`) — zéro
`PackageReference`, cohérent avec la règle de dépendance zéro du reste du
cœur. Il s'appuie sur `System.Diagnostics.Process` (shell vers le binaire
moteur de conteneurs) et `System.Text.Json` (enveloppes stdin/stdout) : les
deux font partie du framework partagé net10.0, donc aucune dépendance NuGet à
ajouter — précédent déjà établi par `OKF4net.Catalog.CatalogManifestParser`
et `OKF4net.Cli.JsonOutput`. **Nuance à ne pas perdre** : « zéro dépendance »
ici veut dire zéro paquet NuGet tiers, pas zéro prérequis d'exécution — ce
projet exige qu'un binaire moteur de conteneurs (`docker`, `podman` ou
`nerdctl`) soit installé et sur le `PATH` de l'hôte, une catégorie de
dépendance nouvelle dans `src/` (`Process.Start` n'a aujourd'hui aucun
précédent en code de production, seulement dans des tests et un spike de
`producers/`).

Composants :

| Type | Rôle |
|---|---|
| `IContainerEngine` | Abstraction d'exécution — une seule méthode utile : lance une commande dans un conteneur avec un flux stdin, retourne stdout/stderr/code de sortie. |
| `CliContainerEngine(string binaryName)` | Seule implémentation v1. Wrapper `Process.Start` autour d'un binaire CLI compatible `run` (Docker, Podman, nerdctl partagent une syntaxe compatible — une seule classe paramétrée par le nom du binaire suffit, pas de classe par moteur). |
| `ContainerRuntimeProfile` | Config host par nom de `runtime` du bundle : image, nature (`Script` \| `SqlClient`), variables d'environnement (ex. connection string). |
| `ContainerAttestationRuntime` | Implémente `IAttestationRuntime`. Prend un `IContainerEngine` + un `ContainerRuntimeProfile`, choisit `ScriptParameterBinder`/`ScriptComputationExecutor` ou `SqlClientParameterBinder`/`SqlClientComputationExecutor` selon `Kind`, toujours `ContainerAttester` comme `Attester`. |
| `ScriptParameterBinder` | `IParameterBinder` pour les profils `Script`. Ne touche **jamais** au texte de la computation — `BoundComputation.BoundText` reste le script sanctionné inchangé. Filtre `parameterValues` aux seuls noms déclarés dans `contract.Parameters` (voir note ci-dessous), type-check chaque valeur retenue contre le `type` déclaré, puis marshale vers une représentation JSON-safe portée par `BoundComputation.Values`. |
| `SqlClientParameterBinder` | `IParameterBinder` pour les profils `SqlClient`. Ne touche **jamais** au texte SQL — `BoundText` reste le SQL sanctionné inchangé, placeholders (`@name`) compris. Même filtrage/type-check que `ScriptParameterBinder`, puis marshale vers la représentation attendue par le mécanisme de bind natif du client cible. |
| `ScriptComputationExecutor` | `IComputationExecutor` pour les profils `Script`. |
| `SqlClientComputationExecutor` | `IComputationExecutor` pour les profils `SqlClient`. |
| `ContainerAttester` | `IAttester` — toujours conteneurisé, toujours via le bootstrap Python (v1), toujours sur une **image Python fixe et unique**, indépendante de l'image du profil (corrigé post-revue #2 : réutiliser l'image du profil `SqlClient`, ex. `postgres:16-alpine`, planterait au démarrage de l'interpréteur — cette image n'a pas Python). |

**Pourquoi deux binders et pas un seul générique par substitution textuelle**
(correction post-revue) : une première version de ce design proposait un
binder unique qui substituait les valeurs directement dans le texte de la
computation. C'est incorrect pour deux raisons. D'abord c'est le vecteur
d'injection classique (échapper correctement une valeur dans du SQL et dans
du code source Python n'a rien à voir). Ensuite, et plus grave, ça casse la
vérification de provenance elle-même : `sql_equality.py` compare le SQL
sanctionné canonicalisé à `receipt.executed_sql`, et son propre commentaire
précise que les bind variables nommées (`@name`) sont comparées
**symboliquement** — "the executor is trusted to bind" — c'est-à-dire que
`executed_sql` est censé conserver le placeholder, jamais la valeur en clair.
Une substitution textuelle en amont ferait échouer cette comparaison à tous
les coups. D'où le principe retenu : le texte bindé ne bouge jamais, les
valeurs voyagent à part (`BoundComputation.Values`), et c'est l'executor —
spécifique au runtime — qui les relaie au mécanisme natif de binding de sa
cible (variable d'environnement JSON pour un script, paramètre CLI natif
pour un client SQL).

**Note sur le filtrage des paramètres (ajoutée post-revue, finding #7)** :
`AttestationOrchestrator.RunAsync` (étape 4) ne fait que vérifier que les
paramètres **requis** sont présents ; il transmet ensuite le dictionnaire
`parameterValues` **complet, non filtré** au binder — le commentaire
« extra values are ignored (§10.3) » décrit l'intention spec, pas un
filtrage déjà appliqué en code. Si les binders de ce projet relaient ce
dictionnaire tel quel dans `OKF_PARAMS_JSON` ou vers le mécanisme de bind
SQL, une clé non déclarée dans `contract.Parameters` peut atteindre le
script/la requête exécutée. Les deux binders doivent donc appliquer
eux-mêmes une liste blanche (noms déclarés uniquement) et un contrôle de
type avant de produire `BoundComputation.Values`.

L'hôte enregistre un `ContainerAttestationRuntime` par nom de runtime distinct
(`"python"`, `"postgres"`, `"sqlserver"`, ...) dans `AttestationRuntimeRegistry`
— chacun avec sa propre image et sa propre config, sans code partagé fragile
entre runtimes.

## Principe transversal : pas de montage de volume

Que ce soit pour le script à exécuter, la requête SQL bindée, ou le module
Python de l'attester, **tout transite par stdin** (texte ou JSON, sans limite
de taille pratique), et la commande lancée dans le conteneur reste toujours
une chaîne **fixe et courte** (l'interpréteur seul, ou un petit wrapper
fourni par ce projet — jamais par le bundle). Ceci évite entièrement la
traduction de chemins de montage entre Windows/Linux/macOS, un point de
friction réel avec Docker Desktop sur Windows.

## Protocoles d'exécution

**Note sur `executor.resource`** : le §10.2 documente ce champ comme pointant
vers des « instructions d'exécution » suivies par un runner (l'exemple
`bigquery` de `bundles/acme_retail` y fait pointer un doc de skill destiné à
un agent, `skills/run-on-bq.md`). Ce host **ne lit ni n'exécute
`executor.resource`** : il exécute directement le texte de la `computation`
(fence inline ou fichier résolu §6.2). C'est un choix assumé de ce host
conteneurisé, pas un oubli — mais un bundle écrit pour un runner qui suit
`executor.resource` à la lettre (comme le fait probablement un agent pour le
pattern `bigquery`) ne se comportera pas de la même façon sous ce host.

### Executor `Script` (ex. `python`)

Le texte de la computation déjà bindée **est** le programme complet à
exécuter :

```sh
<engine> run -i --rm --name okf-<guid> <image> <interpréteur> -
```

stdin = texte de la computation sanctionnée, **inchangé** (voir « Pourquoi
deux binders » ci-dessus — aucune valeur n'y est substituée). Les valeurs de
paramètres, elles, sont exposées séparément via une variable d'environnement
JSON (`OKF_PARAMS_JSON`, produite par `ScriptComputationExecutor` à partir de
`BoundComputation.Values`) : convention v1, le script doit la lire
lui-même (`json.loads(os.environ["OKF_PARAMS_JSON"])`) plutôt que s'attendre
à des valeurs déjà injectées dans son propre texte. Convention de sortie :
le script doit imprimer sur stdout un JSON correspondant aux champs déclarés
dans `executor.receipt`. Aucun bootstrap n'est nécessaire — le texte
sanctionné est directement le programme ; `ScriptComputationExecutor` se
contente de parser stdout comme `Receipt.Fields`.

### Executor `SqlClient` (ex. `postgres`, `sqlserver`)

stdin = texte SQL sanctionné, **inchangé, placeholders (`@name`) compris** :

```sh
<engine> run -i --rm --name okf-<guid> -e OKF_CONN=... <image> sh -c "<wrapper fixe>"
```

Le wrapper (fourni par ce projet, jamais par le bundle) exécute le SQL
**strictement inchangé** — contrainte dure : toute réécriture de requête (ex.
l'envelopper dans `json_agg(...)` pour forcer une sortie JSON, ou substituer
les placeholders par les valeurs avant exécution) romprait la vérification de
provenance d'un attester qui compare `executed_sql` au texte sanctionné (cf.
`sql_equality.py`, qui compare les bind variables **symboliquement** — la
valeur ne doit jamais apparaître en clair dans `executed_sql`). Les valeurs de
paramètres (`SqlClientParameterBinder`) sont transmises au wrapper séparément
et doivent atteindre le client SQL via son mécanisme natif de bind parameters
(ex. `\bind`/paramètres nommés `psql`, `-v` `sqlcmd`) — jamais par
interpolation dans la chaîne de requête. Le wrapper se contente ensuite de
choisir le mode de sortie du client natif le plus proche d'un format
structuré et de le convertir en JSON. **Décision** : v1 utilise des images
officielles existantes (ex. `postgres:16-alpine`, image `mssql-tools`), pas
d'image publiée par OKF4net. **Spike requis à l'implémentation**, sur deux
points conjoints (pas seulement le formatage JSON) : (a) le mécanisme de bind
parameters natif disponible par client/version dans ces images, et (b) le
formatage JSON du résultat sans réécrire la requête. Recommandation
complémentaire : épingler les images par digest plutôt que par tag flottant
(`postgres:16-alpine` peut changer de contenu sans préavis).

### Attester

Un module d'attestation (ex. `sql_equality.py`) est une **bibliothèque**, pas
un programme autonome — il expose une fonction (convention v1 :
`attest(**kwargs)`) à appeler avec des noms de kwargs fixés par la
convention OKF4net (pas ceux, arbitraires, que le script utilise déjà). stdin
porte une enveloppe JSON :

```json
{ "attester_source": "<texte complet du module Python>", "kwargs": { "sanctioned_computation": "...", "receipt": { ... }, "values": { ... } } }
```

```sh
<engine> run -i --rm --name okf-<guid> <image> python -c "<bootstrap fixe et court>"
```

Le bootstrap (chaîne C# constante embarquée dans `OKF4net.Attestation.Containers`,
jamais dérivée du bundle) : écrit `attester_source` dans un fichier temporaire
à l'intérieur du conteneur, l'importe via `importlib`, appelle
`attest(**kwargs)`, imprime le verdict JSON sur stdout. `ContainerAttester`
parse ce JSON en `AttestationVerdict`.

Convention v1 : Python uniquement. D'autres langages (même bootstrap, script
embarqué différent) sont un travail futur sans exemple concret à ce jour.

**Contrat de sortie (précisé post-revue, finding #9)** : le protocole n'était
défini que côté entrée (noms des kwargs). Côté sortie, le module d'attestation
importé ne doit **jamais** écrire sur stdout — un simple `print()` de debug
laissé dans un import corromprait le flux JSON que `ContainerAttester`
s'attend à lire. Le bootstrap est seul responsable de stdout : il n'imprime
qu'une fois, à la toute fin, le verdict JSON complet (clés et types exacts à
fixer à l'implémentation — au minimum un booléen de résultat explicite).
Toute diagnostic/log du module importé doit être redirigé ailleurs (stderr,
ou capturé et ignoré) avant l'appel à `attest(**kwargs)`.

**Ce choix de nommage de kwargs est un détail d'implémentation propre à ce
projet, pas un gap de la spec OKF v0.2** — §10 laisse l'exécution entièrement
au host (`executor.resource`/`attester.resource` sont de simples chemins
validés par la résolution §6.2, sans sémantique d'invocation définie par la
spec). Rien à faire remonter en amont sur ce point.

**`bundles/acme_retail` reste intact** (copie verbatim Apache-2.0) : son
`attesters/sql_equality.py` n'est pas modifié pour matcher cette convention.

**Mise en garde (correction post-revue)** : ce n'est pas qu'une question de
renommer des kwargs. `sql_equality.py` attend un paramètre `claimed_value`
(« the value the caller is about to display ») qui n'existe **nulle part**
dans `AttestationContext` (`Contract`, `Computation`, `Bound`, `Values`,
`Receipt` — voir `Values.cs`) : à l'étape où `AttestationOrchestrator` appelle
l'attester, `Displayable` n'est pas encore calculé, rien n'a encore été
« réclamé ». Le docstring du script le confirme d'ailleurs : « Safe to run
consumer-side » — c'est un script pensé pour être appelé **par un
consommateur**, après réception du résultat et juste avant affichage, pas
pour brancher directement sur `IAttester`. Le bundle de validation
(ci-dessous) doit donc écrire son attester pour correspondre à la forme
réelle d'`AttestationContext`, **pas** reproduire la signature de
`sql_equality.py` — et le câblage réel d'`acme_retail` sur ce host, s'il a
lieu un jour, demandera d'abord de trancher où vit la vérification de
fidélité « valeur affichée = résultat » (le host `IAttester`, ou bien un
maillon consommateur séparé), pas seulement d'adapter des noms de paramètres.

## Gestion d'erreurs & sécurité

**Correction post-revue (finding #5)** : cette section décrivait un
comportement de `AttestationOrchestrator.RunAsync` qui n'est plus le
comportement actuel du code (`AttestationOrchestrator.cs` a évolué depuis la
rédaction initiale de ce document). L'orchestrateur distingue maintenant
explicitement deux cas :

- une exception qui représente **l'annulation de l'appelant** (le
  `CancellationToken` passé à `RunAsync` est déclenché, et l'exception est —
  ou enveloppe — une `OperationCanceledException`, via `IsCallerCancellation`) :
  elle **n'est jamais convertie en donnée**, elle est relancée telle quelle
  (ou ré-émise avec le même token) pour que l'appelant la reconnaisse comme
  une annulation, pas comme un échec du run ;
- un échec de stage ordinaire (tout le reste) : converti en
  `AttestationOutcome.Fail(...)`, et **seul le type de l'exception** rejoint
  `Reasons` (jamais son `Message`) — délibéré, parce que `Reasons` est rendu
  dans le contexte d'un agent, et le message d'une exception venant d'un
  binder/executor/attester hors du contrôle de cette bibliothèque peut
  porter une connection string, une requête, ou la donnée qui a fait
  échouer le run. L'exception complète n'atteint que
  `AttestationOutcome.Error`, lu uniquement côté hôte.

**Conséquences directes pour ce projet** :

- Quand `CliContainerEngine` tue un conteneur parce que le
  `CancellationToken` **de l'appelant** a été déclenché, il doit relancer une
  vraie `OperationCanceledException` liée à **ce même token** (jamais un type
  d'exception maison enveloppant la cause) — sinon `IsCallerCancellation` ne
  la reconnaît pas, et une annulation demandée par l'appelant se transforme
  en échec ordinaire du run.
- À l'inverse, quand ce projet lève ses propres exceptions informatives
  (code de sortie, stderr tronqué) sur binaire moteur absent, code de sortie
  non nul, JSON de sortie malformé, ou son propre timeout indépendant : ce
  détail riche peut vivre dans le `Message`/les données de l'exception (il
  n'atteint que `Error`), mais ne doit **jamais** être supposé sûr pour un
  rendu orienté modèle — même discipline que le reste de l'orchestrateur.

Points de robustesse propres aux conteneurs :

- **Contrainte d'implémentation explicite (corrigée post-revue)** : la
  garantie « aucune chaîne issue du bundle n'atteint jamais un shell » repose
  entièrement sur `CliContainerEngine` construisant sa commande via
  `ProcessStartInfo.ArgumentList` (jamais une chaîne concaténée, jamais
  `UseShellExecute = true`) — chaque argument (image, variables `-e`, nom du
  conteneur) est un élément de tableau séparé, jamais interpolé dans une
  chaîne de commande. La même règle s'applique **à l'intérieur** du
  conteneur pour le wrapper `SqlClient` : il doit relayer stdin directement
  au mode natif de lecture de requête du client (ex. `psql -f -`), sans
  jamais capturer ce contenu dans une variable shell pour le réinterpoler
  (piège facile à réintroduire en écrivant le petit wrapper de conversion
  JSON évoqué plus haut).
- Toujours `--rm` + un nom unique par invocation, pour pouvoir faire
  `<engine> kill <nom>` explicitement sur annulation (`CancellationToken`) —
  tuer le process client local ne suffit pas à arrêter le conteneur côté
  démon. **Course create/kill à gérer (ajouté post-revue)** : une annulation
  qui arrive pendant la création du conteneur (traction d'image, démarrage)
  peut faire échouer un `kill <nom>` précoce ("not found"), suivi d'un
  démarrage tardif qui échappe à la tentative — `CliContainerEngine` doit
  garantir l'ordre (ex. `create` puis `start` séparés, kill retenté sur une
  fenêtre bornée) plutôt que supposer qu'un seul `kill` couvre toutes les
  phases du cycle de vie.
- **Timeout mur-à-mur indépendant de l'appelant** (ajouté post-revue) : un
  `CancellationToken` qui n'est jamais annulé (appel agent fire-and-forget,
  UI abandonnée) ne doit pas laisser un conteneur tourner indéfiniment.
  `ContainerRuntimeProfile` porte une durée max par défaut ; au-delà,
  `CliContainerEngine` annule et tue le conteneur lui-même, indépendamment du
  token appelant.
- **Limites de ressources** (ajoutées post-revue) : `--rm` seul ne plafonne
  ni CPU ni mémoire ni nombre de process — un script buggé ou hostile peut
  épuiser l'hôte de l'intérieur d'un conteneur par ailleurs « isolé ».
  `ContainerRuntimeProfile` doit porter des valeurs par défaut sûres
  (`--memory`, `--cpus`, `--pids-limit`) passées systématiquement par
  `CliContainerEngine`. **Précision post-revue (finding #8)** : ceci est une
  exigence de conception, pas encore une politique arrêtée — les valeurs
  numériques concrètes (et la vérification qu'elles sont réellement
  appliquées par moteur/plateforme, pas seulement acceptées comme flags)
  restent à fixer et valider à l'implémentation ; une configuration invalide
  ou accidentellement illimitée doit être rejetée plutôt que silencieusement
  ignorée.
- **Plafond de lecture sur stdout ET stderr, drainés en parallèle (précisé
  post-revue, finding #6)** : « stderr tronqué » ne veut pas dire « lecture
  bornée » — si l'implémentation lit tout stderr avant de tronquer la
  chaîne finale, un conteneur qui inonde stderr épuise la mémoire de l'hôte
  malgré la limite mémoire du conteneur lui-même. Les deux flux doivent être
  drainés **concurremment** à l'écriture de stdin (pas séquentiellement,
  qui expose au deadlock classique de pipe si l'enfant remplit son tampon
  de sortie en attendant d'être lu), chacun avec son propre plafond de
  taille appliqué **pendant** la lecture, jamais après coup. Ne jamais
  parser un préfixe de stdout tronqué comme un receipt ou un verdict valide.
- Secrets (connection strings) passés via `-e` : limitation documentée en v1
  (visibles via `docker inspect`/`/proc/<pid>/environ`) ; recommandation aux
  hôtes d'utiliser des identifiants à privilège minimal. Un mécanisme de
  secrets plus robuste (`--secret`) est un travail futur. **Ce point est
  aggravé par l'absence d'isolation réseau côté `SqlClient`** (ci-dessous) :
  c'est précisément le profil qui manipule des identifiants qui ne peut pas
  bénéficier de `--network=none`.
- Le conteneur est la frontière de sécurité : le calcul sanctionné est du
  code de bundle semi-fiable, pas totalement fiable. `ContainerRuntimeProfile`
  doit permettre de durcir l'exécution (`--network=none` par défaut pour les
  profils `Script` qui n'ont pas besoin du réseau — inapplicable aux profils
  `SqlClient`, qui doivent atteindre une base externe — et filesystem en
  lecture seule où possible).

## Tests

- **Unitaires** (`tests/OKF4net.Tests/Attestation.Containers/`) : la quasi
  totalité de la logique (`ScriptComputationExecutor`,
  `SqlClientComputationExecutor`, `ContainerAttester`,
  `ScriptParameterBinder`, `SqlClientParameterBinder`) se teste via un
  `FakeContainerEngine` qui enregistre le `ContainerRunSpec` reçu et retourne
  un `ContainerRunResult` canné — aucun Docker requis, cohérent avec le reste
  de la suite xunit. Cas à couvrir explicitement (post-revue) : un
  `SqlClientParameterBinder` ne doit **jamais** produire un `BoundText`
  différent du texte sanctionné en entrée, quelles que soient les valeurs —
  test de non-régression direct sur le defect trouvé en revue.
- **Intégration réelle** (shell effectif vers `docker`/`podman`) : **hors
  CI**, décision explicite comme `producers/` (pas par omission). Une
  commande locale documentée (ex. `dotnet test --filter
  Category=ContainerIntegration`) reste la garantie, à exécuter manuellement
  avant de toucher au code de ce projet.

## Bundle de validation

Pour prouver la convention d'attestation sans toucher à `bundles/acme_retail`,
un nouveau bundle d'exemple `bundles/attestation_containers_demo/` est écrit
dès le départ selon la convention OKF4net, accompagné d'un petit projet
`samples/attestation-containers-demo/` qui câble
`OKF4net.Attestation.Containers` + `AttestationOrchestrator` de bout en bout
sur un runtime `python` et un runtime SQL (postgres ou sqlserver) réel.

## Travaux futurs (hors périmètre v1)

- Moteurs d'exécution structurellement différents (Kubernetes Jobs, APIs
  cloud comme ECS RunTask/Cloud Run Jobs) — modèle asynchrone (soumission +
  polling), nécessiterait une vraie implémentation `IContainerEngine`
  distincte ; l'interface reste ouverte à cette extension sans redesign.
- Images conteneur publiées et maintenues par OKF4net (si les images
  officielles s'avèrent insuffisantes pour un formatage JSON robuste).
- Bootstrap d'attester pour des langages autres que Python.
- Mécanisme de secrets plus robuste que les variables d'environnement.
- Câblage réel de `bundles/acme_retail` sur ce host, une fois la convention
  validée sur le bundle de démonstration.

## Points en attente d'arbitrage (round 2)

### #1 — `AttestationContext` ne porte pas de quoi résoudre `attester.resource`

`Bundle.TryResolveResource` exige un `Concept` (résolution §6.2 sûre,
relative au répertoire du concept ou à la racine du bundle). `AttestationContext`
(`Contract`, `Computation`, `Bound`, `Values`, `Receipt`) n'en porte aucun,
et un `IAttestationRuntime` est construit une seule fois puis réutilisé à
travers de nombreux runs/bundles — il ne peut donc pas non plus fermer sur
un bundle/concept particulier. Un `IAttester` conteneurisé n'a
structurellement aucun moyen sûr d'obtenir le texte du module qu'il doit
exécuter. Ceci touche `OKF4net.Attestation` (déjà livré), pas seulement ce
nouveau projet. Deux pistes, à trancher avant d'écrire le plan
d'implémentation :

- Pré-résoudre `attester.resource` dans `AttestationOrchestrator.RunAsync`
  (même traitement que `computation` à l'étape 2 — `TryResolveComputation`
  est déjà le patron à suivre) et ajouter le texte résolu à
  `AttestationContext` ; les implémentations `IAttester` n'ont alors jamais
  besoin d'un `Bundle`.
- Étendre `AttestationContext` avec `Bundle`/`Concept` (ou `ConceptId`), et
  laisser chaque `IAttester` résoudre lui-même via `TryResolveResource`.

### #3/#4 — Le wrapper `SqlClient` ne peut pas honorer binding natif + « pas de shell » avec une CLI brute

`sqlcmd -v` est une substitution de variable de script côté client
(`$(nom)`), pas un binding natif des paramètres nommés T-SQL — l'utiliser
réintroduirait la substitution textuelle que le fix du binder (round 1)
visait justement à éliminer. Et `psql`, même piloté uniquement via stdin
(`psql -f -`), interprète ses propres méta-commandes (`\!`, `\copy ... program`)
qui peuvent invoquer un shell depuis l'intérieur du conteneur `SqlClient` —
celui qui porte les identifiants de connexion. Aucune des deux CLI ne peut
donc servir de mécanisme d'exécution sans compromettre soit l'invariant de
binding, soit la garantie « aucune chaîne du bundle n'atteint un shell ».

Piste de correction (à valider) : remplacer la CLI native par un pilote de
base de données piloté depuis un langage (ex. Python + un pilote pur-Python
comme `pg8000` pour Postgres), avec le SQL et les valeurs transmis par la
même mécanique d'enveloppe déjà conçue pour l'attester — ce qui unifierait
aussi le choix d'image avec la correction du finding #2. Conséquence
possible : suspendre `sqlserver` jusqu'à ce qu'un pilote comparable soit
validé pour ce dialecte.
