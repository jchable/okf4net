// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Yaml;

namespace OKF4net;

/// <summary>Where a <c>computation</c> field's payload lives (§10.2): inline in the frontmatter, or referenced by path.</summary>
public enum ComputationSource
{
    /// <summary>The computation's source code is embedded directly in the frontmatter.</summary>
    Inline,

    /// <summary>The computation's source code is referenced by a bundle-relative path.</summary>
    File,
}

/// <summary>One entry of the <c>parameters</c> list (§10.2). <see cref="Name"/> is "" when the entry omitted the (required) <c>name</c> key.</summary>
public readonly record struct ComputationParameter(string Name, string? Type, bool Required);

/// <summary>The <c>executor</c> mapping (§10.2): the skill/tool that runs the computation, and the receipt fields it must produce.</summary>
public readonly record struct Executor(string? Resource, IReadOnlyList<string> Receipt);

/// <summary>The <c>attester</c> mapping (§10.2): the process that verifies an execution's receipt.</summary>
public readonly record struct Attester(string? Resource);

/// <summary>
/// The full §10.2 Attested Computation contract projected from a concept's
/// frontmatter: <c>runtime</c>, <c>parameters</c>, <c>computation</c>,
/// <c>executor</c>, <c>attester</c>.
/// </summary>
public readonly record struct AttestedComputationContract(
    string? Runtime,
    IReadOnlyList<ComputationParameter> Parameters,
    string? ComputationPath,
    Executor? Executor,
    Attester? Attester);

/// <summary>A sanctioned <c>computation</c> field value (§10.2): either inline code or a path to it.</summary>
public readonly record struct SanctionedComputation(ComputationSource Source, string? InlineCode, string? Path);

/// <summary>Parsing for the §10.2 Attested Computation contract fields. Lenient: malformed shapes degrade to defaults rather than throwing (§3 permissive loading); judgment is deferred to the validator.</summary>
public static class AttestedComputation
{
    /// <summary>
    /// Projects the whole-frontmatter §10.2 contract (<c>runtime</c>,
    /// <c>parameters</c>, <c>computation</c>, <c>executor</c>,
    /// <c>attester</c>) from <paramref name="map"/>. Never throws.
    /// </summary>
    public static AttestedComputationContract Project(YamlMapping map)
    {
        var runtime = map.Get("runtime")?.AsDisplayString();
        var parameters = ParseParameters(map.Get("parameters"));
        var computationPath = map.Get("computation")?.AsDisplayString();
        var executor = ParseExecutor(map.Get("executor"));
        var attester = ParseAttester(map.Get("attester"));
        return new AttestedComputationContract(runtime, parameters, computationPath, executor, attester);
    }

    private static IReadOnlyList<ComputationParameter> ParseParameters(YamlValue? value)
    {
        if (value is not YamlSequence seq)
        {
            return [];
        }

        var list = new List<ComputationParameter>();
        foreach (var item in seq.Items)
        {
            if (item is not YamlMapping m)
            {
                continue;
            }

            var name = m.Get("name")?.AsDisplayString() ?? "";
            var type = m.Get("type")?.AsDisplayString();
            var required = m.Get("required")?.AsBool() ?? false;
            list.Add(new ComputationParameter(name, type, required));
        }

        return list;
    }

    private static Executor? ParseExecutor(YamlValue? value)
    {
        if (value is not YamlMapping m)
        {
            return null;
        }

        var resource = m.Get("resource")?.AsDisplayString();
        var receipt = ParseStringList(m.Get("receipt"));
        return new Executor(resource, receipt);
    }

    private static Attester? ParseAttester(YamlValue? value)
        => value is YamlMapping m ? new Attester(m.Get("resource")?.AsDisplayString()) : null;

    private static IReadOnlyList<string> ParseStringList(YamlValue? value) => YamlValue.AsStringList(value);
}
