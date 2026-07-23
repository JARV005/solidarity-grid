using Microsoft.Extensions.Logging.Console;
using SolidarityGrid.Psp;
using SolidarityGrid.Psp.Logging;

var builder = WebApplication.CreateBuilder(args);

// El PSP simulado solo necesita HTTP/1.1: es un adquirente REST.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(8080));

builder.Logging.ClearProviders();
builder.Logging.AddConsoleFormatter<PspConsoleFormatter, ConsoleFormatterOptions>();
builder.Logging.AddConsole(console => console.FormatterName = PspConsoleFormatter.FormatterName);

// Solo queremos ver la historia del cobro, no el ruido HTTP del framework.
builder.Logging.AddFilter("Microsoft", LogLevel.Warning);
builder.Logging.AddFilter("System", LogLevel.Warning);

builder.Services.AddSingleton<ChargeStore>();

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.MapPost("/charge", async (ChargeRequest body, HttpRequest http, ChargeStore store) =>
{
    var key = http.Headers["Idempotency-Key"].FirstOrDefault();
    if (string.IsNullOrWhiteSpace(key))
    {
        return Results.BadRequest(new { error = "Falta el header Idempotency-Key." });
    }

    var (authCode, replayed) = await store.ChargeAsync(key, body);
    return Results.Ok(new { authCode, replayed });
});

app.MapGet("/charges/{key}", (string key, ChargeStore store) =>
    store.TryGet(key, out var view) ? Results.Ok(view) : Results.NotFound());

app.Run();
