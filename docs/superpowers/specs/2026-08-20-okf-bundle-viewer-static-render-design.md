# Design — Bundle viewer, tranche 1 : rendu statique (`okf render`)

- **Date** : 2026-08-20
- **Statut** : design validé en brainstorming, prêt pour plan d'implémentation
- **Issue** : [#40](https://github.com/jchable/okf4net/issues/40) — « Feature request: OKF bundle viewer (static render + local live server) »
- **Portée** : cette spec ne couvre que la **première des deux tranches** de #40 — la génération d'un site statique. Le serveur live (`okf serve`) fera l'objet de sa propre spec.

## 1. Objectif

Rendre un bundle OKF explorable dans un navigateur sans passer par les verbes CLI un par un : une page par concept (frontmatter + corps rendu), une page d'index, et des liens inter-concepts navigables.

Cible : onboarding, démos, et relecture pendant l'écriture d'un bundle.

## 2. Découpage de #40 et conséquence sur les dépendances

L'issue demande deux modes : rendu statique et serveur live local. Ils sont livrés séparément, le statique d'abord.

Ce découpage a une conséquence directe : **la tranche 1 n'a besoin d'aucune bibliothèque HTTP**, donc les trois options de dépendance débattues dans l'issue (`HttpListener` zero-dep / ASP.NET Core / outil front-end séparé) ne se posent pas ici. La décision est reportée telle quelle à la spec du serveur live, sans être préemptée.

Conséquence : `src/OKF4net.Viewer/` reste **zero-dependency**, calqué sur `OKF4net.Catalog` (aucun `PackageReference`, un unique `ProjectReference` vers `OKF4net`). La règle de `CLAUDE.md` n'a pas besoin d'exception pour cette tranche.

## 3. Décisions actées en brainstorming

| Sujet | Décision | Raison |
|---|---|---|
| Rendu markdown → HTML | **côté client**, lib JS vendorisée | pas de parseur CommonMark à écrire ni à maintenir côté C# |
| Lib JS | **marked** (MIT), vendorisée en ressource embarquée, créditée dans `NOTICE` | licence compatible LGPL ; HTML brut désactivable |
| Emplacement | nouveau projet `src/OKF4net.Viewer/` + verbe `okf render` | réutilisable par la tranche « serveur live » et par un hôte tiers ; testable hors CLI |
| Recherche full-text | **hors périmètre v1** | voir §4 |
| Style | copie des tokens visuels de `web/src/styles/site.css`, **maintenue indépendamment** | cohérence visuelle sans coupler deux projets aux cycles de vie différents |

## 4. Pourquoi pas de recherche en v1

`CLAUDE.md` interdit de forker un second scorer à côté de `ConceptSearch`. Or dans un site **statique** il n'existe aucun serveur pour exécuter `ConceptSearch` au moment de la requête : toute recherche interactive impliquerait de réimplémenter la pondération (titre ×3, tags/description ×2, corps ×1) en JavaScript — c'est-à-dire exactement le fork que la règle proscrit, avec le risque classique de dérive silencieuse entre les deux implémentations.

La recherche est donc reportée à la tranche « serveur live », où `ConceptSearch` tourne côté serveur sans duplication. La v1 se limite à la navigation (index, pages, liens croisés, backlinks), ce qui reste utile seul.

## 5. Architecture

Trois unités, chacune avec une responsabilité unique et testable isolément :

```
src/OKF4net.Viewer/
  SiteModel.cs       # Bundle -> modèle d'affichage. Pur, aucune I/O.
  HtmlWriter.cs      # SiteModel -> fichiers sur disque. Seul point d'I/O.
  ViewerAssets.cs    # CSS + marked.js, ressources embarquées (compatible AOT).
```

- **`SiteModel`** — projection pure : pour chaque concept, son `ConceptId`, les entrées de frontmatter **dans l'ordre du document** (`Frontmatter.AsMapping().Entries`, ce qui préserve les clés producteur inconnues), le corps markdown brut, ses backlinks (`Bundle.Backlinks`) et sa table de liens (§7). Plus la page d'index et la liste des `ParseErrors`.
- **`HtmlWriter`** — sérialise le modèle en fichiers. Aucune logique de présentation métier.
- **`ViewerAssets`** — expose le CSS et `marked.min.js` en ressources embarquées, pour que le binaire Native AOT reste autonome.

Le CLI ajoute `CmdRender` sur le patron de `CmdGraph` : `Positional` pour le bundle, `Load` pour le chargement, `CliOperationException` pour l'arm d'erreur (`error: …` sur stderr, exit 1).

```
okf render <bundle> --out <dir>
```

## 6. Forme de la sortie

L'arborescence du bundle est conservée : le concept `tables/users` devient `tables/users.html`. Les liens entre pages sont donc **relatifs**, et le site s'ouvre directement en `file://` sans serveur — c'est ce qui rend la tranche 1 autonome plutôt que dépendante de la tranche 2.

Le markdown est embarqué **inline** dans chaque page (conteneur `<script type="text/markdown">`), jamais chargé par `fetch()`, qui serait bloqué par la politique d'origine en `file://`.

Symétrie utile : `IndexGenerator.BuildIndexText` produit déjà du **markdown**. La page d'index emprunte donc exactement le même chemin de rendu que les pages de concept — il n'y a pas de second moteur de rendu pour l'index.

## 7. Recâblage des liens

`LinkScanner.ExtractLinks` classe les liens (`Absolute`, `Relative`, `External`, `Anchor`, `Other`) mais **ne renvoie pas leurs positions** dans le texte. Réécrire le markdown par manipulation de chaînes serait donc fragile, et ajouter les positions modifierait une API du cœur pour les besoins d'un consommateur.

À la place, chaque page embarque une **table de correspondance** — `cible brute` → (`href généré`, `Exists`) — construite depuis `Bundle.LinksFrom(id)`, sérialisée en JSON dans un conteneur `<script type="application/json">` (même patron que le conteneur markdown de §6). Après le rendu markdown, le JS parcourt les `<a>` produits et réécrit leur `href` depuis cette table.

Les `href` générés sont **relatifs à la page courante**, pas à la racine du site, pour que `file://` fonctionne à toute profondeur : depuis `tables/users.html`, un lien vers le concept `glossary/term` donne `../glossary/term.html`. Ce calcul est fait côté C# dans `SiteModel` (donc testable sans navigateur), pas côté JS.

Propriétés :
- pas de manipulation textuelle du markdown ;
- aucun changement dans `OKF4net` ;
- la table est une projection pure, donc testable en C# sans navigateur ;
- les liens **cassés** (`Exists == false`) reçoivent une classe distincte et restent visibles comme cassés, au lieu de pointer vers une page inexistante ;
- `External` et `Anchor` sont laissés intacts.

## 8. Sécurité

Le contenu d'un bundle est **semi-fiable** : un bundle peut provenir d'un dépôt tiers, ou être généré par `producers/OkfProducer` à partir d'un repo arbitraire. Le rendu côté client déplace la surface d'attaque dans la page générée, ce qui impose deux exigences — à traiter comme des exigences, pas comme des détails d'implémentation :

1. **Échappement du conteneur.** Le markdown inline doit être échappé de sorte qu'aucune séquence `</script` présente dans un corps de concept ne puisse fermer le conteneur prématurément et injecter du balisage.
2. **Pas de passthrough HTML brut.** `marked` doit être configuré pour ne pas laisser passer le HTML brut du markdown, sinon un corps de concept peut injecter du script arbitraire dans la page générée.

Chacune de ces deux exigences doit être verrouillée par un test dédié utilisant une entrée hostile réelle, et non seulement par une relecture — cf. la leçon de la revue adversariale des parseurs sur entrée non fiable.

## 9. Cas limites

| Cas | Comportement |
|---|---|
| `ParseErrors` non vide | remontés sur la page d'index, jamais silencieusement ignorés (fidèle au chargement permissif de `Bundle`) |
| `--out` inexistant | créé |
| `--out` déjà peuplé | ses propres fichiers sont écrasés ; **rien d'autre n'est supprimé** |
| `--out` à l'intérieur de la racine du bundle | **refusé** — sinon le rendu pollue le bundle qu'il visualise |
| bundle vide | site valide avec un index vide, pas une erreur |
| traversée de chemin via un id | `ConceptId` rejette déjà `..`, verrouillé par un test |
| `index.md` / `log.md` | ne sont pas des concepts (`Bundle.IndexFiles` / `LogFiles`), pas de page générée pour eux |

## 10. Tests

- **`SiteModel` et table de liens** — purs, tests unitaires directs.
- **`HtmlWriter`** — tests sur répertoire temporaire.
- **Sécurité** — deux tests à entrée hostile (§8).
- **Verbe CLI** — `CliTests` : codes de sortie, `<bundle>` manquant, `--out` manquant, `--out` dans le bundle.
- **Native AOT** — le publish doit continuer de passer ; les assets étant des ressources embarquées, aucun risque de trimming sur du code réflexif.

**Aucun golden n'est concerné.** La sortie HTML n'est pas un comportement couvert par la spec OKF, donc `tests/fixtures/` n'est ni lu ni modifié par ce lot.

## 11. Hors périmètre

- Serveur live (`okf serve`) — tranche 2, spec dédiée.
- Recherche full-text — voir §4.
- Authentification / accès distant — le site est un artefact local.
- Édition d'un bundle depuis le viewer — lecture seule.
- Conformance CommonMark complète — seul le sous-ensemble réellement utilisé par les corps de concepts est visé.

## 12. Documentation à mettre à jour

- `NOTICE` — crédit de `marked` (MIT).
- `CLAUDE.md` — nouveau projet dans la carte d'architecture ; préciser qu'il reste zero-dependency.
- `README.md` — le verbe `render` dans la section CLI.
- `CHANGELOG.md` — entrée de la nouvelle capacité.
- `ROADMAP.md` — l'item « Bundle viewer » passe en partiellement livré, la tranche serveur live restant ouverte.
