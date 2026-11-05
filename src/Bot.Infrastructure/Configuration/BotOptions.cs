using Advocacia.Bot.Application.Abstractions;

namespace Advocacia.Bot.Infrastructure.Configuration;

/// <summary>Meta (WhatsApp Cloud API). Segredos vêm do Key Vault/variáveis de ambiente, nunca do repositório.</summary>
public sealed class WhatsAppOptions
{
    public const string SectionName = "WhatsApp";

    /// <summary>Versão da Graph API (ex.: <c>v21.0</c>).</summary>
    public string GraphApiVersion { get; set; } = "v21.0";

    public string GraphApiBaseUrl { get; set; } = "https://graph.facebook.com";

    /// <summary>Token escolhido por nós e cadastrado no painel da Meta; ela o devolve no <c>GET /webhook</c> de verificação.</summary>
    public string VerifyToken { get; set; } = string.Empty;

    /// <summary>
    /// App Secret do app da Meta. Valida a assinatura (<c>X-Hub-Signature-256</c>) de TODO webhook.
    /// Ausente = o Bot rejeita todos os webhooks (falha fechada).
    /// </summary>
    public string AppSecret { get; set; } = string.Empty;

    /// <summary>Os números/escritórios atendidos por esta instância do Bot.</summary>
    public List<WhatsAppChannel> Channels { get; set; } = [];
}

/// <summary>Como o Bot chama o Core.</summary>
public sealed class CoreOptions
{
    public const string SectionName = "Core";

    /// <summary>URL base da API do Core (ex.: <c>https://api.interna</c>).</summary>
    public string BaseUrl { get; set; } = string.Empty;
}

/// <summary>Como o Core chama o Bot (<c>POST /api/v1/bot/send</c>).</summary>
public sealed class InboundApiOptions
{
    public const string SectionName = "InboundApi";

    /// <summary>
    /// Chave que o Core apresenta ao Bot. É OUTRA credencial, independente das chaves que o Bot usa
    /// para chamar o Core (<c>WhatsApp:Channels[].CoreApiKey</c>). Deve girar periodicamente.
    /// </summary>
    public string ApiKey { get; set; } = string.Empty;
}
