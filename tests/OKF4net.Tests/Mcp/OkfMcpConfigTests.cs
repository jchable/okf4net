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
    public void Missing_root_with_no_discoverable_bundle_fails_and_names_every_fix()
    {
        var ok = OkfMcpConfig.TryResolve([], Env(), Path.GetTempPath(), _ => null, out _, out _, out var error);

        Assert.False(ok);
        Assert.NotNull(error);
        Assert.Contains("OKF_BUNDLE_ROOT", error);
        Assert.Contains("okf-init", error);
        Assert.Contains("okf_version", error);
        Assert.DoesNotContain('\n', error);
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

    // ---- One-line startup error contract -------------------------------------
    // Spec (docs/design/specs/2026-07-24-okf-mcp-server-design.md): on a missing
    // or invalid bundle root, "print a one-line usage/error to stderr". Program.cs
    // writes exactly OkfMcpConfig.FormatStartupError(error) with a single
    // Console.Error.WriteLine, so guarding this string to a single line (no CR/LF)
    // guards the on-the-wire contract without spawning a process.

    [Fact]
    public void Formatted_missing_root_error_is_a_single_line_with_message_and_usage()
    {
        OkfMcpConfig.TryResolve([], Env(), Path.GetTempPath(), _ => null, out _, out _, out var error);

        var line = OkfMcpConfig.FormatStartupError(error);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.StartsWith("okf-mcp: ", line);
        Assert.Contains("OKF_BUNDLE_ROOT", line);
        Assert.Contains("okf_version", line);
        Assert.Contains("Usage:", line);
    }

    [Fact]
    public void Formatted_not_found_error_is_a_single_line_with_message_and_usage()
    {
        var missing = Path.Combine(Path.GetTempPath(), "okf-does-not-exist-" + Guid.NewGuid().ToString("N"));
        OkfMcpConfig.TryResolve([missing], Env(), out _, out _, out var error);

        var line = OkfMcpConfig.FormatStartupError(error);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.Contains("not found", line);
        Assert.Contains("Usage:", line);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("bundle root not found: /x")]
    [InlineData("no bundle root given. Pass it as the first argument or set OKF_BUNDLE_ROOT.")]
    public void Formatted_startup_error_is_always_one_line(string? error)
    {
        var line = OkfMcpConfig.FormatStartupError(error);

        Assert.DoesNotContain('\n', line);
        Assert.DoesNotContain('\r', line);
        Assert.StartsWith("okf-mcp: ", line);
        Assert.Contains("Usage:", line);
    }

    // ---- Discovery fallback ---------------------------------------------------

    private const string MarkedIndex = "---\nokf_version: \"0.2\"\n---\n\n# Index\n";

    [Fact]
    public void Discovery_supplies_the_root_when_no_arg_and_no_env()
    {
        var top = Directory.CreateTempSubdirectory("okf-cfg-disc-").FullName;
        try
        {
            var knowledge = Directory.CreateDirectory(Path.Combine(top, "knowledge")).FullName;
            File.WriteAllText(Path.Combine(knowledge, "index.md"), MarkedIndex);

            var ok = OkfMcpConfig.TryResolve([], Env(), top, out var root, out var readOnly, out var error);

            Assert.True(ok);
            Assert.Null(error);
            Assert.Equal(knowledge, root);
            Assert.False(readOnly);
        }
        finally
        {
            Directory.Delete(top, recursive: true);
        }
    }

    [Fact]
    public void Env_root_beats_discovery()
    {
        var top = Directory.CreateTempSubdirectory("okf-cfg-disc-").FullName;
        var explicitRoot = Directory.CreateTempSubdirectory("okf-cfg-env-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(top, "index.md"), MarkedIndex);

            var ok = OkfMcpConfig.TryResolve([], Env(("OKF_BUNDLE_ROOT", explicitRoot)), top, out var root, out _, out _);

            Assert.True(ok);
            Assert.Equal(explicitRoot, root);
        }
        finally
        {
            Directory.Delete(top, recursive: true);
            Directory.Delete(explicitRoot, recursive: true);
        }
    }

    [Fact]
    public void Arg_beats_discovery()
    {
        var top = Directory.CreateTempSubdirectory("okf-cfg-disc-").FullName;
        var explicitRoot = Directory.CreateTempSubdirectory("okf-cfg-arg-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(top, "index.md"), MarkedIndex);

            var ok = OkfMcpConfig.TryResolve([explicitRoot], Env(), top, out var root, out _, out _);

            Assert.True(ok);
            Assert.Equal(explicitRoot, root);
        }
        finally
        {
            Directory.Delete(top, recursive: true);
            Directory.Delete(explicitRoot, recursive: true);
        }
    }

    [Fact]
    public void Nonexistent_env_root_fails_without_discovery_fallback()
    {
        var top = Directory.CreateTempSubdirectory("okf-cfg-disc-").FullName;
        try
        {
            File.WriteAllText(Path.Combine(top, "index.md"), MarkedIndex);
            var missing = Path.Combine(Path.GetTempPath(), "okf-does-not-exist-" + Guid.NewGuid().ToString("N"));

            var ok = OkfMcpConfig.TryResolve([], Env(("OKF_BUNDLE_ROOT", missing)), top, out _, out _, out var error);

            Assert.False(ok);
            Assert.Contains("not found", error);
        }
        finally
        {
            Directory.Delete(top, recursive: true);
        }
    }
}
