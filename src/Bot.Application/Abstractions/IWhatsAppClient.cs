namespace Advocacia.Bot.Application.Abstractions;

/// <summary>
/// Envio pela WhatsApp Cloud API oficial da Meta. Só esta porta fala com a Meta — nem o Core
/// nem nenhuma outra parte do Bot. Nenhuma API não oficial é usada.
/// </summary>
public interface IWhatsAppClient
{
    /// <summary>Envia um texto. Devolve o <c>wamid</c> atribuído pela Meta.</summary>
    Task<Result<string>> SendTextAsync(
        WhatsAppChannel channel, string toPhoneNumber, string body, CancellationToken cancellationToken);
}
