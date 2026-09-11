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
/// the SQL).
///
/// <para><b>Receipt contract.</b> <c>stdout</c> carries one JSON object and
/// nothing else: <c>executed_sql</c> (the text sent, echoed back) and
/// <c>result</c> (the rows, as a list of column-keyed objects). A column value
/// JSON cannot represent natively — <c>NUMERIC</c>, <c>DATE</c>,
/// <c>TIMESTAMP</c>, <c>UUID</c>, <c>BYTEA</c> — arrives as its Python
/// <c>str()</c> form rather than failing the run.</para>
///
/// <para><b>Known trade-off.</b> <see cref="Wrapper"/> pip-installs its
/// driver on every run, so a run needs network access to a package index and
/// pays the install cost each time. The alternative, a purpose-built image
/// with the driver vendored in, is the better answer for anything beyond
/// local use.</para>
/// </summary>
public sealed class SqlClientComputationExecutor(IContainerEngine engine, ContainerRuntimeProfile profile) : IComputationExecutor
{
    private const string Wrapper = """
        import sys, os, json, subprocess
        # stdout is the receipt channel and nothing else: pip's own output must never
        # land on it. --quiet alone is not enough -- it only makes this usually
        # invisible, which is what made it a latent, environment-dependent failure
        # (a warning, a progress line, a resolver message and the receipt is
        # unparseable). DEVNULL makes it structural. stderr is left alone so a real
        # install failure is still diagnosable; check=True still aborts on one.
        subprocess.run([sys.executable, '-m', 'pip', 'install', '--quiet', 'pg8000'],
                       check=True, stdout=subprocess.DEVNULL)
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
            # default=str: Postgres returns plenty of types json.dumps cannot encode --
            # NUMERIC as Decimal, DATE/TIMESTAMP as date/datetime, UUID, BYTEA as bytes.
            # Without it the dump raises TypeError AFTER the query has already run
            # against the live database, so the receipt is lost and the run reports a
            # bare "SQL wrapper exited with code 1" for a query that actually succeeded.
            # The receipt contract is therefore: non-JSON-native column values arrive as
            # their Python str() form.
            sys.stdout.write(json.dumps({'executed_sql': sql, 'result': result}, default=str))
        finally:
            conn.close()
        """;

    /// <summary>
    /// The names a declared parameter cannot take, because
    /// <c>pg8000.native.Connection.run</c> already uses them:
    /// <c>(self, sql, stream=None, types=None, **params)</c>. Two distinct failures
    /// hide behind one list, and they are not equally loud:
    /// <list type="bullet">
    /// <item><c>self</c> and <c>sql</c> are bound positionally by
    /// <c>conn.run(sql, **values)</c>, so supplying them again raises
    /// <c>TypeError: got multiple values for argument …</c> — noisy, but attributed
    /// to the bundle's query.</item>
    /// <item><c>stream</c> and <c>types</c> raise <b>nothing at all</b>. They are
    /// optional parameters of the driver, so the value is silently consumed as a
    /// driver option and never reaches <c>**params</c> — the query runs with its
    /// placeholder unbound. That is the worse of the two, and the reason this check
    /// is a rejection rather than a nicer error message.</item>
    /// </list>
    /// Kept here rather than in the shared filter because the restriction belongs to
    /// this transport alone.
    /// </summary>
    private static readonly string[] DriverReservedParameterNames = ["self", "sql", "stream", "types"];

    /// <inheritdoc />
    public async ValueTask<Receipt> ExecuteAsync(
        BoundComputation bound,
        AttestedComputationContract contract,
        CancellationToken cancellationToken = default)
    {
        // The wrapper binds with `conn.run(sql, **values)`, so a parameter named like
        // one of the driver's own arguments collides with its signature -- either
        // loudly (a TypeError blamed on the bundle's query) or, for the driver's
        // optional arguments, silently, with the value consumed as a driver option
        // and the placeholder left unbound. See DriverReservedParameterNames for
        // which does which. Caught here, at this transport only: a Script profile
        // passes values as JSON in an environment variable and has no reserved
        // names, so this must not live in the shared DeclaredParameterFilter.
        foreach (var reserved in DriverReservedParameterNames)
        {
            if (bound.Values.ContainsKey(reserved))
            {
                throw new ArgumentException(
                    $"parameter '{reserved}' collides with a reserved keyword argument of the SQL driver; rename it in the concept's `parameters`.");
            }
        }

        var envelope = JsonSerializer.Serialize(new { sql = bound.BoundText ?? "", values = bound.Values });

        var spec = new ContainerRunSpec(
            Image: profile.Image,
            Command: ["python3", "-c", Wrapper],
            Stdin: envelope,
            Environment: profile.Environment,
            NetworkMode: profile.NetworkMode,
            MemoryBytes: profile.MemoryBytes,
            Cpus: profile.Cpus,
            PidsLimit: profile.PidsLimit,
            Timeout: profile.Timeout);

        var result = await engine.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        return ReceiptParsing.Parse(result, "SQL wrapper");
    }
}
