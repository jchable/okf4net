// SPDX-License-Identifier: LGPL-3.0-or-later
using System.ComponentModel;
using System.Text.Json;
using Microsoft.Extensions.AI;
using OKF4net.Agents;
using OKF4net.Attestation;
using OKF4net.Tests.Attestation;

namespace OKF4net.Tests.Agents;

/// <summary>
/// Tests <see cref="OkfBundleTools.GetTools"/>: the ten tool methods exposed
/// as Agent Framework <see cref="AIFunction"/>s (via <see cref="AITool"/>)
/// when no attestation orchestrator is wired (so <c>okf_run_computation</c>
/// is omitted; see <see cref="OkfComputationToolsTests"/> for the wired
/// case), with no LLM involved — everything is verified at the
/// <see cref="AIFunction"/> level, including a real end-to-end invocation
/// that proves argument binding from a plain dictionary works.
/// </summary>
public class AIFunctionExposureTests
{
    private static readonly string BundlePath = Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "appendix_a");

    private static readonly string[] ExpectedNamesInOrder =
    [
        "okf_read_concept",
        "okf_browse",
        "okf_graph",
        "okf_search",
        "okf_write_concept",
        "okf_append_log",
        "okf_regenerate_indexes",
        "okf_validate_bundle",
        "okf_changes_since",
        "okf_get_computation",
    ];

    [Fact]
    public void GetTools_returns_exactly_ten_tools()
    {
        var tools = new OkfBundleTools(BundlePath);
        Assert.Equal(10, tools.GetTools().Count);
    }

    [Fact]
    public void GetTools_names_are_the_ten_snake_case_names_in_stable_order()
    {
        var tools = new OkfBundleTools(BundlePath);
        var names = tools.GetTools().Cast<AIFunction>().Select(f => f.Name).ToList();
        Assert.Equal(ExpectedNamesInOrder, names);
    }

    [Fact]
    public void GetTools_every_function_has_a_non_empty_description()
    {
        var tools = new OkfBundleTools(BundlePath);
        foreach (var tool in tools.GetTools())
        {
            var function = Assert.IsAssignableFrom<AIFunction>(tool);
            Assert.False(string.IsNullOrWhiteSpace(function.Description), $"{function.Name} should have a non-empty Description.");
        }
    }

    [Fact]
    public void GetTools_descriptions_come_from_the_underlying_method_DescriptionAttribute()
    {
        var tools = new OkfBundleTools(BundlePath);
        foreach (var tool in tools.GetTools())
        {
            var function = Assert.IsAssignableFrom<AIFunction>(tool);
            var method = function.UnderlyingMethod
                ?? throw new InvalidOperationException($"{function.Name} has no UnderlyingMethod to compare against.");
            var attribute = method.GetCustomAttributes(typeof(DescriptionAttribute), inherit: false)
                .Cast<DescriptionAttribute>()
                .SingleOrDefault()
                ?? throw new InvalidOperationException($"{method.Name} has no [Description] attribute.");

            Assert.Equal(attribute.Description, function.Description);
        }
    }

    [Fact]
    public void okf_read_concept_schema_requires_conceptId()
    {
        var function = GetFunction(new OkfBundleTools(BundlePath), "okf_read_concept");
        var required = RequiredProperties(function);
        Assert.Contains("conceptId", required);
    }

    [Fact]
    public void okf_search_schema_has_optional_tag()
    {
        var function = GetFunction(new OkfBundleTools(BundlePath), "okf_search");
        var schema = function.JsonSchema;
        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("tag", out _), "schema should declare a 'tag' property.");

        var required = RequiredProperties(function);
        Assert.Contains("query", required);
        Assert.DoesNotContain("tag", required);
    }

    [Fact]
    public void okf_write_concept_schema_requires_all_three_params()
    {
        var function = GetFunction(new OkfBundleTools(BundlePath), "okf_write_concept");
        var required = RequiredProperties(function);
        Assert.Contains("conceptId", required);
        Assert.Contains("frontmatterYaml", required);
        Assert.Contains("body", required);
        Assert.Equal(3, required.Count);
    }

    [Fact]
    public void okf_browse_schema_has_optional_path()
    {
        var function = GetFunction(new OkfBundleTools(BundlePath), "okf_browse");
        var schema = function.JsonSchema;
        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("path", out _), "schema should declare a 'path' property.");

        var required = RequiredProperties(function);
        Assert.DoesNotContain("path", required);
    }

    [Fact]
    public void okf_graph_schema_has_optional_conceptId()
    {
        var function = GetFunction(new OkfBundleTools(BundlePath), "okf_graph");
        var schema = function.JsonSchema;
        var properties = schema.GetProperty("properties");
        Assert.True(properties.TryGetProperty("conceptId", out _), "schema should declare a 'conceptId' property.");

        var required = RequiredProperties(function);
        Assert.DoesNotContain("conceptId", required);
    }

    [Fact]
    public void okf_append_log_schema_requires_kind_and_text()
    {
        var function = GetFunction(new OkfBundleTools(BundlePath), "okf_append_log");
        var required = RequiredProperties(function);
        Assert.Contains("kind", required);
        Assert.Contains("text", required);
        Assert.Equal(2, required.Count);
    }

    [Fact]
    public void okf_regenerate_indexes_schema_has_no_params()
    {
        var function = GetFunction(new OkfBundleTools(BundlePath), "okf_regenerate_indexes");
        var schema = function.JsonSchema;
        var properties = schema.GetProperty("properties");
        Assert.Empty(properties.EnumerateObject());

        var required = RequiredProperties(function);
        Assert.Empty(required);
    }

    [Fact]
    public void okf_validate_bundle_schema_has_no_params()
    {
        var function = GetFunction(new OkfBundleTools(BundlePath), "okf_validate_bundle");
        var schema = function.JsonSchema;
        var properties = schema.GetProperty("properties");
        Assert.Empty(properties.EnumerateObject());

        var required = RequiredProperties(function);
        Assert.Empty(required);
    }

    [Fact]
    public void okf_changes_since_schema_requires_sinceDate()
    {
        var function = GetFunction(new OkfBundleTools(BundlePath), "okf_changes_since");
        var required = RequiredProperties(function);
        Assert.Contains("sinceDate", required);
        Assert.Single(required);
    }

    [Fact]
    public async Task okf_read_concept_invocation_matches_direct_call()
    {
        var tools = new OkfBundleTools(BundlePath);
        var function = GetFunction(tools, "okf_read_concept");

        var direct = tools.ReadConcept("tables/orders");

        var arguments = new AIFunctionArguments(new Dictionary<string, object?> { ["conceptId"] = "tables/orders" }!);
        var result = await function.InvokeAsync(arguments);

        Assert.Equal(direct, result?.ToString());
    }

    [Fact]
    public async Task okf_validate_bundle_invocation_with_no_args_succeeds()
    {
        var tools = new OkfBundleTools(BundlePath);
        var function = GetFunction(tools, "okf_validate_bundle");

        var direct = tools.ValidateBundle();

        var result = await function.InvokeAsync(new AIFunctionArguments());

        Assert.Equal(direct, result?.ToString());
    }

    /// <summary>
    /// Binding probe for <c>okf_run_computation</c>'s
    /// <c>IReadOnlyDictionary&lt;string, object?&gt;</c> parameter: no other
    /// tool takes a non-scalar parameter, so this is the first proof that
    /// <see cref="AIFunctionFactory.Create(System.Delegate, string)"/> can
    /// bind it from a JSON object at all -- and, more importantly, that the
    /// *keys* survive the trip: the contract declares one required parameter
    /// (<c>threshold</c>), so the orchestrator's own required-parameter gate
    /// (<c>parameterValues.ContainsKey(p.Name)</c>) only passes if the bound
    /// dictionary actually carries that key. Arguments are parsed generically
    /// from a JSON string via <see cref="JsonDocument"/> (a
    /// <see cref="JsonElement"/> value for <c>parameterValues</c>) rather than
    /// hand-built as the exact CLR dictionary type, mirroring how a real
    /// MCP/agent host would hand the call over. Confirms the dictionary
    /// parameter binds successfully with keys intact: the fallback
    /// string-JSON-parameter design discussed in the task brief was not
    /// needed.
    /// </summary>
    [Fact]
    public async Task okf_run_computation_binds_parameterValues_dictionary_from_a_json_object()
    {
        using var tmp = new TempDir();
        tmp.Write(
            "c/rev.md",
            "---\ntype: Attested Computation\nruntime: bigquery\nparameters:\n  - name: threshold\n    required: true\nexecutor: { resource: r.md, receipt: [job_id] }\n---\n# Computation\n\n```\nX\n```\n");

        IReadOnlyDictionary<string, object?>? captured = null;
        var runtime = FakeRuntime.Passing(receipt: new Receipt(new Dictionary<string, object?> { ["job_id"] = "j1" }));
        runtime.BindFunc = (contract, computation, values, _) =>
        {
            captured = values;
            return ValueTask.FromResult(new BoundComputation(contract.Runtime ?? "bigquery", computation.InlineCode, null, values));
        };

        var reg = new AttestationRuntimeRegistry(new Dictionary<string, IAttestationRuntime> { ["bigquery"] = runtime });
        var tools = new OkfBundleTools(tmp.Path, new AttestationOrchestrator(reg));
        var function = GetFunction(tools, "okf_run_computation");

        var argsJson = """{"conceptId": "c/rev", "parameterValues": {"threshold": 42, "label": "q3"}}""";
        using var argsDoc = JsonDocument.Parse(argsJson);
        var arguments = new AIFunctionArguments(
            new Dictionary<string, object?>
            {
                ["conceptId"] = argsDoc.RootElement.GetProperty("conceptId").GetString(),
                ["parameterValues"] = argsDoc.RootElement.GetProperty("parameterValues").Clone(),
            });

        var result = await function.InvokeAsync(arguments);
        var text = result?.ToString()?.ToLowerInvariant() ?? string.Empty;

        // If the dictionary had bound as empty (or with mangled keys), the
        // orchestrator's required-parameter gate would reject the run and
        // this would read "displayable: no" instead.
        Assert.Contains("displayable: yes", text);
        Assert.NotNull(captured);
        Assert.True(captured!.ContainsKey("threshold"), "the bound parameter values should carry the 'threshold' key supplied via JSON.");
    }

    private static AIFunction GetFunction(OkfBundleTools tools, string name) =>
        tools.GetTools().Cast<AIFunction>().Single(f => f.Name == name);

    private static List<string> RequiredProperties(AIFunction function)
    {
        var schema = function.JsonSchema;
        if (!schema.TryGetProperty("required", out var required))
        {
            return [];
        }

        return required.EnumerateArray().Select(e => e.GetString()!).ToList();
    }
}
