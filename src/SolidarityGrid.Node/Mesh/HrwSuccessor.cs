using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace SolidarityGrid.Node.Mesh;

/// <summary>
/// Rendezvous hashing (HRW): elige, sin coordinacion, un unico sucesor para una
/// transaccion huerfana. Cada nodo vivo corre esto localmente con la misma lista
/// de vivos y llega al mismo ganador, de modo que exactamente uno asume el relevo.
/// </summary>
public static class HrwSuccessor
{
    public static string Elect(string txId, IReadOnlyCollection<string> aliveNodes)
    {
        if (aliveNodes is null || aliveNodes.Count == 0)
        {
            throw new ArgumentException("No hay nodos vivos entre los que elegir sucesor.", nameof(aliveNodes));
        }

        string? winner = null;
        ulong bestScore = 0;

        foreach (var node in aliveNodes)
        {
            ulong score = Score(txId, node);

            // Mayor score gana; ante un empate (astronomicamente improbable con
            // SHA256) desempata el nodeId menor, para no depender del orden de la lista.
            if (winner is null
                || score > bestScore
                || (score == bestScore && string.CompareOrdinal(node, winner) < 0))
            {
                winner = node;
                bestScore = score;
            }
        }

        return winner!;
    }

    // SHA256 y no string.GetHashCode(): este ultimo esta aleatorizado por proceso
    // en .NET Core, asi que cada contenedor calcularia un sucesor distinto y el
    // relevo se romperia en silencio. SHA256 es identico en todos los procesos.
    private static ulong Score(string txId, string nodeId)
    {
        Span<byte> hash = stackalloc byte[SHA256.HashSizeInBytes];
        SHA256.HashData(Encoding.UTF8.GetBytes($"{txId}:{nodeId}"), hash);
        return BinaryPrimitives.ReadUInt64BigEndian(hash);
    }
}
