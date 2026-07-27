// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Mcp;

namespace OKF4net.Tests.Mcp;

public sealed class OkfMcpConfigTests
{
    private static Func<string, string?> Env(params (string Key, string Value)[] pairs)
    {
        var map = pairs.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal);
        return key => map.TryGetValue(key, out var v) ? v : null;
    }

    [Fact]
    public void Missing_root_fails_and_names_the_env_var()
    {
        var ok = OkfMcpConfig.TryResolve([], Env(), out _, out _, out var error);

        Assert.False(ok);
        Assert.Contains("OKF_BUNDLE_ROOT", error);
    }

    [Fact]
    public void Nonexistent_root_fails_with_not_found()
    {
        var missing = Path.Combine(Path.GetTempPath(), "okf-does-not-exist-" + Guid.NewGuid().ToString("N"));

        var ok = OkfMcpConfig.TryResolve([missing], Env(), out _, out _, out var error);

        Assert.False(ok);
        Assert.Contains("not found", error);
    }

    [Fact]
    public void Arg_takes_precedence_and_defaults_to_read_write()
    {
        var dir = Directory.CreateTempSubdirectory("okf-cfg-").FullName;
        try
        {
            var ok = OkfMcpConfig.TryResolve([dir], Env(("OKF_BUNDLE_ROOT", "ignored")), out var root, out var readOnly, out var error);

            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(dir, root);
            Assert.False(readOnly);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void Env_root_used_when_no_arg_and_readonly_flag_parsed()
    {
        var dir = Directory.CreateTempSubdirectory("okf-cfg-").FullName;
        try
        {
            var ok = OkfMcpConfig.TryResolve(
                [],
                Env(("OKF_BUNDLE_ROOT", dir), ("OKF_MCP_READONLY", "1")),
                out var root,
                out var readOnly,
                out _);

            Assert.True(ok);
            Assert.Equal(dir, root);
            Assert.True(readOnly);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
