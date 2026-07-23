using System.Diagnostics.CodeAnalysis;
using SolidarityGrid.Node.Domain;

namespace SolidarityGrid.Node.Infrastructure;

/// <summary>
/// Estado local del nodo. No es una base de datos ni persiste: la durabilidad
/// viene de la replicacion 3x, no del disco. Toda escritura converge via
/// MergeRules, de modo que aplicar la misma entrada dos veces es inocuo.
/// </summary>
public interface ILedger
{
    /// <summary>Funde <paramref name="entry"/> con lo que haya y devuelve la entrada resultante.</summary>
    LedgerEntry Apply(LedgerEntry entry);

    bool TryGet(string txId, [MaybeNullWhen(false)] out LedgerEntry entry);

    /// <summary>Copia estable de todas las entradas, para construir el digest del latido.</summary>
    IReadOnlyCollection<LedgerEntry> Snapshot();
}
