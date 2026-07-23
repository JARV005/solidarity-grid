using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using SolidarityGrid.Node.Configuration;
using SolidarityGrid.Node.Mesh;
using Xunit;

namespace SolidarityGrid.Node.Tests;

public class MembershipServiceTests
{
    private static MembershipService Build(FakeTimeProvider clock, params string[] peers)
    {
        var options = Options.Create(new NodeOptions
        {
            NodeId = "node-self",
            Peers = peers,
            SuspectMs = 3000,
            DeadMs = 5000
        });
        return new MembershipService(options, clock, NullLogger<MembershipService>.Instance);
    }

    private static MembershipState StateOf(MembershipService membership, string nodeId) =>
        membership.GetStatus().Single(peer => peer.NodeId == nodeId).State;

    [Fact]
    public void Seeded_peers_start_alive()
    {
        var membership = Build(new FakeTimeProvider(), "node-1", "node-2");

        Assert.All(membership.GetStatus(), peer => Assert.Equal(MembershipState.Alive, peer.State));
    }

    [Fact]
    public void Peer_degrades_to_suspect_then_dead_without_heartbeats()
    {
        var clock = new FakeTimeProvider();
        var membership = Build(clock, "node-1");

        clock.Advance(TimeSpan.FromSeconds(3));
        membership.EvaluateTransitions();
        Assert.Equal(MembershipState.Suspect, StateOf(membership, "node-1"));

        clock.Advance(TimeSpan.FromSeconds(2)); // 5s sin latido
        membership.EvaluateTransitions();
        Assert.Equal(MembershipState.Dead, StateOf(membership, "node-1"));
    }

    [Fact]
    public void Heartbeat_revives_a_dead_peer()
    {
        var clock = new FakeTimeProvider();
        var membership = Build(clock, "node-1");

        clock.Advance(TimeSpan.FromSeconds(6));
        membership.EvaluateTransitions();
        Assert.Equal(MembershipState.Dead, StateOf(membership, "node-1"));

        membership.RecordHeartbeat("node-1");
        Assert.Equal(MembershipState.Alive, StateOf(membership, "node-1"));
    }

    [Fact]
    public void Heartbeat_resets_the_lease_against_the_local_clock()
    {
        var clock = new FakeTimeProvider();
        var membership = Build(clock, "node-1");

        clock.Advance(TimeSpan.FromSeconds(4)); // ya seria Suspect
        membership.RecordHeartbeat("node-1");   // pero el latido reinicia el lease

        var status = membership.GetStatus().Single();
        Assert.Equal(MembershipState.Alive, status.State);
        Assert.Equal(0.0, status.SecondsSinceLastContact, precision: 1);
    }

    [Fact]
    public void Unknown_peer_is_tracked_on_first_heartbeat()
    {
        var membership = Build(new FakeTimeProvider()); // sin peers sembrados

        membership.RecordHeartbeat("node-9");

        Assert.Equal(MembershipState.Alive, StateOf(membership, "node-9"));
    }
}
