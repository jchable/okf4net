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

- Une entrée par `Source`, mêmes clés que celles lues par `ParseSources` (`id`, `resource`, `title`, `author`, `usage_count`, `last_modified`).
- Un champ optionnel `null` sur le `Source` est **omis** de la `YamlMapping` (pas écrit comme `null` explicite) — sortie compacte, cohérente avec le style des fixtures existantes.
- `Source.Author` (type `Actor?`) sérialise via `Actor.Raw` (la chaîne d'origine, déjà conservée par `Actor.Parse` — pas de reconstruction depuis `Kind`/`Id`/`Producer`/`Version`).
- Propriété de round-trip attendue par les tests : `ParseSources(ToYaml(sources))` égale `sources` élément par élément (modulo l'absence des champs `null` en entrée, qui restent absents en sortie — pas de perte d'information sur les champs renseignés).
- Pas de validation ici (ex. `Resource` vide) : `Provenance` reste une couche de (dé)sérialisation lenient, la validation producer-grade reste dans `BundleValidator`/`OkfDocument.Validate`.

### 3.2 `ConceptId.Slugify`

```csharp
public sealed class ConceptId
{
    // existant : New, Parse, TryParse, FromPath, ToPath, ValidateSegment, ...

    /// <summary>Normalise une chaîne libre vers un segment valide (voir <see cref="ValidateSegment"/>) : minuscule ASCII, tout caractère hors charset devient '-', les '-' consécutifs sont fusionnés, bordures ajustées pour respecter la règle de premier caractère.</summary>
    public static string Slugify(string input);
}
```

- Fonction **pure**, sans I/O ni état : mêmes entrée → même sortie, aucune notion de dédup (deux titres qui slugifient au même segment produisent le même résultat — la dédup, si besoin, reste au producer appelant, qui a seul la visibilité sur les ids déjà émis pendant l'exécution en cours).
- Réutilise la même définition de charset que `ValidateSegment` (idéalement en factorisant les prédicats `IsValidFirstChar`/`IsValidLaterChar` existants plutôt qu'en dupliquant la règle) — pour que `Slugify` et `ValidateSegment` ne puissent jamais diverger.
- Cas limite : une entrée qui slugifie vers une chaîne vide (ex. entrée entièrement faite de caractères invalides) **lève `ConceptIdException`**, cohérent avec `New`/`Parse` qui rejettent déjà un segment vide — pas de placeholder silencieux. L'appelant qui veut un fallback (ex. un id généré) le construit explicitement en attrapant l'exception, plutôt que `Slugify` ne décide à sa place d'une valeur de repli arbitraire.

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
    public OkfDocumentBuilder AddSource(string resource, string? id = null, string? title = null, Actor? author = null, long? usageCount = null, string? lastModified = null);
    public OkfDocumentBuilder Extension(string key, YamlValue value); // clé producer-defined hors §4.1 bien connues
    public OkfDocumentBuilder Body(string body);

    public OkfDocument Build();
}
```

- Convention retenue : setters nommés par le champ qu'ils posent (`Title`, `Description`, `Resource`), sans préfixe `Set`, sauf pour les collections qui s'accumulent (`Tags(...)` remplace la liste en un appel — usage attendu ponctuel ; `AddSource(...)` ajoute une entrée à la fois, appelable plusieurs fois).
- `ForType(string)` est le point d'entrée obligatoire (`type` est le seul champ requis par la conformité spec §11) ; les autres méthodes sont optionnelles.
- `Build()` assemble un `Frontmatter` (via `Set` répétés + `Provenance.ToYaml` du §3.1 pour les sources accumulées) et un `OkfDocument` — **ne valide pas** (`OkfDocument.Validate()` reste explicite, appelé par `WriteConcept` ou par l'appelant s'il veut échouer plus tôt).
- Mutable en interne (accumulation dans un `YamlMapping`/liste privée), mais chaque méthode retourne `this` — pas de copie défensive à chaque appel : un `OkfDocumentBuilder` n'est pas destiné à être partagé/réutilisé après `Build()`.
- Usage typique :

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
- Le producer lui-même (`producers/OkfProducer`) : spec séparée, à écrire après ce lot.

## 5. Plan de test

Tests xunit ajoutés dans `tests/OKF4net.Tests/` (même solution, même CI — ce sont des ajouts à `src/OKF4net`, pas un projet séparé) :

- `Provenance.ToYaml` : round-trip `ParseSources(ToYaml(sources)) == sources` sur un jeu de `Source` couvrant tous les champs optionnels présents/absents ; vérifie qu'un champ `null` n'apparaît pas dans la `YamlMapping` produite ; vérifie la sérialisation `Author` via `Actor.Raw` pour les trois `ActorKind`.
- `ConceptId.Slugify` : table de cas (espaces, majuscules, caractères Unicode/spéciaux, tirets consécutifs, entrée déjà valide — no-op) ; chaque sortie non vidée par le cas limite doit passer `ValidateSegment` sans exception (propriété : `Slugify` produit toujours un segment valide) ; cas de l'entrée entièrement invalide (comportement du §3.2 à trancher dans le plan).
- `BundleConceptWriter.WriteConcept(string, Frontmatter, string)` : même suite de cas que la surcharge string existante (nouveau concept, mise à jour, id réservé `index`/`log`, reparse-point, `AutoStampGenerated`) rejouée sur la nouvelle surcharge — pas de nouveaux comportements à tester, seulement l'équivalence avec la surcharge existante sur les mêmes entrées.
- `OkfDocumentBuilder` : construit un document avec tous les champs, vérifie le `Frontmatter`/`Body` produits ; vérifie qu'un appel `Build()` sans `Description`/`Title` (juste `ForType`) produit un document qui échoue `OkfDocument.Validate()` (puisque le builder ne valide pas lui-même) mais passe `ValidateConformance()` (seul `type` requis, §11).

Suite complète doit rester verte, `dotnet format --verify-no-changes` clean, aucune régression sur les goldens (aucun de ces ajouts ne touche un chemin existant testé par `tests/fixtures/`).

## 6. CHANGELOG / docs

Entrée sous `Added` dans `[Unreleased]` : nouvelle API producer-facing (`Provenance.ToYaml`, `ConceptId.Slugify`, surcharge `BundleConceptWriter.WriteConcept`, `OkfDocumentBuilder`) — motivée par le producer `producers/OkfProducer` à venir, mais utilisable indépendamment par tout appelant programmatique.

## 7. Contraintes respectées

- Zéro dépendance tierce nouvelle (BCL only) — ces ajouts vivent dans `src/OKF4net`, soumis à la règle zero-dependency stricte.
- Zéro breaking change : toutes les nouvelles API sont additives (nouvelle méthode statique, nouvelle surcharge, nouveau type) ; aucune signature existante modifiée.
- SPDX header, namespace file-scoped, XML doc sur l'API publique, nullable enabled — conventions du repo.
- Pas de nouvelle golden fixture requise (ces ajouts ne touchent aucun comportement couvert par `tests/fixtures/`).
