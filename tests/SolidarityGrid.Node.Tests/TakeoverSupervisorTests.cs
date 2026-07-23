using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SolidarityGrid.Node.Configuration;
using SolidarityGrid.Node.Domain;
using SolidarityGrid.Node.Infrastructure;
using SolidarityGrid.Node.Mesh;
using SolidarityGrid.Node.Processing;
using Xunit;

namespace SolidarityGrid.Node.Tests;

public class TakeoverSupervisorTests
{
    private const string Self = "node-2";
    private static readonly string[] Peers = { "node-1", "node-3" };
    private static readonly string[] AliveSet = { "node-2", "node-3" };

    [Fact]
    public async Task Assumes_when_it_is_the_hrw_successor()
    {
        var tx = TxElecting("node-2");
        var (ledger, supervisor) = BuildScene(tx, TxState.Processing, ownerDead: true);

        await supervisor.ScanOnceAsync(CancellationToken.None);

        Assert.True(ledger.TryGet(tx, out var entry));
        Assert.Equal("node-2", entry.Owner);
        Assert.Equal(2, entry.Epoch); // epoch 1 -> 2 (fencing token)
    }

    [Fact]
    public async Task Does_not_assume_when_another_node_is_the_successor()
    {
        var tx = TxElecting("node-3");
        var (ledger, supervisor) = BuildScene(tx, TxState.Processing, ownerDead: true);

        await supervisor.ScanOnceAsync(CancellationToken.None);

        Assert.True(ledger.TryGet(tx, out var entry));
        Assert.Equal("node-1", entry.Owner); // intacto: se encarga node-3
        Assert.Equal(1, entry.Epoch);
    }

    [Fact]
    public async Task Does_not_assume_when_the_owner_is_only_suspect()
    {
        var tx = TxElecting("node-2");
        var (ledger, supervisor) = BuildScene(tx, TxState.Processing, ownerDead: false);

        await supervisor.ScanOnceAsync(CancellationToken.None);

        Assert.True(ledger.TryGet(tx, out var entry));
        Assert.Equal("node-1", entry.Owner); // Suspect no dispara relevo
        Assert.Equal(1, entry.Epoch);
    }

    [Fact]
    public async Task Does_not_assume_a_completed_entry()
    {
        var tx = TxElecting("node-2");
        var (ledger, supervisor) = BuildScene(tx, TxState.Completed, ownerDead: true);

        await supervisor.ScanOnceAsync(CancellationToken.None);

        Assert.True(ledger.TryGet(tx, out var entry));
        Assert.Equal("node-1", entry.Owner);
        Assert.Equal(TxState.Completed, entry.State); // terminal e inmutable
        Assert.Equal(1, entry.Epoch);
    }

    private static (InMemoryLedger Ledger, TakeoverSupervisor Supervisor) BuildScene(
        string txId, TxState ownerState, bool ownerDead)
    {
        var ledger = new InMemoryLedger();
        ledger.Apply(new LedgerEntry(
            txId, ownerState, "node-1", 1, 25000m,
            ownerState == TxState.Completed ? "AUTH-OLD" : null));

        var clock = new FakeTimeProvider();
        var options = Options.Create(new NodeOptions
        {
            NodeId = Self,
            Peers = Peers,
            SuspectMs = 3000,
            DeadMs = 5000
        });
        var membership = new MembershipService(options, clock, NullLogger<MembershipService>.Instance);

        // Avanzamos el reloj hasta dejar a node-1 Dead (6s) o solo Suspect (3s),
        // y refrescamos node-3 para que siga vivo como candidato a sucesor.
        clock.Advance(TimeSpan.FromSeconds(ownerDead ? 6 : 3));
        membership.RecordHeartbeat("node-3");

        var replicator = new PeerReplicator(new NoopTransport(), options);
        var processor = new PaymentProcessor(
            ledger, new NoopGateway(), replicator, NullLogger<PaymentProcessor>.Instance);
        var supervisor = new TakeoverSupervisor(
            ledger, membership, replicator, processor, options, NullLogger<TakeoverSupervisor>.Instance);

        return (ledger, supervisor);
    }

    private static string TxElecting(string winner)
    {
        for (var i = 0; i < 100_000; i++)
        {
            var tx = $"TX-{i}";
            if (HrwSuccessor.Elect(tx, AliveSet) == winner)
            {
                return tx;
            }
        }

        throw new InvalidOperationException($"No se encontró una tx cuyo sucesor HRW sea {winner}.");
    }

    private sealed class NoopGateway : IPaymentGateway
    {
        public Task<PaymentResult> ChargeAsync(string txId, decimal amount, string currency, CancellationToken ct) =>
            Task.FromResult(new PaymentResult("AUTH-XXXX", Replayed: true));
    }

    private sealed class NoopTransport : IPeerTransport
    {
        public Task<bool> SendDigestAsync(string peer, NodeDigest digest, CancellationToken ct) =>
            Task.FromResult(true);
    }
}
