# Viewer interactif (`okf-render`) — design

Date : 2026-10-06
Statut : proposé — révision 3 (2026-10-06) : revue externe intégrée
(verdict « prêt après corrections nommées »), arbitrages du propriétaire rendus
(section 10)

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
<https://claude.ai/artifact/CwM9LAYtiCdBeY1rLRDigz>. Direction retenue :
**A + B + palette de C**, décrites en texte en section 2 pour qu'un lecteur sans
accès au lien puisse évaluer la spec. En cas d'écart, la spec fait foi.

## 2. Périmètre

Dans le périmètre :

- **A — page à trois zones** (écran par défaut) : explorateur en arbre à gauche
  (repliable, concept courant surligné, filtre par nom, pastilles du palier de
  confiance et de péremption), page au centre (contenu inchangé), panneau droit
  (sommaire, graphe local 1 ou 2 sauts, « Referenced by »).
- **B — graphe global** (bouton depuis A) : écran plein cadre, facettes (type,
  palier de confiance, péremption, tags), tiroir de détail du concept
  sélectionné, pan/zoom, nœuds glissables.
- **Palette « Jump to »** (bouton visible, raccourcis Ctrl K et `/`) : saut vers
  un concept par correspondance sur le titre, l'id et les tags (§4.6).
- **Bascule de thème** clair/sombre, par défaut la préférence système.
- Encodage des types par **forme et couleur** (cercle, carré, losange, triangle,
  anneau), jamais la couleur seule.

Hors périmètre, par décision :

- Recherche plein texte. Elle exigerait le scoreur `ConceptSearch`, qui n'est pas
  dupliqué en JS (`CLAUDE.md`) ; la palette ne regarde que titre/id/tags et ne
  lit pas le corps.
- Tout serveur (`okf serve` n'existe pas et n'est pas prévu), toute édition.
- Toute dépendance JS nouvelle : le viewer n'embarque que `marked`.

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
- ordre : `ConceptId.CompareTo`.

`ConceptAudit.Run` reçoit une **horloge explicite** (`FixedClock`) : seul le
palier de confiance est lu dans le résultat, la péremption étant évaluée dans le
navigateur (§4.4), et la projection doit rester pure.

Les changements se limitent à `src/OKF4net.Viewer/` (projection, écriture,
assets), à `tools/viewer-security-check/`, aux tests du viewer, au smoke test AOT
de `ci.yml`, et à la documentation (`CLAUDE.md`, README, ROADMAP).
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
  palier de confiance, `staleAfterMs`) ; un concept est désigné partout par son
  **indice** dans ce tableau.
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

## 4. JavaScript

### 4.1 Fichiers et chargement

- **Contrat de `viewer.js` inchangé.** La ROADMAP prévoit que l'extension VS Code
  réutilise `viewer.js` tel quel (payload `{ body, links }`). Le nouveau code vit
  dans des fichiers séparés sous `Assets/`, avec **son propre amorçage**,
  indépendant de celui du corps ; `viewer.js` ne dépend d'aucun nouveau global.
- **Scripts classiques, pas de modules ES** (bloqués en `file://`). Chaque
  fichier expose un global, chargeable par le harnais Node comme `run.js` charge
  déjà `viewer.js`.

### 4.2 Simulation du graphe global (module maison)

Module pur, sans DOM :

- entrée triée (§3.1) ; graine dérivée de cet ordre ; ni `Math.random` ni
  horloge ;
- **primitives restreintes** à `+ − × ÷` et `Math.sqrt`, dont ECMAScript fixe
  l'arrondi ; `Math.sin/cos/exp/pow/atan2` sont approchées selon le moteur ;
- **ordre des accumulations fixé** (nœuds par indice, cellules de grille dans un
  ordre fixe) ;
- **indépendance au découpage en frames** : une tranche s'arrête toujours entre
  deux itérations complètes, jamais au milieu ; l'état après k itérations est le
  même quel que soit le découpage ;
- **deux bornes** : un nombre maximal d'itérations, et un budget de travail qui
  compte les **paires candidates visitées** (pas seulement les forces
  calculées), avec un plafond par cellule, plus les **interactions de ressorts** ;
  chaque tranche a son propre budget ;
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
  accessible.

### 4.4 Péremption (A2, A9)

- L'index porte `staleAfterMs` : `Lifecycle.StaleAfter` converti en
  millisecondes depuis l'epoch Unix, **arrondi vers le haut** au milliseconde
  entier. Comme `Date.now()` est un entier de millisecondes,
  `Date.now() >= staleAfterMs` équivaut exactement à `now >= StaleAfter`
  (`Lifecycle.IsStale`). Une chaîne ISO ne convient pas : comparée à un nombre
  elle donne `NaN`, et `Date.parse` perd les fractions sous la milliseconde
  (`2026-10-06T00:00:00.0001000Z` serait périmé en JS à `.000Z` et pas en C#).
- L'affichage de la date reprend `Lifecycle.StaleAfterDate`, préparé en C#.
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
chemins de l'index via le résolveur (§3.5).

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
  doublon.
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
  **dans les deux thèmes**.
- Vrais `<button>`/`<a>`/`<input>` étiquetés partout.

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
   jamais chronométré**) ; changements rapides de filtre et annulation.
2. **Déterminisme** : même entrée → même sortie sur deux exécutions **et sous
   des découpages en frames différents** ; contrôle statique qu'aucune fonction
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

Côté xunit : la projection `SiteModel` → index (ordre `ConceptId.CompareTo`,
arbre destination + enfants, arêtes fusionnées et fantômes, palier issu de
`ConceptAudit` sous horloge fixe, `staleAfterMs` aux frontières, sérialisation
hostile), l'écriture d'`assets/okf-index.js` par `HtmlWriter`, et la racine
déclarée par chaque page. Les tests de marqueurs source dans `ViewerAssetsTests`
disent dans leur commentaire qu'ils sont de simples vérifications de surface.

**Hors de portée du harnais** : jsdom ne fait pas de layout (`getBBox` absent),
donc fluidité, géométrie, contraste, absence de flash et raccourcis navigateur
relèvent de la **recette manuelle** (A14) : une recette écrite, exécutée à chaque
tranche, en `file://`, sur `acme_retail` et sur le bundle d'OKF4net, dans
Chrome, Edge et Firefox (Safari en meilleur effort).

Aucun golden ne couvre `okf-render` (`tests/fixtures/golden/` couvre audit, fmt,
graph, info, validate, verify) : pas d'arbitrage golden attendu. `graph.dot`
fige en revanche l'ensemble d'arêtes de `okf graph` (A3).

## 8. Contrat clavier et focus

- **Palette** : dialogue modal (modèle WAI-ARIA *dialog modal*). À l'ouverture,
  focus dans le champ ; Tab et Maj+Tab restent dans le dialogue ; Échap ferme ;
  flèches haut/bas déplacent l'option active, Entrée ouvre ; nombre de résultats
  annoncé (région live) ; si un filtrage supprime l'option active, la sélection
  passe à la première option ; à la fermeture, focus rendu au déclencheur.
- **Raccourcis** : un **bouton visible** ouvre toujours la palette. Ctrl K et `/`
  sont captés seulement si aucun champ éditable n'a le focus, sans autre
  modificateur (Alt, Meta), hors composition IME (`isComposing`) ;
  `preventDefault` uniquement quand le raccourci est capté. Ctrl K est aussi un
  raccourci de Chrome et Firefox, `/` de Firefox : la recette vérifie le
  comportement réel.
- **Graphe** : un seul arrêt de tabulation pour tout le graphe (tabindex
  itinérant), flèches pour passer d'un nœud à ses voisins, Entrée pour ouvrir ;
  zoom, dézoom et recentrage par boutons ; la liste équivalente remplace le
  glisser-déposer pour qui ne peut pas glisser.
- **Explorateur** : commandes distinctes ouvrir/déplier (§3.2), état
  replié/déplié annoncé.
- **Thème** : bouton à nom d'action explicite ou état annoncé (`aria-pressed`).

## 9. Livraison en tranches indépendantes

- **P1** — mesure du schéma d'index (§3.6), puis coque A sauf le graphe :
  explorateur, palette, bascule de thème, `assets/okf-index.js`, panneau droit
  avec sommaire, fragments (§5) et backlinks. P1 est livrable seul : contrat
  d'index fixé, amorçage indépendant de celui du corps, test du viewer historique
  sans les nouveaux scripts (§7, contrôle 8).
- **P2** — graphe local, ajouté au panneau droit.
- **P3** — graphe global, simulation, facettes.

Chaque tranche a son propre plan et sa recette. La mise à jour de `CLAUDE.md`
(description du viewer, « no full-text search », exception A2), du README et de
la ROADMAP (A4) accompagne P1.

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

Choix de rédaction non arbitrés, contestables : voisinage local non orienté
(§4.3) ; fusion des arêtes répétées avec compte (§3.3) ; règle exacte de
dérivation des ids de titres renvoyée au plan (§5).
