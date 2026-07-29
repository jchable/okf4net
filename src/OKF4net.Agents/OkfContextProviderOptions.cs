// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Agents.AI;
using OKF4net.Catalog;

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
    /// <see cref="MemoryCaptureMode.Enabled"/> writes is bundle-global
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
    [Obsolete("MemoryDirectory (single-bundle capture) is deprecated in favour of role:memory catalog sources and the scoped IMemoryStore. Used only by the V1 OkfBundleTools-based provider constructor.")]
    public string MemoryDirectory { get; init; } = "memory";

    /// <summary>
    /// The maximum number of scored concepts injected into a single
    /// invocation's context. Defaults to <c>5</c>.
    /// </summary>
    public int MaxConceptsInjected { get; init; } = 5;

    /// <summary>
    /// The host-authenticated scope for an invocation. Absent (<see langword="null"/>)
    /// ⇒ <see cref="KnowledgeAccessScope.Local"/>. Used only by the scoped (V2)
    /// provider constructor. Never derive scope from a message.
    /// </summary>
    /// <remarks>
    /// If this delegate throws, the exception is <b>not</b> swallowed: it
    /// propagates straight out of <c>ProvideAIContextAsync</c>/<c>StoreAIContextAsync</c>
    /// to the caller. That is a deliberate asymmetry with the provider's
    /// otherwise-documented never-throw guarantee, which covers failures in
    /// bundle/resolver/memory-store I/O -- not a host-supplied delegate that
    /// is itself broken. A <see cref="ScopeAccessor"/> that throws is a
    /// host-contract violation, not a data or I/O failure, so it is
    /// deliberately left unguarded rather than silently degraded.
    /// </remarks>
    public Func<AIContextProvider.InvokingContext, KnowledgeAccessScope>? ScopeAccessor { get; init; }

    /// <summary>The tier scoped memory capture writes to. Defaults to <see cref="MemoryTier.User"/>.</summary>
    public MemoryTier CaptureTier { get; init; } = MemoryTier.User;

    /// <summary>
    /// The floor fraction (0..1) of <see cref="TokenBudget"/> guaranteed to the
    /// knowledge surface before spillover. Defaults to <c>0.6</c> (knowledge
    /// slightly prioritized; memory augments).
    /// </summary>
    public double KnowledgeBudgetShare { get; init; } = 0.6;

    /// <summary>
    /// The floor fraction (0..1) of <see cref="TokenBudget"/> guaranteed to the
    /// memory surface before spillover. Defaults to <c>0.4</c>. Must satisfy
    /// <see cref="KnowledgeBudgetShare"/> + this ≤ 1.
    /// </summary>
    public double MemoryBudgetShare { get; init; } = 0.4;

    /// <summary>How stale concepts (§5.5) are treated when building context. Default <see cref="StalePolicy.Use"/>: surface everything (the read tool flags staleness), never silently drop.</summary>
    public StalePolicy StalePolicy { get; init; } = StalePolicy.Use;

    /// <summary>
    /// The <see cref="OKF4net.Catalog.KnowledgeQuery.FairnessQuota"/> to
    /// attach to the knowledge query this provider issues;
    /// <see langword="null"/> (the default) attaches none, deferring to
    /// whatever the resolver itself is configured with. When set, must be
    /// greater than zero; validated by the V2 provider constructor.
    /// </summary>
    /// <remarks>
    /// Exposed because this provider is the archetypal early-truncating
    /// consumer: it renders passages top-down until
    /// <see cref="TokenBudget"/> is exhausted, so without interleaving one
    /// prolific source's run can consume the whole budget before any other
    /// source is reached. Has no effect unless the injected resolver uses a
    /// fusing strategy.
    /// </remarks>
    public int? KnowledgeQueryFairnessQuota { get; init; }
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
    /// Captures the deterministic exchange into memory (V1: the single
    /// bundle; V2: the scope's tier via <c>IMemoryStore</c>). In V1, this
    /// writes the current deterministic daily memory (the last user message
    /// and the agent's final response, deterministically formatted -- no
    /// LLM involved) into the shared bundle. Any session that can read the
    /// bundle may later retrieve the captured exchange, since the write is
    /// bundle-global and unscoped by session, user, or tenant.
    /// </summary>
    Enabled,
}
