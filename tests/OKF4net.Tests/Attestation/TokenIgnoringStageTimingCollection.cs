// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Threading;
using Xunit;

namespace OKF4net.Tests.Attestation;

/// <summary>
/// Runs the classes holding a wall-clock bound on a token-ignoring stage
/// (<see cref="AttestationOrchestratorTests"/>, <c>OkfComputationToolsTests</c>) with no
/// other test alongside them: xunit executes a collection that disables parallelization
/// on its own, after all the parallel ones. The orchestrator abandons a stage on
/// thread-pool callbacks (timer, token registration, pool hop), so in-process pool
/// starvation from the rest of the suite stretches those bounds (#197; the same cause as
/// #149/#193, where pool callbacks ran 2.0-2.7 s late under four concurrent suites).
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class TokenIgnoringStageTimingCollection
{
    /// <summary>The collection's name, shared by the definition and the test classes.</summary>
    public const string Name = "Token-ignoring stage timing";

    /// <summary>
    /// Raises, never lowers, the pool's minimum worker count: a blocking stage parks a pool
    /// thread for the test's duration and, once the minimum is used up, the pool adds
    /// threads slowly, so the abandon callback would queue behind it. The setting is
    /// process-wide, but only raised; it removes test-host noise, not anything the product does.
    /// </summary>
    public static void EnsureThreadPoolHeadroom()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        if (workers < 64)
        {
            ThreadPool.SetMinThreads(64, io);
        }
    }
}
