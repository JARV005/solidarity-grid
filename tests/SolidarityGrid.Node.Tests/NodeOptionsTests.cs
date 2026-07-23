using SolidarityGrid.Node.Configuration;
using Xunit;

namespace SolidarityGrid.Node.Tests;

// Andamiaje: un unico test sobre la unica logica pura del bloque 1, para dejar
// el pipeline de tests verde antes de construir el mesh encima.
public class NodeOptionsTests
{
    [Fact]
    public void ParsePeers_splits_and_trims()
    {
        var peers = NodeOptions.ParsePeers("node-2:8081, node-3:8081");

        Assert.Equal(new[] { "node-2:8081", "node-3:8081" }, peers);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void ParsePeers_returns_empty_when_unset(string? raw)
    {
        Assert.Empty(NodeOptions.ParsePeers(raw));
    }
}
