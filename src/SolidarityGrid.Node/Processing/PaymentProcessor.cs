using System.Collections.Concurrent;
using SolidarityGrid.Node.Domain;
using SolidarityGrid.Node.Infrastructure;
using SolidarityGrid.Node.Mesh;

namespace SolidarityGrid.Node.Processing;

/// <summary>
/// Orquesta el ciclo de vida de un pago y hace de fencing. Distingue dos caminos:
/// el del dueño original (Received -> Processing -> Completed) y el del relevo,
/// donde el supervisor ya dejó la entrada en Processing con epoch+1.
/// </summary>
public sealed class PaymentProcessor
{
    private readonly ILedger _ledger;
    private readonly IPaymentGateway _gateway;
    private readonly PeerReplicator _replicator;
    private readonly ILogger<PaymentProcessor> _logger;

    // Trabajos en vuelo por txId, con el epoch bajo el que se ejecutan. El epoch es
    // lo que permite el fencing: un epoch entrante mayor cancela este trabajo.
    private readonly ConcurrentDictionary<string, InFlight> _inFlight = new();

    public PaymentProcessor(
        ILedger ledger,
        IPaymentGateway gateway,
        PeerReplicator replicator,
        ILogger<PaymentProcessor> logger)
    {
        _ledger = ledger;
        _gateway = gateway;
        _replicator = replicator;
        _logger = logger;
    }

    /// <summary>Camino del dueño original.</summary>
    public void Begin(string txId, string currency) => Launch(txId, currency, takenOver: false);

    /// <summary>Camino del relevo: el supervisor ya transiciono a Processing con epoch+1.</summary>
    public void Resume(string txId, string currency) => Launch(txId, currency, takenOver: true);

    public bool IsProcessing(string txId) => _inFlight.ContainsKey(txId);

    /// <summary>
    /// Fencing. Si por gossip llega una entrada con epoch mayor que el de nuestro
    /// trabajo en vuelo, ya no somos el dueño: abandonamos. El epoch es el token;
    /// no se negocia, el mayor gana.
    /// </summary>
    public void ApplyFencing(LedgerEntry incoming)
    {
        if (_inFlight.TryGetValue(incoming.TxId, out var work) && incoming.Epoch > work.Epoch)
        {
            _logger.LogInformation(
                "{Tx} fue relevada (epoch {Mine} -> {Winner}, ahora dueño {Owner}). Abandono mi trabajo en vuelo.",
                incoming.TxId, work.Epoch, incoming.Epoch, incoming.Owner);
            work.Cts.Cancel();
        }
    }

    private void Launch(string txId, string currency, bool takenOver)
    {
        if (!_ledger.TryGet(txId, out var entry))
        {
            return;
        }

        var cts = new CancellationTokenSource();
        if (!_inFlight.TryAdd(txId, new InFlight(entry.Epoch, cts)))
        {
            cts.Dispose();
            return; // ya hay trabajo en vuelo para esta tx
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(txId, currency, takenOver, cts.Token);
            }
            catch (OperationCanceledException)
            {
                // Cancelado por fencing: ApplyFencing ya lo narró.
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

    internal async Task RunAsync(string txId, string currency, bool takenOver, CancellationToken ct)
    {
        if (!_ledger.TryGet(txId, out var entry))
        {
            return;
        }

        if (!takenOver)
        {
            entry = _ledger.Apply(entry with { State = TxState.Processing });
            _logger.LogInformation("{Tx} en proceso. Contactando al adquirente.", txId);
            await _replicator.ReplicateAsync(entry, ct);
        }

        var result = await _gateway.ChargeAsync(txId, entry.Amount, currency, ct);

        if (result.Replayed)
        {
            // Transaccion en duda resuelta: el cobro ya estaba aplicado, recuperamos
            // la autorizacion por Idempotency-Key sin volver a cobrar.
            _logger.LogInformation("El adquirente reporta el cobro ya aplicado. Recupero autorización sin re-cobrar.");
        }

        var completed = _ledger.Apply(entry with { State = TxState.Completed, AuthCode = result.AuthCode });
        _logger.LogInformation("{Tx} completada. Autorización {AuthCode}.", txId, result.AuthCode);
        await _replicator.ReplicateAsync(completed, ct);
    }

    private sealed record InFlight(int Epoch, CancellationTokenSource Cts);
}
