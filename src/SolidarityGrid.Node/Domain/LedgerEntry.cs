namespace SolidarityGrid.Node.Domain;

/// <summary>
/// Version convergente de una transaccion. Inmutable: cada cambio de estado
/// produce un nuevo LedgerEntry. La igualdad estructural del record es lo que
/// permite comparar merges por valor en los tests de convergencia.
/// </summary>
public record LedgerEntry(
    string TxId,        // = Idempotency-Key del cliente
    TxState State,
    string Owner,       // nodeId
    int Epoch,          // fencing token, monotonico
    decimal Amount,
    string? AuthCode);  // solo presente en Completed
