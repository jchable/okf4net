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
executor's image. So the executor's image is whatever the sanctioned code needs
— a `Script` profile can run on an image with no Python at all — while the
bootstrap keeps its own. (A `SqlClient` image is the one that *must* be
Python-capable: the wrapper is `python3`.)

## What the host controls

`ContainerRuntimeProfile` carries the image, the interpreter, the environment,
and the `--network` mode. Everything about how hardened the container itself
is lives on one shared `ContainerIsolation` record (`profile.Isolation` /
`ContainerAttesterOptions.Isolation`), so a hardening decision reaches the
script executor, the SQL executor and the attester at once instead of landing
on two of the three and missing the third:

- **`Isolation.User`** — passed as `--user`; defaults to `65534:65534` (`nobody` on every
  mainstream image), so bundle-authored code never runs as the image's default
  user (root, on most images). A host whose image insists on its own user sets
  this to `null`, knowingly.
- **`Isolation.DropAllCapabilities`** — `--cap-drop ALL` when true (default).
- **`Isolation.NoNewPrivileges`** — `--security-opt no-new-privileges` when true (default).
- **`Isolation.ReadOnlyRootFilesystem`** / **`Isolation.TmpfsMounts`** — see below.
- **The four per-run ceilings** (`Isolation.MemoryBytes`/`--memory`,
  `Isolation.Cpus`/`--cpus`, `Isolation.PidsLimit`/`--pids-limit`,
  `Isolation.Timeout`, wall clock). A non-positive ceiling is
  **rejected**, because to docker and podman zero there means *unlimited* — a
  value that removes the ceiling it appears to set.

The root filesystem is mounted **read-only** by default, with `/tmp` as a
memory-backed `tmpfs` that dies with the container. That is not a hole in the
hardening: it is the one writable path the stages genuinely need — the attester
bootstrap writes the bundle's module there before importing it, and the SQL
wrapper installs its driver there — named explicitly instead of leaving the
whole image writable. Both `Isolation.ReadOnlyRootFilesystem` and
`Isolation.TmpfsMounts` can be changed, on the profile and on
`ContainerAttesterOptions`, if a host needs different paths.

A different path is honoured, not just mounted: the **first**
`Isolation.TmpfsMounts` entry is passed into every container as **`TMPDIR`**,
and nothing inside the containers names `/tmp` itself — the attester
bootstrap's temp module, pip's own working files and the SQL wrapper's
`--target` all follow `TMPDIR`. So `Isolation.TmpfsMounts = ["/scratch"]` works
with `/tmp` left read-only. A `TMPDIR` the
host sets in `Environment` wins over the derived one — so under a read-only root
it must lead to one of the mounts: `tempfile` (which pip goes through too) never
creates it, and skips on through `TEMP`, `TMP`, `/tmp`, `/var/tmp` and
`/usr/tmp`, which are read-only unless mounted. Each entry must be an
absolute container path, optionally with engine options (`/scratch:size=64m`);
anything else is rejected when the `ContainerIsolation` is built.

On `ContainerAttesterOptions`, change these with
`new ContainerAttesterOptions().Isolation with { ... }` rather than a fresh
`new ContainerIsolation { ... }`: the latter starts from the profile-sized
ceilings and silently drops the attester's smaller ones.

An empty `Isolation.TmpfsMounts` under a read-only root is allowed on a
`ContainerRuntimeProfile` — a script that writes nothing, or a `SqlClient` image
with the driver vendored in, needs no scratch, and that is the tightest
configuration available (a bare Python `SqlClient` image then has nowhere to
install its driver, and fails). It is **rejected** on `ContainerAttesterOptions`,
when the `ContainerAttester` is constructed, and so is a `TMPDIR` in its
`Environment` from which none of those candidates is one of its mounts (not
mounted `:ro`) or docker's own `/dev/shm`: the bootstrap writes a temp file on every run, so that
attester could never attest anything. The check is built to reject only what is
sure to fail — candidates are resolved as `tempfile` resolves them against the
`/` working directory of an image with no `WORKDIR` — and it cannot see three
things, each of which can make it reject a configuration that would have
worked: an image `WORKDIR` that is itself a mount, `TEMP`/`TMP` set by the
image's own `ENV`, and podman, whose default `--read-only-tmpfs` also makes
`/run`, `/tmp` and `/var/tmp` writable (podman is not exercised by this
project's tests). In each case, point `TMPDIR` at a mount. The profile does not
check its own `TMPDIR` the same way, for the same reason it allows no mount.

Network access is the one isolation setting that stays on the profile rather
than `ContainerIsolation`, because it defaults *by kind*: closed for `Script`
and open for `SqlClient` (which must reach a database, and a package index
unless its image vendors the driver). Either can be overridden — including
back to the engine's default, with an explicit `null`.

## Limitations in this version

- **Secrets travel as environment variables.** `OKF_CONN` and anything else in
  the profile's `Environment` are visible to `docker inspect` and in the host
  process list. **`OKF_PARAMS_JSON` is the same exposure class** — parameter
  values, not just connection strings, are readable that way. Do not pass a
  value you would not put in a process listing.
- **The `SqlClient` wrapper pip-installs its driver when the image lacks it**
  (pinned to `pg8000==1.31.5`, since the process holds `OKF_CONN`), into the
  tmpfs rather than the image, because the root filesystem is read-only. So on a
  bare Python image a run needs network access to a package index and pays the
  install cost each time. An image with the driver vendored in skips the install
  entirely, can run with `NetworkMode` closed down to its database's network, and
  is the better answer for anything beyond local use.
- **`executed_sql` is echoed by this host's own wrapper**, so comparing it
  against the sanctioned text proves the wrapper sent what it was given — not
  that the database ran it. Real provenance needs a receipt field the engine
  itself produces, such as a BigQuery `job_id` resolved against the job's own
  recorded SQL.
- **`--user` is a uid, not a sandbox.** Non-root plus `--cap-drop ALL` and
  `no-new-privileges` removes the ordinary escalation paths; it does not add
  user namespaces, seccomp/AppArmor profiles beyond the engine's defaults, or
  gVisor/Kata-class isolation. An image whose entrypoint requires root will
  fail under the default profile — set `Isolation = new() { User = null }` to
  keep the image's user, knowingly.
