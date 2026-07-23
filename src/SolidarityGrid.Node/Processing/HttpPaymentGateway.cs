using System.Net.Http.Json;

namespace SolidarityGrid.Node.Processing;

/// <summary>
/// Llama al PSP con la Idempotency-Key = txId. El deadline generoso (30s) vive en
/// la configuracion del HttpClient nombrado "psp": el adquirente es lento por
/// diseño, no por fallo, asi que no queremos que un timeout corto lo confunda con
/// una caida.
/// </summary>
public sealed class HttpPaymentGateway : IPaymentGateway
{
    private readonly IHttpClientFactory _httpClientFactory;

    public HttpPaymentGateway(IHttpClientFactory httpClientFactory) => _httpClientFactory = httpClientFactory;

    public async Task<PaymentResult> ChargeAsync(string txId, decimal amount, string currency, CancellationToken ct)
    {
        var client = _httpClientFactory.CreateClient("psp");

        using var request = new HttpRequestMessage(HttpMethod.Post, "charge");
        request.Headers.Add("Idempotency-Key", txId);
        request.Content = JsonContent.Create(new { amount, currency });

        using var response = await client.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        return await response.Content.ReadFromJsonAsync<PaymentResult>(cancellationToken: ct)
               ?? throw new InvalidOperationException("El PSP devolvió un cuerpo vacío.");
    }
}
