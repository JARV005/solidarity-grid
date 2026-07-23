using SolidarityGrid.Node.Mesh;
using Xunit;

namespace SolidarityGrid.Node.Tests;

public class HrwSuccessorTests
{
    private static readonly string[] Cluster = { "node-1", "node-2", "node-3" };

    [Fact]
    public void Elect_returns_same_successor_regardless_of_node_order()
    {
        // El determinismo entre nodos es el punto: da igual en que orden cada
        // contenedor tenga su lista de vivos, todos eligen el mismo sucesor.
        var forward = HrwSuccessor.Elect("TX-99", new[] { "node-1", "node-2", "node-3" });
        var reversed = HrwSuccessor.Elect("TX-99", new[] { "node-3", "node-2", "node-1" });
        var shuffled = HrwSuccessor.Elect("TX-99", new[] { "node-2", "node-1", "node-3" });

        Assert.Equal(forward, reversed);
        Assert.Equal(forward, shuffled);
    }

    [Fact]
    public void Elect_result_is_always_within_the_alive_set()
    {
        foreach (var txId in new[] { "TX-1", "TX-2", "TX-42", "TX-99", "pay-abc" })
        {
            Assert.Contains(HrwSuccessor.Elect(txId, Cluster), Cluster);
        }
    }

    [Fact]
    public void Elect_excludes_the_dead_node()
    {
        // Encontramos una tx cuyo dueno HRW es node-1, lo damos por muerto y
        // comprobamos que el relevo recae en otro vivo, nunca en el caido.
        var txOwnedByNode1 = Enumerable.Range(0, 1000)
            .Select(i => $"TX-{i}")
            .First(tx => HrwSuccessor.Elect(tx, Cluster) == "node-1");

        var alive = new[] { "node-2", "node-3" };
        var successor = HrwSuccessor.Elect(txOwnedByNode1, alive);

        Assert.NotEqual("node-1", successor);
        Assert.Contains(successor, alive);
    }

    [Fact]
    public void Elect_throws_when_no_nodes_are_alive()
    {
        Assert.Throws<ArgumentException>(() => HrwSuccessor.Elect("TX-1", Array.Empty<string>()));
    }
}
