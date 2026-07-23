using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;

namespace SolidarityGrid.Psp.Logging;

/// <summary>
/// Prefija cada linea con [psp], en el mismo estilo narrativo que los nodos, para
/// que los logs entrelazados de la demo se lean de un vistazo.
/// </summary>
public sealed class PspConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "psp-prefix";

    public PspConsoleFormatter() : base(FormatterName)
    {
    }

    public override void Write<TState>(
        in LogEntry<TState> logEntry,
        IExternalScopeProvider? scopeProvider,
        TextWriter textWriter)
    {
        var message = logEntry.Formatter?.Invoke(logEntry.State, logEntry.Exception);
        if (string.IsNullOrEmpty(message) && logEntry.Exception is null)
        {
            return;
        }

        textWriter.Write("[psp] ");
        textWriter.Write(message);

        if (logEntry.Exception is not null)
        {
            textWriter.Write(' ');
            textWriter.Write(logEntry.Exception.ToString());
        }

        textWriter.Write(Environment.NewLine);
    }
}
