using Advocacia.Bot.Application.Abstractions;
using Advocacia.Bot.Application.Conversations;
using Advocacia.Bot.Application.Outbound;
using Advocacia.Bot.Infrastructure.Configuration;
using Advocacia.Bot.Infrastructure.Core;
using Advocacia.Bot.Infrastructure.Meta;
using Advocacia.Bot.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Refit;

namespace Advocacia.Bot.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddBotInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("BotDb")
            ?? throw new InvalidOperationException("ConnectionStrings:BotDb não configurada (banco próprio do Bot).");

        services.Configure<WhatsAppOptions>(configuration.GetSection(WhatsAppOptions.SectionName));
        services.Configure<CoreOptions>(configuration.GetSection(CoreOptions.SectionName));
        services.Configure<InboundApiOptions>(configuration.GetSection(InboundApiOptions.SectionName));

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<IChannelRegistry, ChannelRegistry>();

        services.AddDbContext<BotDbContext>(options =>
        {
            options.UseNpgsql(connectionString);
            options.UseSnakeCaseNamingConvention();
        });
        services.AddScoped<IIdempotencyLedger, EfIdempotencyLedger>();
        services.AddHostedService<IdempotencyCleanupService>();

        // Core: todas as chamadas são idempotentes (start recupera o aberto, mensagem é por
        // externalId, handoff e status também), então o handler padrão de resiliência pode repetir.
        var coreBaseUrl = configuration.GetSection(CoreOptions.SectionName).Get<CoreOptions>()?.BaseUrl;
        services
            .AddRefitClient<ICoreApi>()
            .ConfigureHttpClient(client =>
            {
                if (!string.IsNullOrWhiteSpace(coreBaseUrl))
                {
                    client.BaseAddress = new Uri(coreBaseUrl);
                }

                client.Timeout = TimeSpan.FromSeconds(30);
            })
            .AddStandardResilienceHandler();
        services.AddScoped<ICoreApiClient, CoreApiClient>();

        // Meta: o envio NÃO é idempotente do lado da Meta — repetir às cegas mandaria duas
        // mensagens ao cliente. Sem retry; quem reenvia é a reentrega do webhook (com ledger) ou o Core.
        services
            .AddHttpClient<IWhatsAppClient, MetaWhatsAppClient>(client => client.Timeout = TimeSpan.FromSeconds(20))
            .AddStandardResilienceHandler(options =>
            {
                options.Retry.ShouldHandle = _ => ValueTask.FromResult(false);
                options.AttemptTimeout.Timeout = TimeSpan.FromSeconds(15);
                options.TotalRequestTimeout.Timeout = TimeSpan.FromSeconds(20);
            });

        services.AddScoped<ConversationService>();
        services.AddScoped<OutboundSendService>();

        return services;
    }
}
