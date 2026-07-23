using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;
using SolidarityGrid.Node.Configuration;
using SolidarityGrid.Node.Domain;
using SolidarityGrid.Node.Infrastructure;
using SolidarityGrid.Node.Logging;
using SolidarityGrid.Node.Mesh;
using SolidarityGrid.Node.Processing;

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
    options.PspUrl = config["PSP_URL"] ?? "http://psp-mock:8080";
});

builder.Logging.ClearProviders();
builder.Logging.AddConsoleFormatter<NodePrefixConsoleFormatter, ConsoleFormatterOptions>();
builder.Logging.AddConsole(console => console.FormatterName = NodePrefixConsoleFormatter.FormatterName);

// El gossip por segundo generaria un diluvio de logs HTTP del framework que
// enterraria la narrativa del sistema. Silenciamos infraestructura a Warning y
// dejamos hablar solo a nuestras categorias.
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<ILedger, InMemoryLedger>();
builder.Services.AddSingleton<MembershipService>();

// El gossip de este bloque viaja por HTTP. La seleccion http|grpc por TRANSPORT
// llegara cuando exista la segunda implementacion de IPeerTransport.
builder.Services.AddHttpClient("gossip");
builder.Services.AddSingleton<IPeerTransport, HttpPeerTransport>();

builder.Services.AddHttpClient("psp", (sp, client) =>
{
    var options = sp.GetRequiredService<IOptions<NodeOptions>>().Value;
    // El PSP es lento por diseño (5-10s), no por fallo: deadline generoso.
    client.BaseAddress = new Uri(options.PspUrl.TrimEnd('/') + "/");
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddSingleton<IPaymentGateway, HttpPaymentGateway>();
builder.Services.AddSingleton<PaymentProcessor>();

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

// Norte-sur: aceptar un pago. Replicamos antes de trabajar; sin replica, no hay 202.
app.MapPost("/pay", async (
    PayRequest body,
    HttpRequest http,
    IOptions<NodeOptions> options,
    ILedger ledger,
    IPeerTransport transport,
    PaymentProcessor processor,
    ILogger<Program> logger,
    CancellationToken ct) =>
{
    var self = options.Value.NodeId;
    var headerKey = http.Headers["Idempotency-Key"].FirstOrDefault();
    var txId = string.IsNullOrWhiteSpace(headerKey) ? $"tx-{Guid.NewGuid():N}" : headerKey!;

    var entry = new LedgerEntry(txId, TxState.Received, self, 1, body.Amount, null);
    ledger.Apply(entry);

    var peers = options.Value.Peers;
    if (peers.Count > 0)
    {
        // Replica sincrona: nunca aceptamos trabajo que solo existe en un nodo.
        var digest = new NodeDigest(self, new[] { entry });
        var outcomes = await Task.WhenAll(peers.Select(async peer =>
            (peer, ok: await transport.SendDigestAsync(peer, digest, ct))));
        var acked = outcomes.Where(o => o.ok).Select(o => o.peer).ToList();

        if (acked.Count == 0)
        {
            logger.LogWarning("{Tx} rechazada: ningún peer confirmó la réplica.", txId);
            return Results.StatusCode(503);
        }

        logger.LogInformation("{Tx} aceptada ({Amount} {Currency}). Replicada en {Peers}.",
            txId, body.Amount, body.Currency, string.Join(", ", acked));
    }
    else
    {
        logger.LogInformation("{Tx} aceptada ({Amount} {Currency}). Sin peers configurados.",
            txId, body.Amount, body.Currency);
    }

    processor.Begin(txId, body.Currency);

    return Results.Accepted($"/tx/{txId}", new { txId });
});

app.MapGet("/tx/{txId}", (string txId, ILedger ledger) =>
    ledger.TryGet(txId, out var entry)
        ? Results.Ok(new
        {
            txId = entry.TxId,
            state = entry.State.ToString(),
            owner = entry.Owner,
            epoch = entry.Epoch,
            amount = entry.Amount,
            authCode = entry.AuthCode
        })
        : Results.NotFound());

var nodeOptions = app.Services.GetRequiredService<IOptions<NodeOptions>>().Value;
var log = app.Services.GetRequiredService<ILogger<Program>>();
log.LogInformation(
    "Nodo en linea. REST en :8080, gRPC en :8081. Peers configurados: {Peers}.",
    nodeOptions.Peers.Count == 0 ? "ninguno" : string.Join(", ", nodeOptions.Peers));

app.Run();

public record PayRequest(decimal Amount, string Currency);
