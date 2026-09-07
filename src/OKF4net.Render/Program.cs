// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;

namespace OKF4net.Render;

/// <summary>Process entry point: wires <see cref="OkfRenderCli.Run"/> to the real console.</summary>
public static class Program
{
    public static int Main(string[] args)
    {
        // Mirrors OKF4net.Cli.Program: force a BOM-less UTF-8 console so
        // output is portable regardless of the host OS's default console
        // code page.
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

        return OkfRenderCli.Run(args, Console.Out, Console.Error);
    }
}
