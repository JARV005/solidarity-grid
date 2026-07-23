using System.Collections.Concurrent;

namespace SolidarityGrid.Psp;

public record ChargeRequest(decimal Amount, string Currency);

public record ChargeView(int Attempts, int Applied, string? AuthCode);

/// <summary>
/// El nucleo de la garantia exactly-once del sistema. La deduplicacion NO es un
/// adorno: es un ConcurrentDictionary con Lazy&lt;Task&gt; por Idempotency-Key. Dos
/// llamadas concurrentes con la misma clave comparten UNA sola ejecucion; la
/// segunda espera el resultado de la primera en vez de cobrar de nuevo.
/// </summary>
public sealed class ChargeStore
{
    private readonly ConcurrentDictionary<string, Charge> _charges = new();
    private readonly ILogger<ChargeStore> _logger;

    public ChargeStore(ILogger<ChargeStore> logger) => _logger = logger;

    public async Task<(string AuthCode, bool Replayed)> ChargeAsync(string key, ChargeRequest request)
    {
        var charge = _charges.GetOrAdd(key, k => new Charge(k, _logger));

        // La primera llamada dispara el cobro; las siguientes esperan ese mismo Task.
        var replayed = charge.RecordAttempt() > 1;
        if (replayed)
        {
            _logger.LogInformation(
                "Cobro {Key} solicitado de nuevo. Devolviendo resultado existente sin re-cobrar.", key);
        }

        var authCode = await charge.Execution.Value;
        return (authCode, replayed);
    }

    public bool TryGet(string key, out ChargeView view)
    {
        if (_charges.TryGetValue(key, out var charge))
        {
            view = charge.ToView();
            return true;
        }

        view = default!;
        return false;
    }

    private sealed class Charge
    {
        private readonly string _key;
        private readonly ILogger _logger;
        private int _attempts;
        private int _applied;
        private volatile string? _authCode;

        public Charge(string key, ILogger logger)
        {
            _key = key;
            _logger = logger;
            Execution = new Lazy<Task<string>>(ExecuteAsync);
        }

        public Lazy<Task<string>> Execution { get; }

        public int RecordAttempt() => Interlocked.Increment(ref _attempts);

        public ChargeView ToView() => new(Volatile.Read(ref _attempts), Volatile.Read(ref _applied), _authCode);

        private async Task<string> ExecuteAsync()
        {
            // El adquirente es lento por diseño: 5-10s. applied cuenta cobros reales,
            // y solo se incrementa aqui, dentro de la unica ejecucion.
            var delay = TimeSpan.FromMilliseconds(Random.Shared.Next(5000, 10001));
            _logger.LogInformation("Cobro {Key} recibido. Ejecutando ({Delay:F1}s).", _key, delay.TotalSeconds);

            await Task.Delay(delay);

            var authCode = $"AUTH-{Random.Shared.Next(0, 0x10000):X4}";
            Interlocked.Increment(ref _applied);
            _authCode = authCode;
            return authCode;
        }
    }
}
