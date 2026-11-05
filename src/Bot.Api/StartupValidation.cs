using Advocacia.Bot.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Advocacia.Bot.Api;

/// <summary>
/// Falha NA SUBIDA quando falta configuração de segurança, em vez de aceitar tráfego e descobrir
/// em produção. Sem App Secret o Bot rejeitaria todo webhook; sem chave de entrada rejeitaria o
/// <c>/send</c> — então melhor nem subir. Lista só os NOMES das chaves faltantes, nunca valores.
/// </summary>
public static class StartupValidation
{
    public static void EnsureSecurityConfiguration(IServiceProvider services)
    {
        var whatsApp = services.GetRequiredService<IOptions<WhatsAppOptions>>().Value;
        var core = services.GetRequiredService<IOptions<CoreOptions>>().Value;
        var inbound = services.GetRequiredService<IOptions<InboundApiOptions>>().Value;

        var missing = new List<string>();

        if (string.IsNullOrWhiteSpace(whatsApp.AppSecret)) missing.Add($"{WhatsAppOptions.SectionName}:AppSecret");
        if (string.IsNullOrWhiteSpace(whatsApp.VerifyToken)) missing.Add($"{WhatsAppOptions.SectionName}:VerifyToken");
        if (string.IsNullOrWhiteSpace(core.BaseUrl)) missing.Add($"{CoreOptions.SectionName}:BaseUrl");
        if (string.IsNullOrWhiteSpace(inbound.ApiKey)) missing.Add($"{InboundApiOptions.SectionName}:ApiKey");
        if (whatsApp.Channels.Count == 0) missing.Add($"{WhatsAppOptions.SectionName}:Channels");

        for (var i = 0; i < whatsApp.Channels.Count; i++)
        {
            var channel = whatsApp.Channels[i];
            var prefix = $"{WhatsAppOptions.SectionName}:Channels:{i}";

            if (channel.TenantId == Guid.Empty) missing.Add($"{prefix}:TenantId");
            if (string.IsNullOrWhiteSpace(channel.PhoneNumberId)) missing.Add($"{prefix}:PhoneNumberId");
            if (string.IsNullOrWhiteSpace(channel.AccessToken)) missing.Add($"{prefix}:AccessToken");
            if (string.IsNullOrWhiteSpace(channel.CoreApiKey)) missing.Add($"{prefix}:CoreApiKey");
        }

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                "Configuração obrigatória ausente: " + string.Join(", ", missing) +
                ". Veja docs/configuracao.md. Em produção, os segredos vêm do Key Vault.");
        }
    }
}
