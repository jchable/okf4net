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
    /// Whether (and how) the provider captures exchanges as long-term memory
    /// concepts in the bundle after each invocation. Defaults to
    /// <see cref="MemoryCaptureMode.Disabled"/>: the memory that
    /// <see cref="MemoryCaptureMode.SharedBundle"/> writes is bundle-global
    /// and unscoped by session, user, or tenant, so a scored recall in
    /// <see cref="OkfContextProvider.ProvideAIContextAsync"/> can surface one
    /// session's captured exchange to a completely different session sharing
    /// the same bundle. Opt in only when the bundle is intended to be a
    /// shared, non-sensitive memory across those sessions.
    /// </summary>
    public MemoryCaptureMode MemoryCapture { get; init; } = MemoryCaptureMode.Disabled;

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

/// <summary>
/// The long-term memory capture behavior of <see cref="OkfContextProvider"/>,
/// selected via <see cref="OkfContextProviderOptions.MemoryCapture"/>.
/// </summary>
public enum MemoryCaptureMode
{
    /// <summary>
    /// Writes no conversational data. <see cref="OkfContextProvider.StoreAIContextAsync"/>
    /// is a complete no-op: no bundle access, no write attempt.
    /// </summary>
    Disabled,

    /// <summary>
    /// Writes the current deterministic daily memory (the last user message
    /// and the agent's final response, deterministically formatted -- no
    /// LLM involved) into the shared bundle. Any session that can read the
    /// bundle may later retrieve the captured exchange, since the write is
    /// bundle-global and unscoped by session, user, or tenant.
    /// </summary>
    SharedBundle,
}
