# OKF4net.Attestation.Containers

A host implementation of `OKF4net.Attestation`'s §10 contracts
(`IParameterBinder`, `IComputationExecutor`, `IAttester`) that executes the
*actual* sanctioned computation and attester scripts a bundle references, in
real containers (Docker/Podman/nerdctl) — never a C# reimplementation of a
bundle's logic. See
[the design doc](https://github.com/jchable/okf4net/blob/main/docs/superpowers/specs/2026-09-07-attestation-containers-design.md)
for the full rationale and protocol.

Requires a container engine binary (`docker`, `podman`, or `nerdctl`)
installed and on `PATH` — this is a runtime prerequisite, not a NuGet
dependency; the project itself has zero third-party package references.
