namespace SolidarityGrid.Node.Configuration;

/// <summary>
/// Identidad y vecindario de este nodo. Se rellena desde variables de entorno
/// (NODE_ID, PEERS) via el patron Options.
/// </summary>
public sealed class NodeOptions
{
    public string NodeId { get; set; } = "node-unknown";

    /// <summary>Direcciones "host:puerto" de los peers, tal como llegan en PEERS.</summary>
    public IReadOnlyList<string> Peers { get; set; } = Array.Empty<string>();

    public static IReadOnlyList<string> ParsePeers(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
