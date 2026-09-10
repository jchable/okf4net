// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using OKF4net.Attestation.Containers.Internal;

namespace OKF4net.Attestation.Containers;

/// <summary>
/// <see cref="IComputationExecutor"/> for <see cref="ContainerRuntimeKind.SqlClient"/>
/// profiles. Runs the bound SQL text — byte-for-byte unchanged, placeholders
/// included — through <see cref="Wrapper"/>, a fixed Python program that
/// binds <see cref="BoundComputation.Values"/> via a pure-Python database
/// driver's own native parameter mechanism (never string interpolation into
/// the SQL). <see cref="Wrapper"/> pip-installs its driver at run time —
/// see the plan task's "known trade-off" note.
/// </summary>
public sealed class SqlClientComputationExecutor(IContainerEngine engine, ContainerRuntimeProfile profile) : IComputationExecutor
{
    private const string Wrapper = """
        import sys, os, json, subprocess
        subprocess.run([sys.executable, '-m', 'pip', 'install', '--quiet', 'pg8000'], check=True)
        import pg8000.native
        from urllib.parse import urlparse
        envelope = json.load(sys.stdin)
        sql = envelope['sql']
        values = envelope.get('values') or {}
        u = urlparse(os.environ['OKF_CONN'])
        conn = pg8000.native.Connection(user=u.username, password=u.password, host=u.hostname, port=u.port or 5432, database=u.path.lstrip('/'))
        try:
            rows = conn.run(sql, **values)
            cols = [c['name'] for c in conn.columns] if conn.columns else []
            result = [dict(zip(cols, row)) for row in rows]
            sys.stdout.write(json.dumps({'executed_sql': sql, 'result': result}))
        finally:
            conn.close()
        """;

    /// <inheritdoc />
    public async ValueTask<Receipt> ExecuteAsync(
        BoundComputation bound,
        AttestedComputationContract contract,
        CancellationToken cancellationToken = default)
    {
        var envelope = JsonSerializer.Serialize(new { sql = bound.BoundText ?? "", values = bound.Values });

        var spec = new ContainerRunSpec(
            Image: profile.Image,
            Command: ["python3", "-c", Wrapper],
            Stdin: envelope,
            Environment: profile.Environment,
            NetworkMode: null,
            MemoryBytes: profile.MemoryBytes,
            Cpus: profile.Cpus,
            PidsLimit: profile.PidsLimit,
            Timeout: profile.Timeout);

        var result = await engine.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        return ReceiptParsing.Parse(result, "SQL wrapper");
    }
}
