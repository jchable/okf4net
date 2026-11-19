// SPDX-License-Identifier: LGPL-3.0-or-later
using System.Net;
using System.Net.Sockets;
using OKF4net.Providers.S3.Internal;

namespace OKF4net.Tests.Providers.S3;

/// <summary>The network handler of a real provider (spec §4.4, minors A3): no ambient state carried between requests, no unbounded pooling.</summary>
public class NetworkHandlerTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>
    /// A real loopback store: every response sets a cookie, and the Cookie header of every request is recorded.
    /// Returns what the SECOND request carried (null when it had no Cookie header).
    /// </summary>
    private static async Task<string?> CookieSentOnSecondRequest(HttpMessageHandler handler)
    {
        var port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var seen = new List<string?>();
        var serving = Task.Run(async () =>
        {
            for (var i = 0; i < 2; i++)
            {
                var context = await listener.GetContextAsync();
                seen.Add(context.Request.Headers["Cookie"]);
                context.Response.AddHeader("Set-Cookie", "store-session=SECRET-CANARY; Path=/");
                context.Response.StatusCode = 200;
                context.Response.Close();
            }
        });

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using (var http = new HttpClient(handler, disposeHandler: true))
        {
            for (var i = 0; i < 2; i++)
            {
                using var response = await http.GetAsync($"http://127.0.0.1:{port}/okf/kb/a.md", deadline.Token);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            }
        }

        await serving.WaitAsync(deadline.Token);
        Assert.Null(seen[0]); // nothing to replay on the first request, whatever the handler
        return seen[1];
    }

    [Fact]
    public async Task A_cookie_a_store_sets_is_never_replayed_on_a_later_request()
    {
        Assert.Null(await CookieSentOnSecondRequest(ClientBuilder.Network()));
    }

    [Fact]
    public async Task Counter_proof_a_default_handler_does_replay_the_cookie_so_the_witness_is_positive()
    {
        var replayed = await CookieSentOnSecondRequest(new SocketsHttpHandler { AllowAutoRedirect = false, UseProxy = false });
        Assert.Equal("store-session=SECRET-CANARY", replayed);
    }

    [Fact]
    public void Smoke_check_only_the_handler_has_cookies_off_and_a_finite_connection_lifetime()
    {
        // NOT the proof (the loopback tests above are: a property can be set and still not take effect). It pins the two
        // settings so that a change to either is a visible edit. Lifetime: a pooled connection is replaced after at most
        // two minutes, so a long-lived host re-resolves DNS; the default is infinite.
        using var handler = ClientBuilder.Network();
        Assert.False(handler.UseCookies);
        Assert.Equal(TimeSpan.FromMinutes(2), handler.PooledConnectionLifetime);
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseProxy);
    }
}
