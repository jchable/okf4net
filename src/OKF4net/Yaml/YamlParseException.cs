// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Yaml;

/// <summary>
/// An error produced while parsing YAML frontmatter. Port of the Rust
/// <c>YamlError</c> (src/yaml/parser.rs, lines 7-26): a 1-based source line
/// (0 if not known) plus a human-readable message. The exception
/// <see cref="Exception.Message"/> mirrors <c>YamlError</c>'s
/// <c>Display</c> implementation.
/// </summary>
public sealed class YamlParseException : OkfException
{
    /// <summary>1-based source line where the problem was detected (0 if not known).</summary>
    public int Line { get; }

    /// <summary>Creates a parse error at 1-based <paramref name="line"/>.</summary>
    public YamlParseException(int line, string message)
        : base(FormatMessage(line, message))
    {
        Line = line;
    }

    private static string FormatMessage(int line, string message) =>
        line > 0 ? $"YAML error at line {line}: {message}" : $"YAML error: {message}";
}
