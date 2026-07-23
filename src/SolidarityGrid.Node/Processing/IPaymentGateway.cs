namespace SolidarityGrid.Node.Processing;

public record PaymentResult(string AuthCode, bool Replayed);

/// <summary>
/// Puerto hacia el adquirente. Impl unica pero justificada: aisla la llamada
/// lenta al PSP para poder probar el procesador sin red ni esperas reales.
/// </summary>
public interface IPaymentGateway
{
    Task<PaymentResult> ChargeAsync(string txId, decimal amount, string currency, CancellationToken ct);
}
