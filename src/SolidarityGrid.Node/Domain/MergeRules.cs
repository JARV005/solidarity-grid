namespace SolidarityGrid.Node.Domain;

/// <summary>
/// El corazon de la correccion del sistema: la funcion de join del CRDT.
/// Funcion pura, sin efectos, sin reloj, sin infraestructura.
///
/// El merge es el maximo bajo un orden total sobre LedgerEntry. Modelarlo como
/// un orden total no es un detalle de implementacion: es lo que garantiza que
/// el join sea conmutativo, asociativo e idempotente por construccion. El
/// maximo sobre un orden total siempre lo es, sin importar en que orden lleguen
/// los latidos ni como se agrupen.
/// </summary>
public static class MergeRules
{
    public static LedgerEntry Merge(LedgerEntry local, LedgerEntry incoming)
        => Compare(incoming, local) > 0 ? incoming : local;

    /// <summary>
    /// Orden total sobre entradas de una misma transaccion. Positivo si <paramref name="a"/>
    /// debe ganar, negativo si gana <paramref name="b"/>, cero solo si son iguales campo a campo.
    /// </summary>
    private static int Compare(LedgerEntry a, LedgerEntry b)
    {
        // 1. Rango mayor gana. Completed/Failed comparten rango, asi que un
        //    terminal siempre desplaza a Received o Processing, sin mirar epoch.
        int byRank = a.State.Rank().CompareTo(b.State.Rank());
        if (byRank != 0) return byRank;

        int rank = a.State.Rank();

        if (rank == TxState.Processing.Rank())
        {
            // 2. Empate en Processing: mayor Epoch gana; si empatan, gana el
            //    Owner lexicograficamente menor (por eso b y a van invertidos).
            int byEpoch = a.Epoch.CompareTo(b.Epoch);
            if (byEpoch != 0) return byEpoch;

            int byOwner = string.CompareOrdinal(b.Owner, a.Owner);
            if (byOwner != 0) return byOwner;
        }
        else if (rank == TxState.Completed.Rank())
        {
            // 3. Empate en terminal: Completed le gana a Failed.
            int byTerminal = TerminalPriority(a.State).CompareTo(TerminalPriority(b.State));
            if (byTerminal != 0) return byTerminal;
        }

        // 4. Empate exacto en las reglas del protocolo. Lo que sigue completa el
        //    orden total de forma determinista para que el merge siga siendo
        //    conmutativo y asociativo aun en casos que las reglas de arriba no
        //    ordenan (dos Received, dos Completed distintos). Solo se dispara
        //    cuando aquellas empatan; si tambien empata aqui, las entradas son
        //    identicas y Merge conserva la local.
        int byEpochTie = a.Epoch.CompareTo(b.Epoch);
        if (byEpochTie != 0) return byEpochTie;

        int byOwnerTie = string.CompareOrdinal(b.Owner, a.Owner);
        if (byOwnerTie != 0) return byOwnerTie;

        int byAmount = a.Amount.CompareTo(b.Amount);
        if (byAmount != 0) return byAmount;

        return string.CompareOrdinal(a.AuthCode, b.AuthCode);
    }

    private static int TerminalPriority(TxState state) => state switch
    {
        TxState.Completed => 1,
        TxState.Failed => 0,
        _ => throw new ArgumentOutOfRangeException(nameof(state), state, "No es un estado terminal.")
    };
}
