using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using SolidarityGrid.Node.Configuration;
using SolidarityGrid.Node.Infrastructure;
using SolidarityGrid.Node.Logging;
using SolidarityGrid.Node.Mesh;

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
    var config = builder.Configuration;
    options.NodeId = config["NODE_ID"] ?? "node-unknown";
    options.Peers = NodeOptions.ParsePeers(config["PEERS"]);
    options.HeartbeatMs = config.GetValue("HEARTBEAT_MS", 1000);
    options.SuspectMs = config.GetValue("SUSPECT_MS", 3000);
    options.DeadMs = config.GetValue("DEAD_MS", 5000);
    options.Transport = config["TRANSPORT"] ?? "http";
});

builder.Logging.ClearProviders();
builder.Logging.AddConsoleFormatter<NodePrefixConsoleFormatter, ConsoleFormatterOptions>();
builder.Logging.AddConsole(console => console.FormatterName = NodePrefixConsoleFormatter.FormatterName);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ILedger, InMemoryLedger>();
builder.Services.AddSingleton<MembershipService>();

// El gossip de este bloque viaja por HTTP. La seleccion http|grpc por TRANSPORT
// llegara cuando exista la segunda implementacion de IPeerTransport.
builder.Services.AddHttpClient("gossip");
builder.Services.AddSingleton<IPeerTransport, HttpPeerTransport>();

builder.Services.AddHostedService<GossipBroadcaster>();
builder.Services.AddHostedService<FailureDetector>();

var app = builder.Build();

var startedAt = DateTimeOffset.UtcNow;

app.MapGet("/health", (IOptions<NodeOptions> options) => Results.Ok(new
{
    nodeId = options.Value.NodeId,
    uptime = Math.Round((DateTimeOffset.UtcNow - startedAt).TotalSeconds, 1)
}));

// Recepcion de gossip: el latido revive al peer y sus entradas convergen via merge.
app.MapPost("/internal/gossip", (NodeDigest digest, MembershipService membership, ILedger ledger) =>
{
    membership.RecordHeartbeat(digest.NodeId);
    foreach (var entry in digest.Entries)
    {
        ledger.Apply(entry);
    }

    return Results.NoContent();
});

// Ventana de observabilidad: la vista local del cluster.
app.MapGet("/mesh/status", (IOptions<NodeOptions> options, MembershipService membership) => Results.Ok(new
{
    nodeId = options.Value.NodeId,
    peers = membership.GetStatus().Select(peer => new
    {
        nodeId = peer.NodeId,
        state = peer.State.ToString(),
        secondsSinceLastContact = peer.SecondsSinceLastContact
    })
}));

var nodeOptions = app.Services.GetRequiredService<IOptions<NodeOptions>>().Value;
var log = app.Services.GetRequiredService<ILogger<Program>>();
log.LogInformation(
    "Nodo en linea. REST en :8080, gRPC en :8081. Peers configurados: {Peers}.",
    nodeOptions.Peers.Count == 0 ? "ninguno" : string.Join(", ", nodeOptions.Peers));

app.Run();
