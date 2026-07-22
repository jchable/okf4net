# Design — Distribuer la CLI `okf` via winget

**Date** : 2026-07-22
**Statut** : validé (design), en attente de plan d'implémentation
**PackageIdentifier** : `Coderise.OKF4net`

## Objectif

Rendre la CLI `okf` (OKF4net.Cli, Native AOT) installable sur Windows via
`winget install Coderise.OKF4net`. Aujourd'hui le binaire n'est publié nulle
part : `release.yml` ne fait que packager la librairie sur NuGet, et aucune
Release GitHub n'est créée. Ce design comble ce manque et produit le manifeste
winget.

## Décisions cadrées

| Sujet | Décision |
|---|---|
| Type de package winget | **Portable** (pas d'installeur MSI) |
| Format d'asset | **Zip** par architecture (`okf-<version>-win-<arch>.zip` contenant `okf.exe`) |
| Architectures | **x64 + arm64** |
| Génération du manifeste | **Automatique en CI** (templates + script, SHA256 calculés au build) |
| Soumission à `microsoft/winget-pkgs` | **Manuelle la première fois** ; automatisation (`winget-releaser`) différée |
| Hébergement des binaires | **Assets de Release GitHub** sur `jchable/okf4net` |

Métadonnées package (issues de `src/OKF4net/OKF4net.csproj`) :
- Publisher : `Coderise` — Author : `Julien CHABLE`
- Moniker / command alias : `okf`
- License : `LGPL-3.0-or-later`
- ShortDescription : *Zero-dependency .NET implementation of the Open Knowledge
  Format (OKF) v0.1: parse, validate, index, and graph OKF knowledge bundles.*
- URLs : repo & licence → `https://github.com/jchable/okf4net`
- Tags : `okf`, `open-knowledge-format`, `knowledge`, `markdown`, `yaml`,
  `knowledge-graph`

## Composants

### 1. Extension du pipeline de release (`.github/workflows/release.yml`)

Native AOT ne se cross-compile pas proprement depuis Linux vers Windows : on
build sur **runners Windows natifs**.

- **Job `cli-binaries`** (matrice) :
  - `windows-latest` → `dotnet publish src/OKF4net.Cli -c Release -r win-x64`
  - `windows-11-arm` → `... -r win-arm64`
  - Chaque job : produit `okf.exe`, le zippe en `okf-<version>-win-<arch>.zip`
    (le zip contient un `okf.exe` propre), calcule son SHA256, et remonte le zip
    comme artifact de workflow.
- **Job `github-release`** (après `cli-binaries`) : crée/actualise la Release
  GitHub pour le tag `v*`, y attache les deux zips + `checksums.txt` (SHA256 des
  deux zips).
- **Job `winget-manifests`** (après `github-release`) : exécute
  `packaging/winget/Generate-Manifests.ps1` avec version, URLs des zips et
  SHA256, produit les 3 YAML finaux et les attache à la Release sous un dossier
  `manifests/` (ou `winget-manifests.zip`).
- **Job `nuget`** : inchangé.

Le `VERSION` est dérivé du tag (`${GITHUB_REF_NAME#v}`) comme aujourd'hui.

> Choix du zip plutôt que de l'exe brut : permet un `PortableCommandAlias: okf`
> propre malgré des noms d'asset versionnés et spécifiques à l'architecture.

### 2. Templates + script de génération (in-repo)

Sous `packaging/winget/` :

- **`templates/Coderise.OKF4net.installer.yaml.in`** — `InstallerType: zip`,
  `NestedInstallerType: portable`, deux entrées `Installers` (x64, arm64) avec
  placeholders `{{Url_X64}}`, `{{Sha256_X64}}`, `{{Url_Arm64}}`,
  `{{Sha256_Arm64}}`, chacune avec
  `NestedInstallerFiles: [{ RelativeFilePath: okf.exe, PortableCommandAlias: okf }]`.
- **`templates/Coderise.OKF4net.locale.en-US.yaml.in`** — métadonnées (Publisher,
  PackageName, Moniker, License, tags, ShortDescription, URLs).
- **`templates/Coderise.OKF4net.yaml.in`** — version manifest
  (`PackageIdentifier`, `{{Version}}`, `DefaultLocale: en-US`,
  `ManifestType: version`).
- **`Generate-Manifests.ps1`** — remplace les placeholders et écrit les 3 YAML
  dans `out/manifests/`. Exécutable localement pour tester.

Manifest schema ciblé : **v1.6.0** (`ManifestVersion: 1.6.0`).

Aucun YAML à SHA256 figé n'est committé → pas de dérive possible entre le repo et
les binaires réellement publiés.

### 3. Documentation (`packaging/winget/README.md`)

Procédure de soumission manuelle une fois la Release publiée :

1. Prérequis : `winget install Microsoft.WingetCreate`, fork de
   `microsoft/winget-pkgs`.
2. Récupérer les manifestes générés depuis la page de Release
   (ou les régénérer localement via `Generate-Manifests.ps1`).
3. Valider : `winget validate --manifest <dir>` puis test d'installation locale
   `winget install --manifest <dir>`.
4. Soumettre : `wingetcreate submit <dir>` (ou PR manuelle vers
   `microsoft/winget-pkgs` sous `manifests/c/Coderise/OKF4net/<version>/`).
5. Après merge par les modérateurs Microsoft : `winget install Coderise.OKF4net`.

Note : bascule future vers l'action `winget-releaser` possible une fois le
package accepté (le script de génération sert de base).

## Critères de succès

- `dotnet publish` AOT réussit pour **x64 et arm64** en CI ; les deux zips +
  `checksums.txt` + les manifestes apparaissent sur la Release du tag.
- `winget validate` passe sur les 3 manifestes générés.
- `winget install --manifest packaging/winget/out/manifests` installe la CLI et
  `okf --version` répond.
- Après acceptation de la PR : `winget install Coderise.OKF4net` fonctionne.

## Risques / points ouverts

- **Runners `windows-11-arm`** : disponibilité et build AOT arm64 à valider en
  CI (premier point à vérifier lors de l'implémentation ; repli possible :
  cross-compile arm64 depuis `windows-latest` avec le toolset MSVC ARM64, ou
  livrer x64 d'abord).
- **Première PR winget-pkgs** : soumise et mergée hors de ce repo par les
  modérateurs Microsoft — délai externe, non automatisable ici.
- **Version cible** : ces changements s'appliqueront au prochain tag publié
  (p. ex. `v0.2.0`) ; la version est dérivée du tag, pas figée dans le design.

## Hors périmètre

- Installeur MSI, entrées registre, désinstallation gérée.
- Automatisation `winget-releaser` (différée).
- Distribution Homebrew / Scoop / apt (autres gestionnaires).
