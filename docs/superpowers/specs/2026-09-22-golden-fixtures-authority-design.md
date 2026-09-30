# L'autorité des fixtures golden — design

Date : 2026-09-22
Statut : proposé

**Révision (revue externe, ronde 2, 2026-09-30)** : une seconde revue externe,
menée sur la branche rebasée, ne trouve **aucun nouveau bloquant** et conclut
« implémentable après corrections nommées ». Cinq corrections importantes sont
intégrées inline : le critère présentation / sémantique devient une liste de
cinq propriétés à préserver au lieu de catégories lexicales qui rangeaient mal
(un diff d'espacement peut détruire une structure §8) ; le relâchement sur les
captures Rust est double et n'était annoncé qu'à moitié ; le balayage des
sorties machine a été fait et montre 22 champs sans assertion de valeur, avec un
seuil « asserté une fois » trop faible — d'où l'assertion de projection complète
par surface ; l'unité de sélection du mode update est définie (le test et son
groupe d'artefacts) ; et deux attributions normatives sont corrigées (le code de
sortie `1` est notre contrat, pas §11 ; l'index racine a droit à `okf_version`).
La revue a aussi trouvé que l'ancienne doctrine vit **dans le code**, pas
seulement dans la documentation.

**Révision (revue externe, ronde 1, 2026-09-25)** : une revue externe adversariale a
trouvé un bloquant et huit constats importants, dont plusieurs réfutent des
affirmations de la première version. Les trois corrections structurantes : la
promesse de complétude de la section 3 était fausse (un champ projeté dans
`okf audit --json` n'est gardé que par son golden), le partage des goldens en
deux familles par nom de fichier était indéfendable (il mélangeait provenance,
présentation et sémantique), et le traitement de la normalisation des chemins
était inversé. Les corrections sont inline ; chaque constat vérifié dans le
dépôt est noté « revue ». Les affirmations que la revue a démenties sont
récrites, pas effacées : leur erreur explique le design actuel.

## Contexte et motivation

`tests/fixtures/` se présente aujourd'hui comme des **captures byte-exact de
l'implémentation de référence**, et `CLAUDE.md` en tire une règle dure : ne
jamais éditer un fixture pour faire passer un test, une différence étant
toujours une régression côté C#. Cette règle a été écrite en juillet 2026,
quand le crate Rust `okf` vivait encore dans le dépôt et que la parité
byte-à-byte avec lui était la garantie recherchée (Phase 1 de la migration).

Ce cadrage n'est plus vrai, et il ne l'est plus qu'à moitié depuis un moment :

- **Sur les 23 fichiers de `tests/fixtures/golden/`, 12 sont encore des
  captures du binaire Rust** — quatre goldens, `info.out`, `graph.dot`,
  `fmt/users.md` et l'arbre `index-input/` (8 fichiers), et un code de sortie,
  `validate.exitcode`, qui contient le caractère `0`. `validate.out` a été
  régénéré depuis le C# au bump v0.2, et `validate-v02.*`,
  `validate-computation.*`, `validate-reserved.*`, `audit-v02.*` ont été écrits
  à la main contre le texte OKF, diagnostic par diagnostic. `verify.out` a été
  écrit contre le format de sortie décidé pour la commande, ce qui n'est pas la
  même chose (revue) ; `verify-dau.md`, lui, est un document dont les entrées
  `verified` relèvent de §5.2 — les deux fichiers d'un même test n'ont pas la
  même nature.
- L'oracle Rust est **abandonné par décision, pas perdu** (revue). La première
  version écrivait que plus rien ne pouvait régénérer ces octets : c'est faux,
  la source du crate est toujours dans l'historique
  (`git cat-file -t d20343c:src/bin/okf.rs` renvoie `blob`). Ce qui a disparu,
  c'est la volonté de traiter une réimplémentation supprimée comme la norme
  d'un projet qui implémente une spec publiée. Retirer cette autorité est donc
  un choix assumé, et il doit être défendu comme tel.
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

### Ce que l'inventaire a démenti, et où l'inventaire s'est trompé

L'intention initiale était de supprimer les goldens devenus sans objet. Un
inventaire de couverture (quelle garantie disparaîtrait si tel golden était
supprimé ?) a montré qu'**aucun test golden n'est à supprimer**. Cinq *fichiers*
le sont, mais pour une autre raison — ils ne sont lus par aucun test, voir
section 4 — et non parce que la garantie qu'ils portaient aurait disparu.

La première version citait deux exemples. La revue en a invalidé un et confirmé
l'autre, et la façon dont le premier s'est trompé vaut d'être gardée :

- **Faux** : « `IndexGenerator.DefaultSynthesize` n'est exécuté nulle part
  ailleurs ». L'inventaire avait grepé le **nom** `DefaultSynthesize`, qui
  n'apparaît dans aucun test — mais la surcharge publique
  `IndexGenerator.RegenerateIndexes` l'utilise par défaut, et `IndexTests`
  l'appelle sur **19 sites** (la v2 disait dix, comptage lui aussi trop étroit —
  revue). Le synthétiseur réel est donc exécuté. Ce que le golden
  fige seul, c'est le **texte** qu'il produit
  (`Contains 3: Customers, Orders, Users.`). Leçon à retenir pour la suite du
  chantier : grep d'un identifiant ≠ mesure de couverture.
- **Confirmé** : la branche « `okf fmt` imprime le document sur stdout » (sans
  `-w`) n'est traversée avec succès que par `Fmt_output_matches_golden` ;
  l'autre test qui l'approche échoue en exit 1 avant d'imprimer.

Le problème à corriger n'est donc pas le contenu de `tests/fixtures/`, c'est ce
que ce contenu **prétend être**.

## Périmètre

Trois obligations, avec des autorités distinctes et nommées comme telles :

1. **Conformité** — ce que la spec impose est vérifié par des tests qui citent
   le `§`. `docs/spec/SPEC.md` est la seule autorité.
2. **Fidélité sémantique des sorties machine** — chaque valeur qu'une sortie
   machine projette a une assertion **discriminante** hors golden : une qui
   distingue la valeur correcte d'une valeur plausible-mais-fausse, pas une
   simple présence. Cette obligation est **née de la revue** : sans elle, la
   conformité peut être respectée par le calcul et trahie par la projection
   (voir section 3, qui fixe aussi la forme : une projection complète par
   surface).
3. **Instantané** — `tests/fixtures/golden/` fige la sortie de nos verbes,
   comme **notre** sortie : regénérable, avec relecture du diff. Aucune
   prétention à une référence externe.

Le jeu d'instantanés couvre `validate`, `info`, `graph`, `fmt`, `audit` et
`verify` — pas la totalité des verbes : `parse` n'a pas de golden, et le golden
d'`index` passe par l'API `IndexGenerator`, pas par le rendu CLI (revue). Il
reste assez large pour garder une alarme sur la mise en forme de verbes comme
`okf info`, dont personne ne remarquerait la dérive.

**Hors périmètre** : comparer notre sortie à l'implémentation de référence
Google. La comparaison datée dans `docs/` montre que sa CLI n'expose pas de
commande équivalente à `validate`/`info`/`graph`/`fmt` ; c'est une preuve à
cette date, pas une vérification de l'état upstream d'aujourd'hui (revue).
Hors périmètre également : une carte de couverture § par § de la spec — ce
serait un chantier distinct, et un document d'état à nourrir.

## Design

### 1. Changement de statut, et à quoi s'accroche la protection

`tests/fixtures/README.md` est réécrit : il a perdu son titre et son
introduction lors d'une édition récente, et ses sections « Provenance » et
« Rules » décrivent encore les captures Rust comme la norme. La nouvelle
version dit :

- la spec est l'autorité de conformité ;
- ce dossier fige notre propre sortie, régénérable, diff relu ;
- la généalogie Rust est une note d'histoire, pas une norme : elle explique
  d'où viennent les octets initiaux de 4 goldens, rien de plus.

La règle dure de `CLAUDE.md` passe de **« ne jamais éditer un fixture »** à
**« ne jamais régénérer sans relire le diff, et jamais pour faire passer un
test »**. La formulation doit rester aussi ferme que l'ancienne sur le point qui
compte : un diff qu'on n'a pas voulu est un échec réel.

#### La protection suit la propriété modifiée, pas le nom du fichier

La première version conservait la seconde exception de `CLAUDE.md` — arbitrage
explicite de l'utilisateur et entrée datée au README — pour une liste de six
goldens « dérivés de la spec ». **La revue a démontré que ce partage est
indéfendable**, et les trois contre-exemples sont vérifiés :

- `validate.out`, classé « présentation », fige `0 error(s)` et
  `✓ conformant with OKF v0.2` sur un bundle privé de champs optionnels — c'est
  exactement le `MUST NOT reject a bundle because of: Missing optional
  frontmatter fields` de §11. Le verdict n'est pas de la mise en forme.
- Les index de `index-input/`, classés « présentation », figent l'absence de
  frontmatter et la structure en sections, que §8 gouverne et que §11 range
  dans la liste de conformité (« Every reserved filename … follows the
  structure in §8 and §9 »).
- `verify.out`, classé « dérivé de la spec », tient son format du **design de
  la commande** : aucun paragraphe OKF ne le prescrit, et le docstring du test
  le dit lui-même.

Et le partage laissait deux trous : `verify-dau.md` n'appartenait à aucune des
deux familles, alors que la règle actuelle de `CLAUDE.md` le **nomme** comme
précédent ; et les fixtures d'**entrée** écrites à la main disparaissaient du
périmètre protégé, alors que `tests/fixtures/README.md` interdit explicitement
d'uniformiser les deux fixtures temporelles — **modifier une entrée peut
neutraliser un test sans toucher aucun golden.**

La règle retenue ne partitionne donc plus des fichiers. Elle qualifie **ce que
le diff change** :

Un diff est **de présentation** — deux runs et relecture, rien de plus — s'il
préserve les cinq propriétés suivantes. Une seule qui bouge, et le diff est
**sémantique** : arbitrage explicite de l'utilisateur **et** entrée datée dans
`tests/fixtures/README.md`, comme aujourd'hui.

1. **Les faits exposés**, leurs valeurs et leurs associations (un compte reste
   attaché au même type, un diagnostic à la même sévérité).
2. **Les éléments présents, absents et leur multiplicité** — rien ne disparaît,
   rien n'apparaît, rien ne se duplique.
3. **Les cibles de liens et les relations** — un graphe garde ses arêtes et
   leurs extrémités.
4. **La validité syntaxique et la structure interprétée du format** — un
   document DOT reste analysable par `dot`, un `index.md` reste des sections et
   des listes au sens de §8.
5. **Les effets écrits et les codes de sortie.**

Un diff mixte relève du régime sémantique, et le doute se tranche du même côté.

**La nature visuelle d'un diff ne suffit donc pas à le qualifier**, et c'est la
correction que la ronde 2 impose : la v2 énonçait des catégories lexicales
(« espacement, formulation, ordre »), dont deux contre-exemples vérifiés
montrent qu'elles rangent mal. Indenter de quatre espaces un `index.md` entier
est *littéralement* un diff d'espacement, et transforme des sections en bloc de
code — la structure que §8 exige disparaît (critère 4). Remplacer
`5 internal (0 broken)` par `5 internal` est *littéralement* une reformulation,
et fait disparaître l'information sur les liens cassés (critère 1).

Un même fichier relève donc des deux régimes selon le diff : réaligner une
colonne de `okf info` n'est pas réviser un verdict, même si les deux touchent
`info.out`.

**Quatre propriétés changent de camp par rapport à la v2**, détaillées en
section 3 ; la plus nette est « l'accolade fermante » du DOT, que la v2 laissait
au seul instantané. C'est faux — sans elle le document n'est plus analysable
(critère 4), et aucun test direct ne la vérifie : les tests DOT existants
contrôlent le préfixe `digraph okf {` et certaines arêtes, jamais la fermeture
ni le document entier. La validité syntaxique d'une sortie machine relève du
régime sémantique, et un test direct doit la couvrir — **structurellement**,
puisque Graphviz n'est pas installé en CI : le test asserte la forme complète du
document (en-tête, chaque ligne d'arête, fermeture), il n'invoque pas `dot`.

#### Un relâchement assumé, pas un effet de bord

Sous la règle actuelle, les 4 captures Rust ne sont révisables que lors d'un
bump de spec délibéré. La règle ci-dessus les relâche **deux fois**, et la v2 n'en
annonçait qu'une (revue) :

1. Un diff **de présentation** les rend révisables par la procédure en deux
   runs, sans arbitrage.
2. Un diff **sémantique** devient possible sous arbitrage et entrée datée,
   **sans exiger de bump de spec**. C'est le second relâchement, celui que la v2
   passait sous silence en écrivant seulement « reste sous arbitrage ».

Exemple concret de ce que le point 2 ouvre : décider que l'index racine généré
déclare désormais `okf_version: "0.2"` — un changement structurel que §12
autorise explicitement — et réviser `index-input/index.md` en conséquence. La
règle actuelle l'interdit sans bump de spec ; la nouvelle le permet sous
arbitrage.

**Les deux sont des décisions, arbitrées avec l'utilisateur le 2026-09-30**, pas
des conséquences mécaniques du changement de méthode de capture. Le second se
défend par la même raison que tout le reste : la condition « bump de spec »
était le corollaire d'une autorité Rust qu'on abandonne, et le contrôle qui
reste — arbitrage plus trace datée — est celui qui gouverne déjà les fixtures
écrites à la main.

#### Les autres textes à corriger

La page Contributing du site (`web/src/pages/Contributing.tsx`) : son bloc
d'avertissement décrit les goldens comme des captures de l'implémentation de
référence. Son attribut `description` dit « byte-exact golden fixtures », ce qui
reste exact et n'a donc pas à changer — la première version lui attribuait à
tort l'affirmation à corriger (revue).

Le voisinage du golden du producteur oppose les deux disciplines en **trois**
endroits, pas un : son README, un commentaire de `.gitattributes`, et un
commentaire de `CheckTests.cs` (revue). Les trois doivent bouger ensemble. Et
l'alignement n'est pas total : le précédent producteur a l'échec volontaire et
les deux runs, mais **pas** l'arbitrage ni l'entrée datée, parce qu'il n'a aucun
sous-ensemble protégé à défendre. Dire « les deux dossiers appliquent désormais
la même discipline » serait faux ; ils partagent la mécanique de capture.

`.github/PULL_REQUEST_TEMPLATE.md` n'est **pas** concerné : il dit seulement
« including golden comparisons », ce qui reste exact.

**L'ancienne doctrine vit aussi dans le code, pas seulement dans la
documentation** (revue), et ces deux textes contrediraient directement la
nouvelle procédure :

- le docstring de la classe `GoldenParityTests` — « Any divergence … is a bug in
  the CLI, never a reason to touch a golden fixture » ;
- le commentaire de `WriteGraphDot` dans `OkfCli.cs` — « Byte-for-byte
  golden-locked … a diff here is a regression, never a fixture to refresh ».

Ils font partie du même balayage. Un commentaire de code qui énonce une règle
abrogée est plus nuisible qu'une page de site périmée : c'est lui qu'on lit en
travaillant.

#### Ce qui ne change pas

`tests/fixtures/** -text` dans `.gitattributes` et l'exclusion du dossier dans
`.editorconfig` **restent**. Regénérable ne veut pas dire normalisable : espaces
de fin, saut de ligne final et fins de ligne LF restent significatifs.

Précision de vocabulaire que la revue impose : « octet-exact » est trop fort
pour décrire la comparaison. Les goldens sont lus avec `File.ReadAllText`, donc
décodés — un BOM serait absorbé — et cinq comparaisons passent par une
normalisation de chemins. Le contrat d'encodage de la capture (UTF-8 sans BOM,
LF, aucun saut de ligne ajouté) doit être énoncé séparément plutôt que résumé
par « octet-exact ».

### 2. Mode regénération

`GoldenParityTests` gagne un mode update inspiré de celui du producteur :
il réécrit les goldens depuis la sortie réelle, puis **échoue** avec un message
expliquant la procédure.

L'échec est délibéré et c'est le cœur du mécanisme : en mode update, le côté
attendu est produit par le harnais qui produit aussi le côté observé, donc la
comparaison est une tautologie — et une tautologie rapportée verte est
exactement ainsi qu'une variable restée dans un shell désarme un golden sans
que personne ne le voie. La procédure est donc **deux runs** : régénérer, lire
`git diff tests/fixtures/golden`, relancer sans la variable pour vérifier
vraiment, committer le diff avec le changement qui l'a causé.

#### Le périmètre est obligatoire

Le mode du producteur s'active par `=1` et réécrit tout. **Ici, non** : la
variable nomme ce qui doit être réécrit, et sans valeur résoluble, rien n'est
réécrit. La raison est celle que la revue a nommée : l'échec volontaire empêche
de présenter une tautologie comme un succès, il **n'empêche pas l'écriture**. Un
lancement non filtré pour un changement de présentation écraserait au passage
des goldens sémantiques, et le message n'arriverait qu'après. Trois choses
restent distinctes, et la rédaction doit les nommer séparément : **autorisation
préalable**, **périmètre de capture**, **validation après capture** (arbitré
avec l'utilisateur le 2026-09-25).

**L'unité de sélection est le test, et ses artefacts forment un groupe**
(revue) : la v2 disait « goldens ou tests » sans trancher, ce qui laissait trois
implémentations possibles pour un test qui produit plusieurs fichiers — nommer
`verify.out` seul devait-il écrire aussi `verify-dau.md`, ne rien écrire
d'autre, ou être refusé ? Le contrat retenu :

- **Un nom désigne un test et le groupe d'artefacts qu'il capture.**
  `Verify_output_matches_golden` couvre `verify.out` **et** `verify-dau.md` ;
  `Index_generation_matches_golden` couvre les trois `index.md`. Il n'existe pas
  de demi-capture d'un groupe, et la capture ne s'élargit jamais au-delà des
  groupes explicitement nommés.
- **Un identifiant inconnu est un échec**, pas un no-op silencieux : une faute
  de frappe qui ne réécrit rien ressemble trop à un test qui passe.
- **Les comparaisons hors périmètre restent actives.** Régénérer `audit-v02.out`
  ne désarme pas `audit-v02.json` ; les deux se nomment quand un même
  changement les touche.

Ce que ce contrat **ne** fait pas, et la v2 le reconnaissait déjà : il ne met en
place aucune autorisation technique. Il déplace la responsabilité sur le choix
du périmètre et sa relecture — un périmètre large par commodité réécrit
effectivement des goldens au diff sémantique. La formule « frein humain
qu'aucune variable d'environnement ne contourne », plus loin dans les risques,
décrit donc une **règle de procédure**, pas une propriété du programme (revue).

Corollaire : **le mode update n'écrit jamais un fixture d'entrée.** Les entrées
(`appendix_a/`, `okf_v02/`, `okf_v02_computation/`, `okf_v02_reserved/`) se
modifient à la main, sous le régime sémantique de la section 1.

#### Trois chemins de capture, pas un

- **Capture de stdout** — `validate*`, `info.out`, `graph.dot`, `fmt/users.md`,
  `audit-v02.*`, `verify.out`.
- **Fichier écrit dans une copie de bundle** — `verify-dau.md` : la capture lit
  le fichier que le verbe a écrit dans le bundle temporaire, pas la sortie
  console. C'est l'artefact qui porte la garantie : le docstring du test
  explique que stdout seul resterait vert si le verbe imprimait la bonne ligne
  en écrivant le mauvais tampon.
- **Arbre de fichiers** — `index-input/` : la capture copie les `index.md`
  produits ; la cardinalité reste une assertion du test, pas un fichier.

Deux contraintes valent pour les trois. **Le chemin de capture passe par
exactement la même invocation que le chemin d'assertion** — un mode update qui
construit sa propre commande fige une sortie que le test ne produit jamais.
Et **tous les artefacts d'un test sont capturés avant son échec délibéré**
(revue) : un helper « écrire puis `Assert.Fail` » appelé à la première
comparaison arrêterait `verify` après stdout, avant le document écrit, et
arrêterait la boucle d'index après son premier fichier. Le répertoire courant
imposé par le harnais (`WithRepoRootAsCwd`) et les arguments relatifs doivent
être préservés à l'identique, sans quoi les chemins capturés changent.

#### Normalisation des chemins : la première version raisonnait à l'envers

Cinq comparaisons normalisent avant de comparer, pas quatre — quatre
remplacements caractère par caractère (`GoldenParityTests.cs:84`, `:93`,
`:101`, `:109`) et un remplacement de paire échappée pour le JSON (`:156`), où
un remplacement caractère par caractère produirait `//` (revue).

La première version en concluait qu'il fallait capturer la sortie **brute** et
interdire la plateforme Windows, pour ne pas « blanchir » une régression de
séparateur. C'est inversé : cette régression est **déjà** invisible côté
assertion, puisque la normalisation s'applique à la sortie observée. Interdire
Windows ne rétablit donc aucune garantie, et capturer du brut sous Windows
produirait un golden que Linux refuserait ensuite.

La règle est donc : **la capture écrit exactement la valeur comparée**, donc la
sortie après la normalisation existante. Le contrat du golden est « texte
normalisé, séparateurs `/` », et le README le dit. Si l'on veut un jour une vraie
garantie sur les séparateurs par plateforme, c'est un test dédié, pas un effet
de bord de la capture.

#### Les dates ne sont pas toutes fixées, et c'est déjà un problème

La première version affirmait que les seams temporels étaient couverts parce que
l'audit reçoit `--as-of` et `verify` un `--at`. **Les goldens de `validate` ne
reçoivent aucune date** (revue), alors que le verbe accepte `--as-of` et que
`okf_v02/metrics/dau.md` porte `stale_after: 2099-01-01T00:00:00Z` : ces
goldens sont suspendus à l'horloge de la machine, et le jour venu ils gagneront
un avertissement de péremption que personne n'a demandé. Le mode update rend la
chose plus visible mais ne la crée pas.

Correctif : **épingler `--as-of` sur les quatre invocations `validate` des
goldens**, à une date fixe antérieure à toute péremption des fixtures, pour que
la sortie ne dépende plus du jour du run. À faire avant le mode update, sans
quoi la première regénération capturerait une sortie datée.

**Vérifié par exécution** (revue) : avec `--as-of 2026-09-25`, les quatre
sorties sont **identiques aux goldens actuels**, codes de sortie compris —
`appendix_a` 0 erreur / 8 avertissements / exit 0, `okf_v02` 0/3/0,
`okf_v02_computation` 0/5/0, `okf_v02_reserved` 4/0/**1**. Le correctif ne crée
donc aucun diff caché. Cette date n'est pas la seule possible : le validateur
n'a aucun contrôle « horodatage dans le futur » relatif à `--as-of` (vérifié
dans `Validate.cs`), donc toute date antérieure à la première péremption donne
la même sortie — mais c'est celle-là qui a été exercée, et c'est celle-là qu'il
faut prendre plutôt qu'une autre supposée équivalente. Deux choses à ne pas
confondre avec de la péremption : les
avertissements §10 sur la forme date-only de `stale_after` et de
`sources[].last_modified` **subsistent** avec une date fixe, parce qu'ils portent
sur la forme du champ et non sur son échéance ; et à la frontière
`--as-of 2099-01-01`, `okf_v02` passe à 4 avertissements et le bundle
computation à 6 — ce qui confirme que la date choisie doit rester en deçà.

#### Ce que le message d'échec doit dire

Le périmètre réécrit, la procédure en deux runs, et — pour tout golden dont le
diff touche une propriété sémantique — que la commande **ne vaut pas
autorisation** : l'arbitrage et l'entrée datée restent dus. Le README du dossier
répète la consigne par groupe de fichiers.

### 3. Combler les trous de conformité et de fidélité

Règle de tri, à écrire dans le README : **une propriété gouvernée par la spec
obtient un test direct citant le `§` ; une valeur projetée dans une sortie
machine obtient une assertion directe même si aucun `§` ne prescrit le format ;
une propriété de pure présentation n'en obtient pas, l'instantané est son seul
gardien, délibérément.**

#### Le trou que la revue a trouvé, et sa généralisation

`okf audit --json` projette pour chaque résultat un champ `status`
(`AuditFindingJson.Status`, §5.4). Les tests JSON assertent `conceptId`, `type`,
`title`, `trust`, `staleAfter`, `stale`, et le `status` **au niveau du bundle** —
jamais celui du résultat. Une projection qui renverrait `draft` pour chaque
résultat laisserait tous les calculs métier corrects, tous leurs tests verts, et
seul le golden JSON s'en apercevrait. Quelqu'un régénère, trouve la différence
plausible, obtient son arbitrage, relance : la sortie publiée est fausse et
aucune assertion ne le dit.

La spec ne prescrit pas ce format JSON, ce qui n'autorise pas à y dénaturer une
valeur qu'elle définit. D'où la deuxième obligation du périmètre : **balayer
chaque champ de chaque sortie machine** (`validate --json`, `info --json`,
`audit --json`, le DOT). Corriger le seul `status` traiterait le symptôme.

**Le balayage a été fait, et il est plus large que prévu** (revue) : **22 champs
ou familles** n'ont aucune assertion de **valeur** hors golden — 6 dans
`validate --json` (`warningCount`, `infoCount`, `diagnostics[].severity`,
`.path`, `.conceptId`, `.message`), 9 dans `info --json` (`bundle`,
`okfVersion`, `indexFileCount`, `logFileCount`, les couples de `types.*`,
`linkCount`, `brokenLinkCount`, `parseErrors` et ses deux champs), 7 dans
`audit --json` (`bundle`, `evaluatedAt`, `query.status`, le compte de
`trust.machine-confirmed`, `status.draft`, `status.deprecated`,
`findings[].path`), plus le `status` par résultat de la ronde 1. `info --json`
est le plus nu : seul `conceptCount` y est asserté par valeur, `linkCount` et
`brokenLinkCount` par simple présence — un `linkCount` constamment nul
passerait — **et aucun golden ne compense**, puisque le golden `info` capture le
rendu texte.

**Et le critère « asserté au moins une fois » est lui-même trop faible.**
`conformant` et `errorCount` le satisfont déjà, alors qu'une projection
constante `true`/`0` passerait les assertions existantes. Le critère retenu est
donc : **une assertion discriminante**, c'est-à-dire qui distingue la valeur
correcte de la valeur plausible-mais-fausse — conformité vraie *et* fausse,
compte nul *et* non nul, champ présent *et* absent, catégories distinctes,
collection complète sur un petit exemple.

**Forme retenue (arbitrée le 2026-09-30) : une assertion de projection complète
par surface.** Un test par sortie machine compare le document **entier** à un
objet attendu construit dans le test, sur un petit fixture, plus les cas opposés
qui comptent. Quatre tests plutôt que vingt-deux assertions dispersées, et deux
propriétés qu'une assertion champ par champ n'apporte pas : la **complétude** de
la collection — un champ qui disparaît fait échouer le test — et la couverture
automatique du **prochain champ ajouté**, qui est exactement ce qui a manqué au
`status` de la ronde 1.

#### Bascule en test direct

Deux colonnes, parce que la première version attribuait à la spec des choix qui
sont les nôtres (revue) : §8 demande des sections et recommande des
descriptions, il ne prescrit ni le groupe `Other`, ni le tri exact des types, ni
la phrase du synthétiseur ; §4 ne définit pas une sérialisation canonique
octet-exacte.

| Propriété | Nature | Où |
|---|---|---|
| Projection complète de `validate --json`, `info --json`, `audit --json` (dont le statut par résultat), avec leurs cas opposés | Fidélité sémantique | `CliTests` |
| Validité syntaxique du DOT : accolade fermante, document analysable, ordre des arêtes, et rien d'émis pour un concept sans lien sortant | Contrat de sortie machine | `CliTests` |
| Ligne `links: N internal (M broken)` de `okf info` : les deux comptes, pas seulement le premier | Fidélité sémantique | `CliTests` |
| Structure d'un `index.md` en sections — **sans** frontmatter, à la seule exception du `okf_version` racine que §12 autorise | **Exigé** (§8, §11) | `IndexTests` |
| Verdict « conformant » malgré des champs optionnels absents | **Exigé** (§11) | `ValidateTests` |
| Verdict non conforme sur fichier réservé malformé (§11) — et sa traduction en code de sortie `1`, qui est notre contrat, pas celui de la spec | **Exigé** + contrat local | `CliTests` |
| Texte du synthétiseur par défaut (`Contains N: …`) | Notre comportement | `IndexTests` |
| Groupe `Other` — produit par `log.md`, fichier réservé sans frontmatter, et non par un concept sans `type` | Notre comportement | `IndexTests` |
| Entrée sans description → pas de suffixe ` - ` | Notre comportement | `IndexTests` |
| `index.md` ne se liste pas lui-même — que le golden ne garantit pas non plus, faute de régénération répétée sur un index préexistant | Notre comportement | `IndexTests` |
| Texte complet de `OkfDocument.Serialize()`, en un seul `Assert.Equal` | Notre comportement | `DocumentTests` |
| Idempotence : reformater un document canonique ne change aucun octet | Notre comportement | `DocumentTests` |
| `okf fmt` sans `-w` imprime le document sur stdout | Notre comportement | `CliTests` |

Restent **volontairement** couverts par le seul instantané, et le README les
nomme comme tels : l'alignement des colonnes de `okf info`, son bloc `types:`,
la deuxième ligne d'en-tête du DOT
(`rankdir=LR; node [shape=box, fontsize=10];`) et l'indentation des arêtes.
Vérifié : la spec ne prescrit aucun de ces deux formats — §6.1 décrit la
sémantique des relations, pas `rankdir` ni des colonnes. Absence de format
normatif n'est pas absence de sémantique normative, et c'est pourquoi les lignes
« exigé » ci-dessus existent.

Quatre propriétés que la v2 rangeait ici en sortent (revue), parce qu'elles
relèvent des critères de la section 1 : l'**accolade fermante** et l'**ordre
global des arêtes** (validité syntaxique, relations), le fait qu'un concept sans
lien sortant **n'émette rien** (présence et multiplicité), et la ligne
`links: N internal (M broken)` — dont la reformulation peut faire disparaître
l'information sur les liens cassés. Ce sont des tests directs, pas des
instantanés.

#### Ce que cette section permet de promettre — et ce qu'elle ne permet pas

La première version promettait qu'après ces ajouts, plus aucun comportement
exigé par la spec ne dépendrait d'un golden, et que regénérer deviendrait donc
sûr. **La revue a réfuté cette promesse** : elle supposait l'inventaire complet,
et il ne l'était pas.

La formulation tenable est plus modeste. Chaque trou identifié est comblé, le
balayage des sorties machine réduit la classe de trous restants, et la relecture
humaine du diff reste une **défense nécessaire**, pas une formalité rendue
inoffensive par une conformité désormais incompromettable.

### 4. Les deux suppressions

Ce design **ne supprime aucun test** : la question de départ était de supprimer
les goldens devenus sans objet, et l'inventaire a répondu qu'aucun ne l'est. Ce
qui disparaît est leur prétention à être une référence externe.

Deux suppressions de duplication, tout de même :

1. **Les 5 copies de bundle dans `golden/index-input/`** (`log.md`,
   `datasets/sales.md`, `tables/{customers,orders,users}.md`). La première
   version présentait leur suppression comme un changement de méthode : à tort,
   le test **copie déjà** `appendix_a/` dans un répertoire temporaire
   (`GoldenParityTests.cs:184`) et ne lit **aucune** de ces cinq copies (revue).
   Ce sont des octets morts : leur retrait ne change aucune assertion, et il
   supprime un piège — aujourd'hui, modifier `appendix_a` laisse ces copies
   périmées sans qu'aucun test ne s'en aperçoive. Ce qu'elles perdent est une
   archive historique autonome du bundle d'entrée, à assumer explicitement.
   La cardinalité conservée (`Assert.Equal(8, allFiles.Length)`) doit être
   décrite pour ce qu'elle est : elle attrape un fichier **créé** en trop et une
   **suppression nette**, mais ni la modification du contenu d'un original, ni
   une suppression compensée par une création. « Rien d'autre touché » était trop
   fort (revue).
2. **Les 4 fichiers `*.exitcode`**, chacun un caractère ASCII sans saut de ligne
   final, avec une note dédiée dans le README et une protection
   `.gitattributes`. Un code de sortie est un entier, pas une sortie à figer.
   Mais **leurs valeurs ne sont pas toutes `0`** (revue) : `validate.exitcode`,
   `validate-v02.exitcode` et `validate-computation.exitcode` valent `0` — un
   bundle reste conformant malgré ses avertissements — et
   `validate-reserved.exitcode` vaut **`1`**. Deux choses à distinguer ici, que
   la v2 confondait (revue) : le **verdict non conforme** sur un fichier réservé
   malformé est exigé par §11 ; sa **traduction en code de sortie `1`** est notre
   contrat de CLI, que la spec ne prescrit nulle part. La valeur à conserver est
   la même ; l'autorité invoquée ne l'est pas. Chaque valeur devient donc une
   assertion explicite avec sa raison ; la suggestion « `Assert.Equal(0, r.Code)` »
   de la première version aurait rendu rouge un comportement correct.

Ces deux suppressions sont des modifications de `tests/fixtures/` au sens de la
règle de la section 1 — des éléments disparaissent (critère 2). Elles relèvent
donc du régime sémantique, et **ce document est l'arbitrage** : chacune reçoit
son entrée datée dans `tests/fixtures/README.md`, qui renvoie ici. Le changement
de règle lui-même est consigné dans le `CHANGELOG.md` sous `[Unreleased]`, comme
tout changement de contrat du dépôt.

## Risques et limites

- **Le mode update est une arme.** L'échec volontaire empêche de prendre une
  tautologie pour un succès ; il n'empêche pas l'écriture, et c'est pourquoi le
  périmètre est obligatoire. Pour un diff de présentation, la relecture est le
  seul filet. Pour un diff sémantique, l'arbitrage et l'entrée datée ajoutent un
  frein — **humain et procédural, pas technique** : rien dans le programme ne
  l'impose, et un périmètre large choisi par commodité réécrit effectivement des
  goldens sémantiques.
- **La frontière présentation / sémantique est un jugement**, encadré par les
  cinq critères de la section 1 mais pas automatisable. C'est le prix de
  l'abandon du partage par nom de fichier, qui était faux. Le README doit donner
  des exemples des deux côtés — dont les deux pièges vérifiés : une indentation
  qui détruit une structure §8, une reformulation qui supprime un compte — et le
  doute se tranche du côté sémantique.
- **La couche instantané ne prouve rien sur la conformité**, par construction.
  Un `okf info` parfaitement conforme à son golden peut violer la spec.
- **Le jeu d'instantanés reste large**, donc chaque changement de formulation
  d'un verbe touchera un fichier et demandera deux runs. C'est le coût assumé
  pour garder une alarme sur `okf info` et `okf audit`.
- **Le relâchement sur les 4 captures Rust est double** et assumé : un diff de
  présentation y suffit désormais sans arbitrage, et un diff sémantique n'exige
  plus de bump de spec, seulement l'arbitrage et l'entrée datée. La règle
  actuelle interdisait les deux.

## Travaux futurs

- Une carte de couverture `§` par `§` de la spec, si le besoin de répondre
  « conformes à quoi exactement » à l'extérieur se présente.
- Un test dédié aux séparateurs de chemin par plateforme, si l'on veut une
  garantie que la normalisation actuelle empêche d'avoir.
- Un oracle Google, si leur implémentation de référence expose un jour des
  commandes comparables.
