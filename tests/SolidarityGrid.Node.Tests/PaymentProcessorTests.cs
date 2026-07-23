using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SolidarityGrid.Node.Configuration;
using SolidarityGrid.Node.Domain;
using SolidarityGrid.Node.Infrastructure;
using SolidarityGrid.Node.Mesh;
using SolidarityGrid.Node.Processing;
using Xunit;

namespace SolidarityGrid.Node.Tests;

public class PaymentProcessorTests
{
    [Fact]
    public async Task Processes_from_received_to_completed_and_propagates_each_step()
    {
        var ledger = new InMemoryLedger();
        ledger.Apply(new LedgerEntry("TX-1", TxState.Received, "node-1", 1, 25000m, null));

        var gateway = new FakeGateway("AUTH-4F2A");
        var transport = new RecordingTransport();
        var processor = BuildProcessor(ledger, transport, gateway, peers: "node-2");

        await processor.RunAsync("TX-1", "COP", takenOver: false, CancellationToken.None);

        Assert.True(ledger.TryGet("TX-1", out var entry));
        Assert.Equal(TxState.Completed, entry.State);
        Assert.Equal("AUTH-4F2A", entry.AuthCode);
        Assert.Equal(25000m, entry.Amount);

        // Exactly-once: un unico cobro al PSP.
        Assert.Equal(1, gateway.Calls);

        // Propago Processing y Completed sin esperar al siguiente latido.
        Assert.Contains(transport.Sent, d => d.Entries.Any(e => e.State == TxState.Processing));
        Assert.Contains(transport.Sent, d => d.Entries.Any(e => e.State == TxState.Completed && e.AuthCode == "AUTH-4F2A"));
    }

    [Fact]
    public async Task Aborts_without_charging_when_cancelled_before_the_psp_call()
    {
        var ledger = new InMemoryLedger();
        ledger.Apply(new LedgerEntry("TX-1", TxState.Received, "node-1", 1, 25000m, null));

        var gateway = new FakeGateway("AUTH-4F2A");
        var processor = BuildProcessor(ledger, new RecordingTransport(), gateway, peers: "node-2");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processor.RunAsync("TX-1", "COP", takenOver: false, cts.Token));

        Assert.Equal(0, gateway.Calls);
    }

    [Fact]
    public async Task Fencing_cancels_in_flight_work_superseded_by_a_higher_epoch()
    {
        var ledger = new InMemoryLedger();
        ledger.Apply(new LedgerEntry("TX-1", TxState.Received, "node-1", 1, 25000m, null));

        var gateway = new BlockingGateway();
        var processor = BuildProcessor(ledger, new RecordingTransport(), gateway, peers: "node-2");

        // Dueño original (epoch 1) empieza a cobrar y se queda esperando al PSP.
        processor.Begin("TX-1", "COP");
        await gateway.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(processor.IsProcessing("TX-1"));

        // Llega por gossip un relevo con epoch 2: el fencing debe cancelar el trabajo.
        processor.ApplyFencing(new LedgerEntry("TX-1", TxState.Processing, "node-2", 2, 25000m, null));

        await WaitUntilAsync(() => !processor.IsProcessing("TX-1"), TimeSpan.FromSeconds(5));
        Assert.False(processor.IsProcessing("TX-1"));

        // No completo: abandono el trabajo al descubrir que ya no era el dueño.
        Assert.True(ledger.TryGet("TX-1", out var entry));
        Assert.NotEqual(TxState.Completed, entry.State);
        Assert.Null(entry.AuthCode);
    }

    private static PaymentProcessor BuildProcessor(
        InMemoryLedger ledger, IPeerTransport transport, IPaymentGateway gateway, params string[] peers)
    {
        var options = Options.Create(new NodeOptions { NodeId = "node-1", Peers = peers });
        var replicator = new PeerReplicator(transport, options);
        return new PaymentProcessor(ledger, gateway, replicator, NullLogger<PaymentProcessor>.Instance);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }
    }

    private sealed class FakeGateway : IPaymentGateway
    {
        private readonly string _authCode;
        public int Calls;

        public FakeGateway(string authCode) => _authCode = authCode;

        public Task<PaymentResult> ChargeAsync(string txId, decimal amount, string currency, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Interlocked.Increment(ref Calls);
            return Task.FromResult(new PaymentResult(_authCode, Replayed: false));
        }
    }

    private sealed class BlockingGateway : IPaymentGateway
    {
        public readonly TaskCompletionSource Started = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<PaymentResult> ChargeAsync(string txId, decimal amount, string currency, CancellationToken ct)
        {
            Started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct); // se libera solo al cancelar (fencing)
            return new PaymentResult("AUTH-NEVER", Replayed: false);
        }
    }

    private sealed class RecordingTransport : IPeerTransport
    {
        private readonly List<NodeDigest> _sent = new();
        public IReadOnlyList<NodeDigest> Sent
        {
            get { lock (_sent) return _sent.ToArray(); }
        }

        public Task<bool> SendDigestAsync(string peer, NodeDigest digest, CancellationToken ct)
        {
            lock (_sent) _sent.Add(digest);
            return Task.FromResult(true);
        }
    }
}
