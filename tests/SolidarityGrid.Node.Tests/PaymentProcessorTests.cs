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

        await processor.ProcessAsync("TX-1", "COP", CancellationToken.None);

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
    public async Task Aborts_without_charging_when_the_work_is_cancelled_before_the_psp_call()
    {
        var ledger = new InMemoryLedger();
        ledger.Apply(new LedgerEntry("TX-1", TxState.Received, "node-1", 1, 25000m, null));

        var gateway = new FakeGateway("AUTH-4F2A");
        var processor = BuildProcessor(ledger, new RecordingTransport(), gateway, peers: "node-2");

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => processor.ProcessAsync("TX-1", "COP", cts.Token));

        Assert.Equal(0, gateway.Calls);
    }

    private static PaymentProcessor BuildProcessor(
        InMemoryLedger ledger, IPeerTransport transport, IPaymentGateway gateway, params string[] peers)
    {
        var options = Options.Create(new NodeOptions { NodeId = "node-1", Peers = peers });
        return new PaymentProcessor(ledger, transport, gateway, options, NullLogger<PaymentProcessor>.Instance);
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
