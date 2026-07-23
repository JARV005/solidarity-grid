namespace SolidarityGrid.Node.Mesh;

/// <summary>
/// Reevalua la membresia periodicamente para que una caida se reporte cerca del
/// umbral DEAD_MS y no en el siguiente latido. Es la unica pieza que "empuja" las
/// transiciones sin necesidad de una peticion entrante.
/// </summary>
public sealed class FailureDetector : BackgroundService
{
    // Evaluamos mas seguido que el latido: si esperaramos HEARTBEAT_MS, la caida
    // podria reportarse hasta un segundo tarde respecto al umbral.
    private static readonly TimeSpan EvaluationInterval = TimeSpan.FromMilliseconds(500);

    private readonly MembershipService _membership;

    public FailureDetector(MembershipService membership) => _membership = membership;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(EvaluationInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                _membership.EvaluateTransitions();
            }
        }
        catch (OperationCanceledException)
        {
            // apagado limpio
        }
    }
}
