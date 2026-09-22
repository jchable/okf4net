// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using OKF4net.Internal;
using OKF4net.Yaml;

namespace OKF4net;

/// <summary>
/// The OKF concept document: YAML frontmatter + markdown body. Its exact
/// parse, serialize, and validation behaviour keeps documents round-tripping
/// compatibly between OKF implementations.
///
/// Link/citation extraction (§6.1/§13.1) is provided by <see cref="Links"/> and
/// <see cref="Citations"/>, which delegate to <see cref="LinkScanner"/>.
/// </summary>
public sealed class OkfDocument : IEquatable<OkfDocument>
{
    private const string FrontmatterDelim = "---";

    /// <summary>
    /// Whether <paramref name="line"/> is a frontmatter fence: it starts at
    /// column 0 with <c>---</c>, and every character after those three is a
    /// space or a tab (§4: "delimited by <c>---</c> on its own line"). Trailing
    /// spaces and tabs are tolerated like Jekyll's <c>^---\s*$</c>, though more
    /// narrowly: Ruby's <c>\s</c> also matches <c>\r</c>, <c>\f</c> and <c>\v</c>.
    /// <paramref name="line"/> is a line as <see cref="LfLines"/> splits it, so
    /// the <c>\r</c> of a CRLF terminator is already gone; a lone <c>\r</c>, or
    /// any other whitespace such as NO-BREAK SPACE, makes the line not a fence.
    ///
    /// An INDENTED <c>---</c> is not a fence. It is ordinary frontmatter text:
    /// the content of a block scalar (<c>description: |</c> whose body holds a
    /// <c>---</c> line), a plain scalar's continuation, or invalid YAML. The
    /// predicate used to compare the <c>Trim()</c>med line, which accepted such a
    /// line as the closing fence and silently cut the frontmatter there. A
    /// leading U+FEFF (byte-order mark) was not trimmed then and is not accepted
    /// now: <see cref="Parse"/> reads a BOM-prefixed file as having no frontmatter.
    ///
    /// The single shared predicate <see cref="Parse"/> and
    /// <see cref="OKF4net.Internal.FrontmatterBlockEdit"/> both call, so the two
    /// cannot disagree about where the frontmatter/body boundary sits (finding
    /// #C7-2: they used to, which silently moved body content into the edited
    /// frontmatter).
    /// </summary>
    internal static bool IsFenceLine(string line)
    {
        if (!line.StartsWith(FrontmatterDelim, StringComparison.Ordinal))
        {
            return false;
        }

        for (var i = FrontmatterDelim.Length; i < line.Length; i++)
        {
            if (line[i] is not (' ' or '\t'))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>The YAML frontmatter block (empty if the file had none).</summary>
    public Frontmatter Frontmatter { get; }

    /// <summary>Everything after the frontmatter.</summary>
    public string Body { get; }

    /// <summary>
    /// Whether <see cref="Parse"/> found a fenced frontmatter block, even an empty
    /// one. <see langword="false"/> for a document parsed without one (then
    /// <see cref="Body"/> is the whole text) and for any document built by the
    /// constructor. Not part of <see cref="Equals(OkfDocument?)"/>.
    /// </summary>
    internal bool HasFrontmatterBlock { get; private init; }

    /// <summary>
    /// For a document <see cref="Parse"/> read as having no frontmatter block, why
    /// its first line looks like a fence but is not one: it would be a fence (see
    /// <see cref="IsFenceLine"/>) once a leading U+FEFF and leading spaces or tabs
    /// are removed. Returns <c>"is not at column 0"</c>,
    /// <c>"starts with a byte-order mark"</c>, both joined with <c>and</c>, or
    /// <see langword="null"/> when the first line is not such a line or the document
    /// has a frontmatter block. Backs the §4 hint
    /// <see cref="DiagnosticCode.FrontmatterFenceNotAtColumn0"/>.
    /// </summary>
    internal string? DisplacedFirstLineFenceCause()
    {
        if (HasFrontmatterBlock)
        {
            return null;
        }

        var newline = Body.IndexOf('\n');
        var line = newline < 0 ? Body : Body[..(newline > 0 && Body[newline - 1] == '\r' ? newline - 1 : newline)];
        var bom = line.StartsWith('\uFEFF');
        if (bom)
        {
            line = line[1..];
        }

        var indented = line.Length > 0 && line[0] is ' ' or '\t';
        if (!(bom || indented) || !IsFenceLine(line.TrimStart(' ', '\t')))
        {
            return null;
        }

        return (bom, indented) switch
        {
            (true, true) => "starts with a byte-order mark and is not at column 0",
            (true, false) => "starts with a byte-order mark",
            _ => "is not at column 0",
        };
    }

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
    /// the entire text is treated as the body and the frontmatter is empty.
    /// An opened-but-unclosed frontmatter block is an error. Both delimiters
    /// must be <c>---</c> at column 0, followed by nothing but spaces or tabs
    /// (§4); an indented <c>---</c> neither opens nor closes the frontmatter.
    /// </summary>
    /// <exception cref="DocumentParseException">
    /// The frontmatter block is unterminated, is not a YAML mapping, or
    /// contains invalid YAML, including a YAML feature outside the supported
    /// subset (anchors, aliases, tags, directives, document markers, an indented
    /// <c>---</c> line outside block-scalar content, text after a closing quote).
    /// </exception>
    public static OkfDocument Parse(string text)
    {
        var lines = LfLines.Split(text);
        if (lines.Count == 0 || !IsFenceLine(lines[0]))
        {
            return new OkfDocument(new Frontmatter(), text);
        }

        var endIdx = -1;
        for (var i = 1; i < lines.Count; i++)
        {
            if (IsFenceLine(lines[i]))
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
            // YAML error lines count from the first frontmatter line: a golden-locked
            // format (tests/fixtures/golden/validate-reserved.out), kept byte for byte.
            // The indented-fence error alone also names the file line. Its purpose is to
            // point at a mistyped line, and "line N" lands one line above it in the
            // file. The frontmatter text starts at file line 2, so the file line is N + 1.
            var fileLine = e.IsIndentedFence ? $" (file line {e.Line + 1})" : string.Empty;
            throw new DocumentParseException($"Invalid YAML in frontmatter: {e.Message}{fileLine}");
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

        return new OkfDocument(frontmatter, body) { HasFrontmatterBlock = true };
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
    /// normalization).
    /// </summary>
    public string Serialize()
    {
        var fmText = Frontmatter.AsMapping().ToYamlString().TrimEnd();
        var body = Body.EndsWith('\n') ? Body : Body + "\n";
        return $"{FrontmatterDelim}\n{fmText}\n{FrontmatterDelim}\n\n{body}";
    }

    /// <summary>
    /// Producer-side validation: requires <c>type</c>, <c>title</c>, and
    /// <c>description</c> to all be present and non-empty.
    ///
    /// For spec **conformance** (§11), which requires only a non-empty
    /// <c>type</c>, use <see cref="ValidateConformance"/>.
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
    /// Spec-conformance validation (§11): the frontmatter must contain a
    /// non-empty <c>type</c> field. Optional fields are not required.
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
    /// section of <see cref="Body"/> (§13.1, legacy). Delegates to
    /// <see cref="LinkScanner.ExtractCitations"/>.
    /// </summary>
    public IReadOnlyList<Citation> Citations() => LinkScanner.ExtractCitations(Body);

    /// <summary>
    /// The concept's provenance sources with v0.2 consumer semantics: the
    /// frontmatter <c>sources</c> field (§5.1) when present, otherwise the
    /// legacy <c># Citations</c> body list mapped to <see cref="Source"/>s
    /// (§13.1 sanctions this fallback for v0.1 documents). Each citation maps
    /// to a source with <see cref="Source.Resource"/> = its link target (or
    /// raw text) and <see cref="Source.Title"/> = its link text.
    /// </summary>
    /// <remarks>
    /// Not to be confused with <see cref="Frontmatter.Sources"/>, which reads
    /// only the literal frontmatter <c>sources</c> field and never falls back
    /// to <c># Citations</c>; use this method for consumer-facing reads and
    /// <see cref="Frontmatter.Sources"/> only when the frontmatter-only value
    /// is specifically what is needed.
    /// </remarks>
    public IReadOnlyList<Source> Sources()
    {
        var fromFrontmatter = Frontmatter.Sources;
        if (fromFrontmatter.Count > 0)
        {
            return fromFrontmatter;
        }

        var citations = Citations();
        if (citations.Count == 0)
        {
            return [];
        }

        return citations
            .Select(c => new Source(Id: null, Resource: c.Target ?? c.Raw, Title: c.Text, Author: null, UsageCount: null, LastModified: null))
            .ToList();
    }

    /// <summary>
    /// <c>true</c> when <see cref="Sources"/> would fall back to the legacy
    /// <c># Citations</c> body list: the frontmatter <c>sources</c> field
    /// (§5.1) is absent or empty, but the body has a <c># Citations</c>
    /// section with at least one entry. Lets consumers (and the validator)
    /// flag documents still using the pre-v0.2 citation form instead of the
    /// <c>sources</c> field.
    /// </summary>
    public bool UsesLegacyCitations() => Frontmatter.Sources.Count == 0 && Citations().Count > 0;

    /// <summary>
    /// Resolves the §10.3 sanctioned computation: the frontmatter
    /// <c>computation</c> path (§10.2) when present, otherwise the first
    /// fenced code block under a <c># Computation</c> heading in
    /// <see cref="Body"/> (or <c>null</c> if neither is present).
    /// </summary>
    public SanctionedComputation Computation()
    {
        var path = Frontmatter.ComputationContract.ComputationPath;
        if (!string.IsNullOrEmpty(path))
        {
            return new SanctionedComputation(ComputationSource.File, null, path);
        }

        return new SanctionedComputation(ComputationSource.Inline, ComputationExtractor.ExtractInline(Body), null);
    }

    /// <summary>
    /// Enumerates the §6.2 path-valued frontmatter fields present on this
    /// document, in a fixed order: the top-level <c>resource</c>, each
    /// <c>sources[i].resource</c> (labelled <c>sources[i].resource</c>), the
    /// §10.2 <c>computation</c>, <c>executor.resource</c>, and
    /// <c>attester.resource</c>. Only non-empty string values are included;
    /// a field absent from the frontmatter is simply omitted.
    /// </summary>
    public IReadOnlyList<FrontmatterResource> FrontmatterResources()
    {
        var result = new List<FrontmatterResource>();

        void AddIfPresent(string field, string? rawPath)
        {
            if (!string.IsNullOrEmpty(rawPath))
            {
                result.Add(new FrontmatterResource(field, rawPath, FrontmatterResourceClassifier.KindOf(rawPath)));
            }
        }

        AddIfPresent("resource", Frontmatter.Resource);

        var sources = Frontmatter.Sources;
        for (var i = 0; i < sources.Count; i++)
        {
            AddIfPresent($"sources[{i}].resource", sources[i].Resource);
        }

        var contract = Frontmatter.ComputationContract;
        AddIfPresent("computation", contract.ComputationPath);
        AddIfPresent("executor.resource", contract.Executor?.Resource);
        AddIfPresent("attester.resource", contract.Attester?.Resource);

        return result;
    }

    /// <summary>
    /// Structural equality: <see cref="Frontmatter"/> equality AND ordinal
    /// <see cref="Body"/> equality — componentwise over the document's two
    /// fields.
    /// </summary>
    public bool Equals(OkfDocument? other) =>
        other is not null
        && (ReferenceEquals(this, other)
            || (Frontmatter.Equals(other.Frontmatter) && string.Equals(Body, other.Body, StringComparison.Ordinal)));

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as OkfDocument);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Frontmatter.GetHashCode(), Body);
}
