# OKF4net.Attestation.Containers

A host implementation of `OKF4net.Attestation`'s §10 contracts
(`IParameterBinder`, `IComputationExecutor`, `IAttester`) that executes the
*actual* sanctioned computation and attester scripts a bundle references, in
real containers (Docker/Podman/nerdctl) — never a C# reimplementation of a
bundle's logic. See
[the design doc](https://github.com/jchable/okf4net/blob/main/docs/superpowers/specs/2026-09-07-attestation-containers-design.md)
for the full rationale.

Requires a container engine binary (`docker`, `podman`, or `nerdctl`)
installed and on `PATH` — this is a runtime prerequisite, not a NuGet
dependency; the project itself has zero third-party package references. For
the same reason it is **not published as a NuGet package**: reference it as a
project, or vendor it.

## The wire contract

§10 fixes the *interface* and leaves invocation entirely host-defined, so
everything below is this host's own convention. A bundle whose scripts follow
it runs unmodified; one written for a different host will not.

### A `Script` computation

The sanctioned text is run as a standalone program by the profile's
`Interpreter` (default `python3`), fed on **stdin**. Parameter values arrive
as a JSON object in the environment variable **`OKF_PARAMS_JSON`**, holding
only the parameters the concept declares — an undeclared key the caller passed
never reaches the container.

The script must **print its receipt as a single JSON object on stdout** and
write nothing else there. The declared `executor.receipt` fields are read from
that object.

```python
import json, os
params = json.loads(os.environ['OKF_PARAMS_JSON'])
print(json.dumps({'message': f"Hello, {params['name']}!"}))
```

### A `SqlClient` computation

The sanctioned text is SQL, sent to a project-authored Python wrapper that
binds values through the driver's own native parameter mechanism. **The SQL is
never edited** — placeholders included — and no value is ever interpolated
into it. The connection string is read from the environment variable
**`OKF_CONN`**, which the host supplies through the profile's `Environment`.

The receipt the wrapper produces carries `executed_sql` (the text sent, echoed
back) and `result` (the rows, as a list of column-keyed objects). A column
type JSON cannot represent natively — `NUMERIC`, `DATE`, `TIMESTAMP`, `UUID`,
`BYTEA` — arrives as its Python `str()` form.

Parameters must not be named `self`, `sql`, `stream` or `types`. The driver's
signature is `run(self, sql, stream=None, types=None, **params)`, so those
names collide in one of two ways: `self` and `sql` raise a `TypeError` inside
the container blamed on your query, while `stream` and `types` raise nothing
at all — the value is consumed as a driver option and your placeholder is
left unbound. The run is refused before any container starts.

### An attester

An attester script is imported as a **library**, not run as a program. It must
expose:

```python
def attest(*, sanctioned_computation, receipt, values):
    return {'ok': True, 'reason': None}
```

`values` is the same filtered set the executor received. The return value is
read as `ok` (anything but `true` fails the attestation) and an optional
`reason`. A stray `print()` inside the module is harmless — the bootstrap
captures stdout for the whole import-and-call.

The attester always runs on `ContainerAttesterOptions.Image`, never the
executor's image: a `SqlClient` profile's image need not have Python at all.

## What the host controls

`ContainerRuntimeProfile` carries the image, the interpreter, the environment,
the `--network` mode, and four per-run ceilings (`--memory`, `--cpus`,
`--pids-limit`, wall clock). A non-positive ceiling is **rejected**, because to
docker and podman zero there means *unlimited* — a value that removes the
ceiling it appears to set.

Network access defaults closed for `Script` and open for `SqlClient` (which
must reach a database and a package index), and either can be overridden.

The root filesystem is mounted **read-only** by default, with `/tmp` as a
memory-backed `tmpfs` that dies with the container. That is not a hole in the
hardening: it is the one writable path the stages genuinely need — the attester
bootstrap writes the bundle's module there before importing it, and the SQL
wrapper installs its driver there — named explicitly instead of leaving the
whole image writable. Both `ReadOnlyRootFilesystem` and `TmpfsMounts` are on the
profile if a host needs different paths.

## Limitations in this version

- **Secrets travel as environment variables.** `OKF_CONN` and anything else in
  the profile's `Environment` are visible to `docker inspect` and in the host
  process list. **`OKF_PARAMS_JSON` is the same exposure class** — parameter
  values, not just connection strings, are readable that way. Do not pass a
  value you would not put in a process listing.
- **The `SqlClient` wrapper pip-installs its driver on every run**, so it needs
  network access to a package index and pays the install cost each time. A
  purpose-built image with the driver vendored in is the better answer for
  anything beyond local use.
- **The `SqlClient` wrapper installs its driver into the tmpfs on every run**
  (`pip install --target /tmp/okf-pkgs`), so it needs network access to a
  package index and pays the install cost each time.
- **`executed_sql` is echoed by this host's own wrapper**, so comparing it
  against the sanctioned text proves the wrapper sent what it was given — not
  that the database ran it. Real provenance needs a receipt field the engine
  itself produces, such as a BigQuery `job_id` resolved against the job's own
  recorded SQL.
