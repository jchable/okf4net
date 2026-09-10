<!-- samples/attestation-containers-demo/README.md -->
# attestation-containers-demo

Runs `bundles/attestation_containers_demo/` end to end through
`OKF4net.Attestation.Containers`. Own solution, not part of `OKF4net.sln` or
CI (same convention as `samples/acme-retail-agent` and
`samples/catalog-explorer`).

## Prerequisites

- Docker (or Podman/nerdctl) on `PATH`.
- For the `postgres` runtime: a reachable Postgres instance with a `users(id int, active boolean)` table, and `OKF_DEMO_PG_CONN` set to its connection string. Without it, this demo only runs the `python` runtime.

## Run

```bash
dotnet run --project samples/attestation-containers-demo
```
