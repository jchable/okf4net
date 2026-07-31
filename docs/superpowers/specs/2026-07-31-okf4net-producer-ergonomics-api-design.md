# Design — API d'ergonomie producer pour OKF4net (Provenance.ToYaml, ConceptId.Slugify, OkfDocumentBuilder)

- **Date** : 2026-07-31
- **Statut** : design validé en brainstorming, prêt pour plan d'implémentation
- **Contexte amont** : identifié en évaluant un producer OKF externe (`tommypacker/okf-generator`, TS) comme prior art, puis en brainstormant un producer natif OKF4net (repo-scanner → bundle OKF v0.2) dans `producers/`. Ce lot est un **prérequis** du producer, extrait en sous-projet séparé à exécuter en premier : le producer construira des `Frontmatter`/`OkfDocument` en mémoire à chaque concept généré, et bute sinon sur trois manques vérifiés par lecture directe de `src/OKF4net/`.

## 1. Objectif

`src/OKF4net` expose aujourd'hui une **API de lecture/écriture pensée pour un appelant qui manipule des chaînes** (agent function-calling via `OKF4net.Agents`, CLI) : `BundleConceptWriter.WriteConcept` prend le frontmatter en YAML déjà sérialisé, `Provenance.ParseSources` ne lit que la direction YAML→typé, et il n'existe aucun helper pour dériver un `ConceptId` valide à partir d'un titre libre. Un appelant **programmatique** qui construit un document OKF entièrement en mémoire (un producer, mais aussi tout futur consommateur similaire) doit soit contourner ces API, soit faire un aller-retour de sérialisation inutile. Ce lot ajoute l'API manquante, purement additive (zéro breaking change), pour que ce cas d'usage soit un citoyen de première classe de la bibliothèque plutôt qu'un contournement dans chaque producer.

## 2. Périmètre — les trois manques (vérifiés par lecture directe du code)

| Manque | Aujourd'hui | Preuve | Impact sans le fix |
|---|---|---|---|
| Pas de sérialisation `sources` | `Provenance.ParseSources(YamlValue?) -> IReadOnlyList<Source>` (`Provenance.cs:16`) n'a pas de direction inverse | Lu intégralement — la classe ne contient que la direction parse | Un producer qui cite ses preuves (§5.1) reconstruit à la main une `YamlSequence`/`YamlMapping` à chaque concept |
| `WriteConcept` n'accepte le frontmatter qu'en YAML string | `WriteConcept(string conceptId, string frontmatterYaml, string body)` (`BundleConceptWriter.cs:150`) parse la string en interne via `ParseFrontmatterAndMaybeStamp` | Lu intégralement — aucune surcharge typée | Un `Frontmatter` construit en mémoire doit être sérialisé (`AsMapping().ToYamlString()`) puis re-parsé par `WriteConcept` — aller-retour inutile |
| Pas de dérivation de segment `ConceptId` | `ConceptId.ValidateSegment` (`ConceptId.cs:169`) encode la règle `[A-Za-z0-9_][A-Za-z0-9_.\-]*` mais ne l'utilise que pour valider, jamais pour normaliser | Lu intégralement — pas de `Slugify`/`FromTitle` dans le fichier ni ailleurs (`grep Slug`, `Kebab`, `FromTitle`, `Slugify` → 0 résultat dans `src/`) | Chaque appelant réimplémente sa propre normalisation titre→segment, avec un risque de divergence de la règle de validation |

Un quatrième candidat (construire un lien markdown absolu entre deux `ConceptId`) a été examiné et **écarté** : `"/" + target.ToString()` est trivial côté appelant, `Links.cs` n'a besoin d'ajouter aucun helper pour ce cas.

## 3. API détaillée

### 3.1 `Provenance.ToYaml`

```csharp
namespace OKF4net;

public static class Provenance
{
    // existant : ParseSources, ParseUsageWindow

    /// <summary>Sérialise des sources §5.1 vers la séquence YAML de la direction inverse de <see cref="ParseSources"/>.</summary>
    public static YamlSequence ToYaml(IEnumerable<Source> sources);
}
```

- Une entrée `YamlMapping` par `Source`, dans l'**ordre canonique de clés** `id, resource, title, author, usage_count, last_modified` (l'ordre dans lequel `ParseSources` les lit, ligne 31-38 de `Provenance.cs`) — un champ `null` sur le `Source` est **omis** de la mapping (pas écrit comme `null` explicite), donc l'ensemble de clés présentes varie par entrée mais leur ordre relatif est toujours ce sous-ensemble de la séquence canonique.
- `sources` est **énuméré une seule fois** (le paramètre `IEnumerable<Source>` n'est pas supposé ré-énumérable — compatible avec un `yield return` paresseux) et l'**ordre d'énumération des éléments est préservé tel quel dans la `YamlSequence`** produite (pas de tri, pas de dédup — deux `Source` identiques en entrée produisent deux entrées identiques en sortie, dans l'ordre donné).
- `Source.Author` (type `Actor?`) sérialise via `Actor.Raw` (la chaîne d'origine, déjà conservée par `Actor.Parse` — pas de reconstruction depuis `Kind`/`Id`/`Producer`/`Version`).
- Propriété de round-trip attendue par les tests : `ParseSources(ToYaml(sources))` égale `sources` élément par élément, dans le même ordre (modulo l'absence des champs `null` en entrée, qui restent absents en sortie — pas de perte d'information sur les champs renseignés).
- Pas de validation ici (ex. `Resource` vide, alors que `Source.Resource` est documenté "required" dans `Provenance.cs`) : `Provenance` reste une couche de (dé)sérialisation lenient des deux côtés, la validation producer-grade reste dans `BundleValidator`/`OkfDocument.Validate`.

### 3.2 `ConceptId.Slugify`

```csharp
public sealed class ConceptId
{
    // existant : New, Parse, TryParse, FromPath, ToPath, ValidateSegment, ...

    /// <summary>Normalise une chaîne libre vers un segment valide (voir <see cref="ValidateSegment"/>).</summary>
    /// <exception cref="ConceptIdException">Le résultat, une fois normalisé et bordures ajustées, est une chaîne vide.</exception>
    public static string Slugify(string input);
}
```

Fonction **pure**, sans I/O ni état — aucune dédup (deux titres qui slugifient au même segment produisent le même résultat ; la dédup, si besoin, reste au producer appelant, qui a seul la visibilité sur les ids déjà émis pendant l'exécution en cours).

**Algorithme, entièrement déterministe** (aucune étape laissée à l'appréciation de l'implémentation) :

1. **Case-folding** : `folded = OKF4net.Internal.UnicodeCaseFold.ToLowercase(input)` — réutilise le folding Unicode déjà utilisé par `IndexGenerator` pour le tri de titres, plutôt qu'un second algorithme de minusculisation divergent (`string.ToLowerInvariant()` ne gère pas Final_Sigma ni İ, voir la doc de `UnicodeCaseFold`).
2. **Mapping caractère par caractère** (itération sur `char`, unité UTF-16 — voir note ci-dessous sur les paires de substituts) : pour chaque caractère de `folded`, le garder tel quel s'il satisfait le même prédicat que `ConceptId.IsValidLaterChar` (`char.IsAsciiLetterOrDigit(c) || c == '_' || c == '.' || c == '-'` — réutiliser le prédicat privé existant, ne pas dupliquer la règle) ; sinon le remplacer par `'-'`.
3. **Fusion des tirets consécutifs** : toute suite de 2+ `'-'` dans le résultat de l'étape 2 devient un seul `'-'`.
4. **Ajustement de la bordure de tête uniquement** : tant que le premier caractère de la chaîne ne satisfait pas `IsValidFirstChar` (`char.IsAsciiLetterOrDigit(c) || c == '_'` — donc un `'-'` ou un `'.'` en tête), le retirer. Répéter jusqu'à ce que le premier caractère soit valide ou que la chaîne soit vide. **Aucun ajustement de la bordure de fin** : un `'-'` ou un `'.'` final est un `IsValidLaterChar` valide, donc laissé tel quel (pas de sur-ingénierie cosmétique au-delà de ce que `ValidateSegment` exige réellement).
5. Si la chaîne résultante est vide → lève `ConceptIdException` (cohérent avec `New`/`Parse`, qui rejettent déjà un segment vide) — pas de placeholder silencieux. L'appelant qui veut un fallback le construit explicitement en attrapant l'exception, plutôt que `Slugify` ne décide à sa place d'une valeur de repli arbitraire.
6. Sinon, retourner la chaîne — **garantie** : le résultat passe toujours `ValidateSegment` sans exception (propriété testée, §5).

**Note paires de substituts (surrogate pairs)** : l'étape 2 opère caractère `char` par caractère (unité UTF-16), pas point de code Unicode. Un caractère hors plan de base (emoji, certains sinogrammes rares) est encodé en deux `char` de substitut, chacun non-ASCII : les deux deviennent donc deux `'-'` adjacents, fusionnés en un seul par l'étape 3. Aucune exception, aucun caractère résiduel invalide — pas besoin d'itérer par `Rune` pour ce mapping (contrairement à `UnicodeCaseFold.ToLowercase`, qui en a besoin pour le folding lui-même).

**Note ouverte — restriction ASCII, décision différée à l'amont** : la spec OKF (§2, texte amont vérifié directement) ne définit *aucune* restriction de caractères pour un concept id — c'est `ConceptId.ValidateSegment`, code déjà existant hors de ce lot, qui impose l'ASCII (`[A-Za-z0-9_][A-Za-z0-9_.\-]*`), vraisemblablement pour rester aligné avec une implémentation Rust de référence. Élargir cette règle a des effets de bord réels (normalisation Unicode NFC/NFD divergente entre systèmes de fichiers macOS et Windows/Linux affectant le round-trip `ConceptId`/`Bundle`, risque de divergence avec les golden fixtures) et est un changement bien plus large que ce lot. Décision pour **ce lot** : `Slugify` continue de replier le non-ASCII vers `'-'` (voir tableau d'exemples ci-dessus), le sujet est remonté à l'amont via une issue dédiée (voir `docs/outreach/upstream-issues/2026-07-31-concept-id-character-set-clarification.md`) et suivi dans `ROADMAP.md` — pas une décision définitive, une décision *pour l'instant*, en attendant un retour du mainteneur de la spec.

**Exemples vérifiés à la main** (à utiliser comme table de test, §5) :

| Entrée | Sortie | Remarque |
|---|---|---|
| `"My Package Name"` | `"my-package-name"` | cas nominal |
| `"  leading spaces"` | `"leading-spaces"` | espaces de tête → tirets → fusionnés → bordure de tête retirée |
| `"3D Print"` | `"3d-print"` | chiffre en tête déjà valide, aucun ajustement |
| `"café"` | `"caf-"` | `é` non-ASCII → `'-'` ; tiret final **non** retiré (étape 4 : pas de trim de fin) |
| `"my.package"` | `"my.package"` | `.` est un `IsValidLaterChar`, préservé tel quel (no-op) |
| `".hidden"` | `"hidden"` | `.` en tête invalide comme premier caractère → retiré |
| `"--double--dash--"` | `"double-dash-"` | tirets consécutifs fusionnés puis tiret de tête retiré ; tiret final conservé |
| `"already-valid_segment.ext"` | `"already-valid_segment.ext"` | déjà valide → no-op (idempotence) |
| `"!!!"` | *(lève `ConceptIdException`)* | tout le contenu invalide, rien ne survit à l'ajustement de bordure |
| `"🎉 emoji"` | `"--emoji"` → bordure retirée → `"emoji"` | paire de substituts → deux `'-'` adjacents → fusionnés → bordure de tête retirée |

### 3.3 Surcharge `BundleConceptWriter.WriteConcept`

```csharp
public sealed class BundleConceptWriter
{
    // existant : WriteConcept(string conceptId, string frontmatterYaml, string body)

    /// <summary>Comme <see cref="WriteConcept(string, string, string)"/>, mais prend un <see cref="Frontmatter"/> déjà construit — évite l'aller-retour sérialisation/re-parsing pour un appelant programmatique.</summary>
    public string WriteConcept(string conceptId, Frontmatter frontmatter, string body);
}
```

- Même pipeline exact que la surcharge existante (validation producer-grade, verrou par bundle, garde anti-reparse-point, stamp `generated` si `AutoStampGenerated`) — implémentée en réutilisant `BuildValidatedContent(YamlValue, string)` directement sur `frontmatter.AsMapping()` plutôt qu'en repassant par une sérialisation/parsing YAML.
- Ne change ni ne duplique aucune des garanties de concurrence/sécurité déjà documentées sur la surcharge string — un seul chemin de validation partagé entre les deux surcharges.

### 3.4 `OkfDocumentBuilder` (API fluent)

```csharp
public sealed class OkfDocumentBuilder
{
    public static OkfDocumentBuilder ForType(string type);

    public OkfDocumentBuilder Title(string title);
    public OkfDocumentBuilder Description(string description);
    public OkfDocumentBuilder Resource(string resource);
    public OkfDocumentBuilder Tags(params string[] tags);
    public OkfDocumentBuilder AddTags(params string[] tags);
    public OkfDocumentBuilder AddSource(string resource, string? id = null, string? title = null, Actor? author = null, long? usageCount = null, string? lastModified = null);
    public OkfDocumentBuilder Extension(string key, YamlValue value); // clé producer-defined hors §4.1 bien connues
    public OkfDocumentBuilder Body(string body); // obligatoire avant Build() -- voir plus bas

    public OkfDocument Build();
}
```

**Conventions de nommage** : setters nommés par le champ qu'ils posent (`Title`, `Description`, `Resource`), sans préfixe `Set`. `ForType(string)` est le point d'entrée statique obligatoire (`type` est le seul champ requis par la conformité spec §11) ; toutes les méthodes d'instance sont optionnelles et peuvent être chaînées dans n'importe quel ordre.

**Sémantique précise par méthode** (pour ne rien laisser à l'interprétation) :

- **`Title`/`Description`/`Resource`** : chaque appel écrase la valeur précédente pour cette clé (dernier appel gagne) ; jamais appelée → clé absente du frontmatter (pas de valeur vide/`null` écrite).
- **`Tags(params string[] tags)`** / **`AddTags(params string[] tags)`** : deux méthodes distinctes avec des sémantiques différentes, confirmées explicitement (pas de comportement implicite) :
  - `Tags(...)` **remplace toute la liste accumulée jusque-là**, y compris ce qu'un `AddTags(...)` précédent avait ajouté — **quel que soit l'ordre d'appel réel** entre les deux méthodes dans la chaîne fluent. Appeler `Tags(...)` après un ou plusieurs `AddTags(...)` efface ces ajouts, pas seulement un `Tags(...)` antérieur.
  - `AddTags(...)` **accumule** — appelable plusieurs fois, chaque appel ajoute ses arguments à la liste interne, dans l'ordre des appels.
  - Dans les deux cas : `YamlSequence` de `YamlString` dans l'ordre final des éléments accumulés, sans tri ni dédoublonnage. Ni l'une ni l'autre jamais appelée (ou appelées avec zéro argument au total) → clé `tags` absente (pas de séquence vide écrite).
- **`AddSource(...)`** : **accumule** — appelable plusieurs fois, chaque appel ajoute une entrée à une liste interne, dans l'ordre des appels. Au `Build()`, cette liste est passée à `Provenance.ToYaml` (§3.1) pour produire la clé `sources`. Jamais appelée → clé `sources` absente. Ne valide pas `resource` (même principe de non-validation qu'en §3.1).
- **`Extension(string key, YamlValue value)`** : échappatoire brute, équivalente à `frontmatter.Set(key, value)` — **aucune garde de collision** avec les clés déjà couvertes par un setter typé (`type`/`title`/`description`/`resource`/`tags`/`sources`). Voir l'ordre d'application ci-dessous pour ce que ça implique en cas de collision : c'est un choix délibéré (pas de logique de détection de conflit à écrire/maintenir), pas un oubli.
- **`Body(string body)`** : dernier appel gagne. **Obligatoire** : si `Build()` est appelé sans qu'aucun `Body(...)` n'ait été fait, il lève `InvalidOperationException` (message clair du type `"OkfDocumentBuilder: Body(...) must be called before Build()."`) plutôt que de produire silencieusement un corps vide — symétrique avec `ForType(...)`, seul autre appel obligatoire du builder. Un appelant qui veut vraiment un corps vide appelle explicitement `Body(string.Empty)` ou `Body("")`.

**Ordre des clés produites par `Build()`** : fixe, **pas** l'ordre d'appel des méthodes du builder — `type, title, description, resource, tags, sources`, puis les clés `Extension(...)` dans leur ordre d'appel. Cet ordre est le sous-ensemble, dans le même ordre relatif, de l'ordre déjà déclaré par `Frontmatter.KnownKeys` (`Frontmatter.cs:26-34`) — pas un ordre inventé pour ce lot. Seules les clés dont le champ correspondant a été renseigné apparaissent (voir omissions ci-dessus) ; `type` est toujours présent (seul champ obligatoire).

**Conséquence de collision** : parce que les clés `Extension(...)` sont insérées *après* les six clés bien connues au moment de `Build()` (voir l'ordre fixe ci-dessus), un appel `Extension("tags", ...)` — par exemple — écrase toujours ce que `Tags(...)` avait produit, **quel que soit l'ordre d'appel des deux méthodes dans la chaîne fluent**. C'est le comportement attendu et documenté, pas un bug : `Build()` applique un ordre fixe, pas l'ordre d'appel réel du code appelant.

**`Build()` est idempotent et non destructif** : peut être appelé plusieurs fois sur le même builder ; chaque appel reconstruit un nouveau `OkfDocument` reflétant l'état accumulé au moment de l'appel, sans consommer/invalider le builder. `Build()` **ne valide pas** (`OkfDocument.Validate()` reste explicite, appelé par `WriteConcept` ou par l'appelant s'il veut échouer plus tôt).

**Thread-safety** : `OkfDocumentBuilder` n'est pas thread-safe et n'a pas besoin de l'être — chaque site de génération de concept construit sa propre instance (pas d'état statique/partagé), la sécurité de concurrence du système reste entièrement portée par `BundleConceptWriter` (§3.3), pas par le builder.

Usage typique :

```csharp
var doc = OkfDocumentBuilder
    .ForType("CLI Tool")
    .Title("okfgen")
    .Description("Generates OKF bundles for repositories")
    .Tags("cli", "okf")
    .AddSource(resource: "README.md", title: "README")
    .AddSource(resource: "package.json")
    .Body("# Summary\n...")
    .Build();

writer.WriteConcept(conceptId, doc.Frontmatter, doc.Body); // §3.3
```

## 4. Hors scope

- Aucune dédup de `ConceptId` (ni dans `Slugify`, ni ailleurs) — état d'exécution, reste au producer.
- Aucun helper de construction de lien markdown (§2, écarté).
- Aucune validation additionnelle dans `OkfDocumentBuilder.Build()` au-delà de ce qu'`OkfDocument.Validate()` fait déjà.
- Aucune sérialisation de `usage_window` (le sibling de `sources`, §5.1) — seul `sources` est dans le périmètre vérifié au §2 ; si un futur besoin apparaît, il suivra le même patron que `Provenance.ToYaml` mais c'est un ajout distinct, pas supposé inclus ici.
- **Élargissement de `ConceptId.ValidateSegment` au-delà de l'ASCII** : explicitement hors scope de ce lot (voir §3.2) — projet séparé, plus important, si jamais entrepris. `Slugify` continue de replier tout caractère non-ASCII vers `'-'` tant que `ValidateSegment` n'a pas changé.
- Le producer lui-même (`producers/OkfProducer`) : spec séparée, à écrire après ce lot.

## 5. Plan de test

Tests xunit ajoutés dans `tests/OKF4net.Tests/` (même solution, même CI — ce sont des ajouts à `src/OKF4net`, pas un projet séparé) :

- `Provenance.ToYaml` : round-trip `ParseSources(ToYaml(sources)) == sources`, ordre préservé, sur un jeu de `Source` couvrant tous les champs optionnels présents/absents ; vérifie qu'un champ `null` n'apparaît pas dans la `YamlMapping` produite (§3.1, décidé) ; vérifie la sérialisation `Author` via `Actor.Raw` pour les trois `ActorKind` ; vérifie que l'ordre des clés par entrée suit `id, resource, title, author, usage_count, last_modified` ; vérifie l'énumération unique (ex. un `IEnumerable<Source>` construit via `yield return` avec un compteur d'appels, pour s'assurer qu'il n'est énuméré qu'une fois).
- `ConceptId.Slugify` : rejoue exactement la table d'exemples du §3.2 (`"My Package Name"` → `"my-package-name"`, `"café"` → `"caf-"`, `".hidden"` → `"hidden"`, `"--double--dash--"` → `"double-dash-"`, `"!!!"` → lève `ConceptIdException`, la paire de substituts emoji, etc.) comme table de test `[Theory]`/`[InlineData]` ; propriété additionnelle : pour toute sortie non-exception, `ValidateSegment` ne lève pas ; idempotence sur une entrée déjà valide (no-op, testé explicitement).
- `BundleConceptWriter.WriteConcept(string, Frontmatter, string)` : même suite de cas que la surcharge string existante (nouveau concept, mise à jour, id réservé `index`/`log`, reparse-point, `AutoStampGenerated`) rejouée sur la nouvelle surcharge — pas de nouveaux comportements à tester, seulement l'équivalence avec la surcharge existante sur les mêmes entrées.
- `OkfDocumentBuilder` :
  - Construit un document avec tous les champs (`ForType`, `Title`, `Description`, `Resource`, `Tags`, `AddSource` x2, `Extension`, `Body`), vérifie le `Frontmatter`/`Body` produits et l'ordre exact des clés (`type, title, description, resource, tags, sources`, puis les clés `Extension` dans leur ordre d'appel).
  - `Build()` sans `Description`/`Title` (juste `ForType` + `Body`) produit un document qui échoue `OkfDocument.Validate()` (puisque le builder ne valide pas lui-même) mais passe `ValidateConformance()` (seul `type` requis, §11).
  - `Build()` sans `Body(...)` appelé → lève `InvalidOperationException`.
  - `Tags("a","b")` suivi de `Tags("c")` → seul `"c"` dans le résultat (écrasement, pas cumul).
  - `AddTags("a")` puis `AddTags("b")` → `["a","b"]` dans le résultat (cumul, ordre d'appel).
  - `AddTags("a")` puis `Tags("b")` → seul `"b"` dans le résultat (`Tags` efface aussi ce qu'`AddTags` avait posé, peu importe l'ordre d'appel — cas explicitement demandé).
  - `Tags("a")` puis `AddTags("b")` → `["a","b"]` (l'inverse : `AddTags` après `Tags` accumule sur la base posée par `Tags`).
  - `Extension("tags", ...)` appelé après `Tags(...)` → la valeur d'`Extension` gagne (ordre fixe, §3.4).
  - `Build()` appelé deux fois sur le même builder (sans modification entre les deux appels) → deux `OkfDocument` distincts mais structurellement égaux (non-destructif, idempotent).

Suite complète doit rester verte, `dotnet format --verify-no-changes` clean, aucune régression sur les goldens (aucun de ces ajouts ne touche un chemin existant testé par `tests/fixtures/`).

## 6. CHANGELOG / docs

Entrée sous `Added` dans `[Unreleased]` : nouvelle API producer-facing (`Provenance.ToYaml`, `ConceptId.Slugify`, surcharge `BundleConceptWriter.WriteConcept`, `OkfDocumentBuilder`) — motivée par le producer `producers/OkfProducer` à venir, mais utilisable indépendamment par tout appelant programmatique.

## 7. Contraintes respectées

- Zéro dépendance tierce nouvelle (BCL only) — ces ajouts vivent dans `src/OKF4net`, soumis à la règle zero-dependency stricte.
- Zéro breaking change : toutes les nouvelles API sont additives (nouvelle méthode statique, nouvelle surcharge, nouveau type) ; aucune signature existante modifiée.
- SPDX header, namespace file-scoped, XML doc sur l'API publique, nullable enabled — conventions du repo.
- Pas de nouvelle golden fixture requise (ces ajouts ne touchent aucun comportement couvert par `tests/fixtures/`).
- **Emplacement des fichiers** : `Provenance.ToYaml` dans `src/OKF4net/Provenance.cs` (existant) ; `ConceptId.Slugify` dans `src/OKF4net/ConceptId.cs` (existant, réutilise les prédicats privés `IsValidFirstChar`/`IsValidLaterChar` déjà dans ce fichier) ; la surcharge `WriteConcept(string, Frontmatter, string)` dans `src/OKF4net/BundleConceptWriter.cs` (existant) ; `OkfDocumentBuilder` dans un **nouveau fichier** `src/OKF4net/OkfDocumentBuilder.cs`.
