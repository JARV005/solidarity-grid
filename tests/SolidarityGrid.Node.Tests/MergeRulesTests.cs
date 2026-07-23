using SolidarityGrid.Node.Domain;
using Xunit;

namespace SolidarityGrid.Node.Tests;

public class MergeRulesTests
{
    // Set curado de versiones de UNA transaccion (mismo TxId, mismo importe: es
    // lo realista). Cubre todos los cruces de rango, epoch y owner que el merge
    // debe ordenar: dos Received distintos, Processing empatando y no empatando
    // en epoch/owner, y los dos terminales con owners y epochs variados.
    private static readonly LedgerEntry[] Curated =
    {
        new("TX-1", TxState.Received,   "node-a", 1, 100m, null),
        new("TX-1", TxState.Received,   "node-b", 1, 100m, null),
        new("TX-1", TxState.Processing, "node-a", 1, 100m, null),
        new("TX-1", TxState.Processing, "node-b", 1, 100m, null),
        new("TX-1", TxState.Processing, "node-a", 2, 100m, null),
        new("TX-1", TxState.Processing, "node-c", 2, 100m, null),
        new("TX-1", TxState.Completed,  "node-a", 2, 100m, "AUTH-1"),
        new("TX-1", TxState.Completed,  "node-b", 3, 100m, "AUTH-2"),
        new("TX-1", TxState.Failed,     "node-a", 2, 100m, null),
        new("TX-1", TxState.Failed,     "node-b", 5, 100m, null),
    };

    public static IEnumerable<object[]> Pairs =>
        from a in Curated from b in Curated select new object[] { a, b };

    public static IEnumerable<object[]> Triples =>
        from a in Curated from b in Curated from c in Curated select new object[] { a, b, c };

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Merge_is_commutative(LedgerEntry a, LedgerEntry b)
    {
        Assert.Equal(MergeRules.Merge(a, b), MergeRules.Merge(b, a));
    }

    [Theory]
    [MemberData(nameof(Triples))]
    public void Merge_is_associative(LedgerEntry a, LedgerEntry b, LedgerEntry c)
    {
        var left = MergeRules.Merge(MergeRules.Merge(a, b), c);
        var right = MergeRules.Merge(a, MergeRules.Merge(b, c));
        Assert.Equal(left, right);
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void Merge_is_idempotent(LedgerEntry a, LedgerEntry b)
    {
        // Idempotencia en el sentido fuerte del CRDT: reaplicar un valor ya
        // fundido no cambia el resultado. Merge(x, x) == x es el caso base.
        Assert.Equal(a, MergeRules.Merge(a, a));

        var merged = MergeRules.Merge(a, b);
        Assert.Equal(merged, MergeRules.Merge(merged, a));
        Assert.Equal(merged, MergeRules.Merge(merged, b));
    }

    [Fact]
    public void Completed_absorbs_Processing_even_with_higher_epoch()
    {
        var completed = new LedgerEntry("TX-1", TxState.Completed, "node-a", 2, 100m, "AUTH-1");
        var laterProcessing = new LedgerEntry("TX-1", TxState.Processing, "node-b", 9, 100m, null);

        Assert.Equal(completed, MergeRules.Merge(completed, laterProcessing));
        Assert.Equal(completed, MergeRules.Merge(laterProcessing, completed));
    }

    [Fact]
    public void Processing_with_higher_epoch_displaces_lower_epoch()
    {
        var oldOwner = new LedgerEntry("TX-1", TxState.Processing, "node-a", 1, 100m, null);
        var newOwner = new LedgerEntry("TX-1", TxState.Processing, "node-b", 2, 100m, null);

        Assert.Equal(newOwner, MergeRules.Merge(oldOwner, newOwner));
        Assert.Equal(newOwner, MergeRules.Merge(newOwner, oldOwner));
    }

    [Fact]
    public void Processing_tie_on_epoch_prefers_lexicographically_lower_owner()
    {
        var ownerB = new LedgerEntry("TX-1", TxState.Processing, "node-b", 2, 100m, null);
        var ownerA = new LedgerEntry("TX-1", TxState.Processing, "node-a", 2, 100m, null);

        Assert.Equal(ownerA, MergeRules.Merge(ownerB, ownerA));
        Assert.Equal(ownerA, MergeRules.Merge(ownerA, ownerB));
    }

    [Fact]
    public void Completed_beats_Failed_on_terminal_tie()
    {
        var completed = new LedgerEntry("TX-1", TxState.Completed, "node-a", 2, 100m, "AUTH-1");
        var failed = new LedgerEntry("TX-1", TxState.Failed, "node-a", 2, 100m, null);

        Assert.Equal(completed, MergeRules.Merge(completed, failed));
        Assert.Equal(completed, MergeRules.Merge(failed, completed));
    }
}
