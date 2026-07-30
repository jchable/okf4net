// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// Extracts the §10.3 inline sanctioned computation from a concept's body:
/// the first fenced code block (opened by <c>```</c> or <c>~~~</c>, at
/// least 3 characters) that immediately follows an ATX H1 heading whose
/// trimmed text is exactly <c># Computation</c>.
/// </summary>
internal static class ComputationExtractor
{
    private const string Heading = "# Computation";

    /// <summary>
    /// Returns the text of the first fenced block found under a
    /// <c># Computation</c> heading, fences excluded, or <c>null</c> if no
    /// such heading exists or no fence is found before other non-blank
    /// content. Indented code blocks (no fence markers) are never
    /// extracted. An opening fence that is never closed returns the
    /// accumulated body text through end-of-input rather than <c>null</c>,
    /// matching CommonMark's own treatment of an unterminated fenced code
    /// block.
    /// </summary>
    internal static string? ExtractInline(string body)
    {
        var lines = LfLines.Split(body);

        var headingIdx = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Trim() == Heading)
            {
                headingIdx = i;
                break;
            }
        }

        if (headingIdx < 0)
        {
            return null;
        }

        var i2 = headingIdx + 1;
        while (i2 < lines.Count && lines[i2].Trim().Length == 0)
        {
            i2++;
        }

        if (i2 >= lines.Count)
        {
            return null;
        }

        var trimmed = lines[i2].TrimStart();
        char fenceChar;
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            fenceChar = '`';
        }
        else if (trimmed.StartsWith("~~~", StringComparison.Ordinal))
        {
            fenceChar = '~';
        }
        else
        {
            return null;
        }

        var openLen = 0;
        while (openLen < trimmed.Length && trimmed[openLen] == fenceChar)
        {
            openLen++;
        }

        var bodyLines = new List<string>();
        for (var j = i2 + 1; j < lines.Count; j++)
        {
            var candidate = lines[j].Trim();
            var closeLen = 0;
            while (closeLen < candidate.Length && candidate[closeLen] == fenceChar)
            {
                closeLen++;
            }

            if (closeLen >= openLen && closeLen == candidate.Length && closeLen >= 3)
            {
                return string.Join("\n", bodyLines);
            }

            bodyLines.Add(lines[j]);
        }

        return string.Join("\n", bodyLines);
    }
}
