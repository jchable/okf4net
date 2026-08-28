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
- **Ne jamais modifier un fichier existant sous `tests/fixtures/`.** Les goldens neufs sont écrits à la main, en LF, avec leur provenance documentée dans `tests/fixtures/README.md`.
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
`a`. Aucun test existant ne couvre ce cas (vérifié : les deux tests du séparateur
n'ont pas de positionnel avant `--`), d'où le test 3 ci-dessous et l'entrée de
CHANGELOG en Task 5.

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
    /// The CLI reads standard input only through the reader handed to
    /// <c>OkfCli.Run</c>, so a test can drive the `-` form in-process instead
    /// of spawning a subprocess.
    /// </summary>
    [Fact]
    public void Run_reads_ids_from_the_injected_stdin()
    {
        // `fmt` is the simplest verb with a positional; passing the path via
        // stdin is not supported, so this asserts the plumbing only: a reader
        // is accepted and the verb that ignores it behaves unchanged.
        var r = TestPaths.RunWithStdin("ignored\n", "fmt", Path.Combine(BundlePath, "tables", "users.md"));

        Assert.Equal(0, r.Code);
        Assert.Contains("title: Orders", r.Out);
    }
```

- [ ] **Step 2: Lancer les tests pour vérifier qu'ils échouent**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~CliTests.Separator_keeps|FullyQualifiedName~CliTests.Run_reads_ids"`
Expected: le premier ÉCHOUE (le séparateur écrase encore), le second ne compile pas (`RunWithStdin` n'existe pas).

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
    internal static (int Code, string Out, string Err) RunWithStdin(string stdin, params string[] args)
    {
        var o = new StringWriter();
        var e = new StringWriter();
        return (OkfCli.Run(args, new StringReader(stdin), o, e), o.ToString(), e.ToString());
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
- Consumes: `ValidateConceptTarget`, `_bundleLock`, `WriteValidatedContentLocked`, `RunTool`, `UtcNow`, `OkfEncodings.Strict`, `OkfTimestamp.FormatUtc`, `BundleValidator.IsIso8601DateTime`, `OkfDocument.Parse`/`ValidateConformance`/`Serialize`, `Frontmatter.AsMapping`, `Actor.Parse`, `YamlMapping.Insert/Get`, `YamlSequence.Items`, `YamlString`.
- Produces: `VerificationOutcome(bool Recorded, string Message, string? ReplacedAt)` et `BundleConceptWriter.RecordVerification(string conceptId, string by, string? at = null) → VerificationOutcome`.

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

        var outcome = WriterOver(tmp).RecordVerification("metrics/dau", "human:ada");

        Assert.True(outcome.Recorded);
        Assert.Null(outcome.ReplacedAt);

        var text = Read(tmp, "metrics/dau.md");
        Assert.Contains("by: human:ada", text);
        Assert.Contains("at: 2026-08-28T09:14:00Z", text);
        Assert.Contains("custom_key: kept", text);
        Assert.Contains("# Body", text);
    }

    [Fact]
    public void Same_actor_replaces_its_own_stamp_in_place()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "metrics/dau.md",
            Fm + "verified:\n  - { by: human:ada, at: 2026-01-01T00:00:00Z }\n"
            + "  - { by: process:nightly, at: 2026-02-02T00:00:00Z }\n---\n\nbody\n");

        var outcome = WriterOver(tmp).RecordVerification("metrics/dau", "human:ada");

        Assert.True(outcome.Recorded);
        Assert.Equal("2026-01-01T00:00:00Z", outcome.ReplacedAt);

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

        WriterOver(tmp).RecordVerification("metrics/dau", "process:nightly");

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

        WriterOver(tmp).RecordVerification("metrics/dau", "human:ada");

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

        WriterOver(tmp).RecordVerification("metrics/dau", "human:ada");

        var stamps = OkfDocument.Parse(Read(tmp, "metrics/dau.md")).Frontmatter.Verified;
        Assert.Equal(2, stamps.Count);
        Assert.Equal("process:nightly", stamps[0].By!.Value.Raw);
        Assert.Equal("human:ada", stamps[1].By!.Value.Raw);
    }

    [Theory]
    [InlineData("human:", "not a well-formed")]
    [InlineData("", "not a well-formed")]
    public void A_malformed_actor_is_refused(string by, string expected)
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");

        var outcome = WriterOver(tmp).RecordVerification("metrics/dau", by);

        Assert.False(outcome.Recorded);
        Assert.Contains(expected, outcome.Message);
        Assert.DoesNotContain("verified", Read(tmp, "metrics/dau.md"));
    }

    [Fact]
    public void A_non_iso_at_is_refused()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", Fm + "---\n\nbody\n");

        var outcome = WriterOver(tmp).RecordVerification("metrics/dau", "human:ada", "hier");

        Assert.False(outcome.Recorded);
        Assert.Contains("ISO-8601", outcome.Message);
    }

    [Fact]
    public void An_unknown_concept_is_refused_without_creating_it()
    {
        using var tmp = new TempDir();

        var outcome = WriterOver(tmp).RecordVerification("metrics/nope", "human:ada");

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

        var outcome = WriterOver(tmp).RecordVerification("metrics/dau", "human:ada");

        Assert.True(outcome.Recorded);
        Assert.Contains("by: human:ada", Read(tmp, "metrics/dau.md"));
    }

    [Fact]
    public void A_concept_without_type_is_refused()
    {
        using var tmp = new TempDir();
        tmp.Write("metrics/dau.md", "---\ntitle: No type\n---\n\nbody\n");

        var outcome = WriterOver(tmp).RecordVerification("metrics/dau", "human:ada");

        Assert.False(outcome.Recorded);
        Assert.Contains("type", outcome.Message);
    }

    [Fact]
    public void Generated_is_never_written_or_refreshed()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", Fm + "generated: { by: okf4net/0.3.0, at: 2020-01-01T00:00:00Z }\n---\n\nbody\n");
        tmp.Write("b.md", Fm + "---\n\nbody\n");

        var writer = WriterOver(tmp);
        writer.RecordVerification("a", "human:ada");
        writer.RecordVerification("b", "human:ada");

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

        writer.RecordVerification("metrics/dau", "process:nightly");
        Assert.Equal(TrustTier.MachineConfirmed, Bundle.Load(tmp.Path).Concepts[0].Document.Frontmatter.TrustTier);

        writer.RecordVerification("metrics/dau", "human:ada");
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
            () => writer.RecordVerification("metrics/dau", "human:ada"),
            () => writer.RecordVerification("metrics/dau", "process:nightly"));

        var stamps = OkfDocument.Parse(Read(tmp, "metrics/dau.md")).Frontmatter.Verified;
        Assert.Equal(2, stamps.Count);
    }
}
```

- [ ] **Step 2: Lancer les tests pour vérifier qu'ils échouent**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~RecordVerificationTests"`
Expected: échec de compilation — `RecordVerification` et `VerificationOutcome` n'existent pas.

- [ ] **Step 3: Implémenter**

Dans `src/OKF4net/BundleConceptWriter.cs`, ajouter le type de résultat au-dessus de la classe :

```csharp
/// <summary>
/// The outcome of <see cref="BundleConceptWriter.RecordVerification"/>:
/// errors-as-data, never thrown. <see cref="ReplacedAt"/> carries the
/// timestamp of the actor's previous stamp when this one replaced it, so a
/// caller can show that a review superseded an earlier one.
/// </summary>
/// <param name="Recorded">Whether the stamp was written.</param>
/// <param name="Message">A confirmation, or the reason nothing was written.</param>
/// <param name="At">
/// The timestamp actually written. Callers outside this assembly cannot format
/// one themselves — <c>OkfTimestamp</c> is internal — so the writer reports the
/// value it used rather than leaving them to guess it.
/// </param>
/// <param name="ReplacedAt">The superseded <c>at</c>, or null when the stamp is new.</param>
public readonly record struct VerificationOutcome(bool Recorded, string Message, string At, string? ReplacedAt);
```

Puis, dans la classe, la méthode et ses deux aides privées :

```csharp
    /// <summary>
    /// Records a review: adds — or replaces, in place — the <c>{ by, at }</c>
    /// entry of <paramref name="by"/> in the concept's §5.2 <c>verified</c>
    /// list, preserving every other frontmatter key and the body. The read,
    /// the edit and the write happen inside one hold of the bundle lock.
    ///
    /// A stamp is a dated declaration, not an authentication result: this
    /// method cannot and does not check that the caller is who
    /// <paramref name="by"/> names. What makes a stamp credible is where it
    /// lands — a reviewed diff — not the tool that wrote it.
    /// </summary>
    /// <param name="conceptId">The concept id (path without <c>.md</c>). Must already exist.</param>
    /// <param name="by">The §7 actor recording the review; must be well-formed.</param>
    /// <param name="at">ISO-8601 timestamp; null uses <see cref="UtcNow"/>.</param>
    public VerificationOutcome RecordVerification(string conceptId, string by, string? at = null)
    {
        if (string.IsNullOrWhiteSpace(conceptId))
        {
            return new VerificationOutcome(false, "Error: invalid concept id — it must not be empty.", string.Empty, null);
        }

        if (conceptId.Contains('\0'))
        {
            return new VerificationOutcome(false, "Error: invalid concept id — it must not contain a null character.", string.Empty, null);
        }

        // Strict on input, permissive on read: `human:` with no id promotes the
        // tier (Actor.IsHuman ignores well-formedness), so it must never be
        // written here even though a parser would accept it.
        if (by is null || !Actor.Parse(by).IsWellFormed)
        {
            return new VerificationOutcome(false, $"Error: '{by}' is not a well-formed §7 actor.", string.Empty, null);
        }

        var stampedAt = at ?? OkfTimestamp.FormatUtc(UtcNow());
        if (!BundleValidator.IsIso8601DateTime(stampedAt))
        {
            return new VerificationOutcome(false, $"Error: '{stampedAt}' is not an ISO-8601 timestamp.", stampedAt, null);
        }

        string? replacedAt = null;
        var result = RunTool(() =>
        {
            var targetError = ValidateConceptTarget(conceptId, out var target);
            if (targetError is not null)
            {
                return targetError;
            }

            lock (_bundleLock)
            {
                if (!File.Exists(target.TargetPath))
                {
                    return $"Error: concept '{conceptId}' does not exist.";
                }

                var text = OkfEncodings.Strict.GetString(File.ReadAllBytes(target.TargetPath));
                var document = OkfDocument.Parse(text);
                var map = document.Frontmatter.AsMapping();

                map.Insert("verified", UpsertStamp(map.Get("verified"), by, stampedAt, out replacedAt));

                var (content, buildError) = BuildConformantContent(map, document.Body);
                if (buildError is not null)
                {
                    return buildError;
                }

                return WriteValidatedContentLocked(target.Id, target.TargetPath, content!, existedBefore: true);
            }
        });

        return result.StartsWith("Error:", StringComparison.Ordinal)
            ? new VerificationOutcome(false, result, stampedAt, null)
            : new VerificationOutcome(true, $"Recorded {conceptId} verified by {by} at {stampedAt}.", stampedAt, replacedAt);
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
    [InlineData(new[] { "verify", "BUNDLE", "metrics/dau", "--by", "human:ada", "--at", "hier" }, "error: --at is not ISO-8601: \"hier\"\n")]
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

        if (at is not null && !BundleValidator.IsIso8601DateTime(at))
        {
            throw new CliOperationException($"--at is not ISO-8601: \"{at}\"");
        }

        var bundle = Load(path);

        // All-or-nothing: every id is resolved against the loaded bundle before
        // anything is written, so one typo cannot leave a half-stamped bundle.
        foreach (var id in ids)
        {
            if (!ConceptId.TryParse(id, out var parsedId) || bundle.Get(parsedId!) is null)
            {
                throw new CliOperationException($"unknown concept \"{id}\"");
            }
        }

        var writer = new BundleConceptWriter(path);

        if (parsed.Has("--dry-run"))
        {
            // The CLI cannot format a timestamp itself (OkfTimestamp is
            // internal to OKF4net) and must not invent one it will not write,
            // so an unpinned dry run says so rather than showing a fake date.
            foreach (var id in ids)
            {
                stdout.Write($"would record {id}  {by}  {at ?? "(now)"}\n");
            }

            return 0;
        }

        foreach (var id in ids)
        {
            var outcome = writer.RecordVerification(id, by, at);
            if (!outcome.Recorded)
            {
                throw new CliOperationException(outcome.Message.Replace("Error: ", string.Empty, StringComparison.Ordinal));
            }

            // outcome.At is the timestamp the writer actually used — the CLI
            // reports it rather than recomputing one that could differ.
            var replaces = outcome.ReplacedAt is { } previous ? $"  (replaces {previous})" : string.Empty;
            stdout.Write($"recorded {id}  {by}  {outcome.At}{replaces}\n");
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

**Pourquoi le CLI ne calcule jamais l'horodatage.** `OkfTimestamp` est
`internal` à `OKF4net` : cet assembly ne peut pas en formater un. C'est la
raison d'être du champ `At` de `VerificationOutcome` (Task 1) — le writer
rapporte la valeur qu'il a écrite, le CLI l'affiche. En `--dry-run`, rien n'est
écrit, donc rien n'est à rapporter : la sortie montre `(now)` plutôt qu'une date
inventée que la vraie exécution ne produirait pas forcément.

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
recorded metrics/dau  human:ada  2026-08-28T09:14:00Z
recorded metrics/legacy  human:ada  2026-08-28T09:14:00Z
```

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

        Assert.Contains("Recorded metrics/dau", text);
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

    [Fact]
    public void Verify_stamps_every_id_in_a_comma_separated_list()
    {
        using var tmp = new TempDir();
        tmp.Write("a.md", "---\ntype: Metric\n---\n\nbody\n");
        tmp.Write("b.md", "---\ntype: Metric\n---\n\nbody\n");

        var text = ToolsOver(tmp).Verify("a, b", "human:ada");

        Assert.Contains("Recorded a", text);
        Assert.Contains("Recorded b", text);
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
            var lines = new StringBuilder();

            // `at` is passed through untouched, null included: the writer owns
            // the clock seam (OkfTimestamp is internal to OKF4net, so this
            // assembly could not format a timestamp anyway) and reports the one
            // it used. The tool invents no date.
            foreach (var id in ids)
            {
                var outcome = _writer.RecordVerification(id, by, at);
                lines.Append(outcome.Message).Append('\n');
            }

            InvalidateBundle();
            return lines.ToString();
        });
    }
```

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

`### Added` : le verbe et le tool. `### Changed` : **deux ruptures** — la
signature de `OkfCli.Run` (paramètre `TextReader`), et la règle du `--` qui
conserve désormais les positionnels antérieurs (`okf <verbe> a -- b` rend `a`, non
plus `b`).

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
