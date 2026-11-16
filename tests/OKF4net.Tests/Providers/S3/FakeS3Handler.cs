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
