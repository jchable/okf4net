# Viewer interactif (`okf-render`) — design

Date : 2026-10-06
Statut : révision 6 (2026-10-07) : P1 livré et recetté ; arbitrages A15–A30
rendus ; revue critique de la révision 5 intégrée ; aucun point ouvert (§13) ;
prêt pour les plans de P1.1, P2 et P3, rédigés en parallèle

**Révision 6 (revue de la révision 5 et arbitrages, 2026-10-07)** : chaque
fichier et chaque méthode a désormais **une seule tranche propriétaire**
(§12.0) : P1.1 livre l'en-tête des trois vues et la plomberie, P2 et P3 créent
leurs propres scripts (plus aucun fichier vide ni script chargé avant
d'exister), seul `RenderGraph` (P3) écrit `graph.html`. `OkfShapes` est
défini au pixel près : géométrie unitaire, `size` = côté de la boîte
englobante, table de tailles et de traits par contexte relevée dans les
maquettes (§12.2). Arbitrages A22–A30 (§10) : libellé des lignes de
l'explorateur, budget de l'index, sablier de péremption, et les six points de
§13 tranchés. Intégrés aussi : composants partagés et requêtes JS ancrés
(§12.6), cas de harnais par fichier de tranche (§12.7), primitives exactes de
la simulation (§4.2), règle de vérificateur (C5) et de double titre (C4)
précisées, `replaceState` en `file://` et cas limites du fragment (§12.5),
largeurs de colonnes sans retour à la ligne (L6), valeurs de maquette
manquantes (§11), valeurs par défaut d'un site construit à la main (§12.3).

**Révision 5 (fidélité aux maquettes, 2026-10-07)** : P1 a été construit sur le
seul texte de la spec ; le rendu visuel des maquettes n'y figurait pas, et le P1
livré s'en écarte visiblement. Cette révision consigne les arbitrages A15–A21
(§10), ajoute la check-list exhaustive des maquettes avec, pour chaque élément,
la tranche qui le livre (§11), et les contrats que les trois plans parallèles
partagent (§12). Une tranche **P1.1** (fidélité) s'intercale avant P2 (§9). Le
texte est aligné sur le comportement livré par P1 et sa recette (R1–R6, §10) ;
les points qui demandent encore une décision sont en §13. Vérifié dans le code
avant rédaction : tout ce que P1.1 lit du frontmatter (`status`, `verified`,
`description`, `type`) est accessible par l'API publique de `Frontmatter`
(§2.1) ; le cœur n'est pas modifié.

**Révision 4 (seconde passe de revue, 2026-10-06)** : trois constats, vérifiés
avant intégration. Le budget par tranche contredisait l'interdiction de couper
une itération : la simulation se suspend désormais à l'intérieur d'une
itération, curseurs et accumulateurs conservés (§4.2, contrôles 1 et 2) ; la
date d'affichage devient un champ `staleAfterDate` de l'index, jamais
reconstruit depuis l'instant arrondi (§3.3, §4.4) ; le graphe gagne une
navigation précédent/suivant dans l'ordre de l'index et une règle de focus sous
filtrage (§8, contrôle 9). Exigences ajoutées au plan pour les ancres (§5).

**Révision 3 (revue externe, 2026-10-06)** : douze constats, tous vérifiés
dans le code avant intégration. Corrections : instant de péremption transmis en
millisecondes entières, arrondi vers le haut, et fréquence de recalcul (§4.4) ;
simulation bornée en itérations et en travail compté, indépendante du découpage
en frames, annulable (§4.2) ; fantômes définis comme entrées distinctes et règle
de facettes (§3.3, §4.3) ; structures de données sans prototype et SVG
applicatif à vocabulaire fixe (§3.4, §4.5) ; harnais étendu par un helper de
page complète et un ordonnanceur injecté (§7) ; arbre à entrées
destination + enfants (§3.2) ; chemins de l'index relatifs à la racine du site
et résolveur unique (§3.5) ; mesure avant P1 et plafond du graphe local (§3.6,
§4.3) ; contrat clavier et focus (§8) ; comparateur nommé (§3.1) ; contrainte
AOT de la sérialisation (§3.4) ; fragments existants pris en charge (§5).
Correction d'une erreur de la révision 1 : `Bundle.Concepts` est trié
(`PathOrdering.CompareComponentWise`), il ne suit pas l'ordre du système de
fichiers. Nuance sur un constat : `HtmlSafeJson` n'est pas écrit à la main pour
des raisons d'AOT (son commentaire le dit) ; la contrainte retenue est qu'un
`System.Text.Json` par réflexion échouerait en AOT, d'où « contexte
source-généré ou `HtmlSafeJson` ».

**Révision 2 (arbitrages, 2026-10-06)** : décisions A1–A8 du propriétaire.

**Révision 1 (relecture interne, 2026-10-06)** : corrections factuelles après
vérification dans le code.

## 1. Intention

Le viewer actuel (`OKF4net.Viewer` / `okf-render`) produit une page par concept
et un index, sans navigation transversale. L'usage prioritaire visé est
**parcourir de la connaissance** (bundles de type `acme_retail`, `ga4`), pas
cartographier du code. Succès = depuis n'importe quelle page, on voit où l'on est
dans le bundle, on saute à un concept en quelques frappes, et on voit ses
relations sans quitter la page.

Maquettes (Claude Design, **lien privé**, données d'exemple inspirées de
`acme_retail`, rendu non vérifié) :
<https://claude.ai/artifact/CwM9LAYtiCdBeY1rLRDigz> — fichiers `Main.dc.html`
(A, page de lecture), `GraphFirst.dc.html` (B, graphe global) et
`Focus.dc.html` (C, dont seule la palette « Jump to » est retenue). Direction
retenue : **A + B + palette de C**, décrites en texte en section 2 et élément par
élément en section 11, pour qu'un lecteur sans accès au lien puisse évaluer la
spec et la recette. En cas d'écart, la spec fait foi ; **la check-list de §11
est contraignante pour ce qu'elle énumère** (valeurs, tranche, écarts motivés).

## 2. Périmètre

Dans le périmètre :

- **En-tête commun** (A15, A17) : marque, nom du bundle, « N concepts · M
  links », bouton de palette, « Filters », « Global graph » (ou « Reading view »
  sur le graphe), bascule de thème (§11.1, contrat §12.3).
- **A — page à trois zones** (écran par défaut) : explorateur en arbre à gauche
  (repliable, concept courant surligné, filtre par nom, filtre par type,
  pastilles du palier de confiance et de péremption, légende) ; **colonne
  centrale comme la maquette A** (A15) : fil d'Ariane, titre H1, rangée de
  puces (type, `status`, confiance, péremption), frontmatter dans une boîte
  repliable, puis le corps markdown, rendu par `viewer.js` inchangé ; panneau
  droit (sommaire avec section courante, graphe local 1 ou 2 sauts,
  « Referenced by »).
- **B — graphe global** : page `graph.html` à la racine du site (A17), ouverte
  par « Global graph » depuis toute page, centrée et sélectionnée sur le concept
  courant ; facettes (type, palier de confiance, péremption, tags, affichage),
  tiroir de détail du concept sélectionné, pan/zoom, nœuds glissables.
- **Palette « Jump to »** (bouton visible, raccourcis Ctrl K et `/`) : saut vers
  un concept par correspondance sur le titre, l'id et les tags (§4.6), au
  dessin de la maquette C.
- **Bascule de thème** clair/sombre, par défaut la préférence système.
- Encodage des types par **forme et couleur** (A19) : les cinq types les plus
  fréquents du bundle reçoivent cercle, carré, losange, triangle, anneau ; les
  autres partagent une forme « autre » ; jamais la couleur seule ; une légende
  partout où les formes apparaissent. Péremption signalée par un sablier ambre,
  jamais confondu avec une forme de type (A24).
- **Polices de la maquette embarquées** (A16) : Inter, Inter Tight, Space Mono
  (SIL OFL 1.1), servies depuis `assets/` (en URI `data:` si `file://` les
  bloque, A26).

Hors périmètre, par décision :

- Recherche plein texte. Elle exigerait le scoreur `ConceptSearch`, qui n'est pas
  dupliqué en JS (`CLAUDE.md`) ; la palette ne regarde que titre/id/tags et ne
  lit pas le corps.
- Tout serveur (`okf serve` n'existe pas et n'est pas prévu), toute édition.
- Toute dépendance JS nouvelle : le viewer n'embarque que `marked`. Les polices
  (A16) sont des fichiers vendorisés comme `marked`, pas des dépendances de
  build ou d'exécution.
- Ce que la maquette C propose hors de sa palette (rail d'icônes, mise en page
  « Focus », bande « Related », puce « Frontmatter ») : écarté par le
  propriétaire.

Navigateurs couverts (A14) : dernières versions desktop de Chrome, Edge et
Firefox ; Safari en meilleur effort.

### 2.1 Périmètre d'API : le cœur n'est pas modifié

Cette spec ne change **aucune** API publique de `src/OKF4net/`. Tout ce dont
elle a besoin y est déjà public :

- arêtes : `Bundle.LinksFrom(ConceptId)` (`ResolvedLink`, avec `Exists`) et
  `Bundle.Backlinks(ConceptId)` ;
- confiance et péremption : `ConceptAudit.Run(Bundle, AuditQuery, IOkfClock?)`
  → `AuditFinding` (`Trust`, `Lifecycle`) ; `Lifecycle.StaleAfter`
  (`DateTimeOffset?`) ;
- libellés : `AuditVocabulary.Name(TrustTier)` ;
- ordre : `ConceptId.CompareTo` ;
- colonne centrale et index (P1.1, A15, A18, A19) : `Frontmatter.Type`,
  `Frontmatter.Description`, `Frontmatter.Get("status")?.AsDisplayString()`
  (la valeur brute, présente ou non), `Frontmatter.Verified`
  (`IReadOnlyList<Stamp>`, `Stamp(Actor? By, string? At)`, `Actor.Raw`,
  `Actor.Kind`, `Actor.IsHuman`, `Actor.Id`, `Actor.IsWellFormed`),
  `Lifecycle.StaleAfterDate`, `Frontmatter.AsMapping().Entries` (déjà lu par
  P1), `YamlValue.AsSequence()` et `AsDisplayString()` (C6 ;
  `YamlValue.AsStringList` est interne, on ne l'emploie pas) ;
- découpage des lignes du corps (C4) : `OKF4net.Internal.LfLines`, interne mais
  déjà visible du viewer (`InternalsVisibleTo`, comme `ReparsePoints`) ;
- nom du bundle : `Bundle.Root` (nom du dossier, §12.3).

`ConceptAudit.Run` reçoit une **horloge explicite** (`FixedClock`) : seul le
palier de confiance est lu dans le résultat, la péremption étant évaluée dans le
navigateur (§4.4), et la projection doit rester pure.

Les changements se limitent à `src/OKF4net.Viewer/` (projection, écriture,
assets, polices), à `tools/viewer-security-check/`, aux tests du viewer, au
smoke test AOT de `ci.yml`, et à la documentation (`CLAUDE.md`, README, ROADMAP,
`NOTICE`). Les types publics de `OKF4net.Viewer` (`ViewerSite`, `ViewerPage`)
gagnent des propriétés `init` ; ce n'est pas le cœur.
`src/OKF4net.Render/` n'a pas besoin de nouvelle option. Si l'implémentation
découvre un besoin dans le cœur, c'est un changement de spec, pas un ajout
silencieux.

## 3. Données : `assets/okf-index.js`

Un script unique, généré par une nouvelle projection pure de `SiteModel`, écrit
sous `assets/` à côté de `viewer.css`/`viewer.js`, qui déclare
`window.OKF_INDEX`. Chaque page le charge par une balise `<script src>` relative
— un `fetch` de JSON est bloqué en `file://`.

### 3.1 Ordre

Un seul comparateur pour tout ce qui est ordonné : **`ConceptId.CompareTo`**
(segment par segment, comparaison ordinale, préfixe d'abord). Il s'applique aux
concepts, aux fantômes, aux départages de la palette et au plafond du graphe
local. L'index est trié côté C# ; le JS départage par **position dans l'index**
et ne réimplémente pas le comparateur. (`a/b` précède `a-b` avec ce
comparateur, pas avec une comparaison des chaînes complètes.)

### 3.2 Arbre

Une entrée d'arbre porte séparément une **destination** (le concept de même id,
s'il existe) et ses **enfants** : `foo.md` et `foo/bar.md` coexistent légalement
(la revue en compte 174 cas dans un corpus réel), `foo` est alors à la fois
ouvrable et dépliable. L'interface offre deux commandes distinctes : ouvrir
(le libellé, si destination) et déplier (le chevron, si enfants). Sous un filtre
par nom, les ancêtres des résultats restent affichés.

### 3.3 Concepts, arêtes, fantômes

- **Concepts** : tableau d'enregistrements (id, titre, type, tags, chemin de page,
  palier de confiance, `staleAfterMs`, `staleAfterDate`) ; un concept est désigné
  partout par son **indice** dans ce tableau.
- **Palier de confiance** : `AuditFinding.Trust`, sous son nom
  `AuditVocabulary.Name(TrustTier)` (A1).
- **`staleAfterMs`** : voir §4.4 ; `null` quand `stale_after` est absent ou
  malformé.
- **Arêtes** : exactement `Bundle.LinksFrom`, l'ensemble que `okf graph` exporte
  (liens du corps, §6 de la spec OKF), sous forme de paires d'indices. Les
  occurrences répétées d'un même couple (`BuildGraph` les conserve) sont
  **fusionnées** en une arête portant un compte.
- **Fantômes** : tableau distinct, une entrée par **cible absente** (identité =
  l'id cible), triée par `ConceptId.CompareTo`. Un fantôme n'a ni type, ni
  confiance, ni tags, ni page : il n'est **jamais navigable**. Une arête vers un
  fantôme référence le tableau des fantômes, jamais celui des concepts.
- **Types** (A19, P1.1) : table `types` classée en C#, chaque concept porte
  `typeIndex` ; **description** tronquée (A18, P1.1). Schéma exact : §12.1.

### 3.4 Sérialisation et forme JavaScript

- Le texte du bundle est non fiable et finit dans un fichier JavaScript : il est
  sérialisé par `System.Text.Json` **avec un contexte source-généré** (un
  `System.Text.Json` par réflexion passerait les tests .NET et échouerait à la
  publication Native AOT) ou par `HtmlSafeJson` ; jamais par concaténation.
- **Aucun dictionnaire indexé par un id ou un titre.** `__proto__`,
  `constructor`, `toString` sont des `ConceptId` valides
  (`ConceptId.IsValidFirstChar` admet `_` et les lettres) ; un objet ordinaire
  indexé par eux les confondrait avec des propriétés héritées, et
  `{"__proto__": …}` n'a pas la même sémantique exécuté comme littéral JS que lu
  par `JSON.parse`. L'index ne contient que des tableaux et des enregistrements à
  clés fixes ; les tables de recherche construites en JS sont des `Map` ou des
  objets `Object.create(null)`.
- Un test xunit fait l'aller-retour sur des titres hostiles (`</script>`,
  U+2028, U+2029, guillemets, antislash) ; comme un aller-retour JSON ne valide
  pas l'objet que le script **exécuté** crée, le harnais JS exécute en plus un
  `okf-index.js` réellement produit par le générateur (§7).
- Insertion dans le DOM **par `textContent`** ; attributs posés par
  `setAttribute` sur des noms fixes (§4.5).
- Collisions : une page de concept s'écrit toujours `<id>.html` ;
  `HtmlWriter.GuardNoCaseCollisions` laisse `assets/` hors de son ensemble et
  reçoit en P1.1 le nom de la page du graphe (§12.3). Son commentaire affirme
  « every asset path ends in `.js` or `.css` », faux dès les polices
  (`.woff2`) et leurs licences (`.txt`) : P1.1 le remplace par « every
  generated page path ends in `.html` and no asset path does ».
- Le smoke test AOT de `ci.yml` (qui vérifie surtout `index.html`) est étendu à
  la présence et au chargement d'`assets/okf-index.js`.

### 3.5 Chemins

Les chemins de page de l'index sont **relatifs à la racine du site**
(`tables/users.html`). L'emplacement du script n'est pas une base : depuis
`tables/users.html`, un `href` `glossary/term.html` viserait
`tables/glossary/term.html`. Chaque page déclare donc sa racine (par ex.
`<html data-okf-root="../">`, valeur produite par `HtmlWriter.RootPrefix`), et
**un seul résolveur** JS sert explorateur, palette et graphes. Tests : racine,
plusieurs profondeurs, dossier généré déplacé.

### 3.6 Taille et coût d'initialisation

Mesure de la revue, sur un corpus de 776 concepts et 891 arêtes (fichiers
suivis d'OKF4net, `--no-msbuild`, schéma indicatif) : 212 Ko de métadonnées et
dossiers, 249 Ko avec des arêtes en indices, 332 Ko avec des arêtes en ids
texte. D'où les indices (§3.3). Ces tailles ne disent rien de la latence
navigateur. **La mesure sur le schéma définitif précède P1** ; au chargement
d'une page, seuls l'arbre et l'état courant sont construits, les structures de
graphe le sont à la demande.

Mesure sur le schéma définitif (P1, 2026-10-06, `--no-msbuild`, commande de la
tâche 4 du plan P1) : bundle d'OKF4net — 795 concepts, 0 fantômes, 912 arêtes,
`okf-index.js` de 280890 octets ; `acme_retail` — 9 concepts, 3165 octets.

Schéma v2 (P1.1, A18, A19) : estimation de la révision 5 sur le même bundle,
par lecture directe des `description:` des 795 fichiers (pas par le générateur) :
**+127 Ko environ** (≈ 408 Ko au total, +45 %), dont 300 descriptions tronquées
à 200 points de code, médiane 124.

Protocole de mesure du schéma v2 (A23) : dès que `IndexScript` écrit le
schéma v2, et avant d'écrire le code JS qui lit les nouveaux champs, P1.1
génère le bundle d'OKF4net par la commande de la tâche 4 du plan P1 (`okfgen
generate --repo . --no-msbuild`, reprise dans `ACCEPTANCE.md`), le rend par
`okf-render`, et consigne ici la taille en octets d'`assets/okf-index.js`, avec
le nombre de concepts, d'arêtes et de fantômes, et celle d'`acme_retail`. Au-delà
de **600 000 octets** pour OKF4net, la troncature descend à 120 points de code
(§12.1) et la mesure est refaite ; sinon 200 reste. Optimisation possible plus
tard, non retenue (A23) : porter les descriptions, que seul le tiroir de
`graph.html` lit, dans un script distinct chargé par cette seule page.

## 4. JavaScript

### 4.1 Fichiers et chargement

- **Contrat de `viewer.js` inchangé.** La ROADMAP prévoit que l'extension VS Code
  réutilise `viewer.js` tel quel (payload `{ body, links }`). Le nouveau code vit
  dans des fichiers séparés sous `Assets/`, avec **son propre amorçage**,
  indépendant de celui du corps ; `viewer.js` ne dépend d'aucun nouveau global.
- **Scripts classiques, pas de modules ES** (bloqués en `file://`). Chaque
  fichier expose un global, chargeable par le harnais Node comme `run.js` charge
  déjà `viewer.js`.
- Fichiers : livrés par P1 — `okf-theme.js`, `okf-site.js` (`OkfSite`),
  `okf-explorer.js`, `okf-palette.js`, `okf-toc.js` ; créés par P1.1 —
  `okf-shapes.js` (`OkfShapes`, §12.2), `okf-page.js` (tête de la colonne
  centrale, glyphes des listes) ; créé par P2 — `okf-local.js` ; créés par P3 —
  `okf-sim.js` (`OkfSim`, §12.5) et `okf-graph.js`. Chaque script est créé par
  sa tranche propriétaire, jamais en fichier vide d'avance (§12.0). Ordre de
  chargement : §12.6.
- `okf-page.js` et `okf-local.js` sont chargés aussi par `index.html` et y
  rendent la main sans rien faire : ils n'agissent que si `<html>` porte
  `data-okf-view="page"`.

### 4.2 Simulation du graphe global (module maison)

Module pur, sans DOM :

- entrée triée (§3.1) ; graine dérivée de cet ordre ; ni `Math.random` ni
  horloge ;
- **primitives restreintes** : `+ − × ÷ %`, `Math.sqrt`, dont ECMAScript fixe
  l'arrondi, et les fonctions exactes `Math.floor`, `Math.ceil`,
  `Math.trunc`, `Math.abs`, `Math.min`, `Math.max`, `Math.imul` ; interdites :
  toute autre fonction `Math` (`sin`, `cos`, `exp`, `pow`, `atan2`… sont
  approchées selon le moteur) et l'opérateur `**`, approché comme `Math.pow` ;
- **ordre des accumulations fixé** (nœuds par indice, cellules de grille dans un
  ordre fixe) ;
- **deux bornes** : un nombre maximal d'itérations, et un budget de travail qui
  compte les **paires candidates visitées** (pas seulement les forces
  calculées), avec un plafond par cellule, plus les **interactions de ressorts** ;
  chaque tranche a son propre budget ;
- **suspension à l'intérieur d'une itération** : quand le budget d'une tranche
  est épuisé, le calcul se suspend là où il en est — curseurs (nœud, cellule,
  arête en cours) et accumulateurs de forces conservés — et reprend à la tranche
  suivante **dans le même ordre d'opérations**. Une itération peut donc couvrir
  plusieurs tranches ; les positions ne sont mises à jour qu'à la fin d'une
  itération complète, si bien que l'état après k itérations est le même quel que
  soit le découpage. (Imposer qu'une itération tienne toujours dans une tranche
  est intenable : son coût croît avec le nombre de nœuds, alors que le budget de
  tranche est une constante faite pour garder les frames courtes.)
- graphe vide ou à un nœud : terminé sans itérer ;
- **annulation** : un changement de filtre invalide la simulation en cours
  (jeton de génération) ;
- au-delà d'un seuil de nœuds visibles (ordre de grandeur 1 500, fantômes
  compris, à calibrer), la simulation ne démarre pas : le graphe demande de
  restreindre (A6) ;
- le glisser-déposer modifie ensuite la disposition : le déterminisme vaut pour
  la disposition initiale.

Sous ces contraintes, même entrée → même disposition initiale sur tout moteur
conforme. Le harnais tourne sous V8 : il prouve le déterminisme et les bornes
sous V8, pas l'égalité entre moteurs, qui repose sur les contraintes ci-dessus.

### 4.3 Filtres, fantômes, graphe local

- **Facettes** (A10) : ET entre facettes, OU à l'intérieur d'une facette.
- **Fantômes sous filtre** (A10) : un fantôme est visible dès qu'**au moins une
  source visible** le cite ; les facettes ne le filtrent pas (il n'a ni type ni
  confiance) ; il compte dans le seuil global ; sa sélection affiche « absent »
  et l'id cible, sans lien.
- **Graphe local** : disposition en anneaux, sans simulation, calculée depuis
  l'index (la table de liens de la page n'a ni liens entrants ni second saut).
  L'appartenance au voisinage est **non orientée** (liens sortants et entrants),
  les arêtes sont dessinées **orientées** (la relation OKF l'est). **Plafond
  (A11) : 40 nœuds** ; au-delà, voisins directs d'abord, puis ordre
  `ConceptId.CompareTo`, avec « +N omis » et un lien vers la liste complète
  accessible. Les cibles absentes voisines apparaissent comme fantômes (non
  navigables, comptés dans le plafond) ; le second saut ne part jamais d'un
  fantôme. Arêtes dessinées : **toutes** les arêtes de l'index dont les deux
  extrémités sont montrées (pas seulement celles du parcours) ; deux arêtes
  opposées A → B et B → A sont décalées de ± 3 perpendiculairement, comme
  G12. La disposition en anneaux peut utiliser `Math.cos`/`Math.sin` : la
  contrainte de déterminisme entre moteurs (§4.2) ne vaut que pour la
  simulation globale. Contrat DOM : §12.4.

### 4.4 Péremption (A2, A9)

- L'index porte `staleAfterMs` : `Lifecycle.StaleAfter` converti en
  millisecondes depuis l'epoch Unix, **arrondi vers le haut** au milliseconde
  entier. Comme `Date.now()` est un entier de millisecondes,
  `Date.now() >= staleAfterMs` équivaut exactement à `now >= StaleAfter`
  (`Lifecycle.IsStale`). Une chaîne ISO ne convient pas : comparée à un nombre
  elle donne `NaN`, et `Date.parse` perd les fractions sous la milliseconde
  (`2026-10-06T00:00:00.0001000Z` serait périmé en JS à `.000Z` et pas en C#).
- L'affichage de la date utilise le champ **`staleAfterDate`** de l'index
  (`Lifecycle.StaleAfterDate`, `yyyy-MM-dd`, ou `null`), préparé en C# ; il n'est
  **jamais reconstruit** depuis `staleAfterMs` : une échéance à
  `23:59:59.9999Z` est arrondie au lendemain en millisecondes, alors que sa date
  est encore celle du jour. Test xunit à cette frontière de minuit.
- **Recalcul** (A9) : au chargement de la page et à chaque retour au premier plan
  (`visibilitychange` vers `visible`).
- C'est la seule part de §5.5 refaite en JS — une comparaison — et une exception
  consciente à la règle « le calcul d'audit n'est pas dupliqué » de `CLAUDE.md`,
  consignée dans `CLAUDE.md` avec P1. Conséquence assumée : le site affiche la
  péremption au moment de la lecture et peut différer d'un `okf audit` lancé le
  jour de la génération.
- Tests xunit : frontière sous la milliseconde, offsets non UTC, formes anciennes
  acceptées par `Lifecycle` (date seule), valeur malformée → `null`.

### 4.5 SVG applicatif

Le graphe est un SVG **construit par le code**, distinct du contenu markdown :
le sanitizer exclut les éléments SVG (`ALLOWED_TAGS` dans `viewer.js`) et **son
allowlist n'est pas élargie**. Noms d'éléments et d'attributs fixes ; formes et
couleurs tirées d'une table contrôlée indexée par type ; coordonnées numériques
vérifiées finies ; libellés par `textContent` ; URL uniquement dérivées des
chemins de l'index via le résolveur (§3.5). Toutes les formes **de type, de
confiance, de péremption et de fantôme** — explorateur, palette, puces,
listes, légendes, graphes — sortent d'**un seul** module, `okf-shapes.js`
(§12.2) ; aucune autre tranche n'en dessine. Les icônes d'interface (lune de
H11, loupe de J2) sont des constantes du module qui les pose, dans le même
vocabulaire SVG fixe.

### 4.6 Palette (A5, A12)

- Normalisation de la requête et des textes : suppression des espaces aux
  extrémités, espaces internes fusionnés, `String.prototype.toLowerCase`
  (indépendant de la locale) ; **pas** de pliage des accents.
- Requête vide : aucun résultat.
- Paliers fixes : id exact, puis préfixe du titre ou de l'id, puis sous-chaîne du
  titre ou de l'id, puis tag exact. Un concept qui correspond à plusieurs
  critères prend son **meilleur palier**. Départage : position dans l'index.
  Aucune pondération : rien de commun avec `ConceptSearch`.
- Raccourcis : voir §8.

### 4.7 Thème

Par défaut `prefers-color-scheme` ; le bouton force clair ou sombre par un
attribut sur `<html>`. Choix mémorisé en `localStorage` sous `try/catch` ; en
`file://`, même une écriture réussie ne garantit pas le partage entre pages
locales. Le thème est appliqué **avant le premier rendu, sur chaque page**,
avec et sans stockage disponible.

## 5. Sommaire, ancres et fragments (A13)

Constat vérifié par la revue : aujourd'hui un titre `## Usage` ne reçoit aucun
id, et un id HTML fourni par le bundle est supprimé par le sanitizer
(`ALLOWED_ATTRS`). Les fragments que `SiteModel.FragmentOf` recolle sur les liens
(`a/b.html#usage`) n'ont donc aucune cible — défaut préexistant, que P1 corrige.

- Les ids sont générés par le viewer **après** la sanitation : préfixe `okf-h-`
  suivi d'un identifiant dérivé du texte du titre par une règle unique (fixée
  par P1 : `OkfSite.slugify`, `uniqueSlugs` — minuscules, espaces en `-`,
  ponctuation retirée), suffixes `-1`, `-2` en cas de
  doublon. Le plan doit couvrir : les fragments déjà préfixés (`#okf-h-usage`),
  les doublons, et les collisions entre un suffixe généré et un titre réel (deux
  « Usage » puis un titre « Usage 1 » ne doivent pas se disputer `usage-1`).
- **Fragments existants** : un fragment `#usage` (au clic, et à l'ouverture
  directe d'une URL avec fragment) est résolu vers l'élément `okf-h-usage` par la
  même règle, puis l'élément est atteint et reçoit le focus de lecture.
- Aucun `id` ni `name` venu du contenu n'est jamais admis : un titre portant
  `id="OKF_INDEX"` masquerait `window.OKF_INDEX` si le script d'index manquait
  (DOM clobbering).

## 6. Accessibilité

Voir aussi le contrat clavier (§8).

- Le graphe n'est jamais le seul chemin : une **liste équivalente** (mêmes
  concepts, mêmes relations, en texte) est disponible.
- Types distingués par la forme *et* la couleur ; contraste de texte ≥ 4,5:1
  **dans les deux thèmes** ; contraste des formes (élément non textuel) ≥ 3:1
  contre le fond et contre le fond de ligne active, dans les deux thèmes
  (valeurs calculées en §11.0, **mesurées** par la recette).
- Vrais `<button>`/`<a>`/`<input>` étiquetés partout ; les glyphes sont
  décoratifs (`aria-hidden="true"`), l'information qu'ils portent existe en
  texte à côté ou dans la légende. Là où aucune légende n'est visible, ce
  texte est visuellement masqué (classe `okf-sr` de P1) : nom du type dans une
  option de palette (J4, la modale cache l'explorateur), palier et « stale »
  dans une ligne de l'explorateur (E10), nombre de filtres actifs dans
  « Filters » (H8).
- Recette P1 (connu, non bloquant) : sous 1 100 px l'ordre visuel est contenu →
  panneau → explorateur mais l'ordre de tabulation reste celui du DOM ; un lien
  « Skip to content » est livré par P1.1 (§11.1, H12).

## 7. Garde-fous

`CLAUDE.md` : xunit ne peut pas exécuter ce JS ; un test qui grep un marqueur
n'est pas une preuve. Le harnais `tools/viewer-security-check/` (job CI
`viewer sanitizer (JS)`) est **étendu** ; son helper actuel (corps + payload,
deux fichiers chargés) reste tel quel pour le sanitizer, et on ajoute :

- un **helper de page complète** (explorateur, palette, graphe, sommaire, avec
  tous leurs scripts) ;
- un **ordonnanceur déterministe injecté** à la place de
  `requestAnimationFrame`, vidé explicitement par les tests :
  `requestAnimationFrame` existe sous `pretendToBeVisual` (option d'`openPage`)
  mais n'est pas déterministe ; `okf-graph.js` ne lit `window.OKF_SCHEDULER`
  que si `typeof OKF_SCHEDULER === "function"` (§12.5) ;
- une **attente explicite** des tests asynchrones avant le décompte final et
  `process.exit` (aujourd'hui `check` est synchrone : une assertion dans une
  Promise pourrait passer après la sortie sans protéger la CI) ;
- l'exécution d'un `okf-index.js` **réellement produit** par le générateur C#
  sur un bundle hostile (fixture régénérée par un test ou génération dans le job
  CI : choix au plan).

Contrôles :

1. **Simulation** : graphes vide, singleton, sans arêtes, agglutiné, à arêtes
   répétées, de plusieurs milliers de nœuds ; aucune valeur `NaN`/infinie,
   positions bornées, itérations ≤ borne, travail compté ≤ budget (**compté,
   jamais chronométré**) ; changements rapides de filtre et annulation ; **une
   itération plus coûteuse qu'une tranche** (budget de tranche minimal) se
   suspend et reprend sans dépasser le budget de chaque tranche.
2. **Déterminisme** : même entrée → même sortie sur deux exécutions **et sous
   des découpages en frames différents**, y compris des découpages qui coupent
   une itération en son milieu ; contrôle statique qu'`okf-sim.js` n'appelle
   aucune fonction `Math` hors de la liste de §4.2 et ne contient pas
   l'opérateur `**` (smoke check, dit comme tel dans son commentaire). Validé
   sous V8 seulement.
3. **Texte hostile** : titre/id/tag contenant du HTML (`<img onerror=…>`) inerte
   dans l'explorateur, la palette, le graphe et le sommaire, DOM inspecté
   **après** ouverture, filtrage et sélection. L'assertion actuelle qui interdit
   tout namespace non XHTML s'applique au contenu du corps ; le SVG applicatif
   est vérifié à part (vocabulaire fixe, §4.5) et ne doit jamais apparaître dans
   `okf-body`.
4. **Clobbering** : **avec et sans index** chargé ; aucun `id`/`name` venu du
   contenu ne survit nulle part (une assertion sur le seul global ne détecterait
   pas la régression quand l'index est déjà affecté).
5. **Péremption** : horloge du realm jsdom remplacée avant chargement du script ;
   avant, à et après l'échéance, frontière de précision (§4.4), `null` jamais
   « stale », recalcul sur `visibilitychange`.
6. **Palette** : ordre des paliers, normalisation, meilleur palier, requête vide,
   interactions clavier et sélection après filtrage.
7. **Clés hostiles** : concepts d'id `__proto__`, `constructor`, `toString`
   présents et distincts dans l'explorateur, la palette et le graphe.
8. **Contrat VS Code** : `viewer.js` seul, avec seulement `{ body, links }` et
   sans aucun nouveau global, rend et nettoie comme aujourd'hui.
9. **Clavier du graphe** : deux composantes et un nœud isolé tous atteignables
   par précédent/suivant ; focus déplacé selon §8 quand un filtrage masque le
   nœud actif.
10. **Formes** (P1.1) : `OkfShapes` ne crée que le vocabulaire SVG fixe de
    §12.2, rejette une coordonnée, une taille ou un trait non finis et une
    forme inconnue, n'emploie jamais un texte du bundle comme nom de classe ;
    pour chaque forme et chaque contexte de la table, les attributs produits
    sont exactement ceux du tableau de §12.2 (valeurs attendues écrites en dur
    dans le cas, pas recalculées) ; le sablier n'a le `d` d'aucune forme de
    type ; `node` ne pose ni `<title>` ni `<text>` ; classement des types lu
    dans l'index, jamais recalculé ; aucun `svg` dans `okf-body`.
11. **Tête de page** (P1.1) : `status`, vérificateur, date, valeurs de
    frontmatter et nom du bundle hostiles restent du texte inerte ; puce de
    péremption basculée aux mêmes instants que le contrôle 5 ; « Show all »
    annoncé (`aria-expanded`) ; un `<code>` du corps portant les classes de
    chrome de P1.1 (`okf-chip-glyph`, `okf-fm`, `okf-row`,
    `okf-section-title`…) reste inchangé — ni masqué, ni garni d'un glyphe —
    après « Show all » et le placement des glyphes (requêtes ancrées, §12.6).
12. **Graphe local** (P2) : plafond de 40 nœuds et « +N omis », 1 et 2 sauts,
    fantômes non navigables, liste équivalente complète, arêtes pleines et
    tiretées selon §12.4, tailles des formes = contextes `local` et
    `localCenter` de §12.2.
13. **Page `graph.html`** (P3) : fragment hostile, inconnu ou d'un fantôme →
    aucune sélection ni lien ; fragment d'un concept → sélection et centrage ;
    fragment d'un concept masqué par les facettes (`hashchange`) → facettes
    remises à leur défaut puis sélection ; au-delà de `NODE_LIMIT`, la
    sélection par fragment remplit le tiroir et la liste, aucun dessin ;
    « Reading view » pointe la page du concept sélectionné ; sélection reflétée
    par `history.replaceState`, et par `location.replace` quand
    `replaceState` lève, jamais par une nouvelle entrée d'historique
    (`history.length` inchangé) ; sélectionner un fantôme retire le fragment.
14. **Aucune requête hors du site** : chaque page (y compris `graph.html`) ne
    charge que des chemins relatifs du site généré — polices comprises.

Côté xunit : la projection `SiteModel` → index (ordre `ConceptId.CompareTo`,
arbre destination + enfants, arêtes fusionnées et fantômes, palier issu de
`ConceptAudit` sous horloge fixe, `staleAfterMs` aux frontières, sérialisation
hostile), l'écriture d'`assets/okf-index.js` par `HtmlWriter`, et la racine
déclarée par chaque page. Les tests de marqueurs source dans `ViewerAssetsTests`
disent dans leur commentaire qu'ils sont de simples vérifications de surface.
Chaque tranche ajoute ses tests xunit dans **ses propres classes**
(`HtmlWriterHeaderTests`, `PageHeadTests`… pour P1.1,
`HtmlWriterGraphPageTests` pour P3), sans éditer celles d'une autre ; seul
P1.1 modifie les classes de P1, pour les comportements qu'il change (par
exemple le compte exact de balises `</script>` de `HtmlWriterTests`, qu'il
rend indépendant du nombre de scripts : il compare les ouvertures et les
fermetures).

**Hors de portée du harnais** : jsdom ne fait pas de layout (`getBBox` absent),
donc fluidité, géométrie, contraste, absence de flash, chargement des polices et
raccourcis navigateur relèvent de la **recette** (A14) : une recette écrite,
exécutée à chaque tranche, en `file://`, sur `acme_retail` et sur le bundle
d'OKF4net, dans Chrome, Edge et Firefox (Safari en meilleur effort), outillée
par un script de recette suivi dans le dépôt et hors CI (§12.8). IME et lecteur
d'écran sont reportés à l'issue #177 et ne bloquent pas la recette d'une
tranche (A21).

Aucun golden ne couvre `okf-render` (`tests/fixtures/golden/` couvre audit, fmt,
graph, info, validate, verify) : pas d'arbitrage golden attendu. `graph.dot`
fige en revanche l'ensemble d'arêtes de `okf graph` (A3).

## 8. Contrat clavier et focus

- **Palette** : dialogue modal (modèle WAI-ARIA *dialog modal*). À l'ouverture,
  focus dans le champ ; Tab et Maj+Tab restent dans le dialogue ; Échap ferme ;
  flèches haut/bas déplacent l'option active, Entrée ouvre ; nombre de résultats
  annoncé (région live) ; si un filtrage supprime l'option active, la sélection
  passe à la première option ; l'option active conservée d'un filtrage à
  l'autre est celle que le lecteur a choisie avec les flèches depuis
  l'ouverture, tant qu'aucun filtrage ne l'a supprimée (le choix est alors
  oublié) : sans ce choix, la sélection est la première option ; à la
  fermeture, focus rendu au déclencheur.
- **Raccourcis** : un **bouton visible** ouvre toujours la palette. Ctrl K et `/`
  sont captés seulement si aucun champ éditable n'a le focus, sans autre
  modificateur (Alt, Meta), hors composition IME (`isComposing`) ;
  `preventDefault` uniquement quand le raccourci est capté. Ctrl K est aussi un
  raccourci de Chrome et Firefox, `/` de Firefox : la recette vérifie le
  comportement réel.
- **Graphe** : un seul arrêt de tabulation pour tout le graphe (tabindex
  itinérant), flèches pour passer d'un nœud à ses voisins, Entrée pour ouvrir ;
  **précédent/suivant dans l'ordre de l'index** parmi les nœuds visibles,
  indépendamment des arêtes (touches à fixer au plan, par ex. Page préc./Page
  suiv., Début/Fin pour le premier et le dernier), pour atteindre un nœud isolé
  ou une autre composante ; si un filtrage masque le nœud actif, le focus passe
  au nœud visible suivant dans l'ordre de l'index, à défaut au précédent, à
  défaut au conteneur du graphe ;
  zoom, dézoom et recentrage par boutons ; la liste équivalente remplace le
  glisser-déposer pour qui ne peut pas glisser.
- **Explorateur** : commandes distinctes ouvrir/déplier (§3.2), état
  replié/déplié annoncé ; puces de type = boutons bascule (`aria-pressed`).
- **Filters** (en-tête) : place le focus dans le champ de filtre de
  l'explorateur (le navigateur le fait défiler en vue, y compris en mise en page
  empilée).
- **Graphe local** : le SVG est une image (`role="img"`, nom résumé) cliquable à
  la souris, sans arrêt de tabulation ; le chemin clavier est la liste
  équivalente du panneau (§12.4) et « Referenced by ». Boutons 1/2 sauts en
  `aria-pressed`.
- **Facettes** (P3) : vraies cases à cocher et boutons bascule ; un changement
  met à jour la ligne d'état (`role="status"`).
- **Thème** : bouton à nom d'action explicite ou état annoncé (`aria-pressed`).
  P1 livre le nom « Dark theme » avec `aria-pressed` ; P1.1 garde ce nom et cet
  état sous l'icône de la maquette.

## 9. Livraison en tranches indépendantes

- **P1 — livré** (2026-10-07, recette verte après R1–R6) : mesure du schéma
  d'index (§3.6), coque A sauf le graphe : explorateur, palette, bascule de
  thème, `assets/okf-index.js` (schéma v1), panneau droit avec sommaire,
  fragments (§5) et backlinks. Documentation (`CLAUDE.md`, README, ROADMAP)
  faite.
- **P1.1 — fidélité** (A15, A16, A18, A19, A22–A30 ; tout ce que §11 marque
  « P1.1 ») : schéma d'index v2 (§12.1) et sa mesure (§3.6) ; module de formes
  et légendes (§12.2) ; en-tête des **trois** vues, y compris celle de
  `graph.html` (§12.3), et nom du fichier du graphe ; colonne centrale (fil
  d'Ariane, puces, boîte de frontmatter, sans double titre) ; explorateur
  (glyphes, puces de type, libellé = segment, comptes, légende, style de
  ligne active) ; section courante du sommaire ; glyphes et compte de
  « Referenced by » ; palette au dessin de C ; polices embarquées et leur
  vérification en `file://` (A26), `NOTICE`, `CLAUDE.md` ; jetons de couleur et
  typographie (§11.0) ; composants partagés (§12.6) ; **plomberie partagée**
  (§12.0) : écriture générique des assets embarqués, table des scripts des
  pages, repères de P2 et P3, sections réservées dans `viewer.css`, chargeur
  des cas de tranche dans `run.js`, `ACCEPTANCE.md` et recette (§12.7,
  §12.8). P1.1 ne crée **aucun** fichier de P2 ou de P3.
- **P2 — graphe local** (§4.3, §12.4) : crée `okf-local.js` et l'inscrit dans
  la table des scripts des pages (une ligne de C#, à son repère) ; sa section
  de `viewer.css`, `cases/p2.js`, ses fixtures `p2-`, sa recette.
- **P3 — graphe global** (§4.2, §4.3, §12.5) : crée `okf-sim.js` et
  `okf-graph.js`, écrit `graph.html` par `HtmlWriter.RenderGraph` (la seule
  méthode qui écrit cette page et charge ces deux scripts), sa section de
  `viewer.css`, `cases/p3.js`, ses fixtures `p3-`, sa recette.

Dépendances et parallélisme :

- **sans dépendance** : le module de simulation `okf-sim.js` de P3 (pur, sans
  DOM, contrat §12.5) et ses contrôles 1 et 2, dans `cases/p3.js`, peuvent
  commencer tout de suite : ce sont deux fichiers nouveaux, qu'aucune autre
  tranche ne touche ; `cases/p3.js` est exécuté dès que le chargeur des cas
  de P1.1 (sa première tâche, §12.7) est sur la branche ;
- **après le contrat d'index, de formes et de coque de P1.1** (§12.0 à §12.3
  livrés et fusionnés sur la branche) : l'interface de P2 et celle de P3 ; P2
  et P3 sont alors parallèles entre eux, car ils n'écrivent dans aucun fichier
  commun hors de leurs repères et sections réservées par P1.1 (§12.0) ;
- les plans des trois tranches sont rédigés en parallèle à partir de cette
  révision ; leur exécution suit l'ordre ci-dessus.

Toutes les tranches s'accumulent sur `feat/viewer-interactive-p1` ; une seule
PR à la fin (A20). Chaque tranche a son propre plan et sa recette ; entre deux
tranches la branche peut contenir un état intermédiaire (par exemple « Global
graph » sans `graph.html` avant P3), que la recette de la tranche en cours ne
compte pas comme défaut.

## 10. Arbitrages du propriétaire

Ces décisions sont celles du propriétaire, consignées ici ; elles ne sont pas
des vérifications techniques.

Rendus le 2026-10-06, après la relecture interne :

- **A1 — Confiance : trois paliers `ConceptAudit`.** `unverified`,
  `machine-confirmed`, `human-reviewed` (§5.3), libellés `AuditVocabulary` tels
  quels, en pastilles et en facette.
- **A2 — Péremption : évaluée dans le navigateur.** Analyse en C#, comparaison
  en JS (§4.4). Option écartée : évaluation figée à la génération.
- **A3 — Arêtes : celles de `okf graph`.** Liens du corps ; liens cassés vers un
  fantôme. Les ressources de frontmatter ne sont pas des arêtes.
- **A4 — ROADMAP : rapatriement** de l'explorateur et du graphe dans
  `okf-render` ; l'entrée *Later* correspondante est retirée avec P1.
- **A5 — Palette : paliers fixes**, sans pondération.
- **A6 — Au-delà du seuil global : filtre obligatoire**, pas de regroupement.
- **A7 — Thème : bouton de bascule**, défaut système.
- **A8 — P1 inclut le panneau droit** (sommaire et backlinks), sans graphe.

Rendus le 2026-10-06, après la revue externe :

- **A9 — Fréquence de recalcul de la péremption** : au chargement et au retour
  au premier plan. Options écartées : au chargement seulement ; minuterie à
  l'échéance.
- **A10 — Fantômes et facettes** : fantôme visible si une source visible le
  cite, non filtré par les facettes, compté dans le seuil, non navigable ; ET
  entre facettes, OU dans une facette.
- **A11 — Plafond du graphe local : 40 nœuds**, voisins directs d'abord, « +N
  omis » et liste complète accessible.
- **A12 — Normalisation de la palette** : simple et explicite (§4.6), sans
  pliage des accents.
- **A13 — Fragments existants pris en charge dans P1** (§5).
- **A14 — Navigateurs** : Chrome, Edge, Firefox (dernières versions desktop) ;
  Safari en meilleur effort ; recette manuelle écrite à chaque tranche.

Rendus le 2026-10-07, après la recette de P1 :

- **A15 — Colonne centrale comme la maquette A** : fil d'Ariane (nom du bundle
  / dossiers / id), H1, rangée de puces (type avec sa forme ; `status` s'il
  existe ; confiance = palier, plus vérificateur et date du `verified` §5.2
  s'ils existent ; « stale after `<date>` » ou état périmé), frontmatter dans une
  boîte repliable « Frontmatter · N fields » avec « Show all », en grille de
  deux colonnes. Le corps reste rendu par `viewer.js`, inchangé ; ces éléments
  sont produits par `HtmlWriter` ; le cœur n'est pas modifié (lecture par API
  publique, §2.1). Double titre : §11.3, C4.
- **A16 — Polices embarquées** : Inter (400, 500, 600), Inter Tight (600, 900),
  Space Mono (400, 700), SIL OFL 1.1, fichiers woff2 intégrés à
  `OKF4net.Viewer`, écrits sous `assets/`, chargés par `@font-face` relatif ;
  mention dans `NOTICE` ; exception documentée dans `CLAUDE.md` à côté de
  `marked` ; taille mesurée et consignée (§11.0).
- **A17 — Graphe global = page `graph.html`** à la racine du site. « Global
  graph » dans l'en-tête de chaque page → `graph.html#<id du concept courant>`,
  ouvert centré et sélectionné sur ce concept ; dans `graph.html`, « Reading
  view » ramène à la page du concept sélectionné, ou à l'index. Précédent et
  Suivant natifs ; URL partageable. Collision de nom : A25, §12.3.
- **A18 — L'index porte la description**, tronquée à 200 points de code (règle
  §12.1) ; taille mesurée et consignée (§3.6).
- **A19 — Encodage des types** : les cinq types les plus fréquents (fréquence
  décroissante, départage ordinal du nom) reçoivent dans l'ordre cercle, carré,
  losange, triangle, anneau et les couleurs des maquettes ; les autres partagent
  une forme « autre » ; légende partout où les formes apparaissent ; classement
  calculé en C# et porté par l'index ; forme et couleur, jamais la couleur
  seule ; en sombre, couleurs adaptées, contraste des formes ≥ 3:1 mesuré.
- **A20 — Une branche, une PR** : P1, P1.1, P2 et P3 s'accumulent sur
  `feat/viewer-interactive-p1` ; la PR #176 est en brouillon jusqu'à la fin.
- **A21 — IME et lecteur d'écran** : vérification manuelle reportée à l'issue
  #177 (priorité basse) ; ne bloquent pas la recette des tranches.

Rendus le 2026-10-07, après la révision 5 et sa revue :

- **A22 — Libellé des lignes de l'explorateur = dernier segment de l'id**,
  comme la maquette A (`gross-margin-period`) ; titre du concept en
  infobulle (attribut `title`) et dans la palette ; un dossier montre son
  segment. Change le comportement de P1 (qui montre le titre) : P1.1 livre le
  changement (E8) et met à jour les helpers du harnais qu'il casse (§12.7).
- **A23 — Description dans l'index conservée (A18), 200 points de code** ;
  descente à 120 seulement si l'index mesuré d'OKF4net dépasse
  **600 000 octets** (protocole : §3.6). Le transfert des descriptions dans un
  script propre à `graph.html` n'est pas retenu maintenant (optimisation
  possible plus tard).
- **A24 — Péremption = sablier ambre**, une seule `path` (deux triangles
  joints pointe à pointe), environ 9 × 10, `--stale` (`#b4540a` en clair,
  `#e08a3e` en sombre, contraste ≥ 3:1 mesuré), toujours accompagné du mot
  « stale » là où la place le permet (puces, tiroir) et dans la légende ;
  drapeau de droite dans les lignes de l'explorateur. Écart voulu au triangle
  des maquettes (§11.8) ; le triangle n'est plus qu'une forme de type.
- **A25 — Nom de la page du graphe** : `graph.html`, sinon `graph-1.html`,
  `graph-2.html`… en cas de collision ; jamais de refus de rendu ; le lien
  « Global graph » porte le nom réel (§12.3).
- **A26 — Polices en `file://`** : vérifiées en premier par P1.1, dans Chrome,
  Edge et Firefox, à **chaque** profondeur de page (procédure §11.0) ; si un
  seul navigateur les bloque, elles sont servies en URI `data:` dans
  `assets/okf-fonts.css`.
- **A27 — Frontmatter replié** : les quatre premières entrées non déjà
  montrées au-dessus (C6).
- **A28 — Classement des types sur données réelles** : la règle d'A19
  s'applique, même quand elle diffère de la maquette (BigQuery Table / Skill
  sur `acme_retail`, §12.1).
- **A29 — « Tab to show it in the graph »** (palette de C) abandonné (J7).
- **A30 — Éléments exigés par §6 et absents des maquettes** livrés tels que
  spécifiés : liste équivalente et bascule « List » (G10), « Skip to content »
  (H12), « +N omitted » et liste du graphe local (X9).

Recette de P1 (2026-10-07, `E:/tmp/okf4net-p1-recette/RECETTE.md`, quatre
navigateurs) : les six défauts R1–R6 sont corrigés sur la branche (`b2b0558`,
`7da06bc`, `4522679`, `567a7ed`, puis `a7e5f37`, `50bf643`, `ba90bd4` après
revue). R3 était déjà reflété en §8 ; les cinq autres, qui portent sur la mise
en page, sont désormais consignés en §11 comme comportements livrés : panneaux
qui ne s'élargissent pas et aucun défilement horizontal à 390 px (R1),
explorateur qui fait défiler sa propre vue jusqu'à l'entrée courante (R2),
titres de palette coupés seulement s'ils ne tiennent pas (R4), `color-scheme`
qui suit le thème (R5), ordre visuel contenu → panneau → explorateur sous
1 100 px (R6).

Choix de rédaction non arbitrés, contestables : voisinage local non orienté
(§4.3) ; fusion des arêtes répétées avec compte (§3.3) ; règle exacte de
dérivation des ids de titres renvoyée au plan (§5) — fixée depuis par P1
(`OkfSite.slugify`, `uniqueSlugs`) ; et les choix de §11 et §12 non dictés par
une maquette ou un arbitrage, listés en §13.

## 11. Fidélité aux maquettes

Check-list **exhaustive** des éléments visibles de A et B et de la palette de
C. Chaque élément porte un identifiant (repris par la recette, §12.8), les
valeurs relevées dans le source des maquettes, et la tranche qui le livre :
**P1** (déjà livré), **P1.1**, **P2**, **P3**, ou **écarté** avec la raison.
« P1 → P1.1 » signifie : fonction livrée par P1, dessin aligné par P1.1. Les
données des maquettes (`acme_retail`, 9 concepts, 14 liens) sont
illustratives. Tailles en px. Ces identifiants (H, E, C, X, G, J, L) sont
distincts des contrôles numérotés du harnais (§7, 1 à 14) et des contrôles de
la recette de P1, renommés RC1–RC11 (§12.8). Un écart à une maquette est
toujours écrit « écart » avec sa raison.

### 11.0 Jetons

Couleurs (custom properties sur `:root`, redéfinies pour le sombre dans les deux
blocs de P1, `[data-theme="dark"]` et `prefers-color-scheme`). Les valeurs
claires sont celles des maquettes ; les sombres reprennent P1 et ajoutent celles
qui manquaient. Contrastes calculés (WCAG) contre `--white`, puis contre le fond
de ligne active `--blue-soft` ; la recette les **mesure** (A19).

| Jeton | Clair | Sombre | Usage | Contraste clair / sombre |
| --- | --- | --- | --- | --- |
| `--white` | `#ffffff` | `#101014` | fond | — |
| `--ink` | `#101014` | `#f2f2f5` | texte, carré | 19,0 / 17,0 |
| `--blue` | `#1a3fd6` | `#8fa5f5` | accent, cercle | 7,7 / 8,0 |
| `--blue-hover` (P1.1) | `#102a96` | `#b7c5f8` | lien survolé | ≥ 4,5 à mesurer |
| `--blue-soft` | `#eef1fd` | `#1a1a22` | ligne active, tête de boîte, `pre` | — |
| `--gray` | `#6a6a72` | `#9a9aa2` | texte secondaire, triangle | 5,4 / 6,8 |
| `--hair` | `#e3e3e8` | `#2a2a33` | filets | — |
| `--red`, alias `--ghost` (P1.1) | `#c0392b` | `#ef6b5e` | lien cassé, fantôme | 5,4 / 6,3 |
| `--edge` (P1.1) | `#8a8a94` | `#8a8a94` | arêtes, flèches | 3,4 / 5,6 |
| `--stale` (P1.1) | `#b4540a` | `#e08a3e` | péremption (sablier, A24) | 5,0 / 7,1 |
| `--okf-type-0` cercle | `#1a3fd6` | `#8fa5f5` | rang 0 | 7,7 / 8,0 |
| `--okf-type-1` carré | `#101014` | `#f2f2f5` | rang 1 | 19,0 / 17,0 |
| `--okf-type-2` losange | `#b4540a` | `#e08a3e` | rang 2 | 5,0 / 7,1 |
| `--okf-type-3` triangle | `#6a6a72` | `#9a9aa2` | rang 3 | 5,4 / 6,8 |
| `--okf-type-4` anneau | `#0b6e69` | `#2fb3a8` | rang 4 (trait : §12.2) | 6,1 / 7,4 |
| `--okf-type-5` autre | `#6a6a72` | `#9a9aa2` | autres types (carré creux) | 5,4 / 6,8 |
| `--backdrop` | `rgba(16,16,20,.34)` | `rgba(0,0,0,.55)` | fond de palette | — |
| `--shadow` | `0 18px 50px rgba(16,16,20,.28)` | `none` | boîte de palette | — |

Contre `--blue-soft`, le minimum des formes de type, de confiance et de
péremption est 4,4:1 en clair (`#b4540a`) et 6,2:1 en sombre : toutes passent
3:1 ; le sablier `--stale` (A24) fait 5,0 / 4,4:1 en clair et 7,1 / 6,5:1 en
sombre (fond / ligne active). Constat au passage : P1 ne redéfinit pas
`--red` en sombre (`#c0392b` sur `#101014` = 3,5:1, sous le seuil texte pour
`a.broken`) ; P1.1 le redéfinit en sombre et ajoute l'alias `--ghost`.

Typographie (A16) — familles `--display` Inter Tight, `--body` Inter, `--mono`
Space Mono, avec les replis de P1 :

| Rôle | Police | Graisse | Taille | Autres |
| --- | --- | --- | --- | --- |
| marque | Inter Tight | 900 | 20 | −0,02em ; `§` Space Mono 700 12 `--blue` |
| H1 de page | Inter Tight | 600 | 34 | −0,02em, interligne 1,15 |
| H2 du corps | Inter Tight | 600 | 21 | −0,02em |
| H2 du tiroir (B) | Inter Tight | 600 | 24 | interligne 1,2 |
| texte du corps | Inter | 400 | 15,5 | interligne 1,65 |
| interface (liens, lignes, champs) | Inter | 400 / 500 / 600 | 13,5 | |
| puces, légendes, pieds | Inter | 400 / 600 | 12 | |
| titres de section | Space Mono | 400 | 11 | capitales, 0,06em, `--gray`, marge basse 8 (10 dans les facettes de B) ; classe partagée `.okf-section-title` (§12.6) |
| ids, fil d'Ariane, comptes | Space Mono | 400 / 700 | 10–13 | |

Polices embarquées (A16) : Inter 400, 500, 600 ; Inter Tight 600, 900 ; Space
Mono 400, 700 ; sous-ensemble **latin** ; woff2. Fichiers pris tels que
distribués par Google Fonts (un fichier par graisse, ou un fichier variable par
famille quand la famille n'est distribuée qu'en variable), **non modifiés** —
aucun sous-ensemble refait par nous, ce qui évite toute question de « Modified
Version » ou de nom réservé sous l'OFL. Sources :
`src/OKF4net.Viewer/Assets/fonts/`, embarquées comme les autres assets, écrites
sous `assets/fonts/` avec les textes de licence `OFL-Inter.txt`,
`OFL-InterTight.txt`, `OFL-SpaceMono.txt`. Provenance (URL, version, `sha256`,
taille) consignée dans `src/OKF4net.Viewer/Assets/fonts/README.md`, qui est
exclu de l'embarquement (`EmbeddedResource Include="Assets\**\*"
Exclude="Assets\fonts\README.md"`). `@font-face` en tête de `viewer.css`,
`url("fonts/<fichier>.woff2")` (relatif à la feuille, donc valable à toute
profondeur), `font-display: swap`, `unicode-range` du sous-ensemble. Les assets
binaires demandent une lecture en octets (`ViewerAssets`) et une écriture en
octets dans `HtmlWriter`, sous les **mêmes** gardes que `WriteFile`.
**Budget** : ≤ 300 Ko pour l'ensemble des woff2 ; taille mesurée et consignée
ici par P1.1, avec la croissance du binaire `okf-render` AOT ; au-delà, retour
au propriétaire. `okf` n'est pas concerné (il ne référence pas
`OKF4net.Viewer`).

**Vérification en `file://` (A26) — première tâche de P1.1**, avant toute
autre tâche CSS, sur le seul `@font-face` relatif ci-dessus. Depuis Firefox 68,
chaque document `file://` est une origine unique et une police web se charge
en mode CORS : l'échec peut toucher une page racine comme une page profonde, et
le comportement de Chromium se vérifie au lieu de se supposer. Procédure : sites
d'`acme_retail` et d'OKF4net (commandes d'`ACCEPTANCE.md`) ; une page de
**chaque** profondeur présente (0 = `index.html`, puis, à chaque profondeur, la
première page dans l'ordre de l'index) ; ouvertes en `file://` par la recette
outillée (§12.8, `--slices p1.1`, contrôle `fonts`) dans Chrome (canal
`chrome`), Edge (`msedge`) et le Firefox de Playwright, puis à la main dans le
Firefox installé sur la page racine et la plus profonde (onglet Réseau). Sur
chaque page, pour chacune des sept faces,
`await document.fonts.load('<graisse> 16px "<famille>"')` doit rendre un
tableau non vide dont chaque face a `status === "loaded"` (pas
`document.fonts.check`, vrai quand aucune face ne correspond), sans requête
échouée sous `assets/fonts/`. Résultat (date, versions, verdict par
navigateur et profondeur) consigné ici. **Un seul échec** : `HtmlWriter`
écrit `assets/okf-fonts.css`, ses `@font-face` en
`url("data:font/woff2;base64,…")` construits à l'écriture depuis les woff2
embarqués (mêmes `unicode-range` et `font-display`), chargé par `<link>`
juste après `viewer.css` sur toutes les pages (`RenderDocumentStart`, §12.0) ;
`viewer.css` perd ses `@font-face` ; les woff2 ne sont plus écrits à part (les
licences le restent) ; budget porté à ≤ 400 Ko pour `okf-fonts.css` ; la
vérification est refaite.

### 11.1 En-tête (pages de concept, index, `graph.html`)

- **H1** Bande de 6 `--blue` en haut, pleine largeur. → P1
- **H2** Barre : hauteur 52, filet bas 1 `--hair`, `padding: 0 20px`,
  `gap: 20px`, pleine largeur (P1 : bloc centré de 1 440, padding 16). → P1.1
- **H3** Marque « OKF4net » + `§` en exposant (jetons §11.0), lien vers
  `index.html` (P1 : « OKF »). → P1.1
- **H4** Séparateur vertical 1 × 20 `--hair`, décoratif. → P1.1
- **H5** Nom du bundle, Space Mono 13 `--ink` (contrat §12.3) ;
  `min-width: 0`, une ligne, coupé en ellipse s'il ne tient pas, nom complet
  en attribut `title` (un nom long ne pousse jamais les outils hors de
  l'écran). → P1.1
- **H6** « N concepts · M links », Space Mono 13 `--gray` ; N = concepts de
  l'index, M = arêtes fusionnées de l'index, fantômes compris ; singuliers
  « 1 concept », « 1 link » ; écrit en C#. Site construit à la main dont
  l'index est vide alors que `Pages` ne l'est pas : comptes omis. → P1.1
- **H7** Bouton de palette, dessin de C : hauteur 34, largeur 320 (rétrécit
  jusqu'à 160), bord `--hair`, texte `--gray` 13,5 « Jump to a concept… », à
  droite l'indication « Ctrl K · / » Space Mono 11 bord `--hair` padding 1 6,
  masquée sous 900 de large ; premier des outils, après l'espace flexible
  (P1 : bouton texte « Jump to... »). → P1 → P1.1
- **H8** « Filters » : hauteur 34, padding 0 14, `gap: 8px`, bord `--hair`,
  13,5 500 ; pastille de compte Space Mono 11, fond `--blue`, texte
  `--white`, padding 1 6, `aria-hidden="true"`, masquée à 0 ; le nom
  accessible porte le compte en texte masqué (« Filters, 2 active ») ; compte
  = puces de type pressées + 1 si le champ de filtre n'est pas vide ; action
  §8. Pages de concept et index seulement (absent de B). → P1.1
- **H9** « Global graph » : lien `<a>` dessiné en bouton, 13,5 600 ; sur A,
  bord `--blue`, fond `--blue-soft`, texte `--blue` ; sur `graph.html`, fond
  `--blue`, texte `--white`, `aria-current="page"`. Les deux états (balisage
  et CSS) → P1.1 (§12.3) ; leur vérification visuelle sur `graph.html` → P3
- **H10** « Reading view » (B seulement) : lien dessiné en bouton neutre
  (bord `--hair`, fond `--white`, 13,5 500), avant « Global graph »,
  `href="index.html"` à l'écriture. Balisage et CSS → P1.1 (§12.3) ; mise à
  jour de sa cible à la sélection → P3 (§12.5)
- **H11** Thème : bouton 34 × 34, bord `--hair`, icône lune 16 (chemin
  `M13 9.5A5.5 5.5 0 0 1 6.5 3a5.5 5.5 0 1 0 6.5 6.5z`, trait 1,5 `--ink`, sans
  remplissage, SVG de vocabulaire fixe) ; nom « Dark theme » et `aria-pressed`
  conservés ; pressé : bord et trait `--blue` (P1 : bouton texte). → P1 → P1.1
- **H12** Lien « Skip to content » (hors maquette, recette P1) : premier
  élément focalisable, visible seulement au focus, vers `#okf-main`. → P1.1
- **H13** Étroit : sous 900, la barre passe sur deux lignes (marque, nom,
  comptes ; puis outils) ; à 390, comptes masqués ; jamais de défilement
  horizontal. → P1.1

### 11.2 Explorateur (A)

- **E1** Panneau de 290 de large à partir de 1 100, filet droit `--hair`
  (P1 : 260 à 320). → P1.1
- **E2** Tête de l'explorateur : padding 14 16 10 ; titre « EXPLORER » (titre
  de section, §11.0, marge basse 6) ; ce titre est du texte, pas un
  `<label>` (la maquette en fait le `<label>` du champ, mais le `<nav>` est
  déjà annoncé « Explorer ») ; le nom accessible du champ **devient**
  « Filter by name » (`aria-label`) — P1 : `<label>` visible « Filter the
  explorer », retiré. → P1 → P1.1
- **E3** Champ : hauteur 34, padding 0 10, bord `--hair`, 13,5, placeholder
  « Filter by name… » ; ancêtres des résultats conservés. → P1 → P1.1
- **E4** Puces de type (A : « Metric 3 », « Attested 2 »…) : **filtre de
  l'explorateur, livré par P1.1**, et non facette de P3 — la maquette les place
  dans l'explorateur, et le « Filters 2 » de l'en-tête compte les deux puces
  pressées. Rangée `gap: 6px`, marge haute 10, retour à la ligne. Une puce par
  type de rang 0 à 4, plus une puce « Other types » (libellé de
  `typeLegendEntries`, §12.2) si des types de rang 5 existent ; texte = nom
  complet du type (pas l'abréviation « Attested ») + compte ; glyphe de forme
  (contexte `icon`) ajouté à gauche, `gap: 7px` comme C5, car ces puces sont la légende
  des types de la page (A19) — écart : la maquette n'a pas de glyphe ;
  hauteur 26, padding 0 10, 12 ; pressée : bord `--blue`, fond `--blue-soft`,
  texte `--blue` 600 ; non pressée : bord `--hair`, fond `--white`, texte
  `--gray`. OU entre puces pressées, ET avec le champ, ancêtres conservés ;
  aucune pressée = aucun filtre. → P1.1
- **E5** Liste sous un filet haut `--hair`, padding 6 0. → P1.1
- **E6** Ligne : hauteur 30, retrait 12 + 18 par niveau, `gap: 9px`, padding
  droit 14, filet gauche 3 transparent ; dans l'ordre : **un** emplacement
  de 12 × 12, comme A — chevron pour un dossier, glyphe de type (contexte
  `icon`) pour un concept ; un nœud à la fois destination et dossier (§3.2)
  a deux emplacements, chevron puis glyphe (écart : A n'a pas ce cas) — puis
  libellé (`flex: 1`, `min-width: 0`, ellipse), compte (si enfants), drapeau
  de confiance, drapeau de péremption (contexte `flag`). → P1 → P1.1
- **E7** Chevron dessiné en CSS : 6 × 6, traits 1,5 `--gray`, 45° ouvert,
  −45° fermé, dans un `<button>` de 12 de large sur la hauteur de la ligne
  (30) — cible conforme à WCAG 2.5.8 par l'exception d'espacement (`gap`
  9) ; état annoncé par `aria-expanded` (P1 : caractères ▸ ▾, bouton plus une
  espace de 24). → P1 → P1.1
- **E8** Libellé 13,5 (A22) : **le dernier segment de l'id**, comme la
  maquette (`gross-margin-period`), pour un concept comme pour un dossier ;
  concept : attribut `title` = titre du concept, attribut `data-okf-id` = id
  (posés par `setAttribute`, valeurs jamais réinterprétées) ; le filtre cherche
  toujours titre et id (P1 : libellé = titre, `title` = id). Concept : 400
  `--ink` ; dossier : 600 `--ink` (P1 : `--gray`). → P1 → P1.1
- **E9** Compte de dossier, Space Mono 11 `--gray` : nombre de concepts
  (destinations) sous le nœud, lui exclu. → P1.1
- **E10** Drapeaux (contexte `flag` de §12.2) : `human-reviewed` point plein
  8 `--blue`, `machine-confirmed` anneau 8 trait 2 `--blue`, `unverified`
  rien ; péremption **sablier** 9 × 10 `--stale` (A24 ; écart : la maquette
  dessine un triangle 9 × 8), visible seulement si périmé maintenant (§4.4) ;
  chaque drapeau porte son texte visuellement masqué (palier, « stale »). →
  P1 (formes servies par `OkfShapes` en P1.1)
- **E11** Ligne active : fond `--blue-soft`, filet gauche 3 `--blue`, libellé
  `--blue` 600 (P1 : couleur et graisse seulement). → P1 → P1.1
- **E12** Légende en pied : filet haut `--hair`, padding 12 16, `gap: 7px`
  entre entrées, `gap: 8px` entre glyphe et texte, 12 `--gray` :
  « human-reviewed », « machine-confirmed », « stale (now ≥ `stale_after`) »
  (`stale_after` en Space Mono), avec leurs glyphes (contexte `flag`,
  sablier pour « stale », A24) ; collée en bas de l'explorateur en mise en page
  large. → P1.1
- **E13** Tête collée en haut de l'explorateur (revue de recette P1),
  étendue aux puces de type. → P1 → P1.1

### 11.3 Colonne centrale (A, A15)

- **C1** Colonne : padding 26 48 0, largeur de lecture ≤ 720 (P1 : 900).
  → P1.1
- **C2** Fil d'Ariane : `nav` « Breadcrumb » en liste ordonnée, Space Mono
  12,5 `--gray`, `gap: 8px`, « / » décoratif ; nom du bundle (lien vers
  l'index) / chaque dossier (lien vers la page du concept de même id s'il
  existe, sinon texte) / dernier segment (`--ink`, `aria-current="page"`) ;
  écrit en C# ; remplace la ligne `.meta` de P1. → P1.1
- **C3** Titre de page (`<h1>`) = titre d'affichage, jetons §11.0, marges
  14 0 6 (P1 : 32). → P1 → P1.1
- **C4** **Double titre — décidé.** Lignes du corps découpées par `LfLines`
  (§2.1, `\r` final retiré) ; ligne vide = vide ou faite seulement d'espaces et
  de tabulations. Si la première ligne non vide est un titre ATX de niveau 1
  en colonne 0 (`#` puis espace ou tabulation, ou `#` seul) dont le texte —
  séquence fermante de `#` retirée **seulement** si elle est précédée d'une
  espace ou d'une tabulation, ou si elle occupe tout le contenu (CommonMark
  §4.2 : `# C#` garde « C# ») ; espaces extrêmes retirés ; espaces internes
  fusionnés — est égal **en ordinal** au titre d'affichage normalisé de même
  façon, cette ligne et les lignes vides qui la précèdent sont retirées du
  markdown que porte le payload. Règle pure dans `SiteModel` (tests xunit,
  CRLF compris), résultat dans une nouvelle propriété `ViewerPage.DisplayBody`
  (`string?`, `null` = `Body`, cas d'une page construite à la main) ; `Body`
  reste le corps brut ; `viewer.js` est inchangé. Sûr : un titre ATX n'a ni
  ligne de continuation ni effet sur l'analyse des lignes suivantes, et le
  reste passe par le même sanitizer. Non retirés (double titre conservé, sans
  risque) : titre setext, titre à mise en forme en ligne, texte différent.
  Conséquence assumée : retirer ce titre décale les suffixes d'ancre d'un
  titre homonyme du corps (`okf-h-x-1` devient `okf-h-x`). → P1.1
- **C5** Puces (rangée `gap: 8px`, marge basse 18 ; chacune hauteur 26,
  padding 0 10, `gap: 7px`, 12 ; classe partagée `.okf-chip`, §12.6 ; forme
  de type au contexte `chip`, confiance et sablier au contexte `flag` de
  §12.2) :
  - **type** : fond `--ink`, texte `--white` 600, forme de son type en
    `--white` (`.okf-chip-type`) ; nom complet, « (no type) » si absent ;
  - **`status`** : seulement si la clé existe, est scalaire et n'est pas `""` ;
    valeur brute, Space Mono, bord `--hair` ;
  - **confiance** : palier lu dans l'index (celui de l'explorateur, jamais
    re-dérivé), avec son glyphe ; paliers vérifiés : bord et texte `--blue` ;
    `unverified` : bord `--hair`, texte `--gray`, sans glyphe. Puis
    « · vérificateur · date » : entrées **retenues** = entrées `verified`
    dont `By` n'est pas nul ; ensemble du palier = les retenues dont
    `By.IsHuman` (palier humain) ou toutes les retenues (palier machine) ; on
    montre la **dernière** de cet ensemble en ordre du document ; ensemble
    vide (par exemple `verified: {at: …}` seul) : ni vérificateur ni date.
    Vérificateur = `Actor.Id` d'un acteur `human:` bien formé, sinon
    `Actor.Raw` ; date = les dix premiers caractères de `At` s'ils ont la forme
    `AAAA-MM-JJ`, sinon `At` tel quel, omise si absente ; « +N » = nombre des
    **autres** entrées du même ensemble, omis à 0 ;
  - **péremption** : seulement si `Lifecycle.StaleAfterDate` existe ; écrite
    en C# « stale after AAAA-MM-JJ » (bord `--hair`, texte `--gray`) ;
    `okf-page.js` la bascule d'après l'index, aux instants de §4.4, en « stale
    since AAAA-MM-JJ » (bord et texte `--stale`, sablier, A24).

  Glyphes posés par `okf-page.js` dans des emplacements vides écrits par le C#
  (§12.3) ; sans JavaScript, les puces restent du texte. → P1.1
- **C6** Boîte de frontmatter : bord `--hair`, marge basse 22 ; tête padding
  9 14, filet bas, fond `--blue-soft` : « Frontmatter · N fields » (titre de
  section ; N = toutes les entrées ; « 1 field ») et, à droite, « Show all » /
  « Show fewer » (12,5 600 `--blue`, sans bord, padding 0, `aria-expanded`,
  `aria-controls`), ajouté par `okf-page.js` seulement s'il y a des entrées
  repliées. Corps : grille de deux colonnes `minmax(0, 1fr)`, 13,5 ; cellule
  en ligne flexible, `gap: 12px`, padding 8 14, filet bas sauf dernière
  rangée ; clé Space Mono 12 `--gray` sur 92 ; valeur 13,5, coupure
  `anywhere` ; valeur structurée (émission YAML compacte) en Space Mono 12,5.
  Repliée (A27) : les quatre premières entrées en ordre du document hors
  `type`, `title`, `status`, `verified`, `stale_after` (déjà montrées par le
  titre et les puces) ; dépliée : toutes, en ordre du document. Les autres
  cellules portent l'attribut `data-okf-extra` (écrit en C#). Sans
  JavaScript : tout est visible. Sans saut de mise en page : `okf-theme.js`
  (dans `<head>`) pose `data-okf-js` sur `<html>`, et la règle qui masque
  `[data-okf-extra]` ne vaut que sous `html[data-okf-js]`, tant que la boîte
  ne porte pas `data-okf-expanded` (posé par « Show all ») ; si `okf-page.js`
  manquait, ces entrées resteraient masquées : il est chargé avec les autres
  assets et le harnais vérifie qu'il s'exécute sans erreur. Une séquence de
  scalaires s'affiche jointe par « , » (maquette : `finance, margin,
  attested`) ; les autres structures gardent l'émission YAML compacte de P1.
  Écart : la maquette tronque `{ resource: …, … }` ; on ne tronque pas (aucune
  perte d'information). Remplace `table.frontmatter`. → P1.1
- **C7** Corps : H2 21 marge 0 0 8 ; paragraphe 15,5 / 1,65, marge 0 0 20 ;
  tableau 13,5, marge 0 0 20, en-têtes Space Mono 12 400 `--gray`, cellules
  padding 6 12 6 0, filets `--hair` ; `pre` fond `--blue-soft`, padding 16,
  Space Mono 12,5 / 1,6 ; liens `--blue`, survol `--blue-hover`. Règles de
  contenu sous `#okf-body`, pas de chrome. → P1.1
- **C8** `index.html` : « Bundle index », compte (`site.Pages.Count`, comme
  P1), erreurs d'analyse, corps — sans maquette, inchangé ; en-tête de §11.1 ;
  ses règles `.meta` et `.errors` sont ancrées en
  `body > .okf-layout > main > :is(.meta, .errors)` (§12.6). → P1 (en-tête et
  ancrage : P1.1)

### 11.4 Panneau contextuel (A)

- **X1** Panneau de 340 à partir de 1 100, filet gauche `--hair`, padding
  20 20 0, sections espacées de 22 (P1 : 260 à 320) ; trois colonnes sans
  retour à la ligne : L6. → P1 → P1.1
- **X2** Titres de section (§11.0). → P1
- **X3** « On this page » : liens 13,5 `--ink`, sans soulignement, padding
  5 0 5 10, filet gauche 2 `--hair` ; H3 en retrait (P1 : `--blue`
  souligné). → P1 → P1.1
- **X4** Section courante : filet `--blue`, texte `--blue` 600,
  `aria-current="location"` ; courante = dernier H2 ou H3 du corps dont le haut
  est au-dessus de 25 % de la hauteur de fenêtre, à défaut le premier ;
  recalculée au défilement (au plus une fois par frame) et après la résolution
  d'un fragment. → P1.1 (`okf-toc.js`)
- **X5** « Neighbourhood » + bascule « 1 hop » / « 2 hops » : boutons accolés,
  hauteur 24, padding 0 10, 12 ; pressé fond `--blue`, texte `--white` 600 ;
  non pressé fond `--white`, bord `--hair`, texte `--gray` ; 1 saut par défaut,
  non mémorisé. → P2
- **X6** Cadre : bord `--hair`, hauteur 250, SVG pleine largeur. → P2
- **X7** Rendu : centre = forme de son type, contexte `localCenter` de §12.2
  (carré 28 comme A), contour de sélection toujours visible (côté + 12, trait
  2, `--blue`), libellé Space Mono 10,5 700 `--ink` ; voisins : contexte
  `local` (cercle 20, losange 25,5, triangle 22 comme A), libellé = dernier
  segment de l'id, Space Mono 10 `--ink`, id complet en `<title>` ; fantôme :
  contexte `local`, forme `ghost`. Libellés centrés sous la forme, ligne de
  base à `cy + size / 2 + 14` (centre : `+ 6 + 14`, sous le contour). Arêtes
  (§4.3) `--edge` 1,4, flèche 7 vers la cible ; **pleine** si la cible n'est
  pas plus proche du centre que la source (« links to »), **tiretée 4 3** si
  elle pointe vers le centre (« referenced by »). → P2
- **X8** Pied 12 `--gray` : « solid = links to · dashed = referenced by » ; à
  droite « Open in graph » 600 `--blue` sans soulignement. → P2
- **X9** Plafond et liste (hors maquette, exigés par §6 et A11) : « +N
  omitted » dans le cadre au-delà de 40 nœuds ; sous le cadre, « List · N
  neighbours » repliable, tous les voisins en lignes `.okf-row` (X10) :
  glyphe (contexte `icon`), id et relation. → P2
- **X10** « Referenced by · N » : compte écrit en C# ; lignes (classe
  partagée `.okf-row`, §12.6) : lien en ligne flexible, 13,5, sans
  soulignement, padding 6 0, filet bas `--hair`, `gap: 9px`, glyphe de type
  (contexte `icon`) posé par `okf-page.js`, puis l'id. → P1 (liste) → P1.1
  (compte, glyphes, dessin)
- **X11** Légende des formes sur une page de concept : les puces de type de
  l'explorateur (E4) ; chaque glyphe porte aussi un `<title>` au nom du type.
  → P1.1

### 11.5 Page `graph.html` (B, A17)

- **G1** En-tête §11.1, variante `graph` écrite par P1.1 (§12.3) :
  « Reading view » (H10), « Global graph » courant (H9), thème ; pas de
  « Filters » (facettes toujours visibles) ; bouton de palette (H7), la palette
  ouvrant la page du concept — écart : B n'a pas de bouton de palette, ajouté
  pour qu'elle soit la même partout. → P3 (page), P1.1 (en-tête)
- **G2** Facettes : panneau de 270, filet droit `--hair`, padding 18 18 0,
  sections espacées de 20, titres de section marge basse 10, défile seul. → P3
- **G3** **Type** : une ligne par entrée de `types`, ordre de l'index ;
  lignes espacées de 2, hauteur 32, `gap: 10px`, 13,5, case 16
  (`accent-color` `--blue`, marge 0), glyphe (contexte `icon`), nom
  (« (no type) »), compte Space Mono 11 `--gray` ; toutes cochées par défaut ;
  nom d'une case décochée en `--gray` ; les types de rang 5 sont listés un par
  un (forme « autre »). C'est la légende des types de la page. → P3
- **G4** **Trust** : `human-reviewed` (point), `machine-confirmed` (anneau),
  `unverified` (emplacement de glyphe vide de 10), glyphes du contexte `flag`,
  comptes ; cochés. → P3
- **G5** **Freshness** : une case « Stale only (as of now) », sablier
  (contexte `flag`, A24 ; écart : B dessine un triangle), compte des périmés
  maintenant ; décochée ; réévaluée aux instants de §4.4. → P3
- **G6** **Tags** : puces de E4 (rangée `gap: 6px`), « tag N », compte
  décroissant puis ordinal ; les 12 premières, puis « Show all tags (K) » ;
  pressées = OU ; aucune = pas de filtre. Non pressées en `--gray` comme E4 —
  écart : B les écrit en `--ink` ; un seul dessin pour la puce partagée. → P3
- **G7** **Display** : « Node labels » (cochée), « Dim unmatched instead of
  hiding » (décochée) ; hauteur 30. → P3
- **G8** Sémantique : ET entre facettes, OU dedans (A10) ; comptes = totaux
  du bundle, fixes ; en mode atténué, les non-retenus restent disposés et
  cliquables à opacité 0,25 et comptent dans le seuil. → P3
- **G9** Ligne d'état en haut à gauche (14, 18), Space Mono 12 `--gray`,
  `role="status"` : « showing X of Y concepts + Z absent · L of M links »
  (X retenus, Y total, Z fantômes visibles — segment omis si 0 —, L arêtes à
  extrémités visibles, M total) ; au-delà du seuil (§4.2) : « N concepts match
  — narrow the filters to draw the graph », la liste restant disponible. → P3
- **G10** Zoom en haut à droite (12, 18), `gap: 6px` : « + » et « − » 34 ×
  34, 18 (`aria-label` « Zoom in », « Zoom out ») ; « Fit » hauteur 34, padding
  0 12, 13 ; plus « List » (`aria-pressed`, hors maquette, §6) qui remplace le
  dessin par la liste équivalente. → P3
- **G11** Nœuds : contexte `graph` de §12.2 (cercle 26, carré 30, losange
  31,1, triangle 24 comme B ; anneau 26 trait 3, autre 26 trait 2, hors
  maquette) ; aucune marque de péremption sur un nœud (B n'en a pas ; le
  sablier est dans G5 et G17) ; libellé Space Mono 11,5 `--ink` = dernier
  segment de l'id (id complet en `<title>`), centré sous la forme, ligne de
  base à `cy + size / 2 + 16`, masqué si « Node labels » est décoché. → P3
- **G12** Arêtes `--edge` 1,3, flèche 7 `--edge` vers la cible ; deux arêtes
  opposées décalées de ± 3 perpendiculairement ; vers un fantôme : `--ghost`
  1,4, tiretée 5 4, flèche `--edge` comme B. → P3
- **G13** Fantôme : contexte `graph`, forme `ghost` (cercle de rayon 13 au
  trait comme B), fond `--white`, trait `--ghost` 1,6 tireté 3 3, libellé
  « absent: `<id>` » `--ghost` 11,5. → P3
- **G14** Sélection : contour carré `--blue` de côté + 12, trait 2,4 (§12.2) ;
  sortantes `--blue` 2,2 pleines, entrantes `--blue` 2,2 tiretées 5 4,
  flèches `--blue` 8 ; focus clavier : contour carré de côté + 20, tireté 3 3
  `--ink` 2, distinct de la sélection. → P3
- **G15** Légende en bas à gauche (16, 18), 12 `--gray`, `gap: 14px` : « Edge
  = body link (§6), as in okf graph · arrow points at the target », « Red
  dashed = broken link to an absent concept », « Blue = selection and its
  links », « Drag to pan · scroll to zoom ». → P3
- **G16** Interactions : glisser le fond = déplacer la vue ; molette = zoom
  autour du pointeur ; glisser un nœud = le déplacer ; clic = sélection ;
  double-clic ou Entrée = ouvrir la page. → P3
- **G17** Tiroir « Selected » : 340, filet gauche, padding 20, `gap: 16px` ;
  « SELECTED » ; id Space Mono 12 `--gray`, puis titre H2 (§11.0) à 6
  au-dessous ; puces (`.okf-chip`, C5) type, confiance sans vérificateur et,
  s'il y a lieu, péremption (« stale after » ou « stale since », sablier, A24 ;
  hors maquette) ; description de l'index (A18), 14 / 1,6 ; « Links to · N »
  (bloc à filet haut `--hair`, padding haut 12) et « Referenced by · N »
  (lignes `.okf-row` de X10, liens vers les pages ; un fantôme cité :
  « absent: `<id>` », sans lien) ; « Open page », lien hauteur 40, fond
  `--blue`, texte `--white` 14 600. Sans sélection : « Select a concept to see
  its links. » Fantôme sélectionné : « absent » et l'id, ni lien ni « Open
  page » (A10). → P3
- **G18** Fragment, historique, « Reading view » : §12.5. → P3
- **G19** Étroit (< 1 100) : facettes dans un `details` « Filters » au-dessus
  du dessin, tiroir sous le dessin, aucun défilement horizontal à 390. → P3

### 11.6 Palette « Jump to » (C)

- **J1** Fond `--backdrop` ; boîte à 120 du haut, largeur 600 (au plus la
  fenêtre moins 32), fond `--white`, bord 1 `--ink`, ombre `--shadow` (P1 :
  96, fond 0,4, sans ombre). → P1 → P1.1
- **J2** Rangée de saisie : hauteur 54, padding 0 16, `gap: 12px`, filet bas
  `--hair` : loupe 18 (`--gray`, trait 1,6), champ 17 sans bord, puis le bouton
  de fermeture dessiné en touche « Esc » (Space Mono 11 `--gray`, bord
  `--hair`, padding 1 6), nom accessible « Close » — second arrêt de
  tabulation de §8 (P1 : bouton « Close » sous la liste). → P1 → P1.1
- **J3** Ligne « Matches in title, id, tags · N » (titre de section, padding
  10 16 6), `aria-hidden="true"`, vide si la requête l'est ; la région
  `role="status"` de P1 garde son texte (« N matching concepts ») et devient
  visuellement masquée. → P1.1
- **J4** Liste : padding bas 8. Option : hauteur ≥ 46, padding 0 16,
  `gap: 12px`, filet gauche 3 transparent : glyphe de type (contexte `icon`),
  nom du type en texte visuellement masqué (§6), titre 15, id Space Mono 12
  `--gray` ; règles de coupure de P1 (R4) conservées. → P1 → P1.1
- **J5** Option active : fond `--blue-soft`, filet gauche 3 `--blue` (P1) ;
  titre `--blue` 600 (P1.1). → P1 → P1.1
- **J6** Pied : filet haut `--hair`, padding 10 16, 12 `--gray`, `gap: 16px` :
  « Up / Down to move », « Enter to open ». → P1.1
- **J7** « Tab to show it in the graph » : **écarté** (A29) — contredit §8
  (Tab reste dans le dialogue) ; la palette ouvre la page.
- **J8** Classement, clavier, modale, raccourcis, IME : inchangés. → P1

### 11.7 Comportements livrés par P1 et sa recette, conservés

- **L1** ≥ 1 100 : explorateur et panneau collants, chacun défile seul ;
  l'explorateur amène l'entrée courante dans sa vue sans faire défiler la page
  (R2). → P1
- **L2** < 1 100 : zones empilées, ordre visuel contenu → panneau →
  explorateur, DOM inchangé (R6). → P1
- **L3** 390 : aucun défilement horizontal (R1) ; un tableau du corps défile
  dans sa boîte. → P1
- **L4** `color-scheme` suit le thème (R5) ; thème posé avant le premier
  rendu. → P1
- **L5** Les maquettes sont des captures de 1 440 × 900 à zones défilant
  seules : la mise en page reste fluide ; la recette compare à 1 440 × 900. → P1
- **L6** À partir de 1 100 : trois colonnes **sans retour à la ligne**
  (`flex-wrap: nowrap` sur `.okf-layout`, explorateur 290 et panneau 340 en
  `flex: none`, `main` en `flex: 1 1 0` avec `min-width: 0`), alors que les
  bases de P1 (260 + 260 + `main` 560) faisaient passer le panneau sous la page
  entre 1 100 et 1 190 une fois E1 et X1 appliqués. La recette mesure à 1 100,
  1 190 et 1 440. → P1.1

### 11.8 Écartés et écarts approuvés

Écartés : J7 ; tout C hors palette (rail d'icônes, mise en page « Focus »,
bande « Related », puce « Frontmatter », fil d'Ariane dans l'en-tête) ;
l'abréviation « Attested » (E4) ; la troncature des valeurs de frontmatter
(C6).

**Écart approuvé par le propriétaire (A24) : le sablier remplace le triangle
de péremption des maquettes.** Il touche tous les endroits où A et B dessinent
la péremption : le drapeau des lignes de l'explorateur (E10), la légende de
l'explorateur (E12) et la facette « Freshness » (G5). Il s'applique aussi aux
éléments sans maquette : la puce de la colonne centrale (C5) et celle du
tiroir (G17). Aucun nœud de graphe, local ou global, ne porte de marque de
péremption (ni A ni B n'en dessinent). Le triangle reste la seule forme du
type de rang 3.

Autres écarts, motivés à leur ligne : E4 (glyphe dans la puce), E6 (deux
emplacements pour un nœud destination et dossier), E2 (titre qui n'est pas un
`<label>`), G1 (bouton de palette), G6 (couleur des puces non pressées).

## 12. Contrats entre tranches

Ce que les trois plans parallèles partagent. Une tranche qui a besoin de changer
un de ces contrats demande un changement de spec ; elle ne le change pas seule.

### 12.0 Propriété des fichiers et des méthodes

Une seule tranche écrit chaque fichier ou méthode ; les autres l'appellent ou
le lisent sans le modifier. Un **repère** est une ligne de commentaire posée
par P1.1 sous laquelle une seule autre tranche insère ses lignes ; deux
repères sont séparés par au moins une ligne que personne ne modifie, si bien que
deux tranches parallèles n'éditent jamais le même bloc. Aucun fichier n'est
créé vide d'avance, aucune page ne charge un script qui n'existe pas encore.

| Fichier ou méthode | Écrit par | Appelé ou lu, sans modification, par |
| --- | --- | --- |
| `SiteModel.cs`, `SiteIndex.cs`, `IndexScript.cs`, `ViewerIndex.cs`, `ViewerModel.cs` (index v2, `DisplayBody`, `BundleName`, `GraphPagePath`) | P1.1 | P3 |
| `ViewerAssets.cs` : lecture générique, interne, d'une ressource embarquée par son chemin (`Text`, `Bytes`) ; les propriétés publiques de P1 restent | P1.1 | P2 et P3 n'y ajoutent rien |
| `OKF4net.Viewer.csproj` : `LogicalName` qui garde le chemin relatif sous `Assets/` (séparateurs normalisés en `/` à la lecture), exclusion de `fonts/README.md` | P1.1 | — |
| `HtmlWriter.WriteAssets` : écrit sous `assets/` **toutes** les ressources embarquées, en ordre ordinal de leur chemin, puis `okf-index.js` (et `okf-fonts.css` si A26 l'exige) | P1.1 | P2, P3 : un fichier ajouté sous `Assets/` est embarqué et écrit sans autre changement |
| `HtmlWriter.Write` | P1.1 ; P3 insère, sous le repère `// P3: graph page (§12.5)`, la seule ligne qui écrit la page du graphe | — |
| `HtmlWriter.PageScripts` : table des scripts de fin de `<body>` des pages de concept et de l'index (§12.6) | P1.1 ; P2 insère `"okf-local.js"` sous le repère `// P2: local graph`, en fin de table | P3 ne la lit pas |
| `HtmlWriter.RenderDocumentStart(site, vue, titre, préfixe, idConcept?)` (du `<!doctype>` à la fin de l'en-tête, `<head>` compris), `RenderHeader(site, vue, préfixe, idConcept?)` pour les vues `page`, `index` **et** `graph`, `ScriptTag(préfixe, nom)`, `HtmlEscape`, `RootPrefix` | P1.1, tests xunit des trois vues compris | P3 (appel) |
| `HtmlWriter.RenderShell`, `RenderPage`, `RenderIndex`, tête de page, « Referenced by » | P1.1 | — |
| `HtmlWriter.GuardNoCaseCollisions`, amorcé avec `index.html` et le nom de la page du graphe | P1.1 | — |
| `HtmlWriter.RenderGraph`, sous le repère `// P3: RenderGraph (§12.5)` placé juste avant `HtmlEscape` | P3 (création) : la **seule** méthode qui écrit `graph.html` et y charge `okf-sim.js` et `okf-graph.js` | — |
| `Assets/okf-shapes.js`, `okf-page.js`, `fonts/` ; révisions d'`okf-site.js`, `okf-theme.js`, `okf-explorer.js`, `okf-palette.js`, `okf-toc.js` | P1.1 | P2, P3 (API de §12.2, `OkfSite`) |
| `Assets/okf-local.js` | P2 (création) | — |
| `Assets/okf-sim.js`, `Assets/okf-graph.js` | P3 (création) | — |
| `viewer.css` | chaque tranche dans sa section (§12.6) | — |
| `run.js` (helpers, chargeur des cas, cas de P1 et de P1.1), `check-index.js`, README du harnais | P1.1 | P2, P3 (helpers reçus par `register(h)`, §12.7) |
| `cases/p2.js` ; `cases/p3.js` | P2 ; P3 | — |
| `fixtures/hostile-bundle/p11-*` ; `p2-*` ; `p3-*` | P1.1 ; P2 ; P3 | — |
| `ACCEPTANCE.md` (sections et lignes de remplacement), `recette/recette.js`, `lib.js`, `p1.js`, `p1-1.js` | P1.1 | P2, P3 |
| section `## P2` d'`ACCEPTANCE.md` et `recette/p2.js` ; section `## P3` et `recette/p3.js` | P2 ; P3 | — |
| tests xunit | chaque tranche dans ses classes (§7) | — |
| `ci.yml` (smoke test AOT) | P1.1 (assets, polices), puis P3 (`graph.html`) après la fusion de P1.1 | — |
| `CLAUDE.md`, README du viewer, `NOTICE` | P1.1, puis P3 après la fusion de P1.1 (§12.8) | — |
| `CHANGELOG.md` | P1.1 écrit l'entrée entière, une ligne par tranche ; un écart de livraison de P2 ou P3 se corrige avant de sortir la PR #176 du brouillon | — |

### 12.1 Schéma de l'index v2 (propriétaire : P1.1)

Les clés de P1 et leur ordre ne changent pas ; les nouvelles s'**ajoutent à la
fin** de leur objet :

```js
window.OKF_INDEX = {"version":2,
  "concepts":[{"id":"…","title":"…","type":"…","tags":["…"],"path":"…",
    "trust":"…","staleAfterMs":1767225600000,"staleAfterDate":"2026-01-01",
    "typeIndex":0,"description":"…"}],
  "ghosts":[{"id":"…"}],
  "edges":[[0,3,1,0]],
  "tree":[{"name":"…","concept":-1,"children":[]}],
  "types":[{"name":"Metric","count":3,"slot":0}]};
```

- `version` passe à 2 ; `OkfSite.readIndex` exige `version === 2` et un tableau
  `types` ; `check-index.js` vérifie les nouveaux champs (bornes de `typeIndex`
  et `slot`, rangs 0 à 4 uniques, longueur de `description`).
- `types` : une entrée par valeur distincte de `type` (ordinal ; `""` compris
  quand un concept n'a pas de type), triée par `count` décroissant puis
  `string.CompareOrdinal` du nom. `slot` : 0 à 4 pour les cinq premiers noms
  **non vides** dans cet ordre, 5 pour tous les autres et pour `""`. Libellé
  affiché de `""` : « (no type) » (`OkfShapes.typeLabel`).
- `typeIndex` : position du type du concept dans `types`.
- `description` : `Frontmatter.Description` ou `""` ; suites d'espaces
  (`char.IsWhiteSpace`) fusionnées en une espace, extrémités retirées ; au-delà
  de 200 points de code (120 si la mesure de §3.6 dépasse 600 000 octets,
  A23 ; une paire de substitution valide compte un, un demi isolé compte un et
  est gardé tel quel), on garde les 199 (resp. 119) premiers, on retire
  les espaces finales et on ajoute « … » (U+2026) — jamais de coupure entre les
  deux moitiés d'une paire. Sérialisée par `HtmlSafeJson.Quote`.
- Côté C# : `IndexConcept` gagne `TypeIndex` et `Description`, `ViewerIndex`
  gagne `Types` (`IndexType(string Name, int Count, int Slot)`),
  `ViewerIndex.Empty` suit.
- Sur `acme_retail` réel, la règle donne Metric → cercle, Attested
  Computation → carré, Policy → losange, BigQuery Table → triangle, Skill →
  anneau (la maquette inverse les deux derniers : ses données sont
  illustratives ; la règle s'applique, A28).

### 12.2 Formes : `assets/okf-shapes.js` (propriétaire : P1.1)

```js
window.OkfShapes = Object.freeze({
  KINDS,                    // Object.freeze(["circle","square","diamond","triangle","ring","other"]) ; KINDS[rang]
  OTHER_SLOT,               // 5
  BOXES,                    // { icon: 12, chip: 10, flag: 10 } : côté du <svg> d'une icône HTML
  SIZES,                    // table gelée ci-dessous : SIZES[contexte][forme] → { size, stroke, dash, ring, focus }
  typeLabel(name),          // name === "" ? "(no type)" : name
  slotOf(index, position),  // rang 0..5 du concept ; 5 si typeIndex ou slot hors bornes
  kindOf(index, position),  // KINDS[slotOf(index, position)]
  trustKind(trust),         // "human-reviewed" → "human", "machine-confirmed" → "machine", sinon null
  shape(kind, cx, cy, size, options),  // un seul élément : <circle>, <rect> ou <path>
  icon(kind, context, title),          // <svg class="okf-glyph"> autonome, pour le HTML ; title facultatif
  node(kind, cx, cy, size, options),   // <g class="okf-node"> pour le <svg> d'un graphe
  typeLegendEntries(index), // [{ role: "type", slot, label, count }] : rangs 0–4 dans l'ordre de types,
                            // puis { slot: 5, label: "Other types", count: cumul } s'il y a lieu
  legend(entries),          // <ul class="okf-legend"> ; entrée : { role: "type", slot, label, count? }
                            // | { role: "trust", trust } | { role: "stale" } | { role: "ghost" }
});
```

- **Formes** (`kind`) : les six de `KINDS` (types), plus `human`, `machine`
  (confiance), `stale` (sablier, A24) et `ghost` (fantôme). Toute autre valeur :
  `TypeError`. Le module crée ses éléments dans le `document` de la fenêtre qui
  l'a chargé (pas de paramètre `doc`).
- **`size`** = côté, en unités utilisateur (px à l'échelle 1), du carré centré
  en (`cx`, `cy`) dans lequel s'inscrit la boîte englobante de la forme : sa
  plus grande dimension vaut `size` ; pour une forme à trait, le **bord
  extérieur** du trait est sur ce carré. `options` : `{ stroke, dash, ring,
  focus }`, nombres finis ≥ 0 (`dash` : paire ou `null`). Les appelants ne
  passent **que** des valeurs de la table : `var s =
  OkfShapes.SIZES[contexte][forme]; OkfShapes.node(forme, x, y, s.size, s)`.
  `SIZES[contexte][forme]` absent : `TypeError`.
- **Géométrie unique**, écrite en coordonnées absolues (aucun `transform`),
  avec `h = size / 2` et `w = stroke` :

  | Forme | Élément | Attributs |
  | --- | --- | --- |
  | `circle`, `human` | `circle` | `cx`, `cy`, `r` = h |
  | `square` | `rect` | `x` = cx − h, `y` = cy − h, `width` = `height` = size |
  | `diamond` | `path` | `d` = `M cx cy−h L cx+h cy L cx cy+h L cx−h cy Z` |
  | `triangle` | `path` | `d` = `M cx cy−h L cx+h cy+h L cx−h cy+h Z` |
  | `ring`, `machine` | `circle` | `r` = (size − w) / 2, `stroke-width` = w |
  | `other` | `rect` | `x` = cx − (size − w) / 2, idem `y`, `width` = `height` = size − w, `stroke-width` = w |
  | `stale` (sablier) | `path` | a = 0,45 × size : `d` = `M cx−a cy−h L cx+a cy−h L cx cy L cx+a cy+h L cx−a cy+h L cx cy Z` — une seule `path`, deux triangles joints pointe à pointe, 0,9 × size de large |
  | `ghost` | `circle` | `r` = (size − w) / 2, `stroke-width` = w, `stroke-dasharray` = dash |

  Chaque nombre est arrondi au centième (`Math.round(v * 100) / 100`) et écrit
  par `String` ; dans `d`, une commande est suivie sans espace de son premier
  nombre, les nombres et commandes sont séparés par une espace
  (`M6 0.35 L11.65 6 …`).
- **Table `SIZES`** — `size` (puis `stroke`, `dash` s'il y a lieu). Relevé dans
  les maquettes : `icon` = glyphes des lignes de l'explorateur, de la palette,
  des listes et des facettes (A, B, C) ; `chip` = puce de type (carré 8 de A) ;
  `flag` = drapeaux et légende de l'explorateur, facettes Trust et Freshness ;
  `local` = voisins du graphe local (A) ; `localCenter` = son centre (carré 28
  de A) ; `graph` = nœuds de B. Les cases en *italique* ne figurent dans aucune
  maquette (choix de rédaction, §13).

  | Contexte (boîte) | circle | square | diamond | triangle | ring | other | human | machine | stale | ghost |
  | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
  | `icon` (12) | 10 | 9 | 11,3 | 11 | 10 / 2 | *9 / 1,5* | — | — | — | *10 / 1,2, 2 2* |
  | `chip` (10) | *8* | 8 | *9* | *9* | *8 / 1,5* | *8 / 1,5* | — | — | — | — |
  | `flag` (10) | — | — | — | — | — | — | 8 | 8 / 2 | 10 | — |
  | `local` | 20 | *23* | 25,5 | 22 | *20 / 2,5* | *20 / 1,5* | — | — | — | *21,2 / 1,4, 3 3* |
  | `localCenter`, `ring` 2 | *26* | 28 | *31,1* | *24* | *26 / 3* | *26 / 2* | — | — | — | — |
  | `graph`, `ring` 2,4, `focus` 2 | 26 | 30 | 31,1 | 24 | *26 / 3* | *26 / 2* | — | — | — | 27,6 / 1,6, 3 3 |

  Lecture d'une case : `size`, puis `/ stroke`, puis `dash` ; une case sans
  trait a `stroke` 0 et `dash` `null` ; `ring` et `focus` d'une ligne valent
  pour toutes ses formes, 0 dans les autres lignes. Virgule décimale du texte,
  point dans le code (`11.3`). Écarts de mesure
  assumés : le triangle de l'explorateur fait 11 × 11
  (maquette 11 × 10) ; le losange de A et B est un carré tourné de 8, 18 et 22,
  soit les diagonales 11,3, 25,5 et 31,1 ; le fantôme de B (cercle de rayon 13,
  trait 1,6 centré) a son bord extérieur à 13,8, d'où 27,6.
- **Exemples travaillés** (à reproduire tels quels par le contrôle 10) :
  `icon` en (6, 6) — losange `M6 0.35 L11.65 6 L6 11.65 L0.35 6 Z`, triangle
  `M6 0.5 L11.5 11.5 L0.5 11.5 Z`, autre `x="2.25" y="2.25" width="7.5"
  height="7.5" stroke-width="1.5"`, anneau `r="4" stroke-width="2"` ; `flag`
  en (5, 5) — sablier `M0.5 0 L9.5 0 L5 5 L9.5 10 L0.5 10 L5 5 Z`, machine
  `r="3" stroke-width="2"` ; `graph` en (100, 100) — carré `x="85" y="85"
  width="30" height="30"`, losange `M100 84.45 L115.55 100 L100 115.55
  L84.45 100 Z`, triangle `M100 88 L112 112 L88 112 Z`, fantôme `r="13"
  stroke-width="1.6" stroke-dasharray="3 3"`.
- **`icon(kind, context, title)`** : `<svg class="okf-glyph" width="B"
  height="B" viewBox="0 0 B B" aria-hidden="true" focusable="false">`, B =
  `BOXES[context]`, forme centrée en (B/2, B/2) aux valeurs de
  `SIZES[context][kind]` ; `title` (texte) → premier enfant `<title>`. Formes de
  type et `ghost` en contexte `icon` (ou `chip` dans une puce de type) ;
  `human`, `machine`, `stale` en contexte `flag`, y compris dans les puces.
- **`node(kind, cx, cy, size, options)`** : `<g class="okf-node">` contenant,
  dans l'ordre, si `options.focus` > 0 un `rect.okf-node-focus` (carré de côté
  size + 20, trait `focus`, `stroke-dasharray` 3 3), si `options.ring` > 0 un
  `rect.okf-node-ring` (carré de côté size + 12, trait `ring`), puis la forme.
  Ces contours ne sont visibles que si le `<g>` porte `okf-focused` ou
  `okf-selected`, classes fixes que l'appelant bascule par `classList`. `node`
  ne pose **ni `<title>` ni `<text>`** : l'appelant insère `<title>` (id
  complet) en premier enfant et ajoute le libellé (X7, G11, G13).
- **Vocabulaire fixe**, pour `OkfShapes` comme pour P2 et P3 — tout élément SVG
  par `createElementNS("http://www.w3.org/2000/svg", nom)` ; éléments : `svg`,
  `g`, `circle`, `rect`, `path`, `line`, `text`, `title`, `defs`, `marker` ;
  attributs : `viewBox`, `width`, `height`, `class`, `aria-hidden`,
  `focusable`, `role`, `aria-label`, `tabindex`, `id` (valeurs fixes `okf-…`
  seulement), `cx`, `cy`, `r`, `x`, `y`, `x1`, `y1`, `x2`, `y2`, `d`,
  `stroke-width`, `stroke-dasharray`, `text-anchor`, `dominant-baseline`,
  `marker-end`, `refX`, `refY`, `markerWidth`, `markerHeight`, `orient`,
  `transform` (seulement `translate(a b) scale(s)`, pour le pan/zoom). Aucun
  autre nom.
- `d`, `transform`, `stroke-width`, `stroke-dasharray` et toute coordonnée sont
  construits à partir de nombres vérifiés par `Number.isFinite` ; sinon
  `TypeError` (bogue de l'appelant). Aucun texte du bundle ne devient un nom de
  classe, un `id` ou un attribut autre que `aria-label` et le texte de
  `<title>`/`<text>` (`textContent`).
- **Couleurs** : jamais en attribut. Classes d'une table fixe sur l'élément de
  forme : `okf-shape-0` à `okf-shape-5`, `okf-trust-human`,
  `okf-trust-machine`, `okf-stale-mark`, `okf-ghost-mark` ; règles CSS de P1.1
  sous un ancêtre `svg` qui lisent les jetons de §11.0 (un changement de thème
  ne redessine rien) : rangs 0 à 3, `human`, `stale` remplis
  (`--okf-type-N`, `--blue`, `--stale`) ; `ring`, `other`, `machine` en trait
  sans remplissage (`--okf-type-4`, `--okf-type-5`, `--blue`) ; `ghost`
  rempli `--white`, trait `--ghost` ; `.okf-node-ring` trait `--blue`,
  `.okf-node-focus` trait `--ink`, sans remplissage ; dans `.okf-chip-type`,
  la forme est `--white` (remplissage ou trait selon la forme). Un `svg` ne
  peut pas apparaître dans `okf-body` (le sanitizer l'exclut, §4.5) : ces
  règles sont ancrées par construction. P2 et P3 peuvent définir leurs propres
  classes fixes préfixées `okf-local-` et `okf-graph-` (arêtes, libellés).
- **Légende** : `legend(entries)` rend une `<li>` par entrée, glyphe puis
  texte (`gap: 8px`), selon `role` (distinct de la forme `kind`) : `type` →
  `icon` + libellé (+ compte en Space Mono 11
  `--gray`) ; `trust` → `flag` + nom du palier (emplacement vide de 10 pour
  `unverified`) ; `stale` → sablier `flag` + « stale (now ≥ `stale_after`) »
  (`stale_after` en Space Mono) ; `ghost` → `icon` + « absent concept ».
- Utilisateurs : explorateur (E4, E6, E10, E12), palette (J4), puces et listes
  (`okf-page.js` : C5, X10), graphe local (P2 : X7, X9), page graphe (P3 : G3
  à G5, G11 à G14, G17). Aucun ne dessine une forme de type, de confiance, de
  péremption ou de fantôme sans ce module.

### 12.3 Coque et en-tête (propriétaire : P1.1)

```html
<html lang="en" data-okf-root="../" data-okf-view="page" data-okf-concept="a/b">
<!-- data-okf-view : "page" | "index" | "graph" ; data-okf-concept seulement sur une page de concept -->
<head> … okf-theme.js, viewer.css [, okf-fonts.css si A26 l'exige] … </head>
<body>
<a class="okf-skip" href="#okf-main">Skip to content</a>
<div class="topline"></div>
<header class="bar"><div class="bar-in">
  <a class="wordmark" href="../index.html">OKF4net<sup>§</sup></a>
  <span class="bar-sep" aria-hidden="true"></span>
  <span class="bar-bundle" id="okf-bundle-name" title="acme_retail">acme_retail</span>
  <span class="bar-counts" id="okf-bundle-counts">9 concepts · 14 links</span>
  <div class="bar-tools" id="okf-tools">
    <!-- okf-palette.js insère #okf-palette-open en premier -->
    <!-- okf-explorer.js insère #okf-filters-toggle juste avant #okf-global-graph (vues page et index) -->
    <!-- vue graph seulement : <a class="okf-tool" id="okf-reading-view" href="index.html">Reading view</a> -->
    <a class="okf-tool okf-tool-graph" id="okf-global-graph" href="../graph.html#a/b">Global graph</a>
    <!-- vue graph : href="graph.html" aria-current="page", sans fragment -->
    <!-- okf-theme.js ajoute #okf-theme-toggle en dernier -->
  </div>
</div></header>
<div class="okf-layout"> … <main id="okf-main"> <div class="okf-page-head">…</div> <div id="okf-body"></div> </main> …
```

- **Écriture** : `RenderDocumentStart` et `RenderHeader` (P1.1, §12.0)
  produisent tout ce qui précède `.okf-layout` pour les **trois** vues ; P3 les
  appelle pour `graph.html` (vue `graph`, préfixe `""`) sans les modifier. Vue
  `graph` : `#okf-reading-view` avant `#okf-global-graph`, celui-ci sans
  fragment et avec `aria-current="page"` ; pas de « Filters » (le script qui
  l'insère n'est pas chargé). Tests xunit des trois vues livrés par P1.1.
- Tout attribut écrit en C# est entre **guillemets doubles**, sa valeur passée
  par `HtmlEscape` (qui échappe `& < > "`, pas `'`).
- **Nom du bundle** : `ViewerSite.BundleName` (`string?`) ; `SiteModel` le
  remplit avec
  `Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundle.Root)))`,
  « bundle » si vide ; `null` (site construit à la main) : même calcul sur
  `BundleRoot`. Échappé par `HtmlEscape` ; aussi en attribut `title` (H5).
- **Comptes** : depuis `site.Index` (H6).
- **Page du graphe** (A25) : `ViewerSite.GraphPagePath` (`string?`) ;
  `SiteModel` le remplit, `null` (site construit à la main) vaut le même calcul
  à l'écriture — une seule fonction, `SiteModel.FreeGraphPagePath(pages)` :
  le premier de `graph.html`, `graph-1.html`, `graph-2.html`… qui n'est égal,
  à la casse près, ni au chemin d'une page, ni à `index.html`, ni au **premier
  segment** du chemin d'une page (un dossier : concept `graph.html/x`). Jamais
  de refus de rendu pour cette raison. Une valeur explicite doit être un seul
  segment `[A-Za-z0-9._-]+\.html`, sinon `ArgumentException` ;
  `GuardNoCaseCollisions` l'ajoute à son ensemble et refuse une valeur
  explicite qui entre en collision (erreur de l'hôte, jamais du `SiteModel`).
  Testé en xunit (racine `graph`, `Graph`, dossier `graph.html/`, suite
  `graph-1`).
- **Lien « Global graph »** : `href` = préfixe de racine + `GraphPagePath` +
  `#` + id sur une page de concept (les caractères d'un `ConceptId` sont tous
  admis dans un fragment) ; sans fragment sur l'index ; sur la vue `graph`,
  `GraphPagePath` seul et `aria-current="page"`. Écrit en C#. C'est **la seule
  source du nom réel de la page du graphe** : aucun script n'écrit
  `graph.html` en dur. P2 reprend ce `href` tel quel (« Open in graph », avec
  son fragment) ; P3 et le harnais le lisent fragment retiré.
- **Tête de page** (`.okf-page-head`, premier enfant de `main`, pages de
  concept) : `nav.okf-crumbs` (C2), `h1`, `div.okf-chips` (C5) — avec les
  emplacements vides `span.okf-chip-glyph` portant `data-okf-slot="0…5"`,
  `data-okf-trust="human|machine"` ou `data-okf-stale` —, `section.okf-fm`
  (C6, cellules repliables marquées `data-okf-extra`). `okf-page.js` lit ces
  attributs, à valeurs fixes écrites par le C#, jamais un texte du bundle, et
  seulement sous `body > .okf-layout > main > .okf-page-head` (§12.6).
- **« Referenced by »** : `h2#okf-backlinks-title` = « Referenced by » +
  `span.okf-count` « · N » ; chaque lien porte `data-okf-target="<id>"` pour que
  `okf-page.js` retrouve son type dans l'index.
- **Valeurs par défaut d'un site construit à la main** : `DisplayBody` `null`
  → `Body` (C4) ; `BundleName` et `GraphPagePath` `null` → calculés comme
  ci-dessus ; `Index` `ViewerIndex.Empty` → comptes omis (H6), explorateur,
  palette et puces sans données (comportement de P1).

### 12.4 Graphe local (propriétaire : P2)

Créé par `okf-local.js` dans `#okf-context`, **juste après `#okf-toc`** et avant
`.okf-backlinks` ; caché s'il n'y a aucun voisin ; il révèle `#okf-context` si
celui-ci était caché.

```html
<section class="okf-local" id="okf-local-graph" aria-labelledby="okf-local-title">
  <div class="okf-local-head">
    <h2 id="okf-local-title" class="okf-section-title">Neighbourhood</h2>
    <div class="okf-hops" role="group" aria-label="Neighbourhood depth">
      <button type="button" id="okf-local-hops-1" aria-pressed="true">1 hop</button>
      <button type="button" id="okf-local-hops-2" aria-pressed="false">2 hops</button>
    </div>
  </div>
  <div class="okf-local-canvas"><svg role="img" aria-label="…">…</svg></div>
  <p class="okf-local-foot"><span>solid = links to · dashed = referenced by</span>
    <a id="okf-local-open" href="…">Open in graph</a></p>
  <details class="okf-local-list"><summary>List · N neighbours</summary><ul>…</ul></details>
</section>
```

- Données : l'index seul (arêtes sortantes et entrantes, fantômes), structures
  construites à la demande (§3.6). Ordre angulaire = position dans l'index.
- Arêtes : §4.3 (toutes celles entre nœuds montrés, opposées décalées de
  ± 3) ; formes : `OkfShapes.node` aux contextes `local` et `localCenter`
  (§12.2), centre marqué `okf-selected` ; libellés : X7.
- Marqueurs de flèche : `id` fixes `okf-local-arrow`, `okf-local-arrow-in`.
- « Open in graph » : `href` de `#okf-global-graph` repris **tel quel**,
  fragment compris ; lien omis s'il manque.
- Clic sur un nœud concept : navigation par le résolveur (§3.5) ; fantôme :
  rien.
- Toute requête DOM part de `#okf-context` (§12.6) ; rien sur une page qui
  n'a pas `data-okf-view="page"`.

### 12.5 `graph.html` (propriétaire : P3)

- Écrite par `HtmlWriter.RenderGraph(site)` (P3, seule méthode qui l'écrit,
  §12.0) au chemin de la page du graphe (§12.3), racine, préfixe `""` :
  `RenderDocumentStart(site, graph, "Global graph", "", null)` (P1.1, appelé
  sans modification), puis le squelette ci-dessous, puis ses propres balises
  `ScriptTag` — `okf-index.js`, `okf-site.js`, `okf-shapes.js`,
  `okf-palette.js`, `okf-sim.js`, `okf-graph.js` ; sans payload, sans
  `marked`, `viewer.js`, `okf-toc.js`, `okf-explorer.js`, `okf-page.js`,
  `okf-local.js`.
- Squelette :

```html
<div class="okf-graph-layout" id="okf-graph-layout">
  <aside class="okf-facets" id="okf-facets" aria-label="Facets"></aside>
  <main id="okf-main" class="okf-graph-main" aria-label="Global graph">
    <p class="okf-graph-status" id="okf-graph-status" role="status"></p>
    <div class="okf-graph-zoom" id="okf-graph-zoom"></div>
    <div class="okf-graph-canvas" id="okf-graph-canvas"></div>
    <section class="okf-graph-list" id="okf-graph-list" aria-label="Concepts and links" hidden></section>
    <p class="okf-graph-legend" id="okf-graph-legend"></p>
  </main>
  <aside class="okf-graph-detail" id="okf-graph-detail" aria-label="Selected concept"></aside>
</div>
<noscript><p>The graph needs JavaScript. <a href="index.html">Bundle index</a></p></noscript>
```

- **Fragment** : `location.hash` sans `#`, `decodeURIComponent` (en cas
  d'échec, la valeur brute), recherche **exacte** dans une `Map` id → position
  des concepts (jamais des fantômes). Trouvé : facettes remises à leur défaut
  (même si le concept n'était masqué que par elles), nœud sélectionné et
  centré. Introuvable ou vide : aucune sélection, vue « Fit ». Au-delà de
  `NODE_LIMIT` nœuds visibles, rien n'est dessiné : la sélection par fragment
  remplit le tiroir et met en évidence l'entrée de la liste équivalente.
- **Historique** : une sélection met l'URL à jour par
  `history.replaceState(null, "", "#" + id)` (désélection : URL sans
  fragment), sous `try/catch` : si `replaceState` lève (origine `null` d'un
  document `file://`), `location.replace("#" + id)` (désélection :
  `location.replace("#")`), le `hashchange` qu'il déclenche étant ignoré par
  un drapeau ; jamais de nouvelle entrée. Un `hashchange` venu du lecteur
  sélectionne l'id reçu, selon la règle du fragment. Sélectionner un
  **fantôme** retire le fragment (un fantôme n'est jamais sélectionné par
  fragment, l'aller-retour resterait faux). Précédent et Suivant restent ceux
  du navigateur entre pages (A17). La recette vérifie en `file://`, dans
  Chrome, Edge et Firefox, que la sélection met l'URL à jour sans changer
  `history.length`.
- **« Reading view »** (`#okf-reading-view`, écrit par P1.1) : `okf-graph.js`
  met son `href` à résolveur(chemin du concept sélectionné), ou `index.html`
  sans sélection.
- Toute requête DOM d'`okf-graph.js` part de `body > .okf-graph-layout` ou de
  `#okf-tools` (§12.6).
- **Simulation** `assets/okf-sim.js`, pure (aucun DOM, aucune horloge, ni
  `Math.random`, seulement les primitives de §4.2, pas d'opérateur `**`,
  aucun global hors le sien), contraintes de §4.2 :

```js
window.OkfSim = Object.freeze({
  NODE_LIMIT,               // 1500 (§4.2, à calibrer par P3 ; consigné ici)
  create(graph, options),   // → Simulation, ou null si graph.nodeCount > NODE_LIMIT
  // graph   : { nodeCount, edges }  nœuds 0..nodeCount-1 = nœuds visibles dans l'ordre de
  //           l'index (concepts par position, puis fantômes par position) ; edges : tableau
  //           de paires [source, cible] d'entiers dans ces bornes, fusionnées, sans boucle
  // options : { maxIterations, sliceWork, cellCap } entiers > 0 (défauts fixés par P3)
});
// Simulation :
//   step()      → { done, iterations, work }  au plus options.sliceWork unités de travail
//                 (paire candidate visitée, interaction de ressort, intégration d'un nœud) ;
//                 reprend au milieu d'une itération (curseurs et accumulateurs conservés)
//   positions() → Float64Array [x0, y0, x1, y1, …], copie, état de la dernière itération complète
//   stats()     → { iterations, totalWork, maxSliceWork }
//   cancel()    → ensuite step() rend { done: true, … } sans travail
```

- **Disposition initiale** sans trigonométrie : grille carrée en ordre
  d'index (côté `Math.ceil(Math.sqrt(n))`, ligne et colonne par
  `Math.floor` et `%`), décalée par un générateur congruentiel **32 bits** à
  état entier : `x = (Math.imul(x, 1664525) + 1013904223) >>> 0`, graine
  dérivée de `nodeCount` et du nombre d'arêtes (§4.2), toutes opérations
  exactes. `okf-sim.js` ne dépend de rien : P3 peut le livrer en premier (§9).
- **Ordonnanceur** : `okf-graph.js` lit une fois au démarrage
  `window.OKF_SCHEDULER` si `typeof window.OKF_SCHEDULER === "function"`
  (fonction `(callback) => void`, injectée par le harnais, §7), sinon
  `requestAnimationFrame`. Un changement de filtre annule la simulation en
  cours (`cancel()` et jeton de génération).

### 12.6 Chargement, assets, CSS (plomberie : P1.1)

- **Pages de concept et index** — `<head>` : `okf-theme.js`, `viewer.css`
  (puis `okf-fonts.css` si A26 l'exige) ; fin de `<body>` : `#okf-payload`,
  puis la table `PageScripts` : `marked.min.js`, `viewer.js`, `okf-index.js`,
  `okf-site.js`, `okf-shapes.js`, `okf-explorer.js`, `okf-palette.js`,
  `okf-toc.js`, `okf-page.js`, et, ajouté par P2 à son repère,
  `okf-local.js`.
- **`graph.html`** (P3, `RenderGraph`) — `<head>` : idem, par
  `RenderDocumentStart` ; fin de `<body>` : `okf-index.js`, `okf-site.js`,
  `okf-shapes.js`, `okf-palette.js`, `okf-sim.js`, `okf-graph.js`.
- **Assets écrits** par `HtmlWriter.WriteAssets` (§12.0) : toutes les
  ressources embarquées d'`Assets/`, en ordre ordinal de leur chemin, puis
  `okf-index.js` ; puis `index.html`, la page du graphe (P3), les pages. Un
  script ajouté par P2 ou P3 sous `Assets/` est donc embarqué et écrit sans
  toucher `ViewerAssets` ni `HtmlWriter` ; il n'est **chargé** que par la
  balise que sa tranche pose (P2 : `PageScripts` ; P3 : `RenderGraph`).
- Chaque script : son IIFE, vérifie ses globaux (`OkfSite`, `OkfShapes`, index)
  et rend la main sans erreur s'ils manquent ; aucune dépendance au DOM d'un
  autre script hors des contrats de §12.
- **CSS** : un seul `viewer.css`, en sections dans cet ordre : `@font-face`
  (P1.1) ; jetons et base (P1, révisés par P1.1) ; chrome P1/P1.1, composants
  partagés compris ; `/* === P2: local graph === */` ;
  `/* === P3: graph page === */`. P1.1 crée ces deux dernières, chacune avec
  sous son titre une ligne de remplacement (`/* (P2 rules) */`,
  `/* (P3 rules) */`) que la tranche remplace, une ligne vide les séparant ;
  chaque tranche n'écrit que dans la sienne ; P2 et P3 n'ajoutent pas de jeton
  (§11.0 les fournit tous).
- **Composants partagés** (P1.1) : titre de section `.okf-section-title`
  (§11.0) ; puces `.okf-chip` et variantes `.okf-chip-type` (fond `--ink`,
  glyphe `--white`), `.okf-chip-trust`, `.okf-chip-status`,
  `.okf-chip-stale` (C5), puces bascules `button.okf-chip[aria-pressed]` (E4,
  G6) ; lignes de liste `.okf-row` (X10) ; légende `.okf-legend` (§12.2) ;
  formes SVG (§12.2) ; texte visuellement masqué `.okf-sr` (P1, ancrage
  étendu). Leurs règles sont écrites une fois, ancrées à **tous**
  leurs conteneurs, présents et futurs : `#okf-explorer`, `#okf-context` (y
  compris `.okf-local`), `body > .okf-layout > main > .okf-page-head`,
  `body > .okf-graph-layout` (par exemple `:is(#okf-explorer, #okf-context,
  body > .okf-layout > main > .okf-page-head, body > .okf-graph-layout)
  .okf-chip`). P2 et P3 emploient ces classes sans réécrire leurs règles.
- **Ancrage, propriété de sécurité** (revue finale de P1) : le sanitizer garde
  `class` sur `<code>` (et rien d'autre : ni `id`, ni `name`, ni `data-*`),
  donc le corps peut porter n'importe quelle classe de chrome, jamais un
  attribut `data-okf-*`. **CSS** : tout sélecteur de chrome est ancré à un
  conteneur de chrome : `#okf-tools`, `#okf-explorer`, `#okf-context`,
  `body > .okf-palette-backdrop`, `body > .okf-layout > main > .okf-page-head`,
  `body > .okf-layout > main > :is(.meta, .errors)` (index, C8),
  `body > header.bar`, `body > .okf-skip`, `body > .okf-graph-layout`, ou un
  ancêtre `svg`. P1.1 ancre aussi les règles héritées qui ne l'étaient pas
  (`.topline`, `.bar-in`, `.wordmark`, `.meta`, `.errors`) ;
  `table.frontmatter` disparaît avec C6. **JS** : tout `querySelector` /
  `querySelectorAll` d'`okf-page.js`, `okf-local.js` et `okf-graph.js` part
  d'un conteneur de chrome obtenu par `getElementById` ou par un sélecteur
  `body > …`, jamais de `document` ; une sélection par classe ne retient que
  les éléments qui portent aussi l'attribut `data-okf-*` attendu.
- **Classes de chrome du harnais** : chaque tranche liste ses classes de chrome
  dans **son** fichier, `fixtures/hostile-bundle/p11-chrome-classes.md`,
  `p2-chrome-classes.md`, `p3-chrome-classes.md`, et ajoute une sonde qui
  vérifie que son vrai chrome garde son style ; le cas existant (P1.1 le
  généralise) parcourt toutes les pages `*chrome-classes.html` et compare les
  styles calculés du contenu portant ces classes, `@media` dépliés compris. Le
  fichier `chrome-classes.md` de P1 reste celui de P1.

### 12.7 Harnais (`tools/viewer-security-check/run.js`)

- **`run.js` appartient à P1.1** ; P2 et P3 n'y écrivent jamais. Leurs cas
  vivent dans `tools/viewer-security-check/cases/p2.js` et `cases/p3.js`, qui
  exportent `register(h)` ; `h` est l'objet gelé des helpers. Le chargeur,
  **première tâche du plan P1.1**, appelle `register(h)` pour chaque
  `cases/*.js` présent, en ordre ordinal du nom, avant le décompte final ; un
  fichier absent n'est pas une erreur. `cases/p3.js` peut donc exister avant le
  chargeur (il n'est alors simplement pas exécuté). Contrôles synchrones d'un
  module pur (`OkfSim`) par `h.check`, asynchrones par `h.checkAsync`.
- Helpers figés (signatures inchangées, passés dans `h`) : `check`, `assert`,
  `checkAsync`, `okfSite`, `siteResources`, `openPage` (ses options `now`,
  `storage`, `storedTheme`, `hash`, `mount`, `blocked`, `override`,
  `beforeParse`), `navigations`, `key`, `type`, `unwrapMedia`, `isShown`,
  `paletteOptions`, et `treeLink(window, id)`, que P1.1 réécrit pour trouver la
  ligne par `data-okf-id` (E8, A22) au lieu de `title === id`. P1.1 ajoute
  `okfShapes()` (fenêtre nue chargeant `okf-site.js` et `okf-shapes.js`).
- P1.1 met à jour les cas de P1 que son changement rend caducs : nom
  accessible du filtre (« Filter the explorer » devient « Filter by name »,
  E2), index de remplacement en `version: 1` (passé à 2, §12.1),
  `main table.frontmatter td` (C6), recherche des lignes par `title` (E8),
  classe `.okf-stale` (drapeau de E10, servi par `OkfShapes`), bouton
  « Close » devenu touche « Esc » de même nom accessible (J2).
- `graph.html` s'ouvre par `openPage` ; P3 y injecte l'ordonnanceur par
  `beforeParse` et trouve la page par le lien `#okf-global-graph` (fragment
  retiré), jamais par un nom en dur.
- Fixtures : chaque tranche ajoute ses propres fichiers sous
  `fixtures/hostile-bundle/` préfixés `p11-`, `p2-`, `p3-` (ou un dossier de ce
  nom), sans éditer ceux d'une autre. Un cas ne code jamais en dur un compte, un
  rang de type ou une taille de fixture : il les lit dans `window.OKF_INDEX`.
  La règle de collision du nom du graphe (§12.3) est testée en xunit, pas par
  une fixture.
- `ACCEPTANCE.md` : P1.1 crée les sections `## P1.1`, `## P2`, `## P3`, chacune
  avec sous son titre une ligne de remplacement (« *(P2 checks)* »…) et une
  ligne vide avant le titre suivant ; chaque tranche remplace la sienne.

### 12.8 Recette outillée (hors CI)

- Emplacement : `tools/viewer-security-check/recette/` — `recette.js` (pilote :
  `--site <dir> --acme <dir> --out <dir> [--browsers chrome,edge,firefox,webkit]
  [--slices p1,p1.1,p2,p3]`), `lib.js` (lancement, contrastes, sondes de styles
  calculés, captures), et **un fichier par tranche** : `p1.js` (portage par
  P1.1 des contrôles de la recette de P1, numérotés C1–C11 dans `RECETTE.md`
  et renommés **RC1–RC11** ici, pour ne pas les confondre avec C1–C8 de
  §11.3), `p1-1.js`, `p2.js`, `p3.js`, chacun exportant
  `async function run(ctx)` qui rend ses résultats indexés par identifiant de
  §11 (H1…L6, RC1…RC11) et par ligne d'`ACCEPTANCE.md`. `p1-1.js` porte aussi
  le contrôle `fonts` (procédure de §11.0) ; `p3.js` vérifie H9 et H10 sur la
  page du graphe.
- Playwright **n'est pas une dépendance du dépôt** : résolu à l'exécution par
  `OKF_PLAYWRIGHT` (chemin d'un module `playwright-core`, par exemple le cache
  `npx` de l'utilisateur), sinon `require("playwright-core")`, avec un message
  clair s'il manque ; jamais dans `package.json` ; jamais lancé par `npm test`
  ni par la CI.
- Résultats et captures écrits sous `--out` (hors du dépôt par défaut : le
  dossier temporaire du système) ; captures à 1 440 × 900, nommées par
  identifiant de §11, pour la comparaison côte à côte avec les maquettes ; la
  recette mesure les valeurs de §11 (police réellement chargée par
  `document.fonts.load` selon §11.0, tailles, couleurs, contrastes des textes
  et des formes dans les deux thèmes, largeurs à 1 100, 1 190 et 1 440, L6) et
  l'absence de toute requête hors du site (contrôle 14). Le rapport d'une
  tranche va dans la description de la PR, pas dans le dépôt.
- Documentation : P1.1 met à jour `CLAUDE.md` (exception des polices à côté de
  `marked`, `okf-shapes.js` source unique des formes de type, de confiance, de
  péremption et de fantôme), `NOTICE`, le README du viewer et celui du harnais
  (section « recette ») ; P3, après la fusion de P1.1, met à jour `CLAUDE.md`
  (`graph.html`, règle de déterminisme de `okf-sim.js`) et le README ; le
  `CHANGELOG` suit §12.0.

## 13. Points ouverts pour le propriétaire

Aucun à la révision 6 : les six points de la révision 5 sont tranchés par
A22 (libellé des lignes), A25 (nom de la page du graphe), A26 (polices en
`file://`), A24 (symbole de péremption), A23 (taille de l'index) et A27 (repli
du frontmatter).

Choix de rédaction non dictés par une maquette ni par un arbitrage, appliqués
par le texte et contestables :

1. **Tailles de forme hors maquette** : les cases en italique de la table
   `SIZES` (§12.2), notamment le carré local 23 (rapport 30 / 26 de B appliqué
   au cercle 20 de A), le centre local aux tailles de B sauf le carré 28 de A,
   l'anneau 26 trait 3 et la forme « autre » de B.
2. **Point de confiance humaine et cercle du rang 0** : deux disques
   `--blue` dans une même ligne de l'explorateur (glyphe de type 10 à gauche,
   drapeau 8 à droite) ; gardés tels que la maquette A les dessine — colonne,
   taille, texte masqué et légende les distinguent.
3. **Contours de nœud** : sélection à côté + 12 (relevé dans A et B), focus
   clavier à côté + 20, tireté 3 3 (G14 ne donne pas de taille).
4. **Ligne d'un nœud destination et dossier** : deux emplacements, chevron puis
   glyphe (E6) ; cible du chevron de 12 de large conforme à WCAG 2.5.8 par
   l'exception d'espacement (E7).
5. **Puces non pressées** : `--gray` partout (E4), alors que B écrit les puces
   de tags en `--ink` (G6).
6. **Flèche d'un lien cassé** : `--edge` comme B, sur une arête `--ghost` (G12).
7. **Masquage sans saut du frontmatter** sous `html[data-okf-js]` (C6) : si
   `okf-page.js` manquait, les entrées repliées resteraient masquées.
