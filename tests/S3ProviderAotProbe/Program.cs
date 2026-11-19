// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OKF4net;
using OKF4net.Providers;
using OKF4net.Providers.S3;

var root = Path.GetFullPath(args[0]);
var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
    .ToDictionary(f => "kb/" + Path.GetRelativePath(root, f).Replace('\\', '/'), File.ReadAllBytes, StringComparer.Ordinal);

// The "other" server: target of every redirect; records the Host and path of what reaches it.
var reached = new List<string>();
using var other = Server.Start(req => { lock (reached) reached.Add(req.Host + " " + req.Path); return Server.Ok("X"u8.ToArray()); });

// The main server: the bucket, or (in redirect mode) a 307 to the location the test sets.
string? redirectTo = null;
using var main = Server.Start(req => Volatile.Read(ref redirectTo) is { } loc ? Server.Redirect(loc) : Server.Answer(req, files));

// 0. The server serves concurrent requests (the load relies on it).
using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) })
{
    var gets = Enumerable.Range(0, 8).Select(_ => http.GetAsync($"http://127.0.0.1:{main.Port}/okf/kb/{files.Keys.First()["kb/".Length..]}"));
    var answered = await Task.WhenAll(gets).WaitAsync(TimeSpan.FromSeconds(10));
    if (answered.Any(r => r.StatusCode != HttpStatusCode.OK)) return 4;
}

// 1. Summaries: S3 provider (public API) vs filesystem.
var clock = new FixedClock(new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero));
var creds = new BasicAWSCredentials("AKID", "secret");
var source = new S3BundleSource
{
    Bucket = "okf",
    Prefix = "kb",
    ServiceUrl = new Uri($"http://127.0.0.1:{main.Port}"),
    ForcePathStyle = true,
    AllowInsecureHttp = true,
    RequestTimeout = TimeSpan.FromSeconds(15),
};
using var s3 = new S3BundleProvider(creds, source);
var a = await Summary(await Bundle.LoadAsync(s3), clock);
var b = await Summary(await Bundle.LoadAsync(new FileSystemBundleProvider(root)), clock);
foreach (var line in a) Console.WriteLine(line);
if (!a.SequenceEqual(b)) { Console.Error.WriteLine("S3 and filesystem summaries differ"); return 1; }

// 2. Two redirects, each refused by the guard UNDER AOT, then followed by a witness client without the guard.
//    Case A: another bucket's host. Case B: the other authority of the SAME bucket (virtual-hosted form of a
//    path-style source). The SDK refuses to follow a Location whose host is no S3 endpoint (ArgumentException, gate G4:
//    okf.127.0.0.1 measured so on 4.1.0), so case B names the buckets virtual-hosted AWS authority, which it follows.
var cases = new[]
{
    (Name: "other-host", Location: $"http://evil.s3.eu-west-1.amazonaws.com:{other.Port}/kb/a.md", Host: "evil.s3.eu-west-1.amazonaws.com"),
    (Name: "same-bucket-other-authority", Location: $"http://okf.s3.eu-west-1.amazonaws.com:{other.Port}/kb/a.md", Host: "okf.s3.eu-west-1.amazonaws.com"),
};
foreach (var c in cases)
{
    Volatile.Write(ref redirectTo, c.Location);
    try
    {
        using var o = await s3.ReadAsync(BundleKey.Parse(files.Keys.First()["kb/".Length..]), null, default);
        Console.Error.WriteLine($"guard did not refuse ({c.Name})");
        return 2;
    }
    catch (S3RequestException e) when (e.Kind == S3FailureKind.RefusedHost)
    {
        Console.WriteLine($"guard[{c.Name}]=refused");
    }
    catch (S3RequestException e)
    {
        Console.Error.WriteLine($"guard did not refuse ({c.Name}): {e.Message}");
        return 2;
    }

    lock (reached) reached.Clear();
    // The witness: a plain client, no guard; DNS for the redirect host goes to the "other" server, every other
    // name to the main one, by ConnectCallback — no hosts file, no proxy, plain http.
    var handler = new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        UseProxy = false,
        ConnectCallback = async (ctx, ct) =>
        {
            var port = string.Equals(ctx.DnsEndPoint.Host, c.Host, StringComparison.OrdinalIgnoreCase) ? other.Port : main.Port;
            var socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), ct);
            return new NetworkStream(socket, ownsSocket: true);
        },
    };
    using var witness = new AmazonS3Client(creds, new AmazonS3Config
    {
        ServiceURL = $"http://127.0.0.1:{main.Port}",
        ForcePathStyle = true,
        AuthenticationRegion = "us-east-1",
        HttpClientFactory = new Fixed(new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(10) }),
    });
    using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
    {
        try { using var _ = await witness.GetObjectAsync(new GetObjectRequest { BucketName = "okf", Key = files.Keys.First() }, deadline.Token); }
        catch (AmazonServiceException) { }
        catch (ArgumentException) { }
    }

    lock (reached)
    {
        // The SDK re-addresses the same key on the new authority, the bucket now taken from its host: another object than the one asked for.
        if (reached.Count != 1 || !reached[0].StartsWith(c.Host, StringComparison.OrdinalIgnoreCase))
        {
            Console.Error.WriteLine($"counter-proof [{c.Name}] reached: {string.Join(", ", reached)}");
            return 3;
        }

        Console.WriteLine($"counter-proof[{c.Name}]={reached[0]}");
    }
}

Volatile.Write(ref redirectTo, null);
return 0;

static async Task<string[]> Summary(Bundle bundle, IOkfClock clock)
{
    var report = await BundleValidator.ValidateAsync(bundle, clock);
    return [
        $"concepts={bundle.Concepts.Count}",
        .. bundle.Concepts.Select(c => "key=" + c.Key).Order(StringComparer.Ordinal),
        .. report.Diagnostics.Select(d => $"diag={d.Code}|{d.Severity}|{d.Key}").Order(StringComparer.Ordinal),
        $"audit={ConceptAudit.Run(bundle, default, clock).Findings.Count}",
    ];
}

sealed class Fixed(HttpClient c) : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig) => c;
    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;
    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => false;
}

sealed record Request(string Method, string Host, string Path, string Query);

/// <summary>A minimal HTTP/1.1 server: one task per accepted connection, one request per connection (Connection: close).</summary>
sealed class Server : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly CancellationTokenSource _stop = new();

    private Server(Func<Request, byte[]> respond)
    {
        _listener.Start();
        var stop = _stop.Token;
        _ = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(stop); } catch (Exception) { return; }
                _ = Task.Run(async () =>
                {
                    using (client)
                    using (var read = CancellationTokenSource.CreateLinkedTokenSource(stop))
                    {
                        read.CancelAfter(TimeSpan.FromSeconds(10)); // a silent client never holds a task for long
                        try
                        {
                            var stream = client.GetStream();
                            var reader = new StreamReader(stream, Encoding.ASCII);
                            var line = (await reader.ReadLineAsync(read.Token) ?? "").Split(' ');
                            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                            string? h;
                            while ((h = await reader.ReadLineAsync(read.Token)) is { Length: > 0 }) { var i = h.IndexOf(':'); headers[h[..i].Trim()] = h[(i + 1)..].Trim(); }
                            var target = line.Length > 1 ? line[1] : "/";
                            var q = target.IndexOf('?');
                            var req = new Request(line[0], (headers.GetValueOrDefault("Host") ?? "").Split(':')[0], q < 0 ? target : target[..q], q < 0 ? "" : target[q..]);
                            await stream.WriteAsync(respond(req), read.Token);
                        }
                        catch (Exception) when (read.IsCancellationRequested) { }
                        catch (IOException) { }
                    }
                });
            }
        });
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public static Server Start(Func<Request, byte[]> respond) => new(respond);

    public static byte[] Ok(byte[] body, string type = "application/octet-stream") =>
        [.. Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {body.Length}\r\nContent-Type: {type}\r\nConnection: close\r\n\r\n"), .. body];

    public static byte[] Redirect(string location) =>
        Encoding.ASCII.GetBytes($"HTTP/1.1 307 Temporary Redirect\r\nLocation: {location}\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");

    public static byte[] Answer(Request req, Dictionary<string, byte[]> files)
    {
        if (req.Query.Contains("list-type=2", StringComparison.Ordinal))
        {
            var sb = new StringBuilder("<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult><EncodingType>url</EncodingType><IsTruncated>false</IsTruncated><MaxKeys>1000</MaxKeys>");
            var prefix = Uri.UnescapeDataString(req.Query.Split('&').FirstOrDefault(p => p.TrimStart('?').StartsWith("prefix=", StringComparison.Ordinal))?.Split('=')[1] ?? "");
            foreach (var key in files.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).Order(Comparer<string>.Create(CompareByCodePoint)))
            {
                sb.Append("<Contents><Key>").Append(Encode(key)).Append("</Key><ETag>\"").Append(Convert.ToHexStringLower(MD5.HashData(files[key])))
                  .Append("\"</ETag><Size>").Append(files[key].Length).Append("</Size></Contents>");
            }

            return Ok(Encoding.UTF8.GetBytes(sb.Append("</ListBucketResult>").ToString()), "application/xml");
        }

        var path = Uri.UnescapeDataString(req.Path); // the probe's own test data has no hostile names
        return path.StartsWith("/okf/", StringComparison.Ordinal) && files.TryGetValue(path["/okf/".Length..], out var bytes)
            ? Ok(bytes)
            : Encoding.ASCII.GetBytes("HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
    }

    private static string Encode(string key) =>
        string.Concat(Encoding.UTF8.GetBytes(key).Select(b => char.IsAsciiLetterOrDigit((char)b) || "-._~/".Contains((char)b) ? ((char)b).ToString() : "%" + b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    private static int CompareByCodePoint(string x, string y)
    {
        var (ex, ey) = (x.EnumerateRunes(), y.EnumerateRunes());
        while (true)
        {
            var (hx, hy) = (ex.MoveNext(), ey.MoveNext());
            if (!hx || !hy) return hx == hy ? 0 : hx ? 1 : -1;
            var c = ex.Current.Value.CompareTo(ey.Current.Value);
            if (c != 0) return c;
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        _stop.Dispose();
    }
}
