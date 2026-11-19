## What

<!-- What does this PR change, and why? -->

## Spec impact

<!-- Does this touch behaviour covered by the OKF v0.2 spec? Cite the section
(§) and explain how conformance is preserved. Write "none" otherwise. -->

## Checklist

- [ ] `dotnet test OKF4net.sln` passes (including golden comparisons)
- [ ] If the PR touches `src/OKF4net.Attestation.Containers/` or `src/OKF4net.Providers.S3/` (or bumps `AWSSDK.S3`/`AWSSDK.Core`): the local `dotnet test tests/OKF4net.Tests --filter Category=ContainerIntegration` / `Category=S3Integration` run reports 0 skipped (both are excluded from CI)
- [ ] `dotnet format OKF4net.sln --verify-no-changes` passes
- [ ] Tests added/updated for behaviour changes
- [ ] No new runtime dependencies (zero-dependency policy)
- [ ] New files carry the `// SPDX-License-Identifier: LGPL-3.0-or-later` header
