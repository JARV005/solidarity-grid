using System.Net.Http.Json;

namespace SolidarityGrid.Node.Mesh;

/// <summary>
/// Transporte de gossip sobre HTTP/1.1. Es la red de seguridad del proyecto: si
/// gRPC falla, el sistema sigue operativo con esto.
/// </summary>
public sealed class HttpPeerTransport : IPeerTransport
{
    // El gossip viaja por el puerto REST. El 8081 queda reservado para gRPC.
    private const int GossipPort = 8080;
    private static readonly TimeSpan CallTimeout = TimeSpan.FromMilliseconds(500);

    private readonly IHttpClientFactory _httpClientFactory;

    public HttpPeerTransport(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task<bool> SendDigestAsync(string peer, NodeDigest digest, CancellationToken ct)
    {
        // Deadline duro de 500ms por llamada: un peer muerto no puede congelar al
        // broadcaster. El token externo respeta el apagado del proceso.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CallTimeout);

        try
        {
            var client = _httpClientFactory.CreateClient("gossip");
            var url = $"http://{peer}:{GossipPort}/internal/gossip";
            using var response = await client.PostAsJsonAsync(url, digest, timeout.Token);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false; // peer inalcanzable
        }
        catch (OperationCanceledException)
        {
            return false; // timeout de 500ms (TaskCanceledException) o apagado
        }
    }
}
