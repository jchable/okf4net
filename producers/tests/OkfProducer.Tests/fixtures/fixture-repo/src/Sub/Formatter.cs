// SPDX-License-Identifier: LGPL-3.0-or-later
namespace N.Sub;

/// <summary>Formats one report line.</summary>
public class Formatter
{
    /// <summary>Formats a count.</summary>
    public string Format(int value)
    {
        return value.ToString();
    }
}
