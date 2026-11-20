// SPDX-License-Identifier: LGPL-3.0-or-later
namespace OKF4net.Tests.Providers.S3.Integration;

/// <summary>
/// One S3-compatible store the T2 suite can run against, selected by the environment variable <c>OKF_S3IT_STORE</c>
/// (<c>rustfs</c>, the default, or <c>minio</c>). An unknown value FAILS the run: a typo must not silently test the default.
/// Every image is pinned by the digest of its multi-architecture index, so a re-tagged image cannot change what the tests
/// talk to. When a digest is bumped, run the local S3Integration suite under Podman and Docker (0 skipped), per store.
/// </summary>
public sealed record StoreProfile(string Name, string Image, string[] Environment, string[] Command, string WireSpace)
{
    /// <summary>docker.io/rustfs/rustfs:1.0.3 (measured 2026-11-19 with <c>docker buildx imagetools inspect</c>: linux/amd64 and linux/arm64).</summary>
    public static readonly StoreProfile RustFs = new(
        "rustfs",
        "docker.io/rustfs/rustfs:1.0.3@sha256:9ca9559533b537d10482fe923288b676ad08f92a7069d9a17327fbae3cab688b",
        ["RUSTFS_ACCESS_KEY={access}", "RUSTFS_SECRET_KEY={secret}"],
        [],
        "%20");

    /// <summary>
    /// MinIO RELEASE.2026-11-16T00-11-40Z, from Chainguard (<c>cgr.dev/chainguard/minio</c>), because MinIO Inc. no longer
    /// publishes its community image: <c>quay.io/minio/minio</c> and <c>docker.io/minio/minio</c> both answer "unauthorized"
    /// to an anonymous pull (measured 2026-11-19, Podman 5.4.2). The digest was read from the pulled image; Chainguard's free
    /// tier keeps only recent tags, so a digest that has been pruned fails the pull loudly, never silently.
    /// The data directory is an anonymous volume: MinIO refuses a tmpfs (<c>file access denied</c>, measured).
    /// </summary>
    public static readonly StoreProfile MinIo = new(
        "minio",
        "cgr.dev/chainguard/minio@sha256:dc5e098902c102b848a72b96671c235f1f8464ccad60a62d1f205f8c221ce12b",
        ["MINIO_ROOT_USER={access}", "MINIO_ROOT_PASSWORD={secret}"],
        ["server", "/data", "--address", ":9000"],
        "+");

    /// <summary>The store named by <c>OKF_S3IT_STORE</c>; RustFS when it is unset or empty.</summary>
    public static StoreProfile FromEnvironment()
    {
        var name = System.Environment.GetEnvironmentVariable("OKF_S3IT_STORE");
        return string.IsNullOrWhiteSpace(name) ? RustFs : name.Trim().ToLowerInvariant() switch
        {
            "rustfs" => RustFs,
            "minio" => MinIo,
            _ => throw new InvalidOperationException($"OKF_S3IT_STORE must be 'rustfs' or 'minio' (got '{name}')"),
        };
    }

    /// <summary>The arguments after <c>run</c>: detached, removed at exit, labelled, loopback port 9000 published to a free port.</summary>
    public string[] RunArguments(string containerName, string accessKey, string secretKey)
    {
        var args = new List<string> { "run", "-d", "--rm", "--name", containerName, "--label", StoreFixture.Label, "-p", "127.0.0.1::9000" };
        if (Name == "minio")
        {
            args.AddRange(["-v", "/data"]);
        }

        foreach (var variable in Environment)
        {
            args.AddRange(["-e", variable.Replace("{access}", accessKey, StringComparison.Ordinal).Replace("{secret}", secretKey, StringComparison.Ordinal)]);
        }

        args.Add(Image);
        args.AddRange(Command);
        return [.. args];
    }
}
