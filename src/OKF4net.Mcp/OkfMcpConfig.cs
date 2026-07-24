// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Mcp;

/// <summary>
/// Resolves the server's startup configuration (bundle root + read-only flag)
/// from command-line arguments and environment variables. Pure and injectable
/// (via the <c>getEnv</c> accessor on <see cref="TryResolve"/>) so both
/// success and failure paths are unit-testable.
/// </summary>
public static class OkfMcpConfig
{
    /// <summary>Environment variable naming the bundle root when no positional argument is given.</summary>
    public const string BundleRootEnv = "OKF_BUNDLE_ROOT";

    /// <summary>Environment variable enabling read-only mode when truthy.</summary>
    public const string ReadOnlyEnv = "OKF_MCP_READONLY";

    private static readonly IReadOnlySet<string> TruthyValues =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "1", "true", "yes", "on" };

    /// <summary>
    /// Resolves configuration. The bundle root is the first non-blank argument,
    /// else the <c>OKF_BUNDLE_ROOT</c> environment variable. Returns
    /// <see langword="false"/> with a human-readable <paramref name="error"/>
    /// when no root is given or the root does not exist.
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
    {
        bundleRoot = string.Empty;
        readOnly = TruthyValues.Contains(getEnv(ReadOnlyEnv)?.Trim() ?? string.Empty);

        var root = args.Count > 0 && !string.IsNullOrWhiteSpace(args[0])
            ? args[0]
            : getEnv(BundleRootEnv);

        if (string.IsNullOrWhiteSpace(root))
        {
            error = $"no bundle root given. Pass it as the first argument or set {BundleRootEnv}.";
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
}
