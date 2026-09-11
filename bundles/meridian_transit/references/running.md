---
type: Reference
title: Running this bundle's computations
description: How to stand up the Postgres this bundle expects and run both Attested Computations through OKF4net.Attestation.Containers.
resource: https://github.com/jchable/okf4net/tree/main/src/OKF4net.Attestation.Containers
tags: [run-instructions]
generated: { by: human:dlemoine@meridian, at: 2026-09-11T09:00:00Z }
status: stable
---

# What runs where

| Computation | `runtime` | Container kind | Needs a database |
|-------------|-----------|----------------|------------------|
| [`daily-ridership`](../computations/daily-ridership.md) | `postgres` | `SqlClient` | yes |
| [`capped-fare`](../computations/capped-fare.md)         | `python`   | `Script`    | no  |

The `python` one is pure, so it runs with the network off. The `postgres` one needs
to reach both the database and a package index, because the wrapper installs its
driver per run.

# Standing up the database

```sh
docker run --rm -d --name meridian-pg \
  -e POSTGRES_PASSWORD=demo -e POSTGRES_DB=meridian -p 5545:5432 postgres:16-alpine
docker exec -i meridian-pg psql -U postgres -d meridian < references/schema.sql
```

Then point the profile at it with **`host.docker.internal`, not `localhost`**:

```sh
export OKF_MERIDIAN_PG_CONN="postgresql://postgres:demo@host.docker.internal:5545/meridian"
```

The connection string is read on the host but consumed **inside** the SqlClient
container, where `localhost` is that container's own loopback. On native Linux
Docker Engine that hostname may need `--add-host=host.docker.internal:host-gateway`,
or the host's bridge IP instead.

# Registering the two runtimes

One profile per bundle `runtime` name, registered under that name:

```csharp
var engine = new CliContainerEngine();

var python = new ContainerAttestationRuntime(
    engine,
    new ContainerRuntimeProfile { Image = "python:3.12-slim", Kind = ContainerRuntimeKind.Script });

var postgres = new ContainerAttestationRuntime(
    engine,
    new ContainerRuntimeProfile
    {
        Image = "python:3.12-slim",
        Kind = ContainerRuntimeKind.SqlClient,
        Environment = new Dictionary<string, string>
        {
            ["OKF_CONN"] = Environment.GetEnvironmentVariable("OKF_MERIDIAN_PG_CONN")!,
        },
    });

var registry = new AttestationRuntimeRegistry(
    new Dictionary<string, IAttestationRuntime> { ["python"] = python, ["postgres"] = postgres });
```

Both profiles are hardened by default: read-only root, a `/tmp` tmpfs, and
memory/CPU/PID ceilings. The `SqlClient` image is Python-capable because the wrapper
is always `python3`, whatever the database.

# Calling them

```csharp
var orchestrator = new AttestationOrchestrator(registry);

var ridership = await orchestrator.RunAsync(
    bundle, ConceptId.Parse("computations/daily-ridership"),
    new Dictionary<string, object?> { ["service_date"] = "2026-09-10" });

var capped = await orchestrator.RunAsync(
    bundle, ConceptId.Parse("computations/capped-fare"),
    new Dictionary<string, object?> { ["fares_cents"] = "[250,250,250,250]", ["cap_cents"] = 700 });
```

With the seed data, the first returns `completed_trips = 5`, `distinct_riders = 2`;
the second returns `charged_cents = 700`, `waived_cents = 300`,
`per_trip_cents = [250, 250, 200, 0]`.

# What a passing attestation here does and does not mean

`capped-fare`'s attester recomputes the policy from the inputs, so a pass is
evidence the displayed number is the number the policy defines.

`daily-ridership`'s attester checks invariants of the query's shape, and that is
all it can do: `executed_sql` is echoed back by the host's own wrapper, so
comparing it to the sanctioned text cannot fail unless the host is broken. Postgres
mints no equivalent of BigQuery's `job_id` for a consumer to resolve independently.
Read a pass there as "the result is shaped like this query's result", not as "the
database ran this text".
