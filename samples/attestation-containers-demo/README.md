<!-- samples/attestation-containers-demo/README.md -->
# attestation-containers-demo

Runs `bundles/attestation_containers_demo/` end to end through
`OKF4net.Attestation.Containers`. Own solution, not part of `OKF4net.sln` or
CI (same convention as `samples/acme-retail-agent` and
`samples/catalog-explorer`).

## Prerequisites

- Docker (or Podman/nerdctl) on `PATH`.
- For the `postgres` runtime: a reachable Postgres instance with a `users(id int, active boolean)` table, and `OKF_DEMO_PG_CONN` set to its connection string. Without it, this demo only runs the `python` runtime.

  **Use `host.docker.internal`, not `localhost`.** The connection string is read by this sample on the host, but it is consumed *inside* the SqlClient container, where `localhost` means that container's own loopback — not yours. A fixture started with `-p 5544:5432` is therefore reached as:

  ```sh
  docker run --rm -d --name okf-demo-pg -e POSTGRES_PASSWORD=demo -e POSTGRES_DB=demo -p 5544:5432 postgres:16-alpine
  docker exec -i okf-demo-pg psql -U postgres -d demo -c "CREATE TABLE users(id int, active boolean); INSERT INTO users VALUES (1,true),(2,true),(3,false);"
  export OKF_DEMO_PG_CONN="postgresql://postgres:demo@host.docker.internal:5544/demo"
  ```

  On native Linux Docker Engine (not Docker Desktop) that hostname may need `--add-host=host.docker.internal:host-gateway`, or substitute the host's real bridge IP.

## Run

```bash
dotnet run --project samples/attestation-containers-demo
```
