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
    /// block. The heading search itself is fence-aware: per CommonMark, a
    /// fenced code block is a container, so a heading-like line inside an
    /// earlier, unrelated fenced block is inert content, not a real
    /// heading, and is skipped rather than matched.
    /// </summary>
    internal static string? ExtractInline(string body)
    {
        var lines = LfLines.Split(body);

        var headingIdx = -1;
        var insideForeignFence = false;
        var foreignFenceChar = '`';
        var foreignFenceOpenLen = 0;
        for (var i = 0; i < lines.Count; i++)
        {
            if (insideForeignFence)
            {
                if (IsFenceCloseLine(lines[i], foreignFenceChar, foreignFenceOpenLen))
                {
                    insideForeignFence = false;
                }

                continue;
            }

            if (TryMatchFenceOpen(lines[i], out var openChar, out var runLen))
            {
                insideForeignFence = true;
                foreignFenceChar = openChar;
                foreignFenceOpenLen = runLen;
            }
            else
            {
                if (lines[i].Trim() == Heading)
                {
                    headingIdx = i;
                    break;
                }

                continue;
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

        if (!TryMatchFenceOpen(lines[i2], out var fenceChar, out var openLen))
        {
            return null;
        }

        var bodyLines = new List<string>();
        for (var j = i2 + 1; j < lines.Count; j++)
        {
            if (IsFenceCloseLine(lines[j], fenceChar, openLen))
            {
                return string.Join("\n", bodyLines);
            }

            bodyLines.Add(lines[j]);
        }

        return string.Join("\n", bodyLines);
    }

    /// <summary>
    /// If <paramref name="line"/> (after trimming leading whitespace) opens a
    /// fenced code block (a run of &gt;=3 <c>`</c> or <c>~</c> characters),
    /// returns <c>true</c> with the fence character and the opening run's
    /// length; otherwise returns <c>false</c>.
    /// </summary>
    private static bool TryMatchFenceOpen(string line, out char fenceChar, out int openLen)
    {
        var trimmedStart = line.TrimStart();
        if (trimmedStart.StartsWith("```", StringComparison.Ordinal))
        {
            fenceChar = '`';
        }
        else if (trimmedStart.StartsWith("~~~", StringComparison.Ordinal))
        {
            fenceChar = '~';
        }
        else
        {
            fenceChar = default;
            openLen = 0;
            return false;
        }

        openLen = 0;
        while (openLen < trimmedStart.Length && trimmedStart[openLen] == fenceChar)
        {
            openLen++;
        }

        return true;
    }

    /// <summary>
    /// <c>true</c> if <paramref name="line"/>, once trimmed, is ENTIRELY a run
    /// of <paramref name="fenceChar"/> of length &gt;= <paramref name="minLen"/>
    /// (and &gt;=3) -- a valid CommonMark closing fence for an opening of that
    /// character and length.
    /// </summary>
    private static bool IsFenceCloseLine(string line, char fenceChar, int minLen)
    {
        var candidate = line.Trim();
        var closeLen = 0;
        while (closeLen < candidate.Length && candidate[closeLen] == fenceChar)
        {
            closeLen++;
        }

        return closeLen == candidate.Length && closeLen >= minLen && closeLen >= 3;
    }
}
