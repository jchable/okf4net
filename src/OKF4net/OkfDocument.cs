// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using OKF4net.Internal;
using OKF4net.Yaml;

namespace OKF4net;

/// <summary>
/// The OKF concept document: YAML frontmatter + markdown body. A faithful
/// port of the reference implementation's <c>OKFDocument</c>, including its
/// exact parse, serialize, and validation behaviour, so that documents
/// round-trip compatibly between implementations. Port of the Rust
/// <c>Document</c> (src/document.rs).
///
/// Link/citation extraction (§8) is provided by <see cref="Links"/> and
/// <see cref="Citations"/>, which delegate to <see cref="LinkScanner"/>.
/// </summary>
public sealed class OkfDocument
{
    private const string FrontmatterDelim = "---";

    /// <summary>The YAML frontmatter block (empty if the file had none).</summary>
    public Frontmatter Frontmatter { get; }

    /// <summary>Everything after the frontmatter.</summary>
    public string Body { get; }

    /// <summary>Creates a document from frontmatter and a body.</summary>
    public OkfDocument(Frontmatter frontmatter, string body)
    {
        Frontmatter = frontmatter;
        Body = body;
    }

    /// <summary>
    /// Parses a document from raw file text.
    ///
    /// If the file does not begin with a <c>---</c> frontmatter delimiter,
    /// the entire text is treated as the body and the frontmatter is empty
    /// (matching the reference parser). An opened-but-unclosed frontmatter
    /// block is an error. Port of <c>Document::parse</c> (document.rs:40-72).
    /// </summary>
    /// <exception cref="DocumentParseException">
    /// The frontmatter block is unterminated, is not a YAML mapping, or
    /// contains invalid YAML.
    /// </exception>
    public static OkfDocument Parse(string text)
    {
        var lines = RustLines.Split(text);
        if (lines.Count == 0 || lines[0].Trim() != FrontmatterDelim)
        {
            return new OkfDocument(new Frontmatter(), text);
        }

        var endIdx = -1;
        for (var i = 1; i < lines.Count; i++)
        {
            if (lines[i].Trim() == FrontmatterDelim)
            {
                endIdx = i;
                break;
            }
        }

        if (endIdx < 0)
        {
            throw new DocumentParseException("Unterminated YAML frontmatter block");
        }

        var fmText = string.Join("\n", lines.Skip(1).Take(endIdx - 1));
        YamlValue value;
        try
        {
            value = YamlValue.Parse(fmText);
        }
        catch (YamlParseException e)
        {
            throw new DocumentParseException($"Invalid YAML in frontmatter: {e.Message}");
        }

        var frontmatter = value switch
        {
            YamlNull => new Frontmatter(),
            YamlMapping m => Frontmatter.FromMapping(m),
            _ => throw new DocumentParseException("Frontmatter must be a YAML mapping"),
        };

        var body = string.Join("\n", lines.Skip(endIdx + 1));
        if (body.StartsWith('\n'))
        {
            body = body[1..];
        }

        return new OkfDocument(frontmatter, body);
    }

    /// <summary>Like <see cref="Parse"/>, but returns <c>false</c> instead of throwing.</summary>
    public static bool TryParse(string text, [NotNullWhen(true)] out OkfDocument? doc, [NotNullWhen(false)] out string? error)
    {
        try
        {
            doc = Parse(text);
            error = null;
            return true;
        }
        catch (DocumentParseException e)
        {
            doc = null;
            error = e.Message;
            return false;
        }
    }

    /// <summary>
    /// Serializes the document back to text: frontmatter delimited by
    /// <c>---</c>, a blank line, then the body (terminated by a newline).
    ///
    /// <see cref="Parse"/> followed by <see cref="Serialize"/> preserves
    /// frontmatter key order and the body (modulo trailing-newline
    /// normalization), matching the reference. Port of
    /// <c>Document::serialize</c> (document.rs:79-90).
    /// </summary>
    public string Serialize()
    {
        var fmText = Frontmatter.AsMapping().ToYamlString().TrimEnd();
        var body = Body.EndsWith('\n') ? Body : Body + "\n";
        return $"{FrontmatterDelim}\n{fmText}\n{FrontmatterDelim}\n\n{body}";
    }

    /// <summary>
    /// Producer-side validation matching the reference
    /// <c>OKFDocument.validate()</c>: requires <c>type</c>, <c>title</c>,
    /// <c>description</c>, and <c>timestamp</c> to all be present and
    /// non-empty.
    ///
    /// For spec **conformance** (§9), which requires only a non-empty
    /// <c>type</c>, use <see cref="ValidateConformance"/>. Port of
    /// <c>Document::validate</c> (document.rs:98-114).
    /// </summary>
    /// <exception cref="DocumentValidationException">One or more required keys are missing or empty.</exception>
    public void Validate()
    {
        var missing = new List<string>();
        foreach (var key in Frontmatter.RequiredKeys)
        {
            var value = Frontmatter.Get(key);
            if (value is null || value.IsEmptyValue)
            {
                missing.Add(key);
            }
        }

        if (missing.Count > 0)
        {
            throw new DocumentValidationException(
                $"Missing required frontmatter keys: {string.Join(", ", missing)}", missing);
        }
    }

    /// <summary>
    /// Spec-conformance validation (§9): the frontmatter must contain a
    /// non-empty <c>type</c> field. Optional fields are not required. Port
    /// of <c>Document::validate_conformance</c> (document.rs:118-129).
    /// </summary>
    /// <exception cref="DocumentValidationException"><c>type</c> is missing or empty.</exception>
    public void ValidateConformance()
    {
        var value = Frontmatter.Get("type");
        var hasType = value is not null && !value.IsEmptyValue;
        if (!hasType)
        {
            throw new DocumentValidationException("Missing required frontmatter keys: type", ["type"]);
        }
    }

    /// <summary>
    /// Extracts all inline markdown links from <see cref="Body"/>, skipping
    /// fenced code blocks and inline code spans. Delegates to
    /// <see cref="LinkScanner.ExtractLinks"/>.
    /// </summary>
    public IReadOnlyList<ConceptLink> Links() => LinkScanner.ExtractLinks(Body);

    /// <summary>
    /// Extracts numbered citation entries from the <c># Citations</c>
    /// section of <see cref="Body"/> (§8). Delegates to
    /// <see cref="LinkScanner.ExtractCitations"/>.
    /// </summary>
    public IReadOnlyList<Citation> Citations() => LinkScanner.ExtractCitations(Body);

}
