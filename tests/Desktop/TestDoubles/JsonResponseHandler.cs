using System.Net;
using System.Text;

namespace RTXLens.UI.Tests.TestDoubles;

internal sealed class JsonResponseHandler(Func<HttpRequestMessage, string> response) : HttpMessageHandler
{
    public JsonResponseHandler(string json) : this(_ => json) { }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
        Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response(request), Encoding.UTF8, "application/json") });
}
