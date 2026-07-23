namespace SolidarityGrid.Node.Configuration;

/// <summary>
/// Identidad y vecindario de este nodo. Se rellena desde variables de entorno
/// (NODE_ID, PEERS) via el patron Options.
/// </summary>
public sealed class NodeOptions
{
    public string NodeId { get; set; } = "node-unknown";

    /// <summary>Hostnames de los peers (uno = un nodeId). El transporte les pone el puerto.</summary>
    public IReadOnlyList<string> Peers { get; set; } = Array.Empty<string>();

    public int HeartbeatMs { get; set; } = 1000;

    public int SuspectMs { get; set; } = 3000;

    public int DeadMs { get; set; } = 5000;

    /// <summary>grpc | http. En este bloque solo existe http; grpc llegara despues.</summary>
    public string Transport { get; set; } = "http";

    public string PspUrl { get; set; } = "http://psp-mock:8080";

    public static IReadOnlyList<string> ParsePeers(string? raw) =>
        string.IsNullOrWhiteSpace(raw)
            ? Array.Empty<string>()
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
}
