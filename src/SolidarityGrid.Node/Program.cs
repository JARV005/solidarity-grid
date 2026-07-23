using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using SolidarityGrid.Node.Configuration;
using SolidarityGrid.Node.Logging;

var builder = WebApplication.CreateBuilder(args);

// Kestrel expone dos puertos porque REST (HTTP/1.1) y gRPC (h2c, HTTP/2 sin TLS)
// no se pueden multiplexar en el mismo puerto sin negociacion ALPN, y aqui no
// hay TLS. 8080 servira el norte-sur (POST /pay); 8081 queda listo para gRPC
// este-oeste, sin servicios aun en este bloque.
builder.WebHost.ConfigureKestrel(kestrel =>
{
    kestrel.ListenAnyIP(8080, listen => listen.Protocols = HttpProtocols.Http1);
    kestrel.ListenAnyIP(8081, listen => listen.Protocols = HttpProtocols.Http2);
});

builder.Services.Configure<NodeOptions>(options =>
{
    options.NodeId = builder.Configuration["NODE_ID"] ?? "node-unknown";
    options.Peers = NodeOptions.ParsePeers(builder.Configuration["PEERS"]);
});

builder.Logging.ClearProviders();
builder.Logging.AddConsoleFormatter<NodePrefixConsoleFormatter, ConsoleFormatterOptions>();
builder.Logging.AddConsole(console => console.FormatterName = NodePrefixConsoleFormatter.FormatterName);

var app = builder.Build();

var startedAt = DateTimeOffset.UtcNow;

app.MapGet("/health", (IOptions<NodeOptions> options) => Results.Ok(new
{
    nodeId = options.Value.NodeId,
    uptime = Math.Round((DateTimeOffset.UtcNow - startedAt).TotalSeconds, 1)
}));

var nodeOptions = app.Services.GetRequiredService<IOptions<NodeOptions>>().Value;
var log = app.Services.GetRequiredService<ILogger<Program>>();
log.LogInformation(
    "Nodo en linea. REST en :8080, gRPC en :8081. Peers configurados: {Peers}.",
    nodeOptions.Peers.Count == 0 ? "ninguno" : string.Join(", ", nodeOptions.Peers));

app.Run();
