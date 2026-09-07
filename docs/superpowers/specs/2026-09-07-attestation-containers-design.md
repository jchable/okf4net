# Orchestrateur d'attestation par conteneurs — design

Date : 2026-09-07
Statut : proposé

**Révision** : ce document intègre les corrections issues d'une relecture
adversariale indépendante (voir `docs/review-briefing-attestation-containers-design.md`
pour le brief d'audit). Le changement principal : le binder de paramètres
n'est plus une substitution textuelle générique unique (défaut trouvé en
revue — cassait la vérification de provenance SQL et rouvrait un vecteur
d'injection), mais deux binders qui ne touchent jamais au texte de la
computation. Les autres corrections (limites de ressources, timeout,
contrainte d'invocation process, portée de `executor.resource`, mise en garde
sur `sql_equality.py`) sont marquées « post-revue » inline.

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
| `ScriptParameterBinder` | `IParameterBinder` pour les profils `Script`. Ne touche **jamais** au texte de la computation — `BoundComputation.BoundText` reste le script sanctionné inchangé. Son seul rôle est de marshaler les valeurs (§10.2 `type`) vers une représentation JSON-safe portée par `BoundComputation.Values`. |
| `SqlClientParameterBinder` | `IParameterBinder` pour les profils `SqlClient`. Ne touche **jamais** au texte SQL — `BoundText` reste le SQL sanctionné inchangé, placeholders (`@name`) compris. Marshale les valeurs vers la représentation attendue par le mécanisme de bind natif du client cible (`psql`/`sqlcmd`). |
| `ScriptComputationExecutor` | `IComputationExecutor` pour les profils `Script`. |
| `SqlClientComputationExecutor` | `IComputationExecutor` pour les profils `SqlClient`. |
| `ContainerAttester` | `IAttester` — toujours conteneurisé, toujours via le bootstrap Python (v1). |

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

Aucun nouveau modèle d'erreur : `AttestationOrchestrator.RunAsync` capture déjà
toute exception levée par le binder/executor/attester et la transforme en
`AttestationOutcome.Error` + `Reasons` (errors-as-data déjà en place). Ce
projet se contente de lever des exceptions informatives (code de sortie,
stderr tronqué) sur binaire moteur absent, code de sortie non nul, JSON de
sortie malformé, ou timeout.

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
  démon.
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
  `CliContainerEngine`.
- Plafond de lecture sur stdout (taille max) pour éviter qu'un script buggé
  ou hostile épuise la mémoire de l'hôte.
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
