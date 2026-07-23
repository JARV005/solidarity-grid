namespace SolidarityGrid.Node.Mesh;

/// <summary>
/// Transporte este-oeste. La unica abstraccion con dos implementaciones
/// previstas (HTTP y gRPC): por eso es una interfaz y no una clase.
/// El contrato es a prueba de fallos: nunca lanza, devuelve false si el peer
/// no respondio. Asi el detector de fallos vive del silencio, no de excepciones.
/// </summary>
public interface IPeerTransport
{
    Task<bool> SendDigestAsync(string peer, NodeDigest digest, CancellationToken ct);
}
