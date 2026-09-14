// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using OKF4net.Attestation.Containers.Internal;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// <see cref="IAttester"/> for every runtime: an attester script is a
/// Python *library* (e.g. exposing <c>attest(**kwargs)</c>), not a
/// standalone program, so a fixed bootstrap imports it by path and calls a
/// known function with fixed kwarg names — this project's own convention
/// (§10 leaves invocation entirely host-defined). Always runs on
/// <see cref="ContainerAttesterOptions.Image"/>, never the executor's
/// profile image: that image is whatever the sanctioned code needs (a
/// <see cref="ContainerRuntimeKind.Script"/> image may have no Python at all),
/// so the bootstrap cannot assume one.
/// </summary>
public sealed class ContainerAttester : IAttester
{
    private readonly IContainerEngine _engine;
    private readonly ContainerAttesterOptions _options;

    /// <summary>
    /// Creates an attester that runs on <paramref name="options"/>' image. Throws
    /// <see cref="ArgumentException"/> when <paramref name="options"/> mounts the root
    /// filesystem read-only with no <see cref="ContainerAttesterOptions.TmpfsMounts"/>:
    /// the bootstrap writes the attester module to a temp file on every run, so that
    /// configuration could never attest anything — and would only say so at run time,
    /// as a Python traceback, after the computation had already been executed.
    /// </summary>
    public ContainerAttester(IContainerEngine engine, ContainerAttesterOptions options)
    {
        ArgumentNullException.ThrowIfNull(engine);
        ArgumentNullException.ThrowIfNull(options);
        if (options.ReadOnlyRootFilesystem && options.TmpfsMounts.Count == 0)
        {
            throw new ArgumentException(
                "ContainerAttesterOptions mounts the root filesystem read-only with no TmpfsMounts, so the attester bootstrap has nowhere to write the module it imports; add a tmpfs mount (e.g. \"/tmp\") or set ReadOnlyRootFilesystem = false.",
                nameof(options));
        }

        _engine = engine;
        _options = options;
    }

    /// <summary>
    /// Reads the JSON envelope from stdin, writes <c>attester_source</c> to a
    /// temp file inside the container — in <c>TMPDIR</c>, which
    /// <see cref="AttestAsync"/> points at the first configured tmpfs mount, so a
    /// read-only root with <c>/scratch</c> mounted instead of <c>/tmp</c> works;
    /// this text never names a directory itself — imports it, and calls
    /// <c>attest(**kwargs)</c>. Redirects stdout to a buffer for the whole
    /// import+call so a stray <c>print()</c> inside the bundle's own module
    /// can never corrupt the one JSON line this prints at the very end (the
    /// design's finding #9).
    /// </summary>
    internal const string Bootstrap = """
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
            Image: _options.Image,
            Command: ["python3", "-c", Bootstrap],
            Stdin: envelope,
            // TMPDIR -> the first tmpfs mount. NamedTemporaryFile writes wherever it
            // points; left unset that is /tmp, which is read-only whenever the host
            // mounted its scratch somewhere else.
            Environment: ScratchDirectory.Apply(_options.Environment, _options.TmpfsMounts),
            NetworkMode: "none",
            MemoryBytes: _options.MemoryBytes,
            Cpus: _options.Cpus,
            PidsLimit: _options.PidsLimit,
            Timeout: _options.Timeout)
        {
            ReadOnlyRootFilesystem = _options.ReadOnlyRootFilesystem,
            TmpfsMounts = _options.TmpfsMounts,
        };

        var result = await _engine.RunAsync(spec, cancellationToken).ConfigureAwait(false);
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
