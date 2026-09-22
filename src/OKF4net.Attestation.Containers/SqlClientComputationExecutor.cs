// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text.Json;
using OKF4net.Attestation;
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
/// <para><b>Driver policy.</b> <see cref="Wrapper"/> imports its driver first and
/// pip-installs it — pinned to one version, never whatever the index serves
/// today into a process holding <c>OKF_CONN</c> — only when the image does not
/// already provide it. On a bare Python image that means network access to a
/// package index and the install cost on every run; an image with the driver
/// vendored in skips the install entirely, can run with
/// <see cref="ContainerRuntimeProfile.NetworkMode"/> closed down to its database's
/// network, and is the better answer for anything beyond local use.</para>
/// </summary>
public sealed class SqlClientComputationExecutor(IContainerEngine engine, ContainerRuntimeProfile profile) : IComputationExecutor
{
    internal const string Wrapper = """
        import sys, os, json
        try:
            # Import first. An image that vendors the driver never reaches for a
            # package index, which is what lets a host run this profile with its
            # network closed down to the database. pip is the fallback for a bare
            # Python image, and only then.
            import pg8000.native
        except ImportError:
            import subprocess, tempfile
            # stdout is the receipt channel and nothing else: pip's own output must never
            # land on it. --quiet alone is not enough -- it only makes this usually
            # invisible, which is what made it a latent, environment-dependent failure
            # (a warning, a progress line, a resolver message and the receipt is
            # unparseable). DEVNULL makes it structural. stderr is left alone so a real
            # install failure is still diagnosable; check=True still aborts on one.
            # The version is pinned: this process holds OKF_CONN, so "whatever the
            # index serves today" is not an acceptable thing to import into it. Keep
            # it in step with the vendored image ContainerIntegrationTests builds.
            #
            # --target into the tmpfs rather than the image's site-packages, because
            # the root filesystem is mounted read-only and an ordinary install fails
            # on /root/.local. The tmpfs is memory-backed and dies with the container,
            # which is where a per-run driver install belongs anyway. sys.path has to
            # be told about it before the retry.
            #
            # Which tmpfs is the host's choice, so the path is never written here: the
            # executor sets TMPDIR to the first configured mount, and gettempdir()
            # follows it. That is load-bearing twice -- pip unpacks and builds in
            # TMPDIR too, so even a correct --target fails when TMPDIR is left
            # pointing at a read-only /tmp.
            pkgs = os.path.join(tempfile.gettempdir(), 'okf-pkgs')
            subprocess.run([sys.executable, '-m', 'pip', 'install', '--quiet',
                            '--target', pkgs, 'pg8000==1.31.5'],
                           check=True, stdout=subprocess.DEVNULL)
            sys.path.insert(0, pkgs)
            import pg8000.native
        from urllib.parse import urlparse, unquote
        envelope = json.load(sys.stdin)
        sql = envelope['sql']
        values = envelope.get('values') or {}
        u = urlparse(os.environ['OKF_CONN'])
        # urlparse hands userinfo back still percent-encoded; libpq decodes it,
        # so a password spelled `p%40ss` (the only way to write `p@ss` in a URL)
        # must be decoded here or it never authenticates.
        conn = pg8000.native.Connection(
            user=unquote(u.username or ''), password=unquote(u.password or ''),
            host=u.hostname, port=u.port or 5432, database=unquote(u.path.lstrip('/')))
        try:
            rows = conn.run(sql, **values)
            # A statement with no result set (DDL, INSERT without RETURNING)
            # returns None, and iterating it raised AFTER the statement had
            # already run -- a side-effecting run reported as a failure.
            cols = [c['name'] for c in conn.columns] if conn.columns else []
            result = [dict(zip(cols, row)) for row in (rows or [])]
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
                throw new AttestationDiagnosticException(
                    $"parameter '{reserved}' collides with a reserved keyword argument of the SQL driver; rename it in the concept's `parameters`.");
            }
        }

        var envelope = JsonSerializer.Serialize(new { sql = bound.BoundText ?? "", values = bound.Values });

        // ToRunSpec points TMPDIR at the first tmpfs mount, which is where the wrapper's
        // fallback install goes (see the comment above its pip call).
        var spec = profile.Isolation.ToRunSpec(profile.Image, ["python3", "-c", Wrapper], envelope, profile.Environment, profile.NetworkMode);

        var result = await engine.RunAsync(spec, cancellationToken).ConfigureAwait(false);
        return ReceiptParsing.Parse(result, "SQL wrapper");
    }
}
