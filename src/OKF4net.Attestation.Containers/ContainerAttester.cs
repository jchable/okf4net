// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// <see cref="IAttester"/> for every runtime: an attester script is a
/// Python *library* (e.g. exposing <c>attest(**kwargs)</c>), not a
/// standalone program, so a fixed bootstrap imports it by path and calls a
/// known function with fixed kwarg names — this project's own convention
/// (§10 leaves invocation entirely host-defined). Always runs on
/// <see cref="ContainerAttesterOptions.Image"/>, never the executor's
/// profile image (a <see cref="ContainerRuntimeKind.SqlClient"/> image has
/// no Python at all).
/// </summary>
public sealed class ContainerAttester(IContainerEngine engine, ContainerAttesterOptions options) : IAttester
{
    /// <summary>
    /// Reads the JSON envelope from stdin, writes <c>attester_source</c> to a
    /// temp file inside the container, imports it, and calls
    /// <c>attest(**kwargs)</c>. Redirects stdout to a buffer for the whole
    /// import+call so a stray <c>print()</c> inside the bundle's own module
    /// can never corrupt the one JSON line this prints at the very end (the
    /// design's finding #9).
    /// </summary>
    private const string Bootstrap = """
        import sys, json, importlib.util, tempfile, io, contextlib
        envelope = json.load(sys.stdin)
        f = tempfile.NamedTemporaryFile(suffix='.py', delete=False, mode='w', encoding='utf-8')
        f.write(envelope['attester_source'])
        f.close()
        buf = io.StringIO()
        with contextlib.redirect_stdout(buf):
            spec = importlib.util.spec_from_file_location('okf_attester', f.name)
            module = importlib.util.module_from_spec(spec)
            spec.loader.exec_module(module)
            result = module.attest(**envelope['kwargs'])
        sys.stdout.write(json.dumps(result))
        """;

    /// <inheritdoc />
    public async ValueTask<AttestationVerdict> AttestAsync(AttestationContext context, CancellationToken cancellationToken = default)
    {
        if (context.AttesterSourceText is null)
        {
            throw new ContainerExecutionException("concept has no resolvable attester.resource", "", "");
        }

        var envelope = JsonSerializer.Serialize(new
        {
            attester_source = context.AttesterSourceText,
            kwargs = new
            {
                sanctioned_computation = context.Computation.InlineCode,
                receipt = context.Receipt.Fields,
                // Bound.Values, NOT context.Values: the binder's filtered, type-checked
                // set, never the caller's raw dictionary. The attester executes
                // bundle-authored code against caller data, so an undeclared key reaching
                // it would defeat the allowlist on the half of the pipeline where it
                // matters most -- and a raw dictionary can also carry an arbitrary CLR
                // object that JsonSerializer refuses, surfacing as a bogus "attester
                // threw". Both executors already use this set.
                values = context.Bound.Values,
            },
        });

        var spec = new ContainerRunSpec(
            Image: options.Image,
            Command: ["python3", "-c", Bootstrap],
            Stdin: envelope,
            Environment: options.Environment,
            NetworkMode: "none",
            MemoryBytes: options.MemoryBytes,
            Cpus: options.Cpus,
            PidsLimit: options.PidsLimit,
            Timeout: options.Timeout)
        {
            ReadOnlyRootFilesystem = options.ReadOnlyRootFilesystem,
            TmpfsMounts = options.TmpfsMounts,
        };

        var result = await engine.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            throw new ContainerExecutionException($"attester exited with code {result.ExitCode}", result.Stdout, result.Stderr);
        }

        JsonElement verdict;
        try
        {
            verdict = JsonSerializer.Deserialize<JsonElement>(result.Stdout);
        }
        catch (JsonException e)
        {
            throw new ContainerExecutionException($"attester stdout was not valid JSON: {e.Message}", result.Stdout, result.Stderr);
        }

        var passed = verdict.ValueKind == JsonValueKind.Object
            && verdict.TryGetProperty("ok", out var okProp)
            && okProp.ValueKind == JsonValueKind.True;
        var detail = verdict.ValueKind == JsonValueKind.Object
            && verdict.TryGetProperty("reason", out var reasonProp)
            && reasonProp.ValueKind == JsonValueKind.String
                ? reasonProp.GetString()
                : null;
        return new AttestationVerdict(passed, detail);
    }
}
