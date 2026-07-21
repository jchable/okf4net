# OKF4net Phase 1 — Core + Tests + CLI + suppression du Rust

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Porter la bibliothèque Rust `okf` (parseur OKF v0.1 : YAML subset, documents, bundle, index, log, validation) et son CLI en .NET 10 sous le nom OKF4net, prouver la parité par les tests portés + golden tests, puis supprimer tout le code Rust.

**Architecture:** Solution multi-projets : `src/OKF4net/` (lib core, zéro dépendance NuGet), `src/OKF4net.Cli/` (binaire `okf`, Native AOT, args parsés à la main), `tests/OKF4net.Tests/` (xUnit, port 1:1 des 48 tests Rust + golden tests de parité). Le code Rust existant est LA spécification : chaque tâche référence le fichier Rust à porter ; les tests Rust portés définissent le comportement observable.

**Tech Stack:** .NET 10 (`net10.0`), C# 14, xUnit v3. Aucune dépendance NuGet dans `src/OKF4net/` et `src/OKF4net.Cli/`.

## Global Constraints

- `src/OKF4net/OKF4net.csproj` et `src/OKF4net.Cli/OKF4net.Cli.csproj` : **zéro `PackageReference`**. Seul le projet de tests référence xUnit.
- TFM : `net10.0` partout ; `<Nullable>enable</Nullable>`, `<TreatWarningsAsErrors>true</TreatWarningsAsErrors>`.
- Namespace racine : `OKF4net` (couche YAML : `OKF4net.Yaml`).
- Comparaisons de chaînes : `StringComparison.Ordinal` (ou `OrdinalIgnoreCase` uniquement là où le Rust fait un tri case-insensitive, cf. `build_index_text`).
- Encodage : UTF-8 sans BOM en écriture de fichiers ; fins de ligne : reproduire exactement ce que fait le Rust (les chaînes générées utilisent `\n`, jamais `Environment.NewLine`).
- Chemins : les ids de concepts utilisent toujours `/` comme séparateur, y compris sous Windows (`ConceptId.FromPath` normalise `\` → `/`).
- Fidélité : à comportement observable égal avec le Rust — mêmes valeurs de retour, mêmes messages d'erreur, mêmes sorties texte. En cas de doute pendant une tâche, ouvrir le fichier Rust cité en référence : il fait foi.
- Le code Rust (`src/*.rs`, `tests/*.rs`, `Cargo.toml`, `Cargo.lock`) ne doit être supprimé **qu'en Tâche 15**, après la parité golden (Tâche 14).
- Commits : préfixes `feat:`/`test:`/`chore:`, un commit par tâche minimum.

---

### Task 1: Scaffolding solution + `YamlValue`/`YamlMapping`

**Files:**
- Create: `OKF4net.sln`, `Directory.Build.props`, `src/OKF4net/OKF4net.csproj`, `tests/OKF4net.Tests/OKF4net.Tests.csproj`
- Create: `src/OKF4net/Yaml/YamlValue.cs`, `src/OKF4net/Yaml/YamlMapping.cs`
- Test: `tests/OKF4net.Tests/Yaml/YamlValueTests.cs`

**Interfaces:**
- Consumes: rien (première tâche).
- Produces (référence Rust : [src/yaml/mod.rs](../../src/yaml/mod.rs), lignes 42–210) :

```csharp
namespace OKF4net.Yaml;

// Miroir de l'enum Rust `Value` : Null, Bool, Int, Float, Str, Seq, Map.
public abstract class YamlValue
{
    public static YamlValue Parse(string text);      // implémenté Task 2 (jette YamlParseException)
    public string ToYamlString();                     // implémenté Task 3 (délègue à YamlEmitter.Emit)
    public string? AsString();                        // YamlString → valeur, sinon null
    public bool? AsBool();
    public long? AsInt();
    public IReadOnlyList<YamlValue>? AsSequence();
    public YamlMapping? AsMapping();
    public bool IsEmptyValue { get; }                 // null, "" , seq vide, map vide (port de is_empty_value)
    public string? AsDisplayString();                 // port de as_display_string (scalaires → texte)
}

public sealed class YamlNull : YamlValue      { public static readonly YamlNull Instance; }
public sealed class YamlBool : YamlValue     { public bool Value { get; } }
public sealed class YamlInt : YamlValue      { public long Value { get; } }
public sealed class YamlFloat : YamlValue    { public double Value { get; } }
public sealed class YamlString : YamlValue   { public string Value { get; } }
public sealed class YamlSequence : YamlValue { public IReadOnlyList<YamlValue> Items { get; } }

// Ordre d'insertion préservé. Miroir de `Mapping` (List interne + index Dictionary).
public sealed class YamlMapping : YamlValue
{
    public YamlMapping();
    public int Count { get; }
    public bool IsEmpty { get; }
    public YamlValue? Get(string key);
    public bool ContainsKey(string key);
    public YamlValue? Insert(string key, YamlValue value); // retourne l'ancienne valeur (remplace SANS changer la position, cf. Rust insert)
    public YamlValue? Remove(string key);
    public IEnumerable<KeyValuePair<string, YamlValue>> Entries { get; } // ordre d'insertion
    public IEnumerable<string> Keys { get; }
}
```

Note de port : en Rust, `Mapping.iter()` rend des paires `(&Value, &Value)` mais `get`/`keys` travaillent en `&str` — les clés du sous-ensemble OKF sont toujours des scalaires chaîne. En C# on fixe la clé à `string` partout ; le parseur (Task 2) rejettera les clés non scalaires comme le fait le Rust.

- [ ] **Step 1: Créer la solution et les projets**

```powershell
dotnet new sln -n OKF4net
dotnet new classlib -n OKF4net -o src/OKF4net -f net10.0
dotnet new xunit3 -n OKF4net.Tests -o tests/OKF4net.Tests -f net10.0
dotnet sln add src/OKF4net tests/OKF4net.Tests
dotnet add tests/OKF4net.Tests reference src/OKF4net
Remove-Item src/OKF4net/Class1.cs, tests/OKF4net.Tests/UnitTest1.cs
```

Créer `Directory.Build.props` à la racine :

```xml
<Project>
  <PropertyGroup>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <LangVersion>14</LangVersion>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
</Project>
```

Vérifier que `src/OKF4net/OKF4net.csproj` ne contient **aucun** `PackageReference`.

- [ ] **Step 2: Écrire les tests qui échouent** (`tests/OKF4net.Tests/Yaml/YamlValueTests.cs`)

```csharp
using OKF4net.Yaml;

namespace OKF4net.Tests.Yaml;

public class YamlValueTests
{
    [Fact]
    public void Mapping_preserves_insertion_order()
    {
        var m = new YamlMapping();
        m.Insert("zebra", new YamlInt(1));
        m.Insert("alpha", new YamlInt(2));
        m.Insert("mike", new YamlInt(3));
        Assert.Equal(new[] { "zebra", "alpha", "mike" }, m.Keys.ToArray());
    }

    [Fact]
    public void Mapping_insert_replaces_in_place_and_returns_previous()
    {
        var m = new YamlMapping();
        m.Insert("a", new YamlInt(1));
        m.Insert("b", new YamlInt(2));
        var previous = m.Insert("a", new YamlInt(99));
        Assert.Equal(1L, ((YamlInt)previous!).Value);
        Assert.Equal(new[] { "a", "b" }, m.Keys.ToArray()); // "a" garde sa position
        Assert.Equal(99L, m.Get("a")!.AsInt());
    }

    [Fact]
    public void Mapping_remove_returns_value_and_forgets_key()
    {
        var m = new YamlMapping();
        m.Insert("k", new YamlString("v"));
        Assert.Equal("v", ((YamlString)m.Remove("k")!).Value);
        Assert.False(m.ContainsKey("k"));
        Assert.Null(m.Remove("k"));
    }

    [Fact]
    public void Scalar_accessors_return_null_on_wrong_kind()
    {
        YamlValue v = new YamlString("hello");
        Assert.Equal("hello", v.AsString());
        Assert.Null(v.AsInt());
        Assert.Null(v.AsBool());
        Assert.Null(v.AsMapping());
    }

    [Fact]
    public void IsEmptyValue_matches_rust_semantics()
    {
        Assert.True(YamlNull.Instance.IsEmptyValue);
        Assert.True(new YamlString("").IsEmptyValue);
        Assert.True(new YamlSequence([]).IsEmptyValue);
        Assert.True(new YamlMapping().IsEmptyValue);
        Assert.False(new YamlInt(0).IsEmptyValue);
        Assert.False(new YamlString("x").IsEmptyValue);
    }
}
```

- [ ] **Step 3: Vérifier l'échec** — Run: `dotnet test tests/OKF4net.Tests --filter YamlValueTests` → Expected: échec de compilation (types absents).

- [ ] **Step 4: Implémenter `YamlValue.cs` et `YamlMapping.cs`** — porter [src/yaml/mod.rs](../../src/yaml/mod.rs) (lignes 42–210) : les accesseurs `as_*`, `is_empty_value`, `as_display_string` s'y trouvent avec leur sémantique exacte. `YamlMapping` : `List<KeyValuePair<string, YamlValue>>` + `Dictionary<string, int>` d'index ; `Remove` recale les index suivants. Laisser `Parse`/`ToYamlString` en `throw new NotImplementedException()` (Tasks 2–3).

- [ ] **Step 5: Vérifier le passage** — Run: `dotnet test tests/OKF4net.Tests --filter YamlValueTests` → Expected: 5 passed.

- [ ] **Step 6: Commit** — `git add -A && git commit -m "feat: OKF4net scaffolding + YamlValue/YamlMapping"`

---

### Task 2: `YamlParser`

**Files:**
- Create: `src/OKF4net/Yaml/YamlParser.cs`, `src/OKF4net/Yaml/YamlParseException.cs`
- Modify: `src/OKF4net/Yaml/YamlValue.cs` (brancher `Parse`)
- Test: `tests/OKF4net.Tests/Yaml/YamlParserTests.cs`

**Interfaces:**
- Consumes: `YamlValue` et dérivés (Task 1).
- Produces (référence Rust : [src/yaml/parser.rs](../../src/yaml/parser.rs) — c'est le plus gros fichier du port, ~700 lignes) :

```csharp
namespace OKF4net.Yaml;

public sealed class YamlParseException : OkfException   // OkfException créée en Task 6 ? NON — créée ICI (base minimale)
{
    public int Line { get; }                            // numéro de ligne 1-based, comme YamlError.line
    public YamlParseException(int line, string message);
}

internal static class YamlParser
{
    public static YamlValue Parse(string text);         // port de parser::parse
}
// YamlValue.Parse(string) => YamlParser.Parse
```

Créer aussi ici `src/OKF4net/OkfException.cs` : `public abstract class OkfException : Exception` (base de toutes les exceptions OKF4net — référencée par les tâches 4, 6, 8).

- [ ] **Step 1: Porter les tests parse de [tests/yaml.rs](../../tests/yaml.rs)** dans `YamlParserTests.cs`. Porter tels quels (mêmes noms en PascalCase, mêmes entrées, mêmes assertions) les tests : `scalars`, `quoted_scalars`, `block_mapping`, `flow_and_block_sequences`, `nested_mappings`, `flow_mapping`, `comments_are_ignored`, `literal_block_scalar`, `folded_block_scalar`, `block_sequence_at_parent_indent`, `conservative_number_resolution`, `unterminated_flow_is_error`, `tab_indentation_is_error`. Exemple de port (extrait — porter les 13) :

```csharp
using OKF4net.Yaml;

namespace OKF4net.Tests.Yaml;

public class YamlParserTests
{
    [Fact]
    public void Block_mapping()
    {
        var v = YamlValue.Parse("type: Metric\ntitle: DAU\ncount: 42\nratio: 0.5\nok: true\nnothing: null\n");
        var m = v.AsMapping()!;
        Assert.Equal("Metric", m.Get("type")!.AsString());
        Assert.Equal(42L, m.Get("count")!.AsInt());
        Assert.True(m.Get("ok")!.AsBool());
        Assert.Same(YamlNull.Instance, m.Get("nothing"));
    }

    [Fact]
    public void Tab_indentation_is_error()
    {
        var ex = Assert.Throws<YamlParseException>(() => YamlValue.Parse("a:\n\tb: 1\n"));
        // Reprendre le message exact du Rust (voir tests/yaml.rs::tab_indentation_is_error)
    }
    // ... les 11 autres, portés depuis tests/yaml.rs avec les mêmes littéraux d'entrée
}
```

Pour chaque test Rust, reprendre **les littéraux d'entrée et les assertions à l'identique** — ils sont dans [tests/yaml.rs](../../tests/yaml.rs) lignes 14–180. Les assertions de round-trip (`roundtrip(...)`) sont différées à la Task 3 : ici, ne porter que la moitié « parse » (l'helper `roundtrip` de tests/yaml.rs fait parse → emit → parse ; en Task 2 on écrit `Parse(...)` direct).

- [ ] **Step 2: Vérifier l'échec** — Run: `dotnet test --filter YamlParserTests` → Expected: FAIL (`NotImplementedException`).

- [ ] **Step 3: Porter `parser.rs` → `YamlParser.cs`.** Structure du fichier Rust : `parse()` (dispatch block/flow), `BlockParser` (indentation, mappings/séquences block, scalaires `|` et `>`, commentaires), `FlowParser` (struct `FlowParser` avec `parse_value`, `parse_map`, `parse_seq`, `parse_flow_scalar`, `skip_ws`), résolution conservative des scalaires plains (bool/int/float/null seulement dans les formes canoniques — `01`, `1_000`, `+1` restent des chaînes). Rejets explicites avec messages : anchors (`&`/`*`), tags (`!`), directives/multi-docs (`%`, `---` interne), tabs d'indentation. Conserver le calcul du numéro de ligne pour `YamlParseException.Line`.

- [ ] **Step 4: Vérifier le passage** — Run: `dotnet test --filter YamlParserTests` → Expected: 13 passed.

- [ ] **Step 5: Commit** — `git commit -am "feat: port YAML subset parser"`

---

### Task 3: `YamlEmitter` + round-trip

**Files:**
- Create: `src/OKF4net/Yaml/YamlEmitter.cs`
- Modify: `src/OKF4net/Yaml/YamlValue.cs` (brancher `ToYamlString`)
- Test: `tests/OKF4net.Tests/Yaml/YamlRoundtripTests.cs`

**Interfaces:**
- Consumes: `YamlValue` (Task 1), `YamlParser` (Task 2).
- Produces (référence Rust : [src/yaml/emitter.rs](../../src/yaml/emitter.rs)) :

```csharp
namespace OKF4net.Yaml;
public static class YamlEmitter
{
    public static string Emit(YamlValue value);   // port de emitter::emit — sortie octet-pour-octet identique
}
```

- [ ] **Step 1: Porter les tests round-trip de [tests/yaml.rs](../../tests/yaml.rs)** : l'helper `roundtrip` + les tests `strings_needing_quotes_roundtrip`, `non_finite_and_large_floats_roundtrip`, et les assertions round-trip des tests déjà portés en Task 2 :

```csharp
using OKF4net.Yaml;

namespace OKF4net.Tests.Yaml;

public class YamlRoundtripTests
{
    // Port de l'helper Rust : parse → emit → re-parse doit rendre une valeur égale,
    // et emit(re-parse) == emit(parse) (stabilité).
    private static YamlValue Roundtrip(string src)
    {
        var v1 = YamlValue.Parse(src);
        var emitted = v1.ToYamlString();
        var v2 = YamlValue.Parse(emitted);
        Assert.Equal(emitted, v2.ToYamlString());
        return v2;
    }

    [Fact]
    public void Strings_needing_quotes_roundtrip()
    {
        // Reprendre la liste EXACTE de tests/yaml.rs:102 (chaînes ressemblant à bool/int/null,
        // deux-points, dièse, guillemets, vide, espaces de tête/queue...)
        foreach (var s in new[] { "true", "42", "null", "a: b", "# not a comment", "", " lead", "trail " })
        {
            var m = new YamlMapping();
            m.Insert("k", new YamlString(s));
            var round = Roundtrip(m.ToYamlString());
            Assert.Equal(s, round.AsMapping()!.Get("k")!.AsString());
        }
    }

    [Fact]
    public void Non_finite_and_large_floats_roundtrip()
    {
        // Porter tests/yaml.rs:151 : .nan / .inf / -.inf / grands flottants — format_float du Rust fait foi
    }
}
```

Nécessite `Equals`/`GetHashCode` structurels sur les `YamlValue` (égalité profonde, comme `PartialEq` en Rust) — les ajouter dans ce task.

- [ ] **Step 2: Vérifier l'échec** — Run: `dotnet test --filter YamlRoundtripTests` → Expected: FAIL.

- [ ] **Step 3: Porter `emitter.rs`** : `emit`, `emit_mapping`, `emit_sequence`, `emit_scalar`, `emit_string` (règles de quoting : double quote si la chaîne est ambiguë — port exact de `double_quote`), `format_float` (représentation minimale stable, `.nan`/`.inf`). Ajouter l'égalité structurelle sur les 7 types `YamlValue`.

- [ ] **Step 4: Run:** `dotnet test --filter "YamlRoundtripTests|YamlParserTests|YamlValueTests"` → Expected: tous passed.

- [ ] **Step 5: Commit** — `git commit -am "feat: port YAML emitter, structural equality, round-trip green"`

---

### Task 4: `ConceptId`

**Files:**
- Create: `src/OKF4net/ConceptId.cs`, `src/OKF4net/ConceptIdException.cs`
- Test: `tests/OKF4net.Tests/ConceptIdTests.cs`

**Interfaces:**
- Consumes: `OkfException` (Task 2).
- Produces (référence Rust : [src/concept_id.rs](../../src/concept_id.rs)) :

```csharp
namespace OKF4net;

public sealed class ConceptIdException : OkfException { public ConceptIdException(string message); }

public sealed class ConceptId : IEquatable<ConceptId>
{
    public static ConceptId Parse(string s);                       // jette ConceptIdException
    public static bool TryParse(string s, out ConceptId? id);
    public static ConceptId New(IReadOnlyList<string> segments);   // port de new()
    public static ConceptId FromPath(string bundleRoot, string path); // normalise '\' → '/', retire '.md'
    public static void ValidateSegment(string segment);            // port de validate_segment
    public IReadOnlyList<string> Segments { get; }
    public string Name { get; }                                     // dernier segment
    public ConceptId? Parent { get; }
    public string ToPath(string bundleRoot);                        // <root>/<a>/<b>.md
    public override string ToString();                              // "a/b/c"
}
```

- [ ] **Step 1: Écrire les tests.** Le Rust n'a pas de fichier de test dédié (les règles sont dans `concept_id.rs` + exercées par bundle/links) — écrire des tests unitaires depuis la doc du fichier :

```csharp
namespace OKF4net.Tests;

public class ConceptIdTests
{
    [Fact]
    public void Parse_and_tostring_roundtrip()
        => Assert.Equal("tables/users", ConceptId.Parse("tables/users").ToString());

    [Fact]
    public void Name_and_parent()
    {
        var id = ConceptId.Parse("a/b/c");
        Assert.Equal("c", id.Name);
        Assert.Equal("a/b", id.Parent!.ToString());
        Assert.Null(ConceptId.Parse("root").Parent);
    }

    [Theory]
    [InlineData("")]                 // vide
    [InlineData("a//b")]             // segment vide
    [InlineData("../b")]             // dot-segment
    [InlineData("a/./b")]
    public void Invalid_ids_throw(string bad)
        => Assert.Throws<ConceptIdException>(() => ConceptId.Parse(bad));
    // Compléter les cas invalides avec la liste exacte de validate_segment (concept_id.rs:124)

    [Fact]
    public void FromPath_strips_md_and_normalizes_separators()
    {
        var id = ConceptId.FromPath(@"C:\bundle", @"C:\bundle\tables\users.md");
        Assert.Equal("tables/users", id.ToString());
    }

    [Fact]
    public void ToPath_appends_md()
        => Assert.Equal(Path.Combine("root", "tables", "users.md"),
                        ConceptId.Parse("tables/users").ToPath("root"));
}
```

- [ ] **Step 2: FAIL** — `dotnet test --filter ConceptIdTests`
- [ ] **Step 3: Porter [src/concept_id.rs](../../src/concept_id.rs)** (144 lignes) : `new`, `parse`, `from_path`, `to_path`, `parent`, `validate_segment` (messages d'erreur identiques). Égalité + `GetHashCode` (utilisé comme clé de dictionnaire en Task 8).
- [ ] **Step 4: PASS** — `dotnet test --filter ConceptIdTests`
- [ ] **Step 5: Commit** — `git commit -am "feat: port ConceptId"`

---

### Task 5: `Frontmatter`

**Files:**
- Create: `src/OKF4net/Frontmatter.cs`
- Test: `tests/OKF4net.Tests/FrontmatterTests.cs`

**Interfaces:**
- Consumes: `YamlMapping`, `YamlValue` (Tasks 1–3).
- Produces (référence Rust : [src/frontmatter.rs](../../src/frontmatter.rs)) :

```csharp
namespace OKF4net;

public sealed class Frontmatter
{
    public static readonly string[] RequiredKeys = ["type", "title", "description", "timestamp"];
    public Frontmatter();                                  // mapping vide
    public static Frontmatter FromMapping(YamlMapping map);
    public YamlMapping AsMapping();                         // le mapping complet, ordonné
    public bool IsEmpty { get; }
    public YamlValue? Get(string key);
    public void Set(string key, YamlValue value);
    public string? Type { get; }                            // port de type_() — via AsDisplayString
    public string? Title { get; }
    public string? Description { get; }
    public string? Resource { get; }
    public string? Timestamp { get; }
    public IReadOnlyList<string> Tags { get; }              // port de tags() (liste ou scalaire unique)
    public IReadOnlyList<string> ExtensionKeys { get; }     // clés hors clés connues, port de extension_keys()
}
```

- [ ] **Step 1: Tests** — depuis la sémantique de [src/frontmatter.rs](../../src/frontmatter.rs) (les getters passent par `as_display_string` ; `tags` accepte liste OU scalaire ; `extension_keys` = clés inconnues dans l'ordre) :

```csharp
namespace OKF4net.Tests;

public class FrontmatterTests
{
    [Fact]
    public void Typed_getters_read_display_strings()
    {
        var fm = Frontmatter.FromMapping(YamlValue.Parse("type: Metric\ntitle: DAU\ncount: 42\n").AsMapping()!);
        Assert.Equal("Metric", fm.Type);
        Assert.Equal("DAU", fm.Title);
        Assert.Null(fm.Description);
    }

    [Fact]
    public void Tags_accept_list_or_single_scalar()
    {
        var list = Frontmatter.FromMapping(YamlValue.Parse("tags: [a, b]\n").AsMapping()!);
        Assert.Equal(new[] { "a", "b" }, list.Tags);
        var single = Frontmatter.FromMapping(YamlValue.Parse("tags: solo\n").AsMapping()!);
        Assert.Equal(new[] { "solo" }, single.Tags);
    }

    [Fact]
    public void Extension_keys_are_unknown_keys_in_order()
    {
        var fm = Frontmatter.FromMapping(
            YamlValue.Parse("type: T\ncustom_z: 1\ntitle: X\ncustom_a: 2\n").AsMapping()!);
        Assert.Equal(new[] { "custom_z", "custom_a" }, fm.ExtensionKeys);
        // la liste des clés « connues » exclues est dans frontmatter.rs:107 — la reprendre exactement
    }
}
```

- [ ] **Step 2: FAIL** → **Step 3: Porter `frontmatter.rs`** (112 lignes) → **Step 4: PASS** — `dotnet test --filter FrontmatterTests`
- [ ] **Step 5: Commit** — `git commit -am "feat: port Frontmatter"`

---

### Task 6: `OkfDocument` + exceptions document

**Files:**
- Create: `src/OKF4net/OkfDocument.cs`, `src/OKF4net/Errors.cs`
- Test: `tests/OKF4net.Tests/DocumentTests.cs`

**Interfaces:**
- Consumes: `Frontmatter` (Task 5), `YamlValue.Parse` (Task 2), `YamlParseException`.
- Produces (référence Rust : [src/document.rs](../../src/document.rs), [src/error.rs](../../src/error.rs)) :

```csharp
namespace OKF4net;

// Port de l'enum DocumentError (error.rs:8) — variantes → exceptions ou données :
public sealed class DocumentParseException : OkfException
{
    public DocumentParseException(string message);          // Yaml(YamlError) et UnterminatedFrontmatter
}
public sealed class DocumentValidationException : OkfException
{
    public IReadOnlyList<string> MissingKeys { get; }        // clés requises absentes/vides
    public DocumentValidationException(string message, IReadOnlyList<string> missingKeys);
}

public sealed class OkfDocument
{
    public OkfDocument(Frontmatter frontmatter, string body);
    public Frontmatter Frontmatter { get; }
    public string Body { get; }
    public static OkfDocument Parse(string text);            // jette DocumentParseException
    public static bool TryParse(string text, out OkfDocument? doc, out string? error);
    public string Serialize();                               // "---\n<yaml>---\n\n<body>" — format exact de document.rs:79
    public void Validate();                                  // strict producteur : RequiredKeys non vides
    public void ValidateConformance();                       // §9 : `type` non vide seulement
    public IReadOnlyList<ConceptLink> Links();               // délègue à LinkScanner (Task 7) — stub NotImplemented ici
    public IReadOnlyList<Citation> Citations();              // idem
}
```

- [ ] **Step 1: Porter les 9 tests de [tests/document.rs](../../tests/document.rs)** — `roundtrip_preserves_frontmatter_and_body`, `parse_no_frontmatter_treats_all_as_body`, `unterminated_frontmatter_raises`, `validate_rejects_missing_required_keys`, `validate_accepts_full_frontmatter`, `conformance_requires_only_type`, `empty_type_is_not_conformant`, `unknown_keys_are_preserved_on_roundtrip`, `empty_frontmatter_block_is_empty_mapping` — avec les littéraux d'entrée exacts du fichier Rust. Exemple :

```csharp
namespace OKF4net.Tests;

public class DocumentTests
{
    [Fact]
    public void Unknown_keys_are_preserved_on_roundtrip()
    {
        // Entrée exacte de tests/document.rs:83
        var src = "---\ntype: Metric\nx_custom: keepme\ntitle: T\n---\n\nBody.\n";
        var doc = OkfDocument.Parse(src);
        Assert.Equal(src, doc.Serialize());
    }

    [Fact]
    public void Conformance_requires_only_type()
    {
        var doc = OkfDocument.Parse("---\ntype: Note\n---\n\nx\n");
        doc.ValidateConformance();                     // ne jette pas
        Assert.Throws<DocumentValidationException>(() => doc.Validate()); // title/description/timestamp manquent
    }
    // ... les 7 autres
}
```

- [ ] **Step 2: FAIL** → **Step 3: Porter `document.rs`** (~150 lignes) : découpage frontmatter (`---` ouvrant strictement en première ligne, fermant sur sa propre ligne, sinon `UnterminatedFrontmatter`), corps, `serialize` (ordre des clés + corps préservés, octet-pour-octet), `validate`/`validate_conformance` (messages identiques). `Links()`/`Citations()` restent `NotImplementedException` jusqu'à Task 7. → **Step 4: PASS** (le test d'intégration links sera vert en Task 7)
- [ ] **Step 5: Commit** — `git commit -am "feat: port OkfDocument parse/serialize/validate"`

---

### Task 7: `LinkScanner` — liens et citations

**Files:**
- Create: `src/OKF4net/Links.cs`
- Modify: `src/OKF4net/OkfDocument.cs` (brancher `Links()`/`Citations()`)
- Test: `tests/OKF4net.Tests/LinksTests.cs`

**Interfaces:**
- Consumes: `ConceptId` (Task 4), `OkfDocument` (Task 6).
- Produces (référence Rust : [src/links.rs](../../src/links.rs)) :

```csharp
namespace OKF4net;

public enum LinkKind { Absolute, Relative, External, Anchor, Other }   // les 5 variantes du Rust

public sealed record ConceptLink(string Text, string Target, LinkKind Kind)
{
    public static LinkKind Classify(string target);        // port de Link::classify (links.rs:39)
    public ConceptId? Resolve(ConceptId source);           // port de Link::resolve — null si External/Anchor/Other,
}                                                          // cible en '/', dot-segments normalisés, non garanti d'exister

public sealed record Citation(uint Number, string? Text, string? Target, string Raw);

public static class LinkScanner
{
    public static IReadOnlyList<ConceptLink> ExtractLinks(string body);       // port de extract_links (links.rs:153)
    public static IReadOnlyList<Citation> ExtractCitations(string body);      // port de extract_citations (links.rs:295)
}
```

- [ ] **Step 1: Porter les 11 tests de [tests/links.rs](../../tests/links.rs)** : `classify_link_kinds`, `extract_inline_links`, `links_inside_code_are_ignored` (code inline ET blocs fencés ignorés), `resolve_absolute_link`, `resolve_relative_link`, `protocol_relative_url_is_external` (`//host/x` est External), `absolute_link_normalizes_dot_segments`, `external_links_do_not_resolve`, `citations_section_parsed`, `citations_stop_at_next_heading`, `document_links_and_citations_integration` — littéraux identiques au Rust.
- [ ] **Step 2: FAIL** → **Step 3: Porter `links.rs`** (~330 lignes) : scanner de liens inline `[text](target)` avec masquage du code (`` ` `` inline, blocs ``` fencés — fonctions `blank_inline_code`/`code_free_lines`), retrait des titres `(url "title")`, `is_external` (schémas `://`, `//` protocol-relative, `mailto:` etc.), résolution absolue (dot-segments) et relative (depuis le parent de la source), section `# Citations` (entrées `[n] ...`, arrêt au heading suivant). Brancher `OkfDocument.Links()`/`Citations()`.
- [ ] **Step 4: PASS** — `dotnet test --filter "LinksTests|DocumentTests"` → tous verts, y compris l'intégration.
- [ ] **Step 5: Commit** — `git commit -am "feat: port link scanner and citations"`

---

### Task 8: `Bundle` + helper `TempDir`

**Files:**
- Create: `src/OKF4net/Bundle.cs`
- Create: `tests/OKF4net.Tests/TempDir.cs`
- Test: `tests/OKF4net.Tests/BundleTests.cs`

**Interfaces:**
- Consumes: `OkfDocument`, `ConceptId`, `LinkScanner` (Tasks 4–7).
- Produces (référence Rust : [src/bundle.rs](../../src/bundle.rs)) :

```csharp
namespace OKF4net;

public sealed class BundleLoadException : OkfException { }   // racine absente / non-répertoire / I/O (error.rs:46)

public sealed record Concept(ConceptId Id, string Path, OkfDocument Document);
public sealed record ResolvedLink(ConceptId Target, bool Exists, string Text, string Raw);

public sealed class Bundle
{
    public static readonly string[] ReservedFilenames = ["index.md", "log.md"];
    public static Bundle Load(string root);                  // permissif : jette UNIQUEMENT sur I/O/racine invalide
    public string Root { get; }
    public IReadOnlyList<Concept> Concepts { get; }
    public int Count { get; }
    public bool IsEmpty { get; }
    public Concept? Get(ConceptId id);
    public bool Contains(ConceptId id);
    public IReadOnlyList<string> IndexFiles { get; }
    public IReadOnlyList<string> LogFiles { get; }
    public IReadOnlyList<(string Path, string Error)> ParseErrors { get; }
    public IReadOnlyList<ResolvedLink> LinksFrom(ConceptId id);
    public IReadOnlyList<ConceptId> Backlinks(ConceptId id);
    public IReadOnlyList<(ConceptId Source, string RawTarget)> BrokenLinks();
    public string? OkfVersion { get; }                       // lu dans l'index.md racine (bundle.rs:196)
}
```

`TempDir` (port de [tests/common/mod.rs](../../tests/common/mod.rs)) :

```csharp
namespace OKF4net.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; }
    public TempDir() { Path = Directory.CreateTempSubdirectory("okf4net-").FullName; }
    public void Write(string relative, string content)   // crée les répertoires parents, écrit UTF-8 sans BOM
    {
        var full = System.IO.Path.Combine(Path, relative);
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content, new System.Text.UTF8Encoding(false));
    }
    public void Dispose() { try { Directory.Delete(Path, recursive: true); } catch { } }
}
```

- [ ] **Step 1: Porter les 8 tests de [tests/bundle.rs](../../tests/bundle.rs)** — y compris l'helper `appendix_a()` (le bundle d'exemple de la spec, littéraux dans bundle.rs:10–50) : `loads_all_concepts`, `resolves_cross_links_and_backlinks`, `broken_links_are_detected_but_not_fatal`, `appendix_a_is_conformant` (dépend de Task 10 — marquer `[Fact(Skip="Task 10")]` temporairement), `missing_type_is_a_conformance_error` (idem), `reserved_files_are_recognized_not_concepts`, `okf_version_read_from_root_index`.
- [ ] **Step 2: FAIL** → **Step 3: Porter `bundle.rs`** (~210 lignes) : walk récursif (ordre déterministe : trier les entrées par nom ordinal — le Rust fait de même pour la stabilité), fichiers `.md` seulement, `index.md`/`log.md` réservés (collectés à part, pas des concepts), parse permissif (`ParseErrors`), construction du graphe (`outbound` via `LinkScanner` + resolve, `backlinks` inversés, `Exists` selon présence), `broken_links`, `OkfVersion` depuis le frontmatter de l'`index.md` racine.
- [ ] **Step 4: PASS** — `dotnet test --filter BundleTests` (2 tests Skip pour Task 10).
- [ ] **Step 5: Commit** — `git commit -am "feat: port Bundle loader and link graph"`

---

### Task 9: `ChangeLog` (log.md)

**Files:**
- Create: `src/OKF4net/ChangeLog.cs`
- Test: `tests/OKF4net.Tests/ChangeLogTests.cs`

**Interfaces:**
- Consumes: rien du core (module feuille).
- Produces (référence Rust : [src/log.rs](../../src/log.rs)) :

```csharp
namespace OKF4net;

public sealed record LogEntry(string? Kind, string Text);            // Kind = marqueur gras optionnel (Update, Creation…)
public sealed record LogDay(string Date, IReadOnlyList<LogEntry> Entries);

public sealed class ChangeLog
{
    public string? Title { get; }
    public IReadOnlyList<LogDay> Days { get; }                        // ordre du document (convention : plus récent d'abord)
    public static ChangeLog Parse(string text);                       // JAMAIS d'exception (port de Log::parse, permissif)
    public string ToMarkdown();
    public IReadOnlyList<string> InvalidDates();                      // headings ## non ISO
    public static bool IsIsoDate(string s);                           // port de is_iso_date (log.rs:135)
}
```

- [ ] **Step 1: Tests** (pas de fichier de test Rust dédié — écrire depuis la sémantique de log.rs) :

```csharp
namespace OKF4net.Tests;

public class ChangeLogTests
{
    [Fact]
    public void Parse_roundtrips_wellformed_log()
    {
        var src = "# Log\n\n## 2026-07-21\n\n- **Update** Added metric X.\n- Plain entry.\n\n## 2026-07-20\n\n- **Creation** Initial.\n";
        var log = ChangeLog.Parse(src);
        Assert.Equal("Log", log.Title);
        Assert.Equal(2, log.Days.Count);
        Assert.Equal("Update", log.Days[0].Entries[0].Kind);
        Assert.Null(log.Days[0].Entries[1].Kind);
        Assert.Equal(src, log.ToMarkdown());   // vérifier le format exact produit par to_markdown (log.rs:77)
    }

    [Fact]
    public void Invalid_dates_are_reported_not_fatal()
    {
        var log = ChangeLog.Parse("## not-a-date\n\n- x\n");
        Assert.Equal(new[] { "not-a-date" }, log.InvalidDates());
    }

    [Theory]
    [InlineData("2026-07-21", true)]
    [InlineData("2026-13-01", false)]
    [InlineData("26-07-21", false)]
    public void IsIsoDate_checks_shape_and_ranges(string s, bool ok)
        => Assert.Equal(ok, ChangeLog.IsIsoDate(s));
    // Aligner les cas limites sur is_iso_date (log.rs:135) — vérifier si le Rust valide les plages ou juste la forme
}
```

- [ ] **Step 2: FAIL** → **Step 3: Porter `log.rs`** (~140 lignes : `parse`, `bullet_body`, `to_markdown`, `invalid_dates`, `is_iso_date`) → **Step 4: PASS**
- [ ] **Step 5: Commit** — `git commit -am "feat: port ChangeLog (log.md)"`

---

### Task 10: `BundleValidator`

**Files:**
- Create: `src/OKF4net/Validate.cs`
- Modify: `tests/OKF4net.Tests/BundleTests.cs` (retirer les 2 `Skip`)
- Test: `tests/OKF4net.Tests/ValidateTests.cs`

**Interfaces:**
- Consumes: `Bundle` (Task 8), `ChangeLog` (Task 9), `OkfDocument`.
- Produces (référence Rust : [src/validate.rs](../../src/validate.rs)) :

```csharp
namespace OKF4net;

public enum Severity { Error, Warning, Info }                        // variantes exactes de validate.rs:22

public sealed record Diagnostic(Severity Severity, string Path, string Message);

public sealed class ValidationReport
{
    public IReadOnlyList<Diagnostic> Diagnostics { get; }
    public bool IsConformant { get; }                                 // aucun Error
    public IEnumerable<Diagnostic> Of(Severity severity);
    public int ErrorCount { get; }
    public int WarningCount { get; }
}

public static class BundleValidator
{
    public static ValidationReport Validate(Bundle bundle);           // port de validate_bundle (validate.rs:97)
    public static bool IsIso8601DateTime(string s);                   // port de is_iso8601_datetime (validate.rs:210)
}
```

- [ ] **Step 1: Tests** : réactiver `appendix_a_is_conformant` et `missing_type_is_a_conformance_error` dans `BundleTests` ; ajouter dans `ValidateTests.cs` des cas ciblés en lisant `validate_bundle` (validate.rs:97–208) — un diagnostic par règle : parse error → Error, `type` vide → Error, clés producteur manquantes → Warning, timestamp non-ISO → Warning, lien cassé → Warning/Info, dates de log invalides. **Reprendre la sévérité exacte de chaque règle depuis le Rust, ne pas la deviner.**
- [ ] **Step 2: FAIL** → **Step 3: Porter `validate.rs`** (~215 lignes) → **Step 4: PASS** — `dotnet test` complet : tous les tests du projet verts, plus aucun Skip.
- [ ] **Step 5: Commit** — `git commit -am "feat: port bundle conformance validator"`

---

### Task 11: `IndexGenerator`

**Files:**
- Create: `src/OKF4net/IndexGenerator.cs`
- Test: `tests/OKF4net.Tests/IndexTests.cs`

**Interfaces:**
- Consumes: `Bundle`-adjacent (travaille sur le disque comme le Rust), `OkfDocument`, `TempDir`.
- Produces (référence Rust : [src/index.rs](../../src/index.rs)) :

```csharp
namespace OKF4net;

public sealed record IndexEntry(string Type, string Title, string Link, string Description);

public static class IndexGenerator
{
    public static string BuildIndexText(IReadOnlyList<IndexEntry> entries);   // groupé par type (tri ascendant),
                                                                              // puis titre case-insensitive (index.rs:36)
    public delegate string Synthesize(string relativeDir, IReadOnlyList<(string Title, string Description)> children);
    public static string DefaultSynthesize(string relativeDir, IReadOnlyList<(string, string)> children);
    public static IReadOnlyList<string> RegenerateIndexes(string bundleRoot);                 // port de regenerate_indexes
    public static IReadOnlyList<string> RegenerateIndexesWith(string bundleRoot, Synthesize synthesize);
}
```

- [ ] **Step 1: Porter les 3 tests de [tests/index.rs](../../tests/index.rs)** (`regenerate_groups_by_type_and_links_relative`, `regenerate_skips_empty_directories`, `regenerate_single_child_reuses_description`) + l'helper `write_doc` — littéraux et sorties attendues identiques au Rust.
- [ ] **Step 2: FAIL** → **Step 3: Porter `index.rs`** (~200 lignes : `build_index_text` avec groupement `BTreeMap` → en C# `SortedDictionary` ordinal, `collect_markdown`, `directories_to_index`, `depth`, `load_doc`, écriture des `index.md`) → **Step 4: PASS**
- [ ] **Step 5: Commit** — `git commit -am "feat: port index.md generator"`

---

### Task 12: Fixtures golden générées par le binaire Rust

**Files:**
- Create: `tests/fixtures/appendix_a/` (bundle d'exemple), `tests/fixtures/golden/` (sorties de référence), `tests/fixtures/README.md`

**Interfaces:**
- Consumes: le binaire Rust `okf` (le code Rust existe encore).
- Produces: fixtures consommées par les Tasks 13–14 : `golden/validate.out` + `validate.exitcode`, `golden/info.out`, `golden/graph.dot`, `golden/fmt/<n>.md`, `golden/index/<arbre index.md attendu>`.

- [ ] **Step 1: Créer le bundle d'exemple** `tests/fixtures/appendix_a/` en reprenant les fichiers de l'helper `appendix_a()` de [tests/bundle.rs](../../tests/bundle.rs) (mêmes chemins, mêmes contenus, plus un `log.md` et un document volontairement non-strict pour exercer les warnings).
- [ ] **Step 2: Générer les sorties de référence avec le Rust** :

```powershell
cargo build --release
$okf = ".\target\release\okf.exe"
& $okf validate tests/fixtures/appendix_a > tests/fixtures/golden/validate.out; "$LASTEXITCODE" | Set-Content tests/fixtures/golden/validate.exitcode -NoNewline
& $okf info     tests/fixtures/appendix_a > tests/fixtures/golden/info.out
& $okf graph    tests/fixtures/appendix_a --dot > tests/fixtures/golden/graph.dot
& $okf fmt      tests/fixtures/appendix_a/tables/users.md > tests/fixtures/golden/fmt/users.md
Copy-Item -Recurse tests/fixtures/appendix_a tests/fixtures/golden/index-input
& $okf index    tests/fixtures/golden/index-input   # les index.md générés DANS index-input sont la référence
```

(Prérequis : toolchain Rust installée — `rustup` ; si indisponible sur la machine, exécuter cette étape dans un conteneur `rust:1.74` et copier les fixtures.)
- [ ] **Step 3: Vérifier** que chaque fichier golden est non vide et committer : `git add tests/fixtures && git commit -m "test: golden fixtures generated from Rust okf binary"`

---

### Task 13: CLI `OKF4net.Cli`

**Files:**
- Create: `src/OKF4net.Cli/OKF4net.Cli.csproj`, `src/OKF4net.Cli/Program.cs`
- Modify: `OKF4net.sln` (ajouter le projet)
- Test: `tests/OKF4net.Tests/CliTests.cs` (smoke tests in-process)

**Interfaces:**
- Consumes: toute la lib `OKF4net` (Tasks 1–11).
- Produces (référence Rust : [src/bin/okf.rs](../../src/bin/okf.rs)) :

```csharp
// Program.cs expose pour les tests :
namespace OKF4net.Cli;
public static class OkfCli
{
    public static int Run(string[] args, TextWriter stdout, TextWriter stderr);
    // Main = Environment.Exit(Run(args, Console.Out, Console.Error))
}
```

Csproj : `<OutputType>Exe</OutputType>`, `<AssemblyName>okf</AssemblyName>`, `<PublishAot>true</PublishAot>`, `<InvariantGlobalization>true</InvariantGlobalization>`, zéro PackageReference.

- [ ] **Step 1: Tests smoke** — un par commande, via `OkfCli.Run` in-process :

```csharp
namespace OKF4net.Tests;

public class CliTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var o = new StringWriter(); var e = new StringWriter();
        return (OKF4net.Cli.OkfCli.Run(args, o, e), o.ToString(), e.ToString());
    }

    [Fact]
    public void No_args_prints_usage_and_fails()
    {
        var r = Run();
        Assert.NotEqual(0, r.Code);   // reprendre le code exact de okf.rs (usage → quel exitcode ?)
    }

    [Fact]
    public void Validate_conformant_bundle_exits_zero()
    {
        var r = Run("validate", "tests/fixtures/appendix_a");
        Assert.Equal(0, r.Code);
    }

    [Fact]
    public void Validate_nonconformant_bundle_exits_nonzero()
    {
        using var tmp = new TempDir();
        tmp.Write("bad.md", "---\ntitle: no type\n---\n\nx\n");
        Assert.NotEqual(0, Run("validate", tmp.Path).Code);
    }
    // + parse, fmt (-w), info, index, graph (--dot) : smoke sur le bundle fixture
}
```

- [ ] **Step 2: FAIL** → **Step 3: Porter [src/bin/okf.rs](../../src/bin/okf.rs)** (~480 lignes) : dispatch des 6 commandes, parsing d'args à la main, textes d'usage/erreur **identiques**, codes de sortie **identiques** (les lire dans okf.rs — ne pas inventer), sorties de `info`/`graph --dot`/`fmt` au caractère près. → **Step 4: PASS** + `dotnet publish src/OKF4net.Cli -c Release` (AOT) sans erreur.
- [ ] **Step 5: Commit** — `git commit -am "feat: port okf CLI (6 commands, AOT)"`

---

### Task 14: Golden tests de parité

**Files:**
- Test: `tests/OKF4net.Tests/GoldenParityTests.cs`

**Interfaces:**
- Consumes: `OkfCli.Run` (Task 13), fixtures (Task 12), `IndexGenerator` (Task 11).

- [ ] **Step 1: Écrire les tests golden** — la sortie C# doit être **octet-pour-octet** celle du Rust :

```csharp
namespace OKF4net.Tests;

public class GoldenParityTests
{
    private const string Bundle = "tests/fixtures/appendix_a";
    private static string Golden(string rel) => File.ReadAllText(Path.Combine("tests/fixtures/golden", rel));

    [Fact]
    public void Validate_output_and_exitcode_match_rust()
    {
        var o = new StringWriter(); var e = new StringWriter();
        var code = OKF4net.Cli.OkfCli.Run(["validate", Bundle], o, e);
        Assert.Equal(int.Parse(Golden("validate.exitcode")), code);
        Assert.Equal(Golden("validate.out"), o.ToString());
    }

    [Fact] public void Info_output_matches_rust()  { /* même schéma avec info.out */ }
    [Fact] public void Graph_dot_matches_rust()    { /* graph --dot vs graph.dot */ }
    [Fact] public void Fmt_output_matches_rust()   { /* fmt users.md vs fmt/users.md */ }

    [Fact]
    public void Index_generation_matches_rust()
    {
        using var tmp = new TempDir();
        // copier tests/fixtures/appendix_a dans tmp, lancer IndexGenerator.RegenerateIndexes(tmp.Path),
        // comparer chaque index.md généré à son homologue de tests/fixtures/golden/index-input/
    }
}
```

- [ ] **Step 2: Exécuter** — Run: `dotnet test --filter GoldenParityTests` → Expected: PASS. **Tout écart est un bug de port à corriger côté C# (le Rust fait foi), jamais en modifiant la fixture.** Attention aux fins de ligne : lire les goldens en binaire si nécessaire, ne pas laisser git les convertir (ajouter `tests/fixtures/** -text` dans `.gitattributes`).
- [ ] **Step 3: Commit** — `git commit -am "test: golden parity with Rust okf proven"`

---

### Task 15: Suppression du Rust + documentation

**Files:**
- Delete: `Cargo.toml`, `Cargo.lock`, `src/*.rs`, `src/yaml/`, `src/bin/`, `tests/*.rs`, `tests/common/`
- Modify: `README.md`, `NOTICE`, `.gitignore`

- [ ] **Step 1: Vérification complète avant suppression** — Run: `dotnet build -warnaserror && dotnet test` → Expected: 0 warning, tous les tests verts (dont GoldenParityTests).
- [ ] **Step 2: Supprimer le code Rust** :

```powershell
git rm Cargo.toml Cargo.lock
git rm -r src/bin src/yaml
git rm src/*.rs tests/*.rs
git rm -r tests/common
```

Vérifier qu'il ne reste plus un seul `.rs` : `Get-ChildItem -Recurse -Filter *.rs` → vide. Retirer `target/` du `.gitignore`, ajouter `bin/`, `obj/`.
- [ ] **Step 3: Réécrire `README.md`** : OKF4net, .NET 10, mêmes sections (What OKF is, Library overview avec le mapping namespace, Usage lib C# + CLI, Mapping to the spec, Building & testing `dotnet build`/`dotnet test`). Conserver la licence Apache-2.0 et le `NOTICE` (le port reste une œuvre dérivée de l'implémentation de référence OKF — mettre à jour la chaîne d'attribution : référence Python → Rust `okf` → OKF4net).
- [ ] **Step 4: Build final** — Run: `dotnet build && dotnet test` → Expected: vert.
- [ ] **Step 5: Commit** — `git commit -am "chore: remove Rust implementation, OKF4net is the sole implementation"`

---

## Self-Review (fait à la rédaction)

- **Couverture spec Phase 1** : Section 1 (core) → Tasks 1–11 ; Section 2 (CLI) → Task 13 ; Section 4 (tests + golden + phasage) → Tasks 12, 14 ; suppression Rust → Task 15. Les 48 tests Rust sont tous mappés (yaml 16, links 11, document 9, bundle 8, index 3 + helper), complétés par des tests unitaires pour `ConceptId`, `Frontmatter`, `ChangeLog`, `Validate` qui n'ont pas de fichier de test Rust dédié.
- **Types** : `OkfException` créée en Task 2 (consommée par 4, 6, 8) ; `ConceptLink`/`Citation` (Task 7) référencées par `OkfDocument` (Task 6, stubs) — ordre de compilation OK car mêmes assembly ; `TempDir` (Task 8) utilisée par Tasks 8, 11, 13, 14.
- **Limite assumée** : pour un port, le code Rust en repo est la spécification exécutable — les étapes « Porter X.rs » renvoient au fichier exact avec ses fonctions nommées plutôt que de dupliquer ~3 300 lignes dans le plan. Les comportements observables sont, eux, verrouillés par les tests portés fournis et les goldens.

## Phases suivantes (plans séparés, après livraison Phase 1)

- **Phase 2** : `OKF4net.Agents` — `OkfBundleTools` (9 tools `AIFunction`). À planifier avec `writing-plans` une fois la Phase 1 mergée.
- **Phase 3** : `OkfContextProvider` (budget tokens + mémoire long-terme).
