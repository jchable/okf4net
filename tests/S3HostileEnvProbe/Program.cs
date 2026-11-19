// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using OKF4net;
using OKF4net.Providers.S3;

// The child process of HostileEnvironmentTests (spec T1 "Environnement hostile", D2, D3). Arguments:
//
//   <s3 port> <evil port> <proxy port> <udp port> <mode: provider|plain>
//   sleep <pid file>        (TestProcessTests only: starts a grandchild, writes both PIDs, then sleeps)
//
// It listens on four loopback ports the test passes in (identical for every run, so the Host header compares):
//   s3    — a one-object bucket (kb/a.md), the endpoint every client is configured with;
//   evil  — what the hostile environment names as AWS_ENDPOINT_URL_S3 / AWS_ENDPOINT_URL / endpoint_url;
//   proxy — what the hostile environment names as HTTP_PROXY / HTTPS_PROXY;
//   udp   — what the hostile environment names as the CSM destination (AWS_CSM_PORT / csm_port).
// Every TCP connection serves one request (Connection: close, one task per connection, so concurrent reads never wait
// on an idle connection). For every request it records the listener, the request line and every header except the
// three that two runs cannot share — x-amz-date, amz-sdk-invocation-id and the signature itself: Authorization is
// printed as its credential scope and its SignedHeaders list, which with the request line and the signed headers'
// values (printed verbatim, like every other header) are every canonical input of the signature but the date
// (maintainer's ruling 2026-11-16, option A: two runs with equal lines produce equal signatures for an equal date).
// No part of the User-Agent varies per process (gate G2 recorded none, and this probe measured none), so nothing in
// it is stripped.
//
// Mode "provider" loads the bundle through S3BundleProvider's PUBLIC constructor (D10 grants internals to
// OKF4net.Tests alone) and prints "concepts=<n>", or "load-error=<type>[:<kind>]".
// Mode "plain" is the POSITIVE WITNESS: plain AmazonS3Clients configured as a careless host would, one scenario per
// key (the key names the scenario in the recorded line), which must show the ambient effects the provider suppresses.
// Their HTTP stack is the BCL default (HttpClientHandler, default proxy) behind a safety net that refuses, before any
// DNS lookup or connection, anything whose next hop is not a loopback address: the witness never leaves the machine,
// whatever the environment says. Each scenario prints "plain <scenario>=ok" or "plain <scenario>=error:<type>:<message>",
// and every hop the safety net refused as "plain <scenario> refused-hop=<host>".
//
// Then, after a settle delay for late CSM datagrams, the recorded lines sorted ordinally, and "UDP <n>".
if (args is ["sleep", var pidFile])
{
    return await SleepAsync(pidFile);
}

if (args is ["sleep-leaf"])
{
    await Task.Delay(TimeSpan.FromMinutes(2));
    return 0;
}

if (args.Length != 5)
{
    Console.Error.WriteLine("usage: S3HostileEnvProbe <s3 port> <evil port> <proxy port> <udp port> <provider|plain>");
    return 3;
}

var s3Port = int.Parse(args[0], CultureInfo.InvariantCulture);
var evilPort = int.Parse(args[1], CultureInfo.InvariantCulture);
var proxyPort = int.Parse(args[2], CultureInfo.InvariantCulture);
var udpPort = int.Parse(args[3], CultureInfo.InvariantCulture);
var mode = args[4];
if (mode is not ("provider" or "plain"))
{
    Console.Error.WriteLine("unknown mode: " + mode);
    return 3;
}

using var udp = new UdpClient(new IPEndPoint(IPAddress.Loopback, udpPort));
var datagrams = 0;
_ = Task.Run(async () =>
{
    while (true)
    {
        await udp.ReceiveAsync();
        Interlocked.Increment(ref datagrams);
    }
});

var body = Encoding.UTF8.GetBytes("---\ntype: Note\ntitle: T\ndescription: D.\n---\n\nBody text.\n");
var listing = Encoding.UTF8.GetBytes(
    "<?xml version=\"1.0\" encoding=\"UTF-8\"?><ListBucketResult><EncodingType>url</EncodingType><IsTruncated>false</IsTruncated><MaxKeys>1000</MaxKeys>"
    + $"<Contents><Key>kb/a.md</Key><ETag>\"e\"</ETag><Size>{body.Length}</Size></Contents></ListBucketResult>");
var lines = new List<string>();
var listeners = new List<TcpListener>();
foreach (var (name, port) in new[] { ("s3", s3Port), ("evil", evilPort), ("proxy", proxyPort) })
{
    var listener = new TcpListener(new IPEndPoint(IPAddress.Loopback, port));

    // Every run rebinds the same ports right after the previous run closed its connections. On Linux and macOS,
    // SO_REUSEADDR only lets the bind pass sockets left in TIME_WAIT. On Windows it would let this socket bind a port
    // that another process (a stale probe) still listens on, so the requests could reach either one. Windows needs no
    // option for TIME_WAIT, and there the port is taken exclusively: a stale listener makes Start fail loudly.
    if (OperatingSystem.IsWindows())
    {
        listener.ExclusiveAddressUse = true;
    }
    else
    {
        listener.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
    }

    listener.Start();
    listeners.Add(listener);
    _ = Task.Run(async () =>
    {
        while (true)
        {
            var client = await listener.AcceptTcpClientAsync();
            _ = Task.Run(() => ServeOneAsync(name, client, lines, listing, body));
        }
    });
}

using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(45));
var results = new List<string>();
var creds = new BasicAWSCredentials("AKIDEXAMPLE", "secret");
var endpoint = new Uri($"http://127.0.0.1:{s3Port}");
if (mode == "provider")
{
    try
    {
        using var provider = new S3BundleProvider(creds, new S3BundleSource { Bucket = "okf", Prefix = "kb", ServiceUrl = endpoint, ForcePathStyle = true, AllowInsecureHttp = true });
        var bundle = await Bundle.LoadAsync(provider, null, deadline.Token);
        results.Add($"concepts={bundle.Concepts.Count}");
    }
    catch (Exception e)
    {
        results.Add("load-error=" + LoadError(e));
    }
}
else
{
    // ua: the configured endpoint, explicitly. Carries the User-Agent, the retry header and the checksum header.
    await PlainAsync("ua", () => new AmazonS3Config { ServiceURL = endpoint.ToString(), ForcePathStyle = true, AuthenticationRegion = "us-east-1" });

    // endpoint: a region and no ServiceURL — an explicit ServiceURL wins over the ambient endpoint settings (measured),
    // so this is the configuration they redirect.
    await PlainAsync("endpoint", () => new AmazonS3Config { RegionEndpoint = RegionEndpoint.USEast1, ForcePathStyle = true });

    // proxy: a host name no resolver gives (".invalid", RFC 6761), so only a proxy can carry the request anywhere.
    await PlainAsync("proxy", () => new AmazonS3Config { ServiceURL = $"http://okf-witness.invalid:{s3Port}", ForcePathStyle = true, AuthenticationRegion = "us-east-1" });

    // imds: ONLY when the environment names an instance-metadata endpoint. A client given NO credentials ends its fallback
    // chain at the instance-metadata service, which the environment then points at the "evil" listener: the witness that
    // such a setting is really read, and that a client that resolves ambient credentials does contact it. (The metadata
    // service's own HTTP stack is not the safety net's, but its destination here is a loopback port.)
    if (Environment.GetEnvironmentVariable("AWS_EC2_METADATA_SERVICE_ENDPOINT") is not null)
    {
        await PlainAsync("imds", () => new AmazonS3Config { ServiceURL = endpoint.ToString(), ForcePathStyle = true, AuthenticationRegion = "us-east-1" }, withCredentials: false);
    }
}

await Task.Delay(1000); // let a late CSM datagram arrive if one was sent
lock (results)
{
    foreach (var l in results)
    {
        Console.WriteLine(l);
    }
}

lock (lines)
{
    foreach (var l in lines.Order(StringComparer.Ordinal))
    {
        Console.WriteLine(l);
    }
}

Console.WriteLine($"UDP {Volatile.Read(ref datagrams)}");
foreach (var l in listeners)
{
    l.Stop();
}

return 0;

async Task PlainAsync(string scenario, Func<AmazonS3Config> configure, bool withCredentials = true)
{
    try
    {
        // Built inside the try: an ambient setting the SDK cannot parse fails the configuration itself (measured).
        var config = configure();
        config.HttpClientFactory = new SafetyNetFactory(results, scenario);
        using var plain = withCredentials ? new AmazonS3Client(creds, config) : new AmazonS3Client(config);
        using var _ = await plain.GetObjectAsync(new GetObjectRequest { BucketName = "okf", Key = "kb/" + scenario + ".md" }, deadline.Token);
        lock (results)
        {
            results.Add($"plain {scenario}=ok");
        }
    }
    catch (Exception e)
    {
        lock (results)
        {
            results.Add($"plain {scenario}=error:{e.GetType().Name}:{e.Message.ReplaceLineEndings(" ")}");
        }
    }
}

// The failure and, through its inner exceptions, the provider's own S3RequestException kind if there is one.
static string LoadError(Exception e)
{
    var text = e.GetType().Name;
    for (var inner = e; inner is not null; inner = inner.InnerException)
    {
        if (inner is S3RequestException s3)
        {
            return $"{text}:{s3.Kind}";
        }
    }

    return text;
}

static async Task ServeOneAsync(string listener, TcpClient client, List<string> lines, byte[] listing, byte[] body)
{
    using (client)
    {
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.ASCII);
        var requestLine = await reader.ReadLineAsync() ?? "";
        var headers = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string? h;
        while ((h = await reader.ReadLineAsync()) is { Length: > 0 })
        {
            var i = h.IndexOf(':');
            headers[h[..i].Trim().ToLowerInvariant()] = h[(i + 1)..].Trim();
        }

        Recorded.Add(lines, listener, requestLine, headers);
        var payload = requestLine.Contains("list-type=2", StringComparison.Ordinal) ? listing : body;
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Length: {payload.Length}\r\nContent-Type: application/xml\r\nConnection: close\r\n\r\n"));
        await stream.WriteAsync(payload);
    }
}

// TestProcessTests: a process tree that never ends by itself — this process, and a grandchild it starts — whose two
// PIDs are written to <pid file> (atomically, by a rename) so the test can check that a deadline kills both.
static async Task<int> SleepAsync(string pidFile)
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
    start.ArgumentList.Add(typeof(SafetyNet).Assembly.Location);
    start.ArgumentList.Add("sleep-leaf");
    using var leaf = Process.Start(start)!;
    File.WriteAllText(pidFile + ".tmp", $"{Environment.ProcessId}\n{leaf.Id}\n");
    File.Move(pidFile + ".tmp", pidFile);
    await Task.Delay(TimeSpan.FromMinutes(2));
    return 0;
}

/// <summary>One recorded request: every header but the date, the invocation id and the signature; Authorization as its scope and SignedHeaders.</summary>
internal static class Recorded
{
    public static void Add(List<string> lines, string listener, string requestLine, SortedDictionary<string, string> headers)
    {
        var auth = headers.GetValueOrDefault("authorization") ?? "";
        var scope = auth.Split(',')[0];
        var signed = auth.Contains("SignedHeaders=", StringComparison.Ordinal) ? auth.Split("SignedHeaders=")[^1].Split(',')[0] : "";
        var shown = headers.Where(p => p.Key is not ("x-amz-date" or "amz-sdk-invocation-id" or "authorization")).Select(p => $"{p.Key}={p.Value}");
        var line = $"{listener} | {requestLine} | auth={scope} | signed={signed} | {string.Join(" | ", shown)}";
        lock (lines)
        {
            lines.Add(line);
        }
    }
}

/// <summary>The plain witness's HTTP stack: the BCL default handler (default proxy included) behind a loopback-only safety net.</summary>
internal sealed class SafetyNetFactory(List<string> results, string scenario) : HttpClientFactory
{
    public override HttpClient CreateHttpClient(IClientConfig clientConfig) =>
        new(new SafetyNet(results, scenario) { InnerHandler = new HttpClientHandler() });

    public override bool UseSDKHttpClientCaching(IClientConfig clientConfig) => false;

    public override bool DisposeHttpClientsAfterUse(IClientConfig clientConfig) => true;

    public override string? GetConfigUniqueString(IClientConfig clientConfig) => null;
}

/// <summary>Refuses, before any DNS lookup or connection, a request whose next hop (the proxy if one applies) is not a loopback address.</summary>
internal sealed class SafetyNet(List<string> results, string scenario) : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var uri = request.RequestUri!;
        var proxy = HttpClient.DefaultProxy;
        var hop = proxy.IsBypassed(uri) ? uri : proxy.GetProxy(uri) ?? uri;
        if (!IPAddress.TryParse(hop.Host, out var ip) || !IPAddress.IsLoopback(ip))
        {
            lock (results)
            {
                results.Add($"plain {scenario} refused-hop={hop.Host}");
            }

            throw new HttpRequestException("safety net: not a loopback hop");
        }

        return base.SendAsync(request, cancellationToken);
    }
}
