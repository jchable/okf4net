# S3 bundle provider (#142) — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Ship `OKF4net.Providers.S3`: a read-only, truly asynchronous `IBundleProvider` over Amazon S3 and S3-compatible stores, built on the AWS SDK for .NET with a mandatory request guard, plus the two core corrections it needs (D15).

**Architecture:** The provider builds its own `AmazonS3Client` from host credentials and an `S3BundleSource`, with a hermetic configuration and one provider-owned `HttpClient` whose handler chain is `PinningHandler` (one authority + one path prefix per configuration) → `BoundingHandler` (transport bounds before the SDK deserializes; captures and strictly parses listing pages) → the network handler. Every provider operation runs inside an `OperationScope` (concurrency permit, operation deadline, a per-operation credentials snapshot read by the SDK through an internal `AWSCredentials`). Reads buffer the whole body and version it by SHA-256 (same format as the filesystem provider); the bundle token `s3v1:` hashes the listing. The core gains two changes: the load reads the bundle version before listing, and `ResolveResourceKeyAsync` turns only an access refusal into `Unsafe`.

**Tech Stack:** C# 14 / .NET 10, `AWSSDK.S3` ≥ 4.1.0 (this project only), xunit (+ `Xunit.SkippableFact`, already referenced), RustFS 1.0.3 in a container (local only), Native AOT, GitHub Actions.

**Spec:** `docs/superpowers/specs/2026-11-16-s3-bundle-provider-design.md` (revision 4, written in French). Decisions D1–D16 are in its §2; read §4 (provider), §6 (tests) and §9 (gate) before any task. Rulings are recorded on #142 (comment of 2026-11-16) and #141 (comment of 2026-11-15). Prerequisite reading: `src/OKF4net/Providers/IBundleProvider.cs`, `src/OKF4net/Bundle.cs` (`LoadCoreAsync`, `ResolveResourceKeyAsync`, `ReadForSurveyAsync`, `Translate`), the embedded provider `src/OKF4net.Providers.EmbeddedResources/`.

## Global Constraints

- Branch `feat/s3-bundle-provider`, stacked on `feat/embedded-resource-provider` (PR #237). Rebase on `dev` only after #237 merges. Nothing is pushed, no PR is opened, nothing is merged without the maintainer's explicit go.
- `src/OKF4net.Providers.S3/` references `src/OKF4net/` and **exactly one** package, `AWSSDK.S3` with `Version="4.1.0"` (a minimum; no brackets). Nothing else in `src/` references this project. The core grants it **no** `InternalsVisibleTo`; the project grants its internals to `OKF4net.Tests` only.
- `src/OKF4net/` is touched by Tasks 2 and 3 only, and only in `LoadCoreAsync` and `ResolveResourceKeyAsync` of `Bundle.cs` (plus their tests). Anything else in the core is #247 or out of scope.
- Provider facts (spec §4.3): `Capabilities = new(CanWrite: false, CompletesSynchronously: false)`; `NativeConstraints = BundleKeyConstraints.WellFormedUnicode`; `Id = "s3:" + first 32 lowercase hex of SHA-256(UTF-8("s3v1\n" + endpoint + "\n" + bucket + "\n" + prefix))`; `Location = "s3:" + (DisplayName ?? first 8 hex of the Id's hash) + (prefix == "" ? "" : "/" + prefix)` — **never the bucket**; object version = lowercase hex SHA-256 of the bytes (no prefix, same as `FileSystemBundleProvider`); bundle version = `"s3v1:" + lowercase hex SHA-256` of the canonical listing stream (spec §4.5).
- Defaults: `RequestTimeout = 30 s`, `MaxConcurrentRequests = 16`, `MaxObjectBytes = BundleLimits.Default.MaxObjectBytes` (256 MiB, at most `Array.MaxLength`), `Checksums = WhenSupported`. Transport bounds: listing body **8 MiB** (8 388 608 bytes), error body **64 KiB** (65 536 bytes), continuation token **1 024 bytes**.
- Messages are library-authored, closed vocabulary: `"S3 <operation> failed: <kind>"` + optional `" (HTTP <code>)"` + optional `" reading '<key>'"`. Never the bucket, the endpoint, a request id, a URL, the S3 error code or any server text.
- Test category for anything that needs a container: `[Trait("Category", "S3Integration")]`. Image `docker.io/rustfs/rustfs:1.0.3`, never `latest`. `ci.yml` and `release.yml` filter `Category!=ContainerIntegration&Category!=S3Integration`.
- Fixed clock for every validation/audit comparison: `new FixedClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero))`.
- `tests/fixtures/` and `bundles/` are read, never edited; no golden is regenerated. `docs/spec/SPEC.md` untouched.
- New files start with `// SPDX-License-Identifier: LGPL-3.0-or-later` (C#) or `<!-- SPDX-License-Identifier: LGPL-3.0-or-later -->` (XML/MSBuild). XML doc comments on public API. `ConfigureAwait(false)` on every `await` in `src/` (CA2007 is an error there).
- Per task, before committing: `dotnet build OKF4net.sln`, `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration&Category!=S3Integration"`, `dotnet format OKF4net.sln --verify-no-changes`. Commit messages end with `Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>`.
- **Stop rule.** If any measurement of Task 1 contradicts the spec, or a later task meets SDK behaviour the gate did not record, stop and report to the maintainer: the spec section is reopened, nothing is worked around silently.

## Review Focus

Failure modes the spec implies that no happy-path test exercises; each is pinned by the test named, in the task that owns the code.

1. **A redirect between two authorities that are each valid for *some* configuration** (`demo.s3.eu-west-1.amazonaws.com/kb/x` ↔ `s3.eu-west-1.amazonaws.com/kb/x`) must be refused even though the host looks like S3 — Task 5, `A_307_between_the_two_authorities_of_one_bucket_is_refused_both_ways`.
2. **A credentials provider that succeeds once and then blocks** must never be called again outside the isolated, bounded resolution, including on the SDK's redirect re-signing path — Task 6, `A_provider_that_succeeds_once_then_blocks_is_never_called_from_the_pipeline`.
3. **Many small but valid listing pages** (pages of 1–100 keys) must load as long as the entry bound holds — Task 7, `Many_small_valid_pages_are_accepted`.
4. **`%FF` versus `%25FF` in an encoded listing** must be either a refused response or two distinct literal names — never one alias — Task 7, `Lax_decoding_aliases_are_impossible`.
5. **A credentials failure that appears only during the executable-resource survey** must fail the load, never record `Unsafe` or `Unreadable` — Task 8, `A_credentials_failure_during_the_survey_fails_the_load`.

---

## File Structure

| File | Responsibility |
|---|---|
| `src/OKF4net/Bundle.cs` | D15 (a) in `LoadCoreAsync`, D15 (b) in `ResolveResourceKeyAsync`. |
| `src/OKF4net.Providers.S3/OKF4net.Providers.S3.csproj` | Project, the one `PackageReference`, packaging block, `InternalsVisibleTo OKF4net.Tests`. |
| `src/OKF4net.Providers.S3/S3BundleSource.cs` | Public configuration record. |
| `src/OKF4net.Providers.S3/S3ChecksumMode.cs`, `S3Operation.cs`, `S3FailureKind.cs` | Public enums. |
| `src/OKF4net.Providers.S3/S3RequestException.cs` | Public exception, closed-vocabulary message. |
| `src/OKF4net.Providers.S3/S3BundleProvider.cs` | The `IBundleProvider`; wires the internals. |
| `src/OKF4net.Providers.S3/Internal/SourceRules.cs` | D6 validation, normalized source, `Id`, `Location`. |
| `src/OKF4net.Providers.S3/Internal/AddressingPlan.cs` | The one admitted authority and path prefix; expected request paths. |
| `src/OKF4net.Providers.S3/Internal/OperationContext.cs` | `AsyncLocal` per-operation state: expected request, credentials snapshot, listing capture. |
| `src/OKF4net.Providers.S3/Internal/PinningHandler.cs` | Refuses any request outside the operation's expected authority/path/prefix. |
| `src/OKF4net.Providers.S3/Internal/BoundingHandler.cs` | Transport bounds before deserialization; captures listing pages. |
| `src/OKF4net.Providers.S3/Internal/GuardException.cs` | The marked, non-retried exception both handlers throw. |
| `src/OKF4net.Providers.S3/Internal/ProviderHttpClientFactory.cs` | One provider-owned `HttpClient`, never cached nor disposed by the SDK. |
| `src/OKF4net.Providers.S3/Internal/ClientBuilder.cs` | Hermetic `AmazonS3Config` and the client. |
| `src/OKF4net.Providers.S3/Internal/SnapshotCredentials.cs` | Internal `AWSCredentials` returning the operation's snapshot; isolated resolution. |
| `src/OKF4net.Providers.S3/Internal/OperationScope.cs` | Permit, deadline, failure priority. |
| `src/OKF4net.Providers.S3/Internal/S3KeyCodec.cs` | Strict percent/UTF-8 decoding; code-point ordering. |
| `src/OKF4net.Providers.S3/Internal/ListPageParser.cs` | Strict parse of a raw `ListObjectsV2` page. |
| `src/OKF4net.Providers.S3/Internal/ListingWalker.cs` | The private entry iterator with every D9 invariant. |
| `src/OKF4net.Providers.S3/Internal/BundleTokenHasher.cs` | The `s3v1` stream. |
| `src/OKF4net.Providers.S3/Internal/ErrorClassifier.cs` | The closed table of spec §4.6. |
| `src/OKF4net.Providers.S3/README.md` | Package README; local integration command at the top. |
| `tests/OKF4net.Tests/Providers/LoadOrderTests.cs` | T0 (a). |
| `tests/OKF4net.Tests/Providers/ResolveFailureTests.cs` | T0 (b). |
| `tests/OKF4net.Tests/Providers/S3/FakeS3Handler.cs` | Scripted S3 server as an `HttpMessageHandler`. |
| `tests/OKF4net.Tests/Providers/S3/S3Test.cs` | Builds providers over the fake; canned XML. |
| `tests/OKF4net.Tests/Providers/S3/*Tests.cs` | T1. |
| `tests/OKF4net.Tests/Providers/S3/Integration/*.cs` | T2 (`S3Integration`). |
| `tests/S3HostileEnvProbe/` | Child-process probe for the hostile-environment test (outside `OKF4net.sln`, built by a non-referencing `ProjectReference` like `YamlHostileProbe`). |
| `tests/S3ProviderAotProbe/` | T3 AOT probe (outside `OKF4net.sln`, real packages). |
| `.github/workflows/ci.yml`, `.github/workflows/release.yml` | Filters, `s3-aot` job, pack step. |
| `README.md`, `CHANGELOG.md`, `CLAUDE.md` | Docs (spec §7.1). |

---

### Task 1: The gate — measure spec §9 before any production code

**Files:**
- Create (throwaway, NOT committed): `<scratchpad>/s3gate/` — a console prototype per version, reusing the measurement harness of the D1 prototypes in `<scratchpad>/s3proto/sdk/` (wire-logging handler, TCP forward proxy, redirect fake). `<scratchpad>` is the session scratchpad directory.
- Modify: `docs/superpowers/specs/2026-11-16-s3-bundle-provider-design.md` — append **Annexe B — résultats de la porte** (French, like the spec), one row per point G1–G12 below, with the command, the raw result and "conforme / contredit".

**Interfaces:**
- Produces, for later tasks (recorded in Annexe B, referenced by row id):
  - G1: the exact settings (`AmazonS3Config` properties or per-request flags) that stop region detection and CSM **for this client only**, or a contradiction (→ stop).
  - G2: the full list of ambient settings read by `AWSSDK.Core`/`AWSSDK.S3` and the setting that neutralizes each.
  - G3: the authority and path for each addressing row of spec §4.4; whether the SDK ever switches addressing on its own; what it does with a dotted bucket.
  - G4: whether the SDK re-calls credentials per signature and synchronously on redirect; the neutral `ClientAppId` value.
  - G5–G12: as listed in the steps.

Run every measurement twice: with `AWSSDK.S3` 4.1.0 (which resolves `AWSSDK.Core` 4.1.0) and with the latest published `AWSSDK.S3`/`AWSSDK.Core` (record exact versions).

- [ ] **Step 1: Set up the two prototype projects**

```bash
S=<scratchpad>/s3gate
mkdir -p $S/min $S/latest
cd $S/min && dotnet new console -f net10.0 && dotnet add package AWSSDK.S3 --version 4.1.0
cd $S/latest && dotnet new console -f net10.0 && dotnet add package AWSSDK.S3 && dotnet add package AWSSDK.Core
dotnet list $S/min package --include-transitive
dotnet list $S/latest package --include-transitive
```

Record the resolved versions in Annexe B header.

- [ ] **Step 2 (G1): every network egress, socket level**

Start TCP listeners on loopback for the configured endpoint and for every host a redirect may name, and resolve those names to loopback by giving the client a `SocketsHttpHandler` whose `ConnectCallback` maps *every* DNS name to `127.0.0.1:<listener port for that name>` (no hosts-file change). Start a UDP listener on a free port. Script responses: 301 + `Location`, 307 + `Location`, 301 `PermanentRedirect` XML with `<Endpoint>`, 400 `AuthorizationHeaderMalformed` with and without `<Region>`, a 200/403 with `x-amz-bucket-region`. Run `GetObject`, `HeadObject` and `ListObjectsV2` with `AWS_CSM_ENABLED=true`, `AWS_CSM_PORT=<udp>` and without. Because `BucketRegionDetector` uses its own transport (`WebRequest`), also run the same scenario with the process's `HttpClient.DefaultProxy` set to a logging proxy listener, to see requests that bypass the client's handler. Record: which transports fire, to which host, `http://` or `https://`, and with which credentials header.

Then try, one at a time, each candidate neutralization **on the client's config only** (not `AWSConfigs.*`, not `RuntimePipelineCustomizerRegistry`): for CSM, any `ClientConfig`/`AmazonS3Config` member or credentials/telemetry setting that disables monitoring for this client; for region detection, the settings that make the SDK never need it (explicit region, regional endpoints, `UseArnRegion=false`, `DisableMultiregionAccessPoints=true`, path-style vs virtual-hosted). Record the minimal set that makes **zero** out-of-band egress in every scenario.

**Stop condition:** if no per-client setting suppresses one of the two transports, write "contredit" in G1 and stop the plan here (spec D3: D1 is reopened).

- [ ] **Step 3 (G2): ambient settings**

Search both assemblies for every environment variable and profile key they read (`Environment.GetEnvironmentVariable` call sites, `FallbackInternalConfigurationFactory`, `CredentialProfile`, `EnvironmentVariableInternalConfiguration`), e.g. with the decompiled IL (`ilspycmd` or reflection over string literals: `[Text.Encoding]::Unicode.GetString(bytes)` then a regex `AWS_[A-Z0-9_]+`). For each, record the `ClientConfig`/`AmazonS3Config` property that overrides it when set explicitly. Then, in a child process with every variable set to a hostile value and `AWS_CONFIG_FILE` pointing to a hostile profile (`endpoint_url`, `sdk_ua_app_id`, `retry_mode`, `max_attempts`, `use_fips_endpoint`, `use_dualstack_endpoint`, `s3_us_east_1_regional_endpoint`, `defaults_mode`, `request_checksum_calculation`), run three requests through a client built with all the overrides and compare URIs, headers (minus `X-Amz-Date`, `amz-sdk-invocation-id`, `amz-sdk-request`) and signatures with an empty-environment run at a fixed clock. Record each difference; each must be closed by an override, or it is a contradiction (stop).

- [ ] **Step 4 (G3): authorities and paths**

With a guard handler that refuses everything and logs `RequestUri`, record the authority and path of `GetObject`, `HeadObject`, `ListObjectsV2` for: `Region` ∈ {`us-east-1`, `eu-west-1`, `cn-north-1`} × {path-style, virtual-hosted} × bucket ∈ {`demo`, `my.dotted.bucket`}; `ServiceUrl` `http://127.0.0.1:9000` × {path-style, virtual-hosted}. Record whether the SDK switches to path-style for a dotted bucket; whether `us-east-1` uses `s3.amazonaws.com` or `s3.us-east-1.amazonaws.com`; `RegionEndpoint.PartitionDnsSuffix` for each region. Then reproduce the bucket-swap: answer a virtual-hosted `GET demo.s3.eu-west-1.amazonaws.com/kb/x` with `307 Location: https://s3.eu-west-1.amazonaws.com/kb/x` and record the second request (expected: same path, bucket now `kb`).

- [ ] **Step 5 (G4): credentials and `ClientAppId`**

Give the client a counting `AWSCredentials` (override `GetCredentials` and, if virtual, `GetCredentialsAsync`); count calls per operation with and without a 307 and with a retried 503. Record which method is called on the redirect path. Then give it a delegating `AWSCredentials` that returns a fixed `ImmutableCredentials` and verify the counts on the host provider stay zero. For `ClientAppId`: set `AWS_SDK_UA_APP_ID=AMBIENTMARK`, then try `ClientAppId = ""` and other candidate values, and record the one for which the `User-Agent` contains no `AMBIENTMARK` and no `app/` token.

- [ ] **Step 6 (G5, G6): guard exception retries; deadlines**

From an inner `DelegatingHandler`, throw a marker exception deriving directly from `Exception` (not `IOException`, not `HttpRequestException`); with `MaxErrorRetry = 2`, record the number of handler calls (expected 1). Then: a 200 `GetObject` whose body stalls after 10 bytes — read the response stream with a token cancelled after 1 s, and separately with `cts.Token.Register(() => response.Dispose())`; record which unblocks the read and the exception type. A 503 storm with a 1 s linked token: record that the retry wait is cut. A credentials provider whose `GetCredentials` blocks: record that `Task.Run(...).WaitAsync(timeout)` returns at the deadline.

- [ ] **Step 7 (G7, G8): checksum failure; bounding before deserialization**

Serve a `GetObject` whose `x-amz-checksum-crc32` does not match: record the exception type and when it is raised (during read, at end, at dispose). In a `DelegatingHandler` below the SDK, read a listing response body to 8 MiB + 1 bytes and return a **new** `HttpResponseMessage` with `ByteArrayContent` on success, or throw the marker on excess; verify the SDK parses the new response normally and that, on excess, it never reaches its unmarshaller (count calls to a stream wrapper on the original content: never read by anyone but the handler). Also verify an `AsyncLocal<T>` value set before `ListObjectsV2Async` is visible inside the handler, including on a retried attempt.

- [ ] **Step 8 (G9): memory**

Load, through the D1 prototype provider modified to buffer and hash bodies, a bundle of 16 objects of 64 MiB each (generated, random bytes in a fenced Markdown body) from RustFS, with and without `Content-Length` (serve without it from a fake), and record peak working set (`Process.PeakWorkingSet64`) and `GC.GetTotalAllocatedBytes`. Compare with the spec's `4B` per read estimate.

- [ ] **Step 9 (G10–G12): RustFS and listing decoding**

From a C# test-like console: detect `podman` (`podman version --format "{{.Server.Version}}"`), else `docker`; start `docker.io/rustfs/rustfs:1.0.3` with `-p 127.0.0.1::9000` and generated `RUSTFS_ACCESS_KEY`/`RUSTFS_SECRET_KEY`; read the port with `<engine> port <id> 9000`; poll `ListBuckets` until ready (≤ 60 s); stop with `<engine> rm -f`. Run once under Podman with no `docker` binary on `PATH` (Windows: temporarily remove it from `PATH` in the child process) and once under Docker on Linux (WSL or a Linux machine). Then upload the hostile names of spec T2 and list them with `EncodingType=url`: record the raw XML `<Key>` values, what the SDK's `S3Object.Key` contains (decoded or not), and the order (strictly increasing by code point?). Compare with a second listing **without** `EncodingType`.

- [ ] **Step 10: Record and decide**

Fill Annexe B (G1–G12 with "conforme" or "contredit"). If any row says "contredit": stop and report to the maintainer with the row. Otherwise commit the annex:

```bash
git add docs/superpowers/specs/2026-11-16-s3-bundle-provider-design.md
git commit -m "docs(s3): gate results, spec appendix B (#142)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 2: Core D15 (a) — the load reads the bundle version before listing

**Files:**
- Modify: `src/OKF4net/Bundle.cs` (`LoadCoreAsync`, the two `try` blocks around `ListNamesAsync` and `GetVersionAsync`, today ~lines 257–300)
- Test: `tests/OKF4net.Tests/Providers/LoadOrderTests.cs`

**Interfaces:**
- Consumes: `InMemoryBundleProvider` (`Add(rawName, text)`, version = hash of names and bytes), `BundleHandle(provider, options, BundleRefreshPolicy.OnAccess)`, `GetCurrentAsync`.
- Produces: no API change.

- [ ] **Step 1: Write the failing test**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>Spec of 2026-11-16 (S3) §4.7 (a): the load reads the bundle token before it lists.</summary>
public class LoadOrderTests
{
    private const string Note = "---\ntype: Note\ntitle: T\ndescription: D.\n---\n\nBody.\n";

    /// <summary>Adds b.md right after the FIRST of (version, listing) completes, whichever the core calls first.</summary>
    private sealed class MutatesBetweenProvider(InMemoryBundleProvider inner) : IBundleProvider
    {
        private int _firstDone;

        public string Id => inner.Id;
        public string Location => inner.Location;
        public BundleProviderCapabilities Capabilities => inner.Capabilities;
        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;

        private void AfterFirst()
        {
            if (Interlocked.Exchange(ref _firstDone, 1) == 0)
            {
                inner.Add("b.md", Note);
            }
        }

        public async ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct)
        {
            var v = await inner.GetVersionAsync(limits, ct);
            AfterFirst();
            return v;
        }

        public async IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            var all = new List<BundleListing>();
            await foreach (var l in inner.EnumerateAsync(prefix, limits, ct))
            {
                all.Add(l);
            }

            if (prefix is null)
            {
                AfterFirst();
            }

            foreach (var l in all)
            {
                yield return l;
            }
        }

        public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) => inner.ReadAsync(key, expected, ct);
        public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => inner.ExistsAsync(key, ct);
        public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) => inner.WriteAsync(key, content, condition, ct);
        public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) => inner.DeleteAsync(key, condition, ct);
    }

    [Fact]
    public async Task An_object_added_between_the_token_and_the_listing_is_never_served_stale_forever()
    {
        var store = new InMemoryBundleProvider().Add("a.md", Note);
        using var handle = new BundleHandle(new MutatesBetweenProvider(store), refreshPolicy: BundleRefreshPolicy.OnAccess);

        _ = await handle.GetCurrentAsync();
        var second = await handle.GetCurrentAsync();

        Assert.Contains(second.Concepts, c => c.Id.ToString() == "b");
    }

    [Fact]
    public async Task A_change_undone_before_the_next_comparison_is_not_reloaded_the_documented_ABA_limit()
    {
        var store = new InMemoryBundleProvider().Add("a.md", Note);
        using var handle = new BundleHandle(store, refreshPolicy: BundleRefreshPolicy.OnAccess);
        var first = await handle.GetCurrentAsync();

        store.Add("a.md", Note.Replace("Body.", "Changed.", StringComparison.Ordinal));
        store.Add("a.md", Note);
        var second = await handle.GetCurrentAsync();

        Assert.Same(first, second); // A -> B -> A between two comparisons: same token, no reload (spec §4.3, limit 1)
    }
}
```

- [ ] **Step 2: Run it and watch the first test fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~LoadOrderTests"`
Expected: `An_object_added_between_the_token_and_the_listing_is_never_served_stale_forever` FAILS (the listing runs first, b.md is added before `GetVersionAsync`, the token already counts b.md, the second access sees the same token and keeps the image without b). The ABA test passes (it documents a limit). If `InMemoryBundleProvider.Add` refuses to replace an existing name, replace the two `Add("a.md", …)` calls with a fresh `store.Add` of the changed text through `WriteAsync(BundleKey.Parse("a.md"), …, BundleWriteCondition.None, default)` on a `new InMemoryBundleProvider(canWrite: true)`.

- [ ] **Step 3: Move the version read before the listing**

In `LoadCoreAsync`, cut the whole `BundleVersion version; try { version = await provider.GetVersionAsync(limits, ct)… } catch …` block and paste it **immediately after** `using var scope = ProviderScopes.Begin(provider);`, before `List<string> treeInput;`. Add above it:

```csharp
        // The token is read BEFORE the listing (spec of 2026-11-16 §4.7 a): a change between the two
        // then gives an image newer than its token, which the next comparison reloads; the opposite
        // order gave an image older than its token, believed fresh until the storage changed again.
```

No other line moves.

- [ ] **Step 4: Run the tests**

Run: `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration&Category!=S3Integration"`
Expected: all pass, `GoldenParityTests` included (no golden changes). If a test that counts provider calls in order fails (e.g. one asserting `["list", "version", …]` on `InMemoryBundleProvider.Calls`), read it: if it pins the *order* of the two calls only, update its expected order and say so in the commit message; if it pins anything else, stop and report.

- [ ] **Step 5: Commit**

```bash
git add src/OKF4net/Bundle.cs tests/OKF4net.Tests/Providers/LoadOrderTests.cs
git commit -m "fix(core): read the bundle token before listing, so a concurrent change is reloaded (#142, spec D15 a)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 3: Core D15 (b) — only a refusal is `Unsafe`; other provider failures surface, translated

**Files:**
- Modify: `src/OKF4net/Bundle.cs` (`ResolveResourceKeyAsync`, the final `catch`, today ~line 1244; its `<summary>`)
- Test: `tests/OKF4net.Tests/Providers/ResolveFailureTests.cs`

**Interfaces:**
- Consumes: `InMemoryBundleProvider.RefuseExistsOf(rawName)` (makes `ExistsAsync` throw `BundleAccessRefusedException`), `BundleValidator.ValidateAsync`, `OkfBundleTools(BundleHandle)`, `ValidateBundleAsync`.
- Produces: `ResolveResourceKeyAsync` throws `BundleLoadException` (message `"provider failure resolving '<key>' (<ExceptionType>)"`, cause as `InnerException`) for any `ExistsAsync` failure other than `BundleAccessRefusedException` and caller cancellation.

- [ ] **Step 1: Write the failing tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Agents;
using OKF4net.Providers;

namespace OKF4net.Tests.Providers;

/// <summary>Spec of 2026-11-16 (S3) §4.7 (b).</summary>
public class ResolveFailureTests
{
    private const string Sentinel = "https://secret.example/bucket-name?X-Amz-Signature=SENTINEL";
    private const string WithResource = "---\ntype: Note\ntitle: T\ndescription: D.\nsources:\n  - refs/q.sql\n---\n\nBody.\n";

    /// <summary>A provider whose ExistsAsync throws what the test gives it.</summary>
    private sealed class FailingExists(InMemoryBundleProvider inner, Exception failure) : IBundleProvider
    {
        public string Id => inner.Id;
        public string Location => inner.Location;
        public BundleProviderCapabilities Capabilities => inner.Capabilities;
        public BundleKeyConstraints NativeConstraints => inner.NativeConstraints;
        public ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct) => inner.GetVersionAsync(limits, ct);
        public IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, CancellationToken ct) => inner.EnumerateAsync(prefix, limits, ct);
        public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) => inner.ReadAsync(key, expected, ct);
        public ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct) => ValueTask.FromException<bool>(failure);
        public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) => inner.WriteAsync(key, content, condition, ct);
        public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) => inner.DeleteAsync(key, condition, ct);
    }

    private sealed class SomeProviderFailure(string m) : BundleProviderException(m);

    public static TheoryData<Exception> Failures => new()
    {
        new IOException(Sentinel),
        new UnauthorizedAccessException(Sentinel),
        new SomeProviderFailure(Sentinel),
    };

    private static InMemoryBundleProvider Store() =>
        new InMemoryBundleProvider().Add("index.md", "# x\n").Add("a.md", WithResource).Add("refs/q.sql", "SELECT 1\n");

    [Fact]
    public async Task A_refusal_is_still_Unsafe()
    {
        var bundle = await Bundle.LoadAsync(Store().RefuseExistsOf("refs/q.sql"));
        var (_, status) = await bundle.ResolveResourceKeyAsync(bundle.Concepts.Single(c => c.Id.ToString() == "a"), "refs/q.sql");
        Assert.Equal(ResourceResolutionStatus.Unsafe, status);
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task Any_other_failure_surfaces_translated_without_the_providers_text(Exception failure)
    {
        var bundle = await Bundle.LoadAsync(new FailingExists(Store(), failure));
        var concept = bundle.Concepts.Single(c => c.Id.ToString() == "a");

        var e = await Assert.ThrowsAsync<BundleLoadException>(async () => await bundle.ResolveResourceKeyAsync(concept, "refs/q.sql"));
        Assert.DoesNotContain("SENTINEL", e.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.example", e.Message, StringComparison.Ordinal);
        Assert.Contains(failure.GetType().Name, e.Message, StringComparison.Ordinal);
        Assert.Same(failure, e.InnerException);
        await Assert.ThrowsAsync<BundleLoadException>(async () => await BundleValidator.ValidateAsync(bundle));
    }

    [Theory]
    [MemberData(nameof(Failures))]
    public async Task The_validation_tool_never_shows_the_providers_text(Exception failure)
    {
        using var handle = new BundleHandle(new FailingExists(Store(), failure));
        var text = await new OkfBundleTools(handle).ValidateBundleAsync();
        Assert.DoesNotContain("SENTINEL", text, StringComparison.Ordinal);
        Assert.DoesNotContain("secret.example", text, StringComparison.Ordinal);
        Assert.DoesNotContain("escapes the bundle", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_cancellation_still_propagates_as_a_cancellation()
    {
        using var cts = new CancellationTokenSource();
        var bundle = await Bundle.LoadAsync(new FailingExists(Store(), new OperationCanceledException(cts.Token)));
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await bundle.ResolveResourceKeyAsync(bundle.Concepts.Single(c => c.Id.ToString() == "a"), "refs/q.sql", cts.Token));
    }
}
```

Note: `Bundle.LoadAsync` itself calls `ResolveResourceKeyAsync` only for §10 concepts (the survey); `a.md` is not one, so these loads succeed before the change and the tests isolate the resolver.

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ResolveFailureTests"`
Expected: `A_refusal_is_still_Unsafe` passes; the three theories FAIL (today the failure reads as `Unsafe`, nothing is thrown, the tool prints "escapes the bundle").

- [ ] **Step 3: Change the catch**

Replace the last `catch` of `ResolveResourceKeyAsync`:

```csharp
        catch (BundleAccessRefusedException)
        {
            return (null, ResourceResolutionStatus.Unsafe);
        }
        catch (Exception e) when (e is BundleProviderException or IOException or UnauthorizedAccessException)
        {
            // Only a refusal of the key's path is Unsafe (§11, permissive). Anything else is the provider
            // failing (a network, a credential, a timeout): it surfaces, translated, so a validation never
            // reports "escapes the bundle" for a store that is down, and no provider text reaches a tool
            // result (spec of 2026-11-16 §4.7 b). The raw failure is the inner exception (host log).
            throw new BundleLoadException($"provider failure resolving '{candidate}' ({e.GetType().Name})", e);
        }
```

Update the method's `<summary>`: replace "Never throws for a provider's refusal (§11, permissive): that reads as <see cref=\"ResourceResolutionStatus.Unsafe\"/>; only a cancellation propagates." with "A provider's refusal of the path reads as <see cref=\"ResourceResolutionStatus.Unsafe\"/> (§11, permissive); any other provider failure throws <see cref=\"BundleLoadException\"/> naming the exception type only, and a cancellation propagates." and add `/// <exception cref="BundleLoadException">The provider failed for another reason than a refusal.</exception>`.

- [ ] **Step 4: Run the whole suite**

Run: `dotnet test OKF4net.sln --filter "Category!=ContainerIntegration&Category!=S3Integration"`
Expected: all pass, goldens included. A pre-existing test that expected `Unsafe` from an `IOException`-throwing `ExistsAsync` is pinning exactly the behaviour this task changes: update it to expect `BundleLoadException` and name it in the commit message. Any other failure: stop and report.

- [ ] **Step 5: Run the producers (they reference the core)**

Run: `dotnet test producers/OkfProducer.sln`
Expected: pass (they only use the filesystem provider).

- [ ] **Step 6: Commit**

```bash
git add src/OKF4net/Bundle.cs tests/OKF4net.Tests/Providers/ResolveFailureTests.cs
git commit -m "fix(core): only a refusal resolves as Unsafe; other provider failures surface, translated (#142, spec D15 b)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 4: Project scaffold, `S3BundleSource`, `Id`/`Location`, dependency guards

**Files:**
- Create: `src/OKF4net.Providers.S3/OKF4net.Providers.S3.csproj`, `S3BundleSource.cs`, `S3ChecksumMode.cs`, `S3Operation.cs`, `S3FailureKind.cs`, `S3RequestException.cs`, `Internal/SourceRules.cs`, `README.md` (stub: title + one paragraph; completed in Task 13)
- Modify: `OKF4net.sln` (`dotnet sln add src/OKF4net.Providers.S3/OKF4net.Providers.S3.csproj --solution-folder src`), `tests/OKF4net.Tests/OKF4net.Tests.csproj` (add `<ProjectReference Include="..\..\src\OKF4net.Providers.S3\OKF4net.Providers.S3.csproj" />` after the EmbeddedResources one)
- Test: `tests/OKF4net.Tests/Providers/S3/SourceTests.cs`, `tests/OKF4net.Tests/Providers/S3/DependencyGuardTests.cs`

**Interfaces:**
- Produces:
  - `public sealed record S3BundleSource { required string Bucket; string Prefix = ""; string? DisplayName; RegionEndpoint? Region; Uri? ServiceUrl; bool ForcePathStyle; bool AllowInsecureHttp; TimeSpan RequestTimeout = 30 s; int MaxConcurrentRequests = 16; long MaxObjectBytes = BundleLimits.Default.MaxObjectBytes; S3ChecksumMode Checksums = WhenSupported; }`
  - `public enum S3ChecksumMode { WhenSupported, WhenRequired }`, `public enum S3Operation { List, Read, Exists }`, `public enum S3FailureKind { Timeout, Throttled, ServerError, ClientError, Transport, RefusedHost, InvalidResponse, Credentials, BucketUnavailable, Integrity, Disposed }`
  - `public sealed class S3RequestException : BundleProviderException { S3Operation Operation; S3FailureKind Kind; int? StatusCode; }` with `internal S3RequestException(S3Operation op, S3FailureKind kind, int? status, BundleKey? key, Exception? inner)`
  - `internal sealed record NormalizedSource(string Bucket, string Prefix, string? DisplayName, RegionEndpoint? Region, Uri? ServiceUrl, bool PathStyle, TimeSpan RequestTimeout, int MaxConcurrentRequests, long MaxObjectBytes, S3ChecksumMode Checksums, string Id, string Location, string EndpointIdentity)`
  - `internal static class SourceRules { static NormalizedSource Normalize(S3BundleSource source); }` (throws `ArgumentException`)

- [ ] **Step 1: Write the csproj**

```xml
<!-- SPDX-License-Identifier: LGPL-3.0-or-later -->
<Project Sdk="Microsoft.NET.Sdk">

  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <GenerateDocumentationFile>true</GenerateDocumentationFile>
    <IsAotCompatible>true</IsAotCompatible>
  </PropertyGroup>

  <PropertyGroup Label="Packaging">
    <PackageId>OKF4net.Providers.S3</PackageId>
    <Authors>Julien CHABLE</Authors>
    <Description>Serve an OKF knowledge bundle from Amazon S3 or an S3-compatible store: a read-only, asynchronous IBundleProvider over a bucket and prefix, with a mandatory request guard, host-owned credentials and content-hash versions.</Description>
    <Copyright>Copyright 2026 Julien CHABLE</Copyright>
    <PackageLicenseExpression>LGPL-3.0-or-later</PackageLicenseExpression>
    <PackageReadmeFile>README.md</PackageReadmeFile>
    <PackageProjectUrl>https://github.com/jchable/okf4net</PackageProjectUrl>
    <RepositoryUrl>https://github.com/jchable/okf4net</RepositoryUrl>
    <RepositoryType>git</RepositoryType>
    <PackageTags>okf;knowledge;s3;aws;object-storage;knowledge-base</PackageTags>
    <PublishRepositoryUrl>true</PublishRepositoryUrl>
    <EmbedUntrackedSources>true</EmbedUntrackedSources>
    <IncludeSymbols>true</IncludeSymbols>
    <SymbolPackageFormat>snupkg</SymbolPackageFormat>
  </PropertyGroup>

  <ItemGroup>
    <None Include="README.md" Pack="true" PackagePath="\" />
    <None Include="..\..\NOTICE" Pack="true" PackagePath="\" />
  </ItemGroup>

  <ItemGroup>
    <!-- The one dependency-policy exception of this project (CLAUDE.md, spec D1): a MINIMUM
         version. Every bump of AWSSDK.S3 or AWSSDK.Core needs the local S3Integration run at
         0 skipped and the s3-aot CI job green. -->
    <PackageReference Include="AWSSDK.S3" Version="4.1.0" />
  </ItemGroup>

  <ItemGroup>
    <ProjectReference Include="..\OKF4net\OKF4net.csproj" />
  </ItemGroup>

  <ItemGroup>
    <InternalsVisibleTo Include="OKF4net.Tests" />
  </ItemGroup>

</Project>
```

- [ ] **Step 2: Write the failing tests**

`tests/OKF4net.Tests/Providers/S3/SourceTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec D5, D6, §4.3 (Id, Location).</summary>
public class SourceTests
{
    private static S3BundleSource Local(string bucket = "okf", string prefix = "kb") =>
        new() { Bucket = bucket, Prefix = prefix, ServiceUrl = new Uri("http://127.0.0.1:9000"), ForcePathStyle = true, AllowInsecureHttp = true };

    public static TheoryData<S3BundleSource> Invalid => new()
    {
        Local(prefix: "/kb"), Local(prefix: "kb/"), Local(prefix: "a//b"), Local(prefix: ".."), Local(prefix: "a/./b"),
        Local(prefix: "kb\uD800"),
        Local() with { DisplayName = "a/b" }, Local() with { DisplayName = "a:b" }, Local() with { DisplayName = "" },
        Local(bucket: "arn:aws:s3:eu-west-1:123:accesspoint/x"), Local(bucket: "Upper"), Local(bucket: "ab"),
        Local(bucket: "my-bucket--x-s3"), Local(bucket: "my-bucket--ol-s3"), Local(bucket: "my-bucket-s3alias"), Local(bucket: "a..b"), Local(bucket: "192.168.0.1"),
        Local() with { ServiceUrl = new Uri("http://user:pw@127.0.0.1:9000") },
        Local() with { ServiceUrl = new Uri("http://127.0.0.1:9000/gateway") },
        Local() with { ServiceUrl = new Uri("http://127.0.0.1:9000/?x=1") },
        Local() with { ServiceUrl = new Uri("http://127.0.0.1:9000/#f") },
        Local() with { AllowInsecureHttp = false },
        Local() with { Region = RegionEndpoint.EUWest1 },
        Local() with { ServiceUrl = null },
        Local() with { RequestTimeout = TimeSpan.Zero }, Local() with { MaxConcurrentRequests = 0 },
        Local() with { MaxObjectBytes = 0 }, Local() with { MaxObjectBytes = (long)Array.MaxLength + 1 },
        new S3BundleSource { Bucket = "my.dotted.bucket", Region = RegionEndpoint.EUWest1 }, // virtual-hosted + dots: refused (spec §4.4)
        new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.CNNorth1 }, // partition outside amazonaws.com: refused in v1 (gate G3)
    };

    [Theory]
    [MemberData(nameof(Invalid))]
    public void An_invalid_source_is_refused(S3BundleSource source) =>
        Assert.Throws<ArgumentException>(() => SourceRules.Normalize(source));

    [Fact]
    public void The_id_is_opaque_stable_and_follows_the_effective_configuration()
    {
        var a = SourceRules.Normalize(Local());
        Assert.Matches("^s3:[0-9a-f]{32}$", a.Id);
        Assert.Equal(a.Id, SourceRules.Normalize(Local()).Id);
        Assert.Equal(a.Id, SourceRules.Normalize(Local() with { ServiceUrl = new Uri("http://127.0.0.1:9000/") }).Id);
        Assert.NotEqual(a.Id, SourceRules.Normalize(Local(prefix: "kb2")).Id);
        Assert.NotEqual(a.Id, SourceRules.Normalize(Local(bucket: "okf2")).Id);
        Assert.NotEqual(a.Id, SourceRules.Normalize(Local() with { ServiceUrl = new Uri("http://127.0.0.1:9001") }).Id);
        Assert.NotEqual(a.Id, SourceRules.Normalize(Local() with { ForcePathStyle = false }).Id);
        Assert.DoesNotContain("okf", a.Id[3..], StringComparison.Ordinal);
    }

    [Fact]
    public void The_location_never_names_the_bucket()
    {
        var plain = SourceRules.Normalize(Local(bucket: "secret-bucket"));
        Assert.Equal("s3:" + plain.Id[3..11] + "/kb", plain.Location);
        Assert.Equal("s3:docs/kb", SourceRules.Normalize(Local(bucket: "secret-bucket") with { DisplayName = "docs" }).Location);
        Assert.Equal("s3:docs", SourceRules.Normalize(Local(bucket: "secret-bucket", prefix: "") with { DisplayName = "docs" }).Location);
        Assert.DoesNotContain("secret-bucket", plain.Location, StringComparison.Ordinal);
    }

    [Fact]
    public void A_prefix_holding_U_FFFD_is_valid_and_distinct_from_any_other() =>
        Assert.NotEqual(SourceRules.Normalize(Local(prefix: "kb�")).Id, SourceRules.Normalize(Local(prefix: "kb")).Id);
}
```

`tests/OKF4net.Tests/Providers/S3/DependencyGuardTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Reflection;
using OKF4net.Providers.S3;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec D1, D10; #141 rule 1.</summary>
public class DependencyGuardTests
{
    [Fact]
    public void The_core_grants_this_package_no_internals()
    {
        // A package that calls an internal of the core breaks at run time (MissingMethodException) when a host
        // pairs it with another version of the core, which NuGet's minimum-version dependency allows.
        var granted = typeof(Bundle).Assembly.GetCustomAttributes<System.Runtime.CompilerServices.InternalsVisibleToAttribute>()
            .Select(a => a.AssemblyName);
        Assert.DoesNotContain(granted, name => name.StartsWith("OKF4net.Providers.S3", StringComparison.Ordinal));
    }

    [Fact]
    public void The_assembly_references_only_the_core_the_aws_sdk_and_the_bcl()
    {
        var names = typeof(S3BundleSource).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToArray();
        var allowed = names.Where(n => n == "OKF4net" || n.StartsWith("AWSSDK.", StringComparison.Ordinal)
            || n.StartsWith("System", StringComparison.Ordinal) || n == "netstandard" || n == "Microsoft.Win32.Primitives");
        Assert.Equal(names.Order(), allowed.Order());
    }
}
```

- [ ] **Step 3: Run them and watch them fail to compile**

Run: `dotnet build OKF4net.sln`
Expected: FAIL — `S3BundleSource`, `SourceRules` do not exist.

- [ ] **Step 4: Write the public types**

`S3ChecksumMode.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3;

/// <summary>How the SDK computes and validates checksums (spec D12); never read from the environment.</summary>
public enum S3ChecksumMode
{
    /// <summary>The SDK default: checksums on every operation that supports them (accepted by RustFS, measured).</summary>
    WhenSupported,

    /// <summary>Only where the operation requires one: for a compatible store that refuses the default.</summary>
    WhenRequired,
}
```

`S3Operation.cs` and `S3FailureKind.cs` (one file each, same header, every member documented):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3;

/// <summary>The provider operation that failed.</summary>
public enum S3Operation
{
    /// <summary>A listing page (EnumerateAsync, GetVersionAsync).</summary>
    List,

    /// <summary>An object read (ReadAsync).</summary>
    Read,

    /// <summary>An existence check (ExistsAsync).</summary>
    Exists,
}
```

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3;

/// <summary>Why an S3 request failed (spec §4.6). Compare by value; members are appended, never inserted.</summary>
public enum S3FailureKind
{
    /// <summary>The operation's deadline passed.</summary>
    Timeout,
    /// <summary>The store asked to slow down (503 SlowDown, 429).</summary>
    Throttled,
    /// <summary>Another 5xx.</summary>
    ServerError,
    /// <summary>Another 4xx.</summary>
    ClientError,
    /// <summary>The connection or the SDK failed before or while reading a response.</summary>
    Transport,
    /// <summary>The request guard refused a request outside this source (spec D3).</summary>
    RefusedHost,
    /// <summary>A response the provider will not trust (transport bounds, listing invariants, an unrecognized status).</summary>
    InvalidResponse,
    /// <summary>The credentials could not be obtained, or the store rejected them.</summary>
    Credentials,
    /// <summary>The bucket is absent or served from another region.</summary>
    BucketUnavailable,
    /// <summary>A response checksum did not match.</summary>
    Integrity,
    /// <summary>The provider was disposed.</summary>
    Disposed,
}
```

`S3RequestException.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;

namespace OKF4net.Providers.S3;

/// <summary>
/// An S3 request failed (spec D7, §4.6). The message is library-authored from a closed vocabulary — operation,
/// kind, HTTP status, bundle key — and never carries a bucket, an endpoint, a request id, a URL, the S3 error code
/// or any other text from the server; the raw failure is the <see cref="Exception.InnerException"/>, for the host's
/// log only (#139, §3). Being a <see cref="BundleProviderException"/>, it fails a load rather than being recorded as
/// a local state of one object.
/// </summary>
public sealed class S3RequestException : BundleProviderException
{
    internal S3RequestException(S3Operation operation, S3FailureKind kind, int? statusCode, BundleKey? key)
        : base(Describe(operation, kind, statusCode, key))
    {
        Operation = operation;
        Kind = kind;
        StatusCode = statusCode;
    }

    internal S3RequestException(S3Operation operation, S3FailureKind kind, int? statusCode, BundleKey? key, Exception inner)
        : base(Describe(operation, kind, statusCode, key), inner)
    {
        Operation = operation;
        Kind = kind;
        StatusCode = statusCode;
    }

    /// <summary>The operation that failed.</summary>
    public S3Operation Operation { get; }

    /// <summary>Why it failed.</summary>
    public S3FailureKind Kind { get; }

    /// <summary>The HTTP status, when a response was received.</summary>
    public int? StatusCode { get; }

    private static string Describe(S3Operation operation, S3FailureKind kind, int? status, BundleKey? key)
    {
        var op = operation switch { S3Operation.List => "list", S3Operation.Read => "read", _ => "exists" };
        var text = $"S3 {op} failed: {kind}";
        if (status is { } s)
        {
            text += string.Create(CultureInfo.InvariantCulture, $" (HTTP {s})");
        }

        return key is null ? text : text + $" for '{key}'";
    }
}
```

`S3BundleSource.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon;

namespace OKF4net.Providers.S3;

/// <summary>
/// Where a bundle lives in S3 or an S3-compatible store, and the provider's own bounds (spec §4.2). Trusted
/// operator configuration (#139, §1); no credential goes here — the host passes an <c>AWSCredentials</c> to the
/// provider's constructor. Exactly one of <see cref="Region"/> (Amazon S3) and <see cref="ServiceUrl"/> (a
/// compatible store's origin) is set.
/// </summary>
public sealed record S3BundleSource
{
    /// <summary>A general-purpose bucket name (3–63 characters: lowercase letters, digits, '-', '.'); access points, ARNs, aliases and directory buckets are refused.</summary>
    public required string Bucket { get; init; }

    /// <summary>The bundle's key prefix: empty (the whole bucket) or '/'-separated segments that are valid bundle key segments, in well-formed Unicode, with no leading or trailing '/'.</summary>
    public string Prefix { get; init; } = "";

    /// <summary>The name shown in <see cref="IBundleProvider.Location"/> instead of the bucket, which never reaches a model (spec D5). One valid key segment, without ':'. When absent, 8 hex digits of the provider id.</summary>
    public string? DisplayName { get; init; }

    /// <summary>The Amazon S3 region; exclusive of <see cref="ServiceUrl"/>.</summary>
    public RegionEndpoint? Region { get; init; }

    /// <summary>A compatible store's origin (scheme, host, port; no user info, path, query or fragment); exclusive of <see cref="Region"/>.</summary>
    public Uri? ServiceUrl { get; init; }

    /// <summary>Path-style addressing (<c>host/bucket/key</c>) instead of virtual-hosted (<c>bucket.host/key</c>); most compatible stores need it.</summary>
    public bool ForcePathStyle { get; init; }

    /// <summary>Allows an <c>http://</c> <see cref="ServiceUrl"/> (a local store, tests). HTTPS is required otherwise (spec D11).</summary>
    public bool AllowInsecureHttp { get; init; }

    /// <summary>The deadline of one provider operation, from the call to the end of the body, retries and credential resolution included (spec §4.5).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Operations in flight at once, bodies included (spec D16).</summary>
    public int MaxConcurrentRequests { get; init; } = 16;

    /// <summary>The largest object read, in bytes (1 to <see cref="Array.MaxLength"/>); a read buffers the whole object (spec D8).</summary>
    public long MaxObjectBytes { get; init; } = BundleLimits.Default.MaxObjectBytes;

    /// <summary>Checksum behaviour (spec D12).</summary>
    public S3ChecksumMode Checksums { get; init; } = S3ChecksumMode.WhenSupported;
}
```

- [ ] **Step 5: Write `SourceRules`**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Amazon;

namespace OKF4net.Providers.S3.Internal;

/// <summary>The validated, normalized form of an <see cref="S3BundleSource"/>, with its identity (spec D5, D6, §4.3).</summary>
internal sealed record NormalizedSource(
    string Bucket, string Prefix, string? DisplayName, RegionEndpoint? Region, Uri? ServiceUrl, bool PathStyle,
    TimeSpan RequestTimeout, int MaxConcurrentRequests, long MaxObjectBytes, S3ChecksumMode Checksums,
    string Id, string Location, string EndpointIdentity);

internal static partial class SourceRules
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    [GeneratedRegex("^[a-z0-9][a-z0-9.-]{1,61}[a-z0-9]$", RegexOptions.CultureInvariant)]
    private static partial Regex BucketShape();

    [GeneratedRegex(@"^\d{1,3}(\.\d{1,3}){3}$", RegexOptions.CultureInvariant)]
    private static partial Regex IpShape();

    private static readonly string[] RefusedSuffixes = ["--x-s3", "--ol-s3", "-s3alias", "--table-s3"];
    private static readonly string[] RefusedPrefixes = ["xn--", "sthree-", "amzn-s3-demo-"];

    public static NormalizedSource Normalize(S3BundleSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var bucket = source.Bucket ?? throw new ArgumentException("A bucket is required.", nameof(source));
        if (!BucketShape().IsMatch(bucket) || bucket.Contains("..", StringComparison.Ordinal) || IpShape().IsMatch(bucket)
            || RefusedSuffixes.Any(s => bucket.EndsWith(s, StringComparison.Ordinal)) || RefusedPrefixes.Any(p => bucket.StartsWith(p, StringComparison.Ordinal)))
        {
            throw new ArgumentException("The bucket is not a general-purpose S3 bucket name.", nameof(source));
        }

        var prefix = source.Prefix ?? "";
        if (prefix.Length > 0 && (!BundleKey.TryParse(prefix, out _, out _) || !IsWellFormed(prefix)))
        {
            throw new ArgumentException("The prefix must be empty or valid bundle key segments in well-formed Unicode.", nameof(source));
        }

        if (source.DisplayName is { } display && (display.Length == 0 || display.Contains('/', StringComparison.Ordinal)
            || display.Contains(':', StringComparison.Ordinal) || !BundleKey.TryParse(display, out _, out _) || !IsWellFormed(display)))
        {
            throw new ArgumentException("The display name must be one bundle key segment, without ':'.", nameof(source));
        }

        if ((source.Region is null) == (source.ServiceUrl is null))
        {
            throw new ArgumentException("Set exactly one of Region and ServiceUrl.", nameof(source));
        }

        if (source.RequestTimeout <= TimeSpan.Zero || source.MaxConcurrentRequests <= 0
            || source.MaxObjectBytes <= 0 || source.MaxObjectBytes > Array.MaxLength)
        {
            throw new ArgumentException("Timeouts and bounds must be positive (MaxObjectBytes at most Array.MaxLength).", nameof(source));
        }

        string endpoint;
        Uri? serviceUrl = null;
        if (source.ServiceUrl is { } url)
        {
            if (!url.IsAbsoluteUri || (url.Scheme != Uri.UriSchemeHttps && url.Scheme != Uri.UriSchemeHttp)
                || url.UserInfo.Length > 0 || url.AbsolutePath != "/" || url.Query.Length > 0 || url.Fragment.Length > 0)
            {
                throw new ArgumentException("ServiceUrl must be an http(s) origin only: no user info, path, query or fragment.", nameof(source));
            }

            if (url.Scheme == Uri.UriSchemeHttp && !source.AllowInsecureHttp)
            {
                throw new ArgumentException("An http:// ServiceUrl needs AllowInsecureHttp.", nameof(source));
            }

            serviceUrl = new Uri(string.Create(CultureInfo.InvariantCulture, $"{url.Scheme}://{url.Host.ToLowerInvariant()}:{url.Port}/"));
            endpoint = string.Create(CultureInfo.InvariantCulture, $"{url.Scheme}://{url.Host.ToLowerInvariant()}:{url.Port}");
        }
        else
        {
            var region = source.Region!;
            if (region.PartitionDnsSuffix != "amazonaws.com")
            {
                throw new ArgumentException("Only regions of the amazonaws.com partition are supported.", nameof(source));
            }

            if (!source.ForcePathStyle && bucket.Contains('.', StringComparison.Ordinal))
            {
                throw new ArgumentException("A bucket name with '.' needs ForcePathStyle on Amazon S3 (TLS would not match a virtual-hosted name).", nameof(source));
            }

            endpoint = "aws:" + region.SystemName;
        }

        endpoint += source.ForcePathStyle ? "|path" : "|vhost";
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes("s3v1\n" + endpoint + "\n" + bucket + "\n" + prefix)));
        var id = "s3:" + hash[..32];
        var location = "s3:" + (source.DisplayName ?? hash[..8]) + (prefix.Length == 0 ? "" : "/" + prefix);
        return new NormalizedSource(bucket, prefix, source.DisplayName, source.Region, serviceUrl, source.ForcePathStyle,
            source.RequestTimeout, source.MaxConcurrentRequests, source.MaxObjectBytes, source.Checksums, id, location, endpoint);
    }

    internal static bool IsWellFormed(string text)
    {
        try
        {
            _ = StrictUtf8.GetByteCount(text);
            return true;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }
}
```

If gate G3 recorded that the SDK never switches addressing for dotted buckets and that dotted buckets work virtual-hosted, keep the refusal anyway (spec §4.4: refused at construction, simplest safe rule). If G3 recorded a partition-suffix API name other than `PartitionDnsSuffix`, use G3's.

- [ ] **Step 6: Run the tests**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Providers.S3"`
Expected: PASS.

- [ ] **Step 7: Full checks and commit**

```bash
dotnet build OKF4net.sln && dotnet test OKF4net.sln --filter "Category!=ContainerIntegration&Category!=S3Integration" && dotnet format OKF4net.sln --verify-no-changes
git add OKF4net.sln src/OKF4net.Providers.S3 tests/OKF4net.Tests/OKF4net.Tests.csproj tests/OKF4net.Tests/Providers/S3
git commit -m "feat(s3): project, S3BundleSource validation, opaque Id and bucket-free Location (#142)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 5: Transport — guard, bounds, provider-owned `HttpClient`, hermetic client

**Files:**
- Create: `src/OKF4net.Providers.S3/Internal/AddressingPlan.cs`, `OperationContext.cs`, `GuardException.cs`, `PinningHandler.cs`, `BoundingHandler.cs`, `ProviderHttpClientFactory.cs`, `ClientBuilder.cs`
- Create (tests): `tests/OKF4net.Tests/Providers/S3/FakeS3Handler.cs`, `tests/OKF4net.Tests/Providers/S3/GuardTests.cs`, `tests/OKF4net.Tests/Providers/S3/TransportBoundsTests.cs`

**Interfaces:**
- Consumes: `NormalizedSource` (Task 4); gate rows G1, G2, G3, G4 (`ClientAppId` value), G5, G7.
- Produces:
  - `internal sealed class AddressingPlan { static AddressingPlan For(NormalizedSource s); string Scheme; string Host; int Port; string PathPrefix /* "/bucket/" or "/" */; string S3Key(string bundleKeyOrEmpty); string ListPrefix(string? bundleKeyPrefix); string ObjectPath(string s3Key) /* decoded */; }`
  - `internal sealed class OperationContext { static AsyncLocal<OperationContext?> Current; S3Operation Operation; string? ExpectedObjectS3Key; string? ExpectedListPrefix; ImmutableCredentials? Credentials; ListPageCapture? Capture; }`, `internal sealed class ListPageCapture { byte[]? Body; }`
  - `internal sealed class GuardException(S3FailureKind kind) : Exception` (derives from `Exception` only — G5: not retried)
  - `internal sealed class PinningHandler(AddressingPlan plan) : DelegatingHandler`
  - `internal sealed class BoundingHandler : DelegatingHandler` with `public const int ListBodyBytes = 8 * 1024 * 1024, ErrorBodyBytes = 64 * 1024, TokenBytes = 1024;`
  - `internal sealed class ProviderHttpClientFactory(HttpClient client) : Amazon.Runtime.HttpClientFactory`
  - `internal static class ClientBuilder { static AmazonS3Client Build(AWSCredentials credentials, NormalizedSource source, HttpMessageHandler network, out HttpClient httpClient); }`
  - Test helper `FakeS3Handler : HttpMessageHandler` with `Func<HttpRequestMessage, HttpResponseMessage> Respond`, `List<HttpRequestMessage> Requests`, `int Count(string host)`.

- [ ] **Step 1: Write the fake**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using System.Text;

namespace OKF4net.Tests.Providers.S3;

/// <summary>A scripted S3 endpoint under the provider's guard (spec T1): records every request it receives.</summary>
internal sealed class FakeS3Handler : HttpMessageHandler
{
    private readonly object _gate = new();

    public Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond { get; set; } =
        (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));

    public List<HttpRequestMessage> Requests { get; } = [];

    public int Count(string host)
    {
        lock (_gate)
        {
            return Requests.Count(r => string.Equals(r.RequestUri!.Host, host, StringComparison.OrdinalIgnoreCase));
        }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        lock (_gate)
        {
            Requests.Add(request);
        }

        var response = await Respond(request, ct);
        response.RequestMessage ??= request;
        return response;
    }

    public static HttpResponseMessage Xml(HttpStatusCode status, string xml, Action<HttpResponseMessage>? tweak = null)
    {
        var r = new HttpResponseMessage(status) { Content = new ByteArrayContent(Encoding.UTF8.GetBytes(xml)) };
        r.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/xml");
        tweak?.Invoke(r);
        return r;
    }

    public static HttpResponseMessage Redirect(HttpStatusCode status, string location)
    {
        var r = new HttpResponseMessage(status);
        r.Headers.Location = new Uri(location);
        return r;
    }

    public static string Error(string code) =>
        $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Error><Code>{code}</Code><Message>m</Message><RequestId>r</RequestId><HostId>h</HostId></Error>";
}
```

- [ ] **Step 2: Write the failing guard and bound tests**

These tests drive the provider's transport through a minimal `AmazonS3Client` built by `ClientBuilder`, with an `OperationContext` set by hand, so they do not depend on Task 6–8. `GuardTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec D3, §4.4: one authority, one path prefix, refused before sending; counter-proofs without the guard.</summary>
public class GuardTests
{
    private static readonly ImmutableCredentials Creds = new("AKIDEXAMPLE", "secret", "TOKEN-CANARY");

    private static (AmazonS3Client Client, FakeS3Handler Fake) Build(S3BundleSource source, bool guarded = true)
    {
        var fake = new FakeS3Handler();
        var normalized = SourceRules.Normalize(source);
        var client = guarded
            ? ClientBuilder.Build(new SnapshotCredentials(), normalized, fake, out _)
            : ClientBuilder.BuildUnguardedForCounterProof(new SnapshotCredentials(), normalized, fake);
        return (client, fake);
    }

    private static async Task<Exception?> Get(AmazonS3Client client, string bucket, string s3Key)
    {
        OperationContext.Current.Value = new OperationContext(S3Operation.Read) { ExpectedObjectS3Key = s3Key, Credentials = Creds };
        try
        {
            using var r = await client.GetObjectAsync(new GetObjectRequest { BucketName = bucket, Key = s3Key });
            return null;
        }
        catch (Exception e)
        {
            return e;
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    public static TheoryData<S3BundleSource, string> TwoAuthorities => new()
    {
        { new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 }, "https://s3.eu-west-1.amazonaws.com/kb/x" },
        { new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1, ForcePathStyle = true }, "https://demo.s3.eu-west-1.amazonaws.com/kb/x" },
    };

    [Theory]
    [MemberData(nameof(TwoAuthorities))]
    public async Task A_307_between_the_two_authorities_of_one_bucket_is_refused_both_ways(S3BundleSource source, string target)
    {
        var (client, fake) = Build(source);
        fake.Respond = (req, _) => Task.FromResult(req.RequestUri!.ToString() == target
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("FROM-TARGET") }
            : FakeS3Handler.Redirect(HttpStatusCode.TemporaryRedirect, target));

        var e = await Get(client, "demo", "kb/x");

        var guard = Assert.IsType<GuardException>(e is GuardException ? e : e?.InnerException ?? e);
        Assert.Equal(S3FailureKind.RefusedHost, guard.Kind);
        Assert.Single(fake.Requests); // the original only; the redirected request never reached the network handler
    }

    [Theory]
    [MemberData(nameof(TwoAuthorities))]
    public async Task Counter_proof_without_the_guard_the_same_307_reaches_the_other_bucket(S3BundleSource source, string target)
    {
        var (client, fake) = Build(source, guarded: false);
        fake.Respond = (req, _) => Task.FromResult(req.RequestUri!.ToString() == target
            ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("FROM-TARGET") }
            : FakeS3Handler.Redirect(HttpStatusCode.TemporaryRedirect, target));

        Assert.Null(await Get(client, "demo", "kb/x"));
        Assert.Equal(2, fake.Requests.Count);
        Assert.Equal(target, fake.Requests[1].RequestUri!.ToString());
    }

    [Fact]
    public async Task A_307_to_another_bucket_host_is_refused_and_its_counter_proof_follows()
    {
        var source = new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 };
        const string target = "https://evil.s3.eu-west-1.amazonaws.com/kb/x";
        foreach (var guarded in new[] { true, false })
        {
            var (client, fake) = Build(source, guarded);
            fake.Respond = (req, _) => Task.FromResult(req.RequestUri!.Host.StartsWith("evil.", StringComparison.Ordinal)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("X") }
                : FakeS3Handler.Redirect(HttpStatusCode.TemporaryRedirect, target));
            var e = await Get(client, "demo", "kb/x");
            Assert.Equal(guarded ? 0 : 1, fake.Count("evil.s3.eu-west-1.amazonaws.com"));
            Assert.Equal(guarded, e is not null);
        }
    }

    [Fact]
    public async Task An_altered_key_on_the_right_authority_is_refused()
    {
        var (client, fake) = Build(new S3BundleSource { Bucket = "okf", ServiceUrl = new Uri("http://127.0.0.1:9000"), ForcePathStyle = true, AllowInsecureHttp = true });
        OperationContext.Current.Value = new OperationContext(S3Operation.Read) { ExpectedObjectS3Key = "kb/a.md", Credentials = Creds };
        try
        {
            await Assert.ThrowsAnyAsync<Exception>(() => client.GetObjectAsync(new GetObjectRequest { BucketName = "okf", Key = "kb/b.md" }));
        }
        finally
        {
            OperationContext.Current.Value = null;
        }

        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task A_request_outside_any_operation_is_refused() =>
        await Assert.ThrowsAnyAsync<Exception>(() => Build(new S3BundleSource { Bucket = "okf", ServiceUrl = new Uri("http://127.0.0.1:9000"), ForcePathStyle = true, AllowInsecureHttp = true })
            .Client.GetObjectAsync(new GetObjectRequest { BucketName = "okf", Key = "kb/a.md" }));

    public static TheoryData<S3BundleSource, string, string> Table => new()
    {
        { new S3BundleSource { Bucket = "okf", ServiceUrl = new Uri("http://127.0.0.1:9000"), ForcePathStyle = true, AllowInsecureHttp = true }, "127.0.0.1:9000", "/okf/" },
        { new S3BundleSource { Bucket = "okf", ServiceUrl = new Uri("http://127.0.0.1:9000"), AllowInsecureHttp = true }, "okf.127.0.0.1:9000", "/" },
        { new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1, ForcePathStyle = true }, "s3.eu-west-1.amazonaws.com:443", "/demo/" },
        { new S3BundleSource { Bucket = "demo", Region = RegionEndpoint.EUWest1 }, "demo.s3.eu-west-1.amazonaws.com:443", "/" },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public void The_plan_admits_exactly_one_authority_and_one_path_prefix(S3BundleSource source, string authority, string pathPrefix)
    {
        var plan = AddressingPlan.For(SourceRules.Normalize(source));
        Assert.Equal(authority, $"{plan.Host}:{plan.Port}");
        Assert.Equal(pathPrefix, plan.PathPrefix);
    }

    [Fact]
    public void Two_providers_share_no_HttpClient()
    {
        var s = SourceRules.Normalize(new S3BundleSource { Bucket = "okf", ServiceUrl = new Uri("http://127.0.0.1:9000"), ForcePathStyle = true, AllowInsecureHttp = true });
        _ = ClientBuilder.Build(new SnapshotCredentials(), s, new FakeS3Handler(), out var a);
        _ = ClientBuilder.Build(new SnapshotCredentials(), s, new FakeS3Handler(), out var b);
        Assert.NotSame(a, b);
    }
}
```

(`SnapshotCredentials` is created in this task as a minimal internal `AWSCredentials` returning `OperationContext.Current.Value?.Credentials ?? throw new GuardException(S3FailureKind.Credentials)`; Task 6 completes it with the isolated resolution.) Note on the virtual-hosted `ServiceUrl` row: its authority is `okf.127.0.0.1` — G3 records what the SDK actually sends for an IP origin without path-style; if the SDK forces path-style there, refuse that configuration in `SourceRules` instead and drop the row (record it in the commit message).

`TransportBoundsTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec §4.6, bounds applied before deserialization.</summary>
public class TransportBoundsTests
{
    /// <summary>A content whose stream counts the bytes read from it and who reads it.</summary>
    private sealed class WitnessContent(byte[] body) : HttpContent
    {
        public long BytesRead;
        public int Readers;

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await stream.WriteAsync(body);

        protected override Task<Stream> CreateContentReadStreamAsync()
        {
            Interlocked.Increment(ref Readers);
            return Task.FromResult<Stream>(new CountingStream(new MemoryStream(body), n => Interlocked.Add(ref BytesRead, n)));
        }

        protected override bool TryComputeLength(out long length)
        {
            length = body.Length;
            return false; // no Content-Length unless the test sets it
        }
    }

    private sealed class CountingStream(Stream inner, Action<int> onRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { var n = inner.Read(buffer, offset, count); onRead(n); return n; }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { var n = await inner.ReadAsync(buffer, ct); onRead(n); return n; }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static async Task<(HttpResponseMessage? Response, Exception? Error, WitnessContent Witness)> Send(HttpStatusCode status, int size, bool list, bool withLength)
    {
        var witness = new WitnessContent(new byte[size]);
        var inner = new FakeS3Handler
        {
            Respond = (_, _) =>
            {
                var r = new HttpResponseMessage(status) { Content = witness };
                if (withLength) r.Content.Headers.ContentLength = size;
                return Task.FromResult(r);
            },
        };
        var bounding = new BoundingHandler { InnerHandler = inner };
        using var invoker = new HttpMessageInvoker(bounding);
        var uri = list ? "http://127.0.0.1:9000/okf/?list-type=2&prefix=kb%2F" : "http://127.0.0.1:9000/okf/kb/a.md";
        OperationContext.Current.Value = new OperationContext(list ? S3Operation.List : S3Operation.Read) { Capture = list ? new ListPageCapture() : null };
        try
        {
            return (await invoker.SendAsync(new HttpRequestMessage(HttpMethod.Get, uri), default), null, witness);
        }
        catch (Exception e)
        {
            return (null, e, witness);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task A_listing_at_the_bound_passes_on_a_new_content(bool withLength)
    {
        var (r, e, w) = await Send(HttpStatusCode.OK, BoundingHandler.ListBodyBytes, list: true, withLength);
        Assert.Null(e);
        Assert.IsNotType<WitnessContent>(r!.Content);
        Assert.Equal(BoundingHandler.ListBodyBytes, (await r.Content.ReadAsByteArrayAsync()).Length);
    }

    [Theory]
    [InlineData(HttpStatusCode.OK, BoundingHandler.ListBodyBytes + 1, true)]
    [InlineData(HttpStatusCode.NotFound, BoundingHandler.ErrorBodyBytes + 1, false)]
    [InlineData(HttpStatusCode.Forbidden, BoundingHandler.ErrorBodyBytes + 1, true)]
    public async Task Past_the_bound_nothing_reaches_the_sdk_and_at_most_one_byte_more_was_read(HttpStatusCode status, int size, bool list)
    {
        foreach (var withLength in new[] { true, false })
        {
            var (r, e, w) = await Send(status, size, list, withLength);
            Assert.Null(r);
            Assert.Equal(S3FailureKind.InvalidResponse, Assert.IsType<GuardException>(e).Kind);
            Assert.True(w.BytesRead <= size, "the handler read past B + 1");
            Assert.True(w.BytesRead <= (list && status == HttpStatusCode.OK ? BoundingHandler.ListBodyBytes : BoundingHandler.ErrorBodyBytes) + 1);
        }
    }

    [Fact]
    public async Task A_successful_object_body_is_passed_through_untouched()
    {
        var (r, e, w) = await Send(HttpStatusCode.OK, 1_000_000, list: false, withLength: true);
        Assert.Null(e);
        Assert.Same(w, r!.Content);
        Assert.Equal(0, w.BytesRead);
    }
}
```

- [ ] **Step 3: Run them and watch them fail to compile**

Run: `dotnet build OKF4net.sln`
Expected: FAIL — the internal types do not exist.

- [ ] **Step 4: Write `GuardException`, `OperationContext`, `AddressingPlan`**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3.Internal;

/// <summary>
/// Thrown by the provider's own handlers to stop a request (refused authority, transport bound). It derives from
/// <see cref="Exception"/> only, so the SDK does not retry it (gate G5), and it carries no text from the request.
/// </summary>
internal sealed class GuardException(S3FailureKind kind) : Exception("request stopped by the S3 provider's guard: " + kind)
{
    public S3FailureKind Kind { get; } = kind;
}
```

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon.Runtime;

namespace OKF4net.Providers.S3.Internal;

/// <summary>What the handlers expect of the requests one provider operation sends (spec §4.4). Flows through the SDK's pipeline by <see cref="AsyncLocal{T}"/> (gate G7).</summary>
internal sealed class OperationContext(S3Operation operation)
{
    public static readonly AsyncLocal<OperationContext?> Current = new();

    public S3Operation Operation { get; } = operation;

    /// <summary>For a read: the decoded S3 key the request path must name.</summary>
    public string? ExpectedObjectS3Key { get; init; }

    /// <summary>For a listing or an existence check: the decoded <c>prefix</c> query parameter.</summary>
    public string? ExpectedListPrefix { get; init; }

    /// <summary>The credentials snapshot every signature of this operation uses (spec §4.4).</summary>
    public ImmutableCredentials? Credentials { get; set; }

    /// <summary>Where the bounding handler leaves a listing page's raw body.</summary>
    public ListPageCapture? Capture { get; init; }
}

/// <summary>The raw body of the last listing page this operation received.</summary>
internal sealed class ListPageCapture
{
    public byte[]? Body { get; set; }
}
```

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3.Internal;

/// <summary>The one authority and path prefix a source's requests may use (spec §4.4 table), and its key mapping (D6).</summary>
internal sealed class AddressingPlan
{
    private AddressingPlan(string scheme, string host, int port, string pathPrefix, string keyPrefix)
    {
        Scheme = scheme;
        Host = host;
        Port = port;
        PathPrefix = pathPrefix;
        KeyPrefix = keyPrefix;
    }

    public string Scheme { get; }
    public string Host { get; }
    public int Port { get; }

    /// <summary>"/&lt;bucket&gt;/" (path-style) or "/" (virtual-hosted): what the raw request path starts with.</summary>
    public string PathPrefix { get; }

    /// <summary>The bundle's S3 key prefix with its trailing '/', or "" for the whole bucket.</summary>
    public string KeyPrefix { get; }

    public static AddressingPlan For(NormalizedSource s)
    {
        var keyPrefix = s.Prefix.Length == 0 ? "" : s.Prefix + "/";
        if (s.ServiceUrl is { } url)
        {
            return s.PathStyle
                ? new AddressingPlan(url.Scheme, url.Host, url.Port, "/" + s.Bucket + "/", keyPrefix)
                : new AddressingPlan(url.Scheme, s.Bucket + "." + url.Host, url.Port, "/", keyPrefix);
        }

        var regional = "s3." + s.Region!.SystemName + "." + s.Region.PartitionDnsSuffix;
        return s.PathStyle
            ? new AddressingPlan(Uri.UriSchemeHttps, regional, 443, "/" + s.Bucket + "/", keyPrefix)
            : new AddressingPlan(Uri.UriSchemeHttps, s.Bucket + "." + regional, 443, "/", keyPrefix);
    }

    /// <summary>The S3 key of a bundle key.</summary>
    public string S3Key(BundleKey key) => KeyPrefix + key;

    /// <summary>The listing prefix for the whole bundle (<paramref name="under"/> null) or under a bundle key prefix.</summary>
    public string ListPrefix(BundleKey? under) => under is null ? KeyPrefix : KeyPrefix + under + "/";
}
```

(If gate G3 recorded that `us-east-1` uses another regional host, or a different property than `PartitionDnsSuffix`, apply G3 here and in `SourceRules`.)

- [ ] **Step 5: Write `PinningHandler`**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3.Internal;

/// <summary>
/// Refuses, before anything is sent, a request outside the current operation (spec D3, §4.4): another scheme,
/// host or port than the plan's one authority; a path that does not start with the plan's prefix; a read whose
/// path is not exactly the expected key; a listing whose <c>prefix</c> is not the expected one; any request made
/// outside a provider operation. Paths are compared after STRICT percent-decoding (<see cref="S3KeyCodec"/>),
/// so an encoding variant cannot pass and a canonicalized path ('..' removed) fails closed.
/// </summary>
internal sealed class PinningHandler(AddressingPlan plan) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var context = OperationContext.Current.Value;
        var uri = request.RequestUri;
        if (context is null || uri is null || !Admits(context, uri))
        {
            throw new GuardException(S3FailureKind.RefusedHost);
        }

        return base.SendAsync(request, ct);
    }

    private bool Admits(OperationContext context, Uri uri)
    {
        if (!string.Equals(uri.Scheme, plan.Scheme, StringComparison.Ordinal)
            || !string.Equals(uri.Host, plan.Host, StringComparison.OrdinalIgnoreCase) || uri.Port != plan.Port)
        {
            return false;
        }

        if (!S3KeyCodec.TryDecodePath(uri.AbsolutePath, out var path) || !path.StartsWith(plan.PathPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var rest = path[plan.PathPrefix.Length..];
        return context.Operation switch
        {
            S3Operation.Read => context.ExpectedObjectS3Key is { } key && string.Equals(rest, key, StringComparison.Ordinal),
            _ => rest.Length == 0 && context.ExpectedListPrefix is { } prefix
                && S3KeyCodec.TryGetQueryValue(uri.Query, "prefix", out var actual) && string.Equals(actual, prefix, StringComparison.Ordinal),
        };
    }
}
```

`S3KeyCodec.TryDecodePath` and `TryGetQueryValue` are written in Task 7; add them **now** in `Internal/S3KeyCodec.cs` with this minimal content, which Task 7 completes and tests in depth:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace OKF4net.Providers.S3.Internal;

/// <summary>Strict percent-decoding to bytes, then UTF-8 without replacement (spec §4.3). Never Uri.UnescapeDataString.</summary>
internal static class S3KeyCodec
{
    private static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    public static bool TryDecode(string encoded, [NotNullWhen(true)] out string? decoded)
    {
        decoded = null;
        var bytes = new List<byte>(encoded.Length);
        Span<byte> one = stackalloc byte[4];
        for (var i = 0; i < encoded.Length; i++)
        {
            var c = encoded[i];
            if (c == '%')
            {
                if (i + 2 >= encoded.Length || !IsHex(encoded[i + 1]) || !IsHex(encoded[i + 2]))
                {
                    return false;
                }

                bytes.Add((byte)((Hex(encoded[i + 1]) << 4) | Hex(encoded[i + 2])));
                i += 2;
                continue;
            }

            if (char.IsSurrogate(c))
            {
                if (!char.IsHighSurrogate(c) || i + 1 >= encoded.Length || !char.IsLowSurrogate(encoded[i + 1]))
                {
                    return false;
                }

                var n = StrictUtf8.GetBytes(encoded.AsSpan(i, 2), one);
                bytes.AddRange(one[..n]);
                i++;
                continue;
            }

            var m = StrictUtf8.GetBytes(encoded.AsSpan(i, 1), one);
            bytes.AddRange(one[..m]);
        }

        try
        {
            decoded = StrictUtf8.GetString(bytes.ToArray());
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    public static bool TryDecodePath(string absolutePath, [NotNullWhen(true)] out string? path) => TryDecode(absolutePath, out path);

    public static bool TryGetQueryValue(string query, string name, out string? value)
    {
        value = null;
        foreach (var pair in query.TrimStart('?').Split('&'))
        {
            var eq = pair.IndexOf('=', StringComparison.Ordinal);
            var key = eq < 0 ? pair : pair[..eq];
            if (string.Equals(key, name, StringComparison.Ordinal))
            {
                return TryDecode(eq < 0 ? "" : pair[(eq + 1)..], out value);
            }
        }

        return false;
    }

    private static bool IsHex(char c) => c is (>= '0' and <= '9') or (>= 'a' and <= 'f') or (>= 'A' and <= 'F');

    private static int Hex(char c) => c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
}
```

- [ ] **Step 6: Write `BoundingHandler`**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net.Http.Headers;

namespace OKF4net.Providers.S3.Internal;

/// <summary>
/// Bounds what reaches the SDK's deserializers (spec §4.6). An error response (status &gt;= 300) or a listing page is
/// read from the network to B + 1 bytes at most (one byte to tell the end from an excess, with or without
/// Content-Length); within B it is handed to the SDK on a NEW in-memory content — the SDK never sees the original
/// stream, so it cannot start deserializing before the check — and a listing page's bytes are also left in the
/// operation's <see cref="ListPageCapture"/> for the provider's strict parse. Past B nothing reaches the SDK. A
/// successful object body passes through untouched (the provider bounds it by MaxObjectBytes).
/// </summary>
internal sealed class BoundingHandler : DelegatingHandler
{
    public const int ListBodyBytes = 8 * 1024 * 1024;
    public const int ErrorBodyBytes = 64 * 1024;
    public const int TokenBytes = 1024;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var response = await base.SendAsync(request, ct).ConfigureAwait(false);
        var isList = request.RequestUri?.Query.Contains("list-type=2", StringComparison.Ordinal) == true;
        var status = (int)response.StatusCode;
        int? bound = status >= 300 ? ErrorBodyBytes : isList ? ListBodyBytes : null;
        if (bound is null)
        {
            return response;
        }

        byte[] body;
        using (response)
        {
            body = await ReadBoundedAsync(response.Content, bound.Value, ct).ConfigureAwait(false)
                ?? throw new GuardException(S3FailureKind.InvalidResponse);
            if (isList && status < 300 && OperationContext.Current.Value?.Capture is { } capture)
            {
                capture.Body = body;
            }

            var copy = new HttpResponseMessage(response.StatusCode)
            {
                Version = response.Version,
                ReasonPhrase = response.ReasonPhrase,
                RequestMessage = response.RequestMessage,
                Content = new ByteArrayContent(body),
            };
            foreach (var h in response.Headers)
            {
                copy.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }

            foreach (var h in response.Content.Headers)
            {
                if (!string.Equals(h.Key, "Content-Length", StringComparison.OrdinalIgnoreCase))
                {
                    copy.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
                }
            }

            copy.Content.Headers.ContentLength = body.Length;
            return copy;
        }
    }

    /// <returns>The body, or null when it is longer than <paramref name="bound"/>.</returns>
    private static async Task<byte[]?> ReadBoundedAsync(HttpContent content, int bound, CancellationToken ct)
    {
        if (content.Headers.ContentLength is { } declared && declared > bound)
        {
            return null;
        }

        var buffer = new byte[bound + 1];
        var total = 0;
        await using var stream = await content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        while (total < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(total, Math.Min(81920, buffer.Length - total)), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return buffer.AsSpan(0, total).ToArray();
            }

            total += n;
        }

        return null; // B + 1 bytes: an excess
    }
}
```

Note: with a declared `Content-Length` greater than the bound, the body is not read at all (zero bytes), which satisfies "at most B + 1".

- [ ] **Step 7: Write the factory and `ClientBuilder`**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon.Runtime;

namespace OKF4net.Providers.S3.Internal;

/// <summary>Always the provider's own HttpClient: never cached across clients, never disposed by the SDK (spec §4.4, gate G4).</summary>
internal sealed class ProviderHttpClientFactory(HttpClient client) : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig) => client;

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;

    public override string? GetConfigUniqueString(IClientConfig clientConfig) => null;
}
```

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon.Runtime;
using Amazon.S3;

namespace OKF4net.Providers.S3.Internal;

/// <summary>Builds the provider's client with a hermetic configuration (spec D2, §4.4; every setting below is a row of gate G2).</summary>
internal static class ClientBuilder
{
    public static AmazonS3Client Build(AWSCredentials credentials, NormalizedSource source, HttpMessageHandler network, out HttpClient httpClient)
    {
        var plan = AddressingPlan.For(source);
        var chain = new PinningHandler(plan) { InnerHandler = new BoundingHandler { InnerHandler = network } };
        httpClient = new HttpClient(chain, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        return new AmazonS3Client(credentials, Configure(source, new ProviderHttpClientFactory(httpClient)));
    }

    /// <summary>The same client WITHOUT the guard, for the counter-proof tests only (never reachable from the public API).</summary>
    internal static AmazonS3Client BuildUnguardedForCounterProof(AWSCredentials credentials, NormalizedSource source, HttpMessageHandler network)
    {
        var httpClient = new HttpClient(network, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        return new AmazonS3Client(credentials, Configure(source, new ProviderHttpClientFactory(httpClient)));
    }

    /// <summary>The network handler of a real provider: no redirect following, no ambient proxy (spec §4.4).</summary>
    public static SocketsHttpHandler Network() => new() { AllowAutoRedirect = false, UseProxy = false };

    private static AmazonS3Config Configure(NormalizedSource source, HttpClientFactory factory)
    {
        var config = new AmazonS3Config
        {
            IgnoreConfiguredEndpointUrls = true,
            RetryMode = RequestRetryMode.Standard,
            MaxErrorRetry = 2,
            UseFIPSEndpoint = false,
            UseDualstackEndpoint = false,
            UseAccelerateEndpoint = false,
            UseArnRegion = false,
            DisableMultiregionAccessPoints = true,
            DefaultConfigurationMode = DefaultConfigurationMode.Standard,
            DisableClockSkewCorrection = false,
            ForcePathStyle = source.PathStyle,
            AllowAutoRedirect = false,
            CacheHttpClient = false,
            Timeout = Timeout.InfiniteTimeSpan,
            HttpClientFactory = factory,
            RequestChecksumCalculation = source.Checksums == S3ChecksumMode.WhenRequired ? RequestChecksumCalculation.WHEN_REQUIRED : RequestChecksumCalculation.WHEN_SUPPORTED,
            ResponseChecksumValidation = source.Checksums == S3ChecksumMode.WhenRequired ? ResponseChecksumValidation.WHEN_REQUIRED : ResponseChecksumValidation.WHEN_SUPPORTED,
        };
        if (source.ServiceUrl is { } url)
        {
            config.ServiceURL = url.ToString();
            config.AuthenticationRegion = "us-east-1";
        }
        else
        {
            config.RegionEndpoint = source.Region;
        }

        ApplyGateRows(config);
        return config;
    }

    /// <summary>
    /// The neutralizations gate G1 (region detection, CSM — for this client only) and G2/G4 (the remaining ambient
    /// settings, the neutral ClientAppId, AuthSchemePreference) recorded in the spec's Annexe B. Write here exactly
    /// the assignments of those rows, one line each with the row id in a trailing comment; nothing else.
    /// </summary>
    private static void ApplyGateRows(AmazonS3Config config)
    {
        // Example of the expected form (replace with Annexe B's rows):
        // config.ClientAppId = "";            // G4
        // config.AuthSchemePreference = "sigv4"; // G2
    }
}
```

`ApplyGateRows` is the one place gate results enter code; the hostile-environment test (Task 10) and the socket-level egress test (Task 10) are its guards. Leave no commented example in the final code: replace the comments with Annexe B's assignments.

Minimal `SnapshotCredentials` (completed in Task 6):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon.Runtime;

namespace OKF4net.Providers.S3.Internal;

/// <summary>The SDK's view of credentials: the current operation's snapshot, never the host's provider (spec §4.4).</summary>
internal sealed class SnapshotCredentials : AWSCredentials
{
    public override ImmutableCredentials GetCredentials() =>
        OperationContext.Current.Value?.Credentials ?? throw new GuardException(S3FailureKind.Credentials);

    public override Task<ImmutableCredentials> GetCredentialsAsync() => Task.FromResult(GetCredentials());
}
```

(If `GetCredentialsAsync` is not virtual in the resolved SDK — gate G4 — drop that override.)

- [ ] **Step 8: Run the tests**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Providers.S3"`
Expected: PASS. If a counter-proof fails (the unguarded client does not follow the 307), read G3: the SDK version under test may not follow it — record in the test's comment which version follows and keep the counter-proof where it does; never delete a counter-proof silently.

- [ ] **Step 9: Full checks and commit**

```bash
dotnet build OKF4net.sln && dotnet test OKF4net.sln --filter "Category!=ContainerIntegration&Category!=S3Integration" && dotnet format OKF4net.sln --verify-no-changes
git add src/OKF4net.Providers.S3/Internal tests/OKF4net.Tests/Providers/S3
git commit -m "feat(s3): request guard on one authority and path, transport bounds, provider-owned HttpClient, hermetic client (#142)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 6: Operation scope — permit, deadline, credentials snapshot, failure priority

**Files:**
- Create: `src/OKF4net.Providers.S3/Internal/OperationScope.cs`
- Modify: `src/OKF4net.Providers.S3/Internal/SnapshotCredentials.cs` (add `CredentialResolver`)
- Modify: `docs/superpowers/specs/2026-11-16-s3-bundle-provider-design.md` §4.4, one sentence (see Step 5)
- Test: `tests/OKF4net.Tests/Providers/S3/OperationScopeTests.cs`, `tests/OKF4net.Tests/Providers/S3/CredentialsTests.cs`

**Interfaces:**
- Consumes: `OperationContext`, `GuardException`, `S3RequestException` internal constructors.
- Produces:
  - `internal sealed class OperationGate(int maxConcurrent, TimeProvider time) { ValueTask<OperationScope> EnterAsync(S3Operation op, TimeSpan timeout, CancellationToken caller, BundleKey? key); }`
  - `internal sealed class OperationScope : IAsyncDisposable { CancellationToken Token; S3Operation Operation; BundleKey? Key; void HoldPermitUntil(Task abandoned); Exception Fail(Exception e); }` — `Fail` applies the priority of spec §4.5 and returns the exception to throw (never throws itself).
  - `internal sealed class CredentialResolver(AWSCredentials host) { Task<ImmutableCredentials> ResolveAsync(OperationScope scope); }`

Design note (spec §4.4 adjustment, recorded in Step 5): `ImmutableCredentials` carries no expiry, so "renew when absent or near expiry" is implemented as **resolve once per operation** — the host's `AWSCredentials` (e.g. `RefreshingAWSCredentials`) caches internally, so this costs a cache read. Each operation gets an immutable snapshot in its `OperationContext`; every signature of that operation, retries and the redirect path included, uses it.

- [ ] **Step 1: Write the failing tests**

`OperationScopeTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec D16, §4.5.</summary>
public class OperationScopeTests
{
    [Fact]
    public async Task Never_more_than_N_operations_hold_a_permit()
    {
        var gate = new OperationGate(3, TimeProvider.System);
        var inFlight = 0;
        var peak = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 10).Select(async _ =>
        {
            await using var scope = await gate.EnterAsync(S3Operation.Read, TimeSpan.FromSeconds(30), default, null);
            var now = Interlocked.Increment(ref inFlight);
            InterlockedMax(ref peak, now);
            await release.Task.WaitAsync(scope.Token);
            Interlocked.Decrement(ref inFlight);
        }).ToArray();
        await Task.Delay(200);
        Assert.Equal(3, Volatile.Read(ref peak));
        release.SetResult();
        await Task.WhenAll(tasks);
    }

    [Fact]
    public async Task Waiting_for_a_permit_counts_in_the_deadline_and_is_a_Timeout()
    {
        var gate = new OperationGate(1, TimeProvider.System);
        await using var held = await gate.EnterAsync(S3Operation.Read, TimeSpan.FromSeconds(30), default, null);
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await gate.EnterAsync(S3Operation.List, TimeSpan.FromMilliseconds(100), default, null));
        Assert.Equal(S3FailureKind.Timeout, e.Kind);
    }

    [Fact]
    public async Task A_cancelled_waiter_leaves_the_queue_as_a_cancellation()
    {
        var gate = new OperationGate(1, TimeProvider.System);
        await using var held = await gate.EnterAsync(S3Operation.Read, TimeSpan.FromSeconds(30), default, null);
        using var cts = new CancellationTokenSource(50);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await gate.EnterAsync(S3Operation.Read, TimeSpan.FromSeconds(30), cts.Token, null));
    }

    [Fact]
    public async Task A_permit_held_for_an_abandoned_task_returns_only_when_it_ends()
    {
        var gate = new OperationGate(1, TimeProvider.System);
        var abandoned = new TaskCompletionSource();
        await using (var scope = await gate.EnterAsync(S3Operation.Read, TimeSpan.FromSeconds(30), default, null))
        {
            scope.HoldPermitUntil(abandoned.Task);
        }

        await Assert.ThrowsAsync<S3RequestException>(async () => await gate.EnterAsync(S3Operation.Read, TimeSpan.FromMilliseconds(100), default, null));
        abandoned.SetResult();
        await using var next = await gate.EnterAsync(S3Operation.Read, TimeSpan.FromSeconds(5), default, null);
    }

    public static TheoryData<string> PriorityCases => new() { "cancel-then-bound", "bound-then-cancel", "foreign-oce", "integrity-alone" };

    [Theory]
    [MemberData(nameof(PriorityCases))]
    public async Task The_first_principal_failure_wins_by_the_rule_of_4_5(string scenario)
    {
        var gate = new OperationGate(4, TimeProvider.System);
        using var caller = new CancellationTokenSource();
        await using var scope = await gate.EnterAsync(S3Operation.Read, TimeSpan.FromSeconds(30), caller.Token, BundleKey.Parse("a.md"));
        Exception result;
        switch (scenario)
        {
            case "cancel-then-bound":
                await caller.CancelAsync();
                result = scope.Fail(new BundleObjectTooLargeException(BundleKey.Parse("a.md"), 10, 5));
                Assert.IsAssignableFrom<OperationCanceledException>(result);
                break;
            case "bound-then-cancel":
                result = scope.Fail(new BundleObjectTooLargeException(BundleKey.Parse("a.md"), 10, 5));
                await caller.CancelAsync();
                Assert.IsType<BundleObjectTooLargeException>(scope.Fail(new OperationCanceledException(caller.Token)));
                break;
            case "foreign-oce":
                result = scope.Fail(new OperationCanceledException()); // not the caller's token
                Assert.Equal(S3FailureKind.Timeout, Assert.IsType<S3RequestException>(result).Kind);
                break;
            default:
                result = scope.Fail(new GuardException(S3FailureKind.Integrity));
                Assert.Equal(S3FailureKind.Integrity, Assert.IsType<S3RequestException>(result).Kind);
                break;
        }
    }

    private static void InterlockedMax(ref int target, int value)
    {
        int seen;
        while ((seen = Volatile.Read(ref target)) < value && Interlocked.CompareExchange(ref target, value, seen) != seen)
        {
        }
    }
}
```

`CredentialsTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon.Runtime;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec §4.4, identifiers: isolated, bounded, one snapshot per operation.</summary>
public class CredentialsTests
{
    private sealed class HostCredentials(Func<int, ImmutableCredentials> get) : AWSCredentials
    {
        public int Calls;
        public override ImmutableCredentials GetCredentials() => get(Interlocked.Increment(ref Calls));
    }

    private static async Task<(ImmutableCredentials? Snapshot, Exception? Error)> Resolve(AWSCredentials host, TimeSpan timeout)
    {
        var gate = new OperationGate(2, TimeProvider.System);
        await using var scope = await gate.EnterAsync(S3Operation.Read, timeout, default, null);
        try
        {
            return (await new CredentialResolver(host).ResolveAsync(scope), null);
        }
        catch (Exception e)
        {
            return (null, e);
        }
    }

    [Fact]
    public async Task A_provider_blocking_synchronously_is_a_Credentials_failure_within_the_deadline()
    {
        var never = new ManualResetEventSlim();
        var (snapshot, e) = await Resolve(new HostCredentials(_ => { never.Wait(); return null!; }), TimeSpan.FromMilliseconds(200));
        Assert.Null(snapshot);
        Assert.Equal(S3FailureKind.Credentials, Assert.IsType<S3RequestException>(e).Kind);
        never.Set();
    }

    [Fact]
    public async Task A_late_result_is_ignored_and_the_permit_waits_for_it()
    {
        var release = new ManualResetEventSlim();
        var gate = new OperationGate(1, TimeProvider.System);
        await using (var scope = await gate.EnterAsync(S3Operation.Read, TimeSpan.FromMilliseconds(100), default, null))
        {
            await Assert.ThrowsAsync<S3RequestException>(() => new CredentialResolver(new HostCredentials(_ => { release.Wait(); return new ImmutableCredentials("A", "S", null); })).ResolveAsync(scope));
        }

        await Assert.ThrowsAsync<S3RequestException>(async () => await gate.EnterAsync(S3Operation.Read, TimeSpan.FromMilliseconds(100), default, null));
        release.Set();
        await using var next = await gate.EnterAsync(S3Operation.Read, TimeSpan.FromSeconds(5), default, null);
    }

    [Fact]
    public async Task A_provider_that_succeeds_once_then_blocks_is_never_called_from_the_pipeline()
    {
        // The SDK reads credentials through SnapshotCredentials, which returns the operation's snapshot and
        // never touches the host's provider (spec §4.4) — on every signature, retries and redirect included.
        var block = new ManualResetEventSlim();
        var host = new HostCredentials(n => { if (n > 1) block.Wait(); return new ImmutableCredentials("A", "S", "T"); });
        var gate = new OperationGate(1, TimeProvider.System);
        await using var scope = await gate.EnterAsync(S3Operation.Read, TimeSpan.FromSeconds(5), default, null);
        OperationContext.Current.Value = new OperationContext(S3Operation.Read) { Credentials = await new CredentialResolver(host).ResolveAsync(scope) };
        try
        {
            var sdkView = new SnapshotCredentials();
            for (var i = 0; i < 5; i++)
            {
                Assert.Equal("A", sdkView.GetCredentials().AccessKey);
                Assert.Equal("A", (await sdkView.GetCredentialsAsync()).AccessKey);
            }
        }
        finally
        {
            OperationContext.Current.Value = null;
            block.Set();
        }

        Assert.Equal(1, host.Calls);
    }

    [Fact]
    public void Outside_an_operation_the_sdk_view_refuses() =>
        Assert.Throws<GuardException>(() => new SnapshotCredentials().GetCredentials());
}
```

The end-to-end form of the "succeeds once then blocks" case — a 307 and a retried 503 through the real client — is in Task 8's `ReadTests` (`Retries_and_redirects_sign_with_the_operation_snapshot`), once `ReadAsync` exists.

- [ ] **Step 2: Run them and watch them fail to compile**

Run: `dotnet build OKF4net.sln`
Expected: FAIL — `OperationGate`, `CredentialResolver` do not exist.

- [ ] **Step 3: Write `OperationScope`/`OperationGate`**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Providers.S3.Internal;

/// <summary>The provider's operation permits (spec D16): one per operation, from the call to the end of the body and of the cleanup.</summary>
internal sealed class OperationGate(int maxConcurrent, TimeProvider time)
{
    private readonly SemaphoreSlim _permits = new(maxConcurrent, maxConcurrent);

    /// <summary>Waits for a permit under the operation's deadline, which starts now (spec §4.5).</summary>
    public async ValueTask<OperationScope> EnterAsync(S3Operation operation, TimeSpan timeout, CancellationToken caller, BundleKey? key)
    {
        var deadline = new CancellationTokenSource(timeout, time);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(caller, deadline.Token);
        try
        {
            await _permits.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!caller.IsCancellationRequested)
        {
            linked.Dispose();
            deadline.Dispose();
            throw new S3RequestException(operation, S3FailureKind.Timeout, null, key);
        }
        catch
        {
            linked.Dispose();
            deadline.Dispose();
            throw;
        }

        return new OperationScope(_permits, operation, key, caller, deadline, linked);
    }
}

/// <summary>One provider operation: its token (caller + deadline), its permit, and the failure priority of spec §4.5.</summary>
internal sealed class OperationScope : IAsyncDisposable
{
    private readonly SemaphoreSlim _permits;
    private readonly CancellationToken _caller;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _linked;
    private Task? _holdUntil;
    private Exception? _principal;

    internal OperationScope(SemaphoreSlim permits, S3Operation operation, BundleKey? key, CancellationToken caller, CancellationTokenSource deadline, CancellationTokenSource linked)
    {
        _permits = permits;
        Operation = operation;
        Key = key;
        _caller = caller;
        _deadline = deadline;
        _linked = linked;
    }

    public S3Operation Operation { get; }

    public BundleKey? Key { get; }

    /// <summary>The caller's token and the deadline together.</summary>
    public CancellationToken Token => _linked.Token;

    /// <summary>Keeps the permit until <paramref name="abandoned"/> ends (an isolated credentials call that outlived its deadline).</summary>
    public void HoldPermitUntil(Task abandoned) => _holdUntil = abandoned;

    /// <summary>
    /// The exception this operation ends with, given a failure just observed (spec §4.5): the caller's cancellation
    /// if it was requested; else the FIRST principal failure retained (a bound, or the first classified failure);
    /// a cancellation that is not the caller's is a Timeout. Never throws.
    /// </summary>
    public Exception Fail(Exception observed)
    {
        if (_caller.IsCancellationRequested)
        {
            return observed as OperationCanceledException ?? new OperationCanceledException(_caller);
        }

        if (_principal is not null)
        {
            return _principal;
        }

        _principal = observed switch
        {
            BundleObjectTooLargeException or BundleLimitExceededException or BundleObjectNotFoundException
                or BundleVersionMismatchException or BundleAccessRefusedException or S3RequestException => observed,
            OperationCanceledException => new S3RequestException(Operation, S3FailureKind.Timeout, null, Key, observed),
            GuardException g => new S3RequestException(Operation, g.Kind, null, Key, g),
            _ => ErrorClassifier.Classify(Operation, Key, observed),
        };
        return _principal;
    }

    public ValueTask DisposeAsync()
    {
        _linked.Dispose();
        _deadline.Dispose();
        if (_holdUntil is { IsCompleted: false } pending)
        {
            _ = pending.ContinueWith(static (_, s) => ((SemaphoreSlim)s!).Release(), _permits, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        else
        {
            _permits.Release();
        }

        return ValueTask.CompletedTask;
    }
}
```

`ErrorClassifier.Classify` is written in Task 8; add it **now** as a stub that returns `new S3RequestException(operation, S3FailureKind.Transport, null, key, e)` so this task compiles, with an XML comment "completed in Task 8 (spec §4.6 table)". Task 8's tests replace the stub's behaviour.

- [ ] **Step 4: Write `CredentialResolver`**

Append to `SnapshotCredentials.cs`:

```csharp
/// <summary>
/// Resolves the host's credentials for one operation (spec §4.4): on the thread pool, so a provider that blocks
/// synchronously cannot hold the caller; under the operation's deadline; a late result is dropped, and the permit
/// stays held until the abandoned call really ends — at most MaxConcurrentRequests threads per provider.
/// </summary>
internal sealed class CredentialResolver(AWSCredentials host)
{
    public async Task<ImmutableCredentials> ResolveAsync(OperationScope scope)
    {
        var call = Task.Run(host.GetCredentials, CancellationToken.None);
        try
        {
            var snapshot = await call.WaitAsync(scope.Token).ConfigureAwait(false);
            return snapshot ?? throw new S3RequestException(scope.Operation, S3FailureKind.Credentials, null, scope.Key);
        }
        catch (OperationCanceledException e)
        {
            scope.HoldPermitUntil(call);
            var failure = scope.Fail(e); // the caller's cancellation, or a Timeout
            throw failure is S3RequestException { Kind: S3FailureKind.Timeout }
                ? new S3RequestException(scope.Operation, S3FailureKind.Credentials, null, scope.Key, failure)
                : failure;
        }
        catch (Exception e) when (e is not S3RequestException)
        {
            throw new S3RequestException(scope.Operation, S3FailureKind.Credentials, null, scope.Key, e);
        }
    }
}
```

(`GetCredentials()` is used rather than `GetCredentialsAsync()` because the latter may itself run the synchronous call on the caller's thread before returning a task, spec §4.4; inside `Task.Run` both are equivalent. Add the four `using`s: `Amazon.Runtime`.)

- [ ] **Step 5: Record the per-operation resolution in the spec**

In spec §4.4, paragraph "Identifiants : un instantané…", replace "Avant chaque opération, le provider renouvelle cet instantané s'il est absent ou proche de son expiration, en appelant" with "`ImmutableCredentials` ne portant pas d'expiration, le provider résout un instantané **par opération**, en appelant" and replace "; un seul renouvellement à la fois (les opérations concurrentes l'attendent)" with "(un fournisseur rafraîchissant de l'hôte met lui-même en cache)".

- [ ] **Step 6: Run the tests, full checks, commit**

```bash
dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Providers.S3"
dotnet build OKF4net.sln && dotnet test OKF4net.sln --filter "Category!=ContainerIntegration&Category!=S3Integration" && dotnet format OKF4net.sln --verify-no-changes
git add src/OKF4net.Providers.S3/Internal tests/OKF4net.Tests/Providers/S3 docs/superpowers/specs/2026-11-16-s3-bundle-provider-design.md
git commit -m "feat(s3): operation permits and deadline, isolated per-operation credentials snapshot, failure priority (#142)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 7: Listing — strict codec, page parser, walker invariants, `s3v1` token

**Files:**
- Modify: `src/OKF4net.Providers.S3/Internal/S3KeyCodec.cs` (add `CompareByCodePoint`)
- Create: `src/OKF4net.Providers.S3/Internal/ListPageParser.cs`, `ListingWalker.cs`, `BundleTokenHasher.cs`
- Test: `tests/OKF4net.Tests/Providers/S3/CodecTests.cs`, `ListPageParserTests.cs`, `ListingWalkerTests.cs`, `BundleTokenTests.cs`

**Interfaces:**
- Consumes: `OperationContext`, `ListPageCapture`, `OperationGate`, `CredentialResolver`, `AddressingPlan`, the client from `ClientBuilder`.
- Produces:
  - `internal sealed record S3Entry(string S3Key, string ETag, long Size)`
  - `internal sealed record ListPage(IReadOnlyList<S3Entry> Entries, bool IsTruncated, string? NextToken, int MaxKeys)`
  - `internal static class ListPageParser { static ListPage Parse(byte[] body); }` — throws `GuardException(InvalidResponse)`
  - `internal sealed class ListingWalker(...) { IAsyncEnumerable<S3Entry> WalkAsync(string listPrefix, int maxKeys, long maxPages, TimeSpan timeout, CancellationToken ct); }`
  - `internal static class BundleTokenHasher { static string Hash(IEnumerable<(string RawName, string ETag, long Size)> entries); }` plus an incremental `Builder` used by `GetVersionAsync`.

- [ ] **Step 1: Write the failing codec and parser tests**

`CodecTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec §4.3, strict decoding.</summary>
public class CodecTests
{
    [Theory]
    [InlineData("%FF")]
    [InlineData("%C0%AF")]
    [InlineData("%ED%A0%80")]
    [InlineData("%")]
    [InlineData("%4")]
    [InlineData("%G1")]
    public void Invalid_encodings_are_refused(string encoded) => Assert.False(S3KeyCodec.TryDecode(encoded, out _));

    [Theory]
    [InlineData("%25FF", "%FF")]
    [InlineData("%25C0%25AF", "%C0%AF")]
    [InlineData("caf%C3%A9.md", "café.md")]
    [InlineData("cafe%CC%81.md", "café.md")]
    [InlineData("a+b.md", "a+b.md")]
    [InlineData("bad%01.md", "bad\u0001.md")]
    [InlineData("%F0%90%80%80", "\U00010000")]
    public void Valid_encodings_decode_once_exactly(string encoded, string expected)
    {
        Assert.True(S3KeyCodec.TryDecode(encoded, out var decoded));
        Assert.Equal(expected, decoded);
    }

    [Fact]
    public void Lax_decoding_aliases_are_impossible()
    {
        Assert.False(S3KeyCodec.TryDecode("%FF.md", out _));
        Assert.True(S3KeyCodec.TryDecode("%25FF.md", out var literal));
        Assert.Equal("%FF.md", literal);
    }

    [Fact]
    public void Code_point_order_is_utf8_order_not_utf16_order()
    {
        // U+E000 < U+10000 in UTF-8 (EE 80 80 < F0 90 80 80); UTF-16 ordinal says the opposite (E000 > D800).
        Assert.True(S3KeyCodec.CompareByCodePoint("", "\U00010000") < 0);
        Assert.True(string.CompareOrdinal("", "\U00010000") > 0);
    }
}
```

`ListPageParserTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using OKF4net.Providers.S3;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec D9: the provider's own strict parse of a raw page.</summary>
public class ListPageParserTests
{
    internal static string Page(IEnumerable<(string EncodedKey, string ETag, long Size)> entries, bool truncated, string? token, string? encodingType = "url", int maxKeys = 1000)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><Name>okf</Name>");
        sb.Append("<MaxKeys>").Append(maxKeys).Append("</MaxKeys>");
        if (encodingType is not null) sb.Append("<EncodingType>").Append(encodingType).Append("</EncodingType>");
        sb.Append("<IsTruncated>").Append(truncated ? "true" : "false").Append("</IsTruncated>");
        if (token is not null) sb.Append("<NextContinuationToken>").Append(token).Append("</NextContinuationToken>");
        foreach (var (k, e, s) in entries)
            sb.Append("<Contents><Key>").Append(k).Append("</Key><ETag>").Append(System.Security.SecurityElement.Escape(e)).Append("</ETag><Size>").Append(s).Append("</Size></Contents>");
        return sb.Append("</ListBucketResult>").ToString();
    }

    private static ListPage Parse(string xml) => ListPageParser.Parse(Encoding.UTF8.GetBytes(xml));

    [Fact]
    public void A_page_decodes_its_keys_strictly()
    {
        var p = Parse(Page([("kb/caf%C3%A9.md", "\"e1\"", 3)], truncated: false, token: null));
        Assert.Equal("kb/café.md", Assert.Single(p.Entries).S3Key);
    }

    public static TheoryData<string> Refused => new()
    {
        Page([("kb/a.md", "\"e\"", 1)], false, null, encodingType: null),
        Page([("kb/a.md", "\"e\"", 1)], false, null, encodingType: "other"),
        Page([("kb/%FF.md", "\"e\"", 1)], false, null),
        Page([("kb/a.md", "", 1)], false, null),
        Page([("kb/a.md", "\"e\"", -1)], false, null),
        Page([("kb/a.md", "\"e\"", 1)], true, null),
        Page([("kb/a.md", "\"e\"", 1)], true, new string('t', 1025)),
        "<not-xml",
        "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e \"x\">]><ListBucketResult/>",
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void An_untrustworthy_page_is_InvalidResponse(string xml) =>
        Assert.Equal(S3FailureKind.InvalidResponse, Assert.Throws<GuardException>(() => Parse(xml)).Kind);
}
```

- [ ] **Step 2: Run them and watch them fail to compile**

Run: `dotnet build OKF4net.sln`
Expected: FAIL — `ListPageParser`, `CompareByCodePoint` missing.

- [ ] **Step 3: Write `CompareByCodePoint` and `ListPageParser`**

Add to `S3KeyCodec`:

```csharp
    /// <summary>Compares by Unicode code point, which is S3's UTF-8 byte order (spec §4.5), not UTF-16 ordinal order.</summary>
    public static int CompareByCodePoint(string a, string b)
    {
        var ea = a.EnumerateRunes();
        var eb = b.EnumerateRunes();
        while (true)
        {
            var ha = ea.MoveNext();
            var hb = eb.MoveNext();
            if (!ha || !hb)
            {
                return ha == hb ? 0 : ha ? 1 : -1;
            }

            var c = ea.Current.Value.CompareTo(eb.Current.Value);
            if (c != 0)
            {
                return c;
            }
        }
    }
```

`ListPageParser.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Xml;

namespace OKF4net.Providers.S3.Internal;

internal sealed record S3Entry(string S3Key, string ETag, long Size);

internal sealed record ListPage(IReadOnlyList<S3Entry> Entries, bool IsTruncated, string? NextToken, int MaxKeys);

/// <summary>
/// Parses a raw ListObjectsV2 page the bounding handler captured (spec D9): no DTD, no resolver, bounded by the
/// handler; EncodingType must be "url"; every Key decoded strictly; ETag present and non-empty; Size present and
/// non-negative; a truncated page carries a non-empty token of at most 1 024 bytes. Anything else is
/// InvalidResponse. The SDK's own model of the page is never trusted for keys.
/// </summary>
internal static class ListPageParser
{
    public static ListPage Parse(byte[] body)
    {
        try
        {
            return ParseCore(body);
        }
        catch (XmlException)
        {
            throw new GuardException(S3FailureKind.InvalidResponse);
        }
    }

    private static ListPage ParseCore(byte[] body)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true };
        using var reader = XmlReader.Create(new MemoryStream(body), settings);
        var entries = new List<S3Entry>();
        string? encodingType = null, token = null;
        bool? truncated = null;
        var maxKeys = 0;
        string? key = null, etag = null;
        long? size = null;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "Contents")
            {
                entries.Add(Entry(key, etag, size));
                (key, etag, size) = (null, null, null);
                continue;
            }

            if (reader.NodeType != XmlNodeType.Element)
            {
                continue;
            }

            switch (reader.LocalName)
            {
                case "EncodingType": encodingType = reader.ReadElementContentAsString(); break;
                case "IsTruncated": truncated = bool.Parse(reader.ReadElementContentAsString()); break;
                case "NextContinuationToken": token = reader.ReadElementContentAsString(); break;
                case "MaxKeys": maxKeys = int.Parse(reader.ReadElementContentAsString(), NumberStyles.None, CultureInfo.InvariantCulture); break;
                case "Key": key = reader.ReadElementContentAsString(); break;
                case "ETag": etag = reader.ReadElementContentAsString(); break;
                case "Size": size = long.Parse(reader.ReadElementContentAsString(), NumberStyles.None, CultureInfo.InvariantCulture); break;
            }
        }

        if (encodingType != "url" || truncated is null)
        {
            throw new GuardException(S3FailureKind.InvalidResponse);
        }

        if (truncated.Value && (string.IsNullOrEmpty(token) || Encoding.UTF8.GetByteCount(token) > BoundingHandler.TokenBytes))
        {
            throw new GuardException(S3FailureKind.InvalidResponse);
        }

        return new ListPage(entries, truncated.Value, token, maxKeys);
    }

    private static S3Entry Entry(string? encodedKey, string? etag, long? size)
    {
        if (encodedKey is null || string.IsNullOrEmpty(etag) || size is not >= 0 || !S3KeyCodec.TryDecode(encodedKey, out var decoded))
        {
            throw new GuardException(S3FailureKind.InvalidResponse);
        }

        return new S3Entry(decoded, etag, size.Value);
    }
}
```

(`bool.Parse`/`int.Parse` failures throw `FormatException`; catch it with `XmlException` in `Parse` too: `catch (Exception e) when (e is XmlException or FormatException or OverflowException)`.)

- [ ] **Step 4: Run codec/parser tests**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~CodecTests|FullyQualifiedName~ListPageParserTests"`
Expected: PASS.

- [ ] **Step 5: Write the failing walker and token tests**

`S3Test.cs` (test helper, used from here on):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using System.Text;
using Amazon.Runtime;
using OKF4net.Providers.S3;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Providers over the scripted fake (spec T1).</summary>
internal static class S3Test
{
    public static readonly BasicAWSCredentials Creds = new("AKIDEXAMPLE", "secret");

    public static S3BundleSource Local(string prefix = "kb") =>
        new() { Bucket = "okf", Prefix = prefix, ServiceUrl = new Uri("http://127.0.0.1:9000"), ForcePathStyle = true, AllowInsecureHttp = true, RequestTimeout = TimeSpan.FromSeconds(5) };

    public static S3BundleProvider Provider(FakeS3Handler fake, S3BundleSource? source = null, AWSCredentials? creds = null) =>
        new(creds ?? Creds, source ?? Local(), fake);

    /// <summary>A fake serving <paramref name="objects"/> (S3 key → bytes) as a real store would: listings (pages of <paramref name="pageSize"/>), GETs.</summary>
    public static FakeS3Handler Serving(IReadOnlyDictionary<string, byte[]> objects, int pageSize = 1000)
    {
        var sorted = objects.Keys.Order(Comparer<string>.Create(OKF4net.Providers.S3.Internal.S3KeyCodec.CompareByCodePoint)).ToArray();
        return new FakeS3Handler
        {
            Respond = (req, _) =>
            {
                var uri = req.RequestUri!;
                if (uri.Query.Contains("list-type=2", StringComparison.Ordinal))
                {
                    OKF4net.Providers.S3.Internal.S3KeyCodec.TryGetQueryValue(uri.Query, "prefix", out var prefix);
                    OKF4net.Providers.S3.Internal.S3KeyCodec.TryGetQueryValue(uri.Query, "continuation-token", out var token);
                    _ = OKF4net.Providers.S3.Internal.S3KeyCodec.TryGetQueryValue(uri.Query, "max-keys", out var mk);
                    var max = Math.Min(pageSize, mk is null ? 1000 : int.Parse(mk, System.Globalization.CultureInfo.InvariantCulture));
                    var start = token is null ? 0 : int.Parse(token, System.Globalization.CultureInfo.InvariantCulture);
                    var matching = sorted.Where(k => k.StartsWith(prefix ?? "", StringComparison.Ordinal)).ToArray();
                    var page = matching.Skip(start).Take(max).ToArray();
                    var more = start + page.Length < matching.Length;
                    return Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.OK, ListPageParserTests.Page(
                        page.Select(k => (Encode(k), "\"" + Convert.ToHexStringLower(System.Security.Cryptography.MD5.HashData(objects[k])) + "\"", (long)objects[k].Length)),
                        more, more ? (start + page.Length).ToString(System.Globalization.CultureInfo.InvariantCulture) : null)));
                }

                OKF4net.Providers.S3.Internal.S3KeyCodec.TryDecode(uri.AbsolutePath, out var path);
                var key = path!["/okf/".Length..];
                return Task.FromResult(objects.TryGetValue(key, out var bytes)
                    ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }
                    : FakeS3Handler.Xml(HttpStatusCode.NotFound, FakeS3Handler.Error("NoSuchKey")));
            },
        };
    }

    public static string Encode(string key)
    {
        var sb = new StringBuilder();
        foreach (var b in Encoding.UTF8.GetBytes(key))
        {
            var c = (char)b;
            if (char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '/')
            {
                sb.Append(c);
            }
            else
            {
                sb.Append('%').Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    public static Dictionary<string, byte[]> Objects(params (string Key, string Text)[] items) =>
        items.ToDictionary(i => i.Key, i => Encoding.UTF8.GetBytes(i.Text), StringComparer.Ordinal);
}
```

`ListingWalkerTests.cs` (through the provider's `EnumerateAsync`, so it waits on Task 8's provider skeleton; write the provider's constructor and `EnumerateAsync`/`GetVersionAsync`/`ExistsAsync` in Step 7 of this task):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using OKF4net.Providers.S3;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec §4.3, D9: the walker's invariants, projections and bounds.</summary>
public class ListingWalkerTests
{
    private const string Note = "---\ntype: Note\ntitle: T\ndescription: D.\n---\n\nB\n";

    private static async Task<List<string>> Names(S3BundleProvider p, BundleKey? prefix = null)
    {
        var names = new List<string>();
        await foreach (var l in p.EnumerateAsync(prefix, BundleLimits.Default, default))
        {
            names.Add(l.RawName);
        }

        return names;
    }

    [Fact]
    public async Task The_prefix_is_removed_and_kb2_is_never_requested()
    {
        var fake = S3Test.Serving(S3Test.Objects(("kb/a.md", Note), ("kb2/x.md", Note), ("other.md", Note)));
        Assert.Equal(["a.md"], await Names(S3Test.Provider(fake)));
        Assert.All(fake.Requests, r => Assert.Contains("prefix=kb%2F", r.RequestUri!.Query, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Many_small_valid_pages_are_accepted()
    {
        var objects = Enumerable.Range(0, 300).ToDictionary(i => $"kb/n{i:D4}.md", _ => "x"u8.ToArray(), StringComparer.Ordinal);
        using var p = S3Test.Provider(S3Test.Serving(objects, pageSize: 7));
        var names = new List<string>();
        await foreach (var l in p.EnumerateAsync(null, BundleLimits.Default with { MaxEntries = 1000 }, default))
        {
            names.Add(l.RawName);
        }

        Assert.Equal(300, names.Count);
    }

    [Fact]
    public async Task More_than_a_thousand_objects_span_pages_and_the_entry_bound_stops_and_disposes()
    {
        var objects = Enumerable.Range(0, 2500).ToDictionary(i => $"kb/n{i:D4}.md", _ => "x"u8.ToArray(), StringComparer.Ordinal);
        var fake = S3Test.Serving(objects);
        using var p = S3Test.Provider(fake);
        Assert.Equal(2500, (await Names(p)).Count);
        var e = await Assert.ThrowsAsync<BundleLoadException>(async () => await Bundle.LoadAsync(p, new BundleLoadOptions { Limits = BundleLimits.Default with { MaxEntries = 1500 } }));
        Assert.IsType<BundleLimitExceededException>(e.InnerException);
    }

    public static TheoryData<string> Inconsistencies => new() { "outside-prefix", "not-increasing", "duplicate", "repeated-token", "truncated-empty", "oversized-page" };

    [Theory]
    [MemberData(nameof(Inconsistencies))]
    public async Task A_transport_inconsistency_fails_the_listing_and_the_load(string scenario)
    {
        var calls = 0;
        var fake = new FakeS3Handler
        {
            Respond = (_, _) =>
            {
                calls++;
                var page = scenario switch
                {
                    "outside-prefix" => ListPageParserTests.Page([("other/a.md", "\"e\"", 1)], false, null),
                    "not-increasing" => ListPageParserTests.Page([("kb/b.md", "\"e\"", 1), ("kb/a.md", "\"e\"", 1)], false, null),
                    "duplicate" => ListPageParserTests.Page([("kb/a.md", "\"e\"", 1), ("kb/a.md", "\"e\"", 1)], false, null),
                    "repeated-token" => ListPageParserTests.Page([("kb/a" + calls + ".md", "\"e\"", 1)], true, "same"),
                    "truncated-empty" => ListPageParserTests.Page([], true, "t" + calls),
                    _ => ListPageParserTests.Page(Enumerable.Range(0, 1001).Select(i => ($"kb/n{i:D4}.md", "\"e\"", 1L)), false, null),
                };
                return Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.OK, page));
            },
        };
        using var p = S3Test.Provider(fake);
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await Names(p));
        Assert.Equal(S3FailureKind.InvalidResponse, e.Kind);
        Assert.True(calls <= 2, "an inconsistency is detected on the page that shows it");
        await Assert.ThrowsAsync<BundleLoadException>(async () => await Bundle.LoadAsync(p));
    }

    [Fact]
    public async Task A_badly_named_but_well_transported_key_is_a_local_problem()
    {
        using var p = S3Test.Provider(S3Test.Serving(S3Test.Objects(("kb/bad\u0001.md", Note), ("kb/a//b.md", Note), ("kb/ok.md", Note))));
        var bundle = await Bundle.LoadAsync(p);
        Assert.Contains(bundle.Concepts, c => c.Id.ToString() == "ok");
        Assert.Contains(bundle.ParseProblems, x => x.RawName == "bad\u0001.md");
    }

    [Fact]
    public async Task Exists_matches_the_key_exactly()
    {
        using var p = S3Test.Provider(S3Test.Serving(S3Test.Objects(("kb/a.md.bak", "x"), ("kb/b.md", "x"))));
        Assert.False(await p.ExistsAsync(BundleKey.Parse("a.md"), default));
        Assert.True(await p.ExistsAsync(BundleKey.Parse("b.md"), default));
    }

    [Fact]
    public async Task Exists_on_a_refusal_never_answers_false()
    {
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.Forbidden, FakeS3Handler.Error("AccessDenied"))) };
        using var p = S3Test.Provider(fake);
        await Assert.ThrowsAsync<S3RequestException>(async () => await p.ExistsAsync(BundleKey.Parse("a.md"), default));
    }

    [Fact]
    public async Task GetVersion_bounds_its_own_walk()
    {
        var objects = Enumerable.Range(0, 20).ToDictionary(i => $"kb/d/{i}/x.md", _ => "x"u8.ToArray(), StringComparer.Ordinal);
        using var p = S3Test.Provider(S3Test.Serving(objects));
        await Assert.ThrowsAsync<BundleLimitExceededException>(async () => await p.GetVersionAsync(BundleLimits.Default with { MaxEntries = 10 }, default));
        await Assert.ThrowsAsync<BundleLimitExceededException>(async () => await p.GetVersionAsync(BundleLimits.Default with { MaxDepth = 2 }, default));
    }
}
```

(The "oversized-page" case returns 1 001 entries for a request with `max-keys=1000`.)

`BundleTokenTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec §4.5: the s3v1 encoding, octet by octet.</summary>
public class BundleTokenTests
{
    private static string H(params (string, string, long)[] e) => BundleTokenHasher.Hash(e);

    [Fact]
    public void The_encoding_is_fixed_by_a_vector()
    {
        // stream = len("a.md")=4 | "a.md" | len("\"e\"")=3 | "\"e\"" | len("1")=1 | "1", lengths as 4-byte big-endian
        var expected = "s3v1:" + Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            [0, 0, 0, 4, (byte)'a', (byte)'.', (byte)'m', (byte)'d', 0, 0, 0, 3, (byte)'"', (byte)'e', (byte)'"', 0, 0, 0, 1, (byte)'1']));
        Assert.Equal(expected, H(("a.md", "\"e\"", 1)));
    }

    [Fact]
    public void Every_change_of_the_triplet_changes_the_token()
    {
        var baseline = H(("a.md", "\"e\"", 1), ("b.md", "\"f\"", 2));
        Assert.NotEqual(baseline, H(("a.md", "\"e\"", 1)));
        Assert.NotEqual(baseline, H(("a.md", "\"e\"", 1), ("b.md", "\"f\"", 2), ("c.md", "\"g\"", 3)));
        Assert.NotEqual(baseline, H(("a.md", "\"e\"", 1), ("c.md", "\"f\"", 2)));
        Assert.NotEqual(baseline, H(("a.md", "\"e\"", 1), ("b.md", "\"f\"", 3)));
        Assert.NotEqual(baseline, H(("a.md", "\"e\"", 1), ("b.md", "\"x\"", 2)));
        Assert.NotEqual(H(("ab", "\"c\"", 1)), H(("a", "b\"c\"", 1))); // field framing: no concatenation alias
    }

    [Fact]
    public void Culture_does_not_change_the_token()
    {
        var before = H(("a.md", "\"e\"", 1234567));
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("ar-SA");
        try
        {
            Assert.Equal(before, H(("a.md", "\"e\"", 1234567)));
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }
}
```

Also add to `ListingWalkerTests` a page-split invariance test: the same 300 objects served with `pageSize: 7` and `pageSize: 1000` give the same `GetVersionAsync` token.

- [ ] **Step 6: Run them and watch them fail to compile**

Run: `dotnet build OKF4net.sln`
Expected: FAIL — `BundleTokenHasher`, `S3BundleProvider` missing.

- [ ] **Step 7: Write `BundleTokenHasher`, `ListingWalker` and the provider's listing members**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OKF4net.Providers.S3.Internal;

/// <summary>The bundle token "s3v1:" + SHA-256 of length-framed (name, ETag, size) triplets in listing order (spec §4.5).</summary>
internal sealed class BundleTokenHasher : IDisposable
{
    private readonly IncrementalHash _hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    public void Add(string rawName, string etag, long size)
    {
        Field(Encoding.UTF8.GetBytes(rawName));
        Field(Encoding.UTF8.GetBytes(etag));
        Field(Encoding.ASCII.GetBytes(size.ToString(CultureInfo.InvariantCulture)));
    }

    public string Finish() => "s3v1:" + Convert.ToHexStringLower(_hash.GetHashAndReset());

    public void Dispose() => _hash.Dispose();

    public static string Hash(IEnumerable<(string RawName, string ETag, long Size)> entries)
    {
        using var h = new BundleTokenHasher();
        foreach (var (n, e, s) in entries)
        {
            h.Add(n, e, s);
        }

        return h.Finish();
    }

    private void Field(byte[] bytes)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, checked((uint)bytes.Length));
        _hash.AppendData(length);
        _hash.AppendData(bytes);
    }
}
```

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.CompilerServices;
using Amazon.S3;
using Amazon.S3.Model;

namespace OKF4net.Providers.S3.Internal;

/// <summary>
/// The private entry iterator (spec §4.3): one ListObjectsV2 page per operation scope, parsed from the raw bytes
/// the bounding handler captured; every D9 invariant checked; pages fetched lazily so the core's entry bound stops
/// the walk by disposing the enumerator.
/// </summary>
internal sealed class ListingWalker(AmazonS3Client client, string bucket, OperationGate gate, CredentialResolver credentials)
{
    public async IAsyncEnumerable<S3Entry> WalkAsync(string listPrefix, int maxKeys, long maxPages, TimeSpan timeout, S3Operation operation, [EnumeratorCancellation] CancellationToken ct)
    {
        string? token = null;
        string? previous = null;
        var seenTokens = new HashSet<string>(StringComparer.Ordinal);
        for (long page = 0; ; page++)
        {
            if (page >= maxPages)
            {
                throw new S3RequestException(operation, S3FailureKind.InvalidResponse, null, null);
            }

            var parsed = await FetchAsync(listPrefix, maxKeys, token, timeout, operation, ct).ConfigureAwait(false);
            if (parsed.Entries.Count > maxKeys || (parsed.IsTruncated && parsed.Entries.Count == 0))
            {
                throw new S3RequestException(operation, S3FailureKind.InvalidResponse, null, null);
            }

            foreach (var entry in parsed.Entries)
            {
                if (!entry.S3Key.StartsWith(listPrefix, StringComparison.Ordinal)
                    || (previous is not null && S3KeyCodec.CompareByCodePoint(previous, entry.S3Key) >= 0))
                {
                    throw new S3RequestException(operation, S3FailureKind.InvalidResponse, null, null);
                }

                previous = entry.S3Key;
                yield return entry;
            }

            if (!parsed.IsTruncated)
            {
                yield break;
            }

            if (!seenTokens.Add(parsed.NextToken!))
            {
                throw new S3RequestException(operation, S3FailureKind.InvalidResponse, null, null);
            }

            token = parsed.NextToken;
        }
    }

    private async Task<ListPage> FetchAsync(string listPrefix, int maxKeys, string? token, TimeSpan timeout, S3Operation operation, CancellationToken ct)
    {
        await using var scope = await gate.EnterAsync(operation, timeout, ct, null).ConfigureAwait(false);
        var capture = new ListPageCapture();
        try
        {
            var context = new OperationContext(operation) { ExpectedListPrefix = listPrefix, Capture = capture };
            OperationContext.Current.Value = context;
            context.Credentials = await credentials.ResolveAsync(scope).ConfigureAwait(false);
            _ = await client.ListObjectsV2Async(new ListObjectsV2Request
            {
                BucketName = bucket,
                Prefix = listPrefix,
                MaxKeys = maxKeys,
                ContinuationToken = token,
                EncodingType = EncodingType.Url,
            }, scope.Token).ConfigureAwait(false);
            return ListPageParser.Parse(capture.Body ?? throw new GuardException(S3FailureKind.InvalidResponse));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw scope.Fail(e);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }
}
```

`S3BundleProvider.cs` (listing members now; `ReadAsync` in Task 8):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Runtime.CompilerServices;
using Amazon.Runtime;
using Amazon.S3;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Providers.S3;

/// <summary>
/// A read-only, asynchronous bundle provider over Amazon S3 or an S3-compatible store (#142; spec of 2026-11-16).
/// The provider builds and owns its S3 client: the host gives credentials and an <see cref="S3BundleSource"/>, never
/// a client, so the request guard (one authority, one path prefix) holds on every request. Object versions are the
/// SHA-256 of the bytes read, in the filesystem provider's format; the bundle token hashes the listing.
/// <para>Not <see cref="BundleProviderCapabilities.CompletesSynchronously"/>: the library's synchronous entry points
/// refuse it. The bucket never appears in <see cref="Location"/> nor in any message.</para>
/// </summary>
public sealed class S3BundleProvider : IBundleProvider, IDisposable
{
    private const int PageKeys = 1000;
    private readonly NormalizedSource _source;
    private readonly AddressingPlan _plan;
    private readonly AmazonS3Client _client;
    private readonly HttpClient _http;
    private readonly OperationGate _gate;
    private readonly CredentialResolver _credentials;
    private readonly ListingWalker _walker;
    private int _disposed;

    /// <summary>Serves the bundle <paramref name="source"/> names, reading with <paramref name="credentials"/>. No I/O.</summary>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="source"/> is outside the documented domain (see <see cref="S3BundleSource"/>).</exception>
    public S3BundleProvider(AWSCredentials credentials, S3BundleSource source)
        : this(credentials, source, ClientBuilder.Network(), TimeProvider.System)
    {
    }

    /// <summary>Tests only: the network handler is replaced; the guard is still above it.</summary>
    internal S3BundleProvider(AWSCredentials credentials, S3BundleSource source, HttpMessageHandler network, TimeProvider? time = null)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(network);
        _source = SourceRules.Normalize(source);
        _plan = AddressingPlan.For(_source);
        _client = ClientBuilder.Build(new SnapshotCredentials(), _source, network, out _http);
        _gate = new OperationGate(_source.MaxConcurrentRequests, time ?? TimeProvider.System);
        _credentials = new CredentialResolver(credentials);
        _walker = new ListingWalker(_client, _source.Bucket, _gate, _credentials);
        Id = _source.Id;
        Location = _source.Location;
    }

    /// <inheritdoc/>
    public string Id { get; }

    /// <inheritdoc/>
    public string Location { get; }

    /// <inheritdoc/>
    public BundleProviderCapabilities Capabilities { get; } = new(CanWrite: false, CompletesSynchronously: false);

    /// <summary>Only <see cref="BundleKeyConstraints.WellFormedUnicode"/>: S3 stores UTF-8 names, case- and form-sensitive.</summary>
    public BundleKeyConstraints NativeConstraints => BundleKeyConstraints.WellFormedUnicode;

    /// <inheritdoc/>
    public async IAsyncEnumerable<BundleListing> EnumerateAsync(BundleKey? prefix, BundleLimits limits, [EnumeratorCancellation] CancellationToken ct)
    {
        ThrowIfDisposed(S3Operation.List);
        var keyPrefix = _plan.KeyPrefix;
        await foreach (var entry in _walker.WalkAsync(_plan.ListPrefix(prefix), PageKeys, (long)limits.MaxEntries + 1, ListTimeout(limits), S3Operation.List, ct).ConfigureAwait(false))
        {
            yield return new BundleListing(entry.S3Key[keyPrefix.Length..], entry.Size, null);
        }
    }

    /// <inheritdoc/>
    public async ValueTask<BundleVersion> GetVersionAsync(BundleLimits limits, CancellationToken ct)
    {
        ThrowIfDisposed(S3Operation.List);
        using var hasher = new BundleTokenHasher();
        var count = 0L;
        var keyPrefix = _plan.KeyPrefix;
        await foreach (var entry in _walker.WalkAsync(_plan.KeyPrefix, PageKeys, (long)limits.MaxEntries + 1, ListTimeout(limits), S3Operation.List, ct).ConfigureAwait(false))
        {
            if (++count > limits.MaxEntries)
            {
                throw new BundleLimitExceededException(BundleBound.Entries, limits.MaxEntries);
            }

            var raw = entry.S3Key[keyPrefix.Length..];
            if (raw.Count(c => c == '/') + 1 > limits.MaxDepth)
            {
                throw new BundleLimitExceededException(BundleBound.Depth, limits.MaxDepth);
            }

            hasher.Add(raw, entry.ETag, entry.Size);
        }

        return new BundleVersion(hasher.Finish());
    }

    /// <inheritdoc/>
    public async ValueTask<bool> ExistsAsync(BundleKey key, CancellationToken ct)
    {
        ThrowIfDisposed(S3Operation.Exists);
        var s3Key = _plan.S3Key(key);
        await foreach (var entry in _walker.WalkAsync(s3Key, 1, 1, _source.RequestTimeout, S3Operation.Exists, ct).ConfigureAwait(false))
        {
            return string.Equals(entry.S3Key, s3Key, StringComparison.Ordinal);
        }

        return false;
    }

    /// <inheritdoc/>
    public ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct) =>
        throw new NotImplementedException("Task 8");

    /// <inheritdoc/>
    public ValueTask<BundleObjectVersion> WriteAsync(BundleKey key, ReadOnlyMemory<byte> content, BundleWriteCondition condition, CancellationToken ct) =>
        ValueTask.FromException<BundleObjectVersion>(new BundleReadOnlyException(Id));

    /// <inheritdoc/>
    public ValueTask DeleteAsync(BundleKey key, BundleWriteCondition condition, CancellationToken ct) =>
        ValueTask.FromException(new BundleReadOnlyException(Id));

    /// <summary>Releases, once, the S3 client and the HttpClient this provider built.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _client.Dispose();
            _http.Dispose();
        }
    }

    private TimeSpan ListTimeout(BundleLimits limits) => limits.RequestTimeout < _source.RequestTimeout ? limits.RequestTimeout : _source.RequestTimeout;

    private void ThrowIfDisposed(S3Operation operation)
    {
        if (Volatile.Read(ref _disposed) != 0)
        {
            throw new S3RequestException(operation, S3FailureKind.Disposed, null, null);
        }
    }
}
```

Two constraints the walker's `maxPages` encodes: for `ExistsAsync`, a single page of one key (`maxKeys: 1, maxPages: 1`) — a truncated first page is fine, the walker returns at the first entry. For `ExistsAsync` the walker must not refuse a truncated page as "past maxPages" before yielding its first entry: the `page >= maxPages` check runs **before** fetching, so the first page is always fetched — correct. `ThrowIfDisposed` throws synchronously from an async member, i.e. through the task — consistent with the contract.

Note: the `S3Test.Provider` helper uses the internal constructor `new(creds, source, fake)`; the `TimeProvider` parameter defaults to null.

- [ ] **Step 8: Run the tests**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Providers.S3"`
Expected: PASS except tests that call `ReadAsync` (none yet in this task's files; `A_badly_named_but_well_transported_key_is_a_local_problem` and the entry-bound test load a bundle and therefore read — mark those two `[Fact(Skip = "Task 8: ReadAsync")]` now and remove the `Skip` in Task 8's Step 1; this is the only temporary skip in the plan).

- [ ] **Step 9: Full checks and commit**

```bash
dotnet build OKF4net.sln && dotnet test OKF4net.sln --filter "Category!=ContainerIntegration&Category!=S3Integration" && dotnet format OKF4net.sln --verify-no-changes
git add src/OKF4net.Providers.S3 tests/OKF4net.Tests/Providers/S3
git commit -m "feat(s3): strict key decoding, page parser, listing walker with D9 invariants, s3v1 token (#142)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 8: Reads and the error table

**Files:**
- Modify: `src/OKF4net.Providers.S3/S3BundleProvider.cs` (`ReadAsync`)
- Modify: `src/OKF4net.Providers.S3/Internal/ErrorClassifier.cs` (replace the stub)
- Test: `tests/OKF4net.Tests/Providers/S3/ReadTests.cs`, `tests/OKF4net.Tests/Providers/S3/ErrorTests.cs`; remove the two `Skip`s of Task 7

**Interfaces:**
- Consumes: everything above; `FileSystemBundleProvider` (version format comparison).
- Produces: `ReadAsync` per spec §4.3; `ErrorClassifier.Classify(S3Operation, BundleKey?, Exception) : Exception` per spec §4.6.

- [ ] **Step 1: Write the failing tests**

`ReadTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using System.Security.Cryptography;
using Amazon.Runtime;
using OKF4net.Providers.S3;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec D8, §4.3 ReadAsync.</summary>
public class ReadTests
{
    private static async Task<byte[]> Bytes(BundleObject o)
    {
        using var ms = new MemoryStream();
        await o.Content.CopyToAsync(ms);
        return ms.ToArray();
    }

    [Fact]
    public async Task The_version_is_the_filesystem_providers_format_for_the_same_bytes()
    {
        using var tmp = new TempDir();
        File.WriteAllText(Path.Combine(tmp.Path, "a.md"), "hello\n");
        var fs = await new FileSystemBundleProvider(tmp.Path).ReadAsync(BundleKey.Parse("a.md"), null, default);
        using var p = S3Test.Provider(S3Test.Serving(S3Test.Objects(("kb/a.md", "hello\n"))));
        using var o = await p.ReadAsync(BundleKey.Parse("a.md"), null, default);
        Assert.Equal(fs.Version, o.Version);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData("hello\n"u8)), o.Version.Token);
        Assert.Equal("hello\n"u8.ToArray(), await Bytes(o));
        Assert.False(o.Content.CanWrite);
        Assert.Equal(6, o.Length);
    }

    [Fact]
    public async Task An_expected_version_is_checked_on_the_bytes_not_the_etag()
    {
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("other", null, "text/plain"), Headers = { ETag = new System.Net.Http.Headers.EntityTagHeaderValue("\"same\"") } }) };
        using var p = S3Test.Provider(fake);
        var expected = new BundleObjectVersion(Convert.ToHexStringLower(SHA256.HashData("original"u8)));
        await Assert.ThrowsAsync<BundleVersionMismatchException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), expected, default));
        Assert.DoesNotContain(fake.Requests, r => r.Headers.IfMatch.Count > 0); // the store's If-Match is not trusted, nor sent
    }

    [Fact]
    public async Task A_declared_length_past_the_bound_is_refused_without_reading_the_body()
    {
        var read = false;
        var fake = new FakeS3Handler
        {
            Respond = (_, _) =>
            {
                var r = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ObservingStream(() => read = true)) };
                r.Content.Headers.ContentLength = 11;
                return Task.FromResult(r);
            },
        };
        using var p = S3Test.Provider(fake, S3Test.Local() with { MaxObjectBytes = 10 });
        await Assert.ThrowsAsync<BundleObjectTooLargeException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        Assert.False(read);
    }

    [Fact]
    public async Task A_body_longer_than_its_bound_without_length_is_refused_at_the_bound()
    {
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new MemoryStream(new byte[11])) }) };
        using var p = S3Test.Provider(fake, S3Test.Local() with { MaxObjectBytes = 10 });
        await Assert.ThrowsAsync<BundleObjectTooLargeException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
    }

    [Fact]
    public async Task A_stalled_body_is_a_Timeout_within_the_deadline()
    {
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(10)) }) };
        using var p = S3Test.Provider(fake, S3Test.Local() with { RequestTimeout = TimeSpan.FromMilliseconds(300) });
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var e = await Assert.ThrowsAsync<S3RequestException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        Assert.Equal(S3FailureKind.Timeout, e.Kind);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_caller_cancellation_stays_a_cancellation()
    {
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new StallingStream(10)) }) };
        using var p = S3Test.Provider(fake);
        using var cts = new CancellationTokenSource(200);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, cts.Token));
    }

    [Fact]
    public async Task Retries_and_redirects_sign_with_the_operation_snapshot()
    {
        var calls = 0;
        var host = new CountingCredentials(() => Interlocked.Increment(ref calls) == 1 ? new ImmutableCredentials("A", "S", "T") : throw new InvalidOperationException("called from the pipeline"));
        var attempts = 0;
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(Interlocked.Increment(ref attempts) < 3 ? FakeS3Handler.Xml(HttpStatusCode.ServiceUnavailable, FakeS3Handler.Error("SlowDown")) : new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent("x"u8.ToArray()) }) };
        using var p = S3Test.Provider(fake, creds: host);
        using var o = await p.ReadAsync(BundleKey.Parse("a.md"), null, default);
        Assert.Equal(3, attempts);
        Assert.Equal(1, Volatile.Read(ref calls));
        Assert.All(fake.Requests, r => Assert.Contains("Credential=A/", r.Headers.Authorization!.Parameter, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_disposed_provider_fails_as_Disposed()
    {
        var p = S3Test.Provider(S3Test.Serving(S3Test.Objects(("kb/a.md", "x"))));
        p.Dispose();
        p.Dispose(); // once
        Assert.Equal(S3FailureKind.Disposed, (await Assert.ThrowsAsync<S3RequestException>(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default))).Kind);
    }

    private sealed class CountingCredentials(Func<ImmutableCredentials> get) : AWSCredentials
    {
        public override ImmutableCredentials GetCredentials() => get();
    }

    private sealed class ObservingStream(Action onRead) : MemoryStream(new byte[11])
    {
        public override int Read(byte[] buffer, int offset, int count) { onRead(); return base.Read(buffer, offset, count); }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) { onRead(); return base.ReadAsync(buffer, ct); }
    }

    /// <summary>Gives <paramref name="first"/> bytes, then blocks until cancelled.</summary>
    private sealed class StallingStream(int first) : Stream
    {
        private int _left = first;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_left > 0) { var n = Math.Min(_left, buffer.Length); _left -= n; return n; }
            await Task.Delay(Timeout.Infinite, ct);
            return 0;
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
```

`ErrorTests.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using OKF4net.Providers.S3;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec §4.6: the closed table, by the real SDK pipeline; closed-vocabulary messages.</summary>
public class ErrorTests
{
    private const string Sentinel = "SENTINEL-bucket.s3.example-REQ123";

    public static TheoryData<HttpStatusCode, string, Type, S3FailureKind?> Table => new()
    {
        { HttpStatusCode.NotFound, "NoSuchKey", typeof(BundleObjectNotFoundException), null },
        { HttpStatusCode.Forbidden, "AccessDenied", typeof(BundleAccessRefusedException), null },
        { HttpStatusCode.Forbidden, "InvalidAccessKeyId", typeof(S3RequestException), S3FailureKind.Credentials },
        { HttpStatusCode.Forbidden, "SignatureDoesNotMatch", typeof(S3RequestException), S3FailureKind.Credentials },
        { HttpStatusCode.BadRequest, "ExpiredToken", typeof(S3RequestException), S3FailureKind.Credentials },
        { HttpStatusCode.NotFound, "NoSuchBucket", typeof(S3RequestException), S3FailureKind.BucketUnavailable },
        { HttpStatusCode.MovedPermanently, "PermanentRedirect", typeof(S3RequestException), S3FailureKind.BucketUnavailable },
        { HttpStatusCode.BadRequest, "AuthorizationHeaderMalformed", typeof(S3RequestException), S3FailureKind.BucketUnavailable },
        { HttpStatusCode.ServiceUnavailable, "SlowDown", typeof(S3RequestException), S3FailureKind.Throttled },
        { (HttpStatusCode)429, "TooManyRequests", typeof(S3RequestException), S3FailureKind.Throttled },
        { HttpStatusCode.InternalServerError, "InternalError", typeof(S3RequestException), S3FailureKind.ServerError },
        { HttpStatusCode.Conflict, "Whatever", typeof(S3RequestException), S3FailureKind.ClientError },
        { HttpStatusCode.Forbidden, "Unknown" + Sentinel, typeof(S3RequestException), S3FailureKind.ClientError },
        { HttpStatusCode.Found, "Moved", typeof(S3RequestException), S3FailureKind.InvalidResponse },
        { HttpStatusCode.PermanentRedirect, "Moved", typeof(S3RequestException), S3FailureKind.InvalidResponse },
        { (HttpStatusCode)599, "Odd", typeof(S3RequestException), S3FailureKind.ServerError },
    };

    [Theory]
    [MemberData(nameof(Table))]
    public async Task Each_row_of_the_table(HttpStatusCode status, string code, Type expected, S3FailureKind? kind)
    {
        var fake = new FakeS3Handler
        {
            Respond = (_, _) => Task.FromResult(FakeS3Handler.Xml(status,
                $"<?xml version=\"1.0\"?><Error><Code>{code}</Code><Message>{Sentinel}\n https://{Sentinel}/x</Message><RequestId>{Sentinel}</RequestId><HostId>{Sentinel}</HostId><Endpoint>{Sentinel}</Endpoint><Region>{Sentinel}</Region></Error>",
                r => r.Headers.TryAddWithoutValidation("x-amz-request-id", Sentinel))),
        };
        using var p = S3Test.Provider(fake);
        var e = await Record.ExceptionAsync(async () => await p.ReadAsync(BundleKey.Parse("a.md"), null, default));
        Assert.IsType(expected, e);
        if (kind is { } k)
        {
            Assert.Equal(k, ((S3RequestException)e!).Kind);
        }

        Assert.DoesNotContain(Sentinel, e!.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("okf", e.Message, StringComparison.Ordinal); // the bucket
        Assert.DoesNotContain("127.0.0.1", e.Message, StringComparison.Ordinal); // the endpoint
        Assert.DoesNotContain(code, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_307_without_Location_is_InvalidResponse() =>
        Assert.Equal(S3FailureKind.InvalidResponse, (await Assert.ThrowsAsync<S3RequestException>(async () =>
            await S3Test.Provider(new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TemporaryRedirect)) })
                .ReadAsync(BundleKey.Parse("a.md"), null, default))).Kind);

    [Fact]
    public async Task A_transport_failure_after_the_headers_is_Transport()
    {
        var fake = new FakeS3Handler { Respond = (_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ThrowingStream()) }) };
        Assert.Equal(S3FailureKind.Transport, (await Assert.ThrowsAsync<S3RequestException>(async () => await S3Test.Provider(fake).ReadAsync(BundleKey.Parse("a.md"), null, default))).Kind);
    }

    [Fact]
    public async Task A_credentials_failure_during_the_survey_fails_the_load()
    {
        const string computation = "---\ntype: computation\ntitle: C\ndescription: D.\nruntime: python\ncomputation: ./c.py\nattester:\n  resource: ./att.py\n---\n\nBody\n";
        var forbidden = false;
        var objects = S3Test.Objects(("kb/c.md", computation), ("kb/c.py", "print(1)\n"), ("kb/att.py", "def attest(r): return True\n"));
        var serving = S3Test.Serving(objects);
        var fake = new FakeS3Handler
        {
            Respond = (req, ct) =>
            {
                if (req.RequestUri!.AbsolutePath.EndsWith("c.md", StringComparison.Ordinal))
                {
                    forbidden = true; // credentials expire right after the concepts are read
                }
                else if (forbidden && !req.RequestUri.Query.Contains("list-type", StringComparison.Ordinal))
                {
                    return Task.FromResult(FakeS3Handler.Xml(HttpStatusCode.Forbidden, FakeS3Handler.Error("ExpiredToken")));
                }

                return serving.Respond(req, ct);
            },
        };
        using var p = S3Test.Provider(fake);
        var e = await Assert.ThrowsAsync<BundleLoadException>(async () => await Bundle.LoadAsync(p));
        Assert.IsType<S3RequestException>(e.InnerException);
    }

    private sealed class ThrowingStream : MemoryStream
    {
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => ValueTask.FromException<int>(new IOException("reset " + Sentinel));
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("reset " + Sentinel);
    }
}
```

(The survey test's concept shape must be one the core surveys: check `tests/fixtures/okf_v02_computation/` for a minimal §10 concept and copy its frontmatter keys exactly; replace the inline `computation` string with that shape if it differs.)

- [ ] **Step 2: Run them and watch them fail**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ReadTests|FullyQualifiedName~ErrorTests"`
Expected: FAIL — `ReadAsync` throws `NotImplementedException`.

- [ ] **Step 3: Write `ReadAsync`**

Replace the placeholder in `S3BundleProvider`:

```csharp
    /// <inheritdoc/>
    public async ValueTask<BundleObject> ReadAsync(BundleKey key, BundleObjectVersion? expected, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(key);
        ThrowIfDisposed(S3Operation.Read);
        var s3Key = _plan.S3Key(key);
        await using var scope = await _gate.EnterAsync(S3Operation.Read, _source.RequestTimeout, ct, key).ConfigureAwait(false);
        var context = new OperationContext(S3Operation.Read) { ExpectedObjectS3Key = s3Key };
        OperationContext.Current.Value = context;
        try
        {
            context.Credentials = await _credentials.ResolveAsync(scope).ConfigureAwait(false);
            using var response = await _client.GetObjectAsync(new Amazon.S3.Model.GetObjectRequest { BucketName = _source.Bucket, Key = s3Key }, scope.Token).ConfigureAwait(false);
            if (response.ContentLength > _source.MaxObjectBytes)
            {
                throw new BundleObjectTooLargeException(key, response.ContentLength, _source.MaxObjectBytes);
            }

            // A stalled body: cancelling the token may not unblock the SDK's stream (gate G6), disposing the response does.
            using var abort = scope.Token.Register(static r => ((IDisposable)r!).Dispose(), response);
            var bytes = await ReadAllAsync(response.ResponseStream, response.ContentLength, key, scope.Token).ConfigureAwait(false);
            var version = new BundleObjectVersion(Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes.AsSpan())));
            if (expected is { } wanted && wanted != version)
            {
                throw new BundleVersionMismatchException(key);
            }

            return new BundleObject(new MemoryStream(bytes.Array!, bytes.Offset, bytes.Count, writable: false), version, bytes.Count);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            throw scope.Fail(e);
        }
        finally
        {
            OperationContext.Current.Value = null;
        }
    }

    /// <summary>Reads the whole body into one buffer sized by Content-Length when known, else grown by doubling, never past MaxObjectBytes.</summary>
    private async Task<ArraySegment<byte>> ReadAllAsync(Stream body, long declared, BundleKey key, CancellationToken ct)
    {
        var max = _source.MaxObjectBytes;
        var buffer = new byte[declared is > 0 ? (int)declared + 1 : 81920];
        var total = 0;
        while (true)
        {
            if (total == buffer.Length)
            {
                if (buffer.Length > max)
                {
                    throw new BundleObjectTooLargeException(key, total, max);
                }

                Array.Resize(ref buffer, (int)Math.Min((long)buffer.Length * 2, max + 1));
            }

            var n = await body.ReadAsync(buffer.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0)
            {
                return new ArraySegment<byte>(buffer, 0, total);
            }

            total += n;
            if (total > max)
            {
                throw new BundleObjectTooLargeException(key, total, max);
            }
        }
    }
```

(When `declared` is known, the buffer is `declared + 1` so the final zero-byte read confirms the end without a resize; a body that disagrees with its `Content-Length` is a transport error raised by `HttpClient` itself and classified `Transport`. The `MemoryStream` over `buffer` is the one copy the provider holds; `Array.MaxLength` caps `max`, so the `(int)` casts are safe.)

- [ ] **Step 4: Write `ErrorClassifier`**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using Amazon.Runtime;
using Amazon.S3;

namespace OKF4net.Providers.S3.Internal;

/// <summary>The closed table of spec §4.6. A pair (status, code) it does not recognize falls into its status's row; anything not from the service is Transport.</summary>
internal static class ErrorClassifier
{
    private static readonly HashSet<string> CredentialCodes = new(StringComparer.Ordinal)
        { "InvalidAccessKeyId", "SignatureDoesNotMatch", "ExpiredToken", "InvalidToken", "TokenRefreshRequired" };

    private static readonly HashSet<string> BucketCodes = new(StringComparer.Ordinal)
        { "NoSuchBucket", "PermanentRedirect", "AuthorizationHeaderMalformed", "IllegalLocationConstraintException" };

    public static Exception Classify(S3Operation operation, BundleKey? key, Exception e)
    {
        var service = e as AmazonServiceException ?? e.InnerException as AmazonServiceException;
        if (service is null)
        {
            return e switch
            {
                GuardException g => new S3RequestException(operation, g.Kind, null, key, g),
                AmazonClientException c when IsChecksum(c) => new S3RequestException(operation, S3FailureKind.Integrity, null, key, c),
                _ => new S3RequestException(operation, S3FailureKind.Transport, null, key, e),
            };
        }

        var status = (int)service.StatusCode;
        var code = service.ErrorCode ?? "";
        if (operation == S3Operation.Read && key is not null)
        {
            if (status == 404 && code == "NoSuchKey")
            {
                return new BundleObjectNotFoundException(key);
            }

            if (status == 403 && code == "AccessDenied")
            {
                return new BundleAccessRefusedException(key, "access denied");
            }
        }

        var kind = CredentialCodes.Contains(code) ? S3FailureKind.Credentials
            : BucketCodes.Contains(code) || (status is 301 && code.Length == 0) ? S3FailureKind.BucketUnavailable
            : status is 503 && code == "SlowDown" || status == 429 ? S3FailureKind.Throttled
            : status >= 500 ? S3FailureKind.ServerError
            : status >= 400 ? S3FailureKind.ClientError
            : S3FailureKind.InvalidResponse; // 3xx not followed, and the terminal rule
        return new S3RequestException(operation, kind, status, key, service);
    }

    private static bool IsChecksum(AmazonClientException c) =>
        c.GetType().Name.Contains("Checksum", StringComparison.Ordinal) || c.Message.Contains("checksum", StringComparison.OrdinalIgnoreCase);
}
```

(`IsChecksum` matches the exception gate G7 recorded; if G7 recorded a specific type, test `c is <that type>` instead of the name and message heuristics.)

- [ ] **Step 5: Run the tests, remove Task 7's two skips, run again**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~OKF4net.Tests.Providers.S3"`
Expected: PASS, zero skipped.

- [ ] **Step 6: Full checks and commit**

```bash
dotnet build OKF4net.sln && dotnet test OKF4net.sln --filter "Category!=ContainerIntegration&Category!=S3Integration" && dotnet format OKF4net.sln --verify-no-changes
git add src/OKF4net.Providers.S3 tests/OKF4net.Tests/Providers/S3
git commit -m "feat(s3): buffered reads versioned by SHA-256, closed error table (#142)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 9: Consumers, the synchronous bridge, D7, no residual file I/O, messages everywhere

**Files:**
- Test: `tests/OKF4net.Tests/Providers/S3/ConsumersTests.cs`

**Interfaces:**
- Consumes: `S3BundleProvider`, `BundleConceptWriter(IBundleProvider)`, `IndexGenerator.RegenerateIndexesAsync`, `OkfBundleTools(BundleHandle)`, `BundleValidator.ValidateAsync`, `CurrentDirectoryCollection`, `FileKnowledgeCatalog` with a host `IBundleProviderFactory` (see `tests/OKF4net.Tests/Catalog/` for an existing factory test to copy the catalog set-up from).
- Produces: tests only (any failure here is a bug in Tasks 4–8: fix it there, in a separate commit).

- [ ] **Step 1: Write the tests**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using OKF4net.Agents;
using OKF4net.Providers.S3;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec T1: consumers, bridge, D7 of #141, no residual file I/O, messages.</summary>
#pragma warning disable CS0618 // the [Obsolete] path members and string tool bridges are exercised on purpose
[Collection(CurrentDirectoryCollection.Name)]
public class ConsumersTests
{
    private const string Fm = "type: Note\ntitle: New\ndescription: A new note.";
    private const string Note = "---\ntype: Note\ntitle: First\ndescription: The first note.\nsources:\n  - refs/q.sql\n---\n\nLinks to [b](b.md).\n";

    private static S3BundleProvider Provider(FakeS3Handler? fake = null) =>
        S3Test.Provider(fake ?? S3Test.Serving(S3Test.Objects(("kb/index.md", "# kb\n"), ("kb/a.md", Note), ("kb/b.md", Note.Replace("First", "Second", StringComparison.Ordinal)), ("kb/refs/q.sql", "SELECT 1\n"))));

    [Fact]
    public async Task Every_write_path_refuses_and_sends_nothing()
    {
        var fake = S3Test.Serving(S3Test.Objects(("kb/a.md", Note)));
        using var p = Provider(fake);
        await Assert.ThrowsAsync<BundleReadOnlyException>(async () => await p.WriteAsync(BundleKey.Parse("x.md"), "x"u8.ToArray(), BundleWriteCondition.None, default));
        await Assert.ThrowsAsync<BundleReadOnlyException>(async () => await p.DeleteAsync(BundleKey.Parse("a.md"), BundleWriteCondition.None, default));
        var writer = new BundleConceptWriter(p);
        await Assert.ThrowsAsync<BundleReadOnlyException>(async () => await writer.WriteConceptAsync("notes/c", Fm, "C\n"));
        await Assert.ThrowsAsync<BundleReadOnlyException>(async () => await writer.AppendLogAsync("Update", "x"));
        await Assert.ThrowsAsync<BundleReadOnlyException>(async () => await IndexGenerator.RegenerateIndexesAsync(p));
        Assert.DoesNotContain(fake.Requests, r => r.Method != HttpMethod.Get);
    }

    [Fact]
    public void The_tool_set_offers_no_write_tool()
    {
        using var handle = new BundleHandle(Provider());
        var names = new OkfBundleTools(handle).GetTools(OkfToolMode.ReadWrite).Select(t => t.Name).ToArray();
        Assert.Contains("okf_read_concept", names);
        Assert.DoesNotContain("okf_write_concept", names);
    }

    [Fact]
    public async Task A_synchronous_read_over_a_really_pending_request_is_refused_and_cancelled()
    {
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var fake = new FakeS3Handler
        {
            Respond = async (_, ct) =>
            {
                try
                {
                    await Task.Delay(Timeout.Infinite, ct);
                }
                catch (OperationCanceledException)
                {
                    cancelled.TrySetResult();
                    throw;
                }

                return new HttpResponseMessage(HttpStatusCode.OK);
            },
        };
        using var p = Provider(fake);
        var e = Assert.Throws<InvalidOperationException>(() => Bundle.Load(p));
        Assert.Equal("provider completed asynchronously; use the async overload", e.Message); // SyncBridge.IncompleteMessage (internal to the core)
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
    }

    [Fact]
    public void A_synchronous_write_is_refused_before_it_starts()
    {
        var fake = new FakeS3Handler();
        using var p = Provider(fake);
        Assert.ThrowsAny<Exception>(() => new BundleConceptWriter(p).WriteConcept("notes/c", Fm, "C\n"));
        Assert.Empty(fake.Requests);
    }

    [Fact]
    public async Task The_path_based_obsolete_members_refuse_this_provider()
    {
        var bundle = await Bundle.LoadAsync(Provider());
        var a = bundle.Concepts.Single(c => c.Id.ToString() == "a");
        Assert.Throws<NotSupportedException>(() => bundle.TryResolveResource(a, "refs/q.sql", out _, out _));
        Assert.Throws<NotSupportedException>(() => bundle.ReadResourceText("refs/q.sql"));
    }

    [Fact]
    public async Task Every_read_entry_point_leaves_an_empty_current_directory_empty()
    {
        using var empty = new TempDir();
        var previous = Directory.GetCurrentDirectory();
        Directory.SetCurrentDirectory(empty.Path);
        try
        {
            var clock = new FixedClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
            using var p = Provider();
            var bundle = await Bundle.LoadAsync(p);
            _ = await BundleValidator.ValidateAsync(bundle, clock);
            _ = ConceptAudit.Run(bundle, default, clock);
            Assert.NotEmpty(ConceptSearch.Search(bundle.Concepts, "first"));
            var (key, _) = await bundle.ResolveResourceKeyAsync(bundle.Concepts.Single(c => c.Id.ToString() == "a"), "refs/q.sql");
            Assert.Equal("SELECT 1\n", (await bundle.ReadResourceTextAsync(key!)).Text);
            using var handle = new BundleHandle(Provider());
            var tools = new OkfBundleTools(handle);
            Assert.Contains("Links to", await tools.ReadConceptAsync("a"), StringComparison.Ordinal);
            Assert.Contains("a", await tools.SearchAsync("first"), StringComparison.Ordinal);
        }
        finally
        {
            Directory.SetCurrentDirectory(previous);
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(empty.Path));
    }

    [Fact]
    public async Task Neither_validation_paths_nor_tool_output_name_the_bucket_or_the_endpoint()
    {
        using var handle = new BundleHandle(Provider());
        var bundle = await handle.GetCurrentAsync();
        var report = await BundleValidator.ValidateAsync(bundle);
        Assert.All(report.Diagnostics, d => Assert.DoesNotContain("okf", d.Path ?? "", StringComparison.Ordinal));
        Assert.All(bundle.Concepts, c => Assert.StartsWith("s3:", c.Path, StringComparison.Ordinal));
        var text = await new OkfBundleTools(handle).ValidateBundleAsync();
        Assert.DoesNotContain("127.0.0.1", text, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_segment_like_a_drive_keeps_its_exact_key()
    {
        using var p = S3Test.Provider(S3Test.Serving(S3Test.Objects(("kb/c:x.md", Note))));
        var bundle = await Bundle.LoadAsync(p);
        var c = Assert.Single(bundle.Concepts);
        Assert.Equal("c:x.md", c.Key!.ToString()); // Path is display only (spec §4.3); on Windows Path.Combine may drop the location
    }
}
#pragma warning restore CS0618
```

Add, in the same file, a catalog test: build a `FileKnowledgeCatalog` over a `catalog.json` naming one source `"provider": "s3-test"` with a test `IBundleProviderFactory` that returns `Provider()` (copy the set-up of the existing factory test under `tests/OKF4net.Tests/Catalog/` — search for `ProviderFactories`), make the fake answer every request with 503 so the source fails, and assert that no `KnowledgeDiagnostic` message contains `okf`, `127.0.0.1` or `SlowDown`.

- [ ] **Step 2: Run them**

Run: `dotnet test OKF4net.sln --filter "FullyQualifiedName~ConsumersTests"`
Expected: PASS. `Bundle.Load(IBundleProvider)` is internal to the core: if the test project cannot call it, use the public synchronous path a host has, `BundleValidator.Validate(bundle)` on a bundle loaded asynchronously from a provider whose next listing never completes, or `new BundleHandle(p).Current` through `OkfBundleTools`' `[Obsolete]` string bridge `tools.ReadConcept("a")` — whichever reaches `SyncBridge.RunCancellable` — and keep the two assertions (exact message, cancellation observed). A failure is a bug in Tasks 4–8: fix it in a separate commit with its own regression test.

- [ ] **Step 3: Full checks and commit**

```bash
dotnet build OKF4net.sln && dotnet test OKF4net.sln --filter "Category!=ContainerIntegration&Category!=S3Integration" && dotnet format OKF4net.sln --verify-no-changes
git add tests/OKF4net.Tests/Providers/S3/ConsumersTests.cs
git commit -m "test(s3): consumers, synchronous bridge, D7, no residual file I/O, bucket-free output (#142)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 10: Hostile environment and out-of-band egress, in a child process

**Files:**
- Create: `tests/S3HostileEnvProbe/S3HostileEnvProbe.csproj`, `tests/S3HostileEnvProbe/Program.cs`
- Modify: `tests/OKF4net.Tests/OKF4net.Tests.csproj` (`<ProjectReference Include="..\S3HostileEnvProbe\S3HostileEnvProbe.csproj" ReferenceOutputAssembly="false" />`, next to `YamlHostileProbe`)
- Test: `tests/OKF4net.Tests/Providers/S3/HostileEnvironmentTests.cs`

**Interfaces:**
- Consumes: the **public** `S3BundleProvider` constructor only (D10 grants internals to `OKF4net.Tests` alone), against loopback listeners the probe starts itself (a `TcpListener` playing the S3 endpoint, a UDP listener for CSM).
- Produces: the probe prints one line per request it served — `request line | sorted headers minus X-Amz-Date, amz-sdk-invocation-id, amz-sdk-request and Authorization | the Authorization credential scope (access key, date, region, service)` — then `concepts=<n>` and `UDP <count>`. The signature itself is not compared: it depends on the time; the scope, the signed path and every header that enters the signature are.

- [ ] **Step 1: Write the probe**

`S3HostileEnvProbe.csproj`:

```xml
<!-- SPDX-License-Identifier: LGPL-3.0-or-later -->
<Project Sdk="Microsoft.NET.Sdk">
  <!-- Not in OKF4net.sln: run as a child process by HostileEnvironmentTests (spec T1), so environment variables
       and ~/.aws/config can be set per run without touching the test host. -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\OKF4net.Providers.S3\OKF4net.Providers.S3.csproj" />
  </ItemGroup>
</Project>
```

`Program.cs`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon.Runtime;
using OKF4net;
using OKF4net.Providers.S3;

// Serves a three-object bucket on a loopback TcpListener, loads it through S3BundleProvider (public API only),
// prints what the provider sent, then how many UDP datagrams (CSM) arrived. Exit 0 on success.
var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, int.Parse(args[0])));
var datagrams = 0;
_ = Task.Run(async () => { while (true) { await udp.ReceiveAsync(); Interlocked.Increment(ref datagrams); } });

var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
var lines = new List<string>();
_ = Task.Run(() => Serve(listener, lines));

using var provider = new S3BundleProvider(new BasicAWSCredentials("AKIDEXAMPLE", "secret"), new S3BundleSource
{
    Bucket = "okf", Prefix = "kb", ServiceUrl = new Uri($"http://127.0.0.1:{port}"), ForcePathStyle = true, AllowInsecureHttp = true,
});
var bundle = await Bundle.LoadAsync(provider);
await Task.Delay(500); // let a CSM datagram arrive if one was sent
lock (lines)
{
    foreach (var l in lines) Console.WriteLine(l);
}

Console.WriteLine($"concepts={bundle.Concepts.Count}");
Console.WriteLine($"UDP {Volatile.Read(ref datagrams)}");
return 0;

static async Task Serve(TcpListener listener, List<string> lines)
{
    while (true)
    {
        using var client = await listener.AcceptTcpClientAsync();
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        while (await reader.ReadLineAsync() is { Length: > 0 } requestLine)
        {
            var headers = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            string? h;
            while ((h = await reader.ReadLineAsync()) is { Length: > 0 })
            {
                var i = h.IndexOf(':');
                headers[h[..i].Trim()] = h[(i + 1)..].Trim();
            }

            var kept = headers.Where(kv => kv.Key is not ("X-Amz-Date" or "amz-sdk-invocation-id" or "amz-sdk-request" or "Authorization"))
                .Select(kv => $"{kv.Key.ToLowerInvariant()}={kv.Value}");
            lock (lines)
            {
                lines.Add($"{requestLine} | {string.Join(";", kept)} | auth-scope={Scope(headers.GetValueOrDefault("Authorization"))}");
            }

            var body = requestLine.Contains("list-type=2", StringComparison.Ordinal)
                ? "<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult><EncodingType>url</EncodingType><IsTruncated>false</IsTruncated><MaxKeys>1000</MaxKeys><Contents><Key>kb/a.md</Key><ETag>\"e\"</ETag><Size>52</Size></Contents></ListBucketResult>"
                : "---\ntype: Note\ntitle: T\ndescription: D.\n---\n\nBody text.\n";
            var bytes = Encoding.UTF8.GetBytes(body);
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {bytes.Length}\r\nContent-Type: application/xml\r\n\r\n"));
            await stream.WriteAsync(bytes);
        }
    }
}

static string Scope(string? authorization) => authorization is null ? "none" : authorization.Split(',')[0]; // "AWS4-HMAC-SHA256 Credential=AKID/date/region/s3/aws4_request"
```

(Adjust `Size` to the byte length of the served body: compute it in code rather than hard-coding 52.)

- [ ] **Step 2: Write the test**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace OKF4net.Tests.Providers.S3;

/// <summary>Spec D2, D3, T1 "Environnement hostile": the provider's requests do not depend on the environment, and nothing leaves by UDP.</summary>
public class HostileEnvironmentTests
{
    private static string ProbeDll() =>
        Path.Combine(TestPaths.RepoRoot(), "tests", "S3HostileEnvProbe", "bin",
#if DEBUG
            "Debug",
#else
            "Release",
#endif
            "net10.0", "S3HostileEnvProbe.dll");

    private static (int Code, string[] Lines) Run(IDictionary<string, string> env, int udpPort)
    {
        var start = new ProcessStartInfo("dotnet") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(ProbeDll());
        start.ArgumentList.Add(udpPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        foreach (var name in start.Environment.Keys.Where(k => k.StartsWith("AWS_", StringComparison.Ordinal) || k.EndsWith("_PROXY", StringComparison.OrdinalIgnoreCase)).ToArray())
        {
            start.Environment.Remove(name);
        }

        foreach (var (k, v) in env)
        {
            start.Environment[k] = v;
        }

        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit(60_000);
        return (p.ExitCode, output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    private static int FreeUdpPort()
    {
        using var u = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    [Fact]
    public void A_hostile_environment_changes_nothing_the_provider_sends()
    {
        using var home = new TempDir();
        var config = Path.Combine(home.Path, "config");
        File.WriteAllText(config, "[default]\nendpoint_url = http://evil.example\nsdk_ua_app_id = AMBIENTMARK\nretry_mode = adaptive\nmax_attempts = 9\nuse_fips_endpoint = true\nuse_dualstack_endpoint = true\nrequest_checksum_calculation = when_required\ndefaults_mode = in-region\n");
        var udp = FreeUdpPort();
        var clean = Run(new Dictionary<string, string>(), udp);
        var hostile = Run(new Dictionary<string, string>
        {
            ["AWS_ENDPOINT_URL_S3"] = "http://evil.example", ["AWS_ENDPOINT_URL"] = "http://evil.example",
            ["AWS_USE_FIPS_ENDPOINT"] = "true", ["AWS_USE_DUALSTACK_ENDPOINT"] = "true",
            ["AWS_RETRY_MODE"] = "adaptive", ["AWS_MAX_ATTEMPTS"] = "9", ["AWS_SDK_UA_APP_ID"] = "AMBIENTMARK",
            ["AWS_CSM_ENABLED"] = "true", ["AWS_CSM_PORT"] = udp.ToString(System.Globalization.CultureInfo.InvariantCulture), ["AWS_CSM_HOST"] = "127.0.0.1",
            ["HTTPS_PROXY"] = "http://127.0.0.1:9", ["HTTP_PROXY"] = "http://127.0.0.1:9",
            ["AWS_CONFIG_FILE"] = config, ["AWS_PROFILE"] = "default",
        }, udp);

        Assert.Equal(0, clean.Code);
        Assert.Equal(0, hostile.Code);
        Assert.Equal(clean.Lines, hostile.Lines);
        Assert.DoesNotContain(hostile.Lines, l => l.Contains("AMBIENTMARK", StringComparison.Ordinal));
        Assert.Contains("UDP 0", hostile.Lines);
    }
}
```

Add to the dictionary every further variable gate G2 listed. The `user-agent` header is part of the compared lines, so `AMBIENTMARK` in either run fails the test; the probe's own `Assert.Equal` of the two line sets covers `x-amz-checksum-*` headers, the authorization scope (region) and the path.

**Out-of-band egress (region detection).** Extend the probe and the test so the region-detection transport, which bypasses the client's `HttpClientFactory` and uses the process's default proxy, is watched too: the probe starts a third loopback `TcpListener` that only counts accepted connections and prints `PROXY <count>`; the test sets `HTTP_PROXY` and `HTTPS_PROXY` (in the hostile run) to that listener's address, passed to the probe as a second argument it binds to (replace `http://127.0.0.1:9`); and the probe's fake answers the **first** `GetObject` with `400` + `<Error><Code>AuthorizationHeaderMalformed</Code><Region>eu-west-3</Region></Error>` and the header `x-amz-bucket-region: eu-west-3` (then serves normally, so the load ends with `S3RequestException` `BucketUnavailable` for that read and the probe catches it and prints `read-region=refused`). Assert `PROXY 0` and `UDP 0` in both runs, and `read-region=refused` in both. A connection counted there is a request the guard never saw: fix `ClientBuilder.ApplyGateRows` (gate G1), never the test.

- [ ] **Step 3: Run it**

Run: `dotnet build OKF4net.sln && dotnet test OKF4net.sln --filter "FullyQualifiedName~HostileEnvironmentTests"`
Expected: PASS. A failure means `ClientBuilder.ApplyGateRows` misses a row of Annexe B: fix it there (Task 5's file), never in the test.

- [ ] **Step 4: Full checks and commit**

```bash
dotnet build OKF4net.sln && dotnet test OKF4net.sln --filter "Category!=ContainerIntegration&Category!=S3Integration" && dotnet format OKF4net.sln --verify-no-changes
git add tests/S3HostileEnvProbe tests/OKF4net.Tests/OKF4net.Tests.csproj tests/OKF4net.Tests/Providers/S3/HostileEnvironmentTests.cs src/OKF4net.Providers.S3
git commit -m "test(s3): hostile environment and CSM egress, in a child process (#142)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 11: `S3Integration` — RustFS fixture and T2

**Files:**
- Create: `tests/OKF4net.Tests/Providers/S3/Integration/RustFsFixture.cs`, `S3IntegrationTests.cs`, `S3ParityTests.cs`
- Modify: `docs/superpowers/specs/2026-11-16-s3-bundle-provider-design.md` T2 row "Noms acceptés, oracle indépendant" (see Step 4)

**Interfaces:**
- Consumes: the public `S3BundleProvider`; an independent test client `new AmazonS3Client(new BasicAWSCredentials(...), new AmazonS3Config { ServiceURL = endpoint, ForcePathStyle = true })` used **only** to create the bucket, upload and list as the oracle.
- Produces: `[Trait("Category", "S3Integration")]` tests; `RustFsFixture : IAsyncLifetime` with `Uri Endpoint`, `BasicAWSCredentials Credentials`, `AmazonS3Client Admin`, `string? SkipReason`, `Task UploadDirectoryAsync(string localDir, string prefix)`, `Task PutAsync(string key, byte[] bytes)`, `Task StopAsync()`.

- [ ] **Step 1: Write the fixture**

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace OKF4net.Tests.Providers.S3.Integration;

/// <summary>
/// docker.io/rustfs/rustfs:1.0.3 on a free loopback port (spec T2). Engine chosen explicitly: podman if it answers,
/// else docker. No engine: SkipReason is set and every test reports skipped. An engine that answers but a container
/// that does not start: InitializeAsync throws, so every test FAILS.
/// </summary>
public sealed class RustFsFixture : IAsyncLifetime
{
    public const string Image = "docker.io/rustfs/rustfs:1.0.3";
    private string? _engine;
    private string? _container;

    public string? SkipReason { get; private set; }
    public Uri Endpoint { get; private set; } = null!;
    public BasicAWSCredentials Credentials { get; } = new("okf" + Guid.NewGuid().ToString("N")[..12], Guid.NewGuid().ToString("N"));
    public AmazonS3Client Admin { get; private set; } = null!;
    public const string Bucket = "okf";

    public async Task InitializeAsync()
    {
        _engine = new[] { "podman", "docker" }.FirstOrDefault(e => Run(e, "version", "--format", "{{.Server.Version}}").Code == 0);
        if (_engine is null)
        {
            SkipReason = "no container engine answers (podman, docker)";
            return;
        }

        var creds = Credentials.GetCredentials();
        var run = Run(_engine, "run", "-d", "--rm", "-p", "127.0.0.1::9000", "-e", "RUSTFS_ACCESS_KEY=" + creds.AccessKey, "-e", "RUSTFS_SECRET_KEY=" + creds.SecretKey, Image);
        if (run.Code != 0)
        {
            throw new InvalidOperationException($"{_engine} run failed: {run.Error}");
        }

        _container = run.Output.Trim();
        var port = Run(_engine, "port", _container, "9000").Output.Trim().Split(':')[^1];
        Endpoint = new Uri($"http://127.0.0.1:{port}");
        Admin = new AmazonS3Client(Credentials, new AmazonS3Config { ServiceURL = Endpoint.ToString(), ForcePathStyle = true, AuthenticationRegion = "us-east-1" });
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (true)
        {
            try
            {
                await Admin.PutBucketAsync(Bucket);
                return;
            }
            catch (Exception) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(500);
            }
        }
    }

    public async Task UploadDirectoryAsync(string localDir, string prefix)
    {
        foreach (var file in Directory.EnumerateFiles(localDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(localDir, file).Replace('\\', '/');
            await PutAsync(prefix + "/" + rel, await File.ReadAllBytesAsync(file));
        }
    }

    public Task PutAsync(string key, byte[] bytes) =>
        Admin.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = key, InputStream = new MemoryStream(bytes) });

    public Task StopAsync()
    {
        if (_engine is not null && _container is not null)
        {
            Run(_engine, "rm", "-f", _container);
            _container = null;
        }

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        Admin?.Dispose();
        return StopAsync();
    }

    private static (int Code, string Output, string Error) Run(string file, params string[] args)
    {
        try
        {
            var start = new ProcessStartInfo(file) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args) start.ArgumentList.Add(a);
            using var p = Process.Start(start)!;
            var output = p.StandardOutput.ReadToEnd();
            var error = p.StandardError.ReadToEnd();
            p.WaitForExit(120_000);
            return (p.ExitCode, output, error);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "", "not found");
        }
    }
}

[CollectionDefinition(Name)]
public sealed class RustFsCollection : ICollectionFixture<RustFsFixture>
{
    public const string Name = "RustFS";
}
```

- [ ] **Step 2: Write T2**

`S3IntegrationTests.cs` — every test `[SkippableFact]` starting with `Skip.If(fx.SkipReason is not null, fx.SkipReason);`, class-level `[Trait("Category", "S3Integration")]`, `[Collection(RustFsCollection.Name)]`. Tests (one `[SkippableFact]` each; each uses its own prefix so tests are independent):

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Text;
using Amazon.S3;
using Amazon.S3.Model;
using OKF4net.Attestation;
using OKF4net.Providers.S3;

namespace OKF4net.Tests.Providers.S3.Integration;

[Trait("Category", "S3Integration")]
[Collection(RustFsCollection.Name)]
public class S3IntegrationTests(RustFsFixture fx)
{
    private S3BundleProvider Provider(string prefix) =>
        new(fx.Credentials, new S3BundleSource { Bucket = RustFsFixture.Bucket, Prefix = prefix, ServiceUrl = fx.Endpoint, ForcePathStyle = true, AllowInsecureHttp = true });

    [SkippableFact]
    public async Task The_server_refuses_dot_segments_and_double_slashes()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        foreach (var key in new[] { "t-dots/a/../b.md", "t-dots/a//b.md", "t-dots/a/./b.md" })
        {
            var e = await Assert.ThrowsAsync<AmazonS3Exception>(() => fx.PutAsync(key, "x"u8.ToArray()));
            Assert.Equal(400, (int)e.StatusCode);
        }

        using var p = Provider("t-dots");
        await foreach (var _ in p.EnumerateAsync(null, BundleLimits.Default, default))
        {
            Assert.Fail("nothing may be listed");
        }
    }

    [SkippableFact]
    public async Task Accepted_hostile_names_serve_their_own_distinctive_content_and_match_an_independent_listing()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        string[] names = ["%2F.md", "%2e%2e.md", "%25.md", "%252e.md", "a+b.md", "a b.md", "a\\b.md", "a?b.md", "a#b.md", "café.md", "café.md", "dir/", ".md", "\U00010000.md"];
        foreach (var n in names)
        {
            await fx.PutAsync("t-names/" + n, Encoding.UTF8.GetBytes("CONTENT-OF:" + n));
        }

        await fx.PutAsync("t-names2/sibling.md", "x"u8.ToArray());
        await fx.PutAsync("/t-names/lead.md", Encoding.UTF8.GetBytes("CONTENT-OF:lead.md")); // RustFS normalizes the leading '/'

        // Oracle: the admin client's listing WITHOUT EncodingType (keys as XML text: an independent decoding path).
        var oracle = (await fx.Admin.ListObjectsV2Async(new ListObjectsV2Request { BucketName = RustFsFixture.Bucket, Prefix = "t-names/" }))
            .S3Objects.Select(o => o.Key["t-names/".Length..]).ToArray();

        using var p = Provider("t-names");
        var listed = new List<string>();
        await foreach (var l in p.EnumerateAsync(null, BundleLimits.Default, default))
        {
            listed.Add(l.RawName);
        }

        Assert.Equal(oracle.Order(StringComparer.Ordinal), listed.Order(StringComparer.Ordinal));
        Assert.DoesNotContain("sibling.md", listed);
        Assert.Equal(listed, listed.Order(Comparer<string>.Create(OKF4net.Providers.S3.Internal.S3KeyCodec.CompareByCodePoint))); // UTF-8 order, spec §4.5 precondition

        foreach (var raw in listed.Where(r => BundleKey.TryParse(r, out _, out _)))
        {
            using var o = await p.ReadAsync(BundleKey.Parse(raw), null, default);
            using var ms = new MemoryStream();
            await o.Content.CopyToAsync(ms);
            Assert.Equal("CONTENT-OF:" + raw, Encoding.UTF8.GetString(ms.ToArray()));
        }

        var bundle = await Bundle.LoadAsync(p); // Native policy: what the core rejects follows its base rules, the rest loads
        Assert.NotEmpty(bundle.Concepts);
    }

    [SkippableFact]
    public async Task A_lowered_key_bound_is_a_local_problem()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        await fx.PutAsync("t-long/" + new string('a', 60) + ".md", "---\ntype: Note\ntitle: T\ndescription: D.\n---\n"u8.ToArray());
        await fx.PutAsync("t-long/ok.md", "---\ntype: Note\ntitle: T\ndescription: D.\n---\n"u8.ToArray());
        var bundle = await Bundle.LoadAsync(Provider("t-long"), new BundleLoadOptions { Limits = BundleLimits.Default with { MaxKeyBytes = 32 } });
        Assert.Contains(bundle.Concepts, c => c.Id.ToString() == "ok");
        Assert.Single(bundle.ParseProblems);
    }

    [SkippableFact]
    public async Task A_pinned_read_and_a_capture_refuse_changed_code()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        await fx.UploadDirectoryAsync(Path.Combine(TestPaths.RepoRoot(), "tests", "fixtures", "okf_v02_computation"), "t-pin");
        using var p = Provider("t-pin");
        var bundle = await Bundle.LoadAsync(p);
        var (concept, survey) = bundle.ExecutableResources.Select(kv => (bundle.Concepts.Single(c => c.Id == kv.Key), kv.Value))
            .First(x => x.Value.Computation.State == ExecutableResourceState.Pinned);
        var key = survey.Computation.Key!;
        await fx.PutAsync("t-pin/" + key, "CHANGED\n"u8.ToArray());

        await Assert.ThrowsAsync<BundleVersionMismatchException>(async () => await bundle.ReadResourceTextAsync(key, survey.Computation.Version));
        var capture = await AttestationOrchestrator.CaptureAsync(bundle, concept);
        Assert.Null(capture.Capture); // AttestationCaptureResult(Capture, Failure, ImageOutdated): refused, nothing to run
        Assert.NotNull(capture.Failure);
        Assert.True(capture.ImageOutdated); // the stored bytes moved past the image's pinned version
    }

    [SkippableFact]
    public async Task OnAccess_sees_a_change_and_Every_only_after_its_interval()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        const string note = "---\ntype: Note\ntitle: T\ndescription: D.\n---\n";
        await fx.PutAsync("t-fresh/a.md", Encoding.UTF8.GetBytes(note));
        using var onAccess = new BundleHandle(Provider("t-fresh"), refreshPolicy: BundleRefreshPolicy.OnAccess);
        using var every = new BundleHandle(Provider("t-fresh"), refreshPolicy: BundleRefreshPolicy.Every(TimeSpan.FromHours(1)));
        _ = await onAccess.GetCurrentAsync();
        _ = await every.GetCurrentAsync();
        await fx.PutAsync("t-fresh/b.md", Encoding.UTF8.GetBytes(note));
        Assert.Contains((await onAccess.GetCurrentAsync()).Concepts, c => c.Id.ToString() == "b");
        Assert.DoesNotContain((await every.GetCurrentAsync()).Concepts, c => c.Id.ToString() == "b");
    }

    [SkippableFact]
    public async Task Bounds_an_object_is_local_a_listing_fails_the_load()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        await fx.PutAsync("t-bounds/big.md", Encoding.UTF8.GetBytes("---\ntype: Note\ntitle: T\ndescription: D.\n---\n" + new string('x', 5000)));
        await fx.PutAsync("t-bounds/ok.md", "---\ntype: Note\ntitle: T\ndescription: D.\n---\n"u8.ToArray());
        var bundle = await Bundle.LoadAsync(Provider("t-bounds"), new BundleLoadOptions { Limits = BundleLimits.Default with { MaxObjectBytes = 1000 } });
        Assert.Contains(bundle.ParseProblems, x => x.RawName == "big.md");
        await Assert.ThrowsAsync<BundleLoadException>(async () => await Bundle.LoadAsync(Provider("t-bounds"), new BundleLoadOptions { Limits = BundleLimits.Default with { MaxEntries = 1 } }));
    }

    [SkippableFact]
    public async Task A_stopped_store_keeps_the_previous_image_marked_stale()
    {
        Skip.If(fx.SkipReason is not null, fx.SkipReason);
        // Runs last in this class by name order is NOT guaranteed: it uses its own fixture instance.
        await using var own = new RustFsFixture();
        await own.InitializeAsync();
        await own.PutAsync("t-down/a.md", "---\ntype: Note\ntitle: T\ndescription: D.\n---\n"u8.ToArray());
        using var handle = new BundleHandle(new S3BundleProvider(own.Credentials, new S3BundleSource { Bucket = RustFsFixture.Bucket, Prefix = "t-down", ServiceUrl = own.Endpoint, ForcePathStyle = true, AllowInsecureHttp = true, RequestTimeout = TimeSpan.FromSeconds(3) }), refreshPolicy: BundleRefreshPolicy.OnAccess);
        var first = await handle.GetCurrentAsync();
        await own.StopAsync();
        var second = await handle.GetCurrentAsync();
        Assert.Same(first, second);
        Assert.True(handle.IsStale);
        Assert.NotNull(handle.LastLoadError);
    }
}
```

(`RustFsFixture` implements `IAsyncLifetime`; for the last test, make it also implement `IAsyncDisposable` with `ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync())`. If `ImageOutdated` turns out false for a changed pinned file, read `AttestationOrchestrator.CaptureAsync` and assert the `Failure` it documents for "the file changed since the load" instead — never drop the assertion.)

`S3ParityTests.cs` — the same five bundles as `EmbeddedResources/ParityTests.Bundles`, each uploaded once under its prefix, loaded through `FileSystemBundleProvider` and through `S3BundleProvider`, asynchronously. Copy the helpers and the five theories of `tests/OKF4net.Tests/Providers/EmbeddedResources/ParityTests.cs` into this class (do not share code with it: it is the embedded package's test), replacing `EmbeddedResourceBundleProvider` with an `S3BundleProvider` over `fx`, every synchronous call (`Bundle.Load`, `BundleValidator.Validate`, `bundle.ReadResourceText`, `IndexGenerator.RegenerateIndexes`) with its `…Async` spelling, and — in the executable-resources theory — compare the **versions as well** (both sides are SHA-256 hex now), then re-read pinned on each side with `ReadResourceTextAsync`. Uploads go in the class constructor, guarded by a static `ConcurrentDictionary<string, Task>` so each bundle is uploaded once per run.

The three load races (spec §4.3 table) and the persistent-ETag-constant case cannot be produced by RustFS deterministically; they are covered by a scripted double **over** RustFS: add to `S3IntegrationTests` a test that wraps the real provider in an `IBundleProvider` decorator deleting (through `fx.Admin`) the concept, the surveyed resource and the root `index.md` between `EnumerateAsync` and the first `ReadAsync`, one per run, asserting respectively `BundleLoadException` with the previous image kept by a handle, `ExecutableResourceState.Missing`, and a null `OkfVersion`. Add the ETag-constant limit as a T1 test instead (Task 8's `ReadTests`): a fake that serves different bytes under the same listing triplet, a handle under `OnAccess` that keeps the old image, and `ReadResourceTextAsync` with the old version refused — name it `A_persistent_change_with_a_constant_triplet_is_not_reloaded_but_pinned_code_is_refused`.

- [ ] **Step 3: Run the integration tests locally, with the engine running**

Run: `dotnet test tests/OKF4net.Tests --filter Category=S3Integration`
Expected: all PASS, **0 skipped**. Then stop the engine (or remove `podman`/`docker` from `PATH` in a shell) and run again: every test reported **skipped** with the reason, none failed. Then run once under Podman with no `docker` on `PATH`, and once under Docker on Linux (WSL), as gate G10 established.

- [ ] **Step 4: Amend the spec's oracle**

In the spec's T2 table, row "Noms acceptés, oracle indépendant", replace "le listing est comparé à celui de `mc` (client indépendant)" with "le listing est comparé à celui d'un client d'administration distinct, sans `EncodingType` (chemin de décodage indépendant : texte XML), `mc` n'étant plus garanti disponible (image MinIO retirée de Docker Hub)".

- [ ] **Step 5: Full checks and commit**

```bash
dotnet build OKF4net.sln && dotnet test OKF4net.sln --filter "Category!=ContainerIntegration&Category!=S3Integration" && dotnet format OKF4net.sln --verify-no-changes
git add tests/OKF4net.Tests/Providers/S3 docs/superpowers/specs/2026-11-16-s3-bundle-provider-design.md
git commit -m "test(s3): RustFS integration (parity, hostile names, pinning, freshness, bounds, outage) (#142)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 12: CI and release — filters, the AOT probe and job, the pack step

**Files:**
- Create: `tests/S3ProviderAotProbe/S3ProviderAotProbe.csproj`, `tests/S3ProviderAotProbe/Program.cs`
- Modify: `.github/workflows/ci.yml` (test filter; new job `s3-aot`), `.github/workflows/release.yml` (test filter; `Pack (Providers.S3)` after `Pack (Providers.EmbeddedResources)`)

**Interfaces:**
- Consumes: the packed `OKF4net` and `OKF4net.Providers.S3`.
- Produces: an executable guard for pack, Native AOT and trimming of the real package, at minimum and latest SDK versions.

- [ ] **Step 1: Write the probe project**

```xml
<!-- SPDX-License-Identifier: LGPL-3.0-or-later -->
<Project Sdk="Microsoft.NET.Sdk">

  <!--
    Not in OKF4net.sln: it consumes the REAL packages, packed by ci.yml's "S3 provider AOT smoke test" job at a
    run-unique version passed as OkfProbeVersion. OkfAwsSdkVersion selects the AWSSDK.S3 under test: the package's
    minimum, or the latest published (spec T3).
  -->
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <PublishAot>true</PublishAot>
    <InvariantGlobalization>true</InvariantGlobalization>
    <IsPackable>false</IsPackable>
  </PropertyGroup>

  <ItemGroup>
    <PackageReference Include="OKF4net.Providers.S3" Version="$(OkfProbeVersion)" />
    <PackageReference Include="AWSSDK.S3" Version="$(OkfAwsSdkVersion)" />
  </ItemGroup>

</Project>
```

`Program.cs` — serves a fake S3 on a loopback `TcpListener` (same HTTP/1.1 loop as `S3HostileEnvProbe`, rewritten here: the probe consumes packages only), hosting `bundles/acme_retail` read from the path given as `args[0]`:

```csharp
// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OKF4net;
using OKF4net.Providers;
using OKF4net.Providers.S3;

// Usage: S3ProviderAotProbe <bundle dir>
// 1. serves <bundle dir> as bucket "okf", prefix "kb", on loopback; loads it through S3BundleProvider and through
//    FileSystemBundleProvider; prints both summaries; exit 1 if they differ.
// 2. guard under AOT: the fake answers a 307 to another bucket's host; the provider must fail with Kind RefusedHost.
// 3. counter-proof: a plain AmazonS3Client (no guard) whose handler proxies to a second listener follows the same 307;
//    the second listener's count must be 1.
var root = Path.GetFullPath(args[0]);
var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    .ToDictionary(f => "kb/" + Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);

var redirectMode = false;
var main = Listen(req => redirectMode
    ? Redirect("http://other.s3.eu-west-1.amazonaws.com/kb/a.md")
    : Answer(req, files));
var second = 0;
var target = Listen(_ => { Interlocked.Increment(ref second); return Ok("X"u8.ToArray()); });

var clock = new FixedClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
var source = new S3BundleSource { Bucket = "okf", Prefix = "kb", ServiceUrl = new Uri($"http://127.0.0.1:{main.Port}"), ForcePathStyle = true, AllowInsecureHttp = true };
using var s3 = new S3BundleProvider(new BasicAWSCredentials("AKID", "secret"), source);
var a = Summary(await Bundle.LoadAsync(s3), clock);
var b = Summary(await Bundle.LoadAsync(new FileSystemBundleProvider(root)), clock);
foreach (var line in a) Console.WriteLine(line);
if (!a.SequenceEqual(b)) { Console.Error.WriteLine("S3 and filesystem summaries differ"); return 1; }

redirectMode = true;
try
{
    await s3.ReadAsync(BundleKey.Parse("a.md"), null, default);
    Console.Error.WriteLine("the guard did not refuse the 307");
    return 2;
}
catch (S3RequestException e) when (e.Kind == S3FailureKind.RefusedHost)
{
    Console.WriteLine("guard=refused");
}

var http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, Proxy = new WebProxy($"http://127.0.0.1:{target.Port}"), UseProxy = true });
using var witness = new AmazonS3Client(new BasicAWSCredentials("AKID", "secret"), new AmazonS3Config
{
    ServiceURL = $"http://127.0.0.1:{main.Port}", ForcePathStyle = true, AuthenticationRegion = "us-east-1", HttpClientFactory = new Fixed(http),
});
try { using var _ = await witness.GetObjectAsync(new GetObjectRequest { BucketName = "okf", Key = "kb/a.md" }); } catch (AmazonServiceException) { }
Console.WriteLine($"counter-proof={second}");
return second == 1 ? 0 : 3;

static string[] Summary(Bundle bundle, IOkfClock clock)
{
    var report = BundleValidator.ValidateAsync(bundle, clock).AsTask().GetAwaiter().GetResult();
    return [
        $"concepts={bundle.Concepts.Count}",
        .. bundle.Concepts.Select(c => "key=" + c.Key).Order(StringComparer.Ordinal),
        .. report.Diagnostics.Select(d => $"diag={d.Code}|{d.Severity}|{d.Key}").Order(StringComparer.Ordinal),
        $"audit={ConceptAudit.Run(bundle, default, clock).Findings.Count}",
    ];
}
```

Complete the file with: `Listen(Func<string, byte[]> respond)` returning an object with `Port`, starting a `TcpListener` on `IPAddress.Loopback, 0` and an accept loop that parses the request line and headers (as in `S3HostileEnvProbe`) and writes the bytes `respond(requestLine)` returns; `Answer(requestLine, files)` producing a `ListBucketResult` with `<EncodingType>url</EncodingType>`, keys percent-encoded byte by byte (unreserved and `/` kept), `ETag` `"<md5 hex>"`, `Size`, and object bodies by path; `Ok(bytes)` and `Redirect(location)` building `HTTP/1.1 200 OK`/`307 Temporary Redirect` responses with `Content-Length`; `sealed class Fixed(HttpClient c) : Amazon.Runtime.HttpClientFactory` returning `c`, `UseSDKHttpClientCaching => false`, `DisposeHttpClientsAfterUse => false`. Check the `AuditReport` member that counts findings (`Findings` or other) in `src/OKF4net/Audit.cs` and use it.

- [ ] **Step 2: Extend the test filters**

In `.github/workflows/ci.yml`, the `Test` step: `--filter "Category!=ContainerIntegration&Category!=S3Integration"`. In `release.yml`, the `Test` step: same filter, and extend its comment: "S3Integration starts RustFS in a container: excluded for the same reason (spec D14)."

- [ ] **Step 3: Add the `s3-aot` job to `ci.yml`**

Append after `embedded-aot`:

```yaml
  # Spec of 2026-11-16 §6 T3 (#142): the S3 provider as a host really consumes it -- the core and the provider PACKED
  # at a run-unique version, published Native AOT against the minimum AWSSDK.S3 and against the latest one, run
  # against a fake S3 the probe serves itself: S3 and filesystem summaries equal, the guard refusing a 307 under AOT,
  # and a counter-proof client without the guard following it.
  s3-aot:
    name: S3 provider AOT smoke test (${{ matrix.rid }}, AWSSDK ${{ matrix.sdk }})
    strategy:
      fail-fast: false
      matrix:
        rid: [linux-x64, osx-arm64, win-x64]
        sdk: [minimum, latest]
        include:
          - rid: linux-x64
            os: ubuntu-latest
          - rid: osx-arm64
            os: macos-latest
          - rid: win-x64
            os: windows-latest
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v7

      - uses: actions/setup-dotnet@v6
        with:
          dotnet-version: 10.0.x

      - name: Derive a CI-only version, an isolated package cache and the AWSSDK version
        shell: pwsh
        run: |
          $declared = (Select-String -Path Directory.Build.props -Pattern '<Version>([^<]*)</Version>').Matches[0].Groups[1].Value
          echo "PROBE_VERSION=$declared-ci.${{ github.run_id }}.${{ github.run_attempt }}" >> $env:GITHUB_ENV
          echo "PROBE_FEED=${{ runner.temp }}/okf-s3-feed" >> $env:GITHUB_ENV
          echo "NUGET_PACKAGES=${{ runner.temp }}/okf-s3-packages" >> $env:GITHUB_ENV
          $minimum = (Select-String -Path src/OKF4net.Providers.S3/OKF4net.Providers.S3.csproj -Pattern 'Include="AWSSDK.S3" Version="([^"]*)"').Matches[0].Groups[1].Value
          if ('${{ matrix.sdk }}' -eq 'minimum') { $sdk = $minimum } else {
            $sdk = ((Invoke-RestMethod https://api.nuget.org/v3-flatcontainer/awssdk.s3/index.json).versions | Where-Object { $_ -notmatch '-' } | Select-Object -Last 1)
          }
          echo "AWS_SDK_VERSION=$sdk" >> $env:GITHUB_ENV

      - name: Pack the core and the provider
        shell: pwsh
        run: |
          foreach ($project in 'src/OKF4net/OKF4net.csproj', 'src/OKF4net.Providers.S3/OKF4net.Providers.S3.csproj') {
            dotnet pack $project -c Release -o $env:PROBE_FEED -p:Version=$env:PROBE_VERSION -p:ContinuousIntegrationBuild=true
            if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed: $project" }
          }

      - name: The package declares AWSSDK.S3 as a minimum
        shell: pwsh
        run: |
          Add-Type -AssemblyName System.IO.Compression.FileSystem
          $zip = [IO.Compression.ZipFile]::OpenRead((Get-ChildItem $env:PROBE_FEED -Filter "OKF4net.Providers.S3.$env:PROBE_VERSION.nupkg").FullName)
          $nuspec = [IO.StreamReader]::new(($zip.Entries | Where-Object Name -like '*.nuspec').Open()).ReadToEnd()
          $zip.Dispose()
          if ($nuspec -notmatch 'id="AWSSDK.S3" version="4\.1\.0"') { throw "AWSSDK.S3 is not declared as the minimum 4.1.0: $nuspec" }

      - name: Publish the probe Native AOT, then check the resolved versions
        shell: pwsh
        run: |
          $config = Join-Path $env:RUNNER_TEMP 'okf-s3-nuget.config'
          @(
            '<?xml version="1.0" encoding="utf-8"?>',
            '<configuration>',
            '  <packageSources>',
            '    <clear />',
            "    <add key=`"okf-s3`" value=`"$env:PROBE_FEED`" />",
            '    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />',
            '  </packageSources>',
            '</configuration>'
          ) | Set-Content $config
          dotnet publish tests/S3ProviderAotProbe/S3ProviderAotProbe.csproj -c Release -r ${{ matrix.rid }} -p:PublishAot=true "-p:OkfProbeVersion=$env:PROBE_VERSION" "-p:OkfAwsSdkVersion=$env:AWS_SDK_VERSION" "-p:RestoreConfigFile=$config" -o probe-out
          if ($LASTEXITCODE -ne 0) { throw "publish failed" }
          $assets = Get-Content tests/S3ProviderAotProbe/obj/project.assets.json -Raw | ConvertFrom-Json
          foreach ($id in 'OKF4net', 'OKF4net.Providers.S3') {
            if (-not $assets.libraries.PSObject.Properties["$id/$env:PROBE_VERSION"]) { throw "$id was not resolved at $env:PROBE_VERSION" }
          }
          $resolved = $assets.libraries.PSObject.Properties.Name | Where-Object { $_ -like 'AWSSDK.*' }
          Write-Host "Resolved AWS packages: $($resolved -join ', ')"
          if (-not ($resolved -contains "AWSSDK.S3/$env:AWS_SDK_VERSION")) { throw "AWSSDK.S3 is not $env:AWS_SDK_VERSION" }

      - name: Run the probe
        shell: pwsh
        run: |
          $exe = Join-Path (Resolve-Path probe-out) ('S3ProviderAotProbe' + $(if ('${{ matrix.rid }}'.StartsWith('win-')) { '.exe' } else { '' }))
          & $exe (Resolve-Path bundles/acme_retail).Path
          if ($LASTEXITCODE -ne 0) { throw "probe exited $LASTEXITCODE" }
```

Run `actionlint` if available locally; otherwise check the YAML by eye against `embedded-aot`.

- [ ] **Step 4: Add the pack step to `release.yml`**

After `Pack (Providers.EmbeddedResources)`:

```yaml
      - name: Pack (Providers.S3)
        run: >
          dotnet pack src/OKF4net.Providers.S3/OKF4net.Providers.S3.csproj -c Release -o artifacts
          -p:Version=$VERSION
          -p:ContinuousIntegrationBuild=true
```

- [ ] **Step 5: Run the probe locally once (Windows, the gate's linker workaround)**

```bash
dotnet pack src/OKF4net/OKF4net.csproj -c Release -o <scratchpad>/feed -p:Version=0.7.0-local.1
dotnet pack src/OKF4net.Providers.S3/OKF4net.Providers.S3.csproj -c Release -o <scratchpad>/feed -p:Version=0.7.0-local.1
```

then publish the probe as the job does (with `-p:OkfProbeVersion=0.7.0-local.1 -p:OkfAwsSdkVersion=4.1.0`, a `nuget.config` naming `<scratchpad>/feed` and nuget.org, `NUGET_PACKAGES` set to `<scratchpad>/pkgs`, under `vcvars64.bat` with `-p:IlcUseEnvironmentalTools=true` as the D1 prototypes needed), run it on `bundles/acme_retail`, and expect exit 0 with `guard=refused` and `counter-proof=1`.

- [ ] **Step 6: Commit**

```bash
git add tests/S3ProviderAotProbe .github/workflows/ci.yml .github/workflows/release.yml
git commit -m "ci(s3): AOT probe on the real packages at minimum and latest AWSSDK, test filters, release pack (#142)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 13: Documentation

**Files:**
- Modify: `src/OKF4net.Providers.S3/README.md` (complete), `README.md` (root), `CHANGELOG.md`, `CLAUDE.md`

- [ ] **Step 1: Package README**

Write `src/OKF4net.Providers.S3/README.md`, in English, starting **exactly** with:

```markdown
# OKF4net.Providers.S3

> **Local guarantee — run it before trusting a change here.** The tests that talk to a real S3-compatible store are
> excluded from CI by decision (`Category=S3Integration`). With Podman or Docker running:
>
> ```sh
> dotnet test tests/OKF4net.Tests --filter Category=S3Integration
> ```
>
> Check the run reports **0 skipped**: every test skips, with its reason, when no container engine answers. Required
> after any change to this package and after any bump of `AWSSDK.S3` or `AWSSDK.Core`.
```

Then sections: **What it is** (read-only, asynchronous, `CompletesSynchronously = false`: synchronous entry points refuse it); **Recipe** (credentials from the host — any `AWSCredentials`, the provider never reads the environment; `S3BundleSource`; a `BundleHandle` with `BundleRefreshPolicy.Every(TimeSpan.FromMinutes(5))`; `Bundle.LoadAsync`; `OkfBundleTools(handle)`; dispose the provider); **Cost of `OnAccess`** (one listing request per 1 000 objects at every access; prefer `Every`); **Memory** (spec §4.3: up to 4 × `MaxObjectBytes` per read in flight; lower `MaxObjectBytes`, the core's bound and `MaxConcurrentRequests` for an untrusted bucket); **Freshness limits** (A → B → A; a persistent change with constant name/ETag/size is not reloaded, pinned code is still refused); **What the guard covers and what it does not** (S3 requests: one authority, one path prefix; region detection and CSM neutralized for this client; credential providers' own traffic — STS, IMDS, SSO — is the host's); **A misconfigured region** (fails as `RefusedHost`, not corrected silently); **Compatible stores** (preconditions: the store decodes request paths once without normalizing dot segments, and lists keys in UTF-8 byte order; tested: RustFS 1.0.3 only); **Checksums** (`S3Checksums.WhenRequired` for a store that refuses the default); **Errors** (`S3RequestException.Kind`, closed vocabulary; the raw cause is `InnerException`, for logs only); **Known limitation #247** (a store failing while validation reads `index.md`/`log.md` shows as `UnparseableIndex`/`UnparseableLog`); **Versions** (declares `AWSSDK.S3 >= 4.1.0`; CI tests the minimum and the latest); **Catalog factory** (out of scope, #239 — a host-written factory of about fifteen lines, shown, with `AcceptedSettings = { "bucket", "prefix", "region", "serviceUrl", "forcePathStyle", "displayName" }` and the host's credentials captured in the factory, never in `catalog.json`). Fix the enum name used in **Checksums** to `S3ChecksumMode.WhenRequired`.

- [ ] **Step 2: Root README, CHANGELOG**

Root `README.md`: in the packages table (next to `OKF4net.Providers.EmbeddedResources`), a row `OKF4net.Providers.S3` — "A bundle in Amazon S3 or an S3-compatible store (read-only, async; depends on `AWSSDK.S3`)" — and, in the providers section, three sentences: what it is, that it is the one package with a third-party dependency besides `Catalog.Hosting`/`Mcp`, and a link to its README.

`CHANGELOG.md`, under `## [Unreleased]`:
- `### Added`: **S3 bundle provider (#142).** `OKF4net.Providers.S3`: `S3BundleProvider`, `S3BundleSource`, `S3RequestException` (`S3Operation`, `S3FailureKind`), `S3ChecksumMode`. Read-only, asynchronous; the provider builds its own client from host credentials, guards every request (one authority, one path prefix), versions objects by SHA-256 of their bytes; depends on `AWSSDK.S3` ≥ 4.1.0.
- `### Changed`: **The load reads the bundle token before listing** (#142, D15 a): a change during a load is reloaded at the next comparison instead of being served stale. **`Bundle.ResolveResourceKeyAsync`/`TryResolveResourceKey` throw `BundleLoadException` for a provider failure other than a refusal** (#142, D15 b) — previously reported as `Unsafe`; validation now fails instead of reporting "escapes the bundle" for a store that is down; the message names the exception type only.

- [ ] **Step 3: CLAUDE.md**

1. "Hard rules", the zero-dependency bullet: after the `Catalog.Hosting` sentence, add: "`src/OKF4net.Providers.S3/` is the second explicit exception: it references `OKF4net` and exclusively `AWSSDK.S3` (declared as a minimum, `>= 4.1.0`; nothing in `src/` references this project), so a bundle can be read from S3; any bump of `AWSSDK.S3` **or** `AWSSDK.Core` requires the local `dotnet test tests/OKF4net.Tests --filter Category=S3Integration` at **0 skipped** and ci.yml's `S3 provider AOT smoke test` green (minimum and latest SDK)."
2. Under "A green xunit run says nothing about code xunit cannot execute", a sub-bullet: "**The same applies to the S3 provider against a real store.** `[Trait("Category", "S3Integration")]` tests start `docker.io/rustfs/rustfs:1.0.3` themselves (Podman or Docker) and are excluded from CI by decision, like `ContainerIntegration`; the guarantee is the local command at the top of `src/OKF4net.Providers.S3/README.md`, with 0 skipped. Its guard against SDK behaviour (redirects, ambient configuration, CSM) is in-process (`GuardTests`, `HostileEnvironmentTests`, both with counter-proofs) and in ci.yml's `S3 provider AOT smoke test`."
3. Architecture, a bullet after `OKF4net.Providers.EmbeddedResources/`: "**`src/OKF4net.Providers.S3/`** — a bundle in Amazon S3 or an S3-compatible store (#142, spec `docs/superpowers/specs/2026-11-16-s3-bundle-provider-design.md`), referencing `OKF4net` and `AWSSDK.S3` only, read-only and truly asynchronous (`CompletesSynchronously = false`). The provider builds its own `AmazonS3Client` (never a host `IAmazonS3`) with a hermetic configuration (`ClientBuilder`; every ambient setting overridden, region detection and CSM neutralized for this client) and one provider-owned `HttpClient` whose chain is `PinningHandler` (one authority and one path prefix per configuration; a 307 between two S3 hostnames would otherwise change the bucket) → `BoundingHandler` (listing 8 MiB, error 64 KiB, token 1 024 bytes, before the SDK deserializes; listing pages are parsed by the provider itself with strict percent/UTF-8 decoding) → the network. Each operation holds a permit (`MaxConcurrentRequests`) and a deadline, and signs with a per-operation credentials snapshot resolved in isolation (`SnapshotCredentials`/`CredentialResolver`): the SDK never calls the host's provider. Reads buffer the whole object; the object version is the SHA-256 hex of the bytes (the filesystem provider's format); the bundle token `s3v1:` hashes (name, ETag, size) in listing order. `Location` never names the bucket (`s3:<DisplayName or 8 hex>/<prefix>`), messages are a closed vocabulary (`S3RequestException.Kind`), and every non-object failure is a `BundleProviderException`, so a load fails rather than recording a local state."
4. The `Providers/` bullet: after the sentence about `BundleHandle`, add "`LoadCoreAsync` reads the bundle token **before** listing (#142, D15 a), and `ResolveResourceKeyAsync` returns `Unsafe` only for a `BundleAccessRefusedException`; any other provider failure throws a `BundleLoadException` naming the exception type (D15 b; the same confusion while validation reads `index.md`/`log.md` is #247)."
5. Commands: add `dotnet test tests/OKF4net.Tests --filter Category=S3Integration   # S3 provider vs RustFS (Podman/Docker), excluded from CI` and update the CI sentence to mention the `S3 provider AOT smoke test`.

- [ ] **Step 4: Graph, checks, commit**

```bash
graphify update .
dotnet build OKF4net.sln && dotnet test OKF4net.sln --filter "Category!=ContainerIntegration&Category!=S3Integration" && dotnet format OKF4net.sln --verify-no-changes
git add src/OKF4net.Providers.S3/README.md README.md CHANGELOG.md CLAUDE.md graphify-out
git commit -m "docs(s3): package README, root README, CHANGELOG, CLAUDE.md (#142)

Co-Authored-By: Claude Opus 5.5 <noreply@anthropic.com>"
```

---

### Task 14: Final verification

**Files:** none (report only).

- [ ] **Step 1: Everything, from a clean build**

```bash
dotnet build OKF4net.sln -c Release
dotnet test OKF4net.sln -c Release --filter "Category!=ContainerIntegration&Category!=S3Integration"
dotnet format OKF4net.sln --verify-no-changes
dotnet test tests/OKF4net.Tests --filter Category=S3Integration
dotnet test producers/OkfProducer.sln
git status --short tests/fixtures bundles docs/spec
```

Expected: build and tests green; format clean; `S3Integration` **0 skipped**, all passed; producers green; the last command prints **nothing** (no fixture, bundle or spec touched).

- [ ] **Step 2: Report to the maintainer**

Report, with the outputs: the commits of the branch, the gate's Annexe B summary, the counts of each test run (passed/skipped), the local AOT probe result, and any deviation from the spec taken during execution with its commit. Ask for the go before pushing or opening a PR (the PR waits for #237 to merge and the branch to be rebased on `dev`).
