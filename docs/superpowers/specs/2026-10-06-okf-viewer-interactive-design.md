# Viewer interactif (`okf-render`) — design

Date : 2026-10-06
Statut : révision 5 (2026-10-07) : P1 livré et recetté ; arbitrages A15–A21
rendus ; fidélité aux maquettes (§11) et contrats entre tranches (§12) ajoutés ;
prêt pour les plans de P1.1, P2 et P3, rédigés en parallèle

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
  partout où les formes apparaissent.
- **Polices de la maquette embarquées** (A16) : Inter, Inter Tight, Space Mono
  (SIL OFL 1.1), servies depuis `assets/`.

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
  `Actor.Kind`, `Actor.Id`), `Lifecycle.StaleAfterDate`,
  `Frontmatter.AsMapping().Entries` (déjà lu par P1) ;
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
- Pas de règle de collision à ajouter : une page de concept s'écrit toujours
  `<id>.html`, et `HtmlWriter.GuardNoCaseCollisions` exclut déjà `assets/` pour
  cette raison (son commentaire dit « the three asset files » : à mettre à jour).
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
à 200 points de code, médiane 124. **P1.1 mesure** la taille réelle avec la
commande de la tâche 4 du plan P1 et la consigne ici, avec celle
d'`acme_retail`, avant d'écrire le code qui lit les nouveaux champs.

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
  `okf-explorer.js`, `okf-palette.js`, `okf-toc.js` ; ajoutés par P1.1 —
  `okf-shapes.js` (`OkfShapes`, §12.2), `okf-page.js` (tête de la colonne
  centrale, glyphes des listes) ; P2 — `okf-local.js` ; P3 — `okf-sim.js`
  (`OkfSim`, §12.5) et `okf-graph.js`. Ordre de chargement : §12.6.

### 4.2 Simulation du graphe global (module maison)

Module pur, sans DOM :

- entrée triée (§3.1) ; graine dérivée de cet ordre ; ni `Math.random` ni
  horloge ;
- **primitives restreintes** à `+ − × ÷` et `Math.sqrt`, dont ECMAScript fixe
  l'arrondi ; `Math.sin/cos/exp/pow/atan2` sont approchées selon le moteur ;
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
  fantôme. La disposition en anneaux peut utiliser `Math.cos`/`Math.sin` : la
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
chemins de l'index via le résolveur (§3.5). Toutes les formes — explorateur,
palette, puces, listes, légendes, graphes — sortent d'**un seul** module,
`okf-shapes.js` (§12.2) ; aucune autre tranche ne dessine une forme de type.

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
  suivi d'un identifiant dérivé du texte du titre par une règle unique (à fixer au plan :
  minuscules, espaces en `-`, ponctuation retirée), suffixes `-1`, `-2` en cas de
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
  texte à côté ou dans la légende.
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
  `requestAnimationFrame` (absent de la configuration jsdom actuelle), vidé
  explicitement par les tests ;
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
   une itération en son milieu ; contrôle statique qu'aucune fonction
   `Math` hors `sqrt` n'est appelée (smoke check, dit comme tel dans son
   commentaire). Validé sous V8 seulement.
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
    §12.2, rejette une coordonnée non finie, n'emploie jamais un texte du bundle
    comme nom de classe ; classement des types lu dans l'index, jamais
    recalculé ; aucun `svg` dans `okf-body`.
11. **Tête de page** (P1.1) : `status`, vérificateur, date, valeurs de
    frontmatter et nom du bundle hostiles restent du texte inerte ; puce de
    péremption basculée aux mêmes instants que le contrôle 5 ; « Show all »
    annoncé (`aria-expanded`).
12. **Graphe local** (P2) : plafond de 40 nœuds et « +N omis », 1 et 2 sauts,
    fantômes non navigables, liste équivalente complète, arêtes pleines et
    tiretées selon §12.4.
13. **Page `graph.html`** (P3) : fragment hostile, inconnu ou d'un fantôme →
    aucune sélection ni lien ; fragment d'un concept → sélection et centrage ;
    « Reading view » pointe la page du concept sélectionné ; sélection reflétée
    par `history.replaceState`, jamais par une nouvelle entrée d'historique.
14. **Aucune requête hors du site** : chaque page (y compris `graph.html`) ne
    charge que des chemins relatifs du site généré — polices comprises.

Côté xunit : la projection `SiteModel` → index (ordre `ConceptId.CompareTo`,
arbre destination + enfants, arêtes fusionnées et fantômes, palier issu de
`ConceptAudit` sous horloge fixe, `staleAfterMs` aux frontières, sérialisation
hostile), l'écriture d'`assets/okf-index.js` par `HtmlWriter`, et la racine
déclarée par chaque page. Les tests de marqueurs source dans `ViewerAssetsTests`
disent dans leur commentaire qu'ils sont de simples vérifications de surface.

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
- **P1.1 — fidélité** (A15, A16, A18, A19 ; tout ce que §11 marque « P1.1 ») :
  schéma d'index v2 (§12.1) et sa mesure (§3.6) ; module de formes et légendes
  (§12.2) ; en-tête complet (§12.3), y compris le lien « Global graph » et le
  nom du fichier du graphe ; colonne centrale (fil d'Ariane, puces, boîte de
  frontmatter, sans double titre) ; explorateur (glyphes, puces de type,
  comptes, légende, style de ligne active) ; section courante du sommaire ;
  glyphes et compte de « Referenced by » ; palette au dessin de C ; polices
  embarquées, `NOTICE`, `CLAUDE.md` ; jetons de couleur et typographie (§11.0) ;
  **plomberie partagée** : fichiers `okf-local.js`, `okf-sim.js`, `okf-graph.js`
  créés vides (en-tête de licence et IIFE vide), embarqués, écrits et
  référencés aux places de §12.6, sections réservées dans `viewer.css`, dans
  `run.js`, dans `ACCEPTANCE.md` et dans la recette (§12.7, §12.8).
- **P2 — graphe local** (§4.3, §12.4) : `okf-local.js`, sa section de
  `viewer.css`, ses cas de harnais et de recette. Aucun C#.
- **P3 — graphe global** (§4.2, §4.3, §12.5) : `okf-sim.js`, `okf-graph.js`,
  écriture de `graph.html` par `HtmlWriter` (`RenderGraph`), sa section de
  `viewer.css`, ses cas.

Dépendances et parallélisme :

- **sans dépendance** : le module de simulation `okf-sim.js` de P3 (pur, sans
  DOM, contrat §12.5) et ses contrôles 1 et 2 peuvent commencer tout de suite ;
  s'il précède la plomberie de P1.1, P3 crée `okf-sim.js` et ses contrôles
  synchrones seulement, et P1.1 embarque ce fichier au lieu d'un fichier vide ;
- **après le contrat d'index et de formes de P1.1** (§12.1, §12.2 livrés et
  fusionnés sur la branche) : l'interface de P2 et celle de P3 ; P2 et P3 sont
  alors parallèles entre eux, car ils n'écrivent dans aucun fichier commun hors
  des sections réservées par P1.1 et d'une ligne chacun au `CHANGELOG` ;
- **après l'en-tête de P1.1** (§12.3) : le lien « Open in graph » de P2 et le
  « Reading view » de P3, qui lisent la cible du lien « Global graph » ;
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
  publique, §2.1). Double titre : §11.3, C2.
- **A16 — Polices embarquées** : Inter (400, 500, 600), Inter Tight (600, 900),
  Space Mono (400, 700), SIL OFL 1.1, fichiers woff2 intégrés à
  `OKF4net.Viewer`, écrits sous `assets/`, chargés par `@font-face` relatif ;
  mention dans `NOTICE` ; exception documentée dans `CLAUDE.md` à côté de
  `marked` ; taille mesurée et consignée (§11.0).
- **A17 — Graphe global = page `graph.html`** à la racine du site. « Global
  graph » dans l'en-tête de chaque page → `graph.html#<id du concept courant>`,
  ouvert centré et sélectionné sur ce concept ; dans `graph.html`, « Reading
  view » ramène à la page du concept sélectionné, ou à l'index. Précédent et
  Suivant natifs ; URL partageable. Collision de nom : §12.5.
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
(`OkfSite.slugify`, `uniqueSlugs`) ; et, en révision 5, les choix de §11 et §12
non dictés par une maquette ou un arbitrage, listés en §13.

## 11. Fidélité aux maquettes

Check-list **exhaustive** des éléments visibles de A et B et de la palette de
C. Chaque élément porte un identifiant (repris par la recette, §12.8), les
valeurs relevées dans le source des maquettes, et la tranche qui le livre :
**P1** (déjà livré), **P1.1**, **P2**, **P3**, ou **écarté** avec la raison.
« P1 → P1.1 » signifie : fonction livrée par P1, dessin aligné par P1.1. Les
données des maquettes (`acme_retail`, 9 concepts, 14 liens) sont
illustratives. Tailles en px.

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
| `--stale` (P1.1) | `#b4540a` | `#e08a3e` | péremption | 5,0 / 7,1 |
| `--okf-type-0` cercle | `#1a3fd6` | `#8fa5f5` | rang 0 | 7,7 / 8,0 |
| `--okf-type-1` carré | `#101014` | `#f2f2f5` | rang 1 | 19,0 / 17,0 |
| `--okf-type-2` losange | `#b4540a` | `#e08a3e` | rang 2 | 5,0 / 7,1 |
| `--okf-type-3` triangle | `#6a6a72` | `#9a9aa2` | rang 3 | 5,4 / 6,8 |
| `--okf-type-4` anneau | `#0b6e69` | `#2fb3a8` | rang 4 (trait 2) | 6,1 / 7,4 |
| `--okf-type-5` autre | `#6a6a72` | `#9a9aa2` | autres types (carré creux) | 5,4 / 6,8 |
| `--backdrop` | `rgba(16,16,20,.34)` | `rgba(0,0,0,.55)` | fond de palette | — |
| `--shadow` | `0 18px 50px rgba(16,16,20,.28)` | `none` | boîte de palette | — |

Contre `--blue-soft`, le minimum est 4,4:1 en clair (`#b4540a`) et 6,2:1 en
sombre : toutes les formes passent 3:1. Constat au passage : P1 ne redéfinit pas
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
| titres de section | Space Mono | 400 | 11 | capitales, 0,06em, `--gray` |
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
taille) consignée dans `src/OKF4net.Viewer/Assets/fonts/README.md`. `@font-face`
en tête de `viewer.css`, `url("fonts/<fichier>.woff2")` (relatif à la feuille,
donc valable à toute profondeur), `font-display: swap`, `unicode-range` du
sous-ensemble. Les assets binaires demandent une lecture en octets
(`ViewerAssets`) et une écriture en octets dans `HtmlWriter`, sous les **mêmes**
gardes que `WriteFile`. **Budget** : ≤ 300 Ko pour l'ensemble des woff2 ; taille
mesurée et consignée ici par P1.1, avec la croissance du binaire `okf-render`
AOT ; au-delà, retour au propriétaire. `okf` n'est pas concerné (il ne référence
pas `OKF4net.Viewer`). Risque `file://` sous Firefox : §13.

### 11.1 En-tête (pages de concept, index, `graph.html`)

- **H1** Bande de 6 `--blue` en haut, pleine largeur. → P1
- **H2** Barre : hauteur 52, filet bas 1 `--hair`, `padding: 0 20px`,
  `gap: 20px`, pleine largeur (P1 : bloc centré de 1 440, padding 16). → P1.1
- **H3** Marque « OKF4net » + `§` en exposant (jetons §11.0), lien vers
  `index.html` (P1 : « OKF »). → P1.1
- **H4** Séparateur vertical 1 × 20 `--hair`, décoratif. → P1.1
- **H5** Nom du bundle, Space Mono 13 `--ink` (contrat §12.3). → P1.1
- **H6** « N concepts · M links », Space Mono 13 `--gray` ; N = concepts de
  l'index, M = arêtes fusionnées de l'index, fantômes compris ; singuliers
  « 1 concept », « 1 link » ; écrit en C#. → P1.1
- **H7** Bouton de palette, dessin de C : hauteur 34, largeur 320 (rétrécit
  jusqu'à 160), bord `--hair`, texte `--gray` 13,5 « Jump to a concept… », à
  droite l'indication « Ctrl K · / » Space Mono 11 bord `--hair` padding 1 6,
  masquée sous 900 de large ; premier des outils, après l'espace flexible
  (P1 : bouton texte « Jump to... »). → P1 → P1.1
- **H8** « Filters » : hauteur 34, padding 0 14, bord `--hair`, 13,5 500 ;
  pastille de compte Space Mono 11, fond `--blue`, texte `--white`, padding
  1 6, masquée à 0 ; compte = puces de type pressées + 1 si le champ de filtre
  n'est pas vide ; action §8. Pages de concept et index seulement (absent de
  B). → P1.1
- **H9** « Global graph » : lien `<a>` dessiné en bouton, 13,5 600 ; sur A,
  bord `--blue`, fond `--blue-soft`, texte `--blue` ; sur `graph.html`, fond
  `--blue`, texte `--white`, `aria-current="page"`. → P1.1 (lien, §12.3), P3
  (cible et état courant)
- **H10** « Reading view » (B seulement) : lien dessiné en bouton neutre
  (bord `--hair`, fond `--white`, 13,5 500), avant « Global graph ». → P3
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
- **E2** Titre « EXPLORER » (titre de section, §11.0) ; le champ garde un nom
  accessible propre, « Filter by name » (P1 : libellé visible « Filter the
  explorer »). → P1 → P1.1
- **E3** Champ : hauteur 34, bord `--hair`, 13,5, placeholder « Filter by
  name… » ; ancêtres des résultats conservés. → P1 → P1.1
- **E4** Puces de type (A : « Metric 3 », « Attested 2 »…) : **filtre de
  l'explorateur, livré par P1.1**, et non facette de P3 — la maquette les place
  dans l'explorateur, et le « Filters 2 » de l'en-tête compte les deux puces
  pressées. Une puce par type de rang 0 à 4, plus une puce « Other » si des
  types de rang 5 existent ; texte = nom complet du type (pas l'abréviation
  « Attested ») + compte ; glyphe de forme ajouté à gauche, car ces puces sont
  la légende des types de la page (A19) ; hauteur 26, padding 0 10, 12 ;
  pressée : bord `--blue`, fond `--blue-soft`, texte `--blue` 600 ; non
  pressée : bord `--hair`, fond `--white`, texte `--gray`. OU entre puces
  pressées, ET avec le champ, ancêtres conservés ; aucune pressée = aucun
  filtre. → P1.1
- **E5** Liste sous un filet haut `--hair`, padding 6 0. → P1.1
- **E6** Ligne : hauteur 30, retrait 12 + 18 par niveau, `gap: 9px`, padding
  droit 14 ; dans l'ordre : chevron (si enfants) ou espace, glyphe de type 12
  (si destination), libellé, compte (si enfants), drapeau de confiance, drapeau
  de péremption. → P1 → P1.1
- **E7** Chevron dessiné en CSS : 6 × 6, traits 1,5 `--gray`, 45° ouvert,
  −45° fermé, dans un bouton d'au moins 24 × 24 (P1 : caractères ▸ ▾). → P1
  → P1.1
- **E8** Libellé 13,5 : **le nom du segment**, comme la maquette
  (`gross-margin-period`), titre en infobulle ; le filtre cherche toujours titre
  et id (P1 : titre) — voir §13. Dossier : 600 `--ink` (P1 : `--gray`). → P1.1
- **E9** Compte de dossier, Space Mono 11 `--gray` : nombre de concepts
  (destinations) sous le nœud, lui exclu. → P1.1
- **E10** Drapeaux : `human-reviewed` point plein 8 `--blue`,
  `machine-confirmed` anneau 8 trait 2 `--blue`, `unverified` rien ; péremption
  triangle 9 × 8 `--stale`, visible seulement si périmé maintenant (§4.4). → P1
  (formes servies par `OkfShapes` en P1.1)
- **E11** Ligne active : fond `--blue-soft`, filet gauche 3 `--blue`, libellé
  `--blue` 600 (P1 : couleur et graisse seulement). → P1 → P1.1
- **E12** Légende en pied : filet haut `--hair`, padding 12 16, `gap: 7px`,
  12 `--gray` : « human-reviewed », « machine-confirmed », « stale (now ≥
  `stale_after`) », avec leurs glyphes ; collée en bas de l'explorateur en
  mise en page large. → P1.1
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
- **C3** H1 = titre d'affichage, jetons §11.0, marges 14 0 6 (P1 : 32). → P1
  → P1.1
- **C4** **Double titre — décidé.** Si la première ligne non vide du corps est
  un titre ATX de niveau 1 en colonne 0 (`#` puis espace ou tabulation) dont le
  texte — séquence fermante de `#` retirée, espaces extrêmes retirés, espaces
  internes fusionnés — est égal **en ordinal** au titre d'affichage normalisé
  de même façon, cette ligne et les lignes vides qui la précèdent sont retirées
  du markdown que porte le payload. Règle pure dans `SiteModel` (tests xunit),
  résultat dans une nouvelle propriété `ViewerPage.DisplayBody` ; `Body` reste
  le corps brut ; `viewer.js` est inchangé. Sûr : un titre ATX n'a ni ligne de
  continuation ni effet sur l'analyse des lignes suivantes, et le reste passe
  par le même sanitizer. Non retirés (double titre conservé, sans risque) :
  titre setext, titre à mise en forme en ligne, texte différent. → P1.1
- **C5** Puces (rangée `gap: 8px`, marge basse 18 ; chacune hauteur 26,
  padding 0 10, 12) :
  - **type** : fond `--ink`, texte `--white` 600, forme 8 en `--white` ; nom
    complet, « (no type) » si absent ;
  - **`status`** : seulement si la clé existe et est scalaire ; valeur brute,
    Space Mono, bord `--hair` ;
  - **confiance** : palier lu dans l'index (celui de l'explorateur, jamais
    re-dérivé), avec son glyphe ; paliers vérifiés : bord et texte `--blue` ;
    `unverified` : bord `--hair`, texte `--gray`, sans glyphe ; puis
    « · vérificateur · date » si une entrée `verified` du genre du palier existe
    — la **dernière** en ordre du document parmi les entrées humaines (palier
    humain) ou parmi toutes (palier machine) ; vérificateur = `Actor.Id` d'un
    acteur `human:` bien formé, sinon `Actor.Raw` ; date = les dix premiers
    caractères de `At` s'ils ont la forme `AAAA-MM-JJ`, sinon `At` tel quel,
    omise si absente ; « +N » s'il existe N autres entrées ;
  - **péremption** : seulement si `Lifecycle.StaleAfterDate` existe ; écrite
    en C# « stale after AAAA-MM-JJ » (bord `--hair`, texte `--gray`) ;
    `okf-page.js` la bascule d'après l'index, aux instants de §4.4, en « stale
    since AAAA-MM-JJ » (bord et texte `--stale`, glyphe de péremption).

  Glyphes posés par `okf-page.js` dans des emplacements vides écrits par le C#
  (§12.3) ; sans JavaScript, les puces restent du texte. → P1.1
- **C6** Boîte de frontmatter : bord `--hair`, marge basse 22 ; tête padding
  9 14, filet bas, fond `--blue-soft` : « Frontmatter · N fields » (titre de
  section ; N = toutes les entrées ; « 1 field ») et, à droite, « Show all » /
  « Show fewer » (12,5 600 `--blue`, sans bord, `aria-expanded`,
  `aria-controls`), ajouté par `okf-page.js` seulement s'il y a des entrées
  repliées. Corps : grille de deux colonnes `minmax(0, 1fr)`, cellule padding
  8 14, filet bas sauf dernière rangée ; clé Space Mono 12 `--gray` sur 92 ;
  valeur 13,5, coupure `anywhere`. Repliée : les quatre premières entrées en
  ordre du document hors `type`, `title`, `status`, `verified`, `stale_after`
  (déjà montrées par H1 et puces) ; dépliée : toutes, en ordre du document.
  Sans JavaScript : tout est visible. Une séquence de scalaires s'affiche jointe
  par « , » (maquette : `finance, margin, attested`) ; les autres structures
  gardent l'émission YAML compacte de P1. Écart : la maquette tronque
  `{ resource: …, … }` ; on ne tronque pas (aucune perte d'information).
  Remplace `table.frontmatter`. → P1.1
- **C7** Corps : H2 21 marge 0 0 8 ; paragraphe 15,5 / 1,65 ; tableau 13,5,
  en-têtes Space Mono 12 400 `--gray`, filets `--hair` ; `pre` fond
  `--blue-soft`, padding 16, Space Mono 12,5 / 1,6 ; liens `--blue`, survol
  `--blue-hover`. Règles de contenu sous `#okf-body`, pas de chrome. → P1.1
- **C8** `index.html` : « Bundle index », compte, erreurs d'analyse, corps —
  sans maquette, inchangé ; en-tête de §11.1. → P1 (en-tête : P1.1)

### 11.4 Panneau contextuel (A)

- **X1** Panneau de 340 à partir de 1 100, filet gauche `--hair`, padding
  20 20 0, sections espacées de 22 (P1 : 260 à 320). → P1 → P1.1
- **X2** Titres de section (§11.0). → P1
- **X3** « On this page » : liens 13,5, padding 5 0 5 10, filet gauche 2
  `--hair` ; H3 en retrait. → P1
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
- **X7** Rendu : centre = forme de son type, contour de sélection carré
  `--blue` 2 à 6 de la forme, libellé Space Mono 10,5 700 ; voisins forme 18 à
  20, libellé = dernier segment de l'id, Space Mono 10 `--ink`, id complet en
  `<title>` ; arêtes `--edge` 1,4, flèche 7 vers la cible ; **pleine** si la
  cible n'est pas plus proche du centre que la source (« links to »),
  **tiretée 4 3** si elle pointe vers le centre (« referenced by ») ; fantôme
  comme G13 à l'échelle. → P2
- **X8** Pied 12 `--gray` : « solid = links to · dashed = referenced by » ; à
  droite « Open in graph » 600 `--blue` sans soulignement. → P2
- **X9** Plafond et liste (hors maquette, exigés par §6 et A11) : « +N
  omitted » dans le cadre au-delà de 40 nœuds ; sous le cadre, « List · N
  neighbours » repliable, tous les voisins avec glyphe, id et relation. → P2
- **X10** « Referenced by · N » : compte écrit en C# ; lignes 13,5, padding
  6 0, filet bas `--hair`, `gap: 9px`, glyphe de type 10 posé par
  `okf-page.js`, puis l'id. → P1 (liste) → P1.1 (compte, glyphes)
- **X11** Légende des formes sur une page de concept : les puces de type de
  l'explorateur (E4) ; chaque glyphe porte aussi un `<title>` au nom du type.
  → P1.1

### 11.5 Page `graph.html` (B, A17)

- **G1** En-tête §11.1 : « Reading view » (H10), « Global graph » courant
  (H9), thème ; pas de « Filters » (facettes toujours visibles) ; bouton de
  palette (H7), la palette ouvrant la page du concept. → P3
- **G2** Facettes : panneau de 270, filet droit `--hair`, padding 18 18 0,
  sections espacées de 20, défile seul. → P3
- **G3** **Type** : une ligne par entrée de `types`, ordre de l'index ;
  hauteur 32, `gap: 10px`, case 16 (`accent-color` `--blue`), glyphe, nom
  (« (no type) »), compte Space Mono 11 `--gray` ; toutes cochées par défaut ;
  nom d'une case décochée en `--gray` ; les types de rang 5 sont listés un par
  un (forme « autre »). C'est la légende des types de la page. → P3
- **G4** **Trust** : `human-reviewed` (point), `machine-confirmed` (anneau),
  `unverified` (emplacement de glyphe vide), comptes ; cochés. → P3
- **G5** **Freshness** : une case « Stale only (as of now) », glyphe de
  péremption, compte des périmés maintenant ; décochée ; réévaluée aux instants
  de §4.4. → P3
- **G6** **Tags** : puces de E4, « tag N », compte décroissant puis ordinal ;
  les 12 premières, puis « Show all tags (K) » ; pressées = OU ; aucune = pas
  de filtre. → P3
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
- **G11** Nœuds (§12.2) : cercle r 13, carré 30, losange de diagonale 31,
  triangle 24, anneau r 13 trait 3, autre 26 ; libellé Space Mono 11,5
  `--ink` = dernier segment de l'id (id complet en `<title>`), masqué si
  « Node labels » est décoché. → P3
- **G12** Arêtes `--edge` 1,3, flèche 7 vers la cible ; deux arêtes opposées
  décalées de ± 3 perpendiculairement ; vers un fantôme : `--ghost` 1,4,
  tiretée 5 4. → P3
- **G13** Fantôme : cercle r 13, fond `--white`, trait `--ghost` 1,6 tireté
  3 3, libellé « absent: `<id>` » `--ghost` 11,5. → P3
- **G14** Sélection : contour carré `--blue` 2,4 à 6 du nœud ; sortantes
  `--blue` 2,2 pleines, entrantes `--blue` 2,2 tiretées 5 4, flèches `--blue`
  8 ; focus clavier : contour tireté `--ink` 2, distinct de la sélection. → P3
- **G15** Légende en bas à gauche (16, 18), 12 `--gray`, `gap: 14px` : « Edge
  = body link (§6), as in okf graph · arrow points at the target », « Red
  dashed = broken link to an absent concept », « Blue = selection and its
  links », « Drag to pan · scroll to zoom ». → P3
- **G16** Interactions : glisser le fond = déplacer la vue ; molette = zoom
  autour du pointeur ; glisser un nœud = le déplacer ; clic = sélection ;
  double-clic ou Entrée = ouvrir la page. → P3
- **G17** Tiroir « Selected » : 340, filet gauche, padding 20, `gap: 16px` ;
  « SELECTED » ; id Space Mono 12 `--gray` ; titre H2 (§11.0) ; puces type et
  confiance (C5, sans vérificateur) ; description de l'index (A18), 14 / 1,6 ;
  « Links to · N » et « Referenced by · N » (lignes de X10, liens vers les
  pages ; un fantôme cité : « absent: `<id>` », sans lien) ; « Open page »,
  lien hauteur 40, fond `--blue`, texte `--white` 14 600. Sans sélection :
  « Select a concept to see its links. » Fantôme sélectionné : « absent » et
  l'id, ni lien ni « Open page » (A10). → P3
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
- **J4** Option : hauteur ≥ 46, padding 0 16, `gap: 12px` : glyphe de type
  10, titre 15, id Space Mono 12 `--gray` ; règles de coupure de P1 (R4)
  conservées. → P1 → P1.1
- **J5** Option active : fond `--blue-soft`, filet gauche 3 `--blue` (P1) ;
  titre `--blue` 600 (P1.1). → P1 → P1.1
- **J6** Pied : filet haut `--hair`, padding 10 16, 12 `--gray`, `gap: 16px` :
  « Up / Down to move », « Enter to open ». → P1.1
- **J7** « Tab to show it in the graph » : **écarté** — contredit §8 (Tab
  reste dans le dialogue) ; la palette ouvre la page.
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

### 11.8 Écartés

J7 ; tout C hors palette (rail d'icônes, mise en page « Focus », bande
« Related », puce « Frontmatter », fil d'Ariane dans l'en-tête) ; l'abréviation
« Attested » (E4) ; la troncature des valeurs de frontmatter (C6).

## 12. Contrats entre tranches

Ce que les trois plans parallèles partagent. Une tranche qui a besoin de changer
un de ces contrats demande un changement de spec ; elle ne le change pas seule.

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
  de 200 points de code (une paire de substitution valide compte un, un demi
  isolé compte un et est gardé tel quel), on garde les 199 premiers, on retire
  les espaces finales et on ajoute « … » (U+2026) — jamais de coupure entre les
  deux moitiés d'une paire. Sérialisée par `HtmlSafeJson.Quote`.
- Côté C# : `IndexConcept` gagne `TypeIndex` et `Description`, `ViewerIndex`
  gagne `Types` (`IndexType(string Name, int Count, int Slot)`),
  `ViewerIndex.Empty` suit.
- Sur `acme_retail` réel, la règle donne Metric → cercle, Attested
  Computation → carré, Policy → losange, BigQuery Table → triangle, Skill →
  anneau (la maquette inverse les deux derniers : ses données sont
  illustratives).

### 12.2 Formes : `assets/okf-shapes.js` (propriétaire : P1.1)

```js
window.OkfShapes = Object.freeze({
  SLOT_NAMES,                  // Object.freeze(["circle","square","diamond","triangle","ring","other"])
  OTHER_SLOT,                  // 5
  typeLabel(name),             // name === "" ? "(no type)" : name
  slotOf(index, position),     // rang 0..5 du concept ; 5 si typeIndex ou slot hors bornes
  icon(doc, slot, size, title),// <svg> autonome pour un contexte HTML ; title facultatif → <title>
  trustIcon(doc, trust, size), // <svg> ou null (unverified, valeur inconnue)
  staleIcon(doc, size),        // <svg>
  ghostIcon(doc, size),        // <svg>
  node(doc, slot, x, y, size), // <g class="okf-node"> à poser dans le <svg> d'un graphe
  ghostNode(doc, x, y, size),  // <g class="okf-node okf-node-ghost">
  typeLegendEntries(index),    // [{ kind: "type", slot, label, count }] : rangs 0–4 dans l'ordre
                               // de types, puis « Other types » (slot 5, compte cumulé) s'il y a lieu
  legend(doc, entries),        // <ul class="okf-legend"> ; entrée : { kind: "type", slot, label, count? }
                               // | { kind: "trust", trust } | { kind: "stale" } | { kind: "ghost" }
});
```

- Tout élément SVG par `createElementNS("http://www.w3.org/2000/svg", nom)`.
  **Vocabulaire fixe** — éléments : `svg`, `g`, `circle`, `rect`, `path`,
  `line`, `text`, `title`, `defs`, `marker` ; attributs : `viewBox`, `width`,
  `height`, `class`, `aria-hidden`, `focusable`, `role`, `aria-label`,
  `tabindex`, `id` (valeurs fixes `okf-…` seulement), `cx`, `cy`, `r`, `x`,
  `y`, `x1`, `y1`, `x2`, `y2`, `d`, `points`, `text-anchor`,
  `dominant-baseline`, `marker-end`, `refX`, `refY`, `markerWidth`,
  `markerHeight`, `orient`, `transform` (seulement `translate(a b) scale(s)`
  pour le pan/zoom). P2 et P3 n'utilisent pas d'autre nom.
- `d`, `points`, `transform` et toute coordonnée sont construits à partir de
  nombres vérifiés par `Number.isFinite` ; sinon `TypeError` (bogue de
  l'appelant). Aucun texte du bundle ne devient un nom de classe, un `id` ou un
  attribut autre que `aria-label` et le texte de `<title>`/`<text>`
  (`textContent`).
- Classes de forme d'une table fixe : `okf-shape-0` à `okf-shape-5`,
  `okf-trust-human`, `okf-trust-machine`, `okf-stale-mark`, `okf-ghost-mark`.
  Les couleurs ne sont jamais posées en attribut : des règles CSS sous un
  ancêtre `svg` (`svg .okf-shape-2 { fill: var(--okf-type-2) }`) les lisent sur
  les jetons de §11.0, si bien qu'un changement de thème ne redessine rien. Un
  `svg` ne peut pas apparaître dans `okf-body` (le sanitizer l'exclut, §4.5) :
  ces règles sont ancrées par construction.
- Géométrie dans `viewBox="0 0 12 12"` (les nœuds de graphe la reproduisent en
  coordonnées absolues, sans `transform`) : cercle `r` 5 plein ; carré 9 × 9
  plein ; losange `M6 1.5 L10.5 6 L6 10.5 L1.5 6 Z` plein ; triangle
  `M6 1 L11.5 11 L0.5 11 Z` plein ; anneau `r` 4, trait 2, sans remplissage ;
  autre carré 8 × 8, trait 1,5, sans remplissage ; confiance humaine point
  `r` 4 plein `--blue`, machine anneau `r` 3, trait 2 ; péremption triangle
  `--stale` ; fantôme cercle trait `--ghost` tireté. Icônes : `aria-hidden`,
  `focusable="false"`.
- Utilisateurs : explorateur (E4, E6, E10, E12), palette (J4), puces et listes
  (`okf-page.js` : C5, X10), graphe local (P2), page graphe (P3). Aucun ne
  dessine une forme de type sans ce module.

### 12.3 Coque et en-tête (propriétaire : P1.1)

```html
<html lang="en" data-okf-root="../" data-okf-view="page" data-okf-concept="a/b">
<!-- data-okf-view : "page" | "index" | "graph" ; data-okf-concept seulement sur une page de concept -->
<a class="okf-skip" href="#okf-main">Skip to content</a>
<div class="topline"></div>
<header class="bar"><div class="bar-in">
  <a class="wordmark" href="../index.html">OKF4net<sup>§</sup></a>
  <span class="bar-sep" aria-hidden="true"></span>
  <span class="bar-bundle" id="okf-bundle-name">acme_retail</span>
  <span class="bar-counts" id="okf-bundle-counts">9 concepts · 14 links</span>
  <div class="bar-tools" id="okf-tools">
    <!-- okf-palette.js insère #okf-palette-open en premier -->
    <!-- okf-explorer.js insère #okf-filters-toggle juste avant #okf-global-graph (page, index) -->
    <!-- graph.html seulement (P3) : <a class="okf-tool" id="okf-reading-view" href="index.html">Reading view</a> -->
    <a class="okf-tool okf-tool-graph" id="okf-global-graph" href="../graph.html#a/b">Global graph</a>
    <!-- okf-theme.js ajoute #okf-theme-toggle en dernier -->
  </div>
</div></header>
<div class="okf-layout"> … <main id="okf-main"> <div class="okf-page-head">…</div> <div id="okf-body"></div> </main> …
```

- **Nom du bundle** : `ViewerSite.BundleName` =
  `Path.GetFileName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(bundle.Root)))`,
  « bundle » si vide ; échappé par `HtmlEscape`.
- **Comptes** : depuis `site.Index` (H6).
- **Page du graphe** : `ViewerSite.GraphPagePath`, calculé par `SiteModel`
  (P1.1) : `graph.html`, sauf si une page de concept s'écrit ainsi à la casse
  près (concept racine `graph`, `Graph`…) ; alors `graph-1.html`,
  `graph-2.html`…, le premier nom libre à la casse près parmi les pages et
  `index.html`. Testé en xunit. `HtmlWriter` l'ajoute à
  `GuardNoCaseCollisions` quand il écrit la page (P3).
- **Lien « Global graph »** : `href` = préfixe de racine + `GraphPagePath` +
  `#` + id sur une page de concept (les caractères d'un `ConceptId` sont tous
  admis dans un fragment) ; sans fragment sur l'index ; sur `graph.html`,
  `aria-current="page"`. Écrit en C#. **P2 et P3 lisent la cible du graphe sur
  ce lien** (`getAttribute("href")`, fragment retiré), jamais en dur.
- **Tête de page** (`.okf-page-head`, premier enfant de `main`, pages de
  concept) : `nav.okf-crumbs` (C2), `h1`, `div.okf-chips` (C5) — avec les
  emplacements vides `span.okf-chip-glyph` portant `data-okf-slot="0…5"`,
  `data-okf-trust="human|machine"` ou `data-okf-stale` —, `section.okf-fm`
  (C6, entrées repliables marquées `okf-fm-extra`). `okf-page.js` lit ces
  attributs, à valeurs fixes écrites par le C#, jamais un texte du bundle.
- **« Referenced by »** : `h2#okf-backlinks-title` = « Referenced by » +
  `span.okf-count` « · N » ; chaque lien porte `data-okf-target="<id>"` pour que
  `okf-page.js` retrouve son type dans l'index.
- L'en-tête de `graph.html` suit le même gabarit (P3 l'écrit avec la même
  méthode de `HtmlWriter`, paramétrée).

### 12.4 Graphe local (propriétaire : P2)

Créé par `okf-local.js` dans `#okf-context`, **juste après `#okf-toc`** et avant
`.okf-backlinks` ; caché s'il n'y a aucun voisin ; il révèle `#okf-context` si
celui-ci était caché.

```html
<section class="okf-local" id="okf-local-graph" aria-labelledby="okf-local-title">
  <div class="okf-local-head">
    <h2 id="okf-local-title">Neighbourhood</h2>
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
- Marqueurs de flèche : `id` fixes `okf-local-arrow`, `okf-local-arrow-in`.
- « Open in graph » : `href` de `#okf-global-graph` ; lien omis s'il manque.
- Clic sur un nœud concept : navigation par le résolveur (§3.5) ; fantôme :
  rien.

### 12.5 `graph.html` (propriétaire : P3)

- Écrite par `HtmlWriter.RenderGraph(site)` au chemin `site.GraphPagePath`
  (racine, préfixe `""`), `data-okf-view="graph"`, sans payload, sans `marked`,
  `viewer.js`, `okf-toc.js`, `okf-explorer.js`, `okf-page.js`, `okf-local.js`.
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
  des concepts (jamais des fantômes). Trouvé : facettes à leur défaut, nœud
  sélectionné et centré. Introuvable ou vide : aucune sélection, vue « Fit ».
- **Historique** : une sélection met l'URL à jour par
  `history.replaceState(null, "", "#" + id)` (désélection : sans fragment) ;
  jamais de nouvelle entrée ; `hashchange` sélectionne l'id reçu. Précédent et
  Suivant restent ceux du navigateur entre pages (A17).
- **« Reading view »** : `href` = résolveur(chemin du concept sélectionné), ou
  `index.html` sans sélection.
- **Simulation** `assets/okf-sim.js`, pure (aucun DOM, aucune horloge, ni
  `Math.random`, aucune fonction `Math` hors `sqrt`, aucun global hors le sien),
  contraintes de §4.2 :

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
  d'index, décalée par un générateur congruentiel entier (opérations exactes
  sous 2^53). `okf-sim.js` ne dépend de rien : P3 peut le livrer en premier
  (§9).
- **Ordonnanceur** : `okf-graph.js` lit une fois au démarrage
  `window.OKF_SCHEDULER` (fonction `(callback) => void`, injectée par le
  harnais, §7) et sinon `requestAnimationFrame`. Un changement de filtre
  annule la simulation en cours (`cancel()` et jeton de génération).

### 12.6 Chargement, assets, CSS (plomberie : P1.1)

- **Pages de concept et index** — `<head>` : `okf-theme.js`, `viewer.css` ;
  fin de `<body>` : `#okf-payload`, `marked.min.js`, `viewer.js`,
  `okf-index.js`, `okf-site.js`, `okf-shapes.js`, `okf-explorer.js`,
  `okf-palette.js`, `okf-toc.js`, `okf-page.js`, `okf-local.js`.
- **`graph.html`** — `<head>` : idem ; fin de `<body>` : `okf-index.js`,
  `okf-site.js`, `okf-shapes.js`, `okf-palette.js`, `okf-sim.js`,
  `okf-graph.js`.
- **Assets écrits** par `HtmlWriter.Write`, dans cet ordre : `viewer.css`,
  `viewer.js`, `marked.min.js`, `okf-theme.js`, `okf-site.js`,
  `okf-shapes.js`, `okf-explorer.js`, `okf-palette.js`, `okf-toc.js`,
  `okf-page.js`, `okf-local.js`, `okf-sim.js`, `okf-graph.js`, `fonts/…`,
  `okf-index.js` ; puis `index.html`, la page du graphe (P3), les pages.
- **P1.1 crée `okf-local.js`, `okf-sim.js` et `okf-graph.js` vides** (en-tête
  de licence, IIFE vide), les embarque, les écrit et les référence : P2 et P3
  ne touchent ni `ViewerAssets`, ni la liste d'assets, ni les balises de
  `RenderShell` ; P3 n'ajoute à `HtmlWriter` que `RenderGraph` et son écriture.
- Chaque script : son IIFE, vérifie ses globaux (`OkfSite`, `OkfShapes`, index)
  et rend la main sans erreur s'ils manquent ; aucune dépendance au DOM d'un
  autre script hors des contrats de §12.
- **CSS** : un seul `viewer.css`, en sections dans cet ordre : `@font-face`
  (P1.1) ; jetons et base (P1, révisés par P1.1) ; chrome P1/P1.1 ;
  `/* === P2: local graph === */` ; `/* === P3: graph page === */`. P1.1 crée
  les deux dernières vides ; chaque tranche n'écrit que dans la sienne ; P2 et
  P3 n'ajoutent pas de jeton (§11.0 les fournit tous).
- **Ancrage, propriété de sécurité** (revue finale de P1) : le sanitizer garde
  `class` sur `<code>`, donc le corps peut porter n'importe quelle classe de
  chrome. Tout sélecteur de chrome est ancré à un conteneur de chrome :
  `#okf-tools`, `#okf-explorer`, `#okf-context`, `body > .okf-palette-backdrop`,
  `body > .okf-layout > main > .okf-page-head`, `body > header.bar`,
  `body > .okf-skip`, `body > .okf-graph-layout`, ou un ancêtre `svg`. P1.1
  ancre aussi les règles héritées qui ne l'étaient pas (`.topline`, `.bar-in`,
  `.wordmark`, `.meta`, `.errors`, `table.frontmatter`). Chaque tranche ajoute
  ses classes à `fixtures/hostile-bundle/chrome-classes.md` **dans un
  paragraphe à elle** (`P2: <code class="…">x</code>`), sans éditer celui
  d'une autre, et une sonde qui vérifie que son vrai chrome garde son style ;
  le cas existant compare déjà les styles calculés du contenu portant ces
  classes, `@media` dépliés compris.

### 12.7 Harnais (`tools/viewer-security-check/run.js`)

- Les helpers partagés restent où P1 les a mis, signatures inchangées :
  `okfSite`, `siteResources`, `openPage` (ses options `now`, `storage`,
  `storedTheme`, `hash`, `mount`, `blocked`, `override`, `beforeParse`),
  `navigations`, `key`, `type`, `checkAsync`, `unwrapMedia`. P1.1 ajoute
  `okfShapes()` (fenêtre nue chargeant `okf-site.js` et `okf-shapes.js`) à
  côté d'`okfSite()`.
- P1.1 place, juste avant `// --- end of async checks ---` et séparés par une
  ligne vide, les repères `// --- P2 cases ---` puis `// --- P3 cases ---`.
  P1.1 écrit ses cas au-dessus du premier, P2 entre les deux, P3 entre le
  second et la fin. Les contrôles synchrones d'un module pur (`OkfSim`) vont
  dans la section de la tranche.
- `graph.html` s'ouvre par `openPage` ; P3 y injecte l'ordonnanceur par
  `beforeParse` et trouve la page par le lien `#okf-global-graph`, jamais par
  un nom en dur.
- Fixtures : chaque tranche ajoute ses propres fichiers sous
  `fixtures/hostile-bundle/` préfixés `p11-`, `p2-`, `p3-` (ou un dossier de ce
  nom), sans éditer ceux d'une autre. Un cas ne code jamais en dur un compte, un
  rang de type ou une taille de fixture : il les lit dans `window.OKF_INDEX`.
  La règle de collision du nom du graphe (§12.3) est testée en xunit, pas par
  une fixture.
- `ACCEPTANCE.md` : P1.1 crée les sections `## P1.1`, `## P2`, `## P3` ; chaque
  tranche remplit la sienne.

### 12.8 Recette outillée (hors CI)

- Emplacement : `tools/viewer-security-check/recette/` — `recette.js` (pilote :
  `--site <dir> --acme <dir> --out <dir> [--browsers chrome,edge,firefox,webkit]
  [--slices p1,p1.1,p2,p3]`), `lib.js` (lancement, contrastes, sondes de styles
  calculés, captures), et **un fichier par tranche** : `p1.js` (portage par
  P1.1 des contrôles C1–C11 de la recette de P1), `p1-1.js`, `p2.js`, `p3.js`,
  chacun exportant `async function run(ctx)` qui rend ses résultats indexés
  par identifiant de §11 (H1…J8) et par ligne d'`ACCEPTANCE.md`.
- Playwright **n'est pas une dépendance du dépôt** : résolu à l'exécution par
  `OKF_PLAYWRIGHT` (chemin d'un module `playwright-core`, par exemple le cache
  `npx` de l'utilisateur), sinon `require("playwright-core")`, avec un message
  clair s'il manque ; jamais dans `package.json` ; jamais lancé par `npm test`
  ni par la CI.
- Résultats et captures écrits sous `--out` (hors du dépôt par défaut : le
  dossier temporaire du système) ; captures à 1 440 × 900, nommées par
  identifiant de §11, pour la comparaison côte à côte avec les maquettes ; la
  recette mesure les valeurs de §11 (police réellement chargée par
  `document.fonts.check`, tailles, couleurs, contrastes des textes et des
  formes dans les deux thèmes) et l'absence de toute requête hors du site
  (contrôle 14). Le rapport d'une tranche va dans la description de la PR, pas
  dans le dépôt.
- Documentation : P1.1 met à jour `CLAUDE.md` (exception des polices à côté de
  `marked`, `okf-shapes.js` source unique des formes), `NOTICE`, le README du
  viewer et celui du harnais (section « recette ») ; P3 met à jour `CLAUDE.md`
  (`graph.html`, règle de déterminisme de `okf-sim.js`) et le README ; chaque
  tranche ajoute sa ligne au `CHANGELOG`.

## 13. Points ouverts pour le propriétaire

Chacun avec une recommandation ; le texte ci-dessus applique la recommandation
en attendant la décision.

1. **Libellé des lignes de l'explorateur** (E8) : la maquette montre le nom du
   segment, P1 montre le titre. Recommandation : le segment (fidélité, et le
   titre est déjà au H1 et dans la palette), titre en infobulle.
2. **Nom de la page du graphe** (§12.3) : `graph.html` (A17) est aussi la page
   d'un concept racine `graph`, id valide et plausible. Recommandation :
   `graph-1.html`, `graph-2.html`… en cas de collision, plutôt que de refuser le
   rendu de ces bundles.
3. **Polices en `file://` sous Firefox** : Firefox peut refuser une police
   chargée depuis un dossier parent du document en `file://` (politique
   d'origine stricte des fichiers), donc depuis `../assets/fonts/` pour toute
   page profonde. Recommandation : le vérifier en premier dans P1.1 ; si c'est
   bloqué, servir les polices en URI `data:` dans une feuille distincte
   `assets/okf-fonts.css` (+33 % de taille, un seul fichier mis en cache),
   budget de §11.0 recalculé.
4. **Glyphes proches hérités de la maquette** : le drapeau « périmé » est un
   triangle comme le type de rang 3, et le point de confiance humaine ressemble
   au cercle du rang 0. Recommandation : garder (couleur, taille, colonne et
   légende les distinguent).
5. **Taille de l'index v2** (§3.6) : +127 Ko estimés (+45 %) pour des
   descriptions de 200 points de code. Recommandation : garder 200 ; ne
   descendre (par exemple à 120) que si la mesure de P1.1 dépasse 450 Ko.
6. **Repli du frontmatter** (C6) : quatre entrées visibles hors champs déjà
   montrés. Recommandation : garder cette règle, simple et fidèle à la maquette.
