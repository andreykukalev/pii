using System.Net;
using System.Text;

namespace Pii.Infrastructure.Tests;

internal sealed class RecordedHttp(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
{
    public int Calls { get; private set; }
    public List<string> Bodies { get; } = [];
    public List<Uri> Uris { get; } = [];
    public List<string?> Authorizations { get; } = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
    {
        cancellation.ThrowIfCancellationRequested();
        Uris.Add(request.RequestUri!);
        Authorizations.Add(request.Headers.TryGetValues("Authorization", out var values) ? string.Join(' ', values) : null);
        Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(cancellation));
        return respond(request, ++Calls);
    }
    public static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json")
    };
}