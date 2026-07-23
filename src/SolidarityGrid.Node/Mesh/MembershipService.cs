using Microsoft.Extensions.Options;
using SolidarityGrid.Node.Configuration;

namespace SolidarityGrid.Node.Mesh;

public enum MembershipState
{
    Alive,
    Suspect,
    Dead
}

/// <summary>
/// Vista local de la salud del clúster. La expiracion de lease se mide SIEMPRE
/// contra el reloj local en el momento de recibir un latido; nunca contra un
/// timestamp del peer, porque no asumimos relojes sincronizados.
/// </summary>
public sealed class MembershipService
{
    private readonly object _gate = new();
    private readonly Dictionary<string, PeerState> _peers = new(StringComparer.Ordinal);
    private readonly TimeProvider _clock;
    private readonly ILogger<MembershipService> _logger;
    private readonly TimeSpan _suspectAfter;
    private readonly TimeSpan _deadAfter;

    public MembershipService(IOptions<NodeOptions> options, TimeProvider clock, ILogger<MembershipService> logger)
    {
        _clock = clock;
        _logger = logger;

        var config = options.Value;
        _suspectAfter = TimeSpan.FromMilliseconds(config.SuspectMs);
        _deadAfter = TimeSpan.FromMilliseconds(config.DeadMs);

        // Sembramos los peers como Alive: en un arranque normal el primer latido
        // llega antes de DEAD_MS y no hay transicion que registrar. Un peer que no
        // arranca se degradara solo a Suspect y luego a Dead.
        var now = _clock.GetUtcNow();
        foreach (var peer in config.Peers)
        {
            _peers[peer] = new PeerState(peer) { LastSeenAt = now, State = MembershipState.Alive };
        }
    }

    /// <summary>Un latido recibido revive al peer y reinicia su lease contra el reloj local.</summary>
    public void RecordHeartbeat(string nodeId)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            if (!_peers.TryGetValue(nodeId, out var peer))
            {
                _peers[nodeId] = new PeerState(nodeId) { LastSeenAt = now, State = MembershipState.Alive };
                return;
            }

            if (peer.State != MembershipState.Alive)
            {
                _logger.LogInformation("{Peer} volvió a responder. Reincorporado al clúster.", nodeId);
                peer.State = MembershipState.Alive;
            }

            peer.LastSeenAt = now;
        }
    }

    /// <summary>Recorre los peers y registra las degradaciones de estado. Idempotente entre ticks.</summary>
    public void EvaluateTransitions()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            foreach (var peer in _peers.Values)
            {
                var elapsed = now - peer.LastSeenAt;
                var next = Classify(elapsed);

                // Solo nos interesa la degradacion; el ascenso a Alive lo hace un latido.
                if (next <= peer.State)
                {
                    continue;
                }

                if (next == MembershipState.Suspect)
                {
                    _logger.LogInformation(
                        "{Peer} tarda en responder (sin latido hace {Elapsed:F1}s). Marcado como sospechoso.",
                        peer.NodeId, elapsed.TotalSeconds);
                }
                else if (next == MembershipState.Dead)
                {
                    _logger.LogInformation(
                        "{Peer} dejó de responder (sin latido hace {Elapsed:F1}s). Marcado como caído.",
                        peer.NodeId, elapsed.TotalSeconds);
                }

                peer.State = next;
            }
        }
    }

    public IReadOnlyList<PeerStatus> GetStatus()
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow();
            return _peers.Values
                .Select(peer =>
                {
                    var elapsed = now - peer.LastSeenAt;
                    return new PeerStatus(peer.NodeId, Classify(elapsed), Math.Round(elapsed.TotalSeconds, 1));
                })
                .OrderBy(status => status.NodeId, StringComparer.Ordinal)
                .ToList();
        }
    }

    private MembershipState Classify(TimeSpan elapsed) =>
        elapsed >= _deadAfter ? MembershipState.Dead
        : elapsed >= _suspectAfter ? MembershipState.Suspect
        : MembershipState.Alive;

    private sealed class PeerState(string nodeId)
    {
        public string NodeId { get; } = nodeId;
        public DateTimeOffset LastSeenAt { get; set; }
        public MembershipState State { get; set; }
    }
}

public record PeerStatus(string NodeId, MembershipState State, double SecondsSinceLastContact);
