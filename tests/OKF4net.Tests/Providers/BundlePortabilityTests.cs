// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Cli;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>
/// The default policy (<see cref="BundleKeyPolicy.Native"/>, maintainer ruling 2026-10-19):
/// a load refuses only what the provider's storage cannot hold or would alias; what the
/// portable profile (<see cref="BundleKeyPolicy.Strict"/>) would refuse beyond that becomes a
/// <see cref="Bundle.PortabilityIssues"/> entry and a <see cref="DiagnosticCode.KeyNotPortable"/>
/// warning. OKF §11 imposes no naming constraint, so none of these names makes a bundle
/// non-conformant by itself.
/// </summary>
public class BundlePortabilityTests
{
    private const string Doc = "---\ntype: Table\ntitle: T\ndescription: d\n---\n\nBody.\n";
    private static readonly BundleLoadOptions StrictOptions = new() { KeyPolicy = BundleKeyPolicy.Strict };

    private static string Nfd => "cafe" + (char)0x0301 + ".md";

    private static string StrictReason(BundleKeyConstraints native, string raw) =>
        new BundleKeyValidator(native, BundleKeyPolicy.Strict).CheckKey(BundleKey.Parse(raw))!;

    [Fact]
    public void The_default_policy_is_native()
    {
        Assert.Equal(BundleKeyPolicy.Native, BundleLoadOptions.Default.KeyPolicy);
        Assert.Equal(BundleKeyPolicy.Native, new BundleLoadOptions().KeyPolicy);
        Assert.Equal(BundleKeyPolicy.Native, default(BundleKeyPolicy));
    }

    [Theory]
    [InlineData("con.md", "con", "reserved Windows device name 'CON'")]
    [InlineData("Con.md", "Con", "reserved Windows device name 'CON'")]
    [InlineData("nul.txt.md", "nul.txt", "reserved Windows device name 'NUL'")]
    public void On_linux_constraints_a_windows_reserved_name_loads_as_a_concept_with_a_warning(string raw, string id, string expectedInReason)
    {
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.Linux).Add(raw, Doc).Add("ok.md", Doc);
        var bundle = Bundle.Load(p);
        var concept = bundle.Get(ConceptId.Parse(id));
        Assert.NotNull(concept);
        Assert.Empty(bundle.ParseProblems);
        Assert.Empty(bundle.RejectedKeys);

        var reason = StrictReason(BundleKeyConstraintSets.Linux, raw);
        Assert.Contains(expectedInReason, reason);
        var issue = Assert.Single(bundle.PortabilityIssues);
        Assert.Equal(new BundlePortabilityIssue(BundleKey.Parse(raw), reason), issue);

        var report = BundleValidator.Validate(bundle);
        Assert.True(report.IsConformant);
        var warning = Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.KeyNotPortable);
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Equal(concept.Path, warning.Path);
        Assert.Equal(concept.Id, warning.Concept);
        Assert.Equal("the name is not portable: " + reason, warning.Message);

        // The portable profile refuses the same name as before, with the same reason.
        var strict = Bundle.Load(p, StrictOptions);
        Assert.Null(strict.Get(ConceptId.Parse(id)));
        Assert.Empty(strict.PortabilityIssues);
        var problem = Assert.Single(strict.ParseProblems);
        Assert.Equal(raw, problem.RawName);
        Assert.Equal(reason, problem.Error);
        Assert.DoesNotContain(BundleValidator.Validate(strict).Diagnostics, d => d.Code == DiagnosticCode.KeyNotPortable);
    }

    [Fact]
    public void A_space_before_the_extension_is_portable_and_no_concept_id_as_on_dev()
    {
        // `a .md` does not END with a space: Windows holds it, so it is no portability issue.
        // The concept id rule (`[A-Za-z0-9_][A-Za-z0-9_.-]*`) refuses it, as `dev` did.
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.Linux).Add("a .md", Doc).Add("ok.md", Doc);
        Assert.Null(new BundleKeyValidator(BundleKeyConstraintSets.Linux, BundleKeyPolicy.Strict).CheckKey(BundleKey.Parse("a .md")));
        foreach (var bundle in new[] { Bundle.Load(p), Bundle.Load(p, StrictOptions) })
        {
            Assert.Equal([ConceptId.Parse("ok")], bundle.Concepts.Select(c => c.Id));
            Assert.Empty(bundle.RejectedKeys);
            var problem = Assert.Single(bundle.ParseProblems);
            Assert.Equal("a .md", problem.RawName);
            Assert.Contains("Invalid concept id segment", problem.Error);
            Assert.Empty(bundle.PortabilityIssues);
        }
    }

    [Fact]
    public void On_linux_constraints_a_segment_ending_with_a_dot_loads_as_a_concept_with_a_warning()
    {
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.Linux).Add("docs./x.md", Doc);
        var bundle = Bundle.Load(p);
        var concept = Assert.Single(bundle.Concepts);
        Assert.Equal(ConceptId.Parse("docs./x"), concept.Id);
        var reason = StrictReason(BundleKeyConstraintSets.Linux, "docs./x.md");
        Assert.Contains("ends with '.' or a space", reason);
        Assert.Equal([new BundlePortabilityIssue(BundleKey.Parse("docs./x.md"), reason)], bundle.PortabilityIssues);
        Assert.Single(BundleValidator.Validate(bundle).Diagnostics, d => d.Code == DiagnosticCode.KeyNotPortable && concept.Id.Equals(d.Concept));

        var strict = Bundle.Load(p, StrictOptions);
        Assert.Empty(strict.Concepts);
        Assert.Equal(reason, Assert.Single(strict.ParseProblems).Error);
    }

    [Fact]
    public void On_linux_constraints_a_directory_ending_with_a_space_is_accepted_and_reported_but_is_no_concept_id()
    {
        // The key is one the storage holds; the concept id rule refuses the space, as on `dev`,
        // so the file is an unparseable document AND a non-portable name.
        const string raw = "a /x.md";
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.Linux).Add(raw, Doc).Add("ok.md", Doc);
        var bundle = Bundle.Load(p);
        Assert.Equal([ConceptId.Parse("ok")], bundle.Concepts.Select(c => c.Id));
        Assert.Empty(bundle.RejectedKeys);
        var problem = Assert.Single(bundle.ParseProblems);
        Assert.Equal(raw, problem.RawName);
        Assert.Contains("Invalid concept id segment", problem.Error);

        var reason = StrictReason(BundleKeyConstraintSets.Linux, raw);
        Assert.Contains("ends with '.' or a space", reason);
        Assert.Equal([new BundlePortabilityIssue(BundleKey.Parse(raw), reason)], bundle.PortabilityIssues);

        var report = BundleValidator.Validate(bundle);
        var warning = Assert.Single(report.Diagnostics, d => d.Code == DiagnosticCode.KeyNotPortable);
        Assert.Equal(Severity.Warning, warning.Severity);
        Assert.Equal(BundleDisplayPath.Compose(p.Location, BundleKey.Parse(raw)), warning.Path);
        Assert.Null(warning.Concept);
        Assert.Equal("the name is not portable: " + reason, warning.Message);

        // Reported right after the unparseable-document error of the same file.
        var all = report.Diagnostics.ToList();
        Assert.Equal(all.FindIndex(d => d.Code == DiagnosticCode.UnparseableDocument) + 1, all.IndexOf(warning));

        var strict = Bundle.Load(p, StrictOptions);
        Assert.Equal(reason, Assert.Single(strict.ParseProblems).Error);
        Assert.Empty(strict.PortabilityIssues);
    }

    [Fact]
    public void On_linux_constraints_two_names_that_differ_only_by_case_both_load_with_a_warning_each()
    {
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.Linux).Add("Orders.md", Doc).Add("orders.md", Doc);
        var bundle = Bundle.Load(p);
        Assert.Equal(2, bundle.Concepts.Count);
        Assert.Empty(bundle.ParseProblems);
        Assert.Empty(bundle.RejectedPrefixes);
        Assert.Equal(
            [
                new BundlePortabilityIssue(BundleKey.Parse("Orders.md"), "'Orders.md' collides with a sibling that differs only by case or Unicode form"),
                new BundlePortabilityIssue(BundleKey.Parse("orders.md"), "'orders.md' collides with a sibling that differs only by case or Unicode form"),
            ],
            bundle.PortabilityIssues);

        var warnings = BundleValidator.Validate(bundle).Diagnostics.Where(d => d.Code == DiagnosticCode.KeyNotPortable).ToList();
        Assert.Equal(2, warnings.Count);
        foreach (var concept in bundle.Concepts)
        {
            var w = Assert.Single(warnings, d => d.Concept == concept.Id);
            Assert.Equal(concept.Path, w.Path);
            Assert.Equal($"the name is not portable: '{concept.Key}' collides with a sibling that differs only by case or Unicode form", w.Message);
        }

        var strict = Bundle.Load(p, StrictOptions);
        Assert.Empty(strict.Concepts);
        Assert.Equal(2, strict.ParseProblems.Count);
        Assert.All(strict.ParseProblems, e => Assert.Contains("collides with a sibling", e.Error));
        Assert.Equal([BundleKey.Parse("Orders.md"), BundleKey.Parse("orders.md")], strict.RejectedPrefixes);
    }

    [Fact]
    public void On_macos_constraints_a_decomposed_name_is_accepted_and_reported()
    {
        Assert.False(BundleKeyConstraintSets.MacOS.HasFlag(BundleKeyConstraints.RequireNfc));
        var nfd = Nfd;
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.MacOS).Add(nfd, Doc).Add("ok.md", Doc);
        var bundle = Bundle.Load(p);

        // The key is accepted (macOS holds a decomposed name); like any non-ASCII name it is
        // no concept id, which `dev` reported the same way.
        Assert.Empty(bundle.RejectedKeys);
        Assert.Contains("Invalid concept id segment", Assert.Single(bundle.ParseProblems).Error);
        var reason = StrictReason(BundleKeyConstraintSets.MacOS, nfd);
        Assert.Contains("U+0301", reason);
        Assert.Equal([new BundlePortabilityIssue(BundleKey.Parse(nfd), reason)], bundle.PortabilityIssues);
        var warning = Assert.Single(BundleValidator.Validate(bundle).Diagnostics, d => d.Code == DiagnosticCode.KeyNotPortable);
        Assert.Equal("the name is not portable: " + reason, warning.Message);

        var strict = Bundle.Load(p, StrictOptions);
        Assert.Equal(reason, Assert.Single(strict.ParseProblems).Error);
        Assert.Contains(BundleKey.Parse(nfd), strict.RejectedKeys);
    }

    [Fact]
    public void On_macos_constraints_a_decomposed_name_beside_its_precomposed_twin_both_load_with_a_warning_each()
    {
        // Both forms can coexist only on a volume that does not compare normalized names, and
        // there they are two files: no native refusal, two portability issues.
        var nfc = "caf" + (char)0x00E9 + ".md";
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.MacOS).Add(nfc, Doc).Add(Nfd, Doc);
        var bundle = Bundle.Load(p);
        Assert.Empty(bundle.RejectedKeys);
        Assert.Empty(bundle.RejectedPrefixes);
        Assert.DoesNotContain(bundle.ParseProblems, e => e.Error.Contains("collides"));
        Assert.Equal(2, bundle.PortabilityIssues.Count);
        Assert.Contains(bundle.PortabilityIssues, i => i.Key.Equals(BundleKey.Parse(nfc)) && i.Reason.Contains("collides with a sibling"));
        Assert.Contains(bundle.PortabilityIssues, i => i.Key.Equals(BundleKey.Parse(Nfd)) && i.Reason.Contains("U+0301"));

        var strict = Bundle.Load(p, StrictOptions);
        Assert.Equal(2, strict.ParseProblems.Count);
        Assert.Contains(strict.ParseProblems, e => e.RawName == nfc && e.Error.Contains("collides with a sibling"));
    }

    [Theory]
    [MemberData(nameof(NativeSets))]
    public void On_every_native_set_two_names_that_differ_only_by_case_both_load_with_a_warning_each(string set, BundleKeyConstraints native)
    {
        // A case-sensitive APFS volume or Windows directory holds both, as Linux does: the
        // default loads both, as `dev` did, and the portable profile reports them.
        var p = new InMemoryBundleProvider(native).Add("Orders.md", Doc).Add("orders.md", Doc);
        var bundle = Bundle.Load(p);
        Assert.Equal(2, bundle.Concepts.Count);
        Assert.Empty(bundle.ParseProblems);
        Assert.Equal([BundleKey.Parse("Orders.md"), BundleKey.Parse("orders.md")], bundle.PortabilityIssues.Select(i => i.Key));
        Assert.Equal(2, BundleValidator.Validate(bundle).Diagnostics.Count(d => d.Code == DiagnosticCode.KeyNotPortable));
        Assert.Empty(Bundle.Load(p, StrictOptions).Concepts);
        Assert.NotNull(set);
    }

    public static TheoryData<string, BundleKeyConstraints> NativeSets => new()
    {
        { "Windows", BundleKeyConstraintSets.Windows },
        { "MacOS", BundleKeyConstraintSets.MacOS },
        { "Linux", BundleKeyConstraintSets.Linux },
    };

    [Fact]
    public void A_provider_that_declares_case_folding_still_refuses_both_spellings_natively()
    {
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.MacOS | BundleKeyConstraints.CaseInsensitiveCollisions).Add("Orders.md", Doc).Add("orders.md", Doc);
        var bundle = Bundle.Load(p);
        Assert.Empty(bundle.Concepts);
        Assert.Equal(2, bundle.ParseProblems.Count(e => e.Error.Contains("collides with a sibling")));
        Assert.Empty(bundle.PortabilityIssues);
    }

    [Fact]
    public void Portable_names_and_strict_loads_have_no_issue()
    {
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.Linux).Add("tables/orders.md", Doc).Add("index.md", "# Root\n");
        Assert.Empty(Bundle.Load(p).PortabilityIssues);
        var withIssue = new InMemoryBundleProvider(BundleKeyConstraintSets.Linux).Add("con.md", Doc);
        Assert.Empty(Bundle.Load(withIssue, StrictOptions).PortabilityIssues);
    }

    [Fact]
    public void A_non_portable_index_is_reported_without_a_concept()
    {
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.Linux).Add("CON/index.md", "# Index\n").Add("CON/x.md", Doc);
        var bundle = Bundle.Load(p);
        Assert.Equal([BundleKey.Parse("CON/index.md"), BundleKey.Parse("CON/x.md")], bundle.PortabilityIssues.Select(i => i.Key));
        var warnings = BundleValidator.Validate(bundle).Diagnostics.Where(d => d.Code == DiagnosticCode.KeyNotPortable).ToList();
        Assert.Equal(2, warnings.Count);
        Assert.Contains(warnings, w => w.Concept is null && w.Path == BundleDisplayPath.Compose(p.Location, BundleKey.Parse("CON/index.md")));
        Assert.Contains(warnings, w => ConceptId.Parse("CON/x").Equals(w.Concept));
    }

    [Fact]
    public void The_portability_pass_counts_names_refused_for_their_length()
    {
        // `Docs/longname.md` (16 bytes) is refused for a 12-byte bound, yet `Docs` is still
        // beside `docs`: the portable profile would refuse `docs/a.md` for that.
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.Linux) { ObeyLimits = false }
            .Add("Docs/longname.md", Doc)
            .Add("docs/a.md", Doc);
        var bundle = Bundle.Load(p, new BundleLoadOptions { Limits = new BundleLimits { MaxKeyBytes = 12 } });
        Assert.Equal([ConceptId.Parse("docs/a")], bundle.Concepts.Select(c => c.Id));
        var issue = Assert.Single(bundle.PortabilityIssues);
        Assert.Equal(BundleKey.Parse("docs/a.md"), issue.Key);
        Assert.Equal("'docs' collides with a sibling that differs only by case or Unicode form", issue.Reason);
    }

    [Fact]
    public void Extra_validators_restrict_the_load_and_never_become_portability_issues()
    {
        var options = new BundleLoadOptions { ExtraKeyValidators = [k => k.Name == "x.md" ? "host refuses x" : null] };
        var p = new InMemoryBundleProvider(BundleKeyConstraintSets.Linux).Add("x.md", Doc).Add("con.md", Doc);
        var bundle = Bundle.Load(p, options);
        Assert.Contains(bundle.ParseProblems, e => e.RawName == "x.md" && e.Error == "host refuses x");
        Assert.Equal([BundleKey.Parse("con.md")], bundle.PortabilityIssues.Select(i => i.Key));
    }

    /// <summary>
    /// End to end on a real filesystem that holds both spellings (Linux, a case-sensitive
    /// APFS volume or Windows directory): <c>okf validate</c> prints what <c>dev</c> printed
    /// plus one warning line per name, and still exits 0 — warnings never change the exit
    /// code. Skipped where the volume folds case, since it cannot hold both names.
    /// </summary>
    [SkippableFact]
    public void Validate_on_a_case_sensitive_filesystem_prints_one_warning_per_colliding_name_and_exits_zero()
    {
        using var tmp = new TempDir();
        tmp.Write("Orders.md", Doc);
        tmp.Write("orders.md", Doc);
        Skip.If(Directory.GetFiles(tmp.Path).Length != 2, "this volume is case-insensitive: it cannot hold both names");

        var bundle = Bundle.Load(tmp.Path);
        Assert.Equal(2, bundle.Count);

        // What `dev` printed: every other finding (the recommended-field warnings of this
        // document), unchanged and in the same order.
        var others = BundleValidator.Validate(bundle).Diagnostics.Where(d => d.Code != DiagnosticCode.KeyNotPortable).ToList();
        Assert.All(others, d => Assert.Equal(Severity.Warning, d.Severity));
        string[] newLines = [.. bundle.Concepts.Select(c =>
            $"[warning] {c.Path}: the name is not portable: '{c.Key}' collides with a sibling that differs only by case or Unicode form")];

        var stdout = new StringWriter { NewLine = "\n" };
        var stderr = new StringWriter { NewLine = "\n" };
        Assert.Equal(0, OkfCli.Run(["validate", tmp.Path], TextReader.Null, stdout, stderr));
        Assert.Equal(string.Empty, stderr.ToString());
        var lines = stdout.ToString().Split('\n');
        var findings = lines.Where(l => l.StartsWith('[')).ToList();
        Assert.Equal(newLines, findings.Where(l => l.Contains(": the name is not portable: ", StringComparison.Ordinal)));
        Assert.Equal(others.Select(d => d.ToString()), findings.Where(l => !l.Contains(": the name is not portable: ", StringComparison.Ordinal)));
        Assert.Contains($"2 concept(s); 0 error(s), {others.Count + 2} warning(s), 0 info.", lines);
        Assert.Contains($"✓ conformant with OKF v{OkfSpec.Version}", lines);

        var jsonOut = new StringWriter { NewLine = "\n" };
        Assert.Equal(0, OkfCli.Run(["validate", tmp.Path, "--json"], TextReader.Null, jsonOut, new StringWriter()));
        using var json = System.Text.Json.JsonDocument.Parse(jsonOut.ToString());
        var codes = json.RootElement.GetProperty("diagnostics").EnumerateArray().Select(d => d.GetProperty("code").GetString()).ToList();
        Assert.Equal(2, codes.Count(c => c == "KeyNotPortable"));
        Assert.Equal(others.Count + 2, codes.Count);
    }
}
