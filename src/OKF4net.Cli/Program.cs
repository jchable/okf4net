// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OKF4net.Cli;

/// <summary>Process entry point: wires <see cref="OkfCli.Run"/> to the real console.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // The `validate` output includes U+2713/U+2717 (✓/✗); force a
        // BOM-less UTF-8 console so they render correctly regardless of the
        // host OS's default console code page (e.g. Windows' legacy code
        // pages otherwise mangle non-ASCII output).
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        return OkfCli.Run(args, Console.Out, Console.Error);
    }
}
