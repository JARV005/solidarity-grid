using Microsoft.Extensions.Options;
using SolidarityGrid.Node.Configuration;
using SolidarityGrid.Node.Domain;

namespace SolidarityGrid.Node.Mesh;

/// <summary>
/// Empuja una entrada concreta a todos los peers y reporta cuales confirmaron.
/// Centraliza el "digest de una entrada -> broadcast" que usan la recepcion del
/// pago, el procesador y el relevo, para no repetir la construccion del digest.
/// </summary>
public sealed class PeerReplicator
{
    private readonly IPeerTransport _transport;
    private readonly NodeOptions _options;

    public PeerReplicator(IPeerTransport transport, IOptions<NodeOptions> options)
    {
        _transport = transport;
        _options = options.Value;
    }

    public async Task<IReadOnlyList<string>> ReplicateAsync(LedgerEntry entry, CancellationToken ct)
    {
        if (_options.Peers.Count == 0)
        {
            return Array.Empty<string>();
        }

        var digest = new NodeDigest(_options.NodeId, new[] { entry });
        var outcomes = await Task.WhenAll(_options.Peers.Select(async peer =>
            (peer, ok: await _transport.SendDigestAsync(peer, digest, ct))));

        return outcomes.Where(outcome => outcome.ok).Select(outcome => outcome.peer).ToList();
    }
}
