using Advocacia.Bot.Api;
using Advocacia.Bot.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

// Logs estruturados em JSON. Telefone e texto de mensagem NUNCA são logados (dado pessoal e
// sigilo profissional) — só ids técnicos e códigos de erro.
builder.Logging.AddJsonConsole();

builder.Services.AddBotInfrastructure(builder.Configuration);
builder.Services.AddScoped<ApiKeyEndpointFilter>();
builder.Services.AddHealthChecks()
    .AddNpgSql(builder.Configuration.GetConnectionString("BotDb") ?? string.Empty, name: "bot-db", tags: ["ready"]);

var app = builder.Build();

// Segredo ausente = não sobe. Os testes de integração usam o ambiente "Testing" com tudo configurado.
if (!app.Environment.IsEnvironment("SkipStartupValidation"))
{
    StartupValidation.EnsureSecurityConfiguration(app.Services);
}

app.MapWebhookEndpoints();
app.MapSendEndpoints();

// Liveness: o processo está de pé. Readiness: o banco próprio responde.
app.MapHealthChecks("/health/live", new() { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new() { Predicate = check => check.Tags.Contains("ready") });

app.Run();

/// <summary>Torna <c>Program</c> acessível aos testes de integração (WebApplicationFactory).</summary>
public partial class Program;
