// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Mcp;

/// <summary>
/// Resolves the server's startup configuration (bundle root + read-only flag)
/// from command-line arguments and environment variables. Pure and injectable
/// (via the <c>getEnv</c> accessor on <see cref="TryResolve(IReadOnlyList{string}, Func{string, string?}, out string, out bool, out string?)"/>) so both
/// success and failure paths are unit-testable.
/// </summary>
public static class OkfMcpConfig
{
    /// <summary>Environment variable naming the bundle root when no positional argument is given.</summary>
    public const string BundleRootEnv = "OKF_BUNDLE_ROOT";

    /// <summary>
    /// Environment variable forcing read-only mode when truthy.
    ///
    /// Read-only is now the DEFAULT, so this is no longer how you get it —
    /// it is retained because it cannot make the server less safe: when it
    /// disagrees with <see cref="WritableEnv"/>, this one wins. Existing
    /// configurations that set it keep working and keep meaning the same thing.
    /// </summary>
    public const string ReadOnlyEnv = "OKF_MCP_READONLY";

    /// <summary>
    /// Environment variable opting IN to the three write tools when truthy.
    ///
    /// Writes used to be the default. That put unconfirmed write access to the
    /// corpus behind nothing on the surface most people actually deploy — a
    /// desktop client's MCP config — while bundle content is untrusted by
    /// design. Serving a bundle for consultation is the common case and the
    /// safe one, so it is what you get for free.
    /// </summary>
    public const string WritableEnv = "OKF_MCP_WRITABLE";

    private static readonly IReadOnlySet<string> TruthyValues =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1", "true", "yes", "on" };

    /// <summary>
    /// Resolves configuration. The bundle root is the first positional argument,
    /// else the <c>OKF_BUNDLE_ROOT</c> environment variable. Returns
    /// <see langword="false"/> with a human-readable <paramref name="error"/>
    /// when no root is given or the root does not exist. Discovery, when it
    /// applies, starts from the current working directory — see the
    /// <c>startDirectory</c> overload.
    /// </summary>
    /// <param name="args">Process arguments (positional bundle root at index 0).</param>
    /// <param name="getEnv">Environment-variable accessor (e.g. <see cref="Environment.GetEnvironmentVariable(string)"/>).</param>
    /// <param name="bundleRoot">The resolved bundle root (empty on failure).</param>
    /// <param name="readOnly">Whether read-only mode is requested.</param>
    /// <param name="error">The failure reason, or <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryResolve(
        IReadOnlyList<string> args,
        Func<string, string?> getEnv,
        out string bundleRoot,
        out bool readOnly,
        out string? error)
        => TryResolve(args, getEnv, Directory.GetCurrentDirectory(), out bundleRoot, out readOnly, out error);

    /// <summary>
    /// <see cref="TryResolve(IReadOnlyList{string}, Func{string, string?}, string, Func{string, string?}, out string, out bool, out string?)"/>
    /// with the production root-index reader
    /// (<see cref="OkfBundleDiscovery.ReadRootIndexOrNull"/>).
    /// </summary>
    /// <param name="args">Process arguments (positional bundle root at index 0).</param>
    /// <param name="getEnv">Environment-variable accessor.</param>
    /// <param name="startDirectory">Directory discovery walks up from when no explicit root is given.</param>
    /// <param name="bundleRoot">The resolved bundle root (empty on failure).</param>
    /// <param name="readOnly">Whether read-only mode is requested.</param>
    /// <param name="error">The failure reason, or <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryResolve(
        IReadOnlyList<string> args,
        Func<string, string?> getEnv,
        string startDirectory,
        out string bundleRoot,
        out bool readOnly,
        out string? error)
        => TryResolve(args, getEnv, startDirectory, OkfBundleDiscovery.ReadRootIndexOrNull, out bundleRoot, out readOnly, out error);

    /// <summary>
    /// Resolves configuration. The bundle root is the first positional
    /// argument, else the <c>OKF_BUNDLE_ROOT</c> environment variable, else a
    /// bundle discovered by <see cref="OkfBundleDiscovery.TryDiscover"/>
    /// walking up from <paramref name="startDirectory"/>. Discovery never
    /// overrides an explicit root: a nonexistent argument or environment root
    /// is still an error. Returns <see langword="false"/> with a
    /// human-readable <paramref name="error"/> when no root can be resolved.
    /// </summary>
    /// <param name="args">Process arguments (positional bundle root at index 0).</param>
    /// <param name="getEnv">Environment-variable accessor.</param>
    /// <param name="startDirectory">Directory discovery walks up from when no explicit root is given.</param>
    /// <param name="readRootIndex">Candidate directory → root index text accessor handed to discovery (injectable for hermetic tests).</param>
    /// <param name="bundleRoot">The resolved bundle root (empty on failure).</param>
    /// <param name="readOnly">Whether read-only mode is requested.</param>
    /// <param name="error">The failure reason, or <see langword="null"/> on success.</param>
    /// <returns><see langword="true"/> on success.</returns>
    public static bool TryResolve(
        IReadOnlyList<string> args,
        Func<string, string?> getEnv,
        string startDirectory,
        Func<string, string?> readRootIndex,
        out string bundleRoot,
        out bool readOnly,
        out string? error)
    {
        bundleRoot = string.Empty;

        // Read-only unless writes are explicitly requested, and an explicit
        // ReadOnlyEnv still wins over WritableEnv. The two can only disagree
        // through a configuration mistake, and of the two ways to resolve one,
        // only this one cannot turn a mistake into a writable server.
        readOnly = !TruthyValues.Contains(getEnv(WritableEnv)?.Trim() ?? string.Empty)
            || TruthyValues.Contains(getEnv(ReadOnlyEnv)?.Trim() ?? string.Empty);

        var root = args.Count > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0]
            : getEnv(BundleRootEnv);

        if (string.IsNullOrWhiteSpace(root))
        {
            // Directory.Exists mirrors the explicit-root check below: the root
            // was just probed by discovery, but a delete in between must yield
            // the one-line error contract, not an exception at load time.
            if (OkfBundleDiscovery.TryDiscover(startDirectory, readRootIndex, out var discovered)
                && Directory.Exists(discovered))
            {
                bundleRoot = discovered;
                error = null;
                return true;
            }

            // ReplaceLineEndings guards the single-line stderr contract: a
            // (legal, on Unix) newline in the CWD path must not break it.
            error = $"no bundle root given and no marked bundle found from {startDirectory.ReplaceLineEndings(" ")} upward. "
                + $"Pass a root as the first argument, set {BundleRootEnv}, or mark the bundle by adding okf_version to its root index.md frontmatter "
                + "(the OKF Claude Code plugin's /okf-init does this for you).";
            return false;
        }

        root = root.Trim();
        if (!Directory.Exists(root))
        {
            error = $"bundle root not found: {root}";
            return false;
        }

        bundleRoot = root;
        error = null;
        return true;
    }

    /// <summary>
    /// Formats a <em>single-line</em> startup usage/error for stderr, per the
    /// server design spec's "print a one-line usage/error to stderr and exit
    /// non-zero" contract. The returned string contains no line break, so the
    /// caller writes it with a single <see cref="Console.Error"/> call rather
    /// than an error line plus a separate usage line.
    /// </summary>
    /// <param name="error">The failure reason from <see cref="TryResolve(IReadOnlyList{string}, Func{string, string?}, out string, out bool, out string?)"/> (may be <see langword="null"/>).</param>
    /// <returns>A one-line message combining the error and the usage hint.</returns>
    public static string FormatStartupError(string? error)
    {
        var message = string.IsNullOrWhiteSpace(error) ? "startup configuration error." : error.Trim();
        if (!message.EndsWith('.'))
        {
            message += ".";
        }

        return $"okf-mcp: {message} Usage: okf-mcp <bundle-root> (or set {BundleRootEnv}, or run inside a bundle whose root index.md declares okf_version; read-only unless {WritableEnv}=1).";
    }
}
