// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Cli;

// Entry point only. Everything else lives in OkfgenCli.Run, which takes its two writers as
// arguments so the whole command surface is testable in-process.
return OkfgenCli.Run(args, Console.Out, Console.Error);
