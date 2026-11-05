using Refit;

namespace Advocacia.Bot.Infrastructure.Core;

// DTOs do contrato HTTP com o Core (docs/contrato-core.md). Camel case, como a Minimal API do Core
// serializa; ficam aqui, na infraestrutura, para o contrato não vazar para o domínio do Bot.
public sealed record StartAtendimentoBody(string PhoneNumber, string? ContactName);

public sealed record StartAtendimentoReply(Guid AtendimentoId, string Status, bool Created);

public sealed record MessageBody(string Direction, string Sender, string Body, string? ExternalId);

public sealed record MessageReply(Guid MessageId);

public sealed record StatusReply(Guid AtendimentoId, string Status);

public sealed record HandoffReply(bool Success);

/// <summary>
/// Contrato Refit da API do Core para o Bot. A chave de API é parâmetro (e não header fixo)
/// porque cada canal — cada escritório — tem a sua.
/// </summary>
public interface ICoreApi
{
    [Post("/api/v1/bot/atendimentos")]
    Task<IApiResponse<StartAtendimentoReply>> StartAsync(
        [Header("X-Api-Key")] string apiKey, [Body] StartAtendimentoBody body, CancellationToken cancellationToken);

    [Post("/api/v1/bot/atendimentos/{id}/mensagens")]
    Task<IApiResponse<MessageReply>> AddMessageAsync(
        [Header("X-Api-Key")] string apiKey, Guid id, [Body] MessageBody body, CancellationToken cancellationToken);

    [Post("/api/v1/bot/atendimentos/{id}/handoff")]
    Task<IApiResponse<HandoffReply>> HandoffAsync(
        [Header("X-Api-Key")] string apiKey, Guid id, CancellationToken cancellationToken);

    [Get("/api/v1/bot/atendimentos/{id}/status")]
    Task<IApiResponse<StatusReply>> GetStatusAsync(
        [Header("X-Api-Key")] string apiKey, Guid id, CancellationToken cancellationToken);
}
