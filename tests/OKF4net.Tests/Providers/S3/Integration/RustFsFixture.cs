// SPDX-License-Identifier: LGPL-3.0-or-later
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;

namespace OKF4net.Tests.Providers.S3.Integration;

/// <summary>
/// docker.io/rustfs/rustfs:1.0.3 on a free loopback port (spec T2). Engine chosen explicitly: podman if it answers,
/// else docker. No engine: <see cref="SkipReason"/> is set and every test reports skipped. An engine that answers but a
/// container that does not start: <see cref="InitializeAsync"/> throws, so every test FAILS. Each instance runs its own
/// container (a unique name, <c>--rm</c>), removed at disposal and when the start fails.
/// <para>The guarantee is one local command, outside CI: <c>dotnet test tests/OKF4net.Tests --filter
/// Category=S3Integration</c>, believed only when the run reports <b>0 skipped</b>.</para>
/// </summary>
public sealed class RustFsFixture : IAsyncLifetime, IAsyncDisposable
{
    public const string Image = "docker.io/rustfs/rustfs:1.0.3";
    public const string Bucket = "okf";

    /// <summary>The label every container this fixture starts carries, so a leftover can be found (and only ours).</summary>
    public const string Label = "okf4net.test=s3integration";

    /// <summary>The deadline of each administrative call (an upload, a listing): a loaded machine slows them, never hangs them.</summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(60);

    private string? _engine;
    private string? _container;

    public string? SkipReason { get; private set; }

    public Uri Endpoint { get; private set; } = null!;

    public BasicAWSCredentials Credentials { get; } = new("okf" + Guid.NewGuid().ToString("N")[..12], Guid.NewGuid().ToString("N"));

    /// <summary>An independent client (no guard, the SDK's own decoding): it creates the bucket, uploads, deletes and lists as the oracle.</summary>
    public AmazonS3Client Admin { get; private set; } = null!;

    /// <summary>The engine this fixture drives (podman or docker), or <see langword="null"/> when none answers.</summary>
    public string? Engine => _engine;

    public async Task InitializeAsync()
    {
        foreach (var candidate in new[] { "podman", "docker" })
        {
            try
            {
                if ((await Run(candidate, TimeSpan.FromSeconds(20), "version", "--format", "{{.Server.Version}}")).Code == 0)
                {
                    _engine = candidate;
                    break;
                }
            }
            catch (TimeoutException)
            {
                // An engine that does not answer within its deadline is not chosen; the next one is tried.
            }
        }

        if (_engine is null)
        {
            SkipReason = "no container engine answers (podman, docker)";
            return;
        }

        try
        {
            var creds = Credentials.GetCredentials();
            var name = "okf-s3it-" + Guid.NewGuid().ToString("N")[..12];
            var run = await Run(_engine, TimeSpan.FromMinutes(5), "run", "-d", "--rm", "--name", name, "--label", Label, "-p", "127.0.0.1::9000", "-e", "RUSTFS_ACCESS_KEY=" + creds.AccessKey, "-e", "RUSTFS_SECRET_KEY=" + creds.SecretKey, Image);
            if (run.Code != 0)
            {
                // The name is reserved even when the start failed half-way: remove it by name.
                _container = name;
                throw new InvalidOperationException($"{_engine} run failed ({run.Code}): {run.Err.Trim()}");
            }

            _container = run.Out.Trim();
            var portLine = (await Run(_engine, TimeSpan.FromSeconds(20), "port", _container, "9000")).Out
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault(l => l.StartsWith("127.0.0.1:", StringComparison.Ordinal))
                ?? throw new InvalidOperationException($"{_engine} port: no loopback mapping for 9000");
            Endpoint = new Uri($"http://127.0.0.1:{portLine.Split(':')[^1]}");
            Admin = new AmazonS3Client(Credentials, new AmazonS3Config
            {
                ServiceURL = Endpoint.ToString(),
                ForcePathStyle = true,
                AuthenticationRegion = "us-east-1",
                MaxErrorRetry = 0,
                Timeout = TimeSpan.FromSeconds(30),
            });
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                using var attempt = new CancellationTokenSource(TimeSpan.FromSeconds(5)); // each readiness call is bounded too
                try
                {
                    await Admin.PutBucketAsync(new PutBucketRequest { BucketName = Bucket }, attempt.Token);
                    return;
                }
                catch (AmazonS3Exception e) when (e.ErrorCode is "BucketAlreadyOwnedByYou" or "BucketAlreadyExists")
                {
                    return; // an earlier attempt created it, its answer was lost to the attempt's deadline
                }
                catch (Exception) when (DateTime.UtcNow < deadline)
                {
                    await Task.Delay(500);
                }
            }
        }
        catch
        {
            await StopAsync(); // a container that started is removed even when the fixture fails
            throw;
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

    public async Task PutAsync(string key, byte[] bytes)
    {
        using var cts = new CancellationTokenSource(CallTimeout);
        await Admin.PutObjectAsync(new PutObjectRequest { BucketName = Bucket, Key = key, InputStream = new MemoryStream(bytes) }, cts.Token);
    }

    /// <summary>
    /// A PUT whose request path is <c>/okf/</c> + <paramref name="rawPath"/> exactly as given, signed here with SigV4
    /// (UNSIGNED-PAYLOAD) over that same path. The SDK cannot send a key with a leading '/' to RustFS: measured, both its
    /// PutObject and its pre-signed URL are refused 403 SignatureDoesNotMatch, so this is how the test reaches the store's
    /// own normalization of such a path. <paramref name="rawPath"/> must hold only RFC 3986 unreserved characters and '/'.
    /// </summary>
    public async Task<int> PutRawPathAsync(string rawPath, byte[] bytes)
    {
        Assert.All(rawPath, c => Assert.True(char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '/', rawPath));
        var creds = Credentials.GetCredentials();
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMdd'T'HHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture);
        var day = amzDate[..8];
        var path = "/" + Bucket + "/" + rawPath;
        var host = Endpoint.Authority;
        const string signed = "host;x-amz-content-sha256;x-amz-date";
        var canonical = $"PUT\n{path}\n\nhost:{host}\nx-amz-content-sha256:UNSIGNED-PAYLOAD\nx-amz-date:{amzDate}\n\n{signed}\nUNSIGNED-PAYLOAD";
        var scope = $"{day}/us-east-1/s3/aws4_request";
        var toSign = $"AWS4-HMAC-SHA256\n{amzDate}\n{scope}\n{Hex(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(canonical)))}";
        var key = Hmac(System.Text.Encoding.UTF8.GetBytes("AWS4" + creds.SecretKey), day);
        key = Hmac(key, "us-east-1");
        key = Hmac(key, "s3");
        key = Hmac(key, "aws4_request");
        var signature = Hex(Hmac(key, toSign));

        using var http = new HttpClient { Timeout = CallTimeout };
        using var request = new HttpRequestMessage(HttpMethod.Put, new Uri(Endpoint, path)) { Content = new ByteArrayContent(bytes) };
        Assert.Equal(path, request.RequestUri!.AbsolutePath); // sent as signed: .NET keeps the empty segment
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", "UNSIGNED-PAYLOAD");
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("Authorization", $"AWS4-HMAC-SHA256 Credential={creds.AccessKey}/{scope}, SignedHeaders={signed}, Signature={signature}");
        using var response = await http.SendAsync(request);
        return (int)response.StatusCode;

        static byte[] Hmac(byte[] k, string data) => System.Security.Cryptography.HMACSHA256.HashData(k, System.Text.Encoding.UTF8.GetBytes(data));
        static string Hex(byte[] b) => Convert.ToHexStringLower(b);
    }

    public async Task DeleteAsync(string key)
    {
        using var cts = new CancellationTokenSource(CallTimeout);
        await Admin.DeleteObjectAsync(new DeleteObjectRequest { BucketName = Bucket, Key = key }, cts.Token);
    }

    /// <summary>One page (at most 1 000 keys) of the admin client's listing under <paramref name="prefix"/>.</summary>
    public async Task<List<S3Object>> ListAsync(string prefix, EncodingType? encoding = null)
    {
        using var cts = new CancellationTokenSource(CallTimeout);
        var request = new ListObjectsV2Request { BucketName = Bucket, Prefix = prefix };
        if (encoding is not null)
        {
            request.Encoding = encoding;
        }

        var response = await Admin.ListObjectsV2Async(request, cts.Token);
        Assert.False(response.IsTruncated ?? false, "the oracle reads one page");
        return response.S3Objects ?? [];
    }

    public async Task StopAsync()
    {
        if (_engine is not null && _container is not null)
        {
            var container = _container;
            _container = null;
            await Run(_engine, TimeSpan.FromSeconds(60), "rm", "-f", container);
        }
    }

    public async Task DisposeAsync()
    {
        Admin?.Dispose();
        await StopAsync();
    }

    ValueTask IAsyncDisposable.DisposeAsync() => new(DisposeAsync());

    /// <summary>Through <see cref="TestProcess"/> (both streams drained, tree killed at the deadline); a missing binary is code -1.</summary>
    private static async Task<(int Code, string Out, string Err)> Run(string file, TimeSpan deadline, params string[] args)
    {
        try
        {
            return await TestProcess.RunAsync(file, args, null, deadline);
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
