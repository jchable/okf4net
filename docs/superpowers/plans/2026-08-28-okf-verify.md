# `okf verify` Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Enregistrer une relecture — une estampille datée `{ by, at }` dans le champ `verified` d'un concept — pour que la worklist d'`okf audit` ait enfin une sortie.

**Architecture:** Un écrivain gouverné unique dans le cœur (`BundleConceptWriter.RecordVerification`, read-modify-write atomique sur le frontmatter), consommé par un verbe CLI et par un tool agent mutateur. Deux prérequis d'infrastructure CLI (positionnels multiples, seam stdin) précèdent le tout.

**Tech Stack:** C# / net10.0, xunit, zéro dépendance tierce, Native AOT pour le CLI.

**Spec:** [docs/superpowers/specs/2026-08-28-okf-verify-design.md](../specs/2026-08-28-okf-verify-design.md)

## Global Constraints

- **Zéro dépendance tierce** dans `src/OKF4net/` et `src/OKF4net.Cli/` : BCL uniquement, aucun `PackageReference` ajouté.
- Tout nouveau fichier commence par `// SPDX-License-Identifier: LGPL-3.0-or-later`.
- Namespaces file-scoped, XML doc sur toute API publique, nullable activé, `TreatWarningsAsErrors` — un warning casse le build.
- **Ne jamais modifier un bundle de fixtures ni un golden existant** sous `tests/fixtures/`. Les goldens neufs sont écrits à la main, en LF avec LF final, avec leur provenance documentée dans `tests/fixtures/README.md` — ce fichier-là, qui est de la documentation, se modifie normalement.
- **Errors-as-data** : aucune exception pour un cas attendu (concept absent, acteur mal formé, date invalide). Le CLI traduit en `error: …` + code 1.
- Aucune sortie existante ne change, à **une exception assumée** (Task 0, règle du `--`), qui a son entrée de CHANGELOG et son test.
- Baseline avant de commencer : `dotnet test OKF4net.sln` = **1055 tests, 0 échec**. Vérifier avant la Task 0.
- `dotnet format OKF4net.sln` avant le dernier commit (la CI lance `--verify-no-changes`).

## Écart assumé par rapport à la spec

La spec §4.1 donne `RecordVerification` rendant `string?` (null = succès). Le plan
rend à la place un **`VerificationOutcome`** structuré. Raison : les deux
consommateurs ont des besoins différents — le CLI doit formater sa propre ligne
et connaître l'horodatage remplacé, le tool agent veut un message prêt à rendre.
Renvoyer une chaîne obligerait le CLI à renifler le préfixe `Error: ` pour
décider de son code retour, exactement le genre de couplage par convention de
chaîne qu'on évite ailleurs. Errors-as-data est préservé : le type ne lève pas.

## Structure des fichiers

| Fichier | Rôle | Task |
|---|---|---|
| `src/OKF4net.Cli/OkfCli.cs` | `CliArgs` : positionnels multiples ; `Run` : paramètre stdin | 0 |
| `src/OKF4net.Cli/Program.cs` | câble `Console.In` | 0 |
| `tests/OKF4net.Tests/TestPaths.cs` | surcharge `Run` avec stdin | 0 |
| `src/OKF4net/BundleConceptWriter.cs` | `RecordVerification`, `UpsertStamp`, `BuildConformantContent` | 1 |
| `tests/OKF4net.Tests/RecordVerificationTests.cs` (créé) | tests du cœur | 1 |
| `src/OKF4net.Cli/OkfCli.cs` | `Usage`, dispatch, `CmdVerify` | 2 |
| `tests/OKF4net.Tests/CliTests.cs` | tests CLI | 2 |
| `tests/fixtures/golden/verify.out` (créé) | golden de sortie | 3 |
| `tests/OKF4net.Tests/GoldenParityTests.cs`, `tests/fixtures/README.md` | parité + provenance | 3 |
| `src/OKF4net.Agents/OkfBundleTools.cs` | tool `okf_verify`, `WriteToolNames` | 4 |
| `tests/OKF4net.Tests/Agents/*`, `tests/OKF4net.Tests/Mcp/*` | tests tool + MCP | 4 |
| `README.md`, `CHANGELOG.md`, `CLAUDE.md`, `ROADMAP.md`, `web/src/pages/**` | documentation | 5 |

---

### Task 0: Les deux prérequis du CLI

**Files:**
- Modify: `src/OKF4net.Cli/OkfCli.cs` (classe `CliArgs`, signature `Run`)
- Modify: `src/OKF4net.Cli/Program.cs`
- Modify: `tests/OKF4net.Tests/TestPaths.cs`
- Test: `tests/OKF4net.Tests/CliTests.cs`

**Interfaces:**
- Produces: `CliArgs.Positionals` (`IReadOnlyList<string>`, ordonnée) à côté de `Positional(string what)` inchangé ; `OkfCli.Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)` ; `TestPaths.RunWithStdin(string stdin, params string[] args)`.

**Changement de comportement assumé.** Avec un seul positionnel, la règle était
« le token après `--` gagne le créneau ». Avec une liste, cette règle n'a plus de
sens (lequel gagne ?), et elle perdrait le bundle sur `okf verify b -- id1 id2`.
La règle devient donc celle de POSIX : `--` **termine la lecture des options**,
les positionnels qui le précèdent sont conservés, ceux qui le suivent s'ajoutent.
Seul cas divergent : `okf <verbe> a -- b` rendait `b` comme positionnel, rendra
`a`. Trois tests du séparateur existent (`CliTests.cs:745`, `:762`, `:782`) et
aucun ne change de résultat sous la nouvelle règle — le troisième a bien un
positionnel avant `--`, mais rien après, donc les deux règles coïncident. En
revanche, la doc XML de ce troisième test décrit une distinction que ce
changement efface (« clear the positionals » contre « only override ») : la
réécrire dans la même tâche, sinon elle explique un comportement disparu.

- [ ] **Step 1: Écrire les tests qui échouent**

Ajouter à `tests/OKF4net.Tests/CliTests.cs` :

```csharp
    /// <summary>
    /// `--` ends option parsing; it does not discard the positionals that came
    /// before it. With a single positional slot the old rule ("the token after
    /// the separator wins") was indistinguishable from this one; with a verb
    /// that takes several, it would silently drop the bundle.
    /// </summary>
    [Fact]
    public void Separator_keeps_positionals_from_both_sides()
    {
        var r = Run("audit", V02BundlePath, "--", "--json");

        // The bundle before `--` is still the positional; `--json` after it is
        // an argument, not a flag, so the output is the text report.
        Assert.Equal(0, r.Code);
        Assert.StartsWith($"bundle:     {V02BundlePath}", r.Out);
        Assert.DoesNotContain("\"conceptCount\"", r.Out);
    }

    /// <summary>
    /// A verb that does not document reading standard input must never touch
    /// it — otherwise `okf fmt file` inside a pipeline would block on a reader
    /// nobody is feeding. A StringReader could not prove this (it records
    /// nothing), so the reader here throws if anything reads it.
    /// </summary>
    [Fact]
    public void A_verb_that_does_not_read_stdin_never_touches_it()
    {
        var r = TestPaths.RunWithReader(
            new ThrowingReader(),
            "fmt",
            Path.Combine(BundlePath, "tables", "users.md"));

        Assert.Equal(0, r.Code);
        Assert.Contains("title: Users", r.Out);
    }

    /// <summary>A reader that fails the test if the CLI reads from it at all.</summary>
    private sealed class ThrowingReader : TextReader
    {
        public override int Peek() => throw new InvalidOperationException("stdin was read");

        public override int Read() => throw new InvalidOperationException("stdin was read");

        public override string? ReadLine() => throw new InvalidOperationException("stdin was read");
    }
```

Le titre asserté est `Users` : c'est le frontmatter de `tables/users.md`
([appendix_a/tables/users.md:3](../../../tests/fixtures/appendix_a/tables/users.md#L3)).

`TestPaths` gagne donc **deux** aides : `RunWithStdin(string, params string[])`
pour le contenu, et `RunWithReader(TextReader, params string[])` pour ce test.

- [ ] **Step 2: Lancer les tests pour vérifier qu'ils échouent**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~CliTests.Separator_keeps|FullyQualifiedName~CliTests.Run_reads_ids"`
**Écris et lance le test du séparateur SEUL d'abord** : il doit échouer, et
c'est la seule preuve que la régression existe avant le correctif. Les deux
autres tests utilisent `RunWithReader`, qui n'existe pas encore — les ajouter
maintenant empêcherait la compilation de tout le projet de tests, donc le
premier ne s'exécuterait jamais et on ne verrait rien échouer. Les ajouter
après le Step 4.

Expected (test du séparateur seul) : ÉCHEC — la sortie est le JSON, parce que
`--json` placé après `--` est encore honoré comme flag.

- [ ] **Step 3: Positionnels multiples dans `CliArgs`**

Dans `src/OKF4net.Cli/OkfCli.cs`, remplacer le champ unique et sa lecture :

```csharp
        /// <summary>
        /// The positional tokens, in order. `--` ends option parsing without
        /// discarding what came before it, so a verb taking several positionals
        /// (`verify <bundle> <id>…`) keeps them all.
        /// </summary>
        private readonly List<string> _positionals = [];
```

**Un `-` seul est un positionnel, pas un flag.** La branche des flags est
`if (token.StartsWith('-'))`, et `"-"` y entre : il est enregistré comme flag et
n'atteint jamais la liste des positionnels. Vérifié en conditions réelles —
`okf fmt -` répond aujourd'hui `error: missing <file>`. Sans ce correctif, toute
la forme stdin de §5.1 est inatteignable : `ids.Contains("-")` serait
systématiquement faux. Le garde à ajouter, dans la même branche :

```csharp
                // A lone "-" is POSIX's "read from standard input" — an
                // argument, not an option. Only a token with something after
                // the dash is a flag.
                if (token.Length > 1 && token.StartsWith('-'))
```

Aucun test existant ne passe un `-` seul (vérifié par recherche), donc rien ne
casse ; c'est une **troisième** entrée « Changed » au CHANGELOG en Task 5.

Dans `Scan`, la branche du séparateur devient un ajout, non un écrasement :

```csharp
                if (token == "--")
                {
                    // Everything past the separator is positional, never a flag.
                    // It APPENDS: the tokens before it are positionals too.
                    for (var j = i + 1; j < args.Length; j++)
                    {
                        scanned._positionals.Add(args[j]);
                    }

                    break;
                }
```

et la branche positionnelle ordinaire :

```csharp
                scanned._positionals.Add(token);
```

Enfin les deux accesseurs :

```csharp
        /// <summary>The first positional argument, or throws naming <paramref name="what"/>.</summary>
        internal string Positional(string what) =>
            _positionals.Count > 0 ? _positionals[0] : throw new CliOperationException($"missing {what}");

        /// <summary>Every positional argument, in order — the first is what <see cref="Positional"/> returns.</summary>
        internal IReadOnlyList<string> Positionals => _positionals;
```

- [ ] **Step 4: Seam stdin sur `Run`**

Dans `OkfCli.cs`, la signature et le passage aux verbes :

```csharp
    public static int Run(string[] args, TextReader stdin, TextWriter stdout, TextWriter stderr)
```

Mettre à jour le commentaire XML de `Run` : ajouter un `<param name="stdin">`
disant que seuls les verbes le documentant le lisent (aujourd'hui `verify`), et
que les autres ne le touchent jamais — aucune lecture bloquante n'est
introduite. Dans le `switch`, seul `CmdVerify` (Task 2) recevra `stdin` ; les
sept autres appels restent inchangés.

Dans `src/OKF4net.Cli/Program.cs` :

```csharp
        return OkfCli.Run(args, Console.In, Console.Out, Console.Error);
```

Dans `tests/OKF4net.Tests/TestPaths.cs`, garder `Run` intact pour les ~60
appels existants et ajouter la variante :

```csharp
    /// <summary>
    /// Runs the CLI in-process like <see cref="Run"/>, with <paramref name="stdin"/>
    /// as its standard input — for the verbs that read ids from a pipe.
    /// </summary>
    internal static (int Code, string Out, string Err) RunWithStdin(string stdin, params string[] args) =>
        RunWithReader(new StringReader(stdin), args);

    /// <summary>
    /// Runs the CLI in-process with an arbitrary <paramref name="stdin"/> reader —
    /// lets a test prove a verb never touches standard input by handing it one
    /// that throws.
    /// </summary>
    internal static (int Code, string Out, string Err) RunWithReader(TextReader stdin, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (OkfCli.Run(args, stdin, o, e), o.ToString(), e.ToString());
    }
```

et faire passer `Run` par `TextReader.Null` :

```csharp
        return (OkfCli.Run(args, TextReader.Null, o, e), o.ToString(), e.ToString());
```

- [ ] **Step 5: Lancer la suite complète**

Run: `dotnet test OKF4net.sln`
Expected: 1055 + 2 nouveaux, 0 échec. **Aucun golden ne bouge** : la règle du `--` ne change que le cas `a -- b`, qu'aucun golden n'exerce.

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net.Cli/OkfCli.cs src/OKF4net.Cli/Program.cs tests/OKF4net.Tests/TestPaths.cs tests/OKF4net.Tests/CliTests.cs
git commit -m "refactor(cli): ordered positionals and a stdin seam"
```

---

### Task 1: Le cœur — `RecordVerification`

**Files:**
- Modify: `src/OKF4net/BundleConceptWriter.cs`
- Test: `tests/OKF4net.Tests/RecordVerificationTests.cs` (créé)

**Interfaces:**
- Consumes: `ValidateConceptTarget`, `_bundleLock`, `WriteValidatedContentLocked`, `RunTool`, `UtcNow`, `OkfEncodings.Strict`, `OkfTimestamp.FormatUtc`, `OkfDocument.Parse`/`ValidateConformance`/`Serialize`, `Frontmatter.AsMapping`, `Actor.Parse`, `YamlMapping.Insert/Get`, `YamlSequence.Items`, `YamlString`.
- Produces: `VerificationRecord(string ConceptId, string At, string? ReplacedAt)`, `VerificationOutcome(bool Recorded, string Message, IReadOnlyList<VerificationRecord> Records)` et **une seule** méthode publique : `BundleConceptWriter.RecordVerifications(IReadOnlyList<string> conceptIds, string by, string? at = null) → VerificationOutcome`.

**Pourquoi une opération de lot, et pas une par concept.** Une boucle
d'écritures unitaires n'est pas tout-ou-rien : un second document non conforme,
illisible ou disparu laisse le premier déjà estampillé. Prévalider dans
l'appelant ne suffit pas non plus — la fenêtre entre le contrôle et l'écriture
reste ouverte, et il faudrait la refermer dans le CLI *et* dans le tool, deux
fois. Le lot résout, lit, parse, valide et **prépare le contenu de tous les
concepts** avant d'en écrire un seul, le tout sous une seule détention du
verrou. Les deux consommateurs appellent la même méthode et héritent de la
garantie ; il n'y a pas de version « un seul concept » à maintenir en parallèle
(un id unique est une liste de un).

Limite à documenter, pas à cacher : le verrou est un verrou C# in-process, et
.NET n'offre pas d'écriture multi-fichiers atomique. Un acteur externe qui
modifie le bundle pendant le lot n'est pas arrêté — même modèle de menace, déjà
documenté, que la garde reparse-point du writer.

- [ ] **Step 1: Écrire les tests qui échouent**

Créer `tests/OKF4net.Tests/RecordVerificationTests.cs` :

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests;

/// <summary>
/// Tests for <see cref="BundleConceptWriter.RecordVerification"/>: the single
/// governed writer of the §5.2 <c>verified</c> field. Every test pins the
/// clock through the writer's own <c>UtcNow</c> seam so no assertion depends
/// on the day the suite runs.
/// </summary>
public class RecordVerificationTests
{
    private const string Fm = "---\ntype: Metric\ntitle: Daily Active Users\n";

    private static BundleConceptWriter WriterOver(TempDir tmp) =>
        new(tmp.Path) { UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc) };

    private static string Read(TempDir tmp, string rel) => File.ReadAllText(Path.Combine(tmp.Path, rel));

    [Fact]
    public void First_stamp_creates_the_list_and_leaves_everything_else_alone()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "custom_key: kept\n---\n\n# Body\n");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada");

        Assert.True(outcome.Recorded);
        Assert.Null(outcome.Records.Single().ReplacedAt);

        // Substring checks would miss a dropped key or a mangled body, so the
        // whole document is compared: the frontmatter is exactly the original
        // keys in order plus `verified`, and the body is untouched.
        var after = OkfDocument.Parse(Read(tmp, "metrics/dau.md"));
        Assert.Equal(["type", "title", "custom_key", "verified"], after.Frontmatter.AsMapping().Keys);
        Assert.Equal("kept", after.Frontmatter.Get("custom_key")!.AsDisplayString());
        Assert.Equal("# Body\n", after.Body);

        var stamp = Assert.Single(after.Frontmatter.Verified);
        Assert.Equal("human:ada", stamp.By!.Value.Raw);
        Assert.Equal("2026-08-28T09:14:00Z", stamp.At);
    }

    [Fact]
    public void Same_actor_replaces_its_own_stamp_in_place()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "metrics/dau.md",
            Fm + "verified:\n  - { by: human:ada, at: 2026-01-01T00:00:00Z }\n"
            + "  - { by: process:nightly, at: 2026-02-02T00:00:00Z }\n---\n\nbody\n");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada");

        Assert.True(outcome.Recorded);
        Assert.Equal("2026-01-01T00:00:00Z", outcome.Records.Single().ReplacedAt);

        var doc = OkfDocument.Parse(Read(tmp, "metrics/dau.md"));
        var stamps = doc.Frontmatter.Verified;
        Assert.Equal(2, stamps.Count);
        // Position preserved: ada stays first, nightly untouched.
        Assert.Equal("human:ada", stamps[0].By!.Value.Raw);
        Assert.Equal("2026-08-28T09:14:00Z", stamps[0].At);
        Assert.Equal("process:nightly", stamps[1].By!.Value.Raw);
        Assert.Equal("2026-02-02T00:00:00Z", stamps[1].At);
    }

    [Fact]
    public void A_different_actor_is_appended_and_never_touches_another_entry()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "metrics/dau.md",
            Fm + "verified:\n  - { by: human:ada, at: 2026-01-01T00:00:00Z }\n---\n\nbody\n");

        WriterOver(tmp).RecordVerifications(["metrics/dau"], "process:nightly");

        var stamps = OkfDocument.Parse(Read(tmp, "metrics/dau.md")).Frontmatter.Verified;
        Assert.Equal(2, stamps.Count);
        Assert.Equal("human:ada", stamps[0].By!.Value.Raw);
        Assert.Equal("2026-01-01T00:00:00Z", stamps[0].At);
        Assert.Equal("process:nightly", stamps[1].By!.Value.Raw);
    }

    /// <summary>
    /// A permissive reader accepts duplicate entries for one actor (§5.2 says
    /// nothing about uniqueness), so the writer replaces the FIRST match only
    /// and never deletes an entry it is not replacing.
    /// </summary>
    [Fact]
    public void Only_the_first_duplicate_of_an_actor_is_replaced()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "metrics/dau.md",
            Fm + "verified:\n  - { by: human:ada, at: 2026-01-01T00:00:00Z }\n"
            + "  - { by: human:ada, at: 2026-02-02T00:00:00Z }\n---\n\nbody\n");

        WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada");

        var stamps = OkfDocument.Parse(Read(tmp, "metrics/dau.md")).Frontmatter.Verified;
        Assert.Equal(2, stamps.Count);
        Assert.Equal("2026-08-28T09:14:00Z", stamps[0].At);
        Assert.Equal("2026-02-02T00:00:00Z", stamps[1].At);
    }

    /// <summary>
    /// `verified: { by, at }` — a single mapping rather than a list — is a
    /// shape <see cref="Trust.ParseVerified"/> accepts (Trust.cs:32), so the
    /// writer must normalize it instead of throwing or overwriting it.
    /// </summary>
    [Fact]
    public void A_single_mapping_verified_is_normalized_to_a_list()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "verified: { by: process:nightly, at: 2026-01-01T00:00:00Z }\n---\n\nbody\n");

        WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada");

        var stamps = OkfDocument.Parse(Read(tmp, "metrics/dau.md")).Frontmatter.Verified;
        Assert.Equal(2, stamps.Count);
        Assert.Equal("process:nightly", stamps[0].By!.Value.Raw);
        Assert.Equal("human:ada", stamps[1].By!.Value.Raw);
    }

    /// <summary>
    /// A concept named twice is refused rather than collapsed: preparing the
    /// same file twice from the same original content would write it twice and
    /// report two lines for one surviving stamp — a result that reads like two
    /// reviews. Nothing is written.
    /// </summary>
    [Fact]
    public void A_duplicate_concept_id_is_refused()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");
        var before = Read(tmp, "metrics/dau.md");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau", "metrics/dau"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("named more than once", outcome.Message);
        Assert.Equal(before, Read(tmp, "metrics/dau.md"));
    }

    [Theory]
    [InlineData("human:", "not a well-formed")]
    [InlineData("", "not a well-formed")]
    public void A_malformed_actor_is_refused(string by, string expected)
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], by);

        Assert.False(outcome.Recorded);
        Assert.Contains(expected, outcome.Message);
        Assert.DoesNotContain("verified", Read(tmp, "metrics/dau.md"));
    }

    [Fact]
    public void A_non_iso_at_is_refused()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada", "hier");

        Assert.False(outcome.Recorded);
        Assert.Contains("yyyy-MM-ddTHH:mm:ssZ", outcome.Message);
    }

    [Fact]
    public void An_unknown_concept_is_refused_without_creating_it()
    {
        using var tmp = new TempDir();

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/nope"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("does not exist", outcome.Message);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "metrics", "nope.md")));
    }

    /// <summary>
    /// Conformance-level validation (§11, non-empty type), NOT producer-grade:
    /// refusing to record a human's review because a third party omitted a
    /// `description` would make exactly the concepts the worklist surfaces
    /// unstampable. See the design spec §4.2.
    /// </summary>
    [Fact]
    public void A_concept_missing_description_is_still_stampable()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntype: Metric\n---\n\nbody\n");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada");

        Assert.True(outcome.Recorded);
        Assert.Contains("by: human:ada", Read(tmp, "metrics/dau.md"));
    }

    [Fact]
    public void A_concept_without_type_is_refused()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntitle: No type\n---\n\nbody\n");

        var outcome = WriterOver(tmp).RecordVerifications(["metrics/dau"], "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("type", outcome.Message);
    }

    [Fact]
    public void Generated_is_never_written_or_refreshed()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", Fm + "generated: { by: okf4net/0.3.0, at: 2020-01-01T00:00:00Z }\n---\n\nbody\n");
        tmp.Write("b.md", Fm + "---\n\nbody\n");

        // AutoStampGenerated defaults to false, so a bare writer would pass this
        // test even if RecordVerifications went through the auto-stamping path.
        // OkfBundleTools turns it ON, which is the configuration that matters.
        var stamping = new BundleConceptWriter(tmp.Path)
        {
            AutoStampGenerated = true,
            UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc),
        };
        stamping.RecordVerifications(["b"], "human:ada");
        Assert.DoesNotContain("generated", Read(tmp, "b.md"));

        var writer = WriterOver(tmp);
        writer.RecordVerifications(["a"], "human:ada");
        writer.RecordVerifications(["b"], "human:ada");

        Assert.Contains("at: 2020-01-01T00:00:00Z", Read(tmp, "a.md"));
        Assert.DoesNotContain("generated", Read(tmp, "b.md"));
    }

    /// <summary>The tier okf audit reads moves as a direct consequence.</summary>
    [Fact]
    public void The_trust_tier_moves_after_a_stamp()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");
        var writer = WriterOver(tmp);

        Assert.Equal(TrustTier.Unverified, Bundle.Load(tmp.Path).Concepts[0].Document.Frontmatter.TrustTier);

        writer.RecordVerifications(["metrics/dau"], "process:nightly");
        Assert.Equal(TrustTier.MachineConfirmed, Bundle.Load(tmp.Path).Concepts[0].Document.Frontmatter.TrustTier);

        writer.RecordVerifications(["metrics/dau"], "human:ada");
        Assert.Equal(TrustTier.HumanReviewed, Bundle.Load(tmp.Path).Concepts[0].Document.Frontmatter.TrustTier);
    }

    /// <summary>
    /// Two verifications of the same concept must not lose a stamp: the read,
    /// the transform and the write all happen inside one hold of the writer's
    /// bundle lock.
    /// </summary>
    [Fact]
    public void Concurrent_verifications_of_one_concept_both_land()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");
        var writer = WriterOver(tmp);

        Parallel.Invoke(
            () => writer.RecordVerifications(["metrics/dau"], "human:ada"),
            () => writer.RecordVerifications(["metrics/dau"], "process:nightly"));

        var stamps = OkfDocument.Parse(Read(tmp, "metrics/dau.md")).Frontmatter.Verified;
        Assert.Equal(2, stamps.Count);
    }
}
```

- [ ] **Step 2: Lancer les tests pour vérifier qu'ils échouent**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~RecordVerificationTests"`
Expected: échec de compilation — `RecordVerification` et `VerificationOutcome` n'existent pas.

- [ ] **Step 3: Implémenter**

Dans `src/OKF4net/BundleConceptWriter.cs`, les deux types de résultat au-dessus de la classe :

```csharp
/// <summary>One concept stamped by <see cref="BundleConceptWriter.RecordVerifications"/>.</summary>
/// <param name="ConceptId">The concept that was stamped.</param>
/// <param name="At">
/// The timestamp written. Callers could format their own — the CLI and the
/// Agents layer both see <c>OkfTimestamp</c> through <c>InternalsVisibleTo</c> —
/// but two clocks are one too many: only the writer holds the seam tests pin,
/// so it reports what it wrote.
/// </param>
/// <param name="ReplacedAt">The superseded <c>at</c>, or null when the stamp is new.</param>
public readonly record struct VerificationRecord(string ConceptId, string At, string? ReplacedAt);

/// <summary>
/// The outcome of <see cref="BundleConceptWriter.RecordVerifications"/>:
/// errors-as-data, never thrown. All-or-nothing — when
/// <see cref="Recorded"/> is false, nothing was written and
/// <see cref="Records"/> is empty.
/// </summary>
/// <param name="Recorded">Whether the batch was written.</param>
/// <param name="Message">A confirmation, or the reason nothing was written.</param>
/// <param name="Records">One entry per stamped concept, in the order given.</param>
public readonly record struct VerificationOutcome(bool Recorded, string Message, IReadOnlyList<VerificationRecord> Records);
```

Puis, dans la classe, la méthode de lot et ses aides privées :

```csharp
    /// <summary>
    /// Records a review of every concept in <paramref name="conceptIds"/>:
    /// adds — or replaces, at its position — the <c>{ by, at }</c> entry of
    /// <paramref name="by"/> in each concept's §5.2 <c>verified</c> list,
    /// preserving every other frontmatter key and the body.
    ///
    /// All-or-nothing: every concept is resolved, read, edited and validated
    /// before the first byte is written, all inside one hold of the bundle
    /// lock, so a bad third id cannot leave the first two stamped. The lock is
    /// an in-process one and .NET has no multi-file atomic write, so an
    /// external actor mutating the bundle mid-batch is not stopped — the same
    /// documented limit as this class's reparse-point guard.
    ///
    /// A stamp is a dated declaration, not an authentication result: this
    /// method cannot and does not check that the caller is who
    /// <paramref name="by"/> names. What makes a stamp credible is where it
    /// lands — a reviewed diff — not the tool that wrote it.
    /// </summary>
    /// <param name="conceptIds">Concept ids (paths without <c>.md</c>); each must already exist.</param>
    /// <param name="by">The §7 actor recording the review; must be well-formed.</param>
    /// <param name="at">
    /// Timestamp in the library's own UTC shape (<c>yyyy-MM-ddTHH:mm:ssZ</c>);
    /// null uses <see cref="UtcNow"/>.
    /// </param>
    public VerificationOutcome RecordVerifications(IReadOnlyList<string> conceptIds, string by, string? at = null)
    {
        if (conceptIds is null || conceptIds.Count == 0)
        {
            return Failed("Error: no concept id given.");
        }

        // Duplicates are refused, not silently collapsed. Preparing the same
        // file twice would build both versions from the same original content
        // and write it twice, reporting two `recorded` lines for the single
        // stamp that survives — a result that reads like two reviews. Naming a
        // concept twice is a mistake in the caller's list; say so.
        var duplicate = conceptIds
            .GroupBy(id => id, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            return Failed($"Error: concept '{duplicate.Key}' is named more than once.");
        }

        // Strict on input, permissive on read: `human:` with no id promotes the
        // tier (Actor.IsHuman ignores well-formedness), so it must never be
        // written here even though a parser would accept it.
        if (by is null || !Actor.Parse(by).IsWellFormed)
        {
            return Failed($"Error: '{by}' is not a well-formed §7 actor.");
        }

        // NOT BundleValidator.IsIso8601DateTime: that predicate validates the
        // date and ignores everything after the `T` (Validate.cs:618), because
        // reading frontmatter is deliberately permissive. Writing is not: a
        // stamp this library produces is UTC in one exact shape, and accepting
        // "2026-08-28" or a +02:00 offset here would write a value the field's
        // own documentation calls UTC.
        var stampedAt = at ?? OkfTimestamp.FormatUtc(UtcNow());
        if (!DateTime.TryParseExact(
                stampedAt,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out _))
        {
            return Failed($"Error: '{stampedAt}' is not a UTC timestamp of the form yyyy-MM-ddTHH:mm:ssZ.");
        }

        var records = new List<VerificationRecord>(conceptIds.Count);
        var message = RunTool(() =>
        {
            // Resolved outside the lock, like AppendToConceptAtomic does.
            var targets = new List<ConceptTarget>(conceptIds.Count);
            foreach (var conceptId in conceptIds)
            {
                var targetError = ValidateConceptTarget(conceptId, out var target);
                if (targetError is not null)
                {
                    return targetError;
                }

                targets.Add(target);
            }

            lock (_bundleLock)
            {
                // PREPARE every concept — read, parse, upsert, validate — before
                // writing any of them. This is what makes the batch all-or-nothing.
                var prepared = new List<(ConceptTarget Target, string Content)>(targets.Count);
                for (var i = 0; i < targets.Count; i++)
                {
                    var target = targets[i];
                    if (!File.Exists(target.TargetPath))
                    {
                        return $"Error: concept '{conceptIds[i]}' does not exist.";
                    }

                    var text = OkfEncodings.Strict.GetString(File.ReadAllBytes(target.TargetPath));
                    var document = OkfDocument.Parse(text);
                    var map = document.Frontmatter.AsMapping();

                    map.Insert("verified", UpsertStamp(map.Get("verified"), by, stampedAt, out var replacedAt));

                    var (content, buildError) = BuildConformantContent(map, document.Body);
                    if (buildError is not null)
                    {
                        return buildError;
                    }

                    prepared.Add((target, content!));
                    records.Add(new VerificationRecord(conceptIds[i], stampedAt, replacedAt));
                }

                foreach (var (target, content) in prepared)
                {
                    var writeResult = WriteValidatedContentLocked(target.Id, target.TargetPath, content, existedBefore: true);
                    if (writeResult.StartsWith("Error:", StringComparison.Ordinal))
                    {
                        return writeResult;
                    }
                }

                return $"Recorded {prepared.Count} verification(s) by {by} at {stampedAt}.";
            }
        });

        return message.StartsWith("Error:", StringComparison.Ordinal)
            ? Failed(message)
            : new VerificationOutcome(true, message, records);

        static VerificationOutcome Failed(string message) => new(false, message, []);
    }

    /// <summary>
    /// Returns the <c>verified</c> sequence with <paramref name="by"/>'s stamp
    /// added, or replaced at its existing position. <see cref="YamlSequence"/>
    /// is immutable, so the list is rebuilt; only the FIRST entry matching the
    /// actor is replaced — a permissive reader accepts duplicates, and this
    /// writer never deletes an entry it is not replacing.
    /// </summary>
    private static YamlSequence UpsertStamp(YamlValue? existing, string by, string at, out string? replacedAt)
    {
        replacedAt = null;

        var items = existing switch
        {
            YamlSequence sequence => new List<YamlValue>(sequence.Items),
            // `verified: { by, at }` — a bare mapping — is a shape ParseVerified
            // accepts, so normalize it into the list rather than discarding it.
            YamlMapping single => [single],
            _ => [],
        };

        var stamp = new YamlMapping();
        stamp.Insert("by", new YamlString(by));
        stamp.Insert("at", new YamlString(at));

        for (var i = 0; i < items.Count; i++)
        {
            if (items[i] is YamlMapping mapping
                && string.Equals(mapping.Get("by")?.AsDisplayString(), by, StringComparison.Ordinal))
            {
                replacedAt = mapping.Get("at")?.AsDisplayString();
                items[i] = stamp;
                return new YamlSequence(items);
            }
        }

        items.Add(stamp);
        return new YamlSequence(items);
    }

    /// <summary>
    /// Serializes after §11 conformance validation only (non-empty <c>type</c>),
    /// unlike <see cref="BuildValidatedContent(YamlValue, string)"/>'s
    /// producer-grade check. Deliberate: recording a review is not producing
    /// content, and refusing a reviewer because a third party omitted a
    /// <c>description</c> would make precisely the concepts an audit surfaces
    /// unstampable. Throws <see cref="DocumentValidationException"/>, caught by
    /// the caller's <see cref="RunTool"/> wrapper.
    /// </summary>
    private static (string? Content, string? Error) BuildConformantContent(YamlMapping frontmatter, string body)
    {
        var document = new OkfDocument(Frontmatter.FromMapping(frontmatter), body);
        document.ValidateConformance();
        return (document.Serialize(), null);
    }
```

Enfin, corriger le commentaire XML du seam d'horloge, qui devient faux :

```csharp
    /// <summary>
    /// Clock seam for the <c>generated</c> auto-stamp and for
    /// <see cref="RecordVerification"/>'s <c>at</c>; overridable in tests.
    /// </summary>
    internal Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;
```

- [ ] **Step 4: Lancer les tests**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~RecordVerificationTests"` puis la suite complète.
Expected: PASS — 13 méthodes (14 cas, la `[Theory]` en comptant deux).

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net/BundleConceptWriter.cs tests/OKF4net.Tests/RecordVerificationTests.cs
git commit -m "feat(core): RecordVerification, the governed writer of verified"
```

---

### Task 2: Le verbe CLI `okf verify`

**Files:**
- Modify: `src/OKF4net.Cli/OkfCli.cs`
- Test: `tests/OKF4net.Tests/CliTests.cs`

**Interfaces:**
- Consumes: Task 0 (`CliArgs.Positionals`, `Run(…, TextReader stdin, …)`), Task 1 (`RecordVerification`, `VerificationOutcome`).
- Produces: le verbe et son format de sortie, que la Task 3 fige en golden.

- [ ] **Step 1: Écrire les tests qui échouent**

Ajouter à `tests/OKF4net.Tests/CliTests.cs` :

```csharp
    private static string NewBundleWithTwoConcepts(TempDir tmp)
    {
        tmp.Write("metrics/dau.md", "---\ntype: Metric\ntitle: DAU\n---\n\nbody\n");
        tmp.Write("metrics/rev.md", "---\ntype: Metric\ntitle: Revenue\n---\n\nbody\n");
        return tmp.Path;
    }

    [Fact]
    public void Verify_records_a_stamp_on_each_named_concept()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);

        var r = Run("verify", bundle, "metrics/dau", "metrics/rev", "--by", "human:ada", "--at", "2026-08-28T09:14:00Z");

        Assert.Equal(0, r.Code);
        Assert.Equal(
            "recorded metrics/dau  human:ada  2026-08-28T09:14:00Z\n"
            + "recorded metrics/rev  human:ada  2026-08-28T09:14:00Z\n",
            r.Out);
        Assert.Contains("by: human:ada", File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md")));
    }

    [Fact]
    public void Verify_reports_the_timestamp_it_superseded()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "metrics/dau.md",
            "---\ntype: Metric\nverified:\n  - { by: human:ada, at: 2026-01-01T00:00:00Z }\n---\n\nbody\n");

        var r = Run("verify", tmp.Path, "metrics/dau", "--by", "human:ada", "--at", "2026-08-28T09:14:00Z");

        Assert.Equal(0, r.Code);
        Assert.Equal(
            "recorded metrics/dau  human:ada  2026-08-28T09:14:00Z  (replaces 2026-01-01T00:00:00Z)\n",
            r.Out);
    }

    /// <summary>The line that closes the loop: audit's ids piped into verify.</summary>
    [Fact]
    public void Verify_reads_ids_from_stdin_when_the_id_is_a_dash()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);

        var r = TestPaths.RunWithStdin(
            "metrics/dau\n\nmetrics/rev\n",
            "verify", bundle, "-", "--by", "human:ada", "--at", "2026-08-28T09:14:00Z");

        Assert.Equal(0, r.Code);
        // The blank line is ignored, both concepts are stamped, order preserved.
        Assert.Equal(
            "recorded metrics/dau  human:ada  2026-08-28T09:14:00Z\n"
            + "recorded metrics/rev  human:ada  2026-08-28T09:14:00Z\n",
            r.Out);
    }

    /// <summary>
    /// All-or-nothing: every id is resolved before anything is written, so one
    /// unknown id leaves the whole bundle untouched.
    /// </summary>
    [Fact]
    public void Verify_writes_nothing_when_one_id_is_unknown()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);
        var before = File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md"));

        var r = Run("verify", bundle, "metrics/dau", "metrics/nope", "--by", "human:ada");

        Assert.Equal(1, r.Code);
        Assert.Equal("error: unknown concept \"metrics/nope\"\n", r.Err);
        Assert.Equal(before, File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md")));
    }

    /// <summary>
    /// Existence is not enough for the pre-flight: a document with no `type`
    /// loads into the bundle but is refused at write time, so without the
    /// conformance check here the concepts named before it would already be
    /// stamped.
    /// </summary>
    [Fact]
    public void Verify_writes_nothing_when_one_concept_is_not_conformant()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);
        tmp.Write("metrics/broken.md", "---\ntitle: No type\n---\n\nbody\n");
        var before = File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md"));

        var r = Run("verify", bundle, "metrics/dau", "metrics/broken", "--by", "human:ada");

        Assert.Equal(1, r.Code);
        Assert.Equal("error: concept \"metrics/broken\" has no `type` and is not §11-conformant\n", r.Err);
        Assert.Equal(before, File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md")));
    }

    [Fact]
    public void Verify_refuses_a_concept_named_twice()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);
        var before = File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md"));

        var r = Run("verify", bundle, "metrics/dau", "metrics/dau", "--by", "human:ada");

        Assert.Equal(1, r.Code);
        Assert.Equal("error: concept 'metrics/dau' is named more than once\n", r.Err);
        Assert.Equal(before, File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md")));
    }

    [Fact]
    public void Verify_dry_run_writes_nothing()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);
        var before = File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md"));

        var r = Run("verify", bundle, "metrics/dau", "--by", "human:ada", "--at", "2026-08-28T09:14:00Z", "--dry-run");

        Assert.Equal(0, r.Code);
        Assert.Equal("would record metrics/dau  human:ada  2026-08-28T09:14:00Z\n", r.Out);
        Assert.Equal(before, File.ReadAllText(Path.Combine(bundle, "metrics", "dau.md")));
    }

    [Theory]
    [InlineData(new[] { "verify", "BUNDLE" }, "error: missing <concept-id>\n")]
    [InlineData(new[] { "verify", "BUNDLE", "metrics/dau" }, "error: verify requires --by <actor>\n")]
    [InlineData(new[] { "verify", "BUNDLE", "metrics/dau", "--by", "human:" }, "error: --by is not a well-formed §7 actor: \"human:\"\n")]
    // Three shapes a permissive reader accepts and a writer must not: garbage,
    // a bare date, and a non-UTC offset.
    [InlineData(new[] { "verify", "BUNDLE", "metrics/dau", "--by", "human:ada", "--at", "hier" }, "error: --at is not a UTC timestamp of the form yyyy-MM-ddTHH:mm:ssZ: \"hier\"\n")]
    [InlineData(new[] { "verify", "BUNDLE", "metrics/dau", "--by", "human:ada", "--at", "2026-08-28" }, "error: --at is not a UTC timestamp of the form yyyy-MM-ddTHH:mm:ssZ: \"2026-08-28\"\n")]
    [InlineData(new[] { "verify", "BUNDLE", "metrics/dau", "--by", "human:ada", "--at", "2026-08-28T09:14:00+02:00" }, "error: --at is not a UTC timestamp of the form yyyy-MM-ddTHH:mm:ssZ: \"2026-08-28T09:14:00+02:00\"\n")]
    public void Verify_rejects_bad_invocations(string[] args, string expected)
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);
        var resolved = args.Select(a => a == "BUNDLE" ? bundle : a).ToArray();

        var r = Run(resolved);

        Assert.Equal(1, r.Code);
        Assert.Equal(expected, r.Err);
    }

    [Fact]
    public void Verify_refuses_to_mix_stdin_with_explicit_ids()
    {
        using var tmp = new TempDir();
        var bundle = NewBundleWithTwoConcepts(tmp);

        var r = Run("verify", bundle, "-", "metrics/dau", "--by", "human:ada");

        Assert.Equal(1, r.Code);
        Assert.Equal("error: \"-\" (stdin) cannot be combined with explicit concept ids\n", r.Err);
    }

    /// <summary>The loop, end to end: audit lists it, verify clears it.</summary>
    [Fact]
    public void Audit_then_verify_removes_the_concept_from_the_unverified_worklist()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntype: Metric\n---\n\nbody\n");

        var before = Run("audit", tmp.Path, "--trust", "unverified");
        Assert.Contains("metrics/dau", before.Out);

        Run("verify", tmp.Path, "metrics/dau", "--by", "human:ada");

        var after = Run("audit", tmp.Path, "--trust", "unverified");
        Assert.Equal("", after.Out);
    }

    [Fact]
    public void Help_lists_verify_after_audit()
    {
        var r = Run("--help");

        var lines = r.Out.Split('\n').Select(l => l.TrimStart()).ToList();
        var auditIndex = lines.FindIndex(l => l.StartsWith("audit ", StringComparison.Ordinal));
        var verifyIndex = lines.FindIndex(l => l.StartsWith("verify ", StringComparison.Ordinal));

        Assert.True(auditIndex >= 0 && verifyIndex == auditIndex + 1);
    }
```

- [ ] **Step 2: Lancer les tests pour vérifier qu'ils échouent**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~CliTests.Verify|FullyQualifiedName~CliTests.Audit_then_verify"`
Expected: `unknown subcommand: verify` sur chaque cas.

- [ ] **Step 3: Implémenter le verbe**

Dans `src/OKF4net.Cli/OkfCli.cs` — ligne d'usage, **juste après `audit`** :

```csharp
        "    verify   <bundle> <id>…   Record a review of one or more concepts (--by <actor>)\n" +
```

Bloc OPTIONS, après la ligne `--as-of` :

```csharp
        "        --by <actor>     Who is recording the review, for `verify` (required)\n" +
        "        --dry-run        Show what `verify` would record, write nothing\n" +
```

Commentaire de classe : « Eight subcommands » devient neuf, en citant `verify`.

Dispatch, après `"audit"` :

```csharp
                "verify" => CmdVerify(rest, stdin, stdout),
```

(`Run` passe `stdin` à ce seul verbe.)

Puis la méthode :

```csharp
    /// <summary>Implements the <c>verify</c> subcommand.</summary>
    private static int CmdVerify(string[] args, TextReader stdin, TextWriter stdout)
    {
        var parsed = CliArgs.Scan(args, "--by", "--at");

        // Both values are READ first, so a flag present without a value names
        // itself ("--by requires a value") rather than surfacing later as a
        // missing argument. They are VALIDATED after the ids, so that the most
        // structural mistake — no concept named at all — is reported first.
        var by = parsed.Value("--by");
        var at = parsed.Value("--at");

        var positionals = parsed.Positionals;
        var path = positionals.Count > 0 ? positionals[0] : throw new CliOperationException("missing <bundle>");
        var ids = positionals.Skip(1).ToList();
        if (ids.Count == 0)
        {
            throw new CliOperationException("missing <concept-id>");
        }

        if (ids.Contains("-"))
        {
            if (ids.Count > 1)
            {
                throw new CliOperationException("\"-\" (stdin) cannot be combined with explicit concept ids");
            }

            ids = ReadIdsFrom(stdin);
            if (ids.Count == 0)
            {
                throw new CliOperationException("no concept ids on standard input");
            }
        }

        // Validated only now: an invocation naming no concept at all is the
        // more structural mistake, and its message must come first.
        if (by is null)
        {
            throw new CliOperationException("verify requires --by <actor>");
        }

        if (!Actor.Parse(by).IsWellFormed)
        {
            throw new CliOperationException($"--by is not a well-formed §7 actor: \"{by}\"");
        }

        // The writer applies the same strict UTC rule; checking here too turns a
        // generic write error into a message naming the flag. Deliberately NOT
        // BundleValidator.IsIso8601DateTime, which only validates the date part.
        if (at is not null && !DateTime.TryParseExact(
                at,
                "yyyy-MM-dd'T'HH:mm:ss'Z'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out _))
        {
            throw new CliOperationException($"--at is not a UTC timestamp of the form yyyy-MM-ddTHH:mm:ssZ: \"{at}\"");
        }

        var bundle = Load(path);

        // Refused here as well as in the writer, so the message reads like its
        // siblings (the writer's ends with a period; the CLI's do not).
        var duplicate = ids.GroupBy(id => id, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            throw new CliOperationException($"concept '{duplicate.Key}' is named more than once");
        }

        // Every id is resolved AND checked for §11 conformance before anything
        // is written. Existence alone would not be enough: Bundle indexes any
        // document that parses, including one with no `type`, which the writer
        // then refuses at write time — so a mistyped id in third position would
        // leave the first two stamped. Both checks here, and "all-or-nothing"
        // is true rather than nearly true.
        foreach (var id in ids)
        {
            if (!ConceptId.TryParse(id, out var parsedId) || bundle.Get(parsedId!) is not { } concept)
            {
                throw new CliOperationException($"unknown concept \"{id}\"");
            }

            if (concept.Document.Frontmatter.Get("type") is not { IsEmptyValue: false })
            {
                throw new CliOperationException($"concept \"{id}\" has no `type` and is not §11-conformant");
            }
        }

        var writer = new BundleConceptWriter(path);

        if (parsed.Has("--dry-run"))
        {
            // A dry run writes nothing, so there is no timestamp to report. It
            // could format one (OkfTimestamp is reachable here), but printing a
            // date the real run would not reproduce is worse than saying "now".
            foreach (var id in ids)
            {
                stdout.Write($"would record {id}  {by}  {at ?? "(now)"}\n");
            }

            return 0;
        }

        // One batch call: the writer prepares every concept before writing any,
        // so nothing is half-stamped if a later one turns out unwritable.
        var outcome = writer.RecordVerifications(ids, by, at);
        if (!outcome.Recorded)
        {
            throw new CliOperationException(outcome.Message.Replace("Error: ", string.Empty, StringComparison.Ordinal));
        }

        foreach (var record in outcome.Records)
        {
            // record.At is the timestamp the writer actually used — the CLI
            // reports it rather than recomputing one that could differ.
            var replaces = record.ReplacedAt is { } previous ? $"  (replaces {previous})" : string.Empty;
            stdout.Write($"recorded {record.ConceptId}  {by}  {record.At}{replaces}\n");
        }

        return 0;
    }

    /// <summary>Reads concept ids from <paramref name="stdin"/>, one per line, ignoring blank lines.</summary>
    private static List<string> ReadIdsFrom(TextReader stdin)
    {
        var ids = new List<string>();
        while (stdin.ReadLine() is { } line)
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                ids.Add(trimmed);
            }
        }

        return ids;
    }
```

**Pourquoi le CLI ne calcule jamais l'horodatage.** Ce n'est pas qu'il ne peut
pas : `OKF4net.csproj` accorde `InternalsVisibleTo` à `okf` comme à
`OKF4net.Agents`, et `OkfCli.cs` importe déjà `OKF4net.Internal`. C'est qu'une
seconde horloge serait une horloge de trop — seul le writer porte le seam que
les tests épinglent, donc lui seul date, et il rapporte ce qu'il a écrit via
`outcome.At`. En `--dry-run`, rien n'est écrit : afficher `(now)` est plus
honnête qu'une date que la vraie exécution ne reproduirait pas.

- [ ] **Step 4: Lancer les tests**

Run: `dotnet test OKF4net.sln`
Expected: PASS. Aucun golden existant ne bouge.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Cli/OkfCli.cs tests/OKF4net.Tests/CliTests.cs
git commit -m "feat(cli): add the okf verify verb"
```

---

### Task 3: Le golden

**Files:**
- Create: `tests/fixtures/golden/verify.out`
- Modify: `tests/OKF4net.Tests/GoldenParityTests.cs`, `tests/fixtures/README.md`

**Interfaces:**
- Consumes: Task 2 (le format de sortie).

**Rappel de règle** : aucune fixture existante n'est modifiée. Le bundle de
travail est une **copie temporaire** de `tests/fixtures/okf_v02` (`verify`
écrit, il ne peut donc pas viser une fixture en place). Le golden est **écrit à
la main**, vérifié contre le format de la spec §5.2, jamais capturé d'un binaire
de référence — `verify` n'existe pas en amont.

- [ ] **Step 1: Écrire le golden**

`tests/fixtures/golden/verify.out`, fins de ligne **LF** :

```
recorded metrics/dau  human:ada  2026-08-28T09:14:00Z  (replaces 2026-07-03T00:00:00Z)
recorded metrics/legacy  human:ada  2026-08-28T09:14:00Z
```

Les deux lignes ne sont pas identiques par accident : `metrics/dau` porte déjà
`{ by: human:ada, at: 2026-07-03T00:00:00Z }`
([okf_v02/metrics/dau.md:10](../../../tests/fixtures/okf_v02/metrics/dau.md#L10)),
donc `UpsertStamp` prend le chemin du remplacement et la ligne porte son suffixe ;
`metrics/legacy` n'a aucune estampille, donc ajout simple. Ce golden épingle les
**deux** chemins d'un coup, ce qui est mieux qu'un golden n'exerçant que l'ajout.
Le fichier doit se terminer par un LF final (le CLI écrit `\n` après la dernière
ligne) : `.editorconfig` met `insert_final_newline = unset` sous
`tests/fixtures/**`, donc aucun outil ne l'ajoutera à ta place.

**Second golden : `tests/fixtures/golden/verify-dau.md`**, le contenu du concept
après écriture. Ne pas l'écrire de tête : lancer la commande une fois sur une
copie, lire le fichier produit, et **vérifier à la main** que chaque ligne est
justifiée avant de la figer — l'estampille `human:ada` porte le nouvel
horodatage à sa position d'origine, `process:nightly` est intacte, `generated`
n'a pas bougé, et le reste du frontmatter comme le corps sont identiques à la
fixture d'origine. C'est ce contrôle-là qui vaut, pas la capture.

- [ ] **Step 2: Écrire le test de parité**

Ajouter à `tests/OKF4net.Tests/GoldenParityTests.cs` :

```csharp
    /// <summary>
    /// `verify` writes, so it runs against a throwaway copy of the v0.2 fixture
    /// rather than the fixture itself. The golden is hand-authored and verified
    /// against the design spec's output format — there is no upstream `verify`
    /// to capture. The date is pinned with --at so it cannot drift.
    /// </summary>
    [Fact]
    public void Verify_output_matches_golden()
    {
        using var tmp = new TempDir();
        CopyDirectory(Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "okf_v02"), tmp.Path);

        var r = Run("verify", tmp.Path, "metrics/dau", "metrics/legacy", "--by", "human:ada", "--at", "2026-08-28T09:14:00Z");

        Assert.Equal(0, r.Code);
        // Concept ids only — always '/'-normalized — so no separator
        // normalization is needed on any platform.
        Assert.Equal(Golden("verify.out"), r.Out);

        // stdout alone would stay green if the verb printed the right line and
        // wrote the wrong stamp, touched `generated`, or mangled the document.
        // The written file is the artefact that matters, so it is pinned too.
        Assert.Equal(Golden("verify-dau.md"), File.ReadAllText(Path.Combine(tmp.Path, "metrics", "dau.md")));
    }
```

- [ ] **Step 3: Documenter la provenance**

Dans `tests/fixtures/README.md`, à la liste des goldens :

```markdown
- `golden/verify.out` — output of `okf verify <copy of okf_v02> metrics/dau
  metrics/legacy --by human:ada --at 2026-08-28T09:14:00Z`. **Hand-authored**,
  verified against the design spec's stated output format rather than captured
  from a reference CLI: `verify` is an OKF4net verb with no upstream
  counterpart. The bundle is a throwaway copy because the verb writes.
```

- [ ] **Step 4: Lancer les tests**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~GoldenParityTests"`
Expected: PASS. **En cas d'écart, corriger le code, jamais le golden** — sauf si l'écart révèle une erreur de ce plan, auquel cas corriger le golden ET le dire dans le message de commit.

- [ ] **Step 5: Commit**

```bash
git add tests/fixtures/golden/verify.out tests/OKF4net.Tests/GoldenParityTests.cs tests/fixtures/README.md
git commit -m "test(verify): pin the verb's output with a golden"
```

---

### Task 4: Le tool agent `okf_verify`

**Files:**
- Modify: `src/OKF4net.Agents/OkfBundleTools.cs`
- Test: `tests/OKF4net.Tests/Agents/OkfVerifyToolTests.cs` (créé), `tests/OKF4net.Tests/Agents/OkfBundleToolsTests.cs`, `tests/OKF4net.Tests/Agents/AIFunctionExposureTests.cs`, `tests/OKF4net.Tests/Mcp/OkfMcpServerTests.cs`

**Interfaces:**
- Consumes: Task 1 (`RecordVerification`, `VerificationOutcome`).
- Produces: le tool `okf_verify`, **mutateur** (dans `WriteToolNames`).

**Fallout attendu** : ajouter un 12e tool casse les tests qui figent le nombre et
l'ordre (`AIFunctionExposureTests`, `OkfBundleToolsTests`, `OkfMcpServerTests`) et
fait passer `WriteToolNames` de trois à quatre entrées. Ajuster les comptes sans
affaiblir une seule assertion (garder les égalités exactes, ne pas les
transformer en `Contains`).

- [ ] **Step 1: Écrire les tests qui échouent**

Créer `tests/OKF4net.Tests/Agents/OkfVerifyToolTests.cs` :

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Extensions.AI;
using OKF4net.Agents;

namespace OKF4net.Tests.Agents;

/// <summary>
/// Tests for <c>okf_verify</c>. The tool is symmetric with the CLI verb — same
/// actors accepted, `human:` included — a deliberate decision: a stamp is a
/// declaration, and its credibility comes from landing in a reviewed diff, not
/// from the tool that wrote it. Being a mutator, it belongs to
/// <see cref="OkfBundleTools.WriteToolNames"/> and disappears from a read-only
/// deployment.
/// </summary>
public class OkfVerifyToolTests
{
    private static OkfBundleTools ToolsOver(TempDir tmp) =>
        new(tmp.Path) { UtcNow = () => new DateTime(2026, 8, 28, 9, 14, 0, DateTimeKind.Utc) };

    [Fact]
    public void Verify_records_a_stamp()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntype: Metric\n---\n\nbody\n");

        var text = ToolsOver(tmp).Verify("metrics/dau", "human:ada");

        // Byte-identical to the CLI verb's line — the two renderers are
        // separate on purpose, so only an exact assertion keeps them aligned.
        Assert.Equal("recorded metrics/dau  human:ada  2026-08-28T09:14:00Z\n", text);
        Assert.Contains("by: human:ada", File.ReadAllText(Path.Combine(tmp.Path, "metrics", "dau.md")));
    }

    [Fact]
    public void Verify_is_registered_and_is_a_write_tool()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n\nbody\n");

        Assert.Contains("okf_verify", ToolsOver(tmp).GetTools().OfType<AIFunction>().Select(t => t.Name));
        Assert.Contains("okf_verify", OkfBundleTools.WriteToolNames);
    }

    [Fact]
    public void Verify_returns_a_usage_message_for_a_malformed_actor()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntype: Metric\n---\n\nbody\n");

        Assert.Contains("Usage: okf_verify", ToolsOver(tmp).Verify("metrics/dau", "human:"));
    }

    [Fact]
    public void Verify_reports_an_unknown_concept_without_writing()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntype: Metric\n---\n\nbody\n");

        var text = ToolsOver(tmp).Verify("metrics/nope", "human:ada");

        Assert.Contains("does not exist", text);
        Assert.False(File.Exists(Path.Combine(tmp.Path, "metrics", "nope.md")));
    }

    /// <summary>
    /// All-or-nothing across the whole list: one unknown id leaves every other
    /// concept untouched. A single-id test cannot catch this.
    /// </summary>
    [Fact]
    public void Verify_refuses_a_concept_named_twice()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n\nbody\n");
        var before = File.ReadAllText(Path.Combine(tmp.Path, "a.md"));

        var text = ToolsOver(tmp).Verify("a, a", "human:ada");

        Assert.Contains("named more than once", text);
        Assert.Equal(before, File.ReadAllText(Path.Combine(tmp.Path, "a.md")));
    }

    [Fact]
    public void Verify_writes_nothing_when_one_id_of_several_is_unknown()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n\nbody\n");
        var before = File.ReadAllText(Path.Combine(tmp.Path, "a.md"));

        var text = ToolsOver(tmp).Verify("a, nope", "human:ada");

        Assert.Contains("does not exist", text);
        Assert.DoesNotContain("recorded a", text);
        Assert.Equal(before, File.ReadAllText(Path.Combine(tmp.Path, "a.md")));
    }

    /// <summary>
    /// The schema is what decides what a bare call means, like okf_audit's:
    /// the two ids/actor parameters required, the timestamp optional.
    /// </summary>
    [Fact]
    public void Verify_schema_requires_ids_and_actor_but_not_at()
    {
        var tools = new OkfBundleTools(Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "okf_v02"));
        var function = tools.GetTools().OfType<AIFunction>().Single(t => t.Name == "okf_verify");
        var properties = function.JsonSchema.GetProperty("properties");

        foreach (var name in new[] { "conceptIds", "by", "at" })
        {
            Assert.True(properties.TryGetProperty(name, out _), $"schema should declare '{name}'.");
        }

        var required = function.JsonSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("conceptIds", required);
        Assert.Contains("by", required);
        Assert.DoesNotContain("at", required);
    }

    /// <summary>
    /// Invoked through the framework's own binding, not by calling the C#
    /// method: the arguments arrive as JSON and must reach the parameters for
    /// the stamp to land. A tool can be registered, schema-correct and still
    /// unusable from a host if that binding is wrong.
    /// </summary>
    [Fact]
    public async Task Verify_stamps_when_invoked_through_the_AIFunction_binding()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntype: Metric\n---\n\nbody\n");
        var tools = ToolsOver(tmp);
        var function = tools.GetTools().OfType<AIFunction>().Single(t => t.Name == "okf_verify");

        // Check AIFunction.InvokeAsync's exact overload against the installed
        // Microsoft.Extensions.AI before writing this call — the argument type
        // has changed across versions, and inventing a signature here is the
        // failure mode this plan exists to avoid. The arguments are:
        // conceptIds = "metrics/dau", by = "human:ada", at = "2026-08-28T09:14:00Z".
        await function.InvokeAsync(/* the version's argument shape */);

        // The emitter writes sequences in BLOCK style — a bare `-`, then the
        // mapping indented under it (verified by running `okf fmt`) — so assert
        // the two lines, never a flow-style `- { by: …, at: … }`.
        var text = File.ReadAllText(Path.Combine(tmp.Path, "metrics", "dau.md"));
        Assert.Contains("by: human:ada", text);
        Assert.Contains("at: 2026-08-28T09:14:00Z", text);
    }

    /// <summary>A bundle that vanishes after construction surfaces as an error string, never an exception.</summary>
    [Fact]
    public void Verify_returns_an_error_string_when_the_bundle_is_gone()
    {
        var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n\nbody\n");
        var tools = ToolsOver(tmp);
        tmp.Dispose();

        Assert.StartsWith("Error: ", tools.Verify("a", "human:ada"));
    }

    [Fact]
    public void Verify_stamps_every_id_in_a_comma_separated_list()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n\nbody\n");
        tmp.Write("b.md", "---\ntype: Metric\n---\n\nbody\n");

        var text = ToolsOver(tmp).Verify("a, b", "human:ada");

        Assert.Contains("recorded a  human:ada", text);
        Assert.Contains("recorded b  human:ada", text);
    }
}
```

- [ ] **Step 2: Lancer les tests pour vérifier qu'ils échouent**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OkfVerifyToolTests"`
Expected: échec de compilation — `Verify` n'existe pas.

- [ ] **Step 3: Implémenter le tool**

Dans `src/OKF4net.Agents/OkfBundleTools.cs` — la constante d'usage, à côté des autres :

```csharp
    private const string VerifyUsageMessage =
        "Usage: okf_verify records a review — comma-separated concept ids, plus a well-formed "
        + "§7 actor (human:<id>, agent:<producer>/<version>, process:<id>). Example: "
        + "okf_verify(\"metrics/dau, metrics/revenue\", \"human:ada\").";
```

`okf_verify` dans `WriteToolNames` :

```csharp
        "okf_verify",
```

La méthode :

```csharp
    /// <summary>
    /// Records a review of one or more concepts: adds — or replaces — the
    /// caller's <c>{ by, at }</c> entry in each concept's <c>verified</c> list.
    /// A stamp is a dated declaration, not a proof: this tool cannot check that
    /// the caller is who <paramref name="by"/> names, exactly like the CLI verb.
    /// </summary>
    /// <param name="conceptIds">Comma-separated concept ids; each must already exist.</param>
    /// <param name="by">The §7 actor recording the review.</param>
    /// <param name="at">ISO-8601 timestamp; omit for now.</param>
    [Description("Record a review of one or more concepts: adds or replaces the caller's { by, at } entry in each concept's `verified` list. The stamp is a dated declaration, not a proof — the same rules as the okf verify CLI verb.")]
    public string Verify(
        [Description("Comma-separated concept ids (paths without .md). Explicit ids only — there is no whole-bundle form.")] string conceptIds,
        [Description("The §7 actor recording the review, e.g. human:ada, agent:assistant/1.0, process:nightly.")] string by,
        [Description("ISO-8601 UTC timestamp; omit for now.")] string? at = null)
    {
        var ids = (conceptIds ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (ids.Count == 0 || by is null || !Actor.Parse(by).IsWellFormed)
        {
            return VerifyUsageMessage;
        }

        return RunTool(() =>
        {
            // All-or-nothing, like the CLI: every id is resolved before the
            // first write, so a typo in the third id cannot leave the first two
            // stamped. Without this, `okf_verify("a, nope", …)` writes to `a`
            // and then reports a failure — the worst of both.
            var bundle = GetBundle();
            foreach (var id in ids)
            {
                if (!ConceptId.TryParse(id, out var parsedId) || bundle.Get(parsedId!) is null)
                {
                    return $"Error: concept '{id}' does not exist.";
                }
            }

            // One batch call — all-or-nothing comes from the writer, so the
            // pre-resolution above is only there to give a nicer message.
            // `at` is passed through untouched, null included: the writer owns
            // the clock seam and reports the timestamp it used, so the tool
            // never dates anything itself.
            var outcome = _writer.RecordVerifications(ids, by, at);
            if (!outcome.Recorded)
            {
                return outcome.Message;
            }

            // The same line shape as the CLI verb, deliberately re-implemented
            // rather than shared: the CLI's bytes are golden-locked and must not
            // move because an agent-facing string was tuned. The tool's tests
            // assert this exact shape so the two cannot drift unnoticed.
            var lines = new StringBuilder();
            foreach (var record in outcome.Records)
            {
                var replaces = record.ReplacedAt is { } previous ? $"  (replaces {previous})" : string.Empty;
                lines.Append($"recorded {record.ConceptId}  {by}  {record.At}{replaces}").Append('\n');
            }

            return lines.ToString();
        });
    }
```

`InvalidateBundle()` est inutile ici : `_writer` est construit avec
`onWriteCommitted: () => _bundle = null`, donc le cache est déjà purgé à chaque
écriture — `WriteConcept` ne l'appelle pas non plus.

Enregistrer dans `GetTools()`, après `okf_write_concept` :

```csharp
            AIFunctionFactory.Create(Verify, "okf_verify"),
```

- [ ] **Step 4: Réparer le fallout et lancer les tests**

Ajuster les comptes dans `AIFunctionExposureTests` (11 → 12, liste ordonnée),
`OkfBundleToolsTests` (`WriteToolNames` : 3 → 4 entrées, sous-ensemble read-only
8 → 8 — `okf_verify` étant mutateur, il **n'entre pas** dans le read-only),
`OkfMcpServerTests` (total 11 → 12, read-only inchangé). Ajouter un test
d'invocation MCP `okf_verify` via `CallToolAsync`, sur le modèle du test
`okf_audit` existant.

Run: `dotnet test OKF4net.sln`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net.Agents/OkfBundleTools.cs tests/OKF4net.Tests/Agents tests/OKF4net.Tests/Mcp
git commit -m "feat(agents): expose okf_verify as a write tool"
```

---

### Task 5: Documentation

**Files:**
- Modify: `README.md`, `CHANGELOG.md`, `CLAUDE.md`, `ROADMAP.md`, `web/src/pages/Cli.tsx`, `web/src/pages/Home.tsx`, `web/src/pages/docs/Cli.tsx`, `web/src/pages/Library.tsx`, `web/src/pages/docs/Library.tsx`, `src/OKF4net.Agents/README.md`, `src/OKF4net.Mcp/README.md`

- [ ] **Step 1: README**

Ajouter `verify` à la liste des verbes (après `audit`), une section montrant la
boucle complète (`okf audit … | cut -d' ' -f1 | okf verify … -`), la ligne du
tableau §5.2 → `RecordVerification`, et **l'encadré d'honnêteté** : ce que
l'estampille garantit (bien formée, datée, sur les concepts nommés), ce qu'elle
ne garantit pas (l'identité du signataire, qu'il ait lu), le fait
qu'`okf_write_concept` peut aussi en écrire une, et le mécanisme recommandé —
l'estampille **dans** le diff relu, jamais inférée d'une approbation.

- [ ] **Step 2: CHANGELOG** — sous `Unreleased` :

`### Added` : le verbe et le tool. `### Changed` : **trois changements** — la
signature de `OkfCli.Run` (paramètre `TextReader` ; `OKF4net.Cli` n'a pas de
`PackageId` et n'est pas publié comme bibliothèque, donc pas de « casse les
appelants externes » : le seul site d'appel hors `Program.cs` est
`TestPaths.cs`) ; la règle du `--`, qui conserve désormais les positionnels
antérieurs (`okf <verbe> a -- b` rend `a`, non plus `b`) ; et un `-` seul, qui
devient un argument (« lire stdin ») au lieu d'être avalé comme flag.
Mettre aussi à jour `CLAUDE.md`, qui documente encore
`OkfCli.Run(args, out, err)`.

- [ ] **Step 3: CLAUDE.md et ROADMAP.md**

`CLAUDE.md` : ajouter `verify` à la liste des verbes, et une ligne disant que
`RecordVerification` est l'écrivain gouverné unique de `verified` — ne pas en
forker un second. `ROADMAP.md` : `okf verify` livré ; et l'**audit conscient du
temps** comme suite immédiate (exposer les estampilles dans `AuditFinding` pour
demander « human-reviewed, mais depuis quand, et le contenu a-t-il bougé ? »,
la question se répondant par `git log -1 -- <chemin>` contre `max(verified[].at)`).

- [ ] **Step 4: Site**

Verbe dans les deux tables (`Home.tsx`, `Cli.tsx`), chapitre dans
`docs/Cli.tsx` avec **sortie réellement capturée** (lancer la commande, ne pas
l'inventer), ligne `RecordVerification` dans les deux pages bibliothèque, et
tables de tools (12e tool) dans `src/OKF4net.Agents/README.md`,
`src/OKF4net.Mcp/README.md` et les pages correspondantes.

- [ ] **Step 5: Vérifier et committer**

```bash
dotnet format OKF4net.sln
dotnet test OKF4net.sln
cd web && npm run typecheck && npm run test && npm run build
```

```bash
git add README.md CHANGELOG.md CLAUDE.md ROADMAP.md web/ src/OKF4net.Agents/README.md src/OKF4net.Mcp/README.md
git commit -m "docs(verify): document the verb, the tool and what a stamp does not prove"
```
