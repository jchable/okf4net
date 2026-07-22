// SPDX-License-Identifier: LGPL-3.0-or-later
using System.ComponentModel;
using Microsoft.Extensions.AI;
using OKF4net.Agents;

namespace OKF4net.Tests.Agents;

/// <summary>
/// Tests <see cref="OkfBundleTools.GetTools"/>: the nine tool methods exposed
/// as Agent Framework <see cref="AIFunction"/>s (via <see cref="AITool"/>),
/// with no LLM involved — everything is verified at the
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
    ];

    [Fact]
    public void GetTools_returns_exactly_nine_tools()
    {
        var tools = new OkfBundleTools(BundlePath);
        Assert.Equal(9, tools.GetTools().Count);
    }

    [Fact]
    public void GetTools_names_are_the_nine_snake_case_names_in_stable_order()
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
