using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using SolidarityGrid.Node.Configuration;
using SolidarityGrid.Node.Domain;
using SolidarityGrid.Node.Infrastructure;
using SolidarityGrid.Node.Mesh;

namespace SolidarityGrid.Node.Processing;

/// <summary>
/// Orquesta el ciclo de vida de un pago: Received -> Processing -> Completed,
/// propagando cada cambio de inmediato (sin esperar al siguiente latido) y
/// llamando al PSP entre medias.
/// </summary>
public sealed class PaymentProcessor
{
    private readonly ILedger _ledger;
    private readonly IPeerTransport _transport;
    private readonly IPaymentGateway _gateway;
    private readonly NodeOptions _options;
    private readonly ILogger<PaymentProcessor> _logger;

    // Trabajos en vuelo por txId. El bloque 5 lo usara para cancelar el trabajo
    // cuyo epoch haya sido superado por un relevo.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _inFlight = new();

    public PaymentProcessor(
        ILedger ledger,
        IPeerTransport transport,
        IPaymentGateway gateway,
        IOptions<NodeOptions> options,
        ILogger<PaymentProcessor> logger)
    {
        _ledger = ledger;
        _transport = transport;
        _gateway = gateway;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Lanza el procesamiento en segundo plano; retorna de inmediato.</summary>
    public void Begin(string txId, string currency)
    {
        var cts = new CancellationTokenSource();
        if (!_inFlight.TryAdd(txId, cts))
        {
            cts.Dispose();
            return; // ya hay un trabajo en vuelo para esta tx
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await ProcessAsync(txId, currency, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Trabajo abortado por un epoch superior (bloque 5).
            }
            catch (Exception ex)
            {
                _logger.LogWarning("{Tx} falló durante el proceso: {Error}", txId, ex.Message);
            }
            finally
            {
                _inFlight.TryRemove(txId, out _);
                cts.Dispose();
            }
        });
    }

    public async Task ProcessAsync(string txId, string currency, CancellationToken ct)
    {
        if (!_ledger.TryGet(txId, out var entry))
        {
            return;
        }

        var processing = _ledger.Apply(entry with { State = TxState.Processing });
        _logger.LogInformation("{Tx} en proceso. Contactando al adquirente.", txId);
        await PropagateAsync(processing, ct);

        var result = await _gateway.ChargeAsync(txId, entry.Amount, currency, ct);

        var completed = _ledger.Apply(processing with { State = TxState.Completed, AuthCode = result.AuthCode });
        _logger.LogInformation("{Tx} completada. Autorización {AuthCode}.", txId, result.AuthCode);
        await PropagateAsync(completed, ct);
    }

    private async Task PropagateAsync(LedgerEntry entry, CancellationToken ct)
    {
        if (_options.Peers.Count == 0)
        {
            return;
        }

        var digest = new NodeDigest(_options.NodeId, new[] { entry });
        var sends = _options.Peers.Select(peer => _transport.SendDigestAsync(peer, digest, ct));
        await Task.WhenAll(sends);
    }
}
