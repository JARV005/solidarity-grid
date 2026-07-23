using System.Net;
using SolidarityGrid.Node.Domain;
using SolidarityGrid.Node.Mesh;
using Xunit;

namespace SolidarityGrid.Node.Tests;

public class HttpPeerTransportTests
{
    private static NodeDigest EmptyDigest() => new("node-self", Array.Empty<LedgerEntry>());

    [Fact]
    public async Task Returns_true_when_peer_accepts_the_digest()
    {
        var transport = TransportThat((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NoContent)));

        Assert.True(await transport.SendDigestAsync("node-1", EmptyDigest(), CancellationToken.None));
    }

    [Fact]
    public async Task Returns_false_when_peer_is_unreachable()
    {
        var transport = TransportThat((_, _) =>
            Task.FromException<HttpResponseMessage>(new HttpRequestException("connection refused")));

        Assert.False(await transport.SendDigestAsync("node-1", EmptyDigest(), CancellationToken.None));
    }

    [Fact]
    public async Task Returns_false_when_peer_returns_an_error_status()
    {
        var transport = TransportThat((_, _) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError)));

        Assert.False(await transport.SendDigestAsync("node-1", EmptyDigest(), CancellationToken.None));
    }

    [Fact]
    public async Task Returns_false_when_the_call_exceeds_the_deadline()
    {
        // El handler respeta el token; el deadline de 500ms lo cancela y el
        // transporte lo traduce a false en vez de dejar burbujear la excepcion.
        var transport = TransportThat(async (_, ct) =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        Assert.False(await transport.SendDigestAsync("node-1", EmptyDigest(), CancellationToken.None));
    }

    private static HttpPeerTransport TransportThat(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> behavior) =>
        new(new StubHttpClientFactory(new StubHandler(behavior)));

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _behavior;

        public StubHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> behavior) =>
            _behavior = behavior;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) =>
            _behavior(request, ct);
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
