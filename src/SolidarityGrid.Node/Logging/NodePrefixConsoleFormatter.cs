using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using SolidarityGrid.Node.Configuration;

namespace SolidarityGrid.Node.Logging;

/// <summary>
/// Prefija cada linea con [nodeId] para que los logs de los tres nodos sean
/// legibles entrelazados en la salida de docker compose. El estilo narrativo
/// del proyecto depende de este prefijo, por eso vive en un formatter propio
/// y no en cada llamada a log.
/// </summary>
public sealed class NodePrefixConsoleFormatter : ConsoleFormatter
{
    public const string FormatterName = "node-prefix";

    private readonly string _nodeId;

    public NodePrefixConsoleFormatter(IOptions<NodeOptions> options)
        : base(FormatterName)
    {
        _nodeId = options.Value.NodeId;
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

        textWriter.Write('[');
        textWriter.Write(_nodeId);
        textWriter.Write("] ");
        textWriter.Write(message);

        if (logEntry.Exception is not null)
        {
            textWriter.Write(' ');
            textWriter.Write(logEntry.Exception.ToString());
        }

        textWriter.Write(Environment.NewLine);
    }
}
