// SPDX-License-Identifier: LGPL-3.0-or-later
using System.CommandLine;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OKF4net;
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;
using OkfProducer.Core.Validation;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IRepositoryScanner, RepositoryScanner>();
builder.Services.AddSingleton<IConceptGenerator, ConceptGenerator>();
builder.Services.AddSingleton<IBundleWriter, BundleWriter>();
builder.Services.AddSingleton<IBundleValidationRunner, BundleValidationRunner>();
using var host = builder.Build();

var repoOption = new Option<string>("--repo") { Description = "Root of the repository to scan", Required = true };
var outOption = new Option<string>("--out") { Description = "Root of the OKF bundle to write", Required = true };
var updateOption = new Option<bool>("--update") { Description = "Allow writing into a non-empty --out, preserving files this run doesn't generate" };
var resetOption = new Option<bool>("--reset") { Description = "Delete and recreate --out before writing" };
var forceOption = new Option<bool>("--force") { Description = "Alias for --reset" };

var generateCommand = new Command("generate", "Generate an OKF bundle from a repository")
{
    Options = { repoOption, outOption, updateOption, resetOption, forceOption },
};

generateCommand.SetAction(parseResult =>
{
    var repo = parseResult.GetValue(repoOption)!;
    var outPath = parseResult.GetValue(outOption)!;
    var reset = parseResult.GetValue(resetOption) || parseResult.GetValue(forceOption);
    var update = parseResult.GetValue(updateOption);
    var policy = reset ? WritePolicy.Reset : update ? WritePolicy.Update : WritePolicy.RequireEmpty;

    var scanner = host.Services.GetRequiredService<IRepositoryScanner>();
    var generator = host.Services.GetRequiredService<IConceptGenerator>();
    var writer = host.Services.GetRequiredService<IBundleWriter>();

    try
    {
        var snapshot = scanner.Scan(repo);
        var concepts = generator.Generate(snapshot);
        var result = writer.Write(outPath, concepts, policy);

        Console.WriteLine($"Wrote {result.Written} concept(s) to {outPath}.");
        foreach (var (id, error) in result.Failures)
        {
            Console.Error.WriteLine($"error: {id}: {error}");
        }

        return result.Failures.Count > 0 ? 1 : 0;
    }
    catch (InvalidOperationException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
});

var okfOption = new Option<string>("--okf") { Description = "Root of the OKF bundle to validate", Required = true };

var validateCommand = new Command("validate", "Validate an OKF bundle")
{
    Options = { okfOption },
};

validateCommand.SetAction(parseResult =>
{
    var okfPath = parseResult.GetValue(okfOption)!;
    var validator = host.Services.GetRequiredService<IBundleValidationRunner>();

    try
    {
        var outcome = validator.Validate(okfPath);

        foreach (var line in outcome.DiagnosticLines)
        {
            Console.WriteLine(line);
        }

        Console.WriteLine($"{outcome.ErrorCount} error(s), {outcome.WarningCount} warning(s).");
        return outcome.IsConformant ? 0 : 1;
    }
    catch (BundleLoadException ex)
    {
        Console.Error.WriteLine($"error: {ex.Message}");
        return 1;
    }
});

var rootCommand = new RootCommand("okfgen -- generate and validate OKF bundles from a repository")
{
    Subcommands = { generateCommand, validateCommand },
};

return rootCommand.Parse(args).Invoke();
