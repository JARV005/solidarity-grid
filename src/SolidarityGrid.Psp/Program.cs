var builder = WebApplication.CreateBuilder(args);

// El PSP simulado solo necesita HTTP/1.1: es un adquirente REST. El delay y la
// deduplicacion idempotente llegaran en un bloque posterior.
builder.WebHost.ConfigureKestrel(kestrel => kestrel.ListenAnyIP(8080));

var app = builder.Build();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

app.Run();
