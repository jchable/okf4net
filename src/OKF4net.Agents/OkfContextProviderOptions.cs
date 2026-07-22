// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Agents;

/// <summary>Options for <see cref="OkfContextProvider"/>.</summary>
public sealed class OkfContextProviderOptions
{
    /// <summary>
    /// The approximate token budget for context injected into an agent
    /// invocation, estimated as <c>chars / 4</c> (no tokenizer dependency).
    /// Defaults to <c>2000</c>. A non-positive value is accepted by the
    /// constructor; it simply yields an empty injected context.
    /// </summary>
    public int TokenBudget { get; init; } = 2000;

    /// <summary>
    /// Whether the provider captures exchanges as long-term memory concepts
    /// in the bundle after each invocation. Defaults to <c>true</c>.
    /// </summary>
    public bool EnableMemoryCapture { get; init; } = true;

    /// <summary>
    /// The bundle subdirectory that holds memory concepts, as a single
    /// <see cref="ConceptId"/> segment (no <c>/</c>; see
    /// <see cref="ConceptId.ValidateSegment"/>). Defaults to <c>"memory"</c>.
    /// Validated by <see cref="OkfContextProvider"/>'s constructor.
    /// </summary>
    public string MemoryDirectory { get; init; } = "memory";

    /// <summary>
    /// The maximum number of scored concepts injected into a single
    /// invocation's context. Defaults to <c>5</c>.
    /// </summary>
    public int MaxConceptsInjected { get; init; } = 5;
}
