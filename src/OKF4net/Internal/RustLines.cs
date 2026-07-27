// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Internal;

/// <summary>
/// A single, correct line-splitter shared by every place in this codebase
/// that needs <c>'\n'</c>-based line splitting (previously four
/// near-identical, and in one case divergent, private copies).
///
/// Semantics: splits only on <c>'\n'</c>. If the character immediately
/// preceding a <c>'\n'</c> is <c>'\r'</c>, that single <c>'\r'</c> is
/// stripped (i.e. <c>"\r\n"</c> is understood as one line terminator). A
/// lone <c>'\r'</c> NOT immediately followed by <c>'\n'</c> is not a line
/// terminator at all and stays embedded in the line content. A trailing
/// <c>'\n'</c> does not produce a trailing empty line, and the empty
/// string produces no lines.
/// </summary>
internal static class RustLines
{
    internal static List<string> Split(string text)
    {
        var result = new List<string>();
        if (text.Length == 0)
        {
            return result;
        }

        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            var end = i;
            if (end > start && text[end - 1] == '\r')
            {
                end--;
            }

            result.Add(text.Substring(start, end - start));
            start = i + 1;
        }

        if (start < text.Length)
        {
            result.Add(text.Substring(start));
        }

        return result;
    }
}
