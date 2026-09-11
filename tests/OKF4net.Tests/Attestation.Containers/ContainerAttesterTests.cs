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
}
