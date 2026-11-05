namespace Advocacia.Bot.Application.Abstractions;

/// <summary>
/// Um número de WhatsApp Business ligado a um escritório. É o que torna o Bot multi-escritório:
/// o webhook diz por qual número a mensagem chegou (<c>phone_number_id</c>), e daí saem o token
/// da Meta e a chave de API do Core DAQUELE escritório. Vem da configuração/Key Vault.
/// </summary>
public sealed class WhatsAppChannel
{
    /// <summary>Escritório (tenant) dono deste número no Core.</summary>
    public Guid TenantId { get; set; }

    /// <summary>Id do número na Meta (<c>metadata.phone_number_id</c> do webhook). Não é o telefone.</summary>
    public string PhoneNumberId { get; set; } = string.Empty;

    /// <summary>Token de acesso da Cloud API para este número.</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>Chave de API que o Core emitiu para o Bot deste escritório (<c>X-Api-Key</c>).</summary>
    public string CoreApiKey { get; set; } = string.Empty;

    /// <summary>Nome do escritório, usado nos textos do fluxo automático.</summary>
    public string OfficeName { get; set; } = "o escritório";
}

public interface IChannelRegistry
{
    WhatsAppChannel? FindByPhoneNumberId(string phoneNumberId);

    WhatsAppChannel? FindByTenantId(Guid tenantId);
}
