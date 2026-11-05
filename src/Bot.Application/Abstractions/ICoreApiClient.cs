namespace Advocacia.Bot.Application.Abstractions;

public sealed record AtendimentoRef(Guid AtendimentoId, string Status, bool Created);

public static class AtendimentoStatuses
{
    public const string Bot = "Bot";
    public const string PendingHuman = "PendingHuman";
    public const string Human = "Human";
    public const string Closed = "Closed";
}

public static class CoreErrorCodes
{
    /// <summary>O Core recusou a fala do bot porque a conversa está com um humano.</summary>
    public const string BotBlocked = "ATENDIMENTO_BOT_BLOCKED";
}

/// <summary>
/// Cliente da API do Core (<c>/api/v1/bot/atendimentos</c>) — a ÚNICA forma de o Bot ver ou
/// alterar o estado da conversa: ele nunca acessa o banco do Core. Cada chamada leva a chave de
/// API do escritório do canal; o Core deduz o tenant da chave.
/// </summary>
public interface ICoreApiClient
{
    /// <summary>Abre (ou recupera) o atendimento aberto do telefone. Idempotente.</summary>
    Task<Result<AtendimentoRef>> StartAtendimentoAsync(
        WhatsAppChannel channel, string phoneNumber, string? contactName, CancellationToken cancellationToken);

    /// <summary>Status atual — consultado ANTES de responder qualquer mensagem.</summary>
    Task<Result<string>> GetStatusAsync(WhatsAppChannel channel, Guid atendimentoId, CancellationToken cancellationToken);

    /// <summary>Registra uma mensagem da conversa. Idempotente por <paramref name="externalId"/>.</summary>
    Task<Result<Guid>> SendMessageAsync(
        WhatsAppChannel channel,
        Guid atendimentoId,
        MessageDirection direction,
        MessageSender sender,
        string body,
        string? externalId,
        CancellationToken cancellationToken);

    /// <summary>O cliente pediu um humano. Idempotente.</summary>
    Task<Result> RequestHandoffAsync(WhatsAppChannel channel, Guid atendimentoId, CancellationToken cancellationToken);
}

public enum MessageDirection
{
    Inbound,
    Outbound,
}

public enum MessageSender
{
    Client,
    Bot,
}
