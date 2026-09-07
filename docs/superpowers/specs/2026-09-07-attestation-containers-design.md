# Orchestrateur d'attestation par conteneurs — design

Date : 2026-09-07
Statut : proposé

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
et `OKF4net.Cli.JsonOutput`.

Composants :

| Type | Rôle |
|---|---|
| `IContainerEngine` | Abstraction d'exécution — une seule méthode utile : lance une commande dans un conteneur avec un flux stdin, retourne stdout/stderr/code de sortie. |
| `CliContainerEngine(string binaryName)` | Seule implémentation v1. Wrapper `Process.Start` autour d'un binaire CLI compatible `run` (Docker, Podman, nerdctl partagent une syntaxe compatible — une seule classe paramétrée par le nom du binaire suffit, pas de classe par moteur). |
| `ContainerRuntimeProfile` | Config host par nom de `runtime` du bundle : image, nature (`Script` \| `SqlClient`), variables d'environnement (ex. connection string). |
| `ContainerAttestationRuntime` | Implémente `IAttestationRuntime`. Prend un `IContainerEngine` + un `ContainerRuntimeProfile`, choisit `ScriptComputationExecutor` ou `SqlClientComputationExecutor` selon `Kind`, toujours `ContainerAttester` comme `Attester`. |
| `TextSubstitutionParameterBinder` | `IParameterBinder` générique unique : substitution textuelle des valeurs déjà validées (§10.2) dans le texte de la computation → `BoundComputation.BoundText`. Pas de logique par runtime — un binder suffit pour du texte, que ce soit du SQL ou un script. |
| `ScriptComputationExecutor` | `IComputationExecutor` pour les profils `Script`. |
| `SqlClientComputationExecutor` | `IComputationExecutor` pour les profils `SqlClient`. |
| `ContainerAttester` | `IAttester` — toujours conteneurisé, toujours via le bootstrap Python (v1). |

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

### Executor `Script` (ex. `python`)

Le texte de la computation déjà bindée **est** le programme complet à
exécuter :

```sh
<engine> run -i --rm --name okf-<guid> <image> <interpréteur> -
```

stdin = texte bindé. Convention minimale : le script doit imprimer sur stdout
un JSON correspondant aux champs déclarés dans `executor.receipt`. Aucun
bootstrap n'est nécessaire — le texte sanctionné est directement le
programme ; `ScriptComputationExecutor` se contente de parser stdout comme
`Receipt.Fields`.

### Executor `SqlClient` (ex. `postgres`, `sqlserver`)

stdin = texte SQL déjà bindé (paramètres substitués par le binder) :

```sh
<engine> run -i --rm --name okf-<guid> -e OKF_CONN=... <image> sh -c "<wrapper fixe>"
```

Le wrapper (fourni par ce projet, jamais par le bundle) exécute le SQL
**strictement inchangé** — contrainte dure : toute réécriture de requête (ex.
l'envelopper dans `json_agg(...)` pour forcer une sortie JSON) romprait la
vérification de provenance d'un attester qui compare `executed_sql` au texte
sanctionné (cf. `sql_equality.py`). Le wrapper se contente de choisir le mode
de sortie du client natif (`psql`/`sqlcmd`) le plus proche d'un format
structuré et de le convertir en JSON. **Décision** : v1 utilise des images
officielles existantes (ex. `postgres:16-alpine`, image `mssql-tools`), pas
d'image publiée par OKF4net — la robustesse exacte du formatage JSON par
moteur SQL reste à valider par un spike à l'implémentation.

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
Le câblage réel de ce bundle sur ce host est reporté ; voir « Bundle de
validation » ci-dessous.

## Gestion d'erreurs & sécurité

Aucun nouveau modèle d'erreur : `AttestationOrchestrator.RunAsync` capture déjà
toute exception levée par le binder/executor/attester et la transforme en
`AttestationOutcome.Error` + `Reasons` (errors-as-data déjà en place). Ce
projet se contente de lever des exceptions informatives (code de sortie,
stderr tronqué) sur binaire moteur absent, code de sortie non nul, JSON de
sortie malformé, ou timeout.

Points de robustesse propres aux conteneurs :

- Toujours `--rm` + un nom unique par invocation, pour pouvoir faire
  `<engine> kill <nom>` explicitement sur annulation (`CancellationToken`) —
  tuer le process client local ne suffit pas à arrêter le conteneur côté
  démon.
- Plafond de lecture sur stdout (taille max) pour éviter qu'un script buggé
  ou hostile épuise la mémoire de l'hôte.
- Secrets (connection strings) passés via `-e` : limitation documentée en v1
  (visibles via `docker inspect`/`/proc/<pid>/environ`) ; recommandation aux
  hôtes d'utiliser des identifiants à privilège minimal. Un mécanisme de
  secrets plus robuste (`--secret`) est un travail futur.
- Le conteneur est la frontière de sécurité : le calcul sanctionné est du
  code de bundle semi-fiable, pas totalement fiable. `ContainerRuntimeProfile`
  doit permettre de durcir l'exécution (`--network=none` par défaut pour les
  profils `Script` qui n'ont pas besoin du réseau, filesystem en lecture
  seule où possible).

## Tests

- **Unitaires** (`tests/OKF4net.Tests/Attestation.Containers/`) : la quasi
  totalité de la logique (`ScriptComputationExecutor`,
  `SqlClientComputationExecutor`, `ContainerAttester`,
  `TextSubstitutionParameterBinder`) se teste via un `FakeContainerEngine` qui
  enregistre le `ContainerRunSpec` reçu et retourne un `ContainerRunResult`
  canné — aucun Docker requis, cohérent avec le reste de la suite xunit.
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
