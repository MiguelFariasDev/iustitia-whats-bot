using Advocacia.Bot.Application.Webhooks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;

namespace Advocacia.Bot.IntegrationTests;

/// <summary>
/// O host REAL do Bot (Program.cs: pipeline, DI, EF, Refit, resiliência) contra PostgreSQL real.
/// Só a rede muda de mãos: as chamadas ao Core e à Meta vão para servidores falsos em memória.
/// </summary>
public class BotApiFactory : WebApplicationFactory<Program>, IAsyncLifetime
{
    public const string AppSecret = "meta-app-secret-de-teste";
    public const string VerifyToken = "token-de-verificacao";
    public const string InboundKey = "chave-que-o-core-apresenta";
    public const string PhoneNumberId = "PNID-1";
    public static readonly Guid TenantId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private readonly BotPostgresFixture _postgres = new();

    public FakeCoreServer Core { get; } = new();

    public FakeMetaServer Meta { get; } = new();

    /// <summary>Quando definido, sobrescreve o App Secret — para os testes de configuração ausente.</summary>
    public string? AppSecretOverride { get; init; } = AppSecret;

    public async Task InitializeAsync() => await _postgres.InitializeAsync();

    async Task IAsyncLifetime.DisposeAsync() => await _postgres.DisposeAsync();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");

        // UseSetting (e não ConfigureAppConfiguration): o DI do Bot lê a configuração durante o
        // registro, antes do ConfigureAppConfiguration de um WebApplicationFactory valer.
        builder.UseSetting("ConnectionStrings:BotDb", _postgres.ConnectionString);
        builder.UseSetting("Core:BaseUrl", "https://core.test");
        builder.UseSetting("InboundApi:ApiKey", InboundKey);
        builder.UseSetting("WhatsApp:GraphApiBaseUrl", "https://graph.test");
        builder.UseSetting("WhatsApp:VerifyToken", VerifyToken);
        builder.UseSetting("WhatsApp:AppSecret", AppSecretOverride ?? string.Empty);
        builder.UseSetting("WhatsApp:Channels:0:TenantId", TenantId.ToString());
        builder.UseSetting("WhatsApp:Channels:0:PhoneNumberId", PhoneNumberId);
        builder.UseSetting("WhatsApp:Channels:0:AccessToken", "token-meta");
        builder.UseSetting("WhatsApp:Channels:0:CoreApiKey", FakeCoreServer.ValidKey);
        builder.UseSetting("WhatsApp:Channels:0:OfficeName", "Escritório Teste");

        builder.ConfigureServices(services =>
        {
            var router = new HostRouterHandler(Core, Meta);

            // Troca o handler PRIMÁRIO de TODO HttpClient nomeado do container (ConfigureAll: os
            // clientes do Refit e o tipado da Meta têm nomes diferentes); os handlers de resiliência
            // continuam por cima, como em produção.
            services.ConfigureAll<HttpClientFactoryOptions>(options =>
                options.HttpMessageHandlerBuilderActions.Add(builder => builder.PrimaryHandler = router));
        });
    }

    public HttpClient CreateSignedClient() => CreateClient();

    public static (byte[] Body, string Signature) Sign(string json, string? secret = AppSecret)
    {
        var body = System.Text.Encoding.UTF8.GetBytes(json);
        return (body, MetaSignatureValidator.Sign(body, secret ?? string.Empty));
    }
}
