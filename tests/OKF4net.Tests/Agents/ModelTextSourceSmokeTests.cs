// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests.Agents;

/// <summary>
/// The grep guard of the model-facing output boundary (#122, design section
/// 3, step 5).
///
/// <para><b>A smoke check, not proof.</b> It greps two source files for the
/// type that used to build tool text by hand: it cannot see a raw
/// <c>$"..."</c> return, a <c>string.Concat</c>, a helper in another file, or
/// what any of it produces. A green run here says only that nobody reached
/// for a <c>StringBuilder</c> in these two files. The proof is
/// <see cref="ModelFacingBoundaryTests"/>, which runs every exposed function
/// and the injected context over a hostile bundle and checks what comes
/// out.</para>
/// </summary>
public class ModelTextSourceSmokeTests
{
    [Theory]
    [InlineData("OkfBundleTools.cs")]
    [InlineData("OkfContextProvider.cs")]
    public void The_model_facing_renderers_build_no_text_with_a_StringBuilder(string file)
    {
        var path = Path.Combine(TestPaths.RepoRoot(), "src", "OKF4net.Agents", file);
        var offending = File.ReadAllLines(path)
            .Select((line, index) => (Line: line, Number: index + 1))
            .Where(l => !l.Line.TrimStart().StartsWith("//", StringComparison.Ordinal))
            .Where(l => l.Line.Contains("StringBuilder", StringComparison.Ordinal))
            .Select(l => $"{file}:{l.Number}: {l.Line.Trim()}")
            .ToList();

        Assert.True(
            offending.Count == 0,
            "Model-facing text is built through ModelTextBuilder only (#122); a StringBuilder here bypasses the fold:\n"
            + string.Join('\n', offending));
    }
}
