namespace Advocacia.Bot.Application.Webhooks;

/// <summary>Uma mensagem recebida do cliente, já extraída do payload da Meta.</summary>
/// <param name="WhatsAppMessageId">O <c>wamid</c>: chave de idempotência de tudo que acontece com esta mensagem.</param>
/// <param name="PhoneNumberId">Por qual número do escritório a mensagem chegou.</param>
/// <param name="From">Telefone do cliente (<c>wa_id</c>, só dígitos, com DDI).</param>
/// <param name="ContactName">Nome do perfil do WhatsApp, se a Meta o enviou.</param>
/// <param name="Text">O texto; <c>null</c> para mídia e outros tipos.</param>
/// <param name="Type">O tipo bruto da Meta (<c>text</c>, <c>image</c>, <c>audio</c>…).</param>
public sealed record InboundMessage(
    string WhatsAppMessageId,
    string PhoneNumberId,
    string From,
    string? ContactName,
    string? Text,
    string Type)
{
    public bool IsText => Text is not null;
}
