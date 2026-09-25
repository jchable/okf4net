# L'autorité des fixtures golden — design

Date : 2026-09-22
Statut : proposé

## Contexte et motivation

`tests/fixtures/` se présente aujourd'hui comme des **captures byte-exact de
l'implémentation de référence**, et `CLAUDE.md` en tire une règle dure : ne
jamais éditer un fixture pour faire passer un test, une différence étant
toujours une régression côté C#. Cette règle a été écrite en juillet 2026,
quand le crate Rust `okf` vivait encore dans le dépôt et que la parité
byte-à-byte avec lui était la garantie recherchée (Phase 1 de la migration).

Ce cadrage n'est plus vrai, et il ne l'est plus qu'à moitié depuis un moment :

- **Sur les 23 fichiers de `tests/fixtures/golden/`, 4 goldens seulement — soit
  12 fichiers — sont encore des captures du binaire Rust** : `info.out`,
  `graph.dot`, `fmt/users.md` et l'arbre `index-input/` (8 fichiers), plus
  `validate.exitcode`, qui contient le caractère `0`. `validate.out` a été
  régénéré depuis le C# au bump v0.2, et
  `validate-v02.*`, `validate-computation.*`, `validate-reserved.*`,
  `audit-v02.*`, `verify.*` ont été **écrits à la main contre le texte de la
  spec**, diagnostic par diagnostic.
- Le binaire Rust n'existe plus et ne peut plus produire de capture. La règle
  « une différence est un bug côté C# » n'a donc plus d'oracle : elle demande de
  ne pas toucher à des fichiers que plus rien ne peut régénérer.
- L'autorité réelle est ailleurs. `docs/spec/SPEC.md` est vendorisée dans le
  dépôt précisément pour que chaque citation `§` résolve localement, et 35
  fichiers de test citent déjà un `§` (`ValidateTests` compte à lui seul 129
  tests). Ces comptages datent du 2026-09-25 sur `dev` ; ils illustrent un
  ordre de grandeur, ils ne sont pas un contrat.

Le dépôt contient par ailleurs **déjà** un golden à la discipline inverse :
`producers/tests/OkfProducer.Tests/fixtures/golden/` capture *notre* sortie,
est regénérable par construction via `OKFGEN_UPDATE_GOLDEN=1`, et **doit** être
régénéré quand le générateur change intentionnellement. Son README prend soin
d'expliquer que sa discipline est « l'opposé de `tests/fixtures/` ». Le mot
« golden » désigne donc deux choses contraires dans le même dépôt.

### Ce que l'inventaire a démenti

L'intention initiale était de supprimer les goldens devenus sans objet. Un
inventaire de couverture (quelle garantie disparaîtrait si tel golden était
supprimé ?) a montré que **ce n'est le cas d'aucun des 23**. Deux exemples
suffisent à le dire :

- `IndexGenerator.DefaultSynthesize` — le synthétiseur de descriptions du §8 —
  n'est exécuté **nulle part ailleurs** dans la suite : tous les tests
  d'`IndexTests` injectent un stub via `RegenerateIndexesWith`, et le test CLI
  d'`index` porte sur un bundle sans sous-répertoire, donc ne l'appelle jamais.
  Seul `golden/index-input/` l'exécute.
- La branche « `okf fmt` imprime le document sur stdout » (sans `-w`) n'est
  traversée avec succès que par `Fmt_output_matches_golden` ; l'autre test qui
  l'approche échoue en exit 1 avant d'imprimer.

Le problème à corriger n'est donc pas le contenu de `tests/fixtures/`, c'est ce
que ce contenu **prétend être**.

## Périmètre

Deux couches, avec des autorités distinctes et nommées comme telles :

1. **Couche conformité** — ce que la spec impose est vérifié par des tests qui
   citent le `§`. `docs/spec/SPEC.md` est la seule autorité.
2. **Couche instantané** — `tests/fixtures/golden/` fige la sortie de nos
   verbes, octet-exact, comme **notre** sortie : regénérable, avec relecture du
   diff. Aucune prétention à une référence externe.

Le jeu d'instantanés reste complet (tous les verbes : `validate`, `info`,
`graph`, `fmt`, `audit`, `verify`), pour garder une alarme sur la mise en forme
de verbes comme `okf info`, dont personne ne remarquerait la dérive.

**Hors périmètre** : comparer notre sortie à l'implémentation de référence
Google. Sa CLI n'expose pas de commande équivalente à `validate`/`info`/`graph`/
`fmt`, donc elle peut servir de référence de comportement à la lecture, jamais
de golden de sortie. Hors périmètre également : une carte de couverture § par §
de la spec — ce serait un chantier distinct, et un document d'état à nourrir.

## Design

### 1. Changement de statut (aucun fichier de fixture supprimé)

`tests/fixtures/README.md` est réécrit — il a perdu son titre et son
introduction, et ses sections « Provenance » et « Rules » décrivent encore les
captures Rust comme la norme. La nouvelle version dit :

- la spec est l'autorité de conformité ;
- ce dossier fige notre propre sortie, régénérable, diff relu ;
- la généalogie Rust est une note d'histoire, pas une norme : elle explique
  d'où viennent les octets initiaux de 4 goldens, rien de plus.

La règle dure de `CLAUDE.md` passe de **« ne jamais éditer un fixture »** à
**« ne jamais régénérer sans relire le diff, et jamais pour faire passer un
test »**. La formulation doit rester aussi ferme que l'ancienne sur le point qui
compte : un diff qu'on n'a pas voulu est un échec réel.

**Sa seconde exception survit, et c'est délibéré.** La règle actuelle a gagné le
2026-09-12/13 une exception plus étroite, utilisée deux fois (`audit-v02.json`
gagnant `evaluatedAt`, `verify-dau.md` après le changement de
`RecordVerifications`) : réviser un fixture écrit à la main contre le texte OKF
exige un **arbitrage explicite de l'utilisateur** et une **entrée datée dans le
README** disant quoi et pourquoi. Cette exigence reste, appliquée à la famille
dérivée de la spec définie en section 2 — un régime pour ces 6 goldens, la
procédure en deux runs seule pour les 5 autres. Le mode update est un outil de
capture, il n'est pas une autorisation : disposer de la commande ne remplace ni
l'arbitrage ni l'entrée datée. Ce que la réécriture change pour ces fichiers,
c'est la façon de produire les octets, pas la permission de le faire.

Les autres endroits qui répètent l'ancien sens sont corrigés dans le même
mouvement : la page Contributing du site (`web/src/pages/Contributing.tsx`) —
son bloc d'avertissement **et** son attribut `description`, qui porte la même
affirmation et qu'un correctif du seul bloc laisserait derrière lui — et le
README du golden du producteur, dont la phrase « la discipline ici est l'OPPOSÉ
de `tests/fixtures/` » devient fausse : les deux dossiers appliquent désormais
la même. `.github/PULL_REQUEST_TEMPLATE.md` n'est **pas** concerné : il dit
seulement « including golden comparisons », ce qui reste exact.

Ce qui ne change pas, et que la nouvelle rédaction doit dire explicitement,
parce que « regénérable » invite à le croire : `tests/fixtures/** -text` dans
`.gitattributes` et l'exclusion du dossier dans `.editorconfig` **restent**. Un
instantané reste octet-exact — espaces de fin, saut de ligne final et fins de
ligne LF compris. Regénérable ne veut pas dire normalisable.

### 2. Mode regénération

`GoldenParityTests` gagne un mode update calqué sur celui du producteur :
`OKF_UPDATE_GOLDEN=1` réécrit chaque golden depuis la sortie réelle, puis
**échoue** avec un message expliquant la procédure.

L'échec est délibéré et c'est le cœur du mécanisme : en mode update, le côté
attendu est produit par le harnais qui produit aussi le côté observé, donc la
comparaison est une tautologie — et une tautologie rapportée verte est
exactement ainsi qu'une variable restée dans un shell désarme un golden sans
que personne ne le voie. La procédure est donc **deux runs** : régénérer, lire
`git diff tests/fixtures/golden`, relancer sans la variable pour vérifier
vraiment, committer le diff avec le changement qui l'a causé.

**Un golden n'est pas toujours du stdout**, et le mode update a donc trois
chemins de capture, pas un :

- **Capture de stdout** — `validate*`, `info.out`, `graph.dot`, `fmt/users.md`,
  `audit-v02.*`, `verify.out`.
- **Fichier écrit dans une copie de bundle** — `verify-dau.md` : la capture lit
  le fichier que le verbe a écrit dans le bundle temporaire, pas la sortie
  console.
- **Arbre de fichiers** — `index-input/` : la capture copie les `index.md`
  produits, et la vérification de cardinalité reste une assertion du test, pas
  un fichier.

Contrainte qui vaut pour les trois : **le chemin de capture doit passer par
exactement la même invocation que le chemin d'assertion**. Un mode update qui
construit sa propre commande fige une sortie que le test ne produit jamais, et
le golden devient vert sans rien garder.

**Piège à traiter explicitement : la normalisation des chemins.** Quatre
assertions remplacent `\` par `/` avant de comparer
(`GoldenParityTests.cs:84`, `:93`, `:101`, `:156`), parce que les goldens ont
été capturés sous Linux et que la sortie contient des chemins natifs. Si le
mode update écrivait la sortie **normalisée**, une régression de séparateur
sous Windows se blanchirait elle-même dans le fichier. La capture écrit donc la
sortie **brute**, et le mode update **refuse de tourner** sur une plateforme
dont la sortie brute diffère du golden attendu — c'est-à-dire ailleurs que sous
Linux — plutôt que de produire un fichier subtilement faux. Le message d'échec
le dit.

Les seams temporels ne posent pas le même problème, mais il faut le vérifier
plutôt que le supposer : les goldens porteurs d'un instant (`audit-v02.json` et
son `evaluatedAt: 2099-06-01T00:00:00Z`, `verify-dau.md` et son `--at`) le
tiennent d'une horloge ou d'un argument injecté par le test, pas de
`DateTimeOffset.UtcNow`. Tant que la capture passe par la même invocation, elle
hérite de la même horloge.

**Les 23 fichiers ne se relisent pas de la même façon**, et le mode update doit
le dire. Deux familles :

- **Présentation** (`info.out`, `graph.dot`, `fmt/users.md`, `index-input/`,
  `validate.out`) : le diff se juge à l'œil.
- **Dérivés de la spec OKF** (`validate-v02.out`, `validate-computation.out`,
  `validate-reserved.out`, `audit-v02.out`, `audit-v02.json`, `verify.out`) :
  leur contenu attendu a été établi en lisant `docs/spec/SPEC.md`, diagnostic
  par diagnostic, et non en capturant une sortie existante. Régénérer l'un
  d'eux depuis notre propre sortie peut bénir silencieusement une régression de
  conformité : si le validateur se met à se tromper, le golden enregistre
  l'erreur et le test redevient vert. Leur diff se relit **contre le `§`**, pas
  à l'œil, et ils restent soumis à l'exception de `CLAUDE.md` rappelée en
  section 1 — **arbitrage explicite de l'utilisateur et entrée datée dans le
  README** avant toute révision.

Le mode update les couvre tous — les exclure créerait une seconde catégorie à
maintenir, et la capture manuelle serait pire que la capture outillée. Mais son
message d'échec **nomme** ces 6 fichiers et dit les deux choses qui les
distinguent : leur diff se relit contre le texte OKF, et la commande ne vaut
pas autorisation de les réviser. Le README du dossier répète la consigne par
groupe de fichiers.

### 3. Combler les trous de la couche conformité

Règle de tri, à écrire dans le README : **une propriété gouvernée par la spec
obtient un test direct citant le `§` ; une propriété de présentation n'en
obtient pas, l'instantané est son seul gardien, délibérément.** Sans cette
règle, on réécrit chaque golden en assertions et on entretient deux fois le même
test.

Bascule en test direct — chacune de ces propriétés n'est aujourd'hui garantie
que par un fichier golden :

| Propriété | § | Fichier de test |
|---|---|---|
| `DefaultSynthesize` exécuté réellement, sans stub | §8 | `IndexTests` |
| Groupe `# Other` pour un document sans `type` | §8 | `IndexTests` |
| Entrée sans description → pas de suffixe ` - ` | §8 | `IndexTests` |
| `index.md` ne se liste pas lui-même | §8 | `IndexTests` |
| Squelette exact d'un `index.md`, ordre des sections entre types | §8 | `IndexTests` |
| Index racine sans frontmatter quand aucun index préexistant | §8 | `IndexTests` |
| Texte complet de `OkfDocument.Serialize()`, en un seul `Assert.Equal` | §4 | `DocumentTests` |
| Idempotence : reformater un document canonique ne change aucun octet | §4 | `DocumentTests` |
| `okf fmt` sans `-w` imprime le document sur stdout | — | `CliTests` |

Restent **volontairement** couverts par le seul instantané, et le README les
nomme comme tels : l'alignement des colonnes de `okf info`, son bloc `types:`,
sa ligne `links:`, la deuxième ligne d'en-tête du DOT
(`rankdir=LR; node [shape=box, fontsize=10];`), l'indentation des arêtes,
l'accolade fermante, l'ordre global des arêtes, et le fait qu'un concept sans
lien sortant n'émette rien. La spec ne dit rien de tout cela : c'est notre mise
en forme, et c'est le travail de l'instantané.

**Propriété visée par cette section** : une fois faite, plus aucun comportement
exigé par la spec ne dépend d'un fichier golden pour être vérifié. C'est ce qui
rend le mode update de la section 2 sûr — régénérer ne peut plus mettre la
conformité en jeu.

### 4. Les deux seules suppressions

Ce design **ne supprime aucun test**, et il faut le dire clairement : la
question de départ était de supprimer les goldens devenus sans objet, et
l'inventaire a répondu qu'aucun ne l'est. Ce qui disparaît est leur prétention à
être une référence externe.

Deux suppressions de duplication, tout de même :

1. **Les 5 copies de bundle dans `golden/index-input/`** (`log.md`,
   `datasets/sales.md`, `tables/{customers,orders,users}.md`) sont des doubles
   octet-pour-octet de `appendix_a/`. Le test copie `appendix_a/` dans un
   répertoire temporaire, lance le générateur, compare les 3 `index.md` aux
   goldens conservés, et garde l'assertion de cardinalité (3 fichiers écrits,
   8 fichiers au total, rien d'autre touché). Gain réel : aujourd'hui, modifier
   `appendix_a` laisse `index-input/` périmé en silence, et seule une relecture
   humaine le rattrape.
2. **Les 4 fichiers `*.exitcode`**, chacun un caractère ASCII sans saut de ligne
   final, avec une note dédiée dans le README et une protection
   `.gitattributes`. Un code de sortie est un entier, pas une sortie à figer :
   `Assert.Equal(0, r.Code)` dans le test dit la même chose sans fichier.

## Risques et limites

- **Le mode update est une arme.** Il rend possible ce que la règle dure
  interdisait. Pour les 5 goldens de présentation, la seule chose qui sépare une
  régénération légitime d'un test désarmé est la relecture du diff ; l'échec
  volontaire du mode update en est le garde-fou, et il ne remplace pas la
  relecture. Pour les 6 goldens dérivés de la spec OKF, l'arbitrage et l'entrée
  datée maintenus en section 1 ajoutent un second frein — humain, celui-là,
  qu'aucune variable d'environnement ne contourne.
- **La couche instantané ne prouve rien sur la conformité**, par construction.
  Un `okf info` parfaitement conforme à son golden peut violer la spec ; c'est
  la couche conformité qui répond de cela, et c'est pourquoi la section 3 est un
  prérequis de la section 2, pas un complément optionnel.
- **Le jeu d'instantanés reste complet**, donc chaque changement de formulation
  d'un verbe touchera un fichier et demandera deux runs. C'est le coût assumé
  pour garder une alarme sur `okf info` et `okf audit`.

## Travaux futurs

- Une carte de couverture `§` par `§` de la spec, si le besoin de répondre
  « conformes à quoi exactement » à l'extérieur se présente.
- Un oracle Google, si leur implémentation de référence expose un jour des
  commandes comparables.
