using Microsoft.Extensions.Options;
using SolidarityGrid.Node.Configuration;
using SolidarityGrid.Node.Domain;
using SolidarityGrid.Node.Infrastructure;
using SolidarityGrid.Node.Mesh;

namespace SolidarityGrid.Node.Processing;

/// <summary>
/// Detecta transacciones huerfanas (Received o Processing cuyo dueño esta caido) y,
/// si HRW elige a este nodo como sucesor, asume el relevo. Es donde se conectan
/// membresia, HRW, ledger y procesador: el nucleo del reto.
/// </summary>
public sealed class TakeoverSupervisor : BackgroundService
{
    private static readonly TimeSpan ScanInterval = TimeSpan.FromSeconds(1);

    private readonly ILedger _ledger;
    private readonly MembershipService _membership;
    private readonly PeerReplicator _replicator;
    private readonly PaymentProcessor _processor;
    private readonly NodeOptions _options;
    private readonly ILogger<TakeoverSupervisor> _logger;

    public TakeoverSupervisor(
        ILedger ledger,
        MembershipService membership,
        PeerReplicator replicator,
        PaymentProcessor processor,
        IOptions<NodeOptions> options,
        ILogger<TakeoverSupervisor> logger)
    {
        _ledger = ledger;
        _membership = membership;
        _replicator = replicator;
        _processor = processor;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(ScanInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await ScanOnceAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // apagado limpio
        }
    }

    internal async Task ScanOnceAsync(CancellationToken ct)
    {
        var self = _options.NodeId;

        foreach (var entry in _ledger.Snapshot())
        {
            if (!IsOrphan(entry))
            {
                continue;
            }

            // nodosVivos = este nodo + peers no caidos. El dueño muerto, por estar
            // Dead, queda excluido; los demas nodos calculan lo mismo y solo uno gana.
            var aliveNodes = new List<string>(_membership.LivePeers()) { self };
            var successor = HrwSuccessor.Elect(entry.TxId, aliveNodes);

            if (successor != self)
            {
                // Decision deliberada de no actuar. En Debug para no ensuciar la
                // narrativa: si el sucesor tambien muere, el proximo escaneo recalcula.
                _logger.LogDebug("{Tx} huérfana, pero el sucesor es {Successor}. Sin acción.",
                    entry.TxId, successor);
                continue;
            }

            await AssumeAsync(entry, ct);
        }
    }

    private bool IsOrphan(LedgerEntry entry)
    {
        // Completed/Failed son terminales. Received cuenta: el dueño pudo morir entre
        // aceptar y empezar a trabajar.
        if (entry.State is not (TxState.Received or TxState.Processing))
        {
            return false;
        }

        // Solo Dead, nunca Suspect. Suspect es incertidumbre; asumir ahi multiplica
        // el trabajo duplicado sin ganar nada.
        return _membership.IsDead(entry.Owner);
    }

    private async Task AssumeAsync(LedgerEntry orphan, CancellationToken ct)
    {
        _logger.LogInformation(
            "{Tx} quedó huérfana (dueño {Owner}, epoch {Epoch}). Soy el sucesor. Asumiendo.",
            orphan.TxId, orphan.Owner, orphan.Epoch);

        var taken = _ledger.Apply(orphan with
        {
            State = TxState.Processing,
            Owner = _options.NodeId,
            Epoch = orphan.Epoch + 1
        });

        _logger.LogInformation("{Tx} epoch {Old} -> {New}. Contactando al adquirente.",
            orphan.TxId, orphan.Epoch, taken.Epoch);

        // Propaga el relevo ANTES de trabajar: fencea al dueño anterior si revive y
        // evita que otro nodo asuma en paralelo.
        await _replicator.ReplicateAsync(taken, ct);

        // La moneda no viaja en el ledger. En un relevo el cobro es un replay que el
        // PSP resuelve por Idempotency-Key ignorando el body, asi que es irrelevante.
        _processor.Resume(orphan.TxId, currency: string.Empty);
    }
}
