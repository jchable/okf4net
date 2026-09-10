// SPDX-License-Identifier: LGPL-3.0-or-later
using System;
using System.Threading;
using System.Threading.Tasks;
using OKF4net.Attestation.Containers;

namespace OKF4net.Tests.Attestation.Containers;

/// <summary>
/// A configurable in-memory <see cref="IContainerEngine"/> for tests: records
/// the last <see cref="ContainerRunSpec"/> it was given, and returns a
/// settable canned result. No process is ever spawned. Mirrors
/// <c>FakeRuntime</c>'s style in <c>tests/OKF4net.Tests/Attestation</c>.
/// </summary>
public sealed class FakeContainerEngine : IContainerEngine
{
    /// <summary>The most recent spec passed to <see cref="RunAsync"/>, or <see langword="null"/> before any call.</summary>
    public ContainerRunSpec? LastSpec { get; private set; }

    /// <summary>Produces the result for a given spec. Defaults to a successful run with an empty JSON object on stdout.</summary>
    public Func<ContainerRunSpec, ContainerRunResult> Respond { get; set; } = _ => new ContainerRunResult(0, "{}", "");

    /// <inheritdoc />
    public ValueTask<ContainerRunResult> RunAsync(ContainerRunSpec spec, CancellationToken cancellationToken = default)
    {
        LastSpec = spec;
        return ValueTask.FromResult(Respond(spec));
    }
}
