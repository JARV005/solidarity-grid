using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using SolidarityGrid.Node.Domain;

namespace SolidarityGrid.Node.Infrastructure;

/// <summary>
/// Ledger en memoria. Cada escritura pasa por MergeRules.Merge dentro del
/// AddOrUpdate, asi que las llegadas concurrentes de latidos convergen al mismo
/// estado independientemente del orden.
/// </summary>
public sealed class InMemoryLedger : ILedger
{
    private readonly ConcurrentDictionary<string, LedgerEntry> _entries = new();

    public LedgerEntry Apply(LedgerEntry entry) =>
        _entries.AddOrUpdate(
            entry.TxId,
            entry,
            (_, existing) => MergeRules.Merge(existing, entry));

    public bool TryGet(string txId, [MaybeNullWhen(false)] out LedgerEntry entry) =>
        _entries.TryGetValue(txId, out entry);

    public IReadOnlyCollection<LedgerEntry> Snapshot() => _entries.Values.ToArray();
}
