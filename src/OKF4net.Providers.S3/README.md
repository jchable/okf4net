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

Serve an [OKF](https://github.com/jchable/okf4net) knowledge bundle from Amazon S3 or an S3-compatible store: a read-only, asynchronous `IBundleProvider` over a bucket and prefix, with a mandatory request guard, host-owned credentials and content-hash versions.

## What it is

`S3BundleProvider` reads one bucket and one key prefix. It is **read-only** (`WriteAsync` and `DeleteAsync` throw `BundleReadOnlyException` without sending anything; `OkfBundleTools` offers no write tool over it) and **truly asynchronous**: `Capabilities.CompletesSynchronously` is `false`, so the library's synchronous entry points refuse it or never wait on it (a read still pending when the synchronous bridge returns is cancelled and the call throws `InvalidOperationException`; a synchronous write is refused with `BundleReadOnlyException` before anything is sent). Use `Bundle.LoadAsync`, `BundleHandle.GetCurrentAsync` and the `…Async` tool methods. The obsolete path-based `Bundle` members (`TryResolveResource`, `ReadResourceText(string)`) throw `NotSupportedException` on such a bundle: read through keys (`Bundle.ResolveResourceKeyAsync`, `Bundle.ReadResourceTextAsync`).

It depends on `AWSSDK.S3`, and on nothing else besides `OKF4net`. The provider builds and owns its own S3 client, with a fixed configuration, and puts a guard under it that refuses every request that is not for this bucket and prefix: no overload accepts an `IAmazonS3` of yours, because the guard could not then hold. Each object's version is the lowercase hex SHA-256 of its bytes (the filesystem provider's format), so pinning sanctioned code never depends on an ETag or on the store honouring a conditional header.

`provider.Location` is `s3:<DisplayName or 8 hex digits>`, followed by `/<prefix>` when the prefix is not empty: **the bucket name never reaches a model**, neither in `Location` (and so in no display path) nor in any exception message.

## Recipe

```csharp
using Amazon;
using Amazon.Runtime;
using OKF4net;
using OKF4net.Agents;
using OKF4net.Providers;
using OKF4net.Providers.S3;

// Credentials are yours: any AWSCredentials that resolves to an access key (anonymous access is not supported).
// The provider never reads the environment, a profile or IMDS for them.
AWSCredentials credentials = new BasicAWSCredentials(accessKey, secretKey);

using var provider = new S3BundleProvider(credentials, new S3BundleSource
{
    Bucket = "my-knowledge",
    Prefix = "okf/prod",          // no leading or trailing '/'; empty = the whole bucket
    DisplayName = "prod-kb",      // what a model sees instead of the bucket
    Region = RegionEndpoint.EUWest3,
});

// One-shot:
var bundle = await Bundle.LoadAsync(provider);

// Long-lived: one shared image, compared with the store at most every five minutes.
using var handle = new BundleHandle(provider, refreshPolicy: BundleRefreshPolicy.Every(TimeSpan.FromMinutes(5)));
var tools = new OkfBundleTools(handle);
```

A compatible store takes a `ServiceUrl` (an origin only: scheme, host, port) instead of a `Region`:

```csharp
new S3BundleSource
{
    Bucket = "kb",
    ServiceUrl = new Uri("http://127.0.0.1:9000"),
    ForcePathStyle = true,        // most compatible stores need it
    AllowInsecureHttp = true,     // http:// is refused otherwise
}
```

Rules enforced at construction (`ArgumentException`, no I/O): exactly one of `Region` and `ServiceUrl`; a general-purpose bucket name (access points, ARNs, aliases and directory buckets are refused); a bucket name containing `.` and an IP-address `ServiceUrl` both need `ForcePathStyle`; a region outside the `amazonaws.com` DNS partition (China, for instance) is refused (give a `ServiceUrl`); positive timeouts and bounds. `S3BundleSource` also carries `RequestTimeout` (30 s: one operation, retries and credential resolution included), `MaxConcurrentRequests` (16), `MaxObjectBytes` (the core's 256 MiB default) and `Checksums`.

Every request is signed: `AnonymousAWSCredentials` resolves to no key, and every operation then fails as `S3FailureKind.Credentials`. Anonymous access to a public bucket is not supported by this version.

Dispose the provider when you are done with it: `Dispose` releases, once, the client and the `HttpClient` it built. A `BundleHandle` does not dispose its provider, so dispose the handle first (as `using` declarations do). Disposing the provider while an operation is in flight interrupts it, and it fails as `S3FailureKind.Disposed`, like every later call (never a raw `ObjectDisposedException`).

## Cost of `OnAccess`

A load costs one listing for the bundle version, a second listing for the names, and one `GET` per object read. With `BundleRefreshPolicy.OnAccess` (the **catalog's default** per source, `KnowledgeCatalogOptions.RefreshPolicy`; the `BundleHandle` default is `Manual`) the version is compared at every access: one `ListObjectsV2` request per 1,000 objects, every time. Prefer `BundleRefreshPolicy.Every(...)`, or `KnowledgeCatalogOptions.RefreshPolicyFor` for an S3 source.

## Memory

A read buffers the whole object (no streaming). For an object of `B` bytes, one read in flight holds up to about `4 × B` before the provider is released (the provider's buffer, the core's copy, and the decoded string), and up to `6 × B` when the response carries no `Content-Length`. At the defaults (16 reads in flight, 256 MiB each) that is several gigabytes. For a bucket you do not trust, lower `S3BundleSource.MaxObjectBytes`, the core's own bound (`BundleLoadOptions.Limits` with a smaller `MaxObjectBytes`) and `MaxConcurrentRequests`. Also lower `BundleLimits.MaxEntries`: a hostile store can answer a listing with one-key pages, which costs up to `MaxEntries` requests.

## Freshness limits

The bundle token (`s3v1:` + a SHA-256 over every listed name, ETag and size) is read **before** the names are listed, so a change that happens during a load is reloaded at the next comparison your refresh policy allows. Two limits are inherent:

1. **A → B → A.** A change undone before the comparison can leave an image that mixes intermediate bytes under a token that is the same again (the filesystem provider has the same limit).
2. **A constant name, ETag and size.** A durable replacement by other bytes with the same size and the same ETag (a collision, or a store whose ETag does not follow the content) does not change the token and is never reloaded by the policy. Sanctioned code stays protected, since its pinned re-read compares the SHA-256 and is refused; the rest of the image (texts, links) stays stale until a visible change or a `RefreshAsync` of yours.

## What the guard covers, and what it does not

Covered: every request this client sends to S3. The guard admits one authority (scheme, host, port) and one path prefix computed from the source and the addressing mode, only `GET`, and for a read exactly the encoded key expected, for a listing exactly the expected `prefix` and a closed set of query parameters. Anything else is refused before it is sent (`S3FailureKind.RefusedHost`). It also allows at most three sends per operation, redirects included. A 307 to another bucket's host (which the SDK would otherwise follow, re-sign and send your session token to), or between the two hostnames of the same bucket, is refused. Region detection and client-side monitoring (UDP), which bypass the HTTP client, are neutralized for this client only. Every ambient setting found to change where a request goes or what it carries is overridden explicitly, from the environment and from the profile file alike: endpoint URLs, FIPS, dual-stack, the us-east-1 legacy global endpoint, defaults mode, retry mode and attempts, checksum modes, account-id endpoint mode, client-side monitoring, proxy variables, and the `app/` and `exec-env/` user-agent markers. Each is checked with a witness that a plain client does change under it (except the profile-file spelling of the us-east-1 setting: measured in a fresh process, no permanent test, see Annexe C12 of the spec): `HostileEnvironmentTests` (in a child process) and `AmbientEndpointTests` (in-process, Region sources). Two ambient header contents still reach the store, unsigned and without changing the destination: inside AWS Lambda (`AWS_LAMBDA_FUNCTION_NAME` and `_X_AMZN_TRACE_ID` set), the SDK adds an `x-amzn-trace-id` header; and the text of `AWS_INTERNAL_ENV` is inserted into the `User-Agent`.

Not covered: the traffic of your credential providers (STS, IMDS, SSO, a refreshing provider's own calls) is yours. The provider calls your `AWSCredentials` isolated, once per operation, under that operation's deadline, and hands the SDK a snapshot, so a provider that blocks or hangs ends the operation as `S3FailureKind.Credentials` and never stalls the SDK.

## A misconfigured region

The provider does not correct a wrong region: the SDK's automatic region detection is off for this client, and a redirect to another host is refused by the guard (`S3FailureKind.RefusedHost`), never followed. A store that answers a wrong-region request with a 301 `PermanentRedirect` or a 400 `AuthorizationHeaderMalformed` is reported as `BucketUnavailable` (both are in the provider's error table; neither was measured against real Amazon S3). Fix the `Region`.

## Compatible stores

Preconditions on a store other than Amazon S3:

- It decodes the request path **once**, without normalizing dot segments (`..`, `.`) or doubled slashes: confinement to the prefix relies on it.
- It lists keys in **strictly increasing UTF-8 byte order**, as S3's general-purpose buckets do: the bundle token hashes the listing in its order, and a listing out of order is refused (`InvalidResponse`).
- It supports `ListObjectsV2` with `encoding-type=url` and answers `EncodingType` `url`.
- It gives every listed object a non-empty ETag and a size.
- It answers listings in the S3 XML namespace (`http://s3.amazonaws.com/doc/2006-03-01/`) or with no namespace. A store that uses another one is refused (`InvalidResponse`): Google Cloud Storage's S3-interoperability endpoint (`http://doc.s3.amazonaws.com/2006-03-01`) is one.
- It never answers a listing page that is truncated (`IsTruncated` true) but holds no key. Such a page is refused (`InvalidResponse`) and the whole load fails. Amazon S3 itself may return one on a versioned bucket, under a long run of delete markers (not measured): a bundle there would never load, and fail closed. Whether to accept such pages, counted against the page budget, is open.
- It accepts SigV4 requests scoped to region `us-east-1`. A `ServiceUrl` source always signs with `us-east-1`, and `S3BundleSource` has no signing-region option (`Region` and `ServiceUrl` are exclusive). A store that checks the region of the signature's scope answers `400 AuthorizationHeaderMalformed`, which the provider reports as `BucketUnavailable`, pointing at the bucket rather than at the signature. Stores believed to check it (none measured): Wasabi's regional endpoints, Scaleway, Backblaze B2, OVH, Garage, MinIO with a site region configured.
- Its single-part ETags are MD5 hex digests, as S3's are. Measured on `AWSSDK.S3` 4.1.0: the SDK checks a single-part ETag against the bytes it reads. A non-hex single-part ETag makes the SDK's own check throw `FormatException`, which the provider reports as `Transport`: a store returning opaque single-part ETags would fail every read. A well-formed MD5 ETag that does not match the bytes is refused as `Integrity`. A multipart-shaped ETag (`<hex>-<parts>`) is not checked.

Tested with RustFS 1.0.3 only (see Testing). Anything else is a precondition, not a claim.

## Checksums

`S3BundleSource.Checksums` is `S3ChecksumMode.WhenSupported` by default, the SDK's own default (accepted by RustFS, measured); it is never read from the environment. A store that refuses the SDK's checksum headers needs `S3ChecksumMode.WhenRequired`. That mode does **not** verify checksums on responses (measured: a wrong response checksum is then not detected). The version of an object is its SHA-256 whatever the mode, so pinning does not depend on this setting.

## Errors

Every failure that is not about one object is an `S3RequestException` (a `BundleProviderException`, so the core fails the load instead of recording a local state). It carries `Operation` (`List`, `Read`, `Exists`), `Kind` and `StatusCode`. `S3FailureKind` is a closed vocabulary, compared by value: `Timeout`, `Throttled`, `ServerError`, `ClientError`, `Transport`, `RefusedHost`, `InvalidResponse`, `Credentials`, `BucketUnavailable`, `Integrity`, `Disposed`. The message is `S3 <operation> failed: <kind>`, plus ` (HTTP <code>)` and ` for '<key>'` when there are some: never the bucket, an endpoint, a request id, a URL, the S3 error code or any other text from the server. The raw cause is the `InnerException`, **for your logs only**; do not forward it to a model.

An object that is absent, refused, of another version or too large is reported with the contract's own exceptions (`BundleObjectNotFoundException`, `BundleAccessRefusedException`, `BundleVersionMismatchException`, `BundleObjectTooLargeException`). A load failure reaches the host as a `BundleLoadException` whose `InnerException` is the `S3RequestException`, and a handle keeps serving its previous image (`IsStale`, `LastLoadError`).

The `Integrity` kind rests on the SDK's message text: AWSSDK.Core 4.1.0 has no typed exception for a hash mismatch, so the provider recognizes the SDK's own wording. It fails closed, but if you float `AWSSDK.Core` above the 4.1.0 minimum, a hash mismatch may be reported as `Transport` instead of `Integrity`. Two tests that go through the real SDK pipeline guard this and run on every SDK bump.

## Known limitation (#247)

While a validation reads `index.md` or `log.md`, a store that fails shows as an `UnparseableIndex` or `UnparseableLog` diagnostic instead of failing the validation. It is the same for every provider and is tracked in [#247](https://github.com/jchable/okf4net/issues/247). Resolving a resource of a concept (`computation`, `attester.resource`) during validation does fail on a store failure.

## Versions

The package declares `AWSSDK.S3 >= 4.1.0` (a minimum, not an exact pin), which admits `AWSSDK.Core` 4.1.0 up to, but excluding, 5.0.0. The guarantees above are checked at run time and by tests rather than assumed: ci.yml's `S3 provider AOT smoke test` publishes a Native AOT probe against the packed packages at the declared minimum and at the latest published versions, and checks the guard under AOT. The local `S3Integration` run (above) is required on every bump of `AWSSDK.S3` or `AWSSDK.Core`.

## In a knowledge catalog

`OKF4net.Catalog` reads `catalog.json` sources through `IBundleProviderFactory`. There is no built-in S3 factory ([#239](https://github.com/jchable/okf4net/issues/239)): write one, about fifteen lines, and keep the credentials in the factory, never in `catalog.json`.

```csharp
sealed class S3Factory(AWSCredentials credentials) : IBundleProviderFactory, IDisposable
{
    private readonly ConcurrentBag<S3BundleProvider> _created = [];

    public string ProviderType => "s3";
    public IReadOnlySet<string> AcceptedSettings { get; } = new HashSet<string>
        { "bucket", "prefix", "region", "serviceUrl", "forcePathStyle", "displayName" };

    public IBundleProvider Create(KnowledgeCatalogSource source)
    {
        var s = source.Settings;
        var provider = new S3BundleProvider(credentials, new S3BundleSource
        {
            Bucket = s["bucket"],
            Prefix = s.GetValueOrDefault("prefix", ""),
            DisplayName = s.GetValueOrDefault("displayName"),
            Region = s.TryGetValue("region", out var region) ? RegionEndpoint.GetBySystemName(region) : null,
            ServiceUrl = s.TryGetValue("serviceUrl", out var url) ? new Uri(url) : null,
            ForcePathStyle = s.GetValueOrDefault("forcePathStyle") == "true",
        });
        _created.Add(provider);
        return provider;
    }

    public void Dispose() { foreach (var p in _created) p.Dispose(); }
}
```

```json
{ "version": 1, "sources": [ { "id": "remote", "provider": "s3",
  "settings": { "bucket": "my-knowledge", "prefix": "okf/prod", "region": "eu-west-3", "displayName": "prod-kb" } } ] }
```

Register it with `KnowledgeCatalogOptions.ProviderFactories`. The catalog never disposes a provider, and this factory keeps every provider it creates until the factory itself is disposed: a reload that changes a source's configuration replaces that source's handle but not the old provider (nor its `HttpClient`), and a rejected reload leaves behind the providers built for the other sources. That is acceptable for a catalog that reloads rarely; a host that reloads often should track providers per source and dispose the ones whose handle left.

**One bad source freezes the whole catalog.** A setting the factory does not list (`IllegalSetting`), or a factory that throws (`ProviderUnavailable`, naming the exception type only), rejects the **whole** reload: the catalog keeps serving its previous snapshot and reports the problem in `LastReloadDiagnostics` (at construction, the first load throws `CatalogException` instead). With the factory above, any of these does it, for every source of the catalog: a missing `bucket`; a `prefix` with a trailing `/` (`okf/`) or any other value `S3BundleSource` refuses; an `http://` `serviceUrl` (`allowInsecureHttp` is not an accepted setting, so a catalog source cannot reach a plain-HTTP store through this factory); a `region` outside the `amazonaws.com` partition (`cn-north-1`). Validate your manifest before you deploy it.

## Testing

Two layers. The unit tests run in CI with no service: the real SDK against scripted responses, with the guard in place, plus a child-process probe under a hostile environment. The `S3Integration` tests start `docker.io/rustfs/rustfs:1.0.3` themselves (Podman if it answers, otherwise Docker); they were run with RustFS 1.0.3 under Podman and under Docker 28.3.3, and nothing else is claimed. What they cannot prove:

- RustFS emits a space as `%20` and a `+` as `%2B`, never a raw `+`. Stores that emit a raw `+` for a space (real S3, MinIO) are handled by form-decoding listed keys; that is guarded by the unit tests, not by the RustFS run.
- The SDK cannot `PUT` a key with a leading `/` to RustFS, so the tests sign that one request themselves.
- The Native AOT probe was verified locally on Windows; the Linux and macOS legs run in CI.

## License

LGPL-3.0-or-later. Depends on `AWSSDK.S3` (Apache-2.0), which is not part of this repository.
