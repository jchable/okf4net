// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;
using OKF4net;
using OKF4net.Attestation;
using OKF4net.Attestation.Containers;
using Xunit;

namespace OKF4net.Tests.Attestation.Containers;

public class ContainerAttesterTests
{
    private static AttestationContext Context(string? attesterSource, Receipt receipt) => new(
        Contract: new AttestedComputationContract("python", [], null, null, new Attester("att.py")),
        Computation: new SanctionedComputation(ComputationSource.Inline, "print()", null),
        Bound: new BoundComputation("python", "print()", null, new Dictionary<string, object?>()),
        Values: new Dictionary<string, object?>(),
        Receipt: receipt,
        AttesterSourceText: attesterSource);

    [Fact]
    public async Task Sends_the_attester_source_and_kwargs_on_stdin_never_via_the_image()
    {
        var engine = new FakeContainerEngine { Respond = _ => new ContainerRunResult(0, """{"ok": true, "reason": null}""", "") };
        var options = new ContainerAttesterOptions();
        var attester = new ContainerAttester(engine, options);

        var verdict = await attester.AttestAsync(Context("def attest(**_):\n    return {}\n", new Receipt(new Dictionary<string, object?> { ["message"] = "hi" })));

        Assert.True(verdict.Passed);
        var envelope = JsonSerializer.Deserialize<JsonElement>(engine.LastSpec!.Stdin!);
        Assert.Equal("def attest(**_):\n    return {}\n", envelope.GetProperty("attester_source").GetString());
        Assert.Equal(options.Image, engine.LastSpec.Image);
    }

    [Fact]
    public async Task Uses_its_own_fixed_image_never_the_executors()
    {
        var engine = new FakeContainerEngine { Respond = _ => new ContainerRunResult(0, """{"ok": true}""", "") };
        var attester = new ContainerAttester(engine, new ContainerAttesterOptions { Image = "python:3.13-slim" });

        await attester.AttestAsync(Context("def attest(**_):\n    return {}\n", new Receipt(new Dictionary<string, object?>())));

        Assert.Equal("python:3.13-slim", engine.LastSpec!.Image);
    }

    [Fact]
    public async Task A_missing_verdict_field_is_treated_as_a_failing_verdict()
    {
        var engine = new FakeContainerEngine { Respond = _ => new ContainerRunResult(0, "{}", "") };
        var attester = new ContainerAttester(engine, new ContainerAttesterOptions());

        var verdict = await attester.AttestAsync(Context("def attest(**_):\n    return {}\n", new Receipt(new Dictionary<string, object?>())));

        Assert.False(verdict.Passed);
    }

    /// <summary>
    /// The verdict is container JSON too, under the receipt's strict contract: a
    /// duplicated <c>ok</c> was resolved by whichever occurrence
    /// <c>TryGetProperty</c> found, so <c>{"ok": false, "ok": true}</c> could read as
    /// a pass while another reader of the same output saw a failure; and the number
    /// rule covers every number anywhere in the document, not only the fields read.
    /// </summary>
    [Theory]
    [InlineData("""{"ok": false, "ok": true}""", "attester stdout had a duplicate JSON property")]
    [InlineData("""{"ok": true, "detail": {"k": 1, "k": 2}}""", "attester stdout had a duplicate JSON property")]
    [InlineData("""{"ok": true, "score": 1e400}""", "attester stdout had a number that cannot be represented exactly")]
    [InlineData("""{"ok": true, "rows": [9223372036854775808]}""", "attester stdout had a number that cannot be represented exactly")]
    public async Task A_verdict_that_breaks_the_strict_JSON_contract_fails_the_stage(string stdout, string message)
    {
        var engine = new FakeContainerEngine { Respond = _ => new ContainerRunResult(0, stdout, "") };
        var attester = new ContainerAttester(engine, new ContainerAttesterOptions());

        var ex = await Assert.ThrowsAsync<ContainerExecutionException>(
            async () => await attester.AttestAsync(Context("def attest(**_):\n    return {}\n", new Receipt(new Dictionary<string, object?>()))));

        Assert.Equal(message, ex.Message);
    }

    [Fact]
    public async Task Throws_when_the_concept_has_no_resolvable_attester_source()
    {
        var engine = new FakeContainerEngine();
        var attester = new ContainerAttester(engine, new ContainerAttesterOptions());

        await Assert.ThrowsAsync<ContainerExecutionException>(
            async () => await attester.AttestAsync(Context(null, new Receipt(new Dictionary<string, object?>()))));
    }

    /// <summary>
    /// The attester is the one stage that executes bundle-authored code against
    /// caller-supplied data, so the allowlist has to hold here above all. It must
    /// receive <see cref="BoundComputation.Values"/> — the set
    /// <see cref="AllowlistParameterBinder"/> filtered and type-checked against the
    /// concept's declared <c>parameters</c> — and never the caller's raw dictionary.
    /// A key the concept never declared must not reach the script at all: the design's
    /// finding #7 applies to this half of the pipeline exactly as it does to the
    /// executors. Sending the raw dictionary also hands arbitrary caller CLR objects to
    /// <c>JsonSerializer.Serialize</c>, which can throw and surface as a bogus
    /// "attester threw".
    /// </summary>
    [Fact]
    public async Task Sends_only_the_declared_values_never_the_raw_caller_dictionary()
    {
        var engine = new FakeContainerEngine { Respond = _ => new ContainerRunResult(0, """{"ok": true, "reason": null}""", "") };
        var attester = new ContainerAttester(engine, new ContainerAttesterOptions());

        var context = new AttestationContext(
            Contract: new AttestedComputationContract("python", [new ComputationParameter("name", "string", true)], null, null, new Attester("att.py")),
            Computation: new SanctionedComputation(ComputationSource.Inline, "print()", null),
            // What the binder produced: the declared parameter only.
            Bound: new BoundComputation("python", "print()", null, new Dictionary<string, object?> { ["name"] = "Ada" }),
            // What the caller passed: the declared parameter plus an undeclared one.
            Values: new Dictionary<string, object?> { ["name"] = "Ada", ["undeclared"] = "leaked" },
            Receipt: new Receipt(new Dictionary<string, object?> { ["message"] = "hi" }),
            AttesterSourceText: "def attest(**_):\n    return {}\n");

        await attester.AttestAsync(context);

        var values = JsonSerializer.Deserialize<JsonElement>(engine.LastSpec!.Stdin!)
            .GetProperty("kwargs")
            .GetProperty("values");

        Assert.Equal("Ada", values.GetProperty("name").GetString());
        Assert.False(values.TryGetProperty("undeclared", out _));
    }

    private static readonly Receipt EmptyReceipt = new(new Dictionary<string, object?>());

    private const string PassingAttester = "def attest(**_):\n    return {}\n";

    /// <summary>
    /// The default must keep behaving exactly as before the fix: <c>/tmp</c> mounted,
    /// and the bootstrap's temp module written there — now because <c>TMPDIR</c> says
    /// so rather than because the Python assumed it.
    /// </summary>
    [Fact]
    public async Task Points_TMPDIR_at_the_default_tmp_mount()
    {
        var engine = new FakeContainerEngine();
        await new ContainerAttester(engine, new ContainerAttesterOptions()).AttestAsync(Context(PassingAttester, EmptyReceipt));

        Assert.Equal(["/tmp"], engine.LastSpec!.TmpfsMounts);
        Assert.Equal("/tmp", engine.LastSpec.Environment["TMPDIR"]);
    }

    /// <summary>
    /// The defect this pins: <c>Isolation.TmpfsMounts</c> was documented as configurable, but the
    /// bootstrap's <c>NamedTemporaryFile</c> wrote to <c>/tmp</c> whatever was mounted, so
    /// <c>["/scratch"]</c> under a read-only root failed every attestation. The first
    /// mount is the scratch directory; the rest are mounted and left alone. The
    /// executable half of this guard is
    /// <c>ContainerIntegrationTests.A_custom_tmpfs_mount_with_no_tmp_is_honoured_by_the_script_executor_and_the_attester</c>.
    /// </summary>
    [Fact]
    public async Task Points_TMPDIR_at_the_first_configured_mount_not_at_tmp()
    {
        var engine = new FakeContainerEngine();
        var options = new ContainerAttesterOptions { Isolation = new ContainerAttesterOptions().Isolation with { TmpfsMounts = ["/scratch", "/var/tmp"] } };
        await new ContainerAttester(engine, options).AttestAsync(Context(PassingAttester, EmptyReceipt));

        Assert.True(engine.LastSpec!.ReadOnlyRootFilesystem);
        Assert.Equal(["/scratch", "/var/tmp"], engine.LastSpec.TmpfsMounts);
        Assert.Equal("/scratch", engine.LastSpec.Environment["TMPDIR"]);
    }

    /// <summary>
    /// <c>--tmpfs</c> takes <c>path[:options]</c>. The options belong on the mount; a
    /// <c>TMPDIR</c> of <c>/scratch:size=64m</c> names a directory that does not exist,
    /// and Python's <c>tempfile</c> would quietly skip it and fall back to <c>/tmp</c>.
    /// </summary>
    [Fact]
    public async Task TMPDIR_is_the_mount_path_without_its_engine_options()
    {
        var engine = new FakeContainerEngine();
        var options = new ContainerAttesterOptions { Isolation = new ContainerAttesterOptions().Isolation with { TmpfsMounts = ["/scratch:size=64m,mode=1777"] } };
        await new ContainerAttester(engine, options).AttestAsync(Context(PassingAttester, EmptyReceipt));

        Assert.Equal(["/scratch:size=64m,mode=1777"], engine.LastSpec!.TmpfsMounts);
        Assert.Equal("/scratch", engine.LastSpec.Environment["TMPDIR"]);
    }

    /// <summary>A <c>TMPDIR</c> the host set explicitly is a decision, not a gap to fill.</summary>
    [Fact]
    public async Task A_TMPDIR_the_host_set_wins_over_the_derived_one()
    {
        var engine = new FakeContainerEngine();
        var options = new ContainerAttesterOptions
        {
            Isolation = new ContainerAttesterOptions().Isolation with { TmpfsMounts = ["/scratch", "/work"] },
            Environment = new Dictionary<string, string> { ["TMPDIR"] = "/work" },
        };
        await new ContainerAttester(engine, options).AttestAsync(Context(PassingAttester, EmptyReceipt));

        Assert.Equal("/work", engine.LastSpec!.Environment["TMPDIR"]);
    }

    /// <summary>
    /// The bootstrap writes a temp file on every run, so a read-only root with nothing
    /// mounted can never attest anything. That is caught when the attester is built,
    /// not discovered as a Python traceback after the executor already ran the
    /// computation — against a live database, for a SqlClient profile.
    /// </summary>
    [Fact]
    public void A_read_only_root_with_no_tmpfs_mount_is_rejected_when_the_attester_is_built()
    {
        var engine = new FakeContainerEngine();
        var ex = Assert.Throws<ArgumentException>(
            () => new ContainerAttester(engine, new ContainerAttesterOptions { Isolation = new ContainerAttesterOptions().Isolation with { TmpfsMounts = [] } }));

        Assert.Equal("options", ex.ParamName);
        Assert.Contains("TmpfsMounts", ex.Message, StringComparison.Ordinal);
        Assert.Null(engine.LastSpec);
    }

    /// <summary>
    /// A <c>TMPDIR</c> the host set wins over the derived one, and Python's
    /// <c>tempfile</c> does not create it: it skips an unusable one and walks on through
    /// <c>TEMP</c>, <c>TMP</c>, <c>/tmp</c>, <c>/var/tmp</c>, <c>/usr/tmp</c> and the
    /// working directory. Under a read-only root with only <c>/scratch</c> mounted, every
    /// one of those is read-only — "No usable temporary directory", verified against real
    /// Docker on <c>python:3.12-slim</c> for each case below. Rejected when the attester
    /// is built, for the same reason an empty <c>Isolation.TmpfsMounts</c> is. An empty value is
    /// skipped by <c>tempfile</c>, a subdirectory of a mount does not exist in a fresh
    /// tmpfs, and <c>/run</c> is not writable under docker's <c>--read-only</c>.
    /// </summary>
    [Theory]
    [InlineData("/work", new[] { "/scratch" })]
    [InlineData("", new[] { "/scratch" })]
    [InlineData("/scratch/sub", new[] { "/scratch" })]
    [InlineData("/run", new[] { "/scratch" })]
    [InlineData("/scratch", new[] { "/scratch:ro" })]
    [InlineData("/scratch", new[] { "/scratch:size=64m,ro" })]
    public void A_read_only_root_whose_TMPDIR_reaches_no_tmpfs_mount_is_rejected_when_the_attester_is_built(
        string tmpdir, string[] mounts)
    {
        var engine = new FakeContainerEngine();
        var options = new ContainerAttesterOptions
        {
            Isolation = new ContainerAttesterOptions().Isolation with { TmpfsMounts = mounts },
            Environment = new Dictionary<string, string> { ["TMPDIR"] = tmpdir },
        };
        var ex = Assert.Throws<ArgumentException>(() => new ContainerAttester(engine, options));

        Assert.Equal("options", ex.ParamName);
        Assert.Contains("TMPDIR", ex.Message, StringComparison.Ordinal);
        Assert.Contains("TmpfsMounts", ex.Message, StringComparison.Ordinal);
        Assert.Null(engine.LastSpec);
    }

    /// <summary>
    /// The guard follows <c>tempfile</c>'s fallbacks rather than demanding that
    /// <c>TMPDIR</c> itself be a mount, so it does not reject a configuration that works.
    /// Each case was verified to work against real Docker on <c>python:3.12-slim</c>: a
    /// mount matched on its container path (engine options, a trailing slash, and the
    /// lexical resolution <c>abspath</c> applies against the image's <c>/</c> working
    /// directory do not matter), docker's own <c>/dev/shm</c> tmpfs, and an unusable or
    /// empty <c>TMPDIR</c> rescued by a mounted <c>/tmp</c> or <c>/var/tmp</c>.
    /// </summary>
    [Theory]
    [InlineData("/work", new[] { "/scratch", "/work" })]
    [InlineData("/scratch/", new[] { "/scratch:size=64m" })]
    [InlineData("/scratch", new[] { "/scratch:ro,rw" })]
    [InlineData("scratch", new[] { "/scratch" })]
    [InlineData("./scratch", new[] { "/scratch" })]
    [InlineData("/work/../scratch", new[] { "/scratch" })]
    [InlineData("/dev/shm", new[] { "/scratch" })]
    [InlineData("/work", new[] { "/tmp" })]
    [InlineData("", new[] { "/tmp" })]
    [InlineData("/work", new[] { "/var/tmp" })]
    public async Task A_read_only_root_whose_TMPDIR_reaches_a_tmpfs_mount_is_accepted(string tmpdir, string[] mounts)
    {
        var engine = new FakeContainerEngine();
        var options = new ContainerAttesterOptions
        {
            Isolation = new ContainerAttesterOptions().Isolation with { TmpfsMounts = mounts },
            Environment = new Dictionary<string, string> { ["TMPDIR"] = tmpdir },
        };
        await new ContainerAttester(engine, options).AttestAsync(Context(PassingAttester, EmptyReceipt));

        Assert.Equal(tmpdir, engine.LastSpec!.Environment["TMPDIR"]);
    }

    /// <summary>
    /// With no <c>TMPDIR</c> from the host, the derived one points at the first mount — and
    /// a <c>ro</c> mount is not writable (verified against real Docker), so this is rejected
    /// too, with a message that names the derived value rather than failing to find one.
    /// </summary>
    [Fact]
    public void A_derived_TMPDIR_on_a_read_only_tmpfs_mount_is_rejected_when_the_attester_is_built()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new ContainerAttester(new FakeContainerEngine(), new ContainerAttesterOptions { Isolation = new ContainerAttesterOptions().Isolation with { TmpfsMounts = ["/scratch:ro"] } }));

        Assert.Contains("TMPDIR '/scratch'", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// The environment is copied when the attester is built, as the mounts are, so the
    /// check cannot be bypassed by changing the host's dictionary afterwards.
    /// </summary>
    [Fact]
    public async Task Changing_the_environment_dictionary_after_construction_does_not_reach_the_run()
    {
        var engine = new FakeContainerEngine();
        var environment = new Dictionary<string, string> { ["TMPDIR"] = "/scratch" };
        var attester = new ContainerAttester(engine, new ContainerAttesterOptions { Isolation = new ContainerAttesterOptions().Isolation with { TmpfsMounts = ["/scratch"] }, Environment = environment });

        environment["TMPDIR"] = "/work";
        await attester.AttestAsync(Context(PassingAttester, EmptyReceipt));

        Assert.Equal("/scratch", engine.LastSpec!.Environment["TMPDIR"]);
    }

    /// <summary><c>TEMP</c> is the next variable <c>tempfile</c> reads, so it can rescue an unusable <c>TMPDIR</c> (verified against real Docker).</summary>
    [Fact]
    public void A_TEMP_that_names_a_tmpfs_mount_rescues_an_unusable_TMPDIR()
    {
        var options = new ContainerAttesterOptions
        {
            Isolation = new ContainerAttesterOptions().Isolation with { TmpfsMounts = ["/scratch"] },
            Environment = new Dictionary<string, string> { ["TMPDIR"] = "/work", ["TEMP"] = "/scratch" },
        };

        _ = new ContainerAttester(new FakeContainerEngine(), options);
    }

    /// <summary>With a writable root, a <c>TMPDIR</c> outside the mounts still has a writable fallback.</summary>
    [Fact]
    public async Task A_TMPDIR_outside_the_tmpfs_mounts_is_allowed_when_the_root_is_writable()
    {
        var engine = new FakeContainerEngine();
        var options = new ContainerAttesterOptions
        {
            Isolation = new ContainerAttesterOptions().Isolation with { ReadOnlyRootFilesystem = false, TmpfsMounts = ["/scratch"] },
            Environment = new Dictionary<string, string> { ["TMPDIR"] = "/work" },
        };
        await new ContainerAttester(engine, options).AttestAsync(Context(PassingAttester, EmptyReceipt));

        Assert.Equal("/work", engine.LastSpec!.Environment["TMPDIR"]);
    }

    /// <summary>
    /// With a writable root there is always somewhere to write, so no mount is fine —
    /// and with no mount there is no scratch to point <c>TMPDIR</c> at, so the image's
    /// own default stands.
    /// </summary>
    [Fact]
    public async Task No_tmpfs_mount_is_allowed_when_the_root_is_writable_and_sets_no_TMPDIR()
    {
        var engine = new FakeContainerEngine();
        var options = new ContainerAttesterOptions { Isolation = new ContainerAttesterOptions().Isolation with { ReadOnlyRootFilesystem = false, TmpfsMounts = [] } };
        await new ContainerAttester(engine, options).AttestAsync(Context(PassingAttester, EmptyReceipt));

        Assert.False(engine.LastSpec!.Environment.ContainsKey("TMPDIR"));
    }

    /// <summary>
    /// A SOURCE-TEXT SMOKE CHECK, not proof: xunit cannot execute the Python in
    /// <see cref="ContainerAttester.Bootstrap"/>. It only pins that the text never names
    /// a directory, so the temp module goes wherever <c>TMPDIR</c> points. The executable
    /// guard is
    /// <c>ContainerIntegrationTests.A_custom_tmpfs_mount_with_no_tmp_is_honoured_by_the_script_executor_and_the_attester</c>,
    /// run against real Docker.
    /// </summary>
    [Fact]
    public void The_bootstrap_never_names_a_temp_directory_itself()
    {
        Assert.DoesNotContain("/tmp", ContainerAttester.Bootstrap, StringComparison.Ordinal);
        Assert.DoesNotContain("dir=", ContainerAttester.Bootstrap, StringComparison.Ordinal);
    }
}
