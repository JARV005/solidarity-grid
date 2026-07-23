using SolidarityGrid.Node.Domain;
using SolidarityGrid.Node.Infrastructure;
using Xunit;

namespace SolidarityGrid.Node.Tests;

public class InMemoryLedgerTests
{
    [Fact]
    public void Apply_converges_through_merge_regardless_of_arrival_order()
    {
        var received = new LedgerEntry("TX-1", TxState.Received, "node-a", 1, 100m, null);
        var processing = new LedgerEntry("TX-1", TxState.Processing, "node-a", 1, 100m, null);
        var completed = new LedgerEntry("TX-1", TxState.Completed, "node-a", 1, 100m, "AUTH-1");

        var ledger = new InMemoryLedger();
        // Latidos fuera de orden: el terminal llega antes que el intermedio.
        ledger.Apply(completed);
        ledger.Apply(received);
        ledger.Apply(processing);

        Assert.True(ledger.TryGet("TX-1", out var stored));
        Assert.Equal(completed, stored);
    }

    [Fact]
    public void Snapshot_returns_all_transactions()
    {
        var ledger = new InMemoryLedger();
        ledger.Apply(new LedgerEntry("TX-1", TxState.Processing, "node-a", 1, 10m, null));
        ledger.Apply(new LedgerEntry("TX-2", TxState.Received, "node-b", 1, 20m, null));

        var ids = ledger.Snapshot().Select(e => e.TxId).OrderBy(x => x);

        Assert.Equal(new[] { "TX-1", "TX-2" }, ids);
    }
}
