using SolidarityGrid.Node.Domain;

namespace SolidarityGrid.Node.Mesh;

/// <summary>
/// Lo que un nodo empuja en cada latido: su identidad mas las entradas que
/// posee. La identidad es la senal de vida; las entradas alimentan el merge.
/// </summary>
public record NodeDigest(string NodeId, IReadOnlyList<LedgerEntry> Entries);
