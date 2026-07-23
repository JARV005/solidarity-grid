namespace SolidarityGrid.Node.Domain;

public enum TxState
{
    Received,
    Processing,
    Completed,
    Failed
}

public static class TxStateExtensions
{
    /// <summary>
    /// Rango de convergencia. El merge hace ganar al rango mayor; Completed y
    /// Failed comparten rango porque ambos son terminales y ninguno debe
    /// desplazar al otro por rango: ese empate lo resuelve una regla propia.
    /// </summary>
    public static int Rank(this TxState state) => state switch
    {
        TxState.Received => 0,
        TxState.Processing => 1,
        TxState.Completed => 2,
        TxState.Failed => 2,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, null)
    };
}
