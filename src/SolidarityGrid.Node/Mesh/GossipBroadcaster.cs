using Microsoft.Extensions.Options;
using SolidarityGrid.Node.Configuration;
using SolidarityGrid.Node.Infrastructure;

namespace SolidarityGrid.Node.Mesh;

/// <summary>
/// Empuja el digest de este nodo a todos sus peers cada HEARTBEAT_MS. Con N=3 es
/// full-mesh push; el transporte por peer trae su propio deadline, asi que un
/// peer caido nunca bloquea la ronda ni tumba el loop.
/// </summary>
public sealed class GossipBroadcaster : BackgroundService
{
    private readonly IPeerTransport _transport;
    private readonly ILedger _ledger;
    private readonly NodeOptions _options;

    public GossipBroadcaster(IPeerTransport transport, ILedger ledger, IOptions<NodeOptions> options)
    {
        _transport = transport;
        _ledger = ledger;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.HeartbeatMs));

        while (await WaitForNextTickAsync(timer, stoppingToken))
        {
            var digest = new NodeDigest(_options.NodeId, _ledger.Snapshot().ToArray());

            // En paralelo y sin await bloqueante peer a peer. El transporte nunca
            // lanza y trae su timeout de 500ms, asi que Task.WhenAll no puede fallar.
            var sends = _options.Peers.Select(peer => _transport.SendDigestAsync(peer, digest, stoppingToken));
            await Task.WhenAll(sends);
        }
    }

    private static async Task<bool> WaitForNextTickAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try
        {
            return await timer.WaitForNextTickAsync(ct);
        }
        catch (OperationCanceledException)
        {
            return false; // apagado limpio
        }
    }
}
