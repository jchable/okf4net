// SPDX-License-Identifier: LGPL-3.0-or-later
using OkfProducer.Core.Generation;
using OkfProducer.Core.Scanning;
using OkfProducer.Core.Validation;

namespace OkfProducer.Tests;

public class EndToEndTests
{
    [Fact]
    public void Scan_generate_write_validate_round_trip_on_a_small_fixture_repo()
    {
        var repoPath = Path.Combine(Path.GetTempPath(), "okfproducer-e2e-repo-" + Guid.NewGuid());
        var outPath = Path.Combine(Path.GetTempPath(), "okfproducer-e2e-out-" + Guid.NewGuid());
        Directory.CreateDirectory(repoPath);
        try
        {
            File.WriteAllText(Path.Combine(repoPath, "package.json"),
                """{ "name": "fixture-lib", "description": "A fixture package for the end-to-end test." }""");
            File.WriteAllText(Path.Combine(repoPath, "README.md"), "# Fixture Repo\n\nHello.\n");

            var snapshot = new RepositoryScanner().Scan(repoPath);
            var concepts = new ConceptGenerator().Generate(snapshot);
            var writeResult = new BundleWriter().Write(outPath, concepts, WritePolicy.RequireEmpty, repoPath);

            Assert.Equal(3, writeResult.Written); // overview + 1 package + 1 doc
            Assert.Empty(writeResult.Failures);

            var validationOutcome = new BundleValidationRunner().Validate(outPath);

            Assert.True(validationOutcome.IsConformant, string.Join("\n", validationOutcome.DiagnosticLines));
            Assert.True(File.Exists(Path.Combine(outPath, "index.md")));
        }
        finally
        {
            Directory.Delete(repoPath, recursive: true);
            if (Directory.Exists(outPath)) Directory.Delete(outPath, recursive: true);
        }
    }
}
