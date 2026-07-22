// SPDX-License-Identifier: LGPL-3.0-or-later
using Microsoft.Agents.AI;

namespace OKF4net.Agents;

/// <summary>
/// An <see cref="AIContextProvider"/> that injects budget-bounded,
/// progressive-disclosure context from an OKF bundle into agent invocations
/// and (optionally) captures exchanges as long-term memory concepts in that
/// same bundle. It never invokes an LLM itself.
/// </summary>
/// <remarks>
/// <para>
/// This is the Phase 3 Task 1 skeleton: the constructor and options are
/// wired and validated, but <see cref="ProvideAIContextAsync"/> and
/// <see cref="StoreAIContextAsync"/> are still no-ops (an empty
/// <see cref="AIContext"/>, and nothing stored, respectively). Progressive
/// disclosure lands in a later task, memory capture in the one after that.
/// </para>
/// <para>
/// The provider shares an existing <see cref="OkfBundleTools"/> instance
/// rather than owning its own bundle root, so it reuses that instance's
/// thread-safe bundle cache, write lock, and <c>UtcNow</c> seam instead of
/// duplicating any of them.
/// </para>
/// </remarks>
public sealed class OkfContextProvider : AIContextProvider
{
    private readonly OkfBundleTools _tools;
    private readonly OkfContextProviderOptions _options;

    /// <summary>
    /// Creates the provider over <paramref name="tools"/>.
    /// </summary>
    /// <param name="tools">
    /// The bundle tool set to share (its bundle cache, write lock, and
    /// <c>UtcNow</c> seam) — not a raw bundle path.
    /// </param>
    /// <param name="options">
    /// Provider options; when omitted or <see langword="null"/>, a fresh
    /// <see cref="OkfContextProviderOptions"/> with its documented defaults
    /// is used.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="tools"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="options"/>.<see cref="OkfContextProviderOptions.MemoryDirectory"/>
    /// is not a single valid <see cref="ConceptId"/> segment (see
    /// <see cref="ConceptId.ValidateSegment"/>).
    /// </exception>
    public OkfContextProvider(OkfBundleTools tools, OkfContextProviderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(tools);

        var effectiveOptions = options ?? new OkfContextProviderOptions();

        try
        {
            ConceptId.ValidateSegment(effectiveOptions.MemoryDirectory);
        }
        catch (ConceptIdException ex)
        {
            throw new ArgumentException(
                $"options.MemoryDirectory must be a single valid concept id segment: {ex.Message}",
                nameof(options),
                ex);
        }

        _tools = tools;
        _options = effectiveOptions;
    }

    /// <inheritdoc/>
    /// <remarks>
    /// Skeleton implementation: always returns an empty <see cref="AIContext"/>.
    /// Progressive disclosure under <see cref="OkfContextProviderOptions.TokenBudget"/>
    /// is wired in a later task.
    /// </remarks>
    protected override ValueTask<AIContext> ProvideAIContextAsync(
        InvokingContext context,
        CancellationToken cancellationToken = default) =>
        new(new AIContext());

    /// <inheritdoc/>
    /// <remarks>
    /// Skeleton implementation: a no-op. Deterministic long-term memory
    /// capture (<see cref="OkfContextProviderOptions.EnableMemoryCapture"/>)
    /// is wired in a later task.
    /// </remarks>
    protected override ValueTask StoreAIContextAsync(
        InvokedContext context,
        CancellationToken cancellationToken = default) =>
        default;
}
